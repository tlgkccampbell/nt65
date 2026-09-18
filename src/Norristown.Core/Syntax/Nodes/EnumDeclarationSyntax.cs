using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.enum name {</c>.</summary>
public sealed class EnumDeclarationSyntax : TypeDeclarationSyntax
{
    internal EnumDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
