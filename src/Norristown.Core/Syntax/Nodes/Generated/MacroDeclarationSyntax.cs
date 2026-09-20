// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.macro name(params): entry -&gt; exit {</c>.</summary>
public sealed class MacroDeclarationSyntax : StatementSyntax
{
    internal MacroDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.macro</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The macro's name, or null.</summary>
    public SyntaxToken? Name => NameAt(1);

    /// <summary>The parameters, or null.</summary>
    public MacroParameterListSyntax? Parameters => FirstNode<MacroParameterListSyntax>();

    /// <summary>The processor state the macro expects and leaves, or null.</summary>
    public ProcSignatureSyntax? Signature => FirstNode<ProcSignatureSyntax>();

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMacroDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMacroDeclaration(this);
}
