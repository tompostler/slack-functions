namespace slack_functions
{
    public sealed class SlackPost
    {
        public string token { get; set; }
        public string text { get; set; }
        public string response_url { get; set; }
        public string user_name { get; set; }
    }
}
