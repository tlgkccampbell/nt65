using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// One source line, holding its tokens. Its kind and brace value come from its own tokens
/// alone, so a line lexes and classifies without knowing anything about the lines around it.
/// </summary>
internal sealed class GreenLine : GreenNode
{
    private Parser.Result? parsed;

    internal GreenLine(ImmutableArray<GreenToken> tokens) : base(SyntaxKind.Line, SumWidths(tokens))
    {
        Tokens = tokens;
        LineKind = Lines.Classify(tokens);
        (Opens, Closes) = Lines.Braces(tokens);
        OpensBlockKind = Opens ? Lines.BlockKindOf(tokens, LineKind)
            : Lines.IsRegion(tokens) ? BlockKind.Region
            : BlockKind.None;

        // A green line holds only its tokens, so its flags cover just the lexer's errors on them;
        // diagnostics from parsing are carried by the statement the parser returns.
        RollUp(tokens);
    }

    /// <summary>The line's tokens, always ending with an <see cref="SyntaxKind.EndOfLine"/> token.</summary>
    public ImmutableArray<GreenToken> Tokens { get; }

    /// <summary>What kind of line this is.</summary>
    public LineKind LineKind { get; }

    /// <summary>The line's last token is a <c>{</c> outside any open parenthesis.</summary>
    public bool Opens { get; }

    /// <summary>The line's first token is <c>}</c>.</summary>
    public bool Closes { get; }

    /// <summary>
    /// For a line that opens a block, the kind of block; <see cref="BlockKind.Region"/> for a
    /// <c>.segment NAME</c> region line, which opens one with no brace; otherwise
    /// <see cref="BlockKind.None"/>.
    /// </summary>
    public BlockKind OpensBlockKind { get; }

    /// <summary>+1, −1 or 0: the line's contribution to the block depth.</summary>
    public int BraceValue => (Opens ? 1 : 0) - (Closes ? 1 : 0);

    /// <inheritdoc/>
    public override int SlotCount => Tokens.Length;

    /// <summary>Offset of token <paramref name="index"/>'s text from the start of the line.</summary>
    public int TextOffset(int index)
    {
        var offset = 0;
        for (var i = 0; i < index; i++)
            offset += Tokens[i].FullWidth;
        return offset + Tokens[index].LeadingWidth;
    }

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => Tokens[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new LineSyntax(tree, parent, this, position);

    /// <summary>
    /// The line parsed as it stands inside a block of <paramref name="context"/>. The most
    /// recent result is cached, so an edit elsewhere in the file does not re-parse this line:
    /// a line's enclosing block kind almost never changes. The cache only saves work — a caller
    /// that asks for another context gets a correct result, which replaces the cached one.
    /// </summary>
    internal Parser.Result Parse(BlockKind context)
    {
        var cached = parsed;
        return cached is not null && cached.Context == context ? cached : parsed = Parser.Parse(this, context);
    }
}
