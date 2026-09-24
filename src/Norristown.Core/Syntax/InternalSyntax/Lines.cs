using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

internal static class Lines
{
    public static LineKind Classify(ImmutableArray<GreenToken> tokens)
    {
        var first = tokens[0].Kind;
        var second = tokens.Length > 1 ? tokens[1].Kind : SyntaxKind.EndOfLine;
        switch (first)
        {
            case SyntaxKind.EndOfLine:
                return LineKind.Blank;
            case SyntaxKind.Directive:
                return LineKind.Directive;
            case SyntaxKind.CloseBrace:
                return LineKind.BlockClose;
        }

        // A register or mnemonic in label or constant position still makes a label or a
        // constant: struct members and initializer values may use those names, and elsewhere
        // the reserved-word error is clearer than an unrecognized line.
        if (first is SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Register or SyntaxKind.Mnemonic)
        {
            if (second == SyntaxKind.Colon)
                return LineKind.Label;
            if (second is SyntaxKind.Equals or SyntaxKind.QuestionEquals)
                return LineKind.Constant;
        }
        return first switch
        {
            _ when IsMacroCall(tokens, 0) => LineKind.MacroCall,
            SyntaxKind.Identifier when second == SyntaxKind.EndOfLine => LineKind.BareIdentifier,
            SyntaxKind.Mnemonic => LineKind.Instruction,
            _ => LineKind.Expression,
        };
    }

    /// <summary>
    /// Returns the index of the token where one of ca65's unnamed labels appears on the line, or
    /// -1 if none does. An unnamed label is defined by a <c>:</c> at the start of a line and
    /// referred to by <c>:+</c> or <c>:-</c> in an operand. A <c>:</c> right after a name is a
    /// label or an address-size prefix instead, so <c>z:foo</c> and <c>a:-1</c> keep those
    /// meanings.
    /// </summary>
    public static int UnnamedLabel(ImmutableArray<GreenToken> tokens)
    {
        if (tokens[0].Kind == SyntaxKind.Colon)
            return 0;
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Kind == SyntaxKind.Colon
                && tokens[i + 1].Kind is SyntaxKind.Plus or SyntaxKind.Minus
                && tokens[i - 1].Kind is not (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.CheapLocal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns whether the line opens a block, by ending with <c>{</c>, and whether it closes one,
    /// by starting with <c>}</c>. A <c>{</c> inside a parenthesis still open on the line does not
    /// open a block, so a half-typed <c>m!({</c> does not swallow the lines after it.
    /// </summary>
    public static (bool Opens, bool Closes) Braces(ImmutableArray<GreenToken> tokens)
    {
        var closes = tokens[0].Kind == SyntaxKind.CloseBrace;
        var last = tokens.Length - 2; // before EndOfLine
        if (last < 0 || tokens[last].Kind != SyntaxKind.OpenBrace)
            return (false, closes);
        var depth = 0;
        for (var i = 0; i < last; i++)
        {
            if (tokens[i].Kind == SyntaxKind.OpenParen)
                depth++;
            else if (tokens[i].Kind == SyntaxKind.CloseParen && depth > 0)
                depth--;
        }
        return (depth == 0, closes);
    }

    /// <summary>
    /// Returns a value indicating whether the line is a <c>.segment NAME</c> region line that
    /// opens no braced block. Such a line puts the lines after it in the segment, rather than
    /// lines inside braces.
    /// </summary>
    public static bool IsRegion(ImmutableArray<GreenToken> tokens) =>
        tokens[0].DirectiveKind == DirectiveKind.Segment && SegmentForm(tokens, opens: false) == SyntaxKind.SegmentRegion;

    /// <summary>
    /// Returns the kind of node a <c>.segment</c> line parses to. A line that opens a block with
    /// <c>{</c> is a <see cref="SyntaxKind.SegmentBlock"/>. Otherwise a line with a <c>:</c> is a
    /// <see cref="SyntaxKind.SegmentDeclaration"/>, and any other line is a
    /// <see cref="SyntaxKind.SegmentRegion"/>, even one with a stray <c>{</c> the parser reports.
    /// </summary>
    /// <param name="tokens">The line's tokens.</param>
    /// <param name="opens">Whether the line opens a block, as <see cref="Braces"/> decides.</param>
    public static SyntaxKind SegmentForm(ImmutableArray<GreenToken> tokens, bool opens)
    {
        if (opens)
            return SyntaxKind.SegmentBlock;
        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxKind.Colon)
                return SyntaxKind.SegmentDeclaration;
        }
        return SyntaxKind.SegmentRegion;
    }

    /// <summary>
    /// Returns a value indicating whether the token at <paramref name="at"/> names a macro being
    /// called, as in <c>name!(...)</c> or <c>name!</c>. A macro may be named after an instruction,
    /// such as one another processor has. Then only <c>!(</c> after it makes a call, because
    /// <c>lda !flag</c> is an instruction whose operand is the logical not of <c>flag</c>.
    /// </summary>
    public static bool IsMacroCall(ImmutableArray<GreenToken> tokens, int at)
    {
        if (at + 1 >= tokens.Length || tokens[at + 1].Kind != SyntaxKind.Bang)
            return false;
        return tokens[at].Kind switch
        {
            SyntaxKind.Identifier => true,
            SyntaxKind.Mnemonic => at + 2 < tokens.Length && tokens[at + 2].Kind == SyntaxKind.OpenParen,
            _ => false,
        };
    }

    /// <summary>
    /// Returns a value indicating whether a token of <paramref name="kind"/> may be a declared
    /// name. Register names and mnemonics are reserved, but that rule belongs to name binding
    /// rather than to reading a line, so <c>.proc a</c> parses, and is reported where every other
    /// reserved-word use is.
    /// </summary>
    public static bool IsName(SyntaxKind kind) =>
        kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic;

    /// <summary>
    /// Returns a value indicating whether the token at <paramref name="at"/>, just after
    /// <c>.export</c>, starts a declaration that <c>.export</c> exports, such as the
    /// <c>.proc</c> of <c>.export .proc init {</c>.
    /// </summary>
    public static bool IsExportedDeclaration(ImmutableArray<GreenToken> tokens, int at) =>
        tokens[at].Kind == SyntaxKind.Directive && SyntaxFacts.IsExportable(tokens[at].DirectiveKind);

    /// <summary>
    /// Returns a value indicating whether the tokens at <paramref name="at"/>, just after the
    /// <c>}</c> that starts a line, name the next block argument of a macro call, as the
    /// <c>else {</c> of <c>} else {</c> does.
    /// </summary>
    public static bool IsNextBlockArgument(ImmutableArray<GreenToken> tokens, int at) =>
        IsName(tokens[at].Kind) && at + 1 < tokens.Length && tokens[at + 1].Kind == SyntaxKind.OpenBrace;

    /// <summary>
    /// Returns the kind of block <paramref name="line"/> opens. The line must end in a <c>{</c>
    /// that opens a block. The kind of a data block is read from the line's parse, so the block
    /// holds what the parser read the brace as opening.
    /// </summary>
    public static BlockKind BlockKindOf(GreenLine line)
    {
        var tokens = line.Tokens;
        var kind = line.LineKind;
        // The statement that opens the block: after a label, or after the } of a continuation.
        var start = kind switch
        {
            LineKind.Label => 2,
            LineKind.BlockClose => 1,
            _ => 0,
        };

        // `.export .proc init {` opens the same kind of block as the declaration after `.export`.
        if (kind == LineKind.Directive && tokens[0].DirectiveKind == DirectiveKind.Export
            && IsExportedDeclaration(tokens, 1))
        {
            start = 1;
        }
        var token = tokens[start];
        if (token.Kind == SyntaxKind.Directive)
        {
            var data = token.DirectiveKind == DirectiveKind.Data || SyntaxFacts.IsElementType(token.DirectiveKind);
            return (data ? DataBlockKind(line.Parse(BlockKind.None)) : null) ?? SyntaxFacts.BlockKindOf(token.DirectiveKind);
        }
        if (IsMacroCall(tokens, start))
            return BlockKind.MacroBlock;
        if (kind == LineKind.BlockClose && IsNextBlockArgument(tokens, start))
            return BlockKind.MacroBlock;
        return BlockKind.Unknown;
    }

    /// <summary>
    /// Returns the kind of block a data line opens, as its parse shows, or null when the parser
    /// did not read the line's <c>{</c> as opening data. <c>.data name {</c> is mixed data. An
    /// element type followed by <c>{</c> opens a body, whose kind
    /// <see cref="SyntaxFacts.DataBodyKind"/> gives.
    /// </summary>
    private static BlockKind? DataBlockKind(Parser.Result parsed)
    {
        var statement = parsed.Node is LabeledLineSyntax labeled ? labeled.Statement : parsed.Node;
        return statement switch
        {
            DataDeclarationSyntax { OpenBraceToken: not null } => BlockKind.Data,
            DataDeclarationSyntax { Directive: { } element } => BodyKind(element),
            DataDirectiveSyntax directive => BodyKind(directive),
            _ => null,
        };

        static BlockKind? BodyKind(DataDirectiveSyntax directive) =>
            directive.Tail is DataBodySyntax
                ? SyntaxFacts.DataBodyKind(directive.Directive.DirectiveKind, directive.Count is not null)
                : null;
    }
}
