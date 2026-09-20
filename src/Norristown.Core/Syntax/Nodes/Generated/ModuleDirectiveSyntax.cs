// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.module name</c> or <c>.module outer::inner</c>.</summary>
public sealed class ModuleDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<SyntaxToken> names;

    internal ModuleDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.module</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The names of the module's path, outermost first.</summary>
    public ImmutableArray<SyntaxToken> Names
    {
        get
        {
            if (names.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref names, [.. ChildTokens.Skip(1).Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)]);
            return names;
        }
    }

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitModuleDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitModuleDirective(this);
}
