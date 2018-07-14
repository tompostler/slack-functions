﻿namespace slack_functions
{
    public static class Messages
    {
        public sealed class Request
        {
            public string category { get; set; }
            public string channel_id { get; set; }
            public string response_url { get; set; }
            public string user_name { get; set; }
        }
    }
}
