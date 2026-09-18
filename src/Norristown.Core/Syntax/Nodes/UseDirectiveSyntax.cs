using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.use a::b</c>, <c>.use a::{b, c as d}</c>, <c>.use a::*</c> or <c>.use a::b as c</c>.</summary>
public sealed class UseDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<SyntaxToken> path;
    private ImmutableArray<UseItemSyntax> items;

    internal UseDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.use</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The names of the path, outermost first: everything before a <c>::*</c>, a <c>::{</c> or an <c>as</c>.</summary>
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
    public SyntaxToken? StarToken => FirstToken(SyntaxKind.Star);

    /// <summary>The <c>{</c> of <c>a::{b, c}</c>, or null.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);

    /// <summary>The names in the braces of <c>a::{b, c}</c>.</summary>
    public ImmutableArray<UseItemSyntax> Items => Nodes(ref items);

    /// <summary>The <c>}</c> of <c>a::{b, c}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => FirstToken(SyntaxKind.CloseBrace);

    /// <summary>The <c>as</c> of <c>a::b as c</c>, or null.</summary>
    public SyntaxToken? AsKeyword =>
        Path.Length > 0 && TokenAfter(Path[^1]) is { Kind: SyntaxKind.Identifier } word && word.Text.Equals("as", StringComparison.OrdinalIgnoreCase) ? word : null;

    /// <summary>The name the path's last name is brought in as, or null.</summary>
    public SyntaxToken? Alias => TokenAfter(AsKeyword) is { Kind: not SyntaxKind.EndOfLine } alias ? alias : null;

    private ImmutableArray<SyntaxToken> ReadPath()
    {
        var names = ImmutableArray.CreateBuilder<SyntaxToken>();
        var tokens = ChildTokens;
        if (NameAt(1) is not { } first)
            return [];
        names.Add(first);
        for (var i = 2; i + 1 < tokens.Length && tokens[i].Kind == SyntaxKind.ColonColon && NameAt(i + 1) is { } next; i += 2)
            names.Add(next);
        return names.ToImmutable();
    }
}
