//
// <copyright file="WorkerRole.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using Microsoft.WindowsAzure.ServiceRuntime;
///using Microsoft.WindowsAzure.Diagnostics;

namespace Microsoft.Samples.ServiceHosting.Thumbnails
{
    public class WorkerRole : RoleEntryPoint
    {
        private Stream CreateThumbnail(Stream input)
        {
            Bitmap orig = new Bitmap(input);

            int width;
            int height;
            if (orig.Width > orig.Height)
            {
                width = 128;
                height = 128 * orig.Height / orig.Width;
            }
            else
            {
                height = 128;
                width = 128 * orig.Width / orig.Height;
            }

            Bitmap thumb = new Bitmap(width, height);
            using (Graphics graphic = Graphics.FromImage(thumb))
            {
                graphic.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphic.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphic.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                graphic.DrawImage(orig, 0, 0, width, height);
                MemoryStream ms = new MemoryStream();
                thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);

                ms.Seek(0, SeekOrigin.Begin);
                return ms;
            }
        }

        public override bool OnStart()
        {

            #region Setup CloudStorageAccount Configuration Setting Publisher

            // This code sets up a handler to update CloudStorageAccount instances when their corresponding
            // configuration settings change in the service configuration file.
            CloudStorageAccount.SetConfigurationSettingPublisher((configName, configSetter) =>
            {
                // Provide the configSetter with the initial value
                configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));

                RoleEnvironment.Changed += (sender, arg) =>
                {
                    if (arg.Changes.OfType<RoleEnvironmentConfigurationSettingChange>()
                        .Any((change) => (change.ConfigurationSettingName == configName)))
                    {
                        // The corresponding configuration setting has changed, propagate the value
                        if (!configSetter(RoleEnvironment.GetConfigurationSettingValue(configName)))
                        {
                            // In this case, the change to the storage account credentials in the
                            // service configuration is significant enough that the role needs to be
                            // recycled in order to use the latest settings. (for example, the 
                            // endpoint has changed)
                            RoleEnvironment.RequestRecycle();
                        }
                    }
                };
            });
            #endregion

            return base.OnStart();
        }

        public override void Run()
        {
            var storageAccount = CloudStorageAccount.Parse(RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"));

            CloudBlobClient blobStorage = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobStorage.GetContainerReference("photogallery");

            CloudQueueClient queueStorage = storageAccount.CreateCloudQueueClient();
            CloudQueue queue = queueStorage.GetQueueReference("thumbnailmaker");

            Trace.TraceInformation("Creating container and queue...");

            // If the Start() method throws an exception, the role recycles.
            // If this sample is run locally and the development storage tool has not been started, this 
            // can cause a number of exceptions to be thrown because roles are restarted repeatedly.
            // Lets try to create the queue and the container and check whether the storage services are running
            // at all.
            bool containerAndQueueCreated = false;
            while (!containerAndQueueCreated)
            {
                try
                {
                    container.CreateIfNotExist();

                    var permissions = container.GetPermissions();

                    permissions.PublicAccess = BlobContainerPublicAccessType.Container;

                    container.SetPermissions(permissions);

                    permissions = container.GetPermissions();

                    queue.CreateIfNotExist();

                    containerAndQueueCreated = true;
                }
                catch (StorageClientException e)
                {
                    if (e.ErrorCode == StorageErrorCode.TransportError)
                    {
                        Trace.TraceError(string.Format("Connect failure! The most likely reason is that the local " +
                            "Development Storage tool is not running or your storage account configuration is incorrect. " +
                            "Message: '{0}'", e.Message));
                        System.Threading.Thread.Sleep(5000);
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            Trace.TraceInformation("Listening for queue messages...");

            // Now that the queue and the container have been created in the above initialization process, get messages
            // from the queue and process them individually.
            while (true)
            {
                try
                {
                    CloudQueueMessage msg = queue.GetMessage();
                    if (msg != null)
                    {
                        string path = msg.AsString;
                        string thumbnailName = System.IO.Path.GetFileNameWithoutExtension(path) + ".jpg";
                        Trace.TraceInformation(string.Format("Dequeued '{0}'", path));
                        CloudBlockBlob content = container.GetBlockBlobReference(path);
                        CloudBlockBlob thumbnail = container.GetBlockBlobReference("thumbnails/" + thumbnailName);
                        MemoryStream image = new MemoryStream();

                        content.DownloadToStream(image);

                        image.Seek(0, SeekOrigin.Begin);
                      
                        thumbnail.Properties.ContentType = "image/jpeg";
                        thumbnail.UploadFromStream(CreateThumbnail(image));

                        Trace.TraceInformation(string.Format("Done with '{0}'", path));

                        queue.DeleteMessage(msg);
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(1000);
                    }
                }
                catch (Exception e)
                {
                    // Explicitly catch all exceptions of type StorageException here because we should be able to 
                    // recover from these exceptions next time the queue message, which caused this exception,
                    // becomes visible again.

                    System.Threading.Thread.Sleep(5000);
                    Trace.TraceError(string.Format("Exception when processing queue item. Message: '{0}'", e.Message));
                }
            }
        }
    }
}
