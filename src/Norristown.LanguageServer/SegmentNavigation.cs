using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers go-to-definition and find-references for segment names. A segment is not a symbol,
/// so the symbol requests cannot answer for one. A segment may be declared in a source file, in
/// the project file, or in the linked configs, and a file several projects build is answered
/// from each of them, since each may place the segment somewhere else.
/// </summary>
internal static class SegmentNavigation
{
    /// <summary>
    /// Returns the segment named at <paramref name="position"/>, or null when the caret is not on
    /// a segment name. A segment is named by a <c>.segment</c> line, by an import's <c>in</c>, and
    /// by the argument of <c>.loadof</c>, <c>.runof</c> and a <c>.spanof</c> that measures a
    /// segment.
    /// </summary>
    public static string? At(SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        if (token.IsMissing || (!token.Span.Contains(position) && token.Span.End != position))
            return null;
        return Named(model, token);
    }

    /// <summary>
    /// Returns where the segment <paramref name="name"/> is declared in each of
    /// <paramref name="analyses"/>. A segment that linked configs declare is declared by each
    /// config line that places it.
    /// </summary>
    public static IReadOnlyList<Protocol.Location> Definitions(IEnumerable<ProgramAnalysis> analyses, string name) =>
        [.. analyses
            .Select(analysis => analysis.Program.Segments.Find(name))
            .OfType<Segment>()
            .SelectMany(Declarations)
            .Distinct()
            .Select(Location)];

    /// <summary>
    /// Returns every place the segment <paramref name="name"/> is named in each of
    /// <paramref name="analyses"/>. With <paramref name="includeDeclaration"/>, these include its
    /// declarations and the project file's entry that adds to it.
    /// </summary>
    public static IReadOnlyList<Protocol.Location> References(
        IEnumerable<ProgramAnalysis> analyses, string name, bool includeDeclaration)
    {
        var found = new List<Protocol.Location>();
        foreach (var analysis in analyses)
        {
            var segment = analysis.Program.Segments.Find(name);
            if (includeDeclaration && segment is not null)
            {
                found.AddRange(Declarations(segment).Select(Location));
                if (segment.Addition is { } addition)
                    found.Add(Location(addition));
            }
            foreach (var model in analysis.Program.Files)
            {
                foreach (var token in model.Tree.Root.DescendantTokens())
                {
                    if (token.Text != name && token.Text != $"\"{name}\"" || Named(model, token) != name)
                        continue;
                    if (!includeDeclaration && token.Parent is SegmentDeclarationSyntax)
                        continue;
                    found.Add(new Protocol.Location(Uris.ToUri(model.Tree.Path), Lsp.ToRange(model.Tree, token.Span)));
                }
            }
        }
        return [.. found.Distinct()];
    }

    /// <summary>Returns the segment <paramref name="token"/> names, or null when it names none.</summary>
    private static string? Named(SemanticModel model, SyntaxToken token) => token.Parent switch
    {
        SegmentStatementSyntax statement when statement.Name == token => SegmentNames.Of(token),
        ImportItemSyntax import when import.Segment == token => token.Text,
        _ => token.Parent?.AncestorsAndSelf().OfType<CallExpressionSyntax>().FirstOrDefault() is { } call
            && SegmentFunctions.NameIn(call) == token && SegmentFunctions.Of(call, model) is { } about
                ? about.Segment.Name
                : null,
    };

    /// <summary>
    /// Returns where a segment is declared: the config lines that place it, or else its one
    /// declaration. A standard segment that nothing declares has none.
    /// </summary>
    private static IEnumerable<Span> Declarations(Segment segment) =>
        segment.Placements.Count > 0 ? segment.Placements : segment.Declaration is { } declared ? [declared] : [];

    private static Protocol.Location Location(Span span) => new(Uris.ToUri(span.File), Lsp.ToRange(span));
}
