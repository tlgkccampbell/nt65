using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// A macro call replaced by what it expands to, which is how one stops using a macro. It writes
/// the same nt65 the expansion view shows, indented where the call was.
/// <para>
/// It is a text edit rather than a rewrite of the tree: the expansion is written from a body in
/// another place, with its arguments put in and its conditions decided, so there is no node of
/// this file to replace it with. A rewrite above a statement goes out through text in any case.
/// </para>
/// <para>
/// A body's names are resolved where the macro is written and its locals belong to each
/// expansion, so two things stand between the expansion and the file. Where the macro is
/// another file's, the change is refused: the names would be resolved again here and could mean
/// something else. Where the body declares anything, the expansion goes in an anonymous
/// <c>.scope</c>, which is what the language offers for a name that would otherwise be declared
/// twice in one routine.
/// </para>
/// </summary>
internal static class InlineMacro
{
    /// <summary>The change offered where the caret is on a macro call, or none where it is not.</summary>
    /// <param name="analysis">The program the call is in.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="caret">Where in that file's text.</param>
    public static IEnumerable<Change> In(ProgramAnalysis analysis, SemanticModel model, int caret)
    {
        var tree = model.Tree;
        if (MacroExpansion.CallAt(model, caret) is not { } call)
            yield break;
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition } macro)
            yield break;

        var title = $"Inline `{macro.Name}!`";
        if (macro.Tree != tree)
        {
            yield return Refused(title,
                $"`{macro.Name}!` is declared in {Named(macro.Tree.Path)}, and its body's names are resolved "
                + "there; written out here they would be resolved again");
            yield break;
        }
        if (InABody(call))
        {
            yield return Refused(title,
                "the call is written in a macro body, where what its arguments stand for is not known "
                + "until the body is expanded");
            yield break;
        }
        if (MacroExpansion.Of(analysis, model, call) is not { } expansion)
            yield break;
        if (expansion.Refusal is { } why)
        {
            yield return Refused(title, why);
            yield break;
        }

        var first = tree.GetLineIndex(call.Position);
        var last = Last(tree, call, first);
        var indent = Edits.IndentOf(tree, first);

        // What the body declares is local to each expansion, and two expansions in one routine
        // would declare it twice; an anonymous scope is inline code and keeps them apart.
        var scoped = Declares(model, definition);
        var inner = scoped ? indent + Edits.Indent : indent;
        var written = expansion.Lines.Select(line => line.Length == 0 ? "" : inner + line).ToList();
        if (scoped)
            written = [indent + ".scope {", .. written, indent + "}"];
        yield return new Change(title, CodeActionKinds.Rewrite,
            [Edits.RemoveLines(tree, first, last) with { Text = written.Count == 0 ? "" : string.Join("\n", written) + "\n" }]);
    }

    /// <summary>The change as it is offered when it cannot be applied: named, and greyed with the reason.</summary>
    private static Change Refused(string title, string why) =>
        new(title, CodeActionKinds.Rewrite, [], Refused: why);

    /// <summary>The last line the call covers: its own, and the blocks it opens.</summary>
    private static int Last(SyntaxTree tree, MacroCallSyntax call, int first)
    {
        var blocks = Macros.BlocksOf(call);
        return blocks.Count == 0 ? first : tree.GetLineIndex(blocks[^1].FullSpan.End - 1);
    }

    /// <summary>Whether a call is written inside a macro body, where its arguments are names and not values.</summary>
    private static bool InABody(SyntaxNode call)
    {
        for (var at = call.Parent; at is not null; at = at.Parent)
        {
            if (at is BlockSyntax { BlockKind: BlockKind.Macro })
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a body declares anything of its own, which each expansion has one of. The line
    /// that opens the block declares the macro and its parameters, and is not the body.
    /// </summary>
    private static bool Declares(SemanticModel model, BlockSyntax definition) =>
        model.Symbols.Any(symbol => symbol.Tree == definition.Tree
            && symbol.NameSpan.Start >= definition.Opener.FullSpan.End
            && symbol.NameSpan.Start < definition.FullSpan.End);

    /// <summary>A file as a reader names it: its own name, without the folders above it.</summary>
    private static string Named(string path) => path[(path.LastIndexOf('/') + 1)..];
}
