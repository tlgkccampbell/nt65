using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The declarations of every file in the workspace whose names hold what the programmer typed,
/// its letters in order, whatever their case. They are read from each file's outline, so a search
/// analyzes nothing.
/// </summary>
internal static class WorkspaceSymbols
{
    /// <summary>How many a search returns at most, which is more than anyone reads.</summary>
    private const int Most = 500;

    /// <summary>The declarations in <paramref name="files"/> that match <paramref name="query"/>, best matches first.</summary>
    public static IReadOnlyList<Protocol.SymbolInformation> Matching(IEnumerable<SyntaxTree> files, string query)
    {
        var found = new List<(int Score, Protocol.SymbolInformation Symbol)>();
        foreach (var tree in files)
        {
            var module = ModuleOf(tree);
            Collect(tree, Outline.Build(tree), module, query, found);
        }
        return [.. found
            .OrderBy(match => match.Score)
            .ThenBy(match => match.Symbol.Name, StringComparer.Ordinal)
            .Take(Most)
            .Select(match => match.Symbol)];
    }

    /// <summary>
    /// How well <paramref name="name"/> matches: 0 for the same name, 1 for one that starts with the
    /// query, 2 for one that holds it, 3 for one that holds its letters in order; null for no match.
    /// </summary>
    public static int? Score(string name, string query)
    {
        if (query.Length == 0)
            return 3;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        var at = 0;
        foreach (var c in name)
        {
            if (at < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[at]))
                at++;
        }
        return at == query.Length ? 3 : null;
    }

    private static void Collect(
        SyntaxTree tree, IReadOnlyList<OutlineItem> items, string? container, string query,
        List<(int Score, Protocol.SymbolInformation Symbol)> found)
    {
        foreach (var item in items)
        {
            if (item.Kind != OutlineKind.Segment && Score(item.Name, query) is { } score)
            {
                found.Add((score, new Protocol.SymbolInformation(
                    item.Name,
                    Lsp.ToSymbolKind(item.Kind),
                    new Protocol.Location(Lsp.ToUri(tree.Path), Lsp.ToRange(tree, item.NameSpan)),
                    container)));
            }

            // A segment block holds declarations without naming them.
            var inner = item.Kind == OutlineKind.Segment ? container
                : container is null ? item.Name
                : $"{container}::{item.Name}";
            Collect(tree, item.Children, inner, query, found);
        }
    }

    /// <summary>The module a file's <c>.module</c> names, or null.</summary>
    private static string? ModuleOf(SyntaxTree tree)
    {
        foreach (var line in tree.Root.DescendantNodes().OfType<LineSyntax>())
        {
            if (line.Statement is ModuleDirectiveSyntax directive)
                return directive.GetText().Split(';')[0].Trim() is var written && written.IndexOf(' ') is var space and > 0
                    ? written[space..].Trim()
                    : null;
            if (line.Statement is not BlankLineSyntax)
                return null;
        }
        return null;
    }
}
