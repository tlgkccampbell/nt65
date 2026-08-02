namespace Norristown.Server
{
    /// <summary>
    /// Contains the process exit codes produced by the Norristown language server.
    /// </summary>
    internal static class LanguageServerExitCodes
    {
        /// <summary>
        /// The process exited successfully.
        /// </summary>
        public const Int32 Success = 0;

        /// <summary>
        /// The process exited with an error.
        /// </summary>
        public const Int32 Error = 1;
    }
}
