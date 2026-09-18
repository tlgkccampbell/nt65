using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The paths in a file that name another file: the binaries an <c>.incbin</c> includes. A path
/// is relative to the file that writes it, which is how the build resolves it too, so what the
/// editor opens is the file the program reads.
/// </summary>
internal static class DocumentLinks
{
    /// <summary>The links in the file <paramref name="model"/> is of, in position order.</summary>
    public static IReadOnlyList<Protocol.DocumentLink> In(SemanticModel model)
    {
        var links = new List<Protocol.DocumentLink>();
        foreach (var node in model.Tree.Root.DescendantNodes())
        {
            // Only a written path is a link. A path a constant names is a name, and a name is
            // already a link to where it is declared.
            if (node is not { Kind: SyntaxKind.DataDirective, ChildTokens.Length: > 0 }
                || !node.ChildTokens[0].Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase)
                || node.ChildNodes.FirstOrDefault() is not { Kind: SyntaxKind.StringExpression } written
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
