using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The fixes the diagnostics name, offered as code actions: each is applied, and the file it
/// leaves is compared with what the programmer would have written.
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
            ".proc main {\n    jmp ($1234)\n}\n",
            ".proc main {\n    jmp ($1234)\n    .next ?\n}\n"
        },
        {
            "Call with `jsl`",
            ".proc far_one: far {\n    rtl\n}\n.proc main {\n    JSR far_one\n    rts\n}\n",
            ".proc far_one: far {\n    rtl\n}\n.proc main {\n    JSL far_one\n    rts\n}\n"
        },
        {
            "Declare `@here` with `.state a8, i8, native`",
            ".proc main {\n    lda #<@here\n@here:\n    rts\n}\n",
            ".proc main {\n    lda #<@here\n@here:\n    .state a8, i8, native\n    rts\n}\n"
        },
        {
            "Declare `@here` with `.state a8, i8, native`",
            ".proc main {\n    lda #<@here\n@here: rts\n}\n",
            ".proc main {\n    lda #<@here\n@here:\n    .state a8, i8, native\n    rts\n}\n"
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
        Assert.Equal(Header + fixedBody, Apply(Header + body, action.Edit.Changes[MainUri]));

        // What the fix leaves is a file with nothing wrong.
        await client.ChangeAsync(MainUri, 2, new TextDocumentContentChangeEvent(null, Header + fixedBody));
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics.Select(d => d.Message));
    }

    /// <summary>A name another module declares and does not export is exported there, and one it exports is brought in here.</summary>
    [Fact]
    public async Task TheMissingExportOrUseIsWrittenWhereItBelongs()
    {
        var timeout = TestContext.Current.CancellationToken;
        const string Gfx = ".module gfx\n.segment CODE\n.proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Main = ".module main\n.use gfx::fill as paint\n.segment CODE\n.proc main {\n    jsr gfx::clear\n    jsr fill\n    rts\n}\n";
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx);
        await client.OpenAsync(MainUri, Main);
        for (var i = 0; i < 1 + 2; i++)
            await client.NextDiagnosticsAsync(timeout);

        var export = await ActionAsync(client, MainUri, "Export `clear` from `gfx`", timeout);
        Assert.Equal([GfxUri], export.Edit.Changes.Keys);
        Assert.Equal(".module gfx\n.export clear\n" + Gfx[".module gfx\n".Length..], Apply(Gfx, export.Edit.Changes[GfxUri]));

        var use = await ActionAsync(client, MainUri, "Bring in `gfx::fill` with `.use`", timeout);
        Assert.Equal(".module main\n.use gfx::fill as paint\n.use gfx::fill\n" + Main[".module main\n.use gfx::fill as paint\n".Length..],
            Apply(Main, use.Edit.Changes[MainUri]));
    }

    /// <summary>A diagnostic whose message names no fix is offered none.</summary>
    [Fact]
    public async Task ADiagnosticWithNoFixOffersNothing()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Header + ".proc main {\n    lda #\n    rts\n}\n");
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

    /// <summary><paramref name="text"/> with <paramref name="edits"/> made, the last in the file first so the earlier stay where they are.</summary>
    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        int Offset(Position position) => position.Line < starts.Count ? starts[position.Line] + position.Character : text.Length;
        foreach (var edit in edits.OrderByDescending(edit => Offset(edit.Range.Start)))
            text = text[..Offset(edit.Range.Start)] + edit.NewText + text[Offset(edit.Range.End)..];
        return text;
    }
}
