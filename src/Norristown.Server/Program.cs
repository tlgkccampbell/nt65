namespace Norristown.Server
{
    /// <summary>
    /// Entry point for the Norristown nt65 language server.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Runs the language server until the client asks it to stop.
        /// </summary>
        /// <param name="args">The program's command line arguments.</param>
        /// <returns>The process exit code.</returns>
        public static async Task<Int32> Main(String[] args)
        {
            using var server = LanguageServer.Create(
                Console.OpenStandardOutput(),
                Console.OpenStandardInput());

            server.StartListening();

            return await server.WaitForExitAsync().ConfigureAwait(false);
        }
    }
}