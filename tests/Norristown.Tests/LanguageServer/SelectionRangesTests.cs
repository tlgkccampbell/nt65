using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What a caret grows to take in as the selection is widened: the operand, the instruction, the
/// block and the routine. Each step is a node of the syntax tree: widening a selection walks the
/// structure of the program, which is what the tree already records.
/// </summary>
public sealed class SelectionRangesTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = """
        .module main
        .cpu 6502
        .segment CODE
        .export .proc main {
        @loop:
            lda origin,x
            bne @loop
            rts
        }
        .data origin: .byte 0
        """;

    [Fact]
    public async Task ACaretGrowsThroughTheTree()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.True(client.Initialized.Capabilities.SelectionRangeProvider);

        // The caret on `origin`, in `lda origin,x`: the name, the operand it is part of, the
        // instruction, and the whole routine declaration.
        var chain = Assert.Single(await client.SelectionRangesAsync(Uri, new Position(5, 10), timeout));
        Assert.Equal(
            [
                "origin",
                "origin,x",
                "lda origin,x",
                ".export .proc main {\n@loop:\n    lda origin,x\n    bne @loop\n    rts\n}",
            ],
            Selected(chain).Take(4));

        // The file is the last step, and every step is bigger than the one under it.
        Assert.EndsWith(".data origin: .byte 0", Selected(chain)[^1], StringComparison.Ordinal);
        Assert.Equal(
            Selected(chain).Select(text => text.Length).Order(),
            Selected(chain).Select(text => text.Length));
    }

    /// <summary>Each step of the chain, as the text it selects.</summary>
    private static IReadOnlyList<string> Selected(SelectionRange? chain)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var steps = new List<string>();
        for (var step = chain; step is not null; step = step.Parent)
        {
            var (from, to) = (step.Range.Start, step.Range.End);
            steps.Add(from.Line == to.Line
                ? lines[from.Line][from.Character..to.Character]
                : string.Join("\n",
                    (string[])[lines[from.Line][from.Character..], .. lines[(from.Line + 1)..to.Line],
                        lines[to.Line][..to.Character]]));
        }
        return steps;
    }
}
