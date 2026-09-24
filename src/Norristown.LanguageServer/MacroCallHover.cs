using System.Text.Json;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Builds the part of a hover that is specific to a macro call. The signature and the doc comment
/// come from the hover every name gets, and this class adds what the call expands to.
/// <para>
/// The <c>expands to</c> row is the answer and is among the leading rows of the hover's grid.
/// The listing of the expansion goes below the rest of the hover as supporting detail, and is
/// shown in full only for short macros. Past eight lines a listing can no longer be taken in
/// at a glance, so the rest is replaced by a link to the expansion view.
/// </para>
/// </summary>
internal static class MacroCallHover
{
    /// <summary>
    /// The number of lines of an expansion a hover shows before it offers the view instead.
    /// </summary>
    private const int Shown = 8;

    /// <summary>
    /// Returns what the call under the caret expands to, as the grid's row shows it, or null
    /// where the caret is not on a call.
    /// </summary>
    /// <param name="analysis">The program, for the bytes and cycles the call assembles to.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="reference">The name under the caret.</param>
    public static string? Becomes(ProgramAnalysis analysis, SemanticModel model, SymbolReference reference) =>
        At(analysis, model, reference)?.Becomes();

    /// <summary>
    /// Returns <paramref name="card"/> with the expansion listed under it, or
    /// <paramref name="card"/> unchanged where the caret is not on a call.
    /// </summary>
    /// <param name="card">The hover text as every name gets it.</param>
    /// <param name="analysis">The program, for the bytes and cycles the call assembles to.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="reference">The name under the caret.</param>
    public static string Added(
        string card, ProgramAnalysis analysis, SemanticModel model, SymbolReference reference)
    {
        // An expansion the analysis could not produce in full is not shown, because a listing
        // that is nearly right about what a line became is worse than no listing. The summary row is still
        // shown, because it comes from the layout and not from the expansion text.
        if (At(analysis, model, reference) is not { Refusal: null, Lines.Count: > 0 } expansion)
            return card;
        var zones = new List<string>
        {
            card,
            $"```nt65\n{string.Join("\n", expansion.Lines.Take(Shown))}\n```",
        };
        if (expansion.Lines.Count > Shown)
            zones.Add(Link(model, reference.Span.Start, expansion.Lines.Count - Shown));
        return string.Join("\n---\n", zones);
    }

    /// <summary>
    /// Returns the expansion of the call the caret is on, or null unless the name under the caret
    /// is the call's macro name.
    /// </summary>
    private static MacroExpansion? At(
        ProgramAnalysis analysis, SemanticModel model, SymbolReference reference)
    {
        if (reference is not { IsDeclaration: false, Symbol.Kind: SymbolKind.Macro })
            return null;
        if (MacroExpansion.CallAt(model, reference.Span.Start) is not { } call)
            return null;
        return Macros.CalleeOf(call) is { } callee && callee.Span.Start == reference.Span.Start
            ? MacroExpansion.Of(analysis, model, call)
            : null;
    }

    /// <summary>
    /// Builds the link to the view that shows the whole expansion, with a note of how many lines
    /// the hover left out. It runs a command the client provides, since the view is implemented in
    /// the client.
    /// </summary>
    private static string Link(SemanticModel model, int position, int rest)
    {
        var line = model.Tree.GetLineIndex(position);
        var character = position - model.Tree.LineStarts[line];
        var arguments = Uri.EscapeDataString(
            JsonSerializer.Serialize(new object[] { Uris.ToUri(model.Tree.Path), line, character }));
        return $"[Show expansion](command:nt65.showExpansion?{arguments}) — {rest} more line{(rest == 1 ? "" : "s")}";
    }
}
