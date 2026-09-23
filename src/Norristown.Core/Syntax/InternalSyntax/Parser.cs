using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Parses one line's tokens into a statement. A line's syntax depends only on its own tokens
/// and the kind of block around it, so a line parses without looking at any other
/// line and its statement survives an edit anywhere else in the file.
/// <para>
/// The parser never gives up on a line: whatever it cannot read becomes a
/// <see cref="SyntaxKind.SkippedTokens"/> node with a diagnostic, which the line holds alongside
/// its line break and any <c>.export</c> before a declaration. A statement therefore contains
/// only its own tokens, wherever it is written. A node's shape does not depend on what the source
/// wrote either: a required piece the source does not write fills its slot as a missing token,
/// with no width and no text, so a required property is never null, and code reading the tree
/// checks <see cref="GreenNode.IsMissing"/> where it cares. An optional part of the line that the
/// source leaves out entirely — a signature, an operand, an <c>as</c> — is null, and null never
/// means anything else.
/// </para>
/// <para>
/// A diagnostic covers the token the parser is looking at. If that is the end of the line —
/// nothing is written there, because the piece the line wants was never typed — the diagnostic
/// goes at the end of the last token the source does have, before the whitespace and comment
/// after it: that is where the missing piece belongs, and a caret there neither drifts right as
/// trailing spaces are typed nor lands past a trailing comment. This is the only placement rule,
/// and <see cref="Caret"/> implements it. A diagnostic about a missing piece is carried by the
/// missing token in its slot, reaching back over the trivia in between with a negative offset
/// where necessary; any other diagnostic is reported over its token and attached to the
/// innermost node the parser finishes that contains it.
/// </para>
/// <para>
/// The class is split into one file per area — declarations, data, directives, macros,
/// signatures, operands, expressions — and this file holds what every area uses to read a line:
/// the tokens, where a diagnostic goes, and the dispatch that picks the area.
/// </para>
/// </summary>
internal sealed partial class Parser
{
    /// <summary>The loosest binding level, which <see cref="ParseExpression"/> starts at.</summary>
    private const int LowestPrecedence = 13;

    /// <summary>The tightest binding level that is still a binary operator.</summary>
    private const int TightestPrecedence = 3;

    /// <summary>
    /// How deeply expressions may nest inside one another. Each level uses about a dozen stack
    /// frames, and a stack overflow kills the whole process, so the parser stops reading an
    /// absurdly nested line rather than crashing.
    /// </summary>
    private const int MaximumNesting = 100;

    private readonly ImmutableArray<GreenToken> tokens;
    private readonly BlockKind context;
    private readonly bool opensBlock;

    // The diagnostics reported over a token, positioned within the line and waiting to be
    // attached to the node that contains them; and the total number of diagnostics the line has
    // been given, missing tokens' included, which is how ReportOnce and Expect tell whether
    // anything has been reported on this line yet.
    private readonly List<Pending> pending = [];
    private int reported;
    private int index;

    // The line's own pieces, which no statement holds: the `.export` that exports what the line
    // declares, and the tokens the statement could not take.
    private GreenToken? exportKeyword;
    private GreenNode? skippedTokens;

    // Whether an operand is being read inside the braces of a macro argument, where `}` ends
    // it as the end of a line does elsewhere.
    private bool braced;

    // How many expressions the parser is inside, which is what MaximumNesting bounds.
    private int nesting;

    // Where each token of the line starts, for placing a diagnostic; see FullStart.
    private int[]? starts;

    private Parser(GreenLine line, BlockKind context)
    {
        tokens = line.Tokens;
        this.context = context;
        opensBlock = line.Opens;
    }

    private GreenToken Current => tokens[index];

    private SyntaxKind Kind => tokens[index].Kind;

    private SyntaxKind Next => index + 1 < tokens.Length ? tokens[index + 1].Kind : SyntaxKind.EndOfLine;

    private bool AtEnd => Kind == SyntaxKind.EndOfLine;

    /// <summary>Whether an operand has run out: the end of the line, or the <c>}</c> around it.</summary>
    private bool AtOperandEnd => AtEnd || (braced && Kind == SyntaxKind.CloseBrace);

    /// <summary>
    /// Whether a declaration could write its name here. Register names and mnemonics are
    /// reserved, but that rule belongs to name binding rather than to reading a line:
    /// <c>.proc a</c> parses, and is reported where every other reserved-word use is.
    /// </summary>
    private bool AtName => Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic;

    /// <summary>
    /// Whether the current token is a contextual word such as <c>dp</c> or <c>proc</c>.
    /// Those are matched without regard to case, as every other word the language fixes is.
    /// </summary>
    private bool AtWord(string word) =>
        Kind == SyntaxKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <paramref name="line"/> as it stands inside a block of <paramref name="context"/>.</summary>
    public static Result Parse(GreenLine line, BlockKind context)
    {
        var parser = new Parser(line, context);
        var node = parser.ParseLine(line.LineKind);
        parser.Settle(node);
        return new Result(context, parser.exportKeyword, node, parser.skippedTokens);
    }

    /// <summary>
    /// Returns the current token and moves past it; the position stays on the end-of-line token
    /// once it is reached.
    /// </summary>
    private GreenToken Advance()
    {
        var token = tokens[index];
        if (index < tokens.Length - 1)
            index++;
        return token;
    }

    /// <summary>Reports over the current token, with the change the message names as its fix.</summary>
    private void Report(DiagnosticMessage message, DiagnosticFix? fix = null) => Report(index, message, fix);

    /// <summary>
    /// Reports only when nothing has been said about this line yet. A half-typed
    /// <c>m!({</c> runs out of tokens inside an argument, inside the braces and inside the
    /// parentheses; the first report says what is missing, and the others would only repeat it.
    /// </summary>
    private void ReportOnce(DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        if (reported == 0)
            Report(index, message, fix);
    }

    /// <summary>
    /// An error expression to use in place of one nested deeper than
    /// <see cref="MaximumNesting"/>, or null while there is room for one more level. The rest of
    /// the line is not parsed: the line keeps it as skipped tokens, so its text is still
    /// preserved, and the only thing reported about it is that it nests too deeply.
    /// </summary>
    private ExpressionSyntax? TooDeeplyNested()
    {
        if (nesting <= MaximumNesting)
            return null;
        ReportOnce(Catalogue.NestingTooDeep.Says(MaximumNesting));
        return new ErrorExpressionSyntax(null);
    }

    /// <summary>Reports over the token at <paramref name="token"/>, wherever it sits in the line.</summary>
    private void Report(int token, DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        var (start, width) = Caret(token);
        pending.Add(new Pending(start, width, message, fix));
        reported++;
    }

    /// <summary>
    /// Attaches to <paramref name="node"/> the pending diagnostics that fall within its text. The
    /// parser has just finished reading the node, so it ends at the parser's current position;
    /// a pending diagnostic inside that range is about this node, since no inner node claimed it.
    /// </summary>
    private T Own<T>(T node) where T : GreenNode
    {
        // Nearly every line reports nothing, and working out where a node starts costs a walk of
        // the tokens before it, so a line with nothing to place does not pay for one.
        if (pending.Count == 0)
            return node;
        var start = FullStart(index) - node.FullWidth;
        var end = start + node.FullWidth;
        for (var i = 0; i < pending.Count;)
        {
            var held = pending[i];
            if (held.Start < start || held.Start + held.Width > end)
            {
                i++;
                continue;
            }
            node.Report(new GreenDiagnostic(held.Start - start, held.Width, held.Message, held.Fix));
            pending.RemoveAt(i);
        }
        return node;
    }

    /// <summary>
    /// Attaches to the statement every diagnostic no node claimed: one over a token of the
    /// statement that no inner node contains, and the rare one about a place outside the statement.
    /// </summary>
    private void Settle(GreenNode statement)
    {
        if (pending.Count == 0)
            return;
        var start = exportKeyword?.FullWidth ?? 0;
        foreach (var held in pending)
            statement.Report(new GreenDiagnostic(held.Start - start, held.Width, held.Message, held.Fix));
        pending.Clear();
    }

    /// <summary>
    /// Where a diagnostic about the token at <paramref name="at"/> goes, as an offset from the
    /// start of the line and a width: over the token, or, if the line has run out, a zero-width
    /// caret at the end of the last token it does have, which is where the missing piece belongs.
    /// </summary>
    private (int Start, int Width) Caret(int at)
    {
        var token = tokens[at];
        if (token.Kind != SyntaxKind.EndOfLine)
            return (FullStart(at) + token.LeadingWidth, token.Text.Length);
        return at > 0
            ? (FullStart(at) - tokens[at - 1].TrailingWidth, 0)
            : (FullStart(at) + token.LeadingWidth, 0);
    }

    /// <summary>Where token <paramref name="at"/> starts in the line, its leading trivia included.</summary>
    private int FullStart(int at)
    {
        // Worked out for the whole line the first time a diagnostic wants one, and kept: a line
        // with nothing to say never pays for it, and one with several things to say pays once
        // rather than walking the tokens before each of them.
        if (starts is null)
        {
            starts = new int[tokens.Length + 1];
            for (var i = 0; i < tokens.Length; i++)
                starts[i + 1] = starts[i] + tokens[i].FullWidth;
        }
        return starts[at];
    }

    private static string Describe(GreenToken token) => $"`{token.Text}`";

    private GreenNode ParseLine(LineKind kind)
    {
        if (kind == LineKind.BlockClose)
            return ParseBlockClose();
        if (kind == LineKind.Blank)
            return Finish(new BlankLineSyntax());

        // An unnamed label must be replaced by a named one, not merely respelled, so it is the
        // only thing reported about its line: a line with `:` where a name belongs or `:+` in an
        // operand is parsed no further, since anything else wrong with it is the same mistake.
        if (Lines.UnnamedLabel(tokens) is >= 0 and var colon)
        {
            Report(colon, Catalogue.UnnamedLabel);
            return Own(new ErrorLineSyntax(TakeRest()));
        }

        // Blocks whose lines follow a grammar of their own. A struct or union member
        // is written like a labelled data declaration and needs no rule of its own.
        //
        // The line that opens a block belongs to that block, so it arrives here with the block's
        // own kind as its context; it is parsed as an opener, and only the lines after it are
        // parsed as members.
        switch (opensBlock ? BlockKind.None : context)
        {
            case BlockKind.Enum:
                return ParseEnumMember();
            case BlockKind.Charmap:
                return ParseCharmapEntry();
            case BlockKind.List:
                return Finish(new ListItemsSyntax(ParseSeparatedList(ParseExpression)));
            case BlockKind.RecordInitializer:
                return ParseMemberValueLine();
            case BlockKind.DataBody:
                return ParseDataValuesLine();
            default:
                break;
        }

        return kind switch
        {
            LineKind.Label => ParseLabeledLine(),
            LineKind.Constant => ParseConstantDeclaration(),
            LineKind.Instruction => Finish(ParseInstruction()),
            LineKind.Directive => ParseDirectiveLine(),
            LineKind.MacroCall => Finish(ParseMacroCall()),

            // A name on its own splices a `block` parameter. Which blocks may hold one is not
            // a question about this line — a splice inside an `.if` inside a body is still a
            // splice — so it is read as one everywhere and the binder says where it belongs.
            LineKind.BareIdentifier => Finish(new BlockSpliceSyntax(Advance())),
            _ => ErrorLine(Catalogue.ExpectedStatement.Says("a label, a constant, an instruction or a directive")),
        };
    }

    /// <summary>Returns the statement, after moving whatever is left on the line into its skipped tokens.</summary>
    private GreenNode Finish(GreenNode statement)
    {
        SkipRest();
        return statement;
    }

    /// <summary>
    /// Anything the statement could not take, kept for the line as
    /// <see cref="SyntaxKind.SkippedTokens"/>.
    /// </summary>
    private void SkipRest()
    {
        if (AtEnd)
            return;

        // One diagnostic per line is enough: where the parser has already said what it
        // wanted, the tokens it then walks past are the same problem said twice.
        //
        // A block written on one line, `.data name { .byte 1 }`, gets its own message: the
        // `{` was read as a block opener, and what follows it is the block's body written on
        // the wrong line, rather than an unexpected token.
        if (reported == 0)
        {
            Report(!opensBlock && index > 0 && tokens[index - 1].Kind == SyntaxKind.OpenBrace
                ? Catalogue.BlockBraceEndsTheLine.Says(Describe(Current))
                : Catalogue.UnexpectedToken.Says(Describe(Current)));
        }
        skippedTokens = Own(new SkippedTokensSyntax(TakeRest()));
    }

    private GreenNode ErrorLine(DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        Report(index, message, fix);
        return Own(new ErrorLineSyntax(TakeRest()));
    }

    /// <summary>
    /// Every token up to, but not including, the end-of-line token, as one list; null when
    /// there are none, since an empty list is stored as null.
    /// </summary>
    private GreenList? TakeRest()
    {
        var rest = new GreenListBuilder();
        while (!AtEnd)
            rest.Add(Advance());
        return rest.ToList();
    }

    private GreenNode ParseBlockClose()
    {
        var brace = Advance();
        if (AtEnd)
            return Finish(new BlockCloseLineSyntax(brace));

        // `} .elseif expr {` and `} .else {` close one branch and open the next; a macro
        // call's `} name {` closes one block argument and opens the next.
        if (AtName && Next == SyntaxKind.OpenBrace)
            return Finish(new BlockContinuationSyntax(brace, Advance(), Advance()));

        return SyntaxFacts.LineDirectiveKind(Current.Text) switch
        {
            SyntaxKind.ElseIfDirective => Finish(ParseIf(brace)),
            SyntaxKind.ElseDirective => Finish(ParseElse(brace)),
            _ => Finish(new BlockCloseLineSyntax(brace)),
        };
    }

    /// <summary>
    /// The current token if it is of <paramref name="kind"/>; otherwise a missing token that
    /// reports nothing, for a slot where some other problem on the line has already been
    /// reported.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind) => Kind == kind ? Advance() : GreenToken.Missing(kind);

    /// <summary>
    /// Like <see cref="Expect(SyntaxKind)"/>, but the missing token carries
    /// <paramref name="message"/> if nothing has been reported on this line yet.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind, DiagnosticMessage message)
    {
        if (Kind == kind)
            return Advance();
        return reported == 0 ? Missing(kind, message) : GreenToken.Missing(kind);
    }

    /// <summary>
    /// The name written here, or a missing identifier carrying <paramref name="message"/>. A name
    /// may be lexed as an identifier, a register or a mnemonic, so it is not a single token kind
    /// that <see cref="Expect(SyntaxKind, DiagnosticMessage)"/> could check for.
    /// </summary>
    private GreenToken ExpectName(DiagnosticMessage message) =>
        AtName ? Advance() : Missing(SyntaxKind.Identifier, message);

    /// <summary>
    /// A missing token of <paramref name="kind"/> carrying <paramref name="message"/>, whether or
    /// not anything has been reported on the line already. It is used instead of
    /// <see cref="Expect(SyntaxKind, DiagnosticMessage)"/> where a second missing piece on a line
    /// deserves a diagnostic of its own.
    /// <para>
    /// The token sits after the trivia that follows the previous token, but the caret belongs
    /// where that previous token's text ends, so the diagnostic reaches back over the trivia.
    /// </para>
    /// </summary>
    private GreenToken Missing(SyntaxKind kind, DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        var (start, width) = Caret(index);
        reported++;
        return GreenToken.Missing(
            kind, new GreenDiagnostic(start - FullStart(index), width, message, fix ?? Writes(kind)));
    }

    /// <summary>
    /// The fix for a missing piece, when the fix is simply to write it: a bracket has only one
    /// possible text, and the missing token's slot already gives its place, so the editor can be
    /// told to insert it there. Anything else — a name, a number, a message in quotes — has to be
    /// written by the programmer, and has no fix.
    /// </summary>
    private static DiagnosticFix? Writes(SyntaxKind kind) =>
        kind is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace or SyntaxKind.OpenParen
            or SyntaxKind.CloseParen or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket
            ? new DiagnosticFix(FixKind.MissingPiece, SyntaxFacts.FixedText(kind))
            : null;

    /// <summary>The <c>{</c> that opens a block: the one place the parser says a brace is wanted.</summary>
    private GreenToken ExpectOpenBrace() => Expect(SyntaxKind.OpenBrace, Catalogue.ExpectedBrace.Says("`{`"));

    /// <summary>
    /// The items of a comma-separated list and the commas between them, as one list; null for a
    /// list with no items, since an empty slot is stored as null. Items and commas alternate, so
    /// a comma is taken only after an item already in the list, and the first item that cannot
    /// be read ends the list: the comma before it becomes the list's last piece, and the rest of
    /// the line is left for the line to hold as skipped tokens. When items are expressions there
    /// is always one to take — the expression parser leaves an empty expression where it could
    /// read none — so <c>1, , 2</c> keeps all three; a list whose items are parsed some other way
    /// stops at the gap instead. Either way, this method inserts nothing between two commas.
    /// </summary>
    private GreenSeparatedList? ParseSeparatedList(Func<GreenNode?> parseItem)
    {
        if (parseItem() is not { } first)
            return null;
        var pieces = ImmutableArray.CreateBuilder<GreenNode>();
        pieces.Add(first);
        while (Kind == SyntaxKind.Comma)
        {
            pieces.Add(Advance());
            if (parseItem() is not { } next)
                break;
            pieces.Add(next);
        }
        return new GreenSeparatedList(pieces.ToImmutable());
    }

    /// <summary>
    /// One line's parse, and the block kind it was parsed in, so a line whose surroundings have
    /// not changed can keep the nodes it already has. The diagnostics are part of those nodes, so
    /// a line kept across an edit keeps what was said about it.
    /// </summary>
    /// <param name="Context">The kind of block the line was read in.</param>
    /// <param name="ExportKeyword">The <c>.export</c> before a declaration the line exports, or null.</param>
    /// <param name="Node">What the line's own tokens parse to.</param>
    /// <param name="SkippedTokens">What the statement could not take, or null when nothing was left.</param>
    public sealed record Result(
        BlockKind Context, GreenToken? ExportKeyword, GreenNode Node, GreenNode? SkippedTokens);

    /// <summary>
    /// A diagnostic reported over a token and waiting for the node that holds it: where it sits in
    /// the line, what it says, and the change the message names as its fix, where it names one.
    /// </summary>
    /// <param name="Start">Where it starts, from the start of the line.</param>
    /// <param name="Width">How many characters it covers.</param>
    /// <param name="Message">What to tell the programmer.</param>
    /// <param name="Fix">The change the message names as its fix, or null.</param>
    private readonly record struct Pending(int Start, int Width, DiagnosticMessage Message, DiagnosticFix? Fix);
}
