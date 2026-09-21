using System.Reflection;
using Nerdbank.Streams;
using Norristown.LanguageServer;
using Norristown.Syntax;
using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// A request the person has moved on from should stop rather than finish. Every handler takes
/// the token the client's <c>$/cancelRequest</c> trips, and the places that walk a whole
/// program ask it between files, which is where a slow answer spends its time.
/// </summary>
public sealed class CancellationTests
{
    /// <summary>
    /// The four that take none: three are the server's own life, which a client does not
    /// cancel, and the fourth answers from a list the workspace already holds.
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

        Assert.Equal(["Configurations", "Exit", "Initialize", "Shutdown"], without);
    }

    /// <summary>A search across a workspace of hundreds of files stops at the next one.</summary>
    [Fact]
    public void ASearchAcrossTheWorkspaceStopsBetweenFiles()
    {
        using var given = new CancellationTokenSource();
        SyntaxTree[] files =
        [
            SyntaxTree.Parse("a.nt65", ".module a\nCOUNT = 1\n"),
            SyntaxTree.Parse("b.nt65", ".module b\nCOUNT = 2\n"),
        ];
        Assert.Equal(2, WorkspaceSymbols.Matching(files, "count", TestContext.Current.CancellationToken).Count);

        given.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkspaceSymbols.Matching(files, "count", given.Token));
    }

    /// <summary>
    /// The client's <c>$/cancelRequest</c> reaches the token the handler is holding. The frames
    /// are read by the server's own framing layer, which decides what crosses at all, so what
    /// this shows is that a cancel crosses it and does what it is for.
    /// </summary>
    [Fact]
    public async Task ACancelFromTheClientTripsTheHandlersToken()
    {
        var timeout = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var waiting = new Waiting();
        using var framing = new Framing(serverStream, serverStream, Server.CreateFormatter());
        using var server = new JsonRpc(framing);
        server.AddLocalRpcTarget(waiting);
        server.StartListening();
        using var client = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        client.StartListening();

        // Nothing but `initialize` is answered before it, this layer included.
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
        /// <returns>Whether the wait has been asked for yet, which at this point it has not.</returns>
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
