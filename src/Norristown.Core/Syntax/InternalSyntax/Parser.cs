using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Parses one line's tokens into a statement. A line's syntax depends only on its own tokens
/// and the kind of block around it, so a line parses without looking at any other
/// line and its statement survives an edit anywhere else in the file.
/// <para>
/// The parser never gives up on a line. Whatever it cannot read becomes a
/// <see cref="SyntaxKind.SkippedTokens"/> node with a diagnostic, which the line holds alongside
/// its line break and any <c>.export</c> before a declaration. A statement therefore contains
/// only its own tokens, wherever it appears. A node's shape does not depend on what the source
/// contains either. A required piece that is absent from the source fills its slot as a missing
/// token, with no width and no text, so a required property is never null, and code reading the
/// tree checks <see cref="GreenNode.IsMissing"/> where it cares. An optional part of the line that
/// the source leaves out entirely, such as a signature, an operand or an <c>as</c>, is null, and
/// null never means anything else.
/// </para>
/// <para>
/// A diagnostic covers the token the parser is looking at. If that token is the end of the line,
/// because the piece the line wants was never typed, the diagnostic goes at the end of the last
/// token the source does have, before the whitespace and comment after it. That is where the
/// missing piece belongs, and a caret there neither drifts right as trailing spaces are typed nor
/// lands past a trailing comment. This is the only placement rule, and <see cref="Caret"/>
/// implements it. A diagnostic about a missing piece is held by the missing token in its slot,
/// reaching back over the trivia in between with a negative offset where necessary. Any other
/// diagnostic is reported over its token and attached to the innermost node the parser finishes
/// that contains it.
/// </para>
/// <para>
/// The class is split into one file per area (declarations, data, directives, macros,
/// signatures, operands and expressions). This file holds what every area uses to read a line,
/// which is the tokens, the placement of diagnostics, and the dispatch that picks the area.
/// </para>
/// </summary>
internal sealed partial class Parser
{
    /// <summary>The loosest binding level, which <see cref="ParseExpression"/> starts at.</summary>
    private const int LowestPrecedence = 13;

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
    // attached to the node that contains them. `reported` is the total number of diagnostics the
    // line has been given, including those on missing tokens. ReportOnce and Expect use it to
    // tell whether anything has been reported on this line yet. An attempt that may fail reports
    // into a list of its own for as long as it runs.
    private List<Pending> pending = [];
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

    /// <summary>
    /// Gets a value indicating whether an operand has run out, at the end of the line or at the
    /// <c>}</c> around it.
    /// </summary>
    private bool AtOperandEnd => AtEnd || (braced && Kind == SyntaxKind.CloseBrace);

    /// <summary>
    /// Gets a value indicating whether a declaration's name could appear here, as
    /// <see cref="Lines.IsName"/> decides.
    /// </summary>
    private bool AtName => Lines.IsName(Kind);

    /// <summary>Parses <paramref name="line"/> as it stands inside a block of <paramref name="context"/>.</summary>
    public static Result Parse(GreenLine line, BlockKind context)
    {
        var parser = new Parser(line, context);
        var node = parser.ParseLine(line.LineKind);
        parser.AttachPending(node);
        if (line.Parts.Length > 1)
            parser.CheckLineBreaks(node);
        return new Result(context, parser.exportKeyword, node, parser.skippedTokens);
    }

    private static string Describe(GreenToken token) => $"`{token.Text}`";

    /// <summary>
    /// Reports each line break of a joined line that stands outside an expression's brackets.
    /// The breaks inside a group's parentheses, a call's arguments, a set or an index are the ones
    /// the language allows. Each report goes on the token before the break, and is held by the
    /// statement, as a diagnostic no inner node claimed is.
    /// </summary>
    private void CheckLineBreaks(GreenNode statement)
    {
        var breaks = new List<(int Start, int Width)>();
        var offset = 0;
        if (exportKeyword is not null)
            FindBreaks(exportKeyword, inside: false, call: false, ref offset, breaks);
        var statementStart = offset;
        FindBreaks(statement, inside: false, call: false, ref offset, breaks);
        if (skippedTokens is not null)
            FindBreaks(skippedTokens, inside: false, call: false, ref offset, breaks);
        foreach (var (start, width) in breaks)
            statement.Report(new GreenDiagnostic(start - statementStart, width, Catalogue.ContinuationOutsideExpression));
    }

    /// <summary>
    /// Adds to <paramref name="breaks"/> the token before each line break under
    /// <paramref name="node"/> that is not inside an expression's brackets, as an offset from the
    /// start of the line and a width. <paramref name="offset"/> is where the node starts, and is
    /// moved past it.
    /// </summary>
    /// <param name="node">The node or token to search.</param>
    /// <param name="inside">Whether the node is inside an expression's brackets.</param>
    /// <param name="call">Whether the node is part of a call, whose argument list is an expression's.</param>
    /// <param name="offset">Where the node starts in the line.</param>
    /// <param name="breaks">The breaks found outside an expression's brackets.</param>
    private static void FindBreaks(GreenNode node, bool inside, bool call, ref int offset, List<(int, int)> breaks)
    {
        if (node is GreenToken token)
        {
            if (!inside && token.TrailingTrivia.Any(trivia => trivia.Kind == SyntaxKind.LineBreakTrivia))
                breaks.Add((offset + token.LeadingWidth, token.Text.Length));
            offset += token.FullWidth;
            return;
        }

        // A bracket's own `(` or `[` is followed by what is inside it, and its closer by what is
        // outside, so only the slots before the last are inside.
        var brackets = node.Kind is SyntaxKind.ParenthesizedExpression or SyntaxKind.SetExpression or SyntaxKind.ElementIndex
            || (node.Kind == SyntaxKind.ArgumentList && call);
        for (var i = 0; i < node.SlotCount; i++)
        {
            if (node.GetSlot(i) is not { } slot)
                continue;
            var within = brackets ? i < node.SlotCount - 1 || inside : inside;
            FindBreaks(slot, within, node.Kind == SyntaxKind.CallExpression, ref offset, breaks);
        }
    }

    /// <summary>
    /// Returns a value indicating whether the current token is the contextual word
    /// <paramref name="word"/>, such as <c>dp</c> or <c>proc</c>. Those words are matched without
    /// regard to case, as every other word the language fixes is.
    /// </summary>
    private bool AtWord(string word) =>
        Kind == SyntaxKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the current token and moves past it. The position stays on the end-of-line token
    /// once it is reached.
    /// </summary>
    private GreenToken Advance()
    {
        var token = tokens[index];
        if (index < tokens.Length - 1)
            index++;
        return token;
    }

    /// <summary>
    /// Reports <paramref name="message"/> over the current token, with the change the message
    /// names as its fix.
    /// </summary>
    private void Report(DiagnosticMessage message, DiagnosticFix? fix = null) => Report(index, message, fix);

    /// <summary>
    /// Reports <paramref name="message"/> only if nothing has been reported on this line yet. A
    /// half-typed <c>m!({</c> runs out of tokens inside an argument, inside the braces and inside
    /// the parentheses. The first report says what is missing, and the others would only repeat
    /// it.
    /// </summary>
    private void ReportOnce(DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        if (reported == 0)
            Report(index, message, fix);
    }

    /// <summary>
    /// Returns an error expression to use in place of one nested deeper than
    /// <see cref="MaximumNesting"/>, or null while there is room for one more level. The rest of
    /// the line is not parsed. The line keeps it as skipped tokens, so its text is still
    /// preserved, and the only thing reported about it is that it nests too deeply.
    /// </summary>
    private ExpressionSyntax? TooDeeplyNested()
    {
        if (nesting <= MaximumNesting)
            return null;
        ReportOnce(Catalogue.NestingTooDeep.Message(MaximumNesting));
        return new ErrorExpressionSyntax(null);
    }

    /// <summary>
    /// Reports <paramref name="message"/> over the token at index <paramref name="token"/>,
    /// wherever it sits in the line.
    /// </summary>
    private void Report(int token, DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        var (start, width) = Caret(token);
        pending.Add(new Pending(start, width, message, fix));
        reported++;
    }

    /// <summary>
    /// Starts an attempt to read the line one way, which may be abandoned. The diagnostics
    /// reported during the attempt are held apart from the line's, and join them only if
    /// <see cref="Keep"/> is called.
    /// </summary>
    /// <returns>The reset point that <see cref="Keep"/> or <see cref="Rewind"/> ends the attempt at.</returns>
    private ResetPoint Attempt()
    {
        var point = new ResetPoint(index, reported, pending);
        pending = [];
        return point;
    }

    /// <summary>
    /// Ends the attempt that started at <paramref name="point"/> by keeping what it read, adding
    /// the diagnostics it reported to the line's.
    /// </summary>
    private void Keep(ResetPoint point)
    {
        point.Pending.AddRange(pending);
        pending = point.Pending;
    }

    /// <summary>
    /// Ends the attempt that started at <paramref name="point"/> by abandoning it. The parser goes
    /// back to the token the attempt started at, and the diagnostics the attempt reported are
    /// dropped, so the line is as it was before the attempt.
    /// </summary>
    private void Rewind(ResetPoint point)
    {
        pending = point.Pending;
        index = point.Index;
        reported = point.Reported;
    }

    /// <summary>
    /// Attaches to <paramref name="node"/> the pending diagnostics that fall within its text. The
    /// parser has just finished reading the node, so it ends at the parser's current position. A
    /// pending diagnostic inside that range is about this node, since no inner node claimed it.
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
    /// Attaches every diagnostic that no node claimed to the statement. These are diagnostics
    /// over a token of the statement that no inner node contains, and the rare diagnostic about a
    /// place outside the statement.
    /// </summary>
    private void AttachPending(GreenNode statement)
    {
        if (pending.Count == 0)
            return;
        var start = exportKeyword?.FullWidth ?? 0;
        foreach (var held in pending)
            statement.Report(new GreenDiagnostic(held.Start - start, held.Width, held.Message, held.Fix));
        pending.Clear();
    }

    /// <summary>
    /// Returns where a diagnostic about the token at <paramref name="at"/> goes, as an offset from
    /// the start of the line and a width. The diagnostic covers the token, or, if the line has run
    /// out, is a zero-width caret at the end of the last token the line does have, which is where
    /// the missing piece belongs.
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

    /// <summary>
    /// Returns where token <paramref name="at"/> starts in the line, including its leading trivia.
    /// </summary>
    private int FullStart(int at)
    {
        // The starts are computed for the whole line the first time a diagnostic needs one, and
        // kept. A line with nothing to report never pays for them, and one with several things to
        // report pays once rather than walking the tokens before each of them.
        if (starts is null)
        {
            starts = new int[tokens.Length + 1];
            for (var i = 0; i < tokens.Length; i++)
                starts[i + 1] = starts[i] + tokens[i].FullWidth;
        }
        return starts[at];
    }

    private GreenNode ParseLine(LineKind kind)
    {
        if (kind == LineKind.BlockClose)
            return ParseBlockClose();
        if (kind == LineKind.Blank)
            return Finish(new BlankLineSyntax());

        // An unnamed label must be replaced by a named one, not merely respelled, so it is the
        // only thing reported about its line. A line with `:` where a name belongs, or with `:+`
        // in an operand, is parsed no further, since anything else wrong with it is the same
        // mistake.
        if (Lines.UnnamedLabel(tokens) is >= 0 and var colon)
        {
            Report(colon, Catalogue.UnnamedLabel);
            return Own(new ErrorLineSyntax(TakeRest()));
        }

        // These blocks' lines follow a grammar of their own. A struct or union member has the
        // form of a labelled data declaration and needs no rule of its own.
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

            // A name on its own splices a `block` parameter. Which blocks may hold one is not a
            // question about this line, since a splice inside an `.if` inside a body is still a
            // splice. So it is read as a splice everywhere, and the binder decides where it
            // belongs.
            LineKind.BareIdentifier => Finish(new BlockSpliceSyntax(Advance())),
            _ => ErrorLine(Catalogue.ExpectedStatement.Message("a label, a constant, an instruction or a directive")),
        };
    }

    /// <summary>
    /// Returns the statement, after moving whatever is left on the line into its skipped tokens.
    /// </summary>
    private GreenNode Finish(GreenNode statement)
    {
        SkipRest();
        return statement;
    }

    /// <summary>
    /// Moves anything the statement could not take into the line's
    /// <see cref="SyntaxKind.SkippedTokens"/>.
    /// </summary>
    private void SkipRest()
    {
        if (AtEnd)
            return;

        // One diagnostic per line is enough. Where the parser has already reported what it
        // wanted, the tokens it then walks past are the same problem reported twice.
        //
        // A block on one line, such as `.data name { .byte 1 }`, gets its own message, because
        // the `{` was read as a block opener, and what follows it is the block's body on the
        // wrong line, rather than an unexpected token.
        if (reported == 0)
        {
            Report(!opensBlock && index > 0 && tokens[index - 1].Kind == SyntaxKind.OpenBrace
                ? Catalogue.BlockBraceEndsTheLine.Message(Describe(Current))
                : Catalogue.UnexpectedToken.Message(Describe(Current)));
        }
        skippedTokens = Own(new SkippedTokensSyntax(TakeRest()));
    }

    /// <summary>
    /// Skips tokens up to the first one at which <paramref name="stop"/> holds, or to the end of
    /// the line, and returns them. The first of them gets <paramref name="message"/>, if nothing
    /// has been reported on the line yet.
    /// </summary>
    private SkippedTokensSyntax SkipUntil(Func<bool> stop, DiagnosticMessage message)
    {
        ReportOnce(message);
        var skipped = new GreenListBuilder();
        while (!AtEnd && !stop())
            skipped.Add(Advance());
        return Own(new SkippedTokensSyntax(skipped.ToList()));
    }

    private GreenNode ErrorLine(DiagnosticMessage message, DiagnosticFix? fix = null)
    {
        Report(index, message, fix);
        return Own(new ErrorLineSyntax(TakeRest()));
    }

    /// <summary>
    /// Returns every token up to, but not including, the end-of-line token, as one list, or null
    /// if there are none, since an empty list is stored as null.
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
        if (Lines.IsNextBlockArgument(tokens, index))
            return Finish(new BlockContinuationSyntax(brace, Advance(), Advance()));

        return SyntaxFacts.LineDirectiveKind(Current.DirectiveKind) switch
        {
            SyntaxKind.ElseIfDirective => Finish(ParseIf(brace)),
            SyntaxKind.ElseDirective => Finish(ParseElse(brace)),
            _ => Finish(new BlockCloseLineSyntax(brace)),
        };
    }

    /// <summary>
    /// Returns the current token if it is of <paramref name="kind"/>, or otherwise a missing token
    /// that reports nothing, for a slot where some other problem on the line has already been
    /// reported.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind) => Kind == kind ? Advance() : GreenToken.Missing(kind);

    /// <summary>
    /// Returns the current token if it is of <paramref name="kind"/>, as
    /// <see cref="Expect(SyntaxKind)"/> does, but the missing token it otherwise returns reports
    /// <paramref name="message"/> if nothing has been reported on this line yet.
    /// </summary>
    private GreenToken Expect(SyntaxKind kind, DiagnosticMessage message)
    {
        if (Kind == kind)
            return Advance();
        return reported == 0 ? Missing(kind, message) : GreenToken.Missing(kind);
    }

    /// <summary>
    /// Returns the current token if it is of <paramref name="kind"/>, or otherwise a missing token
    /// that reports <paramref name="message"/>, whatever else the line has reported. It is used
    /// where a missing piece deserves a diagnostic of its own.
    /// </summary>
    private GreenToken Require(SyntaxKind kind, DiagnosticMessage message) =>
        Kind == kind ? Advance() : Missing(kind, message);

    /// <summary>
    /// Returns the name at the current token, or a missing identifier that reports
    /// <paramref name="message"/>. A name may be lexed as an identifier, a register or a mnemonic,
    /// so it is not a single token kind that <see cref="Expect(SyntaxKind, DiagnosticMessage)"/>
    /// could check for.
    /// </summary>
    private GreenToken ExpectName(DiagnosticMessage message) =>
        AtName ? Advance() : Missing(SyntaxKind.Identifier, message);

    /// <summary>
    /// Returns a missing token of <paramref name="kind"/> that reports <paramref name="message"/>,
    /// whether or not anything has been reported on the line already. It is used instead of
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
            kind, new GreenDiagnostic(start - FullStart(index), width, message, fix ?? InsertionFix(kind)));
    }

    /// <summary>
    /// Returns the fix for a missing piece when the fix is simply to insert it, or null otherwise.
    /// A bracket has only one possible text, and the missing token's slot already gives its place,
    /// so the editor can be told to insert it there. Anything else, such as a name, a number or a
    /// message in quotes, has to be typed by the programmer, and has no fix.
    /// </summary>
    private static DiagnosticFix? InsertionFix(SyntaxKind kind) =>
        kind is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace or SyntaxKind.OpenParen
            or SyntaxKind.CloseParen or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket
            ? new DiagnosticFix(FixKind.MissingPiece, SyntaxFacts.FixedText(kind))
            : null;

    /// <summary>
    /// Returns the <c>{</c> that opens a block. This is the one place the parser reports that a
    /// brace is expected.
    /// </summary>
    private GreenToken ExpectOpenBrace() => Expect(SyntaxKind.OpenBrace, Catalogue.ExpectedBrace.Message("`{`"));

    /// <summary>
    /// Parses the items of a comma-separated list and the commas between them as one list, or
    /// returns null for a list with no items, since an empty slot is stored as null. Items and
    /// commas alternate, so a comma is taken only after an item already in the list, and the first
    /// item that cannot be read ends the list. The comma before it becomes the list's last piece,
    /// and the rest of the line is left for the line to hold as skipped tokens.
    /// <para>
    /// When items are expressions there is always one to take, because the expression parser
    /// leaves an empty expression where it could read none, so <c>1, , 2</c> keeps all three. A
    /// list whose items are parsed some other way stops at the gap instead. Either way, this method
    /// inserts nothing between two commas.
    /// </para>
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
    /// Represents one line's parse, and the block kind it was parsed in, so a line whose
    /// surroundings have not changed can keep the nodes it already has. The diagnostics are part
    /// of those nodes, so a line kept across an edit keeps the diagnostics reported on it.
    /// </summary>
    /// <param name="Context">The kind of block the line was read in.</param>
    /// <param name="ExportKeyword">The <c>.export</c> before a declaration the line exports, or null.</param>
    /// <param name="Node">The node the line's own tokens parse to.</param>
    /// <param name="SkippedTokens">The tokens the statement could not take, or null if nothing was left.</param>
    public sealed record Result(
        BlockKind Context, GreenToken? ExportKeyword, GreenNode Node, GreenNode? SkippedTokens);

    /// <summary>
    /// Represents a diagnostic reported over a token and waiting for the node that holds it. It
    /// records where the diagnostic sits in the line, its message, and the change the message
    /// names as its fix, if it names one.
    /// </summary>
    /// <param name="Start">Where the diagnostic starts, relative to the start of the line.</param>
    /// <param name="Width">How many characters the diagnostic covers.</param>
    /// <param name="Message">The message for the programmer.</param>
    /// <param name="Fix">The change the message names as its fix, or null.</param>
    private readonly record struct Pending(int Start, int Width, DiagnosticMessage Message, DiagnosticFix? Fix);

    /// <summary>
    /// Represents the point an attempt started at, which <see cref="Rewind"/> returns the parser
    /// to.
    /// </summary>
    /// <param name="Index">The token the attempt started at.</param>
    /// <param name="Reported">How many diagnostics the line had been given before the attempt.</param>
    /// <param name="Pending">The line's pending diagnostics, which the attempt's own join if it is kept.</param>
    private readonly record struct ResetPoint(int Index, int Reported, List<Pending> Pending);
}
