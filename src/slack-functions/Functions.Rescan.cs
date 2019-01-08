using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace slack_functions
{
    public static partial class Functions
    {
        [FunctionName(nameof(Rescan))]
        public static Task Rescan(
            // Every night at 7, 9, 11, and 1
            [TimerTrigger("0 0 19,21,23,1 * * *")]TimerInfo timer,
            ILogger logger)
        {
            return Functions.RescanInternal(logger);
        }

        private static async Task RescanInternal(ILogger logger)
        {
            Functions.PopulateDirectoriesInContainer(force: true);

            // Run through each populated directory and see if we have to create/update its config with file changes
            var status = new Dictionary<string, (string, int, int)>();
            foreach (var dir in Functions.DirectoriesInContainer)
            {
                var fileNames = new HashSet<string>(dir.Value.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => (_ as CloudBlob).Name));

                // Get or create the config
                var config = Functions.ImageContainer.GetBlockBlobReference(dir.Key + ".json");
                if (!await config.ExistsAsync())
                {
                    logger.LogInformation("Creating configuration file {0}...", config.Name);
                    await config.UploadTextAsync(JsonConvert.SerializeObject(new DirectoryStatus { UnseenFiles = fileNames }));
                    status.Add(dir.Key, ("Created configuration file", 0, 0));
                    continue;
                }
                logger.LogInformation("Downloading configuration file {0}", config.Name);
                var leaseId = await config.AcquireLeaseAsync(TimeSpan.FromSeconds(45));
                var ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());

                // Remove from seen
                var extraSeen = ds.SeenFiles.Where(sf => !fileNames.Contains(sf)).ToList();
                var extraUnseen = ds.UnseenFiles.Where(uf => !fileNames.Contains(uf)).ToList();
                var newUnseen = fileNames.Where(fn => !ds.SeenFiles.Contains(fn) && !ds.UnseenFiles.Contains(fn)).ToList();
                if (extraSeen.Count > 0 || extraUnseen.Count > 0 || newUnseen.Count > 0)
                {
                    status.Add(dir.Key, (null, extraSeen.Count + extraUnseen.Count, newUnseen.Count));
                    ds.SeenFiles.RemoveWhere(sf => extraSeen.Contains(sf));
                    ds.UnseenFiles.RemoveWhere(uf => extraUnseen.Contains(uf));
                    foreach (var nu in newUnseen) ds.UnseenFiles.Add(nu);

                    // Write configuration back
                    logger.LogInformation("Uploading configuration file {0}...", config.Name);
                    await config.UploadTextAsync(JsonConvert.SerializeObject(ds), Encoding.UTF8, AccessCondition.GenerateLeaseCondition(leaseId), new BlobRequestOptions(), new OperationContext());
                }

                // Make sure we release the lease
                await config.ReleaseLeaseAsync(AccessCondition.GenerateLeaseCondition(leaseId));
            }

            // Report status, if necessary
            logger.LogInformation(JsonConvert.SerializeObject(status));
            if (!String.IsNullOrWhiteSpace(Functions.SlackNotifyChannelId) && status.Count > 0)
            {
                var sb = new StringBuilder();
                var maxname = Math.Max(status.Keys.Max(_ => _.Length), "CATEGORY".Length);
                sb.AppendLine("```");
                sb.AppendLine($"{"CATEGORY".PadRight(maxname)}  REMOVALS  ADDITIONS");
                foreach (var stat in status)
                {
                    sb.Append(stat.Key.PadRight(maxname));
                    sb.Append("  ");
                    if (String.IsNullOrWhiteSpace(stat.Value.Item1))
                    {
                        sb.Append($"{stat.Value.Item2}".PadLeft("REMOVALS".Length));
                        sb.Append("  ");
                        sb.Append($"{stat.Value.Item3}".PadLeft("ADDITIONS".Length));
                        sb.AppendLine();
                    }
                    else
                        sb.AppendLine(stat.Value.Item1);
                }
                sb.AppendLine("```");

                // Send it to slack
                var res = await Functions.HttpClient.PostAsJsonAsync(
                    "https://slack.com/api/chat.postMessage",
                    new
                    {
                        channel = Functions.SlackNotifyChannelId,
                        text = sb.ToString()
                    });
                logger.LogInformation("{0} response: {1} {2}", nameof(Rescan), res.StatusCode, await res.Content.ReadAsStringAsync());
            }
        }
    }
}
