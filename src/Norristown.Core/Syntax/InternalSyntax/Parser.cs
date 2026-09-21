using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Parses one line's tokens into a statement. A line's syntax depends only on its own tokens
/// and the kind of block around it, so a line parses without looking at any other
/// line and its statement survives an edit anywhere else in the file.
/// <para>
/// The parser never aborts a line: whatever it cannot read becomes a
/// <see cref="SyntaxKind.SkippedTokens"/> node with a diagnostic, which the line holds as it
/// holds the line break and the <c>.export</c> before a declaration. A statement is therefore
/// only its own tokens, wherever it is written. A node's shape does not depend on what the source
/// wrote either: a piece the line has a place for and does not write stands in its slot as a
/// missing token, of no width and with no text, so a required property is never null and what
/// reads the tree asks <see cref="GreenNode.IsMissing"/> where it cares. A piece belonging to a
/// part of the line the source left out altogether — a signature, an operand, an <c>as</c> — is
/// null, and that is the whole of what null means.
/// </para>
/// <para>
/// A diagnostic goes over the token the parser is looking at. Where that is the end of the line —
/// where there is nothing written, because the piece the line wants was never typed — it goes at
/// the end of the last token the source does have, ahead of the whitespace and the comment after
/// it: that is where the piece belongs, and a caret there neither drifts right as trailing spaces
/// are typed nor lands past a trailing comment. That is the one rule, and
/// <see cref="Caret"/> is the whole of it. A diagnostic about a piece the line does not have rides
/// on the missing token that stands in its slot, reaching back over the trivia between them with a
/// negative offset where it must; anything else is reported over its token and given to the
/// innermost node the parser finishes that holds it.
/// </para>
/// <para>
/// The class is written in one file per area — declarations, data, directives, macros,
/// signatures, operands, expressions — and this file holds what every area reads a line with:
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
    /// How deeply expressions may nest inside one another. Each level costs a dozen stack
    /// frames, and a stack that runs out takes the process with it, so a line nobody would
    /// write stops being read rather than stopping everything.
    /// </summary>
    private const int MaximumNesting = 100;

    private readonly ImmutableArray<GreenToken> tokens;
    private readonly BlockKind context;
    private readonly bool opensBlock;

    // The diagnostics reported over a token, placed in the line and waiting for the node that
    // holds them, and how many diagnostics the line has been given in all, missing tokens'
    // included, which is what "nothing has been said about this line yet" reads.
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

    /// <summary>The next token, which stays put once the end-of-line token is reached.</summary>
    private GreenToken Advance()
    {
        var token = tokens[index];
        if (index < tokens.Length - 1)
            index++;
        return token;
    }

    /// <summary>Reports over the current token, with the change the message names as its fix.</summary>
    private void Report(string message, DiagnosticFix? fix = null) => Report(index, message, fix);

    /// <summary>
    /// Reports only when nothing has been said about this line yet. A half-typed
    /// <c>m!({</c> runs out of tokens inside an argument, inside the braces and inside the
    /// parentheses; the first of those says what is missing, and the rest is the same news.
    /// </summary>
    private void ReportOnce(string message, DiagnosticFix? fix = null)
    {
        if (reported == 0)
            Report(index, message, fix);
    }

    /// <summary>
    /// The expression that stands where a nest of them runs deeper than the parser reads, or
    /// null while there is room for one more. What is left on the line is not read: the line
    /// keeps it as skipped tokens, so the text still reads back whole, and the one thing said
    /// about the line is that it nests too deeply.
    /// </summary>
    private ExpressionSyntax? TooDeeplyNested()
    {
        if (nesting <= MaximumNesting)
            return null;
        ReportOnce($"this nests more than {MaximumNesting} expressions deep, which is as far as nt65 reads");
        return new ErrorExpressionSyntax(null);
    }

    /// <summary>Reports over the token at <paramref name="token"/>, wherever it sits in the line.</summary>
    private void Report(int token, string message, DiagnosticFix? fix = null)
    {
        var (start, width) = Caret(token);
        pending.Add(new Pending(start, width, message, fix));
        reported++;
    }

    /// <summary>
    /// Gives <paramref name="node"/> the diagnostics reported over the text it holds. The parser
    /// has just finished reading it, so it ends where the parser now stands; a diagnostic inside
    /// it is one it is about, and one the parser has not placed yet.
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
    /// Gives the statement whatever no piece of the line claimed: a diagnostic over a token the
    /// statement holds that no one node of it stands for, and the rare one about a place outside it.
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
    /// Where a diagnostic about the token at <paramref name="at"/> goes, from the start of the
    /// line: over the token, or, where the line has run out, at the end of the last token it does
    /// have, which is where the piece it wants belongs.
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

        // An unnamed label needs a name rather than a spelling, so it is the whole news about
        // its line: `:` where a name belongs and `:+` in an operand are read no further, and
        // what the rest of the line would otherwise be reported for is this same mistake.
        if (Lines.UnnamedLabel(tokens) is >= 0 and var colon)
        {
            Report(colon, "an unnamed label is written `@name`: a cheap local, private to the routine around it");
            return Own(new ErrorLineSyntax(TakeRest()));
        }

        // Blocks with a line grammar of their own. A struct or union member
        // is written like a labelled data declaration and needs no rule of its own.
        //
        // The line that opens a block belongs to that block, so it arrives here in its own
        // context; it is read as the opener it is, and only the lines after it are members.
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
            _ => ErrorLine("expected a label, a constant, an instruction or a directive"),
        };
    }

    /// <summary>The statement, with whatever is left on the line kept aside for the line to hold.</summary>
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
        // A block written on one line, `.data name { .byte 1 }`, is that one thing: the
        // `{` was read as the opener it is, and what follows it is the body, on the wrong
        // line rather than unexpected.
        if (reported == 0)
        {
            Report(!opensBlock && index > 0 && tokens[index - 1].Kind == SyntaxKind.OpenBrace
                ? $"a block's `{{` ends the line that opens it: {Describe(Current)} goes on the next line, and `}}` on its own"
                : $"unexpected {Describe(Current)}");
        }
        skippedTokens = Own(new SkippedTokensSyntax(TakeRest()));
    }

    private GreenNode ErrorLine(string message, DiagnosticFix? fix = null)
    {
        Report(index, message, fix);
        return Own(new ErrorLineSyntax(TakeRest()));
    }

    /// <summary>
    /// Every token up to, but not including, the end-of-line token, as one list; null where
    /// there are none, which is what an empty list is held as.
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
    /// The token of <paramref name="kind"/> written here, or the missing token that stands where
    /// one belongs and says nothing, which is for a slot something else on the line has already
    /// been reported for.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind) => Kind == kind ? Advance() : GreenToken.Missing(kind);

    /// <summary>
    /// The same, with <paramref name="message"/> on the missing token, where nothing has been said
    /// about this line yet.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind, string message)
    {
        if (Kind == kind)
            return Advance();
        return reported == 0 ? Missing(kind, message) : GreenToken.Missing(kind);
    }

    /// <summary>
    /// The name written here, or the missing identifier that stands where one belongs, carrying
    /// <paramref name="message"/>. A name may be spelled as an identifier, a register or a
    /// mnemonic, which is why it is not one kind for <see cref="Expect(SyntaxKind, string)"/>.
    /// </summary>
    private GreenToken ExpectName(string message) =>
        AtName ? Advance() : Missing(SyntaxKind.Identifier, message);

    /// <summary>
    /// The missing token of <paramref name="kind"/>, standing where one belongs that the source
    /// does not have and carrying <paramref name="message"/> whether or not the line has been
    /// reported on already: what <see cref="Expect(SyntaxKind, string)"/> does where the second
    /// piece missing on a line is news of its own.
    /// <para>
    /// The token sits after the trivia that follows the token before it, and the caret belongs
    /// where that token's text ends, so the diagnostic reaches back over the trivia.
    /// </para>
    /// </summary>
    private GreenToken Missing(SyntaxKind kind, string message, DiagnosticFix? fix = null)
    {
        var (start, width) = Caret(index);
        reported++;
        return GreenToken.Missing(
            kind, new GreenDiagnostic(start - FullStart(index), width, message, fix ?? Writes(kind)));
    }

    /// <summary>
    /// The fix for a piece the line does not have, where writing the piece is the whole of it: a
    /// bracket has one text and one place, which the missing token in the slot already says, so
    /// the editor is told to write it there. Anything else — a name, a number, a message in quotes
    /// — is the programmer's to write, and has no fix.
    /// </summary>
    private static DiagnosticFix? Writes(SyntaxKind kind) =>
        kind is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace or SyntaxKind.OpenParen
            or SyntaxKind.CloseParen or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket
            ? new DiagnosticFix(FixKind.MissingPiece, SyntaxFacts.FixedText(kind))
            : null;

    /// <summary>The <c>{</c> that opens a block: the one place the parser says a brace is wanted.</summary>
    private GreenToken ExpectOpenBrace() => Expect(SyntaxKind.OpenBrace, "expected `{`");

    /// <summary>
    /// The items of a comma-separated list and the commas between them, as the one list that holds
    /// them; null for a list with no items, which is what a slot with nothing in it reads as. A
    /// separated list alternates an item and the comma after it, so a comma is taken only after
    /// an item already in the list, and the first item that cannot be read ends the list: the
    /// comma before it is the list's last piece and the rest of the line is the line's to hold.
    /// Where an item is an expression there is always one to take — the parser leaves the empty
    /// expression where it could read none — so <c>1, , 2</c> keeps all three, and a list whose
    /// items are written some other way stops at the gap instead. Either way nothing is invented
    /// to stand between two commas.
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
    private readonly record struct Pending(int Start, int Width, string Message, DiagnosticFix? Fix);
}
