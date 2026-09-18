using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.list name {</c>.</summary>
public sealed class ListDeclarationSyntax : TypeDeclarationSyntax
{
    internal ListDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
