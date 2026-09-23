using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The fixes that diagnostics suggest, offered as code actions: each is applied, and the
/// resulting file is compared with what the programmer would have written by hand.
/// </summary>
public sealed class CodeActionsTests
{
    private const string MainUri = "file:///c:/work/main.nt65";

    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    public static TheoryData<string, string, string> Fixes => new()
    {
        {
            "End the path here with `.next ?`",
            ".export .proc main {\n    jmp ($1234)\n}\n",
            ".export .proc main {\n    jmp ($1234)\n    .next ?\n}\n"
        },
        {
            "Say it runs into `after` with `.fallthrough`",
            ".export .proc main {\n    .if 1 {\n        nop\n    }\n}\n.export .proc after {\n    rts\n}\n",
            ".export .proc main {\n    .if 1 {\n        nop\n    }\n    .fallthrough after\n}\n.export .proc after {\n    rts\n}\n"
        },
        {
            "Say the branch is always taken with `.next @over`",
            ".export .proc main {\n    sec\n    bcs @over\n    .byte 1\n@over:\n    rts\n}\n",
            ".export .proc main {\n    sec\n    bcs @over\n    .next @over\n    .byte 1\n@over:\n    rts\n}\n"
        },
        {
            "Write it as `.fallthrough`",
            ".export .proc main {\n    jsr after\n    .next after\n}\n.export .proc after {\n    rts\n}\n",
            ".export .proc main {\n    jsr after\n    .fallthrough after\n}\n.export .proc after {\n    rts\n}\n"
        },
        {
            "Call with `jsl`",
            ".proc far_one: far {\n    rtl\n}\n.export .proc main {\n    JSR far_one\n    rts\n}\n",
            ".proc far_one: far {\n    rtl\n}\n.export .proc main {\n    JSL far_one\n    rts\n}\n"
        },
        {
            "Declare `@here` with `.state a8, i8, native`",
            ".export .proc main: a8, i8 {\n    lda #<@here\n@here:\n    rts\n}\n",
            ".export .proc main: a8, i8 {\n    lda #<@here\n@here:\n    .state a8, i8, native\n    rts\n}\n"
        },
        {
            "Declare `@here` with `.state a8, i8, native`",
            ".export .proc main: a8, i8 {\n    lda #<@here\n@here: rts\n}\n",
            ".export .proc main: a8, i8 {\n    lda #<@here\n@here:\n    .state a8, i8, native\n    rts\n}\n"
        },
        {
            "Make `table` a `.data` declaration",
            ".segment RODATA\ntable:\n    .byte 1, 2\n",
            ".segment RODATA\n.data table: .byte 1, 2\n"
        },
        {
            "Make `table` a `.data` declaration",
            ".segment RODATA\ntable: .byte 1\n    .word 2, 3\n",
            ".segment RODATA\n.data table {\n    .byte 1\n    .word 2, 3\n}\n"
        },
    };

    [Theory]
    [MemberData(nameof(Fixes))]
    public async Task AFixWritesWhatTheDiagnosticNames(string title, string body, string fixedBody)
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Header + body);
        await client.NextDiagnosticsAsync(timeout);

        var action = await ActionAsync(client, MainUri, title, timeout);

        Assert.Equal("quickfix", action.Kind);
        Assert.Equal([MainUri], action.Edit.Changes.Keys);
        Assert.Equal(Header + fixedBody, Editing.Apply(Header + body, action.Edit.Changes[MainUri]));

        // What the fix leaves is a file with nothing wrong.
        await client.ChangeAsync(MainUri, 2, new TextDocumentContentChangeEvent(null, Header + fixedBody));
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics.Select(d => d.Message));
    }

    /// <summary>A name another module declares but does not export gets an <c>.export</c> in that module; one it does export gets a <c>.use</c> here.</summary>
    [Fact]
    public async Task TheMissingExportOrUseIsWrittenWhereItBelongs()
    {
        var timeout = TestContext.Current.CancellationToken;
        const string Gfx = ".module gfx\n.segment CODE\n.proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Main = ".module main\n.use gfx::fill as paint\n.segment CODE\n.export .proc main {\n    jsr gfx::clear\n    jsr fill\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.OpenAsync(MainUri, Main);
        await client.NextDiagnosticsAsync(MainUri, timeout);

        var export = await ActionAsync(client, MainUri, "Export `clear` from `gfx`", timeout);
        Assert.Equal([GfxUri], export.Edit.Changes.Keys);
        Assert.Equal(".module gfx\n.export clear\n" + Gfx[".module gfx\n".Length..], Editing.Apply(Gfx, export.Edit.Changes[GfxUri]));

        var use = await ActionAsync(client, MainUri, "Bring in `gfx::fill` with `.use`", timeout);
        Assert.Equal(".module main\n.use gfx::fill as paint\n.use gfx::fill\n" + Main[".module main\n.use gfx::fill as paint\n".Length..],
            Editing.Apply(Main, use.Edit.Changes[MainUri]));
    }

    /// <summary>A diagnostic whose message names no fix is offered none.</summary>
    [Fact]
    public async Task ADiagnosticWithNoFixOffersNothing()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        // On the 6502 an immediate has only one width, so the only diagnostic on the half-written
        // line is the parser's, and no edit the server could write would fix it.
        await client.OpenAsync(MainUri, ".module main\n.cpu 6502\n.segment CODE\n.export .proc main {\n    lda #\n    rts\n}\n");
        Assert.NotEmpty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        Assert.Empty(await ActionsAsync(client, MainUri, timeout));
    }

    private static async Task<CodeAction> ActionAsync(TestClient client, string uri, string title, CancellationToken timeout)
    {
        var actions = await ActionsAsync(client, uri, timeout);
        return Assert.Single(actions, action => action.Title == title);
    }

    private static Task<IReadOnlyList<CodeAction>> ActionsAsync(TestClient client, string uri, CancellationToken timeout) =>
        client.RequestAsync<IReadOnlyList<CodeAction>>("textDocument/codeAction",
            new CodeActionParams(new TextDocumentIdentifier(uri), new Range(new Position(0, 0), new Position(100, 0)),
                new CodeActionContext([])),
            timeout);
}
