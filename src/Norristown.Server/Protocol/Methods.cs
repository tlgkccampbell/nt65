namespace Norristown.Server.Protocol
{
    /// <summary>
    /// Contains the well-known names of the language server protocol's RPC methods.
    /// </summary>
    public static class Methods
    {
        public const String Initialize = "initialize";

        public const String Initialized = "initialized";

        public const String Shutdown = "shutdown";

        public const String Exit = "exit";
    }
}
