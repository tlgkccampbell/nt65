using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.charmap name {</c>.</summary>
public sealed class CharmapDeclarationSyntax : TypeDeclarationSyntax
{
    internal CharmapDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
