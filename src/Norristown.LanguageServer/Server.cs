using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Norristown.LanguageServer;

internal sealed class Server
{
    private readonly ServerLog log;
    private JsonRpc? rpc;

    private Server(ServerLog log) => this.log = log;

    /// <summary>Serves one client until it sends <c>exit</c> or disconnects.</summary>
    public static async Task RunAsync(Stream input, Stream output, ServerLog log)
    {
        var server = new Server(log);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, CreateFormatter()));
        server.rpc = rpc;
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();
        try
        {
            await rpc.Completion;
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
            // The client went away without `exit`; nothing to clean up yet.
        }
    }

    internal static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return formatter;
    }

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(InitializeParams request)
    {
        var client = request.ClientInfo is { } info ? $"{info.Name} {info.Version}".TrimEnd() : "unknown client";
        log.Write($"connected: {client}");
        return new InitializeResult(new ServerCapabilities(), new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    [JsonRpcMethod("initialized")]
    public Task InitializedAsync(JsonElement _) =>
        rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready"));

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() => rpc!.Dispose();
}
