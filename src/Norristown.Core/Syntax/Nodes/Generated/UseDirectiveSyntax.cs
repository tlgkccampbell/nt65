// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or <c>.use a::b as c</c>.</summary>
public sealed partial class UseDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<SyntaxToken> path;
    private ImmutableArray<UseItemSyntax> items;

    internal UseDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.use</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The path, outermost name first: everything before a <c>::*</c>, a <c>::{</c> or an <c>as</c>.</summary>
    public ImmutableArray<SyntaxToken> Path
    {
        get
        {
            if (path.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref path, ReadPath());
            return path;
        }
    }

    /// <summary>The <c>*</c> of <c>a::*</c>, or null.</summary>
    public SyntaxToken? StarToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Star) : SlotTokenOrNull(3);

    /// <summary>The <c>{</c> of <c>a::{b, c}</c>, or null.</summary>
    public SyntaxToken? OpenBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotTokenOrNull(4);

    /// <summary>The names in the braces of <c>a::{b, c}</c>.</summary>
    public ImmutableArray<UseItemSyntax> Items => Nodes(ref items);

    /// <summary>The <c>}</c> of <c>a::{b, c}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBrace) : SlotTokenOrNull(6);

    /// <summary>The <c>as</c> of <c>a::b as c</c>, or null.</summary>
    public SyntaxToken? AsKeyword =>
        Green is GreenSyntax ? Path.Length > 0 && TokenAfter(Path[^1]) is { Kind: SyntaxKind.Identifier } word && word.Text.Equals("as", StringComparison.OrdinalIgnoreCase) ? word : null : SlotTokenOrNull(7);

    /// <summary>The name the path's last name is brought in as, or null.</summary>
    public SyntaxToken? Alias => Green is GreenSyntax ? TokenAfter(AsKeyword) : SlotTokenOrNull(8);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitUseDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitUseDirective(this);
}
