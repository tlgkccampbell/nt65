using Norristown.Flow;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What is shown in the lines of a file without being part of it: how long each instruction
/// takes at the end of its line, how long each block takes on the line that starts it, and on the
/// 65816 the processor state that reaches each label, beside its name.
/// </summary>
internal static class InlayHints
{
    /// <summary>The hints for the lines <paramref name="range"/> covers, in position order.</summary>
    public static IReadOnlyList<Protocol.InlayHint> In(
        SemanticModel model, CodeLayout? layout, ControlFlow? flow, StateAnalysis? states, TextSpan range)
    {
        var tree = model.Tree;
        var hints = new List<(int At, int Order, Protocol.InlayHint Hint)>();
        bool Within(SyntaxNode node) => node.Tree == tree && node.Span.End >= range.Start && node.Position <= range.End;

        if (layout is not null)
        {
            foreach (var line in tree.Root.DescendantNodes().Where(node => node.Green is GreenLine && Within(node)))
            {
                if (line.Statement is { Kind: SyntaxKind.InstructionStatement or SyntaxKind.EnsureDirective } statement
                    && layout.AnyOf(statement) is { Cycles: { } cycles })
                {
                    var end = LineContext.CodeEnd(tree, statement.LineIndex);
                    hints.Add((end, 0, Hint(tree, end, Lsp.Spell(cycles))));
                }
            }
        }

        // A block is shown on the line it starts at, after the instruction's own count.
        foreach (var block in flow?.Regions.SelectMany(region => region.Blocks) ?? [])
        {
            if (block is not { On: null, Cycles: { } cycles, Steps: [{ On: null, Statement: var first }, ..] } || !Within(first))
                continue;
            var end = LineContext.CodeEnd(tree, first.LineIndex);
            hints.Add((end, 1, Hint(tree, end, $"block: {Lsp.Spell(cycles)}")));

            // What reaches a label is what reaches the first line under it.
            if (block.Label is { Kind: SymbolKind.Label } label && label.Tree == tree && states?.AnyBefore(first) is { } state)
                hints.Add((label.NameSpan.End, 0, Hint(tree, label.NameSpan.End, $"{state.Processor}")));
        }
        return [.. hints.OrderBy(hint => hint.At).ThenBy(hint => hint.Order).Select(hint => hint.Hint)];
    }

    private static Protocol.InlayHint Hint(SyntaxTree tree, int at, string label) =>
        new(Lsp.ToPosition(tree, at), label, PaddingLeft: true);
}
