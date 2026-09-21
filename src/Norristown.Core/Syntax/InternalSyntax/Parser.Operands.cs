namespace Norristown.Syntax.InternalSyntax;

// An instruction and the operand forms. Which ones each CPU and mnemonic allow is
// layout's to say; here they are only read.
internal sealed partial class Parser
{
    private InstructionStatementSyntax ParseInstruction()
    {
        var mnemonic = Advance();
        return new InstructionStatementSyntax(mnemonic, AtEnd ? null : ParseOperand());
    }

    /// <summary>The operand forms. Which ones each CPU and mnemonic allow is layout's to say.</summary>
    private OperandSyntax ParseOperand()
    {
        if (Kind == SyntaxKind.Hash)
            return ParseImmediate();

        // `asl a` is the accumulator; `a:` is an address-size prefix on what follows.
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

        // `mvn #src, #dst` and `mvp` take two bank bytes, written as immediates.
        return Kind == SyntaxKind.Comma && Next == SyntaxKind.Hash
            ? new ImmediateOperandSyntax(hash, value, Advance(), Advance(), ParseExpression())
            : new ImmediateOperandSyntax(hash, value, null, null, null);
    }

    /// <summary>
    /// <c>(expr)</c>, <c>(expr),y</c>, <c>(expr,x)</c> and <c>(expr,s),y</c>, or null when the
    /// parentheses turn out to be an ordinary expression: <c>lda (a + b) * 2</c> is not
    /// indirect. Only a whole operand in parentheses is, which is how ca65 reads it too.
    /// </summary>
    private OperandSyntax? TryParseIndirect()
    {
        var start = index;
        var said = reported;
        var placed = pending.Count;
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

        // An attempt that comes to nothing leaves nothing: the tokens it read are read again as
        // an ordinary expression, and the nodes it built go with the diagnostics they were given,
        // so no missing token it stood in for outlives it.
        index = start;
        if (pending.Count > placed)
            pending.RemoveRange(placed, pending.Count - placed);
        reported = said;
        return null;
    }

    private LongIndirectOperandSyntax ParseLongIndirect()
    {
        var openBracket = Advance();
        var address = ParseExpression();
        var closeBracket = Kind == SyntaxKind.CloseBracket
            ? Advance()
            : Missing(SyntaxKind.CloseBracket, "expected `]`");
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

        // `,x`, `,y` and `,s` index; a second expression is what `bbr`/`bbs` take.
        return Kind == SyntaxKind.Register
            ? new AbsoluteOperandSyntax(prefix, address, comma, Advance(), null)
            : new AbsoluteOperandSyntax(prefix, address, comma, null, ParseExpression());
    }

    /// <summary>
    /// <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c>. The lexer emits a name and a <c>:</c>
    /// and the parser decides by position; here, in operand position, nothing else can
    /// be written, so a name followed by <c>:</c> is a prefix.
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
