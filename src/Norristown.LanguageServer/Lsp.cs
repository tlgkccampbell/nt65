using System.Collections.Immutable;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Converts analysis results to protocol types, so the analysis stays free of LSP. This class
/// holds all of the 0-based counting, which <see cref="Hovers"/>, <see cref="Rename"/> and the
/// other answers use through <see cref="ToRange(SyntaxTree, TextSpan)"/> and
/// <see cref="ToPosition(SyntaxTree, int)"/>.
/// <para>
/// Protocol types are written out in full, because several of them share a name with
/// something in the analysis core (<c>Diagnostic</c>, <c>SymbolKind</c>) or in the framework
/// (<c>Range</c>).
/// </para>
/// </summary>
internal static class Lsp
{
    /// <summary>The name the client shows as the origin of every diagnostic nt65 reports.</summary>
    private const string SourceName = "nt65";

    /// <summary>
    /// Converts everything wrong with one file to protocol diagnostics, together with the
    /// branches this build leaves out. An omitted branch is not a problem, so it is a hint the
    /// client renders faded rather than anything that appears in a problem list.
    /// </summary>
    public static IReadOnlyList<Protocol.Diagnostic> ToDiagnostics(
        IEnumerable<Diagnostic> diagnostics, SyntaxTree? tree, Configuration configuration) =>
        [
            .. diagnostics.Select(ToDiagnostic),
            .. (tree is null ? [] : configuration.Omitted(tree)).Select(span => new Protocol.Diagnostic(
                ToRange(tree!, span),
                Protocol.DiagnosticSeverity.Hint,
                Catalogue.OmittedBranch.Id,
                SourceName,
                Catalogue.OmittedBranch.Format,
                null,
                [Protocol.DiagnosticTag.Unnecessary])),
        ];

    /// <summary>Returns the file's outline, nested the way its blocks are.</summary>
    public static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree) =>
        ToSymbols(tree, Outline.Build(tree));

    /// <summary>Returns one foldable range per block.</summary>
    public static IReadOnlyList<Protocol.FoldingRange> ToFoldingRanges(SyntaxTree tree) =>
        [.. Folding.Build(tree).Select(range => new Protocol.FoldingRange(range.StartLine, range.EndLine))];

    /// <summary>
    /// Returns where the name at <paramref name="position"/> is declared, or null. The declaration
    /// may be in another file of the program, so the location carries its own URI.
    /// </summary>
    public static Protocol.Location? ToDefinition(ProgramModel program, SemanticModel model, int position) =>
        model.ReferenceAt(position)?.Symbol is { } named && program.Current(named) is var symbol
            ? new Protocol.Location(Uris.ToUri(symbol.Tree.Path), ToRange(symbol.Tree, symbol.NameSpan))
            : null;

    /// <summary>
    /// Returns where the module a <c>.place</c> names is declared, when the caret is on its
    /// path, or null otherwise. A module is not a symbol, so there is no symbol reference there
    /// to answer from.
    /// </summary>
    public static Protocol.Location? ToPlacedDefinition(ProgramAnalysis analysis, SemanticModel model, int position) =>
        PlacedAt(analysis, model, position) is { Tree: var tree } && Placements.Declaration(tree) is { } declared
            ? new Protocol.Location(Uris.ToUri(tree.Path), ToRange(tree, declared.Name.Span))
            : null;

    /// <summary>Returns every place the name at <paramref name="position"/> appears, in every file.</summary>
    public static IReadOnlyList<Protocol.Location> ToReferences(
        ProgramModel program, SemanticModel model, int position, bool includeDeclaration) =>
        model.ReferenceAt(position) is not { } asked
            ? []
            : [.. program.ReferencesTo(asked.Symbol)
                .Where(found => includeDeclaration || !found.Reference.IsDeclaration)
                .Select(found => new Protocol.Location(
                    Uris.ToUri(found.File.Tree.Path), ToRange(found.File.Tree, found.Reference.Span)))];

    /// <summary>
    /// Returns every place in this file where the name at <paramref name="position"/> appears,
    /// for a client to mark while the caret is on one of them.
    /// </summary>
    public static IReadOnlyList<Protocol.DocumentHighlight> ToHighlights(SemanticModel model, int position) =>
        [.. Occurrences(model, position).Select(reference => new Protocol.DocumentHighlight(
            ToRange(model.Tree, reference.Span),
            reference.IsDeclaration ? Protocol.DocumentHighlightKind.Write : Protocol.DocumentHighlightKind.Read))];

    /// <summary>
    /// Returns the edits that format lines <paramref name="first"/> to <paramref name="last"/> in
    /// nt65's layout, one edit per line that changes. Layout is computed over the whole file, but
    /// only edits for these lines are returned, which is what a client formatting a selection
    /// wants.
    /// </summary>
    public static IReadOnlyList<Protocol.TextEdit> ToFormatting(SyntaxTree tree, int first, int last) =>
        [.. Formatter.Changes(tree, first, last).Select(change => new Protocol.TextEdit(
            ToRange(tree, new TextSpan(change.Start, change.Length)), change.NewText))];

    /// <summary>Converts one diagnostic to its protocol form.</summary>
    internal static Protocol.Diagnostic ToDiagnostic(Diagnostic diagnostic) => new(
        ToRange(diagnostic.Span),
        ToSeverity(diagnostic.Severity),
        diagnostic.Id,
        SourceName,
        diagnostic.Message,
        diagnostic.Related.Count == 0
            ? null
            : [.. diagnostic.Related.Select(related => new Protocol.DiagnosticRelatedInformation(
                new Protocol.Location(Uris.ToUri(related.Span.File), ToRange(related.Span)), related.Message))],
        diagnostic.IsUnnecessary ? [Protocol.DiagnosticTag.Unnecessary] : null);

    /// <summary>
    /// Returns the module whose path a <c>.place</c> gives at <paramref name="position"/>, or null
    /// if there is none.
    /// </summary>
    internal static (SyntaxTree Tree, string Path, TextSpan Span)? PlacedAt(
        ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        if (token.Parent?.AncestorsAndSelf().OfType<PlaceDirectiveSyntax>().FirstOrDefault() is not { } place
            || !place.Name.Span.Contains(position) && place.Name.Span.End != position
            || Placements.PathOf(place.Name) is not { } path
            || analysis.Placements.ModuleNamed(path) is not { } tree)
        {
            return null;
        }
        return (tree, path, place.Name.Span);
    }

    /// <summary>Converts a span of a syntax tree to a 0-based range.</summary>
    internal static Protocol.Range ToRange(SyntaxTree tree, TextSpan span) => ToRange(tree.LineStarts, span);

    /// <summary>
    /// Converts a span of a text that has not been parsed, such as a project file, to a 0-based
    /// range. Its lines are split the way a syntax tree splits them.
    /// </summary>
    internal static Protocol.Range ToRange(string text, TextSpan span) => ToRange(SyntaxTree.LineOffsets(text), span);

    /// <summary>Converts a diagnostic span, which is 1-based and on one line, to a 0-based range.</summary>
    internal static Protocol.Range ToRange(Span span) => new(
        new Protocol.Position(span.LineIndex, span.StartColumn - 1),
        new Protocol.Position(span.LineIndex, span.EndColumn - 1));

    /// <summary>Converts an offset into a syntax tree to a 0-based line and column.</summary>
    internal static Protocol.Position ToPosition(SyntaxTree tree, int position) => ToPosition(tree.LineStarts, position);

    /// <summary>
    /// Converts an offset into a text that has not been parsed to a 0-based line and column. Its
    /// lines are split the way a syntax tree splits them.
    /// </summary>
    internal static Protocol.Position ToPosition(string text, int position) =>
        ToPosition(SyntaxTree.LineOffsets(text), position);

    /// <summary>
    /// Returns the first and last lines a selection covers, clamped to the file. A selection that
    /// ends at the start of a line covers only the lines above it, since that is how an editor
    /// represents whole lines selected by dragging down the margin.
    /// </summary>
    internal static (int First, int Last) SelectedLines(SyntaxTree tree, Protocol.Range range)
    {
        var first = Math.Clamp(range.Start.Line, 0, tree.LineCount - 1);
        var last = range.End.Line > first && range.End.Character == 0 ? range.End.Line - 1 : range.End.Line;
        return (first, Math.Clamp(last, first, tree.LineCount - 1));
    }

    /// <summary>Converts the kind of an outline item to the symbol kind the client shows for it.</summary>
    internal static Protocol.SymbolKind ToSymbolKind(OutlineKind kind) => kind switch
    {
        OutlineKind.Proc => Protocol.SymbolKind.Function,
        OutlineKind.Scope => Protocol.SymbolKind.Namespace,
        OutlineKind.Segment => Protocol.SymbolKind.Module,
        OutlineKind.Macro => Protocol.SymbolKind.Function,
        OutlineKind.Constant => Protocol.SymbolKind.Constant,
        OutlineKind.Data => Protocol.SymbolKind.Variable,
        OutlineKind.Type => Protocol.SymbolKind.Struct,
        OutlineKind.Function => Protocol.SymbolKind.Function,
        _ => Protocol.SymbolKind.Field,
    };

    /// <summary>Returns the outline items and their children as protocol symbols.</summary>
    private static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree, IReadOnlyList<OutlineItem> items) =>
        [.. items.Select(item => new Protocol.DocumentSymbol(
            item.Name,
            item.Detail,
            ToSymbolKind(item.Kind),
            ToRange(tree, item.Span),
            ToRange(tree, item.NameSpan),
            item.Children.Count == 0 ? null : ToSymbols(tree, item.Children)))];

    /// <summary>Returns every reference in this file to the symbol at <paramref name="position"/>.</summary>
    private static IReadOnlyList<SymbolReference> Occurrences(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? model.ReferencesTo(reference.Symbol) : [];

    /// <summary>Converts a span, given where each line starts, to a 0-based range.</summary>
    private static Protocol.Range ToRange(ImmutableArray<int> lineStarts, TextSpan span) =>
        new(ToPosition(lineStarts, span.Start), ToPosition(lineStarts, span.End));

    /// <summary>Converts an offset, given where each line starts, to a 0-based line and column.</summary>
    private static Protocol.Position ToPosition(ImmutableArray<int> lineStarts, int position)
    {
        var line = SyntaxTree.GetLineIndex(lineStarts, position);
        return new Protocol.Position(line, position - lineStarts[line]);
    }

    /// <summary>Converts a diagnostic's severity to the protocol's.</summary>
    private static Protocol.DiagnosticSeverity ToSeverity(Severity severity) => severity switch
    {
        Severity.Error => Protocol.DiagnosticSeverity.Error,
        Severity.Warning => Protocol.DiagnosticSeverity.Warning,
        _ => Protocol.DiagnosticSeverity.Information,
    };
}
