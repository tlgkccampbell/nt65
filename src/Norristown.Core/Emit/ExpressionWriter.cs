using System.Globalization;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;
using static Norristown.Emit.Ca65Directives;
using static Norristown.Emit.Ca65Numbers;

namespace Norristown.Emit;

/// <summary>
/// Rewrites a statement's expressions and operands for ca65, as edits recorded in a
/// <see cref="TokenRewriter"/> or as text on their own. Names become flat names, text becomes
/// bytes, a call becomes what it evaluates to, a macro parameter becomes its argument, and an
/// operand gains the address-size prefix the chosen mode needs. Nested operations are
/// parenthesized so that nothing depends on ca65's precedence. The <see cref="Emitter"/> decides
/// which lines to write, and this decides what the expressions in them say.
/// </summary>
/// <param name="model">The semantic model of the file being written.</param>
/// <param name="layout">The layout of the file being written.</param>
/// <param name="names">The flat names of the program.</param>
/// <param name="ends">The symbols this file writes an end label for.</param>
/// <param name="source">The path of the file's source.</param>
/// <param name="output">The path of the file's output.</param>
/// <param name="expansion">Returns the expansion the emitter is writing.</param>
/// <param name="notTranspiled">Reports a node for which nothing can be written.</param>
internal sealed class ExpressionWriter(
    SemanticModel model, CodeLayout layout, FlatNames names, IReadOnlySet<Symbol> ends,
    string source, string output, Func<Expansion?> expansion, Action<SyntaxNode> notTranspiled)
{
    /// <summary>
    /// Gets the expansion being written, which identifies the iteration of each enclosing
    /// repetition and the expansion of each enclosing macro.
    /// </summary>
    private Expansion? Expansion => expansion();

    /// <summary>
    /// Records the edits that make <paramref name="node"/>'s output differ from its source, as
    /// <see cref="Substitute(SyntaxNode, TokenRewriter, bool)"/> does for a node that stands on
    /// its own.
    /// </summary>
    internal void Substitute(SyntaxNode node, TokenRewriter rewriter) => Substitute(node, rewriter, nested: false);

    /// <summary>
    /// Returns an expression written out rather than edited in place. A call becomes what it
    /// stands for, and every nested operation is parenthesized, so nothing depends on how ca65
    /// reads precedence. Any comment the expression produces goes to <paramref name="comments"/>,
    /// the comment list of the line it is written into, when there is one. Otherwise, text after
    /// the expression on that line would end up inside its comment.
    /// </summary>
    internal string Rendered(SyntaxNode node, List<string>? comments = null)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return "(" + Rendered(parenthesized.Expression, comments) + ")";
            case BinaryExpressionSyntax binary when Evaluator.IsIn(binary.OperatorToken)
                && model.ValueOf(binary, Expansion).AsNumber() is null:
                return Compared(binary, comments) ?? binary.GetText().Trim();
            case BinaryExpressionSyntax binary when !Evaluator.IsIn(binary.OperatorToken):
                return $"({Rendered(binary.Left, comments)} {TokenRewriter.Ca65Operator(binary.OperatorToken)} {Rendered(binary.Right, comments)})";
            case UnaryExpressionSyntax unary:
                return $"({unary.OperatorToken.Text}{Rendered(unary.Operand, comments)})";
            default:
                break;
        }

        // Anything with a value nt65 knows is written as that value. Anything else keeps its own
        // spelling, with names flattened.
        if (model.ValueOf(node, Expansion).AsNumber() is { } value)
            return Constant(value);
        var rewriter = new TokenRewriter();
        Substitute(node, rewriter, nested: false);
        return rewriter.Inline(node, comments);
    }

    /// <summary>
    /// Writes one value of a slot as <see cref="Datum"/> returns it when it returns anything, and
    /// as the source has it otherwise. <paramref name="inBank"/> says whether the slot is an
    /// <c>.addr</c>, which holds the address within its bank, as <see cref="Narrows"/> describes.
    /// </summary>
    internal void InPlace(SyntaxNode value, int width, bool bigEndian, TokenRewriter rewriter, bool inBank = false)
    {
        if (Datum(value, width, bigEndian, rewriter.Comments) is { } text)
        {
            rewriter.Replace(value, text);
            return;
        }
        Substitute(value, rewriter, nested: false);
        Narrow(value, width, rewriter, inBank && RangeChecksInBank(null));
    }

    /// <summary>
    /// Returns one value of a slot <paramref name="width"/> bytes wide, written out rather than
    /// edited in place. It is what <see cref="Datum"/> returns when that returns anything, and
    /// what <see cref="Rendered"/> writes otherwise, inside <c>.lobyte()</c> or <c>.loword()</c>
    /// where <see cref="Narrows"/> says the slot needs it. Any comment either produces is dropped.
    /// <paramref name="inBank"/> says whether the slot is an <c>.addr</c>, which holds the address
    /// within its bank.
    /// </summary>
    internal string SlotText(SyntaxNode value, int width, bool bigEndian, bool inBank = false)
    {
        if (Datum(value, width, bigEndian, []) is { } text)
            return text;
        var rendered = Rendered(value);
        return Narrows(value, width, inBank && RangeChecksInBank(null)) ? LowPartOf(rendered, width) : rendered;
    }

    /// <summary>
    /// Returns one value of a slot <paramref name="width"/> bytes wide where ca65 cannot take it
    /// as it stands. A negative constant is its two's complement, and a big-endian value wider
    /// than a word, which ca65 has no directive for, is its bytes, high first. Returns null for a
    /// value written as it stands. The source text of a rewritten value is added to
    /// <paramref name="comments"/>.
    /// </summary>
    internal string? Datum(SyntaxNode value, int width, bool bigEndian, List<string> comments)
    {
        var known = model.ValueOf(value, Expansion).AsNumber();
        if (bigEndian && width > 2)
        {
            if (model.BytesOf(value, Expansion) is { Count: > 0 } text)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", text.SelectMany(b => HighFirst(b, width)));
            }
            if (known is { } number)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", HighFirst(number, width));
            }
            var rendered = Rendered(value, comments);
            var low = $".bankbyte({rendered}), .hibyte({rendered}), .lobyte({rendered})";
            return width == 3 ? low : $".lobyte(({rendered}) >> 24), {low}";
        }
        if (known is not (< 0 and var negative))
            return null;
        comments.Add(value.GetText().Trim());
        return Hex(negative & (long)(ulong.MaxValue >> (64 - (8 * width))), 2 * width);

        static IEnumerable<string> HighFirst(long number, int width) =>
            Enumerable.Range(0, width).Select(i => Hex((number >> (8 * (width - 1 - i))) & 0xff, 2));
    }

    /// <summary>
    /// Rewrites a negative constant in an immediate as its two's complement, and writes an
    /// immediate inside <c>.lobyte()</c> or <c>.loword()</c> where <see cref="LinkRange.Narrows"/>
    /// says ca65 or ld65 needs it there. An immediate is a byte or a word slot, as wide as the
    /// instruction makes it.
    /// </summary>
    internal void Immediate(StatementSyntax statement, int bytes, TokenRewriter rewriter)
    {
        if (statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax { Value: var value, SecondValue: null } })
            Immediate(value, bytes, rewriter);
    }

    /// <summary>
    /// Replaces a frame's member in a stack-relative operand with the offset from the stack
    /// pointer that the analysis counted for it here, because ca65 knows nothing of frames.
    /// </summary>
    internal void ReplaceFrameSlot(StatementSyntax statement, TokenRewriter rewriter)
    {
        if (layout.Of(statement, Expansion)?.Slot is not { } slot
            || statement is not InstructionStatementSyntax { Operand: { } operand })
        {
            return;
        }
        foreach (var name in operand.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            if (name.GlobalToken is not null || name.Names is not [var first, ..]
                || model.SymbolAt(first) is not { Kind: SymbolKind.Frame })
            {
                continue;
            }
            rewriter.ReplaceName(name, slot.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Replaces a <c>d:</c> operand with its offset into the direct page, given the D the
    /// analysis found. For example, with D at <c>$2100</c>, <c>lda d:$2105</c> is
    /// <c>lda z:$05</c>. ca65 has no <c>d:</c>, and knows nothing of D.
    /// </summary>
    internal void Direct(StatementSyntax statement, TokenRewriter rewriter)
    {
        if (layout.Of(statement, Expansion) is not { Direct: { } offset } laid
            || statement is not InstructionStatementSyntax { Operand: AbsoluteOperandSyntax { Prefix: { } sourcePrefix } operand })
        {
            return;
        }
        foreach (var token in sourcePrefix.ChildTokens)
            rewriter.Replacements[token.Position] = "";

        // The whole address goes, with the parentheses written around any operation in it.
        rewriter.Replace(operand.Address, (laid.Prefix ?? "") + Hex(offset, 2), around: false);
    }

    /// <summary>Returns the label just past a symbol's last byte, which is what <c>.endof</c> stands for.</summary>
    internal string EndLabelOf(Symbol symbol) => NameOf(symbol) + "__end";

    /// <summary>
    /// Formats text as its byte values. Empty text is no bytes, which is written as an empty
    /// string, as it is for an empty literal.
    /// </summary>
    private static string BytesText(IReadOnlyList<long> bytes) =>
        bytes.Count == 0 ? "\"\"" : string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));

    /// <summary>
    /// Returns whether <paramref name="text"/> is one parenthesized whole, whose first parenthesis
    /// closes at its last character.
    /// </summary>
    private static bool IsWrapped(string text)
    {
        if (!text.StartsWith('('))
            return false;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
                return i == text.Length - 1;
        }
        return false;
    }

    /// <summary>
    /// Returns <paramref name="text"/> inside <c>.lobyte()</c> for a slot of one byte, or inside
    /// <c>.loword()</c> for a wider one, without doubling the parentheses of text that is already
    /// one parenthesized whole.
    /// </summary>
    private static string LowPartOf(string text, int width)
    {
        var function = width == 1 ? ".lobyte" : ".loword";
        return IsWrapped(text) ? function + text : $"{function}({text})";
    }

    /// <summary>
    /// Returns ca65's operator for <paramref name="kind"/>, which is <c>.lobyte</c>,
    /// <c>.hibyte</c> or <c>.bankbyte</c>.
    /// </summary>
    private static string ByteOperator(BuiltinKind kind) => kind switch
    {
        BuiltinKind.Lobyte => "<",
        BuiltinKind.Hibyte => ">",
        _ => "^",
    };

    /// <summary>
    /// Writes <paramref name="text"/>, an operation, in place of a node that stands for a single
    /// value. Where the node is an operand of another operation, the text is parenthesized, so
    /// that <c>#&gt;player::hp</c> is <c>#&gt;(player+255)</c> rather than the high byte of
    /// <c>player</c> plus 255.
    /// </summary>
    private static void ReplaceOperation(SyntaxNode node, string text, TokenRewriter rewriter) =>
        rewriter.Replace(node, IsOperation(node.Parent) ? $"({text})" : text);

    /// <summary>Returns whether a node is an operation, whose text needs parentheses where it is an operand.</summary>
    private static bool IsOperation(SyntaxNode? node) => node is BinaryExpressionSyntax or UnaryExpressionSyntax;

    /// <summary>Returns the name a symbol has in the output, in the expansion being written.</summary>
    private string NameOf(Symbol symbol) => names.Of(symbol, Expansion);

    /// <summary>
    /// Returns whether the output writes <paramref name="value"/>, the value of a slot
    /// <paramref name="width"/> bytes wide, inside <c>.lobyte()</c> or <c>.loword()</c>. That is
    /// the case where <see cref="LinkRange.Narrows"/> says so. It is also the case for a slot that
    /// holds the address within its bank and that ca65 range-checks, as <paramref name="inBank"/>
    /// marks it, where the value names an address placed past $FFFF. The slot holds the value
    /// plus <paramref name="offset"/>.
    /// </summary>
    private bool Narrows(SyntaxNode value, int width, bool inBank, long offset = 0) =>
        LinkRange.Narrows(model, value, Expansion, width, offset)
        || (inBank && LinkRange.NamesWideAddress(model, value, Expansion, width));

    /// <summary>
    /// Returns whether ca65 range-checks a slot that holds an address within its bank, so that
    /// ld65 would refuse a label placed past $FFFF there. The slot is an <c>.addr</c> where
    /// <paramref name="mode"/> is null, and the two-byte address of an operand in
    /// <paramref name="mode"/> otherwise.
    /// </summary>
    /// <remarks>
    /// An <c>.addr</c> and every two-byte operand hold the address within its bank on every CPU.
    /// On the 65816, ca65 keeps only the low 16 bits of an <c>.addr</c> and of an operand in the
    /// absolute, absolute X, absolute Y and absolute indexed indirect modes. It range-checks every
    /// other slot, so <c>jmp (abs)</c> and <c>jml [abs]</c> are range-checked on the 65816 as
    /// well. <c>.loword()</c> gives ca65 the address within the bank wherever it range-checks.
    /// </remarks>
    private bool RangeChecksInBank(AddressingMode? mode) => mode switch
    {
        null or AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY
            or AddressingMode.AbsoluteIndirectX => layout.Cpu != Cpu.Wdc65816,
        AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectLong => true,
        _ => false,
    };

    /// <summary>
    /// Writes <paramref name="value"/>, the value of a slot <paramref name="width"/> bytes wide,
    /// inside <c>.lobyte()</c> or <c>.loword()</c> where <see cref="Narrows"/> says ca65 or ld65
    /// would otherwise refuse it for an address it names. The low part loses nothing, because the
    /// value always fits the slot or the slot holds the address within its bank, as
    /// <paramref name="inBank"/> says. The edits <paramref name="rewriter"/>
    /// already holds for the value are kept inside the call. Returns whether the value was
    /// written so.
    /// </summary>
    private bool Narrow(SyntaxNode value, int width, TokenRewriter rewriter, bool inBank = false)
    {
        if (!Narrows(value, width, inBank))
            return false;

        // The comments stay with the line rather than going inside the call.
        List<string> comments = [];
        var text = rewriter.Inline(value, comments);
        rewriter.Comments.AddRange(comments);
        rewriter.Replace(value, LowPartOf(text, width), around: false);
        return true;
    }

    /// <summary>
    /// Rewrites <paramref name="value"/>, the value of an immediate in an instruction of
    /// <paramref name="bytes"/> bytes, as its two's complement where it is a negative constant,
    /// and inside <c>.lobyte()</c> or <c>.loword()</c> where <see cref="Narrows"/> says ca65 or
    /// ld65 needs it there. Returns whether it rewrote the value.
    /// </summary>
    private bool Immediate(ExpressionSyntax value, int bytes, TokenRewriter rewriter)
    {
        if (bytes is not (2 or 3))
            return false;
        if (Datum(value, bytes - 1, bigEndian: false, rewriter.Comments) is not { } text)
            return Narrow(value, bytes - 1, rewriter);
        rewriter.Replace(value, text, around: false);
        return true;
    }

    /// <summary>
    /// Records the edits that make the output differ from the source. These are flat names, the
    /// address-size prefix the chosen mode needs, byte values for text, and the parentheses that
    /// keep the output from depending on ca65's precedence. An operation that is
    /// <paramref name="nested"/> in another gets parentheses of its own.
    /// </summary>
    private void Substitute(SyntaxNode node, TokenRewriter rewriter, bool nested)
    {
        switch (node)
        {
            case NameExpressionSyntax name:
                Name(name, rewriter);
                return;

            case LiteralExpressionSyntax literal when literal is StringExpressionSyntax or CharacterExpressionSyntax:
                Text(literal, rewriter);
                return;

            case CallExpressionSyntax call:
                Applied(call, rewriter);
                return;

            case BinaryExpressionSyntax binary when Evaluator.IsIn(binary.OperatorToken):
                Membership(binary, rewriter);
                return;

            // `wdm #n` is written as its bytes, which is what it is to every processor but the
            // emulator that hooks it.
            case InstructionStatementSyntax { Operand: ImmediateOperandSyntax hook } instruction
                when instruction.MnemonicKind == MnemonicKind.Wdm:
                rewriter.Replacements[instruction.Mnemonic.Position] = ".byte";
                rewriter.Replacements[hook.HashToken.Position] = "$42, ";
                break;

            case AbsoluteOperandSyntax operand:
                // In a macro body an `operand` parameter takes the place of a whole operand, so
                // the argument the call passed replaces the body's operand, including its prefix
                // and index. The prefix goes on last, outside any parentheses the expression was
                // given.
                if (Given(operand, rewriter))
                    return;
                foreach (var child in operand.ChildNodes)
                    Substitute(child, rewriter, nested: false);
                NarrowAddress(operand, operand.Address, rewriter);
                Prefix(operand, rewriter);
                return;

            // ca65 takes no `z:` or `a:` inside the parentheses or brackets of an indirect
            // operand, so the narrowing alone sizes the pointer's address.
            case IndirectOperandSyntax indirect:
                Pointer(indirect, indirect.Address, rewriter);
                return;
            case IndexedIndirectOperandSyntax indexed:
                Pointer(indexed, indexed.Address, rewriter);
                return;
            case LongIndirectOperandSyntax indirectLong:
                Pointer(indirectLong, indirectLong.Address, rewriter);
                return;

            case DataDirectiveSyntax directive:
                rewriter.TerminateStrz(directive);

                // An `.incbin` names a file rather than holding data, so its path is left a
                // path, pointed at the file from the output's location.
                if (Included(directive, rewriter))
                    return;

                // An element type's values, and a `.res` fill, are each written into a slot of a
                // fixed width.
                if (DataSyntax.IsElementType(directive) && directive.Tail is not BracedDataSyntax)
                {
                    rewriter.Replacements[directive.Directive.Position] = ForCa65(directive.Directive.DirectiveKind, directive.Directive.Text);
                    var (width, bigEndian) = ElementFormat(directive);
                    var inBank = directive.Directive.DirectiveKind == DirectiveKind.Addr;
                    foreach (var value in DataLengths.ElementsOf(directive))
                        InPlace(value, width, bigEndian, rewriter, inBank);
                    return;
                }
                if (directive.Directive.DirectiveKind is DirectiveKind.Res or DirectiveKind.Align
                    && directive.Tail is InlineDataSyntax { Values: [var count, .. var fills] })
                {
                    ReplaceCount(count, rewriter);
                    foreach (var fill in fills)
                        InPlace(fill, 1, bigEndian: false, rewriter);
                    return;
                }
                break;

            case DataValuesSyntax values
                when DataSyntax.DirectiveOfValues(values) is { IsRecord: false } of:
                var (valueWidth, valuesBigEndian) = ElementFormat(of);
                foreach (var value in values.Values)
                    InPlace(value, valueWidth, valuesBigEndian, rewriter, of.Directive.DirectiveKind == DirectiveKind.Addr);
                return;

            // The distance between two places in one data declaration is a constant nt65 has
            // worked out, and a constant is written as its value, never as text for ca65 to
            // work out again.
            case BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference
                when NamesAnAddress(difference) && Worth(difference).AsNumber() is { } distance:
                rewriter.Replace(difference, nested ? $"({Constant(distance)})" : Constant(distance));
                rewriter.Comments.Add(difference.GetText().Trim());
                return;

            case BinaryExpressionSyntax:
            case UnaryExpressionSyntax:
                foreach (var op in node.ChildTokens)
                {
                    if (TokenRewriter.Ca65Operator(op) is var spelled && spelled != op.Text)
                        rewriter.Replacements[op.Position] = spelled;
                }
                if (nested)
                {
                    var tokens = TokenRewriter.Tokens(node);
                    rewriter.Before[tokens[0].Position] = "(" + rewriter.Before.GetValueOrDefault(tokens[0].Position, "");
                    rewriter.After[tokens[^1].Position] = rewriter.After.GetValueOrDefault(tokens[^1].Position, "") + ")";
                }
                foreach (var child in node.ChildNodes)
                    Substitute(child, rewriter, nested: true);
                return;

            default:
                break;
        }

        foreach (var child in node.ChildNodes)
            Substitute(child, rewriter, nested: false);
    }

    /// <summary>
    /// Writes <c>value .in set</c> as its value, 1 or 0, where nt65 knows it. Where it does not,
    /// because the value is an address that only the linker places, it is written as ca65's
    /// comparisons, one for each item of the set.
    /// </summary>
    private void Membership(BinaryExpressionSyntax binary, TokenRewriter rewriter)
    {
        if (model.ValueOf(binary, Expansion).AsNumber() is { } value)
        {
            rewriter.Replace(binary, Constant(value));
            return;
        }
        if (Compared(binary, rewriter.Comments) is not { } compared)
        {
            notTranspiled(binary);
            return;
        }

        // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
        rewriter.Replace(binary, "+" + compared);
    }

    /// <summary>
    /// Returns <c>value .in set</c> as ca65's comparisons, joined with <c>||</c>, or null when the
    /// set is a list with an item whose value nt65 does not know. A list's items are written as
    /// their values, since their names belong to the file that declares the list.
    /// </summary>
    private string? Compared(BinaryExpressionSyntax binary, List<string>? comments)
    {
        var value = Rendered(binary.Left, comments);
        var tests = new List<string>();
        switch (binary.Right)
        {
            case SetExpressionSyntax set:
                foreach (var range in set.Items)
                {
                    tests.Add(range.Last is { } last
                        ? $"(({value} >= {Rendered(range.First, comments)}) && ({value} <= {Rendered(last, comments)}))"
                        : $"({value} = {Rendered(range.First, comments)})");
                }
                break;
            case NameExpressionSyntax name when model.SymbolOf(name, Expansion) is { Kind: SymbolKind.List } list:
                foreach (var item in list.Items)
                {
                    if (model.ValueOf(item).AsNumber() is not { } known)
                        return null;
                    tests.Add($"({value} = {Constant(known)})");
                }
                break;
            default:
                return null;
        }
        return tests.Count == 0 ? "0" : "(" + string.Join(" || ", tests) + ")";
    }

    /// <summary>
    /// Returns an argument written where the body named its parameter. It goes in as a
    /// parenthesized whole, so `value * 2` with the argument `1 + 2` is `(1 + 2) * 2`, which is
    /// six where ca65's textual substitution would give five. Likewise, `#&lt;value` with
    /// `label+1` is `#&lt;(label+1)`. The argument keeps its own spelling, because it is an
    /// expression, not a number, and a name in it is written the way the same name would be
    /// written anywhere else.
    /// </summary>
    private string Substituted(SyntaxNode argument, List<string>? comments = null)
    {
        var rewriter = new TokenRewriter();
        Substitute(argument, rewriter, nested: false);
        var text = rewriter.Inline(argument, comments);
        return IsOperation(argument) ? "(" + text + ")" : text;
    }

    /// <summary>
    /// Replaces a count that ca65 needs to know when it reaches the line, such as how much a
    /// <c>.res</c> reserves or what an <c>.align</c> aligns to. ca65 cannot know the count if a
    /// name in it is defined further down the output. A count that uses such a name is written
    /// as its value, with the source text in a comment beside it.
    /// </summary>
    private void ReplaceCount(SyntaxNode count, TokenRewriter rewriter)
    {
        var named = count.DescendantNodes().Prepend(count).OfType<NameExpressionSyntax>().Any(name =>
            name.Names is [.., var last] && model.SymbolAt(last) is { Kind: not (SymbolKind.Binding or SymbolKind.MacroParameter) });
        if (named && model.ValueOf(count, Expansion).AsNumber() is { } known)
        {
            rewriter.Comments.Add(count.GetText().Trim());
            rewriter.Replace(count, Constant(known));
            return;
        }
        Substitute(count, rewriter, nested: false);
    }

    /// <summary>
    /// Rewrites the path of an <c>.incbin</c> so that ca65 finds the file from the output rather
    /// than from the source. ca65 looks beside the file it is assembling, so the output assembles
    /// from any directory. Returns whether the directive was an <c>.incbin</c>.
    /// </summary>
    private bool Included(DataDirectiveSyntax directive, TokenRewriter rewriter)
    {
        if (directive.Directive.DirectiveKind != DirectiveKind.IncBin)
            return false;
        var values = directive.Tail is InlineDataSyntax inline ? inline.Values : default;
        if (values is [var path, ..]
            && model.ValueOf(path, Expansion) is { Kind: ValueKind.String, Text: { } named })
        {
            rewriter.Replace(path, "\"" + Paths.Relative(Paths.Directory(output), Paths.Beside(source, named)) + "\"");
        }
        foreach (var argument in values.Skip(1))
            Substitute(argument, rewriter, nested: false);
        return true;
    }

    /// <summary>
    /// Rewrites a name as ca65 needs it. Most names become the flat name of the symbol they
    /// refer to. A list becomes its items, a member path its offset, a macro parameter the
    /// argument given for it, a string its bytes, and a setting or a checked import its value.
    /// </summary>
    private void Name(NameExpressionSyntax name, TokenRewriter rewriter)
    {
        // A path names one symbol, and the whole of it becomes that symbol's flat name. A body is
        // written out in every file that calls its macro, so the symbol a name refers to comes
        // from resolving the whole program rather than this one file.
        if (name.Names is not [.., var last] || model.SymbolAt(last) is not { } named)
            return;
        var reference = named;

        // A path that ends in a repetition's binding names a different member on every iteration.
        if (named.Kind == SymbolKind.Binding && name.SimpleName is null)
        {
            if (model.SymbolOf(name, Expansion) is not { } namesake)
                return;
            reference = namesake;
        }

        // A list's name is replaced by its items wherever data uses it.
        if (model.ItemsOf(name) is { Count: > 0 } items)
        {
            rewriter.ReplaceName(name, string.Join(", ", items.Select(item => Rendered(item, rewriter.Comments))));
            rewriter.Comments.Add(name.GetText().Trim());
            return;
        }

        // A member is an offset. The offsets along the path are added up, on the address the
        // path starts from when it starts at an instance rather than at a type. An index along
        // the path adds whole elements to the same sum.
        if (reference.Kind == SymbolKind.Member || (name.IsIndexed && reference.IsAddress))
        {
            MemberPath(name, rewriter);
            return;
        }

        // A macro parameter is replaced by the argument the call passed it, as a parenthesized
        // whole, so `value * 2` with the argument `1 + 2` is 6 rather than 5.
        var symbol = reference;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            if (Parameter(symbol, rewriter.Comments) is not { } given)
                return;
            rewriter.ReplaceName(name, given);
            return;
        }

        // The name a repetition binds has a different value on every iteration, and the one
        // written is its value on the iteration being written.
        if (symbol.Kind == SymbolKind.Binding)
        {
            if (model.BindingsOf(Expansion)?.TryGetValue(symbol, out var bound) is not true)
                return;

            // A list item is written as it stands, with its own names substituted; a number
            // is written as its value on this iteration.
            var replacement = bound.Item is { } item ? Rendered(item, rewriter.Comments) : null;
            if (replacement is null && bound.Value.AsNumber() is { } number)
                replacement = Constant(number);
            if (replacement is null)
                return;

            rewriter.ReplaceName(name, replacement);

            // The binding's value is written into the line, so naming the binding in a comment
            // as well would only repeat it down every line an unrolled body writes.
            return;
        }

        // A string constant cannot be expressed in ca65, in this module or any other, so it is
        // written as the bytes of its text, as a literal is.
        if (symbol.Value.IsString)
        {
            if (model.BytesOf(name, Expansion) is { Count: > 0 } bytes)
            {
                rewriter.Replace(name, BytesText(bytes));
                rewriter.Comments.Add(name.GetText().Trim());
            }
            return;
        }

        // A setting and a checked import are written as their value, never by name. That way a
        // `-D` given to ca65 cannot collide with a setting, and a checked import is a value nt65
        // has already used in its own arithmetic.
        var byValue = (symbol.IsSetting || symbol.Kind == SymbolKind.ImportedConstant)
            && symbol.Value.AsNumber() is not null;
        rewriter.ReplaceName(name, byValue ? Constant(symbol.Value.Number) : NameOf(symbol));
        if (byValue)
            rewriter.Comments.Add(symbol.QualifiedName);
    }

    /// <summary>
    /// Returns the text a macro parameter is replaced by here, which is the operand the call
    /// passed, the expression it passed, or the word or number the parameter is bound to.
    /// </summary>
    private string? Parameter(Symbol parameter, List<string> comments)
    {
        if (model.BindingsOf(Expansion)?.TryGetValue(parameter, out var bound) is not true)
            return null;
        if (bound.Value.IsWord)
            return bound.Value.Text;
        if (bound.Argument is { Parameter.Kind: ParameterKind.Operand } given)
            return given.Operand is { } operand ? Substituted(operand, comments) : null;

        // A member of an enum is a constant, and constants are written as their values.
        if (bound is { Member: not null, Item: null } && bound.Value.AsNumber() is { } member)
            return Constant(member);
        return bound.Item is { } item ? Substituted(item, comments) : null;
    }

    /// <summary>
    /// Replaces a path through a type or an instance, including the elements any <c>[i]</c>
    /// along it steps over. Through a type it is a number, and through an instance it is that
    /// instance plus the offset, which ca65 and ld65 resolve. Either way the path it came from is kept
    /// in a comment.
    /// </summary>
    private void MemberPath(NameExpressionSyntax name, TokenRewriter rewriter)
    {
        Symbol? start = null;
        long offset = 0;
        foreach (var token in name.Names)
        {
            if (model.SymbolAt(token) is not { } part)
                continue;

            // A repetition's binding at the end of the path names the field of its member's name.
            if (part.Kind == SymbolKind.Binding && token == name.Names[^1])
                part = model.SymbolOf(name, Expansion) ?? part;
            if (part.Kind == SymbolKind.Member)
                offset += part.Value.AsNumber() ?? 0;
            else if (part.IsAddress)
                start ??= part;
        }
        foreach (var (part, index) in ElementIndexes.Of(name))
        {
            if (model.SymbolAt(part) is { } indexed && ElementIndexes.Stride(indexed) is { } stride
                && model.ValueOf(index.Index, Expansion).AsNumber() is { } element)
            {
                offset += element * stride;
            }
        }

        if (start is null)
            rewriter.Replace(name, Constant(offset));
        else if (offset == 0)
            rewriter.Replace(name, NameOf(start));
        else
            ReplaceOperation(name, $"{NameOf(start)}+{offset}", rewriter);
        rewriter.Comments.Add(name.GetText().Trim());
    }

    /// <summary>
    /// Returns the value of <paramref name="node"/> in the expansion being written, with cycle
    /// counts from the layout.
    /// </summary>
    private Value Worth(SyntaxNode node) => model.ValueOf(node, Expansion, cycles: layout.CyclesOf);

    /// <summary>Returns whether an expression names an address anywhere along any of its paths.</summary>
    private bool NamesAnAddress(SyntaxNode node) =>
        node.DescendantNodes().OfType<NameExpressionSyntax>()
            .Any(name => name.Names.Any(part => model.SymbolAt(part) is { IsAddress: true }));

    /// <summary>
    /// Writes a call out as what it evaluates to. A charmap applied to text becomes the bytes it
    /// maps the text to, and a function call becomes its value. A call nt65 cannot evaluate is
    /// reported rather than passed to ca65, which knows neither charmaps nor functions.
    /// </summary>
    private void Applied(CallExpressionSyntax call, TokenRewriter rewriter)
    {
        var tokens = TokenRewriter.Tokens(call);
        if (tokens.Count == 0)
            return;

        // A segment function such as `.loadof` is written as the symbol ld65 defines for that segment.
        if (SegmentFunctions.Of(call, model) is { } about)
        {
            rewriter.Replace(call, SegmentFunctions.LinkerName(about.Function, about.Segment));
            return;
        }

        // `.endof(f)` and `.spanof(f)` describe layout rather than shape, so they are written
        // as the addresses they are and resolved by ca65 and ld65. In a macro body, `f` is what
        // the call being written gave the parameter.
        if (Extents.Is(call, model, out var span, Expansion) && Extents.MeasuredBy(call) is { } named
            && model.SymbolOf(named, Expansion) is { } measured && (ends.Contains(measured) || measured.Tree != model.Tree))
        {
            rewriter.Replace(call, span ? $"({EndLabelOf(measured)} - {NameOf(measured)})" : EndLabelOf(measured));
            return;
        }

        // `.bankof(name)` is ca65's `.bank(name)`, which ld65 answers from the memory area the
        // name runs in. ca65 gives that call the address size of the name, which no byte holds, so
        // the bank is written as its low byte, which fits one.
        if (call.BuiltinKind == BuiltinKind.Bankof && call.Arguments.Arguments is [var banked])
        {
            rewriter.Replace(call, $".lobyte(.bank({Substituted(banked, rewriter.Comments)}))");
            return;
        }

        // `.exprof(p)` is replaced by the expression inside the operand the call passed as `p`
        // (`5` for `{#5}`, `ptr` for `{(ptr),y}`), unless it is a constant, which the path
        // below writes as a number.
        if (Semantics.Operands.IsExprOf(call) && Worth(call).AsNumber() is null
            && model.ExprOf(call, Expansion) is { } inner)
        {
            rewriter.Replace(call, "(" + Substituted(inner, rewriter.Comments) + ")");
            return;
        }

        // A call to a built-in, which has no declared callee, is written from what the
        // analysis works out for it.
        if (call.Callee is null)
        {
            // Text a built-in builds or chooses is written as its bytes, as a literal is, because
            // ca65 never sees text.
            if (Worth(call).IsString)
            {
                if (model.BytesOf(call, Expansion) is { } built)
                {
                    rewriter.Replace(call, BytesText(built));
                    rewriter.Comments.Add(call.GetText().Trim());
                }
                else
                {
                    notTranspiled(call);
                }
                return;
            }

            // An address `.select` or `.switch` chooses is written as the value it chose, which is
            // all ca65 sees.
            if (Worth(call).AsNumber() is null && model.ChosenBy(call, Expansion) is { } choice)
            {
                // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
                var chosen = Rendered(choice, rewriter.Comments);
                rewriter.Replace(call, chosen.StartsWith('(') ? "+" + chosen : chosen);
                return;
            }
            if (Worth(call).AsNumber() is { } builtin)
            {
                rewriter.Replace(call, Constant(builtin));
                return;
            }
            Substitute(call.Arguments, rewriter, nested: false);
            return;
        }

        string? text = null;
        if (model.BytesOf(call, Expansion) is { } bytes && (bytes.Count > 0 || Worth(call).IsString))
            text = BytesText(bytes);
        else if (model.ValueOf(call, Expansion).AsNumber() is { } value)
            text = Constant(value);
        // The call's own text goes in the comment below, so whatever its arguments would add to
        // the comment only repeats part of it.
        else if (LinkTime(call, null, [], []) is { } linked)
        {
            // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
            text = linked.StartsWith('(') && call.FirstAncestorOrSelf<AbsoluteOperandSyntax>() is { } operand
                && TokenRewriter.Tokens(operand)[0].Position == tokens[0].Position
                ? "+" + linked
                : linked;
        }

        if (text is null)
        {
            notTranspiled(call);
            return;
        }
        rewriter.Replace(call, text);
        rewriter.Comments.Add(call.GetText().Trim());
    }

    /// <summary>
    /// Returns a call to a <c>.func</c> whose value only the linker knows, written as the
    /// function's body with each parameter replaced by the argument given for it. ld65 then works
    /// the body out once the addresses in it are known. Each part of the body that does not depend
    /// on an address is written as its value. Returns null where the body uses an address in
    /// anything but an operator, which the analysis reports.
    /// </summary>
    /// <param name="call">The call to write.</param>
    /// <param name="outer">
    /// The parameters of the function whose body holds the call, with what each is given, or null
    /// for a call outside any function's body.
    /// </param>
    /// <param name="writing">The functions whose bodies are being written, which a call must not reenter.</param>
    /// <param name="comments">Receives any comment the arguments produce.</param>
    private string? LinkTime(
        CallExpressionSyntax call, IReadOnlyDictionary<Symbol, ParameterValue>? outer, HashSet<Symbol> writing, List<string> comments)
    {
        if (call.Callee is null || model.SymbolOf(call.Callee, Expansion) is not { Kind: SymbolKind.Func, Items: [var body, ..] } function
            || FunctionArguments.Match(function, call) is not { } arguments || !writing.Add(function))
        {
            return null;
        }
        try
        {
            var given = new Dictionary<Symbol, ParameterValue>();
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                var value = ValueIn(argument, outer);
                var text = value.AsNumber() is { } number ? Constant(number) : Linked(argument, outer, writing, comments);
                if (text is null)
                    return null;
                given[function.ParameterSymbols[i]] = new ParameterValue(value, text);
            }
            return Linked(body, given, writing, comments);
        }
        finally
        {
            writing.Remove(function);
        }
    }

    /// <summary>
    /// Returns part of a <c>.func</c> body written for ld65 to work out, with each parameter in
    /// <paramref name="parameters"/> replaced by what it is given, or null where ld65 cannot work
    /// it out. A part with a value nt65 knows is written as that value. An operation is
    /// parenthesized, so nothing depends on how ca65 reads precedence.
    /// </summary>
    /// <param name="node">The part of the body to write.</param>
    /// <param name="parameters">
    /// The parameters of the function whose body holds the part, with what each is given, or null
    /// for an argument outside any function's body.
    /// </param>
    /// <param name="writing">The functions whose bodies are being written.</param>
    /// <param name="comments">Receives any comment the part produces.</param>
    private string? Linked(
        SyntaxNode node, IReadOnlyDictionary<Symbol, ParameterValue>? parameters, HashSet<Symbol> writing, List<string> comments)
    {
        if (parameters is null)
            return Rendered(node, comments);
        if (ValueIn(node, parameters).AsNumber() is { } value)
            return Constant(value);
        switch (node)
        {
            // An operation is parenthesized where it is written, so the source's own parentheses
            // would only double them.
            case ParenthesizedExpressionSyntax parenthesized:
                return Linked(parenthesized.Expression, parameters, writing, comments);
            case BinaryExpressionSyntax binary when !Evaluator.IsIn(binary.OperatorToken):
                return Linked(binary.Left, parameters, writing, comments) is { } left
                    && Linked(binary.Right, parameters, writing, comments) is { } right
                    ? $"({left} {TokenRewriter.Ca65Operator(binary.OperatorToken)} {right})"
                    : null;
            case UnaryExpressionSyntax unary:
                return Linked(unary.Operand, parameters, writing, comments) is { } operand
                    ? $"({unary.OperatorToken.Text}{operand})"
                    : null;
            case NameExpressionSyntax name when model.SymbolOf(name, Expansion) is { } named
                && parameters.TryGetValue(named, out var given):
                return given.Text;
            case CallExpressionSyntax { Callee: { } callee } call
                when model.SymbolOf(callee, Expansion) is { Kind: SymbolKind.Func }:
                return LinkTime(call, parameters, writing, comments);

            // A byte of a value is written as ca65's byte operator, which ld65 works out.
            case CallExpressionSyntax
            {
                Callee: null, BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte,
                Arguments.Arguments: [var argument],
            } byteOf:
                return Linked(argument, parameters, writing, comments) is { } of
                    ? $"({ByteOperator(byteOf.BuiltinKind)}({of}))"
                    : null;
            default:
                break;
        }

        // Anything else is written as it would be outside the body, which it can be only when
        // it names none of the parameters.
        var usesParameter = node.DescendantNodes().Prepend(node).OfType<NameExpressionSyntax>()
            .Any(name => model.SymbolOf(name, Expansion) is { } named && parameters.ContainsKey(named));
        return usesParameter ? null : Rendered(node, comments);
    }

    /// <summary>
    /// Returns the value of part of a <c>.func</c> body with each parameter in
    /// <paramref name="parameters"/> taking the value it is given, or the value of an expression
    /// outside any body when <paramref name="parameters"/> is null.
    /// </summary>
    private Value ValueIn(SyntaxNode node, IReadOnlyDictionary<Symbol, ParameterValue>? parameters) =>
        parameters is null
            ? Worth(node)
            : model.ValueOf(node, parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value), Expansion);

    /// <summary>Replaces text with its byte values, keeping the source spelling in a comment.</summary>
    private void Text(LiteralExpressionSyntax literal, TokenRewriter rewriter)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes)
            return;
        rewriter.Replacements[literal.Token.Position] = BytesText(bytes);
        rewriter.Comments.Add(literal.GetText());
    }

    /// <summary>
    /// Writes an operand that names an <c>operand</c> parameter as the operand the call passed.
    /// Returns whether the operand named such a parameter.
    /// </summary>
    /// <param name="operand">The operand in the macro body.</param>
    /// <param name="rewriter">The edits of the line being written.</param>
    /// <param name="laid">
    /// The layout of the instruction the operand ends up in, or null to take it from the
    /// operand's own instruction. An argument that names a parameter of an enclosing macro passes
    /// the layout on, so that the operand it stands for is narrowed for the instruction.
    /// </param>
    private bool Given(AbsoluteOperandSyntax operand, TokenRewriter rewriter, LineLayout? laid = null)
    {
        if (Semantics.Operands.Substituted(model, operand, Expansion) is not { } given)
            return false;

        var own = operand.Parent is { } instruction ? layout.Of(instruction, Expansion) : null;
        var text = Argument(given, laid ?? own, rewriter.Comments);
        if (text is null)
            return false;

        // ca65 reads a `(` at the head of an operand as indirect addressing, so an expression
        // that starts with one gets a unary `+`, which changes nothing about what it is worth.
        var prefix = own?.Prefix ?? "";
        if (text.StartsWith('(') && (prefix.Length > 0 || given.IsAddress))
            text = "+" + text;

        var tokens = TokenRewriter.Tokens(operand);
        rewriter.Replacements[tokens[0].Position] = prefix + text;
        for (var i = 1; i < tokens.Count; i++)
            rewriter.Replacements[tokens[i].Position] = "";
        return true;
    }

    /// <summary>
    /// Returns the text of the operand a call passed, in the form the body asked for. That is the
    /// operand as it stands, the byte after it, or one byte of an immediate value. The operand as
    /// it stands, and the byte after it, are narrowed for <paramref name="laid"/>, the layout of
    /// the instruction they end up in, as <see cref="Whole"/> narrows an operand.
    /// </summary>
    private string? Argument(OperandSubstitution given, LineLayout? laid, List<string> comments)
    {
        // `.byteof` on an immediate is a byte of the value. It is a number where nt65 knows the
        // value, and a shift and a mask where only the linker will.
        if (given.ByteOf && given.Operand is ImmediateOperandSyntax)
        {
            if (given.Expression is not { } value)
                return null;
            if (model.ValueOf(value, Expansion).AsNumber() is { } known)
                return "#" + Constant((known >> (int)(8 * given.Offset)) & 0xff);
            var shifted = Substituted(value, comments);
            return given.Offset == 0
                ? $"#({shifted} & $ff)"
                : $"#(({shifted} >> {8 * given.Offset}) & $ff)";
        }

        // Anything else is the argument's own operand, as it stands when the body named it
        // whole, and with `+ n` on its expression when the body asked for a later byte. The sum
        // is narrowed for the instruction as the operand itself would be.
        var offset = given.Offset;
        if (offset == 0)
            return Whole(given.Operand, laid, comments);
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var address = Substituted(addressed, comments);
        var sum = offset > 0 ? $"{address}+{offset}" : $"{address}{offset}";
        if (laid is { Mode: { } mode } && NarrowedWidth(addressed, mode, offset) is { } width)
            sum = LowPartOf(sum, width);
        return sum + index;
    }

    /// <summary>
    /// Returns the text of an operand a call passed whole, written as an operand written in place
    /// would be in the instruction laid out as <paramref name="laid"/>. Its immediate is
    /// rewritten as <see cref="Immediate(StatementSyntax, int, TokenRewriter)"/> rewrites one,
    /// and its address is narrowed as <see cref="NarrowAddress(SyntaxNode, AddressingMode, TokenRewriter)"/>
    /// narrows one. An expression passed as the operand is parenthesized unless it is narrowed,
    /// as <see cref="Substituted"/> parenthesizes it.
    /// </summary>
    private string Whole(SyntaxNode operand, LineLayout? laid, List<string> comments)
    {
        var rewriter = new TokenRewriter();
        if (operand is AbsoluteOperandSyntax passedOn && Given(passedOn, rewriter, laid))
            return rewriter.Inline(operand, comments);

        Substitute(operand, rewriter, nested: false);
        var narrowed = laid is { Mode: { } mode } && operand switch
        {
            ImmediateOperandSyntax { Value: var value, SecondValue: null } => Immediate(value, laid.Length, rewriter),
            ImmediateOperandSyntax => false,
            _ => CodeLayout.Expression(operand) is { } address && NarrowAddress(address, mode, rewriter),
        };
        var text = rewriter.Inline(operand, comments);
        return !narrowed && IsOperation(operand) ? "(" + text + ")" : text;
    }

    /// <summary>
    /// Writes the address of an operand inside <c>.lobyte()</c> or <c>.loword()</c> where ca65 or
    /// ld65 would otherwise refuse it, in the mode its instruction is laid out in, as
    /// <see cref="NarrowAddress(SyntaxNode, AddressingMode, TokenRewriter)"/> decides.
    /// </summary>
    private void NarrowAddress(OperandSyntax operand, ExpressionSyntax address, TokenRewriter rewriter)
    {
        if (operand.Parent is { } instruction && layout.Of(instruction, Expansion)?.Mode is { } mode)
            NarrowAddress(address, mode, rewriter);
    }

    /// <summary>
    /// Writes the address of an operand in <paramref name="mode"/> inside <c>.lobyte()</c> or
    /// <c>.loword()</c> where ca65 or ld65 would otherwise refuse it, and returns whether it did.
    /// A one-byte address is written so where <see cref="LinkRange.Narrows"/> says so, as the run
    /// address of a zero-page segment is, which the output imports as absolute. A two-byte
    /// address is written so where it names an address placed past $FFFF in a mode that
    /// <see cref="RangeChecksInBank"/> says ca65 range-checks.
    /// </summary>
    private bool NarrowAddress(SyntaxNode address, AddressingMode mode, TokenRewriter rewriter) =>
        NarrowedWidth(address, mode) is { } width && Narrow(address, width, rewriter, inBank: width == 2);

    /// <summary>
    /// Returns the width of the low part the address of an operand in <paramref name="mode"/> is
    /// written as, as <see cref="NarrowAddress(SyntaxNode, AddressingMode, TokenRewriter)"/>
    /// decides, or null where it is written as it stands. The operand addresses
    /// <paramref name="address"/> plus <paramref name="offset"/>.
    /// </summary>
    private int? NarrowedWidth(SyntaxNode address, AddressingMode mode, long offset = 0) =>
        Instructions.Width(mode) switch
        {
            AddressSize.ZeroPage when Narrows(address, 1, inBank: false, offset) => 1,
            AddressSize.Absolute when RangeChecksInBank(mode) && Narrows(address, 2, inBank: true, offset) => 2,
            _ => null,
        };

    /// <summary>
    /// Records the edits for an indirect operand, whose <paramref name="address"/> is the address
    /// of the pointer, as <see cref="NarrowAddress(OperandSyntax, ExpressionSyntax, TokenRewriter)"/>
    /// narrows it.
    /// </summary>
    private void Pointer(OperandSyntax operand, ExpressionSyntax address, TokenRewriter rewriter)
    {
        foreach (var child in operand.ChildNodes)
            Substitute(child, rewriter, nested: false);
        NarrowAddress(operand, address, rewriter);
    }

    /// <summary>Writes the <c>z:</c> or <c>a:</c> that shows which mode was chosen.</summary>
    private void Prefix(AbsoluteOperandSyntax operand, TokenRewriter rewriter)
    {
        var instruction = operand.Parent;
        if (instruction is null)
            return;
        var chosen = layout.Of(instruction, Expansion)?.Prefix;
        var prefix = chosen ?? "";
        var sourcePrefix = operand.Prefix;
        if (chosen is null && sourcePrefix is not null)
            return;
        var tokens = TokenRewriter.Tokens(operand.Address);

        // ca65 reads a `(` at the head of an operand, or straight after a prefix, as indirect
        // addressing. An expression that starts with one therefore gets a unary `+` in front of
        // it. Examples are `lda (hi + lo) * 2`, which the language allows,
        // `jml (bank << 16) | .loword(f)`, and an expression written out with parentheses around
        // its first operation. The `+` changes nothing and keeps the operand an expression. The
        // `(` can also come from an edit, such as a member path written as a parenthesized sum.
        var opens = tokens.Count > 0
            && (rewriter.Before.GetValueOrDefault(tokens[0].Position, "")
                + rewriter.Replacements.GetValueOrDefault(tokens[0].Position, tokens[0].Text)).StartsWith('(');
        var text = opens ? prefix + "+" : prefix;

        if (sourcePrefix is not null)
        {
            // The source already has a prefix, so it is replaced with the one chosen, in case an
            // expression made the operand wider.
            foreach (var token in sourcePrefix.ChildTokens)
                rewriter.Replacements[token.Position] = "";
            rewriter.Replacements[sourcePrefix.Name.Position] = text;
            return;
        }
        if (tokens.Count > 0)
            rewriter.Before[tokens[0].Position] = text + rewriter.Before.GetValueOrDefault(tokens[0].Position, "");
    }

    /// <summary>
    /// Represents what a <c>.func</c> parameter is given in a call that is written for ld65, as
    /// the value nt65 knows for it and the text written in its place.
    /// </summary>
    private readonly record struct ParameterValue(Value Value, string Text);
}
