using System.Collections.Generic;

namespace slack_functions
{
    public sealed class DirectoryStatus
    {
        public HashSet<string> SeenFiles { get; set; } = new HashSet<string>();
        public HashSet<string> UnseenFiles { get; set; } = new HashSet<string>();
    }
}
