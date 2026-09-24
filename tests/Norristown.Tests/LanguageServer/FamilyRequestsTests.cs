using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what an editor is given for an instance of a routine family, declared both in the
/// compact <c>.multiproc</c> form and in the long <c>.each</c> form. The tests cover where an
/// instance is declared, what renaming it changes, what completion offers after the scope it is
/// in, and where its cost is shown. None of this expands the family; it all comes from what the
/// binder records about it.
/// </summary>
public sealed class FamilyRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// One family declared each way, and a routine that calls an instance of each. The line
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
            nop
            rts
        }
        """;

    /// <summary>
    /// An instance is declared where its family is declared, so go to definition lands on the
    /// line that declares every one of them.
    /// </summary>
    [Fact]
    public async Task DefinitionGoesToTheFamilysLine()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // `play::triangle` on line 23, and the `ch` of the `.multiproc` on line 9.
        var folded = await client.DefinitionAsync(Uri, Locate.At(Source, "play::|triangle"), timeout);
        Assert.Equal(Uri, folded?.Uri);
        Assert.Equal(Locate.Span(Source, ".multiproc Channel, |ch"), folded?.Range);

        // `stop::triangle` on line 24, and the `ch` of the `.proc` on line 16.
        var unfolded = await client.DefinitionAsync(Uri, Locate.At(Source, "stop::|triangle"), timeout);
        Assert.Equal(Locate.Span(Source, ".proc |ch"), unfolded?.Range);
    }

    /// <summary>
    /// An instance's name is the enum member's, so renaming one renames the member and every
    /// use of every instance named after it. The families' own lines do not change.
    /// </summary>
    [Fact]
    public async Task RenamingAnInstanceRenamesTheMember()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(Uri, Locate.At(Source, "play::|triangle"), "noise", timeout);
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
        var timeout = TestTimeout.Token();
        var text = Source.Replace("    jsr play::triangle", "    jsr play::", StringComparison.Ordinal);
        await using var client = await OpenAsync(timeout, text);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(Uri), Locate.At(text, "jsr play::|")), timeout);

        var labels = items.Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["pulse1", "pulse2", "triangle"], labels.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The instances of a family are all declared on its one line, so that line gets no lenses,
    /// which would crowd it with one per instance. The hover there gives each instance's cost.
    /// </summary>
    [Fact]
    public async Task AFamilyGetsNoLensesAndItsHoverGivesTheCost()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(Uri)), timeout);
        var folded = await client.HoverAsync(Uri, Locate.At(Source, ".multiproc Channel, |ch"), timeout);
        var unfolded = await client.HoverAsync(Uri, Locate.At(Source, ".proc |ch"), timeout);

        // Only `main`, on line 22, has lenses. The hover is the same in either form, on the
        // `.multiproc`'s binding on line 9 and on the name of the `.each`'s `.proc` on line 16.
        Assert.Equal([22], lenses.Select(lens => lens.Range.Start.Line).Distinct());
        foreach (var (hover, scope) in new[] { (folded, "play"), (unfolded, "stop") })
        {
            Assert.Equal($"""
                ```nt65
                repetition binding ch
                ```

                ```nt65-hover
                private to  {scope}
                cost        6 cycles
                reads       none
                preserves   A, X, Y, C
                declares    {scope}::pulse1, {scope}::pulse2, {scope}::triangle
                ```
                """.ReplaceLineEndings("\n"), hover?.Contents.Value);
        }
    }

    /// <summary>
    /// A call names one instance, so its hover gives that instance's cost alone; the family's
    /// binding is where every instance's cost is given.
    /// </summary>
    [Fact]
    public async Task AnInstanceNamedAtACallHoversWithItsOwnCost()
    {
        const string Text = """
            .module main
            .enum Channel {
                pulse1
                triangle
            }

            .segment CODE
            .scope play {
                .multiproc Channel, ch {
                    .if ch == Channel::triangle {
                        nop
                    }
                    rts
                }
            }

            .export .proc main {
                jsr play::triangle
                rts
            }
            """;
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout, Text);
        await client.NextDiagnosticsAsync(timeout);

        var call = await client.HoverAsync(Uri, Locate.At(Text, "play::|triangle"), timeout);
        var binding = await client.HoverAsync(Uri, Locate.At(Text, ".multiproc Channel, |ch"), timeout);

        Assert.Contains("cost       8 cycles\n", call?.Contents.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("pulse1", call?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("pulse1: 6 cycles", binding?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("triangle: 8 cycles", binding?.Contents.Value, StringComparison.Ordinal);
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
