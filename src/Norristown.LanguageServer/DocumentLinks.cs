using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Finds the paths in a file that name another file, which are the binaries an <c>.incbin</c>
/// includes. A path is relative to the file that contains it, which is also how the build
/// resolves it, so the file the editor opens is the file the program reads.
/// </summary>
internal static class DocumentLinks
{
    /// <summary>Returns the links in <paramref name="model"/>'s file, in position order.</summary>
    public static IReadOnlyList<Protocol.DocumentLink> In(SemanticModel model)
    {
        var links = new List<Protocol.DocumentLink>();
        foreach (var node in model.Tree.Root.DescendantNodes())
        {
            // Only a literal path is a link. A path that a constant names is a name, and a name
            // already links to where it is declared.
            if (node is not DataDirectiveSyntax data
                || !data.Directive.Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase)
                || data.Tail is not InlineDataSyntax { Values: [StringExpressionSyntax written, ..] }
                || model.ValueOf(written) is not { Kind: ValueKind.String, Text: { Length: > 0 } path })
            {
                continue;
            }
            links.Add(new Protocol.DocumentLink(
                Lsp.ToRange(model.Tree, written.Span),
                Lsp.ToUri(Paths.Beside(model.Tree.Path, path))));
        }
        return links;
    }
}
