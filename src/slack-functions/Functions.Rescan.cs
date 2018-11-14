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
        public static async Task Rescan(
            // 7PM every day
            [TimerTrigger("0 0,30 0-1,19-23 * * *")]TimerInfo timer,
            ILogger logger)
        {
            Functions.PopulateDirectoriesInContainer(force: true);

            // Run through each populated directory and see if we have to create/update its config with file changes
            var status = new Dictionary<string, string>();
            foreach (var dir in Functions.DirectoriesInContainer)
            {
                var fileNames = new HashSet<string>(dir.Value.ListBlobs().Where(_ => _ is CloudBlob).Select(_ => (_ as CloudBlob).Name));

                // Get or create the config
                var config = Functions.ImageContainer.GetBlockBlobReference(dir.Key + ".json");
                if (!await config.ExistsAsync())
                {
                    logger.LogInformation("Creating configuration file {0}...", config.Name);
                    await config.UploadTextAsync(JsonConvert.SerializeObject(new DirectoryStatus { UnseenFiles = fileNames }));
                    status.Add(dir.Key, "Created configuration file");
                    continue;
                }
                logger.LogInformation("Downloading configuration file {0}", config.Name);
                var leaseId = await config.AcquireLeaseAsync(TimeSpan.FromSeconds(45));
                var ds = JsonConvert.DeserializeObject<DirectoryStatus>(await config.DownloadTextAsync());

                // Remove from seen
                var extraSeen = ds.SeenFiles.Where(sf => !fileNames.Contains(sf)).ToList();
                var extraUnseen = ds.UnseenFiles.Where(uf => !fileNames.Contains(uf)).ToList();
                var newUnseen = fileNames.Where(fn => !ds.SeenFiles.Contains(fn) && !ds.UnseenFiles.Contains(fn)).ToList();
                status.Add(dir.Key, $"-{extraSeen.Count} seen, -{extraUnseen.Count}/+{newUnseen.Count} unseen");
                ds.SeenFiles.RemoveWhere(sf => extraSeen.Contains(sf));
                ds.UnseenFiles.RemoveWhere(uf => extraUnseen.Contains(uf));
                foreach (var nu in newUnseen) ds.UnseenFiles.Add(nu);

                // Write configuration back and release lease
                logger.LogInformation("Uploading configuration file {0}...", config.Name);
                await config.UploadTextAsync(JsonConvert.SerializeObject(ds), Encoding.UTF8, AccessCondition.GenerateLeaseCondition(leaseId), new BlobRequestOptions(), new OperationContext());
                await config.ReleaseLeaseAsync(AccessCondition.GenerateLeaseCondition(leaseId));
            }

            // Report status, if necessary
            logger.LogInformation(JsonConvert.SerializeObject(status));
            if (!String.IsNullOrWhiteSpace(Functions.SlackNotifyChannelId) && status.Count > 0)
            {
                var sb = new StringBuilder();
                var maxname = Math.Max(DirectoriesInContainer.Keys.Max(_ => _.Length), "CATEGORY".Length);
                sb.AppendLine("```");
                sb.AppendLine($"{"CATEGORY".PadRight(maxname)}  STATUS MESSAGE");
                foreach (var stat in status)
                {
                    sb.Append(stat.Key);
                    sb.Append("  ");
                    sb.AppendLine(stat.Value);
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
