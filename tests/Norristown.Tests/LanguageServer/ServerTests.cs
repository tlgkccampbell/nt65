using System.Text.Json;
using Nerdbank.Streams;
using Norristown.LanguageServer;
using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>Drives the server in-process over a pair of streams, the way an editor would.</summary>
public sealed class ServerTests
{
    [Fact]
    public async Task InitializesLogsTheConnectionAndExits()
    {
        var timeout = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var logText = new StringWriter();
        using var log = new ServerLog(logText);
        var server = Server.RunAsync(serverStream, serverStream, log);

        var client = new Client();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        rpc.AddLocalRpcTarget(client);
        rpc.StartListening();

        var result = await rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize",
            new { processId = (int?)null, clientInfo = new { name = "test-client", version = "1.0" }, capabilities = new { } },
            timeout);
        Assert.Equal("Norristown Assembler", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Contains("connected: test-client 1.0", logText.ToString());

        await rpc.NotifyWithParameterObjectAsync("initialized", new { });
        var message = await client.LogMessage.Task.WaitAsync(timeout);
        Assert.Equal("Norristown language server ready", message.GetProperty("message").GetString());

        await rpc.InvokeWithParameterObjectAsync<JsonElement>("shutdown", null, timeout);
        await rpc.NotifyWithParameterObjectAsync("exit", null);
        await server.WaitAsync(TimeSpan.FromSeconds(10), timeout);
    }

    private sealed class Client
    {
        public TaskCompletionSource<JsonElement> LogMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnLogMessage(JsonElement message) => LogMessage.TrySetResult(message);
    }
}
