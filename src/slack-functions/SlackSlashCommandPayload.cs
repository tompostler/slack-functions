namespace slack_functions
{
    public sealed class SlackSlashCommandPayload
    {
        public string token { get; set; }
        public string text { get; set; }
        public string response_url { get; set; }
        public string user_name { get; set; }
        public string channel_id { get; set; }
    }
}
