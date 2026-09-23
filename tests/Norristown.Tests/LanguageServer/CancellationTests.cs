using System.Reflection;
using Nerdbank.Streams;
using Norristown.LanguageServer;
using Norristown.Syntax;
using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// A request the user no longer wants should stop rather than run to completion. Every handler
/// takes the token that the client's <c>$/cancelRequest</c> trips, and code that walks a whole
/// program checks the token between files, since that walk is where a slow request spends its
/// time.
/// </summary>
public sealed class CancellationTests
{
    /// <summary>
    /// Every handler takes a token except five: three manage the server's lifecycle, which a
    /// client does not cancel, and the other two answer from state the server already holds —
    /// the workspace's list of configurations, and which hints this session shows.
    /// </summary>
    [Fact]
    public void EveryRequestHandlerTakesACancellationToken()
    {
        var without = typeof(Server)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<JsonRpcMethodAttribute>() is not null)
            .Where(method => !method.GetParameters().Any(one => one.ParameterType == typeof(CancellationToken)))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["Configurations", "Exit", "Initialize", "Shutdown", "ToggleCycleHints"], without);
    }

    /// <summary>A cancelled search across a workspace of hundreds of files stops before the next file.</summary>
    [Fact]
    public void ASearchAcrossTheWorkspaceStopsBetweenFiles()
    {
        using var given = new CancellationTokenSource();
        SyntaxTree[] files =
        [
            SyntaxTree.Parse("a.nt65", ".module a\nCOUNT = 1\n"),
            SyntaxTree.Parse("b.nt65", ".module b\nCOUNT = 2\n"),
        ];
        Assert.Equal(2, WorkspaceSymbols.Matching(files, "count", TestTimeout.Token()).Count);

        given.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkspaceSymbols.Matching(files, "count", given.Token));
    }

    /// <summary>
    /// The client's <c>$/cancelRequest</c> reaches the token the handler is holding. Messages
    /// are read by the server's own framing layer, which decides which messages are passed on at
    /// all, so this shows that a cancel is passed on and cancels the handler.
    /// </summary>
    [Fact]
    public async Task ACancelFromTheClientTripsTheHandlersToken()
    {
        var timeout = TestTimeout.Token();
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var waiting = new Waiting();
        using var framing = new Framing(serverStream, serverStream, Server.CreateFormatter());
        using var server = new JsonRpc(framing);
        server.AddLocalRpcTarget(waiting);
        server.StartListening();
        using var client = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        client.StartListening();

        // The framing layer passes nothing else on until `initialize` has been answered.
        await client.InvokeWithParameterObjectAsync<object?>("initialize", null, timeout);

        using var giveUp = new CancellationTokenSource();
        var asked = client.InvokeWithParameterObjectAsync<object?>("waitForever", null, giveUp.Token);
        await waiting.Started.Task.WaitAsync(timeout);
        await giveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
    }

    /// <summary>A handler that does nothing but wait for the client to change its mind.</summary>
    private sealed class Waiting
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The handshake the framing layer waits for before it passes anything else on.</summary>
        /// <returns>Whether <c>waitForever</c> has been called yet, which at this point it has not.</returns>
        [JsonRpcMethod("initialize")]
        public bool Initialize() => Started.Task.IsCompleted;

        [JsonRpcMethod("waitForever")]
        public Task WaitForeverAsync(CancellationToken cancellation)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellation);
        }
    }
}
