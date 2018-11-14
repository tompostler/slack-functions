using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;

namespace slack_functions
{
    public static partial class Functions
    {
        [FunctionName(nameof(KeepAliveEvenings))]
        public static void KeepAliveEvenings(
            [TimerTrigger("0 * 17-23 * * *")]TimerInfo timer,
            ILogger logger)
        {
            logger.LogInformation("IT'S {0} AND ALL IS WELL.", DateTimeOffset.Now);
        }

        [FunctionName(nameof(KeepAliveWeekendDay))]
        public static void KeepAliveWeekendDay(
            [TimerTrigger("0 * 11-17 * * Sat,Sun")]TimerInfo timer,
            ILogger logger)
        {
            logger.LogInformation("IT'S {0} AND ALL IS WELL.", DateTimeOffset.Now);
        }
    }
}
