using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Represents one line as the parser reads it, holding its tokens. That is one line of the file,
/// or several that <see cref="Continuations"/> joined because an expression's bracket stayed open
/// across them. Its kind and brace value come from its own tokens alone, so a line is classified
/// without knowing anything about the lines around it.
/// </summary>
internal sealed class GreenLine : GreenNode
{
    /// <summary>The value of <see cref="opensBlockKind"/> before the kind has been found.</summary>
    private const int Unfound = -1;

    private Parser.Result? parsed;

    // The kind of block the line opens, held as an int so that threads that read the line at once
    // never see half of a write. Each thread that finds the kind finds the same one.
    private int opensBlockKind = Unfound;

    internal GreenLine(ImmutableArray<GreenToken> tokens) : this(tokens, default)
    {
    }

    internal GreenLine(ImmutableArray<GreenToken> tokens, ImmutableArray<GreenLine> parts)
        : base(SyntaxKind.Line, SumWidths(tokens))
    {
        Tokens = tokens;
        Parts = parts.IsDefault ? [this] : parts;
        LineKind = Lines.Classify(tokens);
        (Opens, Closes) = Lines.Braces(tokens);

        // A green line holds only its tokens, so its flags cover just the lexer's errors on them.
        // Diagnostics from parsing are held by the statement the parser returns.
        RollUp(tokens);
    }

    /// <summary>Gets the line's tokens, which always end with an <see cref="SyntaxKind.EndOfLine"/> token.</summary>
    public ImmutableArray<GreenToken> Tokens { get; }

    /// <summary>
    /// Gets the lines of the file this line was joined from, in order, or this line alone when it
    /// is one line of the file.
    /// </summary>
    public ImmutableArray<GreenLine> Parts { get; }

    /// <summary>Gets the kind of line this is.</summary>
    public LineKind LineKind { get; }

    /// <summary>
    /// Gets a value indicating whether the line's last token is a <c>{</c> outside any open
    /// parenthesis.
    /// </summary>
    public bool Opens { get; }

    /// <summary>Gets a value indicating whether the line's first token is <c>}</c>.</summary>
    public bool Closes { get; }

    /// <summary>
    /// Gets the kind of block the line opens, or <see cref="BlockKind.None"/> if it opens none. A
    /// <c>.segment NAME</c> region line opens a <see cref="BlockKind.Region"/> block, which has no
    /// brace.
    /// </summary>
    /// <remarks>
    /// The kind of a data block is read from the line's parse, so the kind is found on first
    /// read. The constructor never parses, because the parser would then read a line that is
    /// not yet complete.
    /// </remarks>
    public BlockKind OpensBlockKind
    {
        get
        {
            var found = Volatile.Read(ref opensBlockKind);
            if (found == Unfound)
            {
                found = (int)FindOpensBlockKind();
                Volatile.Write(ref opensBlockKind, found);
            }
            return (BlockKind)found;
        }
    }

    /// <summary>Gets the line's contribution to the block depth, which is +1, −1 or 0.</summary>
    public int BraceValue => (Opens ? 1 : 0) - (Closes ? 1 : 0);

    /// <inheritdoc/>
    public override int SlotCount => Tokens.Length;

    /// <summary>Gets the parse that <see cref="Parse"/> has cached, or null if there is none.</summary>
    internal Parser.Result? CachedParse => parsed;

    /// <summary>Returns the offset of token <paramref name="index"/>'s text from the start of the line.</summary>
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
    /// Returns the line parsed as it stands inside a block of <paramref name="context"/>. The most
    /// recent result is cached, so an edit elsewhere in the file does not re-parse this line,
    /// because a line's enclosing block kind almost never changes. The cache only saves work. A
    /// caller that asks for another context gets a correct result, which replaces the cached one.
    /// </summary>
    internal Parser.Result Parse(BlockKind context)
    {
        var cached = parsed;
        return cached is not null && cached.Context == context ? cached : parsed = Parser.Parse(this, context);
    }

    /// <summary>
    /// Returns the kind of block the line opens, which <see cref="OpensBlockKind"/> gives. Finding
    /// the kind of a data block parses the line. The parser reads a line that opens a block the
    /// same way in any context, so that parse is kept as the one in the block's own kind. That is
    /// the context the block layer asks for, since a data block is never one of the blocks that
    /// take the context of the body around them.
    /// </summary>
    private BlockKind FindOpensBlockKind()
    {
        if (!Opens)
            return Lines.IsRegion(Tokens) ? BlockKind.Region : BlockKind.None;
        var kind = Lines.BlockKindOf(this);
        if (parsed is { Context: BlockKind.None } opener)
            parsed = opener with { Context = kind };
        return kind;
    }
}
