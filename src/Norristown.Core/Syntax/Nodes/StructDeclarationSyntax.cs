using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.struct name {</c>.</summary>
public sealed class StructDeclarationSyntax : TypeDeclarationSyntax
{
    internal StructDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}
