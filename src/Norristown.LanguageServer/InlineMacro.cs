using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the refactoring that replaces a macro call with its expansion, for moving code off a
/// macro. It produces the same nt65 text the expansion view shows, indented to where the call was.
/// <para>
/// It is a text edit rather than a rewrite of the syntax tree. The expansion is built from a
/// body declared elsewhere, with the arguments substituted and its conditions resolved, so no
/// node in this file could replace the call. Rewrites of whole statements are emitted as text
/// edits in any case.
/// </para>
/// <para>
/// Names in a macro body are resolved where the macro is declared, and the names it declares
/// are local to each expansion, so two problems arise in pasting the expansion into the file.
/// Where the macro is declared in another file, the change is refused, because the names would
/// be resolved again here and could mean something else. Where the body declares anything, the
/// expansion goes in an anonymous <c>.scope</c>, which is how the language keeps a name from
/// being declared twice in one routine.
/// </para>
/// </summary>
internal static class InlineMacro
{
    /// <summary>
    /// Returns the change offered where the caret is on a macro call, or nothing where it is not.
    /// </summary>
    /// <param name="analysis">The program the call is in.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="caret">The caret's position in that file's text.</param>
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
                $"`{macro.Name}!` is declared in {Named(macro.Tree.Path)}: its body's names are looked up in that file, "
                + "and once inlined here they would be looked up in this one, where they may mean something else");
            yield break;
        }
        if (InABody(call))
        {
            yield return Refused(title,
                "the call is inside a macro body, where what its arguments stand for is not known "
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
        // would declare it twice. An anonymous scope is inline code and keeps them apart.
        var scoped = MacroExpansion.Declares(analysis, definition);
        var inner = scoped ? indent + Edits.Indent : indent;
        var inlined = expansion.Lines.Select(line => line.Length == 0 ? "" : inner + line).ToList();
        if (scoped)
            inlined = [indent + ".scope {", .. inlined, indent + "}"];
        yield return new Change(title, CodeActionKinds.Rewrite,
            [Edits.RemoveLines(tree, first, last) with { Text = inlined.Count == 0 ? "" : string.Join("\n", inlined) + "\n" }]);
    }

    /// <summary>
    /// Creates the change as offered when it cannot be applied, with its title and greyed out
    /// with the reason.
    /// </summary>
    private static Change Refused(string title, string why) =>
        new(title, CodeActionKinds.Rewrite, [], Refused: why);

    /// <summary>
    /// Returns the last line the call covers, which is its own line or the end of the last block
    /// argument it opens.
    /// </summary>
    private static int Last(SyntaxTree tree, MacroCallSyntax call, int first)
    {
        var blocks = Macros.BlocksOf(call);
        return blocks.Count == 0 ? first : tree.GetLineIndex(blocks[^1].FullSpan.End - 1);
    }

    /// <summary>
    /// Checks whether a call is inside a macro body, where its arguments are names and not values.
    /// </summary>
    private static bool InABody(SyntaxNode call)
    {
        for (var at = call.Parent; at is not null; at = at.Parent)
        {
            if (at is BlockSyntax { BlockKind: BlockKind.Macro })
                return true;
        }
        return false;
    }

    /// <summary>Returns a file's name without its folders, as a message shows it.</summary>
    private static string Named(string path) => path[(path.LastIndexOf('/') + 1)..];
}
