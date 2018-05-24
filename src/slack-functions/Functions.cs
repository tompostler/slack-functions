using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Text;
using System.Threading.Tasks;

namespace slack_functions
{
    public static class Functions
    {
        private static string StoreConn => ConfigurationManager.AppSettings.Get("StorageConnection");
        private static string StoreIConn => ConfigurationManager.AppSettings.Get("StorageIConnection");
        private static string SlackToken => ConfigurationManager.AppSettings.Get("SlackTokenImg");

        private static HttpClient HttpClient = new HttpClient();
        private static CloudBlobContainer ImageContainer;

        static Functions()
        {
            var client = CloudStorageAccount.Parse(StoreIConn).CreateCloudBlobClient();
            ImageContainer = client.GetContainerReference("images");
            ImageContainer.CreateIfNotExists(BlobContainerPublicAccessType.Off);
        }

        [FunctionName(nameof(ReceiveImageWebhookAsync))]
        public static async Task<HttpResponseMessage> ReceiveImageWebhookAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "slack/img")]HttpRequestMessage req,
            [Queue("request", Connection = "StorageConnection")]IAsyncCollector<Messages.Request> collector,
            ILogger logger)
        {
            // Get the bits from the message
            var uselessData = await req.Content.ReadAsFormDataAsync();
            var data = new SlackPost
            {
                token = uselessData["token"],
                text = uselessData["text"],
                response_url = uselessData["response_url"]
            };

            // Make sure it's a legit request
            if (!SlackToken.Equals(data.token))
            {
                logger.LogWarning("Unauthorized request. Provided slack token: '{0}'", data.token);
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            // Queue up the work and send back a response
            await collector.AddAsync(new Messages.Request { category = data.text, response_url = data.response_url });
            return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" }, JsonMediaTypeFormatter.DefaultMediaType);
        }

        private static Dictionary<string, CloudBlobDirectory> KnownDirectories;
        private static Random Random = new Random();

        [FunctionName(nameof(ProcessImageWebhookAsync))]
        public static async Task ProcessImageWebhookAsync(
            [QueueTrigger("request", Connection = "StorageConnection")]Messages.Request request,
            ILogger logger)
        {
            // If known directories is null, populate
            if (KnownDirectories == null)
            {
                logger.LogInformation("Populating {0}...", nameof(KnownDirectories));
                KnownDirectories = new Dictionary<string, CloudBlobDirectory>();
                foreach (var cbd in ImageContainer.ListBlobs().Where(_ => _ is CloudBlobDirectory).Select(_ => _ as CloudBlobDirectory))
                    KnownDirectories.Add(cbd.Prefix.Substring(0, cbd.Prefix.Length - 1), cbd);
            }

            // Fix up the category
            if (string.IsNullOrWhiteSpace(request.category))
            {
                logger.LogInformation("Category is empty. Setting to 'all'.");
                request.category = "all";
            }
            else
            {
                logger.LogInformation("Category is '{0}'.", request.category);
                request.category = request.category.Trim();
            }

            // If category is help, respond with help
            if (request.category == "help")
            {
                var response = await HttpClient.PostAsJsonAsync(
                    request.response_url,
                    new
                    {
                        text = "To request a specific file, use that file's full name as returned by a previous message.\n"
                                + "Otherwise, you can specific a category or leave it blank to default to all.\n"
                                + "Available categories: `" + string.Join("`, `", KnownDirectories.Keys) + "`"
                    });
                logger.LogInformation("Help response: {0} {1}", response.StatusCode, await response.Content.ReadAsStringAsync());
                return;
            }

            // Get just a single image
            CloudBlob blob = null;
            if (request.category.Contains("/"))
            {
                blob = ImageContainer.GetBlobReference(request.category);
                if (await blob.ExistsAsync())
                {
                    await SendImageToSlack(request.response_url, blob, logger);
                    return;
                }
                else
                {
                    await HttpClient.PostAsJsonAsync(request.response_url, new
                    {
                        response_type = "in_channel",
                        text = "Sorry. That image no longer exists."
                    });
                    return;
                }
            }

            // Get configuration for category
            var config = ImageContainer.GetBlockBlobReference(request.category + ".json");
            string leaseId = null;
            DirectoryStatus ds;
            if (!await config.ExistsAsync())
            {
                logger.LogInformation("Populating configuration file...");
                if (request.category == "all")
                {
                    ds = new DirectoryStatus();
                    foreach (var directory in KnownDirectories.Values)
                        foreach (var file in directory.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob))
                            ds.UnseenFiles.Add(file.Name);
                }
                else
                {
                    var files = KnownDirectories[request.category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                    ds = new DirectoryStatus
                    {
                        UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                    };
                }
            }
            else
            {
                logger.LogInformation("Downloading configuration file...");
                leaseId = await config.AcquireLeaseAsync(TimeSpan.FromSeconds(45));
                ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());
            }

            // Make sure we have unseen files
            if (ds.UnseenFiles.Count == 0)
            {
                logger.LogInformation("Resetting unseen files from seen.");
                ds.UnseenFiles = ds.SeenFiles;
                ds.SeenFiles = new HashSet<string>();
            }

            // Pick one
            logger.LogInformation("Picking a blob...");
            do
            {
                if (blob != null)
                {
                    // This if condition happens when a blob we were trying to see doesn't exist.
                    // Therefore remove it from the seen files.
                    logger.LogInformation("Removing blob '{0}'.", blob.Name);
                    ds.SeenFiles.Remove(blob.Name);
                }

                var blobname = ds.UnseenFiles.ElementAt(Random.Next(ds.UnseenFiles.Count));
                blob = ImageContainer.GetBlobReference(blobname);
                ds.UnseenFiles.Remove(blobname);
                ds.SeenFiles.Add(blobname);
            }
            while (!await blob.ExistsAsync());

            await SendImageToSlack(request.response_url, blob, logger);

            // Write configuration back and release lease
            logger.LogInformation("Uploading configuration file...");
            if (leaseId == null)
                await config.UploadTextAsync(JsonConvert.SerializeObject(ds));
            else
            {
                await config.UploadTextAsync(JsonConvert.SerializeObject(ds), Encoding.UTF8, AccessCondition.GenerateLeaseCondition(leaseId), new BlobRequestOptions(), new OperationContext());
                await config.ReleaseLeaseAsync(AccessCondition.GenerateLeaseCondition(leaseId));
            }
        }

        private static async Task SendImageToSlack(string response_url, CloudBlob blob, ILogger logger)
        {
            // Acquire SAS token
            logger.LogInformation("Acquiring a SAS...");
            var sas = blob.GetSharedAccessSignature(new SharedAccessBlobPolicy
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessStartTime = DateTimeOffset.UtcNow,
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(24)
            });

            // Send a response to slack
            var res = await HttpClient.PostAsJsonAsync(response_url, new
            {
                response_type = "in_channel",
                attachments = new[]
                {
                    new
                    {
                        pretext = blob.Name,
                        image_url = blob.Uri.AbsoluteUri + sas
                    }
                }
            });
            logger.LogInformation("Help response: {0} {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        }
    }
}
