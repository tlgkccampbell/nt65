using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Norristown.Server.Protocol;
using StreamJsonRpc;

namespace Norristown.Server
{
    /// <summary>
    /// An instance of the Norristown nt65 language server.
    /// </summary>
    public sealed class LanguageServer : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly JsonRpc _rpc;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Int32? _exitCode;

        /// <summary>
        /// Initializes a new instance of the <see cref="LanguageServer"/> type.
        /// </summary>
        public LanguageServer(JsonRpc rpc)
        {
            _rpc = rpc;

            foreach (var method in typeof(LanguageServer).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<JsonRpcMethodAttribute>();
                if (attr is not null)
                {
                    _rpc.AddLocalRpcMethod(method, this, attr);
                }
            }
        }

        /// <summary>
        /// Creates a new <see cref="LanguageServer"/> instance with the specified sending and receiving streams.
        /// </summary>
        /// <param name="sending">The server's sending stream.</param>
        /// <param name="receiving">The server's receiving stream.</param>
        /// <returns>The <see cref="LanguageServer"/> instance that was created.</returns>
        public static LanguageServer Create(Stream sending, Stream receiving)
        {
            ArgumentNullException.ThrowIfNull(sending);
            ArgumentNullException.ThrowIfNull(receiving);

            return new(new JsonRpc(new HeaderDelimitedMessageHandler(sending, receiving)));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _rpc.Dispose();
        }

        /// <summary>
        /// Begins reading messages from the underlying RPC connection.
        /// </summary>
        public void StartListening() => _rpc.StartListening();

        /// <summary>
        /// Waits for the session to end and reports the process exit code.
        /// </summary>
        /// <returns>The process exit code.</returns>
        public async Task<Int32> WaitForExitAsync()
        {
            await Task.WhenAny(_exited.Task, _rpc.Completion).ConfigureAwait(false);

            try
            {
                await _rpc.DispatchCompletion.ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            { /* ignored */ }

            _ = _rpc.Completion.Exception;

            lock (_gate)
            {
                return _exitCode ?? LanguageServerExitCodes.Error;
            }
        }

#pragma warning disable CA1822

        /// <summary>
        /// Handles <c>initialize</c>.
        /// </summary>
        /// <param name="request">An initialization request describing the client's capabilities.</param>
        /// <returns>An <see cref="InitializeResult"/> describing the server's capabilities.</returns>
        [JsonRpcMethod(Methods.Initialize, UseSingleObjectParameterDeserialization = true)]
        public InitializeResult Initialize(InitializeParams request)
        {
            return new InitializeResult { Capabilities = new() };
        }

        /// <summary>
        /// Handles <c>initialized</c>.
        /// </summary>
        /// <param name="request">The initialization notification's payload.</param>
        [JsonRpcMethod(Methods.Initialized, UseSingleObjectParameterDeserialization = true)]
        public void Initialized(InitializedParams request)
        {
            // TODO
        }

        /// <summary>
        /// Handles <c>shutdown</c>.
        /// </summary>
        /// <returns>Always returns <see cref="null"/>.</returns>
        [JsonRpcMethod(Methods.Shutdown)]
        public Object? Shutdown()
        {
            return null;
        }

        /// <summary>
        /// Handles <c>exit</c>.
        /// </summary>
        [JsonRpcMethod(Methods.Exit, UseSingleObjectParameterDeserialization = true)]
        public void Exit()
        {
            lock (_gate)
            {
                _exitCode = LanguageServerExitCodes.Error;
            }

            _exited.TrySetResult();
        }

#pragma warning restore
    }
}
