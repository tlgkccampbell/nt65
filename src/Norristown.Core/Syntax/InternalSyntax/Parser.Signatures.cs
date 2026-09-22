using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

// The processor state a signature is written in, which a proc, an extern proc, a macro, an
// import and the `.state` and `.ensure` directives all take.
internal sealed partial class Parser
{
    /// <summary>The <c>: entry -&gt; exit</c> of a proc, an extern proc or a macro.</summary>
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
        // A name that is no item's word names a signature set, which stands for its items. One
        // spelled like a width, `a9`, is a width misspelled.
        if (Kind == SyntaxKind.ColonColon
            || (Kind == SyntaxKind.Identifier && !SyntaxFacts.IsStateWord(Current.Text) && !LooksLikeAWidth(Current.Text)))
        {
            return new StateSetItemSyntax(ParseName());
        }

        // `?` on its own is every tracked part of the state unknown, the state a routine
        // reached from outside nt65 is entered in.
        if (Kind == SyntaxKind.Question)
            return new StateUnknownItemSyntax(Advance());

        // `a` and `i` are the accumulator and index widths; `a` arrives as a register token.
        if (Kind is not (SyntaxKind.Identifier or SyntaxKind.Register))
        {
            Report(Catalogue.ExpectedStateItem.Says("a processor-state item"));
            return null;
        }

        var nameIndex = index;
        var name = Advance();

        // `dp = e` and `dbr = e` are the parts given a value with an `=`, and `dbr = [...]` a set
        // of banks; the value is read before the word is checked, so that a misspelled part is
        // the last news about the item. Which word takes a set is the signature's to say.
        if (Kind == SyntaxKind.Equals)
        {
            var equals = Advance();
            if (Kind == SyntaxKind.OpenBracket)
            {
                var openBracket = Advance();
                var ranges = Kind != SyntaxKind.CloseBracket ? ParseSeparatedList(ParseBankRange) : null;
                var closeBracket = Expect(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Says("`]`"));
                if (!SyntaxFacts.IsStateItem(name.Text, SyntaxKind.Equals))
                    Report(nameIndex, Catalogue.StateItemUnknown.Says(name.Text));
                return Own(new StateBanksItemSyntax(name, equals, openBracket, ranges, closeBracket));
            }
            var given = ParseExpression();
            if (!SyntaxFacts.IsStateItem(name.Text, SyntaxKind.Equals))
                Report(nameIndex, Catalogue.StateItemUnknown.Says(name.Text));

            // The item is what a misspelled word is an item of, so it takes what was said of it.
            return Own(new StateValueItemSyntax(name, equals, given));
        }

        GreenToken? suffix = Kind is SyntaxKind.Star or SyntaxKind.Question ? Advance() : null;
        if (!SyntaxFacts.IsStateItem(name.Text, suffix?.Kind ?? SyntaxKind.None))
        {
            Report(nameIndex, Catalogue.StateItemUnknown.Says(name.Text));
            return Own(new StateFlagItemSyntax(name, suffix));
        }
        else if (name.Text.Equals("args", StringComparison.OrdinalIgnoreCase))
        {
            // `args n`: how many bytes the caller pushes before the call.
            return new StateValueItemSyntax(name, null, ParseExpression());
        }
        else if (name.Text.Equals("inline", StringComparison.OrdinalIgnoreCase))
        {
            // `inline n` or `inline .strz`: how much data follows each call.
            return Kind == SyntaxKind.Directive && Current.Text.Equals(".strz", StringComparison.OrdinalIgnoreCase)
                ? new StateInlineItemSyntax(name, Advance())
                : new StateValueItemSyntax(name, null, ParseExpression());
        }
        else if (name.Text.Equals("keeps", StringComparison.OrdinalIgnoreCase))
        {
            return new StateKeepsItemSyntax(name, ParseKeptRegisters());
        }
        return new StateFlagItemSyntax(name, suffix);
    }

    /// <summary>
    /// The registers of a <c>keeps a, x</c>. A bare register name is an item nowhere else, so
    /// the list runs on through the commas that separate the signature's own items, and stops
    /// at the first comma that is followed by anything else.
    /// </summary>
    private GreenSeparatedList? ParseKeptRegisters()
    {
        if (!AtKeptRegister(index))
        {
            Report(Catalogue.ExpectedKeptRegisters.Says("the registers it keeps: `keeps a`, `keeps x, y`"));
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

    /// <summary>Whether the token at <paramref name="at"/> names a register a <c>keeps</c> may take.</summary>
    private bool AtKeptRegister(int at) =>
        at < tokens.Length && tokens[at].Kind is SyntaxKind.Identifier or SyntaxKind.Register
        && SyntaxFacts.IsKeptRegister(tokens[at].Text);

    private static bool LooksLikeAWidth(string text) =>
        text.Length > 1 && char.ToLowerInvariant(text[0]) is 'a' or 'i' && text[1..].All(char.IsAsciiDigit);
}
