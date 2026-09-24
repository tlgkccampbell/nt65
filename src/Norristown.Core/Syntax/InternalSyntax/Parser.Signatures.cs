using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// Parses the processor state that a signature gives, which a proc, an extern proc, a macro, an
// import and the `.state` and `.ensure` directives all take.
internal sealed partial class Parser
{
    private static bool LooksLikeAWidth(string text) =>
        text.Length > 1 && char.ToLowerInvariant(text[0]) is 'a' or 'i' && text[1..].All(char.IsAsciiDigit);

    /// <summary>Parses the <c>: entry -&gt; exit</c> of a proc, an extern proc or a macro.</summary>
    private ProcSignatureSyntax ParseSignature()
    {
        var colon = Advance();
        var entry = ParseStateList();
        GreenToken? arrow = null;
        StateListSyntax? exit = null;
        if (Kind == SyntaxKind.Arrow)
        {
            arrow = Advance();
            exit = ParseStateList();
        }
        return new ProcSignatureSyntax(colon, entry, arrow, exit);
    }

    private StateListSyntax ParseStateList() => new(ParseSeparatedList(ParseStateItem));

    private GreenNode? ParseStateItem()
    {
        // A name that is not an item word names a signature set, which stands for its items. A
        // name shaped like a width, such as `a9`, is treated as a misspelled width instead.
        if (Kind == SyntaxKind.ColonColon
            || (Kind == SyntaxKind.Identifier && !SyntaxFacts.IsStateWord(Current.Text) && !LooksLikeAWidth(Current.Text)))
        {
            return new StateSetItemSyntax(ParseName());
        }

        // `?` on its own marks every tracked part of the state as unknown, which is the state a
        // routine called from outside nt65 starts in.
        if (Kind == SyntaxKind.Question)
            return new StateUnknownItemSyntax(Advance());

        // `a` and `i` are the accumulator and index widths; `a` arrives as a register token.
        if (Kind is not (SyntaxKind.Identifier or SyntaxKind.Register))
        {
            Report(Catalogue.ExpectedStateItem.Message("a processor-state item, such as `a8`, `i16` or `dp = 0`"));
            return null;
        }

        var nameIndex = index;
        var name = Advance();

        // `dp = e` and `dbr = e` are the parts given a value with an `=`, and `dbr = [...]` a set
        // of banks. The value is parsed before the word is checked, so that a misspelled name is
        // the last diagnostic reported for the item. Which words may take a set of banks is
        // checked when the signature is analysed, not here.
        if (Kind == SyntaxKind.Equals)
        {
            var equals = Advance();
            if (Kind == SyntaxKind.OpenBracket)
            {
                var openBracket = Advance();
                var ranges = Kind != SyntaxKind.CloseBracket ? ParseSeparatedList(ParseRange) : null;
                var closeBracket = Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Message("`]`"));
                if (!SyntaxFacts.IsStateItem(name.Text, SyntaxKind.Equals))
                    Report(nameIndex, Catalogue.StateItemUnknown.Message(name.Text));
                return Own(new StateBanksItemSyntax(name, equals, openBracket, ranges, closeBracket));
            }
            var given = ParseExpression();
            if (!SyntaxFacts.IsStateItem(name.Text, SyntaxKind.Equals))
                Report(nameIndex, Catalogue.StateItemUnknown.Message(name.Text));

            // The misspelled-name diagnostic is about the item, so the item takes it.
            return Own(new StateValueItemSyntax(name, equals, given));
        }

        GreenToken? suffix = Kind is SyntaxKind.Star or SyntaxKind.Question ? Advance() : null;
        if (!SyntaxFacts.IsStateItem(name.Text, suffix?.Kind ?? SyntaxKind.None))
        {
            Report(nameIndex, Catalogue.StateItemUnknown.Message(name.Text));
            return Own(new StateFlagItemSyntax(name, suffix));
        }
        else if (name.Text.Equals("args", StringComparison.OrdinalIgnoreCase))
        {
            // `args n` gives how many bytes the caller pushes before the call.
            return new StateValueItemSyntax(name, null, ParseExpression());
        }
        else if (name.Text.Equals("inline", StringComparison.OrdinalIgnoreCase))
        {
            // `inline n` or `inline .strz` gives how much data follows each call.
            return Current.DirectiveKind == DirectiveKind.Strz
                ? new StateInlineItemSyntax(name, Advance())
                : new StateValueItemSyntax(name, null, ParseExpression());
        }
        else if (name.Text.ToLowerInvariant() is "keeps" or "reads" or "saves")
        {
            return new StateRegistersItemSyntax(name, ParseRegisters(name.Text.ToLowerInvariant()));
        }
        return new StateFlagItemSyntax(name, suffix);
    }

    /// <summary>
    /// Parses the registers of a <c>keeps a, x</c>, a <c>reads a, c</c> or a <c>saves x</c>. A bare
    /// register name is not an item anywhere else in a signature, so the list continues through
    /// the commas that also separate the signature's own items, and stops at the first comma
    /// followed by anything else. <c>reads none</c> declares that a routine reads nothing, which
    /// is not what leaving <c>reads</c> out says.
    /// </summary>
    private GreenSeparatedList? ParseRegisters(string word)
    {
        if (word == "reads" && Kind == SyntaxKind.Identifier
            && Current.Text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new GreenSeparatedList([new IdentifierNameSyntax(Advance(), null)]);
        }
        if (!AtKeptRegister(index))
        {
            Report(Catalogue.ExpectedKeptRegisters.Message(word switch
            {
                "reads" => "the registers it reads: `reads a`, `reads a, c`, or `reads none`",
                "saves" => "the register the store saves: `saves x`",
                _ => "the registers it keeps: `keeps a`, `keeps x, y`",
            }));
            return null;
        }
        var pieces = ImmutableArray.CreateBuilder<GreenNode>();
        pieces.Add(new IdentifierNameSyntax(Advance(), null));
        while (Kind == SyntaxKind.Comma && AtKeptRegister(index + 1))
        {
            pieces.Add(Advance());
            pieces.Add(new IdentifierNameSyntax(Advance(), null));
        }
        return new GreenSeparatedList(pieces.ToImmutable());
    }

    /// <summary>
    /// Returns a value indicating whether the token at <paramref name="at"/> names a register that
    /// a <c>keeps</c>, <c>reads</c> or <c>saves</c> may take.
    /// </summary>
    private bool AtKeptRegister(int at) =>
        at < tokens.Length && tokens[at].Kind is SyntaxKind.Identifier or SyntaxKind.Register
        && SyntaxFacts.IsKeptRegister(tokens[at].Text);
}
