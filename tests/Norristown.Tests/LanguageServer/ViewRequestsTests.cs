using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The two views beside a source: what the file became, and what a macro call becomes. Both are
/// one request each, so that a client is only showing and highlighting, and both answer about
/// the program as the editor holds it rather than as the disk does.
/// </summary>
public sealed class ViewRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>One file with a macro in it. The line numbers below are this text's, from zero.</summary>
    private const string Source = """
        .module main
        .segment ZEROPAGE
        .data ptr: .word

        SCREEN = $0400

        .macro set16(dest: operand, value) {
            lda #<value
            sta dest
            lda #>value
            sta dest+1
        }

        .segment CODE
        .export .proc main {
            set16!(ptr, SCREEN)
            rts
        }
        """;

    /// <summary>
    /// The output is the ca65 the build writes, and every line of it that a source line wrote
    /// says which one, so a caret in either can point at the other.
    /// </summary>
    [Fact]
    public async Task TheOutputIsTheCa65WithTheLinesItCameFrom()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var output = await OutputAsync(client, timeout);
        Assert.NotNull(output);
        Assert.Equal("main.s", output.Path);
        Assert.Equal(Uri, output.Uri);
        Assert.Equal(1, output.Version);
        Assert.Null(output.Note);
        Assert.Contains("lda #<SCREEN", output.Text, StringComparison.Ordinal);

        // The header is the `.feature` block, which is there for ca65: a view opens past it.
        Assert.True(output.Header > 0);
        Assert.All(output.Lines, run => Assert.True(run.First >= output.Header));

        // The call's line became the four lines of the expansion, in one run.
        var call = output.Lines.Single(run => run.Source == 15);
        Assert.Equal(4, call.Last - call.First + 1);
        var lines = output.Text.Split('\n');
        Assert.Equal("    lda #<SCREEN", lines[call.First]);
        Assert.Equal("    sta z:ptr+1", lines[call.Last]);
    }

    /// <summary>
    /// The output follows the editor and not the disk, and a file with errors shows what could
    /// be written under a first line saying that it is incomplete and why.
    /// </summary>
    [Fact]
    public async Task TheOutputFollowsTheEditorAndSaysWhenItIsIncomplete()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);
        Assert.NotNull(await OutputAsync(client, timeout));

        // An unsaved edit: the call is given a name nothing declares.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(15, 16), new Position(15, 22)), "MISSING"));
        _ = await client.NextDiagnosticsAsync(Uri, timeout);

        // Having been asked once, the server says when the program has settled after an edit.
        var said = await client.NextOutputChangedAsync(timeout);
        Assert.Equal(Uri, said.GetProperty("uri").GetString());

        var output = await OutputAsync(client, timeout);
        Assert.NotNull(output);
        Assert.Equal(2, output.Version);
        Assert.Equal(
            "nt65: incomplete, because the program is wrong. line 16: `MISSING` is not declared",
            output.Note);
        Assert.StartsWith($"; {output.Note}\n", output.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A call is written out as the nt65 the programmer would have written, with what it becomes
    /// said first, and a hover over it says the same thing.
    /// </summary>
    [Fact]
    public async Task ACallIsWrittenOutAsNt65()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var expansion = await client.RequestAsync<ExpansionResult?>("nt65/expansion",
            new ExpansionParams(new TextDocumentIdentifier(Uri), new Position(15, 6)), timeout);
        Assert.NotNull(expansion);
        Assert.Equal("set16!(ptr, SCREEN)", expansion.Title);
        Assert.Equal("expands to 4 lines · 8 bytes · 10 cycles", expansion.Summary);
        Assert.Equal("lda #<SCREEN\nsta ptr\nlda #>SCREEN\nsta ptr+1\n", expansion.Text);
        Assert.Empty(expansion.Links);
        Assert.Null(expansion.Note);

        // Hover over the call says the signature and the comment as it always did, and then the
        // one line a call is asked about, and then what it becomes.
        var hover = await client.HoverAsync(Uri, new Position(15, 6), timeout);
        Assert.NotNull(hover);
        Assert.Contains(".macro set16(dest: operand, value)", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("expands to 4 lines · 8 bytes · 10 cycles", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("lda #<SCREEN", hover.Contents.Value, StringComparison.Ordinal);

        // Four lines fit, so there is nothing left to send anyone to a view for.
        Assert.DoesNotContain("Show expansion", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Past eight lines the listing stops being something a reader takes in at a glance, so what
    /// is left is a link to the view that holds it, saying how much was left out.
    /// </summary>
    [Fact]
    public async Task ALongExpansionIsSummarisedAndLinkedTo()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, """
            .module main
            .macro clear(n: const) {
                .repeat n, i {
                    lda #0
                    sta $0400 + i
                }
            }
            .segment CODE
            .export .proc main {
                clear!(5)
                rts
            }
            """);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);

        var hover = await client.HoverAsync(Uri, new Position(9, 6), timeout);
        Assert.NotNull(hover);
        var said = hover.Contents.Value;
        Assert.Contains("expands to 10 lines · ", said, StringComparison.Ordinal);
        Assert.Contains("[Show expansion](command:nt65.showExpansion?", said, StringComparison.Ordinal);
        Assert.Contains("— 2 more lines", said, StringComparison.Ordinal);

        // Eight lines and no more: the listing is the working, and the summary is the answer.
        var listing = said.Split("```nt65")[^1].Split("```")[0].Trim().Split('\n');
        Assert.Equal(8, listing.Length);
        Assert.Equal("lda #0", listing[0].Trim());
    }

    /// <summary>A call written out in place of itself, and the reasons it is not offered.</summary>
    [Fact]
    public async Task ACallIsInlinedWhereThatChangesNothingButTheText()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var actions = await client.RequestAsync<IReadOnlyList<CodeAction>>("textDocument/codeAction",
            new CodeActionParams(
                new TextDocumentIdentifier(Uri),
                new Range(new Position(15, 6), new Position(15, 6)),
                new CodeActionContext([])),
            timeout);
        var inline = Assert.Single(actions, action => action.Title == "Inline `set16!`");
        Assert.Null(inline.Disabled);
        var edit = Assert.Single(inline.Edit.Changes[Uri]);
        Assert.Equal(
            "    lda #<SCREEN\n    sta ptr\n    lda #>SCREEN\n    sta ptr+1\n",
            edit.NewText);

    }

    /// <summary>
    /// A call written inside a macro body is expanded by whatever expands that body, so it is
    /// offered greyed with the reason rather than quietly left out.
    /// </summary>
    [Fact]
    public async Task ACallInsideABodyIsRefusedWithTheReason()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, """
            .module main
            .macro twice(n) {
                .byte n, n
            }
            .macro pair(n) {
                twice!(n)
            }
            .segment RODATA
            .export .data table {
                pair!(3)
            }
            """);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);

        var actions = await client.RequestAsync<IReadOnlyList<CodeAction>>("textDocument/codeAction",
            new CodeActionParams(
                new TextDocumentIdentifier(Uri),
                new Range(new Position(5, 6), new Position(5, 6)),
                new CodeActionContext([])),
            timeout);
        var refused = Assert.Single(actions, action => action.Title == "Inline `twice!`");
        Assert.NotNull(refused.Disabled);
        Assert.Contains("written in a macro body", refused.Disabled.Reason, StringComparison.Ordinal);
        Assert.Empty(refused.Edit.Changes);
    }

    private static Task<OutputResult?> OutputAsync(TestClient client, CancellationToken cancellation) =>
        client.RequestAsync<OutputResult?>("nt65/output",
            new OutputParams(new TextDocumentIdentifier(Uri)), cancellation);

    private static async Task<TestClient> OpenAsync(CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(cancellation);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, cancellation)).Diagnostics);
        return client;
    }
}
