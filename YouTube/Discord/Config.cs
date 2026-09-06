namespace YouTube.Discord
{
    // Discord application configuration lives outside of the page code.  The application id is
    // public (it is sent by every Rich Presence client); no client secret belongs in the app.
    internal static class Config
    {
        public const string ApplicationId = "1541840878484856843";
        public const string ClientId = "1541840878484856843";

        public const int RpcVersion = 1;

        // Coalescing keeps the one-second player clock from flooding Discord IPC.
        public const int PublishDelayMilliseconds = 350;
        public const int ProgressPublishIntervalSeconds = 10;
        public const int ReconnectDelaySeconds = 15;

        public const string ProductName = "YouTube";
        public const string PresenceEnabledSettingKey = "DiscordPresenceEnabled";
        // Set this only after uploading an image with the same key in Developer Portal.
        public const string LargeImageKey = "";
        public const string LargeImageText = "YouTube for Windows";
    }
}
