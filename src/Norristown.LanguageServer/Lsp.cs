using System.Text;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The analysis as the protocol spells it. Everything the server sends is built here, so
/// the analysis stays free of LSP and this file holds all of the 0-based counting.
/// <para>
/// Protocol types are written out in full, because several of them share a name with
/// something in the analysis core (<c>Diagnostic</c>, <c>SymbolKind</c>) or in the framework
/// (<c>Range</c>).
/// </para>
/// </summary>
internal static class Lsp
{
    /// <summary>What the client shows as the origin of every diagnostic nt65 reports.</summary>
    private const string SourceName = "nt65";

    /// <summary>
    /// Everything wrong with one file, and the branches this build leaves out. An omitted
    /// branch is not a problem, so it is a hint the client renders faded rather than
    /// anything that appears in a problem list.
    /// </summary>
    public static IReadOnlyList<Protocol.Diagnostic> ToDiagnostics(
        IEnumerable<Diagnostic> diagnostics, SyntaxTree tree, Configuration configuration) =>
        [
            .. diagnostics.Select(ToDiagnostic),
            .. configuration.Omitted(tree).Select(span => new Protocol.Diagnostic(
                ToRange(tree, span),
                Protocol.DiagnosticSeverity.Hint,
                SourceName,
                "the build configuration leaves this branch out",
                null,
                [Protocol.DiagnosticTag.Unnecessary])),
        ];

    /// <summary>The file's outline, nested the way its blocks are.</summary>
    public static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree) =>
        ToSymbols(tree, Outline.Build(tree));

    /// <summary>One foldable range per block.</summary>
    public static IReadOnlyList<Protocol.FoldingRange> ToFoldingRanges(SyntaxTree tree) =>
        [.. Folding.Build(tree).Select(range => new Protocol.FoldingRange(range.StartLine, range.EndLine))];

    /// <summary>What to show about the name at <paramref name="position"/>, or null if there is none.</summary>
    public static Protocol.Hover? ToHover(SemanticModel model, int position)
    {
        if (model.ReferenceAt(position) is not { } reference)
            return null;
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(Describe(reference.Symbol, model.Tree)),
            ToRange(model.Tree, reference.Span));
    }

    /// <summary>
    /// Where the name at <paramref name="position"/> is declared, or null. The declaration
    /// may be in another file of the program, so the location carries its own URI.
    /// </summary>
    public static Protocol.Location? ToDefinition(SemanticModel model, int position) =>
        model.ReferenceAt(position)?.Symbol is { } symbol
            ? new Protocol.Location(ToUri(symbol.Tree.Path), ToRange(symbol.Tree, symbol.NameSpan))
            : null;

    /// <summary>Every place the name at <paramref name="position"/> is written, in every file.</summary>
    public static IReadOnlyList<Protocol.Location> ToReferences(
        ProgramModel program, SemanticModel model, int position, bool includeDeclaration) =>
        [.. Everywhere(program, model, position)
            .Where(found => includeDeclaration || !found.Reference.IsDeclaration)
            .Select(found => new Protocol.Location(
                ToUri(found.File.Tree.Path), ToRange(found.File.Tree, found.Reference.Span)))];

    /// <summary>The same places, for a client to mark while the caret is on one of them.</summary>
    public static IReadOnlyList<Protocol.DocumentHighlight> ToHighlights(SemanticModel model, int position) =>
        [.. Occurrences(model, position).Select(reference => new Protocol.DocumentHighlight(
            ToRange(model.Tree, reference.Span),
            reference.IsDeclaration ? Protocol.DocumentHighlightKind.Write : Protocol.DocumentHighlightKind.Read))];

    /// <summary>The name at <paramref name="position"/>, which is what a rename would replace.</summary>
    public static Protocol.Range? ToRenameRange(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? ToRange(model.Tree, reference.Span) : null;

    /// <summary>
    /// Renaming every occurrence of the name at <paramref name="position"/>, or the reason
    /// it cannot be renamed to <paramref name="newName"/>.
    /// </summary>
    public static (Protocol.WorkspaceEdit? Edit, string? Problem) ToRename(
        ProgramModel program, SemanticModel model, int position, string newName)
    {
        if (model.ReferenceAt(position) is not { } reference)
            return (null, "there is no name here to rename");
        if (CheckNewName(reference.Symbol, newName) is { } problem)
            return (null, problem);

        // An exported name is written in every file that uses it, so the edit spans the
        // program rather than the file the caret is in.
        var edits = new Dictionary<string, IReadOnlyList<Protocol.TextEdit>>(StringComparer.Ordinal);
        foreach (var byFile in Everywhere(program, model, position).GroupBy(found => found.File))
        {
            edits[ToUri(byFile.Key.Tree.Path)] =
                [.. byFile.Select(found => new Protocol.TextEdit(
                    ToRange(byFile.Key.Tree, found.Reference.Span), newName))];
        }
        return (new Protocol.WorkspaceEdit(edits), null);
    }

    /// <summary>
    /// Why <paramref name="newName"/> will not do, or null when it will: a rename
    /// that leaves the file not compiling is not a rename.
    /// </summary>
    private static string? CheckNewName(Symbol symbol, string newName)
    {
        var name = newName;
        if (symbol.IsCheapLocal)
        {
            if (!name.StartsWith('@'))
                return $"`{symbol.DisplayName}` is a cheap local, so its new name must start with `@`";
            name = name[1..];
        }
        else if (name.StartsWith('@'))
        {
            return $"`{symbol.DisplayName}` is not a cheap local, so its new name may not start with `@`";
        }

        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStart(name[0]) || !name.All(SyntaxFacts.IsIdentifierPart))
            return $"`{newName}` is not a name: names are a letter or `_` followed by letters, digits and `_`";
        if (!symbol.IsCheapLocal && (SyntaxFacts.IsMnemonic(name) || SyntaxFacts.IsRegister(name)))
            return $"`{newName}` is a reserved word";

        var taken = symbol.IsCheapLocal ? symbol.Scope.FindCheapLocal(name) : symbol.Scope.FindMember(name);
        return taken is null || taken == symbol ? null : $"`{newName}` is already declared in this scope";
    }

    /// <summary>Everywhere the symbol at <paramref name="position"/> is written in this file.</summary>
    private static IReadOnlyList<SymbolReference> Occurrences(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? model.ReferencesTo(reference.Symbol) : [];

    /// <summary>The same, across every file of the program, in file and source order.</summary>
    private static IEnumerable<(SemanticModel File, SymbolReference Reference)> Everywhere(
        ProgramModel program, SemanticModel model, int position)
    {
        if (model.ReferenceAt(position)?.Symbol is not { } symbol)
            return [];
        return program.Files
            .OrderBy(file => file.Tree.Path, StringComparer.Ordinal)
            .SelectMany(file => file.ReferencesTo(symbol).Select(reference => (file, reference)));
    }

    /// <summary>
    /// What an editor shows about a symbol: what kind it is, what it is worth, how wide an
    /// address it is and, for an address, the segment it sits in.
    /// </summary>
    private static string Describe(Symbol symbol, SyntaxTree asked)
    {
        var text = new StringBuilder($"**{symbol.KindText}** `{symbol.QualifiedName}`\n");

        // A name from another module is worth naming that module for: it is the file the
        // declaration is in, and the file whose `.export` makes it nameable here.
        if (symbol.Tree != asked)
            text.Append($"\n- from: `{symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..]}`");

        // A name no path can reach is shown as it is written, so the routine or scope it is
        // private to is worth saying instead.
        if (!symbol.IsReachableByPath && symbol.Scope.NearestNamed()?.Name is { } owner)
            text.Append($"\n- private to: `{owner}`");
        if (symbol.Kind == SymbolKind.Member)
            text.Append($"\n- offset: `{symbol.Value}`");
        else if (symbol.Value.IsKnown)
            text.Append($"\n- value: `{symbol.Value}`");
        if (symbol.Type is { } type)
            text.Append($"\n- type: `{type.QualifiedName}`");
        if (symbol.Size is { } room)
            text.Append($"\n- size: `{room}` byte{(room == 1 ? "" : "s")}");
        if (symbol.Count is { } count && symbol.Kind != SymbolKind.Member)
            text.Append($"\n- count: `{count}`");
        if (symbol.AddressSize is { } size)
            text.Append($"\n- address size: `{Spell(size)}` ({(int)size} byte{((int)size == 1 ? "" : "s")})");
        if (symbol.IsAddress && symbol.Segment is { } segment)
            text.Append($"\n- segment: `{segment}`");
        return text.ToString();
    }

    /// <summary>An address size as the language writes it.</summary>
    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    private static Protocol.Diagnostic ToDiagnostic(Diagnostic diagnostic) => new(
        ToRange(diagnostic.Span),
        ToSeverity(diagnostic.Severity),
        SourceName,
        diagnostic.Message,
        diagnostic.Related.Count == 0
            ? null
            : [.. diagnostic.Related.Select(related => new Protocol.DiagnosticRelatedInformation(
                new Protocol.Location(ToUri(related.Span.File), ToRange(related.Span)), related.Message))]);

    private static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree, IReadOnlyList<OutlineItem> items) =>
        [.. items.Select(item => new Protocol.DocumentSymbol(
            item.Name,
            item.Detail,
            ToSymbolKind(item.Kind),
            ToRange(tree, item.Span),
            ToRange(tree, item.NameSpan),
            item.Children.Count == 0 ? null : ToSymbols(tree, item.Children)))];

    /// <summary>A diagnostic span, which is 1-based and on one line, as a 0-based range.</summary>
    private static Protocol.Range ToRange(Span span) => new(
        new Protocol.Position(span.Line - 1, span.StartColumn - 1),
        new Protocol.Position(span.Line - 1, span.EndColumn - 1));

    private static Protocol.Range ToRange(SyntaxTree tree, TextSpan span) =>
        new(ToPosition(tree, span.Start), ToPosition(tree, span.End));

    private static Protocol.Position ToPosition(SyntaxTree tree, int position)
    {
        var line = tree.GetLineIndex(position);
        return new Protocol.Position(line, position - tree.LineStarts[line]);
    }

    private static Protocol.DiagnosticSeverity ToSeverity(Severity severity) => severity switch
    {
        Severity.Error => Protocol.DiagnosticSeverity.Error,
        Severity.Warning => Protocol.DiagnosticSeverity.Warning,
        _ => Protocol.DiagnosticSeverity.Information,
    };

    private static Protocol.SymbolKind ToSymbolKind(OutlineKind kind) => kind switch
    {
        OutlineKind.Proc => Protocol.SymbolKind.Function,
        OutlineKind.Scope => Protocol.SymbolKind.Namespace,
        OutlineKind.Segment => Protocol.SymbolKind.Module,
        OutlineKind.Constant => Protocol.SymbolKind.Constant,
        _ => Protocol.SymbolKind.Field,
    };

    /// <summary>A logical path back as a URI, for a diagnostic that points into another file.</summary>
    private static string ToUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : path;
}
