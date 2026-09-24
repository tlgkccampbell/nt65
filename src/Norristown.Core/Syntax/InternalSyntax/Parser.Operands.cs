namespace Norristown.Syntax.InternalSyntax;

// Parses an instruction and its operand forms. Which forms each CPU and mnemonic allow is
// checked later, during layout, because the parser only reads them.
internal sealed partial class Parser
{
    private InstructionStatementSyntax ParseInstruction()
    {
        var mnemonic = Advance();
        return new InstructionStatementSyntax(mnemonic, AtEnd ? null : ParseOperand());
    }

    /// <summary>
    /// Parses an operand in any of its forms. Which forms each CPU and mnemonic allow is checked
    /// during layout.
    /// </summary>
    private OperandSyntax ParseOperand()
    {
        if (Kind == SyntaxKind.Hash)
            return ParseImmediate();

        // In `asl a` the `a` is the accumulator, while `a:` is an address-size prefix on what follows.
        if (Kind == SyntaxKind.Register && Next != SyntaxKind.Colon
            && Current.Text.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return new AccumulatorOperandSyntax(Advance());
        }

        if (Kind == SyntaxKind.OpenParen && TryParseIndirect() is { } indirect)
            return indirect;
        if (Kind == SyntaxKind.OpenBracket)
            return ParseLongIndirect();
        return ParseAddressOperand();
    }

    private ImmediateOperandSyntax ParseImmediate()
    {
        var hash = Advance();
        var value = ParseExpression();

        // `mvn #src, #dst` and `mvp` take two bank bytes, given as immediates.
        return Kind == SyntaxKind.Comma && Next == SyntaxKind.Hash
            ? new ImmediateOperandSyntax(hash, value, Advance(), Advance(), ParseExpression())
            : new ImmediateOperandSyntax(hash, value, null, null, null);
    }

    /// <summary>
    /// Parses <c>(expr)</c>, <c>(expr),y</c>, <c>(expr,x)</c> or <c>(expr,s),y</c>, or returns
    /// null when the parentheses turn out to be an ordinary expression, as in
    /// <c>lda (a + b) * 2</c>. Only a whole operand in parentheses is indirect, which is how ca65
    /// reads it too.
    /// </summary>
    private OperandSyntax? TryParseIndirect()
    {
        var start = index;
        var reportedBefore = reported;
        var pendingBefore = pending.Count;
        var openParen = Advance();
        var address = ParseExpression();

        (GreenToken Comma, GreenToken Register)? inner = null;
        if (Kind == SyntaxKind.Comma && (IsRegister(1, "x") || IsRegister(1, "s")))
            inner = (Advance(), Advance());
        if (Kind == SyntaxKind.CloseParen)
        {
            var closeParen = Advance();
            GreenToken? comma = null, register = null;
            if (Kind == SyntaxKind.Comma && IsRegister(1, "y"))
            {
                comma = Advance();
                register = Advance();
            }
            if (AtOperandEnd)
            {
                return inner is (var innerComma, var innerRegister)
                    ? new IndexedIndirectOperandSyntax(
                        openParen, address, innerComma, innerRegister, closeParen, comma, register)
                    : new IndirectOperandSyntax(openParen, address, closeParen, comma, register);
            }
        }

        // A failed attempt leaves no trace: its tokens are read again as an ordinary expression,
        // and the nodes it built are discarded along with the diagnostics reported while building
        // them, so no diagnostic about a missing token from the attempt survives it.
        index = start;
        if (pending.Count > pendingBefore)
            pending.RemoveRange(pendingBefore, pending.Count - pendingBefore);
        reported = reportedBefore;
        return null;
    }

    private LongIndirectOperandSyntax ParseLongIndirect()
    {
        var openBracket = Advance();
        var address = ParseExpression();
        var closeBracket = Kind == SyntaxKind.CloseBracket
            ? Advance()
            : Missing(SyntaxKind.CloseBracket, Catalogue.ExpectedBracket.Message("`]`"));
        return Kind == SyntaxKind.Comma && IsRegister(1, "y")
            ? new LongIndirectOperandSyntax(openBracket, address, closeBracket, Advance(), Advance())
            : new LongIndirectOperandSyntax(openBracket, address, closeBracket, null, null);
    }

    private AbsoluteOperandSyntax ParseAddressOperand()
    {
        var prefix = TryAddressPrefix();
        var address = ParseExpression();
        if (Kind != SyntaxKind.Comma)
            return new AbsoluteOperandSyntax(prefix, address, null, null, null);
        var comma = Advance();

        // `,x`, `,y` and `,s` index, and a second expression is what `bbr`/`bbs` take.
        return Kind == SyntaxKind.Register
            ? new AbsoluteOperandSyntax(prefix, address, comma, Advance(), null)
            : new AbsoluteOperandSyntax(prefix, address, comma, null, ParseExpression());
    }

    /// <summary>
    /// Parses a <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c> prefix, or returns null if there is
    /// none. The lexer emits a name and a <c>:</c>, and the parser decides by position. In operand
    /// position nothing else can appear there, so a name followed by <c>:</c> is a prefix.
    /// </summary>
    private AddressPrefixSyntax? TryAddressPrefix()
    {
        if (Next != SyntaxKind.Colon || Kind is not (SyntaxKind.Identifier or SyntaxKind.Register)
            || !SyntaxFacts.IsAddressPrefix(Current.Text))
        {
            return null;
        }
        return new AddressPrefixSyntax(Advance(), Advance());
    }

    private bool IsRegister(int offset, string name) =>
        index + offset < tokens.Length && tokens[index + offset].Kind == SyntaxKind.Register
        && tokens[index + offset].Text.Equals(name, StringComparison.OrdinalIgnoreCase);
}
