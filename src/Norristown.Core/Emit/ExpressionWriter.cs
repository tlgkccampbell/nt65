using System.Globalization;
using Norristown.Layout;
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
    /// as the source has it otherwise.
    /// </summary>
    internal void InPlace(SyntaxNode value, int width, bool bigEndian, TokenRewriter rewriter)
    {
        if (Datum(value, width, bigEndian, rewriter.Comments) is { } text)
            rewriter.Replace(value, text);
        else
            Substitute(value, rewriter, nested: false);
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
    /// Rewrites a negative constant in an immediate as its two's complement. An immediate is a
    /// byte or a word slot, as wide as the instruction makes it.
    /// </summary>
    internal void Immediate(StatementSyntax statement, int bytes, TokenRewriter rewriter)
    {
        if (statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax { Value: var value, SecondValue: null } }
            && bytes is 2 or 3 && Datum(value, bytes - 1, bigEndian: false, rewriter.Comments) is { } text)
        {
            rewriter.Replace(value, text, around: false);
        }
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
    /// Writes <paramref name="text"/>, an operation, in place of a node that stands for a single
    /// value. Where the node is an operand of another operation, the text is parenthesized, so
    /// that <c>#&gt;player::hp</c> is <c>#&gt;(player+255)</c> rather than the high byte of
    /// <c>player</c> plus 255.
    /// </summary>
    private static void ReplaceOperation(SyntaxNode node, string text, TokenRewriter rewriter) =>
        rewriter.Replace(node, node.Parent is BinaryExpressionSyntax or UnaryExpressionSyntax ? $"({text})" : text);

    /// <summary>Returns the name a symbol has in the output, in the expansion being written.</summary>
    private string NameOf(Symbol symbol) => names.Of(symbol, Expansion);

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
                Prefix(operand, rewriter);
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
                    foreach (var value in DataLengths.ElementsOf(directive))
                        InPlace(value, width, bigEndian, rewriter);
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
                    InPlace(value, valueWidth, valuesBigEndian, rewriter);
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
        return argument is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? "(" + text + ")"
            : text;
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
                rewriter.Replace(name, string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2))));
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
        // as the addresses they are and resolved by ca65 and ld65.
        if (Extents.Is(call, model, out var span) && Extents.MeasuredBy(call) is { } named
            && model.SymbolOf(named) is { } measured && (ends.Contains(measured) || measured.Tree != model.Tree))
        {
            rewriter.Replace(call, span ? $"({EndLabelOf(measured)} - {NameOf(measured)})" : EndLabelOf(measured));
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

        if (text is null)
        {
            notTranspiled(call);
            return;
        }
        rewriter.Replace(call, text);
        rewriter.Comments.Add(call.GetText().Trim());
    }

    /// <summary>Replaces text with its byte values, keeping the source spelling in a comment.</summary>
    private void Text(LiteralExpressionSyntax literal, TokenRewriter rewriter)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes)
            return;
        rewriter.Replacements[literal.Token.Position] =
            string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        rewriter.Comments.Add(literal.GetText());
    }

    /// <summary>
    /// Writes an operand that names an <c>operand</c> parameter as the operand the call passed.
    /// Returns whether the operand named such a parameter.
    /// </summary>
    private bool Given(AbsoluteOperandSyntax operand, TokenRewriter rewriter)
    {
        if (Semantics.Operands.Substituted(model, operand, Expansion) is not { } given)
            return false;

        var text = Argument(given, rewriter.Comments);
        if (text is null)
            return false;

        // ca65 reads a `(` at the head of an operand as indirect addressing, so an expression
        // that starts with one gets a unary `+`, which changes nothing about what it is worth.
        var prefix = operand.Parent is { } instruction ? layout.Of(instruction, Expansion)?.Prefix ?? "" : "";
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
    /// operand as it stands, the byte after it, or one byte of an immediate value.
    /// </summary>
    private string? Argument(OperandSubstitution given, List<string> comments)
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
        // whole, and with `+ n` on its expression when the body asked for a later byte.
        var offset = given.Offset;
        if (offset == 0)
            return Substituted(given.Operand, comments);
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var address = Substituted(addressed, comments);
        return offset > 0 ? $"{address}+{offset}{index}" : $"{address}{offset}{index}";
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
}
