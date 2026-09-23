using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Workspace symbol search: the declarations in every file of the workspace whose names contain
/// the letters the programmer typed, in order, ignoring case. They are read from each file's
/// outline, so a search analyzes nothing.
/// </summary>
internal static class WorkspaceSymbols
{
    /// <summary>The most results a search returns, which is already more than anyone reads.</summary>
    private const int Most = 500;

    /// <summary>The declarations in <paramref name="files"/> that match <paramref name="query"/>, best matches first.</summary>
    /// <param name="files">Every file of the workspace, which may be hundreds.</param>
    /// <param name="query">What the programmer typed.</param>
    /// <param name="cancellation">Checked between files, so a search the user has moved on from stops early.</param>
    public static IReadOnlyList<Protocol.SymbolInformation> Matching(
        IEnumerable<SyntaxTree> files, string query, CancellationToken cancellation = default)
    {
        var found = new List<(int Score, Protocol.SymbolInformation Symbol)>();
        foreach (var tree in files)
        {
            cancellation.ThrowIfCancellationRequested();
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
    /// query, 2 for one that contains it, 3 for one that contains its letters in order (and for an
    /// empty query); null for no match.
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

            // A segment block holds declarations but adds nothing to their container name.
            var inner = item.Kind == OutlineKind.Segment ? container
                : container is null ? item.Name
                : $"{container}::{item.Name}";
            Collect(tree, item.Children, inner, query, found);
        }
    }

    /// <summary>The module a file's <c>.module</c> names, or null when its first non-blank line is not a <c>.module</c>.</summary>
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
