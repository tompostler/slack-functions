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
    public static partial class Functions
    {
        private static string StoreConn => ConfigurationManager.AppSettings.Get("StorageConnection");
        private static string StoreIConn => ConfigurationManager.AppSettings.Get("StorageIConnection");
        private static string SlackToken => ConfigurationManager.AppSettings.Get("SlackTokenImg");
        private static string SlackOauthToken => ConfigurationManager.AppSettings.Get("SlackOauthToken");
        private static string SlackNotifyChannelId => ConfigurationManager.AppSettings.Get("SlackNotifyChannelId");
        private static bool DebugFlag => bool.TryParse(ConfigurationManager.AppSettings.Get("Debug"), out bool t) && t;

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly Random Random = new Random();
        private static readonly CloudBlobContainer ImageContainer;

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
                                + "To schedule a bunch of messages, say `!timer interval duration [category]` where `interval` and `duration` are TimeSpans represented as HH:MM:SS or numbers suffixed by the appropriate `d`, `h`, or `m` character. (WARNING: There is no check for a valid category before scheduling all the images). Just `timer` also works.\n"
                                + "To schedule a bunch of messages in a more rigid way, say `!cron expression duration [category]` where `expression` is a backtick'd cron expression and `duration` is the same as `!timer`. (WARNING: There is no check for a valid category before scheduling all the images). Just `cron` also works.\n"
                                + "To schedule a bunch of messages in a more flexible way, say `!random count duration [category]` where `count` is the number of images you want to see and `duration` is the same as `!timer`. (WARNING: There is no check for a valid category before scheduling all the images). Just `random` also works.\n"
                                + "(In case you forgot, cron is `min hour dom mon dow`)\n"
                                + "To reset a category for re-viewing, say `!reset category`.\n"
                                + "To force a rescan of available images, say `!rescan`.\n"
                                + "\n"
                                + "Otherwise, you can specify a category, multiple space-separated categories, or leave it blank to default to a special category of 'all' (which looks at the distribution of images to pick an actual category).\n"
                                + "\n"
                                + "Available categories: `" + string.Join("`, `", DirectoriesInContainer.Keys) + "`"
                    },
                    JsonMediaTypeFormatter.DefaultMediaType);
            }

            // If category is !timer, then do the timer
            if (data.text.StartsWith("!timer") || data.text.StartsWith("timer"))
            {
                // parts[0] !timer/timer
                // parts[1] TimeSpan/minutes interval
                // parts[2] TimeSpan/hours duration
                // parts[3]... Category(ies) (optional)
                var parts = data.text.Split(' ');
                data.text = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : null;
                if (parts.Length < 3)
                {
                    await SendMessageToSlack(data.channel_id, "You did not have the right number of arguments to `!timer`.", logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                string errMsg = null;
                var int_parse = parts[1].GetTimeSpan();
                if (!string.IsNullOrWhiteSpace(int_parse.msg)) errMsg = int_parse.msg;
                var dur_parse = parts[2].GetTimeSpan();
                if (!string.IsNullOrWhiteSpace(dur_parse.msg)) errMsg = dur_parse.msg;
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }
                logger.LogInformation("Category:{0} Interval:{1} Duration:{2}", data.text, int_parse.parsed, dur_parse.parsed);

                if (int_parse.parsed < TimeSpan.FromSeconds(30) || int_parse.parsed > TimeSpan.FromHours(24))
                    errMsg = "Interval must be between 30 seconds and 24 hours.";
                else if (dur_parse.parsed > TimeSpan.FromDays(7))
                    errMsg = $"Duration cannot last more than 7 days. (Is currently `{dur_parse.parsed}`)";
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                // Now that we passed command validation, actually schedule the messages
                var count = (int)(dur_parse.parsed.TotalSeconds / int_parse.parsed.TotalSeconds) + 1;
                for (int i = 0; i < count; i++)
                    await queue.AddMessageAsync(
                        new CloudQueueMessage(
                            JsonConvert.SerializeObject(
                                new Messages.Request
                                {
                                    category = data.text,
                                    channel_id = data.channel_id,
                                    response_url = data.response_url,
                                    user_name = data.user_name + $", timer {i + 1}/{count}"
                                })),
                        timeToLive: null,
                        initialVisibilityDelay: TimeSpan.FromSeconds(int_parse.parsed.TotalSeconds * i),
                        options: null,
                        operationContext: null);

                // Inform of the configuration
                await SendMessageToSlack(data.channel_id, $"{data.user_name} has scheduled {count} images for the '{data.text}' category every {int_parse.parsed} for the next {dur_parse.parsed}.", logger);
                return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
            }

            // If category is !cron, then do the cron
            if (data.text.StartsWith("!cron") || data.text.StartsWith("cron"))
            {
                // parts[0] !cron/cron `
                // parts[1] expression
                // parts[2] ` TimeSpan/hours duration Category(ies) (optional)
                var parts = data.text.Split('`');
                if (parts.Length < 2)
                {
                    await SendMessageToSlack(data.channel_id, "You did not have the right number of arguments to `!cron`.", logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }
                var expression = parts[1];
                data.text = data.text.Replace(expression, string.Empty);

                // parts[0] !cron/cron
                // parts[1] ``
                // parts[2] TimeSpan/hours duration
                // parts[3]... Category(ies) (optional)
                data.text = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : null;
                if (parts.Length < 3)
                {
                    await SendMessageToSlack(data.channel_id, "You did not have the right number of arguments to `!cron`.", logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                var schedule = NCrontab.CrontabSchedule.TryParse(expression);
                if (schedule == null)
                {
                    await SendMessageToSlack(data.channel_id, $"Could not parse your expression: {parts[1]}", logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                string errMsg = null;
                var dur_parse = parts[2].GetTimeSpan();
                if (!string.IsNullOrWhiteSpace(dur_parse.msg)) errMsg = dur_parse.msg;
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }
                logger.LogInformation("Category:{0} Duration:{2}", data.text, dur_parse.parsed);
                if (dur_parse.parsed > TimeSpan.FromDays(7))
                    errMsg = $"Duration cannot last more than 7 days. (Is currently `{dur_parse.parsed}`)";
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                var delays = new List<TimeSpan> { TimeSpan.Zero }.Union(schedule.GetNextOccurrences(DateTime.Now, DateTime.Now + dur_parse.parsed).Select(dt => dt - DateTime.Now)).ToList();
                for (int i = 0; i < delays.Count - 1; i++)
                {
                    var diff = delays[i + 1] - delays[i];
                    if (diff < TimeSpan.FromSeconds(30) || diff > TimeSpan.FromHours(24))
                    {
                        await SendMessageToSlack(data.channel_id, "Interval must be between 30 seconds and 24 hours.", logger);
                        return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                    }
                }

                // Now that we passed command validation, actually schedule the messages
                for (int i = 0; i < delays.Count; i++)
                    await queue.AddMessageAsync(
                        new CloudQueueMessage(
                            JsonConvert.SerializeObject(
                                new Messages.Request
                                {
                                    category = data.text,
                                    channel_id = data.channel_id,
                                    response_url = data.response_url,
                                    user_name = data.user_name + $", cron {i + 1}/{delays.Count}"
                                })),
                        timeToLive: null,
                        initialVisibilityDelay: delays[i],
                        options: null,
                        operationContext: null);

                // Inform of the configuration
                await SendMessageToSlack(data.channel_id, $"{data.user_name} has scheduled {delays.Count} images for the '{data.text}' category with expression `{schedule}` for the next {dur_parse.parsed}.", logger);
                return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
            }

            // If category is !random, then do the random
            if (data.text.StartsWith("!random") || data.text.StartsWith("random"))
            {
                // parts[0] !random/random
                // parts[1] int count
                // parts[2] TimeSpan/hours duration
                // parts[3]... Category(ies) (optional)
                var parts = data.text.Split(' ');
                data.text = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3) : null;
                if (parts.Length < 3)
                {
                    await SendMessageToSlack(data.channel_id, "You did not have the right number of arguments to `!random`.", logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                string errMsg = null;
                if (!int.TryParse(parts[1], out var cnt_parsed)) errMsg = $"Could not parse count from '{parts[1]}'.";
                var dur_parse = parts[2].GetTimeSpan();
                if (!string.IsNullOrWhiteSpace(dur_parse.msg)) errMsg = dur_parse.msg;
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }
                logger.LogInformation("Category:{0} Count:{1} Duration:{2}", data.text, cnt_parsed, dur_parse.parsed);

                if (dur_parse.parsed.TotalMinutes / cnt_parsed <= 5)
                    errMsg = "Cannot randomize more than 1 image per 5 minutes.";
                else if (dur_parse.parsed > TimeSpan.FromDays(7))
                    errMsg = $"Duration cannot last more than 7 days. (Is currently `{dur_parse.parsed}`)";
                if (errMsg != null)
                {
                    await SendMessageToSlack(data.channel_id, errMsg, logger);
                    return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
                }

                // Create the random intervals
                var count = cnt_parsed + 1;
                var delays = new TimeSpan[count];
                delays[0] = TimeSpan.Zero;
                var random = new Random();
                for (int i = 1; i < delays.Length; i++)
                    delays[i] = TimeSpan.FromMinutes(dur_parse.parsed.TotalMinutes * random.NextDouble());
                Array.Sort(delays);
                logger.LogInformation($"Delays: {string.Join(", ", delays)}");

                // Now that we passed command validation, actually schedule the messages
                for (int i = 0; i < count; i++)
                    await queue.AddMessageAsync(
                        new CloudQueueMessage(
                            JsonConvert.SerializeObject(
                                new Messages.Request
                                {
                                    category = data.text,
                                    channel_id = data.channel_id,
                                    response_url = data.response_url,
                                    user_name = data.user_name + $", random {i + 1}/{count}"
                                })),
                        timeToLive: null,
                        initialVisibilityDelay: delays[i],
                        options: null,
                        operationContext: null);

                // Inform of the configuration
                await SendMessageToSlack(data.channel_id, $"{data.user_name} has scheduled {count} images for the '{data.text}' category randomly for the next {dur_parse.parsed}.", logger);
                return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
            }

            if (data.text.StartsWith("!reset"))
            {
                var category = data.text.Split(' ').Last().Trim();
                var config = ImageContainer.GetBlockBlobReference(category + ".json");
                var successful = await config.DeleteIfExistsAsync();

                // Inform of the configuration
                await SendMessageToSlack(data.channel_id, $"Resetting configuration for {category} was {(successful ? string.Empty : "un")}successful.", logger);
                return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
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
            return req.CreateResponse(HttpStatusCode.OK, new { response_type = "in_channel" });
        }

        private static (string msg, TimeSpan parsed) GetTimeSpan(this string source)
        {
            // Look for the following formats:
            //  ##d --> number of days
            //  ##h --> number of hours
            //  ##m --> number of minutes
            //  HH:MM:SS --> regular timespan parsing

            source = source.ToLower();
            double parsed = 0;
            if (source.Contains("m"))
            {
                if (double.TryParse(source.Replace("m", String.Empty), out parsed))
                    return (null, TimeSpan.FromMinutes(parsed));
                else
                    return ($"Found a `m`, but couldn't parse minutes from '{source}'.", default);
            }
            else if (source.Contains("h"))
            {
                if (double.TryParse(source.Replace("h", String.Empty), out parsed))
                    return (null, TimeSpan.FromHours(parsed));
                else
                    return ($"Found a `h`, but couldn't parse hours from '{source}'.", default);
            }
            else if (source.Contains("d"))
            {
                if (double.TryParse(source.Replace("d", String.Empty), out parsed))
                    return (null, TimeSpan.FromDays(parsed));
                else
                    return ($"Found a `d`, but couldn't parse days from '{source}'.", default);
            }
            else
            {
                if (TimeSpan.TryParse(source, out TimeSpan parsedt))
                    return (null, parsedt);
                else
                    return ($"Couldn't parse a `TimeSpan` from '{source}'.", default);
            }
        }

        private static Dictionary<string, CloudBlobDirectory> DirectoriesInContainer;
        private static void PopulateDirectoriesInContainer(bool force = false)
        {
            // If directories in container is null, populate
            if (DirectoriesInContainer == null || force)
            {
                var dic = new Dictionary<string, CloudBlobDirectory>();
                foreach (var cbd in ImageContainer.ListBlobs().Where(_ => _ is CloudBlobDirectory).Select(_ => _ as CloudBlobDirectory))
                    dic.Add(cbd.Prefix.Substring(0, cbd.Prefix.Length - 1), cbd);
                DirectoriesInContainer = dic;
            }
        }

        private static Dictionary<string, int> UnseenCountInDirectories;
        private static DateTimeOffset UnseenCountInDirectoriesExpirationTime = DateTimeOffset.MinValue;
        private static async Task PopulateUnseenCountInDirectories()
        {
            // This will scan/update at most every 15 minutes or every service start
            if (UnseenCountInDirectoriesExpirationTime < DateTimeOffset.Now)
            {
                PopulateDirectoriesInContainer();
                UnseenCountInDirectories = new Dictionary<string, int>();

                foreach (var dir in DirectoriesInContainer)
                {
                    var config = Functions.ImageContainer.GetBlockBlobReference(dir.Key + ".json");

                    // No config, just add all the blobs
                    if (!await config.ExistsAsync())
                    {
                        var blobNames = dir.Value.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                        UnseenCountInDirectories.Add(dir.Key, blobNames.Count());
                        continue;
                    }

                    var ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());
                    UnseenCountInDirectories.Add(dir.Key, ds.UnseenFiles.Count);
                }
                UnseenCountInDirectoriesExpirationTime = DateTimeOffset.Now.AddMinutes(15);
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
                    if (dirstatus.SeenFiles.Count == 0)
                    {
                        sb.AppendLine("NOT YET QUERIED");
                        continue;
                    }
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

            // If they ask for a rescan, then do it
            if (request.category == "!rescan")
            {
                await Functions.RescanInternal(logger);
                return;
            }

            // Get configuration for category
            var config = ImageContainer.GetBlockBlobReference(request.category + ".json");
            string leaseId = null;
            DirectoryStatus ds;
            string responseChance = null;
            if (!await config.ExistsAsync())
            {
                logger.LogInformation("Populating configuration file...");
                if (DirectoriesInContainer.ContainsKey(request.category))
                {
                    var files = DirectoriesInContainer[request.category].ListBlobs().Where(_ => _ is CloudBlob).Select(_ => _ as CloudBlob);
                    ds = new DirectoryStatus
                    {
                        UnseenFiles = new HashSet<string>(files.Select(_ => _.Name))
                    };
                }
                else if (request.category == "all" || DirectoriesInContainer.Keys.Count(k => k.StartsWith(request.category.Substring(0, 1))) > 0)
                {
                    await PopulateUnseenCountInDirectories();
                    var categoryOptions = UnseenCountInDirectories;

                    // If there's a space in the category, then they're asking for multiple (potentially fuzzy) matches
                    if (request.category.Contains(" "))
                    {
                        var fuzzyCategories = request.category.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var matchedCategories = new HashSet<string>();
                        foreach (var fuzzyCategory in fuzzyCategories)
                            foreach (var categoryOption in categoryOptions.Keys.Where(k => k.StartsWith(fuzzyCategory)))
                                matchedCategories.Add(categoryOption);
                        categoryOptions = UnseenCountInDirectories.Where(kvp => matchedCategories.Contains(kvp.Key)).ToDictionary(_ => _.Key, _ => _.Value);
                    }
                    // Check for fuzzy matches or default to all
                    else if (DirectoriesInContainer.Keys.Count(k => k.StartsWith(request.category)) > 0)
                        categoryOptions = UnseenCountInDirectories.Where(kvp => kvp.Key.StartsWith(request.category)).ToDictionary(_ => _.Key, _ => _.Value);
                    logger.LogInformation("Categories to select from: {0}", JsonConvert.SerializeObject(categoryOptions));

                    // Pick one of them based on unseen image distribution (for better results)
                    var totalUnseen = categoryOptions.Sum(kvp => kvp.Value);
                    int offset = Random.Next(totalUnseen);
                    string category = null;
                    foreach (var kvp in categoryOptions)
                    {
                        category = kvp.Key;
                        offset -= kvp.Value;
                        if (offset <= 0)
                        {
                            responseChance = (kvp.Value * 1.0 / totalUnseen).ToString("p");
                            break;
                        }
                    }

                    // Make sure we were able to match something
                    if (category == null)
                    {
                        await HttpClient.PostAsJsonAsync(request.response_url, new
                        {
                            response_type = "in_channel",
                            text = "Could not determine any valid categories from that. Please try again."
                        });
                        return;
                    }

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
                logger.LogInformation("Notifying out of images...");
                await HttpClient.PostAsJsonAsync(request.response_url, new
                {
                    response_type = "in_channel",
                    text = $"No more unseen images for {request.category}. Use `!reset {request.category}`"
                });
                if (!string.IsNullOrWhiteSpace(leaseId))
                    await config.ReleaseLeaseAsync(AccessCondition.GenerateLeaseCondition(leaseId));
                return;
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

            await SendImageToSlack(request.category, request.user_name, request.channel_id, blob, logger, responseChance);

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

        private static async Task SendImageToSlack(string request_text, string user_name, string channel_id, CloudBlob blob, ILogger logger, string responseChance = null)
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
                            pretext = $"Request: '{request_text}' ({user_name})\nResponse: {blob.Name}{(responseChance == null ? string.Empty : "\nResponse category chance: " + responseChance)}",
                            image_url = blob.Uri.AbsoluteUri + sas
                        }
                    }
                });
            logger.LogInformation("{0} response: {1} {2}", nameof(SendImageToSlack), res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        private static async Task SendMessageToSlack(string channel, string text, ILogger logger)
        {
            var res = await HttpClient.PostAsJsonAsync("https://slack.com/api/chat.postMessage", new { channel, text });
            logger.LogInformation("{0} response: {1} {2}", nameof(SendMessageToSlack), res.StatusCode, await res.Content.ReadAsStringAsync());
        }
    }
}
