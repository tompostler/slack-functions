using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace slack_functions
{
    public static class Functions
    {
        private static string StoreConn => ConfigurationManager.AppSettings.Get("StorageConnection");
        private static string StoreIConn => ConfigurationManager.AppSettings.Get("StorageIConnection");
        private static string SlackToken => ConfigurationManager.AppSettings.Get("SlackTokenImg");
        private static string SlackOauthToken => ConfigurationManager.AppSettings.Get("SlackOauthToken");
        private static bool DebugFlag => bool.TryParse(ConfigurationManager.AppSettings.Get("Debug"), out bool t) && t;

        private static HttpClient HttpClient = new HttpClient();
        private static Random Random = new Random();
        private static CloudBlobContainer ImageContainer;

        static Functions()
        {
            var client = CloudStorageAccount.Parse(StoreIConn).CreateCloudBlobClient();
            ImageContainer = client.GetContainerReference("images");
            ImageContainer.CreateIfNotExists(BlobContainerPublicAccessType.Off);

            if (!string.IsNullOrWhiteSpace(SlackOauthToken))
                HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SlackOauthToken);
        }

        [FunctionName(nameof(ReceiveImageWebhookAsync))]
        public static async Task<HttpResponseMessage> ReceiveImageWebhookAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "slack/img")]HttpRequestMessage req,
            [Queue("request", Connection = "StorageConnection")]CloudQueue queue,
            ILogger logger)
        {
            // Get the bits from the message
            var uselessData = await req.Content.ReadAsFormDataAsync();
            var data = new SlackSlashCommandPayload
            {
                token = uselessData[nameof(SlackSlashCommandPayload.token)],
                text = uselessData[nameof(SlackSlashCommandPayload.text)],
                response_url = uselessData[nameof(SlackSlashCommandPayload.response_url)],
                user_name = uselessData[nameof(SlackSlashCommandPayload.user_name)],
                channel_id = uselessData[nameof(SlackSlashCommandPayload.channel_id)]
            };
            if (DebugFlag) logger.LogInformation(JsonConvert.SerializeObject(uselessData.AllKeys.Select(k => new { key = k, val = uselessData[k] })));

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
                        text = $"slack-functions v{AssemblyName.GetAssemblyName(Assembly.GetExecutingAssembly().Location).Version.ToString()}\n"
                                + "\n"
                                + "To request a specific file, use that file's full name as returned by a previous message.\n"
                                + "To get a status of how many images have been seen, ask for the special category of 'status'.\n"
                                + "To schedule a bunch of messages, say `!timer TimeSpan Count [category]` where TimeSpan is a HH:MM:SS interval and count is how many images. (WARNING: There is no check for a valid category before scheduling all the images)\n"
                                + "\n"
                                + "Otherwise, you can specify a category or leave it blank to default to a special category of 'all' (which looks at the distribution of images to pick an actual category).\n"
                                + "\n"
                                + "Available categories: `" + string.Join("`, `", DirectoriesInContainer.Keys) + "`"
                    },
                    JsonMediaTypeFormatter.DefaultMediaType);
            }

            // If category is !timer, then do the timer
            if (data.text.StartsWith("!timer"))
            {
                // parts[0] !timer
                // parts[1] TimeSpan
                // parts[2] Count
                // parts[3] Category (optional)
                var parts = data.text.Split(' ');
                data.text = parts.Length == 4 ? parts[3] : null;
                string errMsg = null;
                bool parsed_interval = int.TryParse(parts[1], out int intervals);
                if (parts.Length != 4 && parts.Length != 3)
                    errMsg = "You did not have the right number of arguments to `!timer`.";
                if (!TimeSpan.TryParse(parts[1], out TimeSpan interval) && !parsed_interval)
                    errMsg = $"`{parts[1]}` was not a valid TimeSpan.";
                if (!int.TryParse(parts[2], out int count))
                    errMsg = $"`{parts[2]}` was not a valid count.";
                if (errMsg != null)
                    return req.CreateResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            response_type = "in_channel",
                            text = errMsg
                        },
                        JsonMediaTypeFormatter.DefaultMediaType);
                if (parsed_interval)
                    interval = TimeSpan.FromSeconds(intervals);
                logger.LogInformation("Category:{0} Interval:{1} Count{2}", data.text, interval, count);

                var duration = TimeSpan.FromSeconds(interval.TotalSeconds * count);
                if (interval < TimeSpan.FromSeconds(30) || interval > TimeSpan.FromHours(24))
                    errMsg = "TimeSpan must be between 30 seconds and 24 hours.";
                else if (count <= 1 || count >= 50)
                    errMsg = "Count must be greater than 1 and less than 50.";
                else if (duration > TimeSpan.FromDays(7))
                    errMsg = $"Count with interval cannot last more than 7 days. (Is currently `{duration}`)";
                if (errMsg != null)
                    return req.CreateResponse(
                        HttpStatusCode.OK,
                        new
                        {
                            response_type = "in_channel",
                            text = errMsg
                        },
                        JsonMediaTypeFormatter.DefaultMediaType);

                // Now that we passed command validation, actually schedule the messages
                for (int i = 1; i <= count; i++)
                    await queue.AddMessageAsync(
                        new CloudQueueMessage(
                            JsonConvert.SerializeObject(
                                new Messages.Request
                                {
                                    category = data.text,
                                    channel_id = data.channel_id,
                                    response_url = data.response_url,
                                    user_name = data.user_name + $", timer {i}/{count}"
                                })),
                        timeToLive: null,
                        initialVisibilityDelay: TimeSpan.FromSeconds(interval.TotalSeconds * i),
                        options: null,
                        operationContext: null);

                // Inform of the configuration
                return req.CreateResponse(
                    HttpStatusCode.OK,
                    new
                    {
                        response_type = "in_channel",
                        text = $"{data.user_name} has scheduled {count} images for the '{data.text}' category every {interval} for the next {duration}."
                    },
                    JsonMediaTypeFormatter.DefaultMediaType);
            }

            if (data.text == "!test")
            {
                logger.LogInformation("Hit !test");
                var response = await HttpClient.PostAsJsonAsync(
                    "https://slack.com/api/chat.postMessage",
                    new
                    {
                        channel = data.channel_id,
                        text = $"Random test {Random.Next()}"
                    });
                logger.LogInformation("Response body: {0}", await response.Content.ReadAsStringAsync());
                data.text = null;
            }

            // Queue up the work and send back a response
            await queue.AddMessageAsync(
                new CloudQueueMessage(
                    JsonConvert.SerializeObject(
                        new Messages.Request
                        {
                            category = data.text,
                            channel_id = data.channel_id,
                            response_url = data.response_url,
                            user_name = data.user_name
                        })));
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
                    await SendImageToSlack(request.category, request.user_name, request.channel_id, blob, logger);
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

            // If they ask for status, then give them some status
            if (request.category == "status")
            {
                var sb = new StringBuilder();
                var maxname = Math.Max(DirectoriesInContainer.Keys.Union(new[] { "TOTAL" }).Max(_ => _.Length), "category".Length);
                sb.AppendLine("```");
                sb.AppendLine($"{"CATEGORY".PadRight(maxname)}  SEEN  UNSEEN  TOTAL  PERCENT VIEWED");
                (int SeenCount, int UnseenCount) totals = (0, 0);
                foreach (var directory in DirectoriesInContainer.Keys)
                {
                    var dirconfig = ImageContainer.GetBlockBlobReference(directory + ".json");
                    if (!await dirconfig.ExistsAsync())
                    {
                        sb.AppendLine($"{directory.PadRight(maxname)}      NOT YET QUERIED");
                        continue;
                    }
                    var dirstatus = JsonConvert.DeserializeObject<DirectoryStatus>(await dirconfig.DownloadTextAsync());
                    sb.Append(directory.PadRight(maxname));
                    sb.Append("  ");
                    sb.Append($"{dirstatus.SeenFiles.Count,4}");
                    totals.SeenCount += dirstatus.SeenFiles.Count;
                    sb.Append("  ");
                    sb.Append($"{dirstatus.UnseenFiles.Count,6}");
                    totals.UnseenCount += dirstatus.UnseenFiles.Count;
                    sb.Append("  ");
                    sb.Append($"{dirstatus.SeenFiles.Count + dirstatus.UnseenFiles.Count,5}");
                    sb.Append("  ");
                    sb.Append($"{1d * dirstatus.SeenFiles.Count / (dirstatus.SeenFiles.Count + dirstatus.UnseenFiles.Count),14:P2}");
                    sb.AppendLine();
                }
                sb.Append("TOTAL".PadRight(maxname));
                sb.Append("  ");
                sb.Append($"{totals.SeenCount,4}");
                sb.Append("  ");
                sb.Append($"{totals.UnseenCount,6}");
                sb.Append("  ");
                sb.Append($"{totals.SeenCount + totals.UnseenCount,5}");
                sb.Append("  ");
                sb.Append($"{1d * totals.SeenCount / (totals.SeenCount + totals.UnseenCount),14:P2}");
                sb.AppendLine();
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
                    // We are picking a category from all
                    // Load all the categories and pick one of them based on image distribution (for better results)
                    var dist = DirectoriesInContainer.SelectMany(dic => dic.Value.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => dic.Key));
                    var category = dist.ElementAt(Random.Next(dist.Count()));
                    config = ImageContainer.GetBlockBlobReference(category + ".json");
                    if (await config.ExistsAsync())
                    {
                        logger.LogInformation("Downloading configuration file {0} for all match on {1}...", config.Name, request.category);
                        leaseId = await config.AcquireLeaseAsync(TimeSpan.FromSeconds(45));
                        ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());
                    }
                    else
                    {
                        logger.LogInformation("Creating configuration file {0} for all match on {1}...", config.Name, request.category);
                        var files = DirectoriesInContainer[category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                        ds = new DirectoryStatus
                        {
                            UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                        };
                    }
                }
                else if (DirectoriesInContainer.ContainsKey(request.category))
                {
                    var files = DirectoriesInContainer[request.category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                    ds = new DirectoryStatus
                    {
                        UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                    };
                }
                else if (DirectoriesInContainer.Keys.Count(k => k.StartsWith(request.category)) > 0)
                {
                    // We are letting fuzzy matching take care of it
                    // Load all the matching categories and pick one of them based on image distribution (for better results)
                    var dist = DirectoriesInContainer
                        .Where(dic => dic.Key.StartsWith(request.category))
                        .SelectMany(dic => dic.Value.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => dic.Key));
                    var category = dist.ElementAt(Random.Next(dist.Count()));
                    config = ImageContainer.GetBlockBlobReference(category + ".json");
                    if (await config.ExistsAsync())
                    {
                        logger.LogInformation("Downloading configuration file {0} for fuzzy match on {1}...", config.Name, request.category);
                        leaseId = await config.AcquireLeaseAsync(TimeSpan.FromSeconds(45));
                        ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());
                    }
                    else
                    {
                        logger.LogInformation("Creating configuration file {0} for fuzzy match on {1}...", config.Name, request.category);
                        var files = DirectoriesInContainer[category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                        ds = new DirectoryStatus
                        {
                            UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                        };
                    }
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

            await SendImageToSlack(request.category, request.user_name, request.channel_id, blob, logger);

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

        private static async Task SendImageToSlack(string request_text, string user_name, string channel_id, CloudBlob blob, ILogger logger)
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
            var res = await HttpClient.PostAsJsonAsync(
                "https://slack.com/api/chat.postMessage",
                new
                {
                    channel = channel_id,
                    attachments = new[]
                    {
                        new
                        {
                            pretext = $"Request: '{request_text}' ({user_name})\nResponse: {blob.Name}",
                            image_url = blob.Uri.AbsoluteUri + sas
                        }
                    }
                });
            logger.LogInformation("{0} response: {1} {2}", nameof(SendImageToSlack), res.StatusCode, await res.Content.ReadAsStringAsync());
        }
    }
}
