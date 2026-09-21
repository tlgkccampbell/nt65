using System.Text.Json;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// What hover adds over a macro call, and nothing else: the signature and the comment are the
/// hover every name gets, and what a call is asked about is what it becomes.
/// <para>
/// The row is the answer and leads the grid; the listing under the card is the working, and is
/// there for the short macros where it fits. Past eight lines the listing stops being something
/// a reader takes in at a glance, so what is left is a link to the view that holds it.
/// </para>
/// </summary>
internal static class MacroCallHover
{
    /// <summary>How many lines of an expansion a hover shows before it offers the view instead.</summary>
    private const int Shown = 8;

    /// <summary>
    /// What the call under the caret becomes, as the grid's row says it, or null where the
    /// caret is not on a call.
    /// </summary>
    /// <param name="analysis">The program, for what the call lays out to.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="reference">The name under the caret.</param>
    public static string? Becomes(ProgramAnalysis analysis, SemanticModel model, SymbolReference reference) =>
        At(analysis, model, reference)?.Becomes();

    /// <summary>
    /// <paramref name="card"/> with the expansion written under it, or <paramref name="card"/>
    /// unchanged where the caret is not on a call.
    /// </summary>
    /// <param name="card">The hover as every name gets it.</param>
    /// <param name="analysis">The program, for what the call lays out to.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="reference">The name under the caret.</param>
    public static string Added(
        string card, ProgramAnalysis analysis, SemanticModel model, SymbolReference reference)
    {
        // An expansion the analysis could not write out is not shown: a listing that is nearly
        // right about what a line became is worse than no listing. The row still says what it
        // becomes, because that is worked out from the layout and not from the text.
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

    /// <summary>The call the caret is on, where the name under it is the macro that call names.</summary>
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
    /// The link to the view that holds the whole expansion, saying how much of it the hover
    /// left out. It runs the client's own command, which is where the view lives.
    /// </summary>
    private static string Link(SemanticModel model, int position, int rest)
    {
        var line = model.Tree.GetLineIndex(position);
        var character = position - model.Tree.LineStarts[line];
        var arguments = Uri.EscapeDataString(
            JsonSerializer.Serialize(new object[] { Lsp.ToUri(model.Tree.Path), line, character }));
        return $"[Show expansion](command:nt65.showExpansion?{arguments}) — {rest} more line{(rest == 1 ? "" : "s")}";
    }
}
