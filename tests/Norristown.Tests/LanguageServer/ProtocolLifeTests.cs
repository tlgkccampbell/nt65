using System.Text.Json;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The shipped executable, driven over its own standard input and output through its whole
/// lifecycle. Every other test drives the server in this process, which is faster but says
/// nothing about how the executable an editor starts copes with messages an editor would not
/// send. One process runs the whole sequence, because starting it costs more than all of it.
/// </summary>
public sealed class ProtocolLifeTests
{
    /// <summary>LSP's own code for a request that arrives before <c>initialize</c>.</summary>
    private const int ServerNotInitialized = -32002;

    private const int InvalidRequest = -32600;
    private const int ParseError = -32700;

    [Fact]
    public async Task TheRealServerAnswersAWholeLifeAndThenLeaves()
    {
        var timeout = TestContext.Current.CancellationToken;
        using var server = StdioServer.Start();

        // Before `initialize`, a request is refused and the server is still there to say so.
        await server.SendAsync(Request(1, "textDocument/documentSymbol", """
            {"textDocument":{"uri":"file:///c%3A/work/main.nt65"}}
            """), timeout);
        Assert.Equal(ServerNotInitialized, ErrorIn(await server.AnswerToAsync(1, timeout)));

        await server.SendAsync(Request(2, "initialize", """
            {"processId":null,"capabilities":{},"rootUri":null}
            """), timeout);
        var initialized = await server.AnswerToAsync(2, timeout);
        Assert.Equal(
            "utf-16", initialized.GetProperty("result").GetProperty("capabilities")
                .GetProperty("positionEncoding").GetString());
        await server.SendAsync("""{"jsonrpc":"2.0","method":"initialized","params":{}}""", timeout);

        // A frame that is not JSON is answered where the protocol says, with no id, and the
        // next frame is read as if nothing had happened.
        await server.SendBrokenAsync(timeout);
        // Skip any notifications the server sent of its own accord before the error.
        var broken = await server.ReceiveAsync(timeout);
        while (!broken.RootElement.TryGetProperty("error", out _))
            broken = await server.ReceiveAsync(timeout);
        Assert.Equal(JsonValueKind.Null, broken.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(ParseError, ErrorIn(broken.RootElement));

        // `"params": null` is how a client sends a request that takes no parameters. The
        // request is answered and the server carries on, rather than the process ending.
        await server.SendAsync("""
            {"jsonrpc":"2.0","id":3,"method":"textDocument/documentSymbol","params":null}
            """, timeout);
        Assert.NotEqual(0, ErrorIn(await server.AnswerToAsync(3, timeout)));

        await server.SendAsync(Request(4, "textDocument/didOpen", """
            {"textDocument":{"uri":"file:///c%3A/work/main.nt65","languageId":"nt65","version":1,
             "text":".module main\n.segment CODE\n.proc reset {\n    jsr step\n    rts\n}\n.proc step {\n    rts\n}\n"}}
            """), timeout);

        // A cancel for a request that may already be answered: either way the server answers
        // that id and stays up. An editor sends such a cancel every time the caret moves.
        await server.SendAsync(Request(5, "textDocument/documentSymbol", """
            {"textDocument":{"uri":"file:///c%3A/work/main.nt65"}}
            """), timeout);
        await server.SendAsync("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":5}}""", timeout);
        var cancelled = await server.AnswerToAsync(5, timeout);
        Assert.True(cancelled.TryGetProperty("result", out _) || cancelled.TryGetProperty("error", out _));

        // Every answer spells the file the way the client spelled it, escape and all.
        await server.SendAsync(Request(6, "textDocument/definition", """
            {"textDocument":{"uri":"file:///c%3A/work/main.nt65"},"position":{"line":3,"character":9}}
            """), timeout);
        var definition = await server.AnswerToAsync(6, timeout);
        var where = definition.GetProperty("result");
        Assert.Equal("file:///c%3A/work/main.nt65", where.GetProperty("uri").GetString());
        Assert.Equal(6, where.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await server.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"shutdown","params":null}""", timeout);
        Assert.True((await server.AnswerToAsync(7, timeout)).TryGetProperty("result", out _));

        // After `shutdown` nothing but `exit` is answered.
        await server.SendAsync(Request(8, "textDocument/documentSymbol", """
            {"textDocument":{"uri":"file:///c%3A/work/main.nt65"}}
            """), timeout);
        Assert.Equal(InvalidRequest, ErrorIn(await server.AnswerToAsync(8, timeout)));

        await server.SendAsync("""{"jsonrpc":"2.0","method":"exit"}""", timeout);
        Assert.True(server.Left(TimeSpan.FromSeconds(10)), "the server was still running after `exit`");
        Assert.Equal(0, server.ExitCode);
    }

    /// <summary>
    /// <c>exit</c> without a <c>shutdown</c> first ends the process with a failure code, as the
    /// protocol says; and a server should not outlive the editor that started it, because an
    /// editor that crashes never sends <c>exit</c>. The editor here is played by a second server
    /// process, and its own exit without <c>shutdown</c> is what the exit-code check is about.
    /// </summary>
    [Fact]
    public async Task LeavingWithoutShuttingDownFailsAndTheServerFollowsTheEditorOut()
    {
        var timeout = TestContext.Current.CancellationToken;
        using var editor = StdioServer.Start();
        using var server = StdioServer.Start();
        await server.SendAsync(
            Request(1, "initialize", $$"""
                {"processId":{{editor.Id}},"capabilities":{},"rootUri":null}
                """), timeout);
        await server.AnswerToAsync(1, timeout);
        Assert.False(server.Left(TimeSpan.Zero), "the server left while the editor was still there");

        await editor.SendAsync(Request(2, "initialize", """
            {"processId":null,"capabilities":{},"rootUri":null}
            """), timeout);
        await editor.AnswerToAsync(2, timeout);
        await editor.SendAsync("""{"jsonrpc":"2.0","method":"exit"}""", timeout);
        Assert.True(editor.Left(TimeSpan.FromSeconds(10)), "the editor was still running after `exit`");
        Assert.Equal(1, editor.ExitCode);

        Assert.True(server.Left(TimeSpan.FromSeconds(10)), "the server outlived the editor that started it");
    }

    private static string Request(int id, string method, string parameters) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

    /// <summary>The error code an answer carries, or 0 for one that carries a result.</summary>
    private static int ErrorIn(JsonElement answer) =>
        answer.TryGetProperty("error", out var error) ? error.GetProperty("code").GetInt32() : 0;
}
