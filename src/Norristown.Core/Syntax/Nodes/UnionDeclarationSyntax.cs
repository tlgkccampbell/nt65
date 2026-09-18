using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.union name {</c>.</summary>
public sealed class UnionDeclarationSyntax : TypeDeclarationSyntax
{
    internal UnionDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
