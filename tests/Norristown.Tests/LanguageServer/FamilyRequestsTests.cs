using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets through an instance of a routine family, in both the folded form and
/// the long one: where it is declared, what renaming it touches, and what is offered after the
/// scope it is in. None of it expands anything: it is all what the binder knows of the family.
/// </summary>
public sealed class FamilyRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// One family written each way, and a routine that calls an instance of each. The line
    /// numbers below are this text's, counted from zero.
    /// </summary>
    private const string Source = """
        .module main
        .enum Channel {
            pulse1
            pulse2
            triangle
        }

        .segment CODE
        .scope play {
            .multiproc Channel, ch {
                rts
            }
        }

        .scope stop {
            .each Channel, ch {
                .proc ch {
                    rts
                }
            }
        }

        .export .proc main {
            jsr play::triangle
            jsr stop::triangle
            rts
        }
        """;

    /// <summary>
    /// An instance is declared where its family is written, so go to definition lands on the
    /// line that declares every one of them.
    /// </summary>
    [Fact]
    public async Task DefinitionGoesToTheFamilysLine()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `play::triangle` on line 23, and the `ch` of the `.multiproc` on line 9.
        var folded = await client.DefinitionAsync(Uri, new Position(23, 14), timeout);
        Assert.Equal(Uri, folded?.Uri);
        Assert.Equal(new Range(new Position(9, 24), new Position(9, 26)), folded?.Range);

        // `stop::triangle` on line 24, and the `ch` of the `.proc` on line 16.
        var written = await client.DefinitionAsync(Uri, new Position(24, 14), timeout);
        Assert.Equal(new Range(new Position(16, 14), new Position(16, 16)), written?.Range);
    }

    /// <summary>
    /// An instance's name is the enum member's, so renaming one renames the member and every
    /// use of every instance named after it. The families' own lines do not change.
    /// </summary>
    [Fact]
    public async Task RenamingAnInstanceRenamesTheMember()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(Uri, new Position(23, 14), "noise", timeout);
        Assert.NotNull(edit);
        var edits = edit.Changes[Uri];

        // The member on line 4, and the two calls on lines 23 and 24.
        Assert.Equal([4, 23, 24], edits.Select(e => e.Range.Start.Line).Order());
        Assert.All(edits, e => Assert.Equal("noise", e.NewText));
    }

    /// <summary>What a family declares is offered after the scope it is declared in.</summary>
    [Fact]
    public async Task CompletionAfterAScopeOffersTheInstances()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout, Source.Replace(
            "    jsr play::triangle", "    jsr play::", StringComparison.Ordinal));

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(Uri), new Position(23, 14)), timeout);

        var labels = items.Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["pulse1", "pulse2", "triangle"], labels.Order(StringComparer.Ordinal));
    }

    private static async Task<TestClient> OpenAsync(CancellationToken cancellation, string? text = null)
    {
        var client = await TestClient.StartAsync(cancellation);
        await client.OpenAsync(Uri, text ?? Source);
        if (text is null)
            Assert.Empty((await client.NextDiagnosticsAsync(cancellation)).Diagnostics);
        return client;
    }
}
