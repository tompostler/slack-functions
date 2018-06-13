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
        private static Random Random = new Random();
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

            // If category is help, respond with help immediately
            if (data.text == "help")
            {
                PopulateDirectoriesInContainer();
                return req.CreateResponse(
                    HttpStatusCode.OK,
                    new
                    {
                        response_type = "in_channel",
                        text = "To request a specific file, use that file's full name as returned by a previous message.\n"
                                + "Otherwise, you can specific a category or leave it blank to default to a special category of 'all'.\n"
                                + "Available categories: `" + string.Join("`, `", DirectoriesInContainer.Keys) + "`"
                    },
                    JsonMediaTypeFormatter.DefaultMediaType);
            }

            // Queue up the work and send back a response
            logger.LogInformation("Data: {0}", JsonConvert.SerializeObject(data));
            await collector.AddAsync(new Messages.Request { category = data.text, response_url = data.response_url });
            return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" }, JsonMediaTypeFormatter.DefaultMediaType);
        }

        private static Dictionary<string, CloudBlobDirectory> DirectoriesInContainer;
        private static void PopulateDirectoriesInContainer()
        {
            // If directories in container is null, populate
            if (DirectoriesInContainer == null)
            {
                DirectoriesInContainer = new Dictionary<string, CloudBlobDirectory>();
                foreach (var cbd in ImageContainer.ListBlobs().Where(_ => _ is CloudBlobDirectory).Select(_ => _ as CloudBlobDirectory))
                    DirectoriesInContainer.Add(cbd.Prefix.Substring(0, cbd.Prefix.Length - 1), cbd);
            }
        }

        [FunctionName(nameof(ProcessImageWebhookAsync))]
        public static async Task ProcessImageWebhookAsync(
            [QueueTrigger("request", Connection = "StorageConnection")]Messages.Request request,
            ILogger logger)
        {
            logger.LogInformation("Populating {0}...", nameof(DirectoriesInContainer));
            PopulateDirectoriesInContainer();

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

            // Get just a single image
            CloudBlob blob = null;
            if (request.category.Contains("/"))
            {
                blob = ImageContainer.GetBlobReference(request.category);
                if (await blob.ExistsAsync())
                {
                    await SendImageToSlack(request.category, request.response_url, blob, logger);
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

            // If they ask for status, then give them some stats
            if (request.category == "status")
            {
                var sb = new StringBuilder();
                var maxname = Math.Max(DirectoriesInContainer.Keys.Max(_ => _.Length), "category".Length);
                sb.AppendLine("```");
                sb.AppendLine($"{"CATEGORY".PadRight(maxname)}  SEEN  UNSEEN  TOTAL  PERCENT VIEWED");
                foreach (var directory in DirectoriesInContainer.Keys)
                {
                    var dirconfig = ImageContainer.GetBlockBlobReference(directory + ".json");
                    if (!await dirconfig.ExistsAsync())
                    {
                        sb.AppendLine($"{directory.PadRight(maxname)}  NOT YET QUERIED");
                        continue;
                    }
                    var dirstatus = JsonConvert.DeserializeObject<DirectoryStatus>(await dirconfig.DownloadTextAsync());
                    sb.Append(directory.PadRight(maxname));
                    sb.Append("  ");
                    sb.Append($"{dirstatus.SeenFiles.Count,4}");
                    sb.Append("  ");
                    sb.Append($"{dirstatus.UnseenFiles.Count,6}");
                    sb.Append("  ");
                    sb.Append($"{dirstatus.SeenFiles.Count + dirstatus.UnseenFiles.Count,5}");
                    sb.Append("  ");
                    sb.Append($"{1d * dirstatus.SeenFiles.Count / (dirstatus.SeenFiles.Count + dirstatus.UnseenFiles.Count),14:P2}");
                    sb.AppendLine();
                }
                sb.AppendLine("```");

                await HttpClient.PostAsJsonAsync(request.response_url, new
                {
                    response_type = "in_channel",
                    text = sb.ToString()
                });
                return;
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
                    foreach (var directory in DirectoriesInContainer.Values)
                        foreach (var file in directory.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob))
                            ds.UnseenFiles.Add(file.Name);
                }
                else if (DirectoriesInContainer.ContainsKey(request.category))
                {
                    var files = DirectoriesInContainer[request.category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                    ds = new DirectoryStatus
                    {
                        UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                    };
                }
                else
                {
                    await HttpClient.PostAsJsonAsync(request.response_url, new
                    {
                        response_type = "in_channel",
                        text = "This is not a valid category. Please try again."
                    });
                    return;
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
                logger.LogInformation("Repopulating unseen images from folder...");
                await config.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, AccessCondition.GenerateLeaseCondition(leaseId), new BlobRequestOptions(), new OperationContext());
                await HttpClient.PostAsJsonAsync(request.response_url, new
                {
                    response_type = "in_channel",
                    text = $"Rebuilding index for {request.category}"
                });
                throw new InvalidOperationException("This will safely retry the message and reset the configuration file.");
            }

            // Pick one
            logger.LogInformation("Picking a blob...");
            do
            {
                if (blob != null)
                {
                    // This happens when a blob we were trying to see doesn't exist.
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

            await SendImageToSlack(request.category, request.response_url, blob, logger);

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

        private static async Task SendImageToSlack(string request_text, string response_url, CloudBlob blob, ILogger logger)
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
                        pretext = $"Request: '{request_text}'\nResponse: {blob.Name}",
                        image_url = blob.Uri.AbsoluteUri + sas
                    }
                }
            });
            logger.LogInformation("Help response: {0} {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        }
    }
}
