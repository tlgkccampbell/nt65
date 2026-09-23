using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// A macro call written out as nt65: the body with the arguments in place, as the programmer
/// would have written it by hand. It is not the ca65 the emitter writes — that is what the
/// output view answers — and it is not flattened: a call inside the body stays a call, because
/// a fully written-out nest of macros is unreadable and nobody wrote it.
/// <para>
/// Choices the body makes based on its arguments are resolved here, because the expanded text
/// has no way to leave them open: an <c>.if</c> over a <c>one</c> parameter becomes the branch it takes, and an
/// <c>.each</c> over a <c>list</c> parameter becomes its iterations, since nt65 has no way to write
/// a call's arguments as a list literal.
/// </para>
/// </summary>
internal sealed class MacroExpansion
{
    /// <summary>One level of the expansion's own indentation.</summary>
    private const string Step = "    ";

    private readonly ProgramAnalysis analysis;
    private readonly SemanticModel model;
    private readonly bool all;
    private readonly List<string> lines = [];
    private readonly List<Link> links = [];
    private string? refusal;

    private MacroExpansion(ProgramAnalysis analysis, SemanticModel model, Symbol macro, MacroCallSyntax call, bool all)
    {
        this.analysis = analysis;
        this.model = model;
        this.all = all;
        Macro = macro;
        Call = call;
    }

    /// <summary>The macro the call names.</summary>
    public Symbol Macro { get; }

    /// <summary>The call itself.</summary>
    public MacroCallSyntax Call { get; }

    /// <summary>The expansion, one line at a time.</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>
    /// The macro calls left unexpanded, in the order they appear, each with the index path that
    /// asks for its expansion.
    /// </summary>
    public IReadOnlyList<Link> Links => links;

    /// <summary>How many bytes the call assembles to, taken from the same layout the build uses.</summary>
    public int Bytes { get; private init; }

    /// <summary>What it costs to run, or null where none of it is code.</summary>
    public CycleCount? Cycles { get; private init; }

    /// <summary>Why it cannot be written into the file where it is called, or null when it can.</summary>
    public string? Refusal => refusal;

    /// <summary>
    /// The call on the line <paramref name="position"/> is in, written out, or null where the
    /// line holds no call or the program does not know what it names.
    /// </summary>
    /// <param name="analysis">The program, for what the call lays out to.</param>
    /// <param name="model">The file the call is in.</param>
    /// <param name="position">Where in that file's text.</param>
    /// <param name="into">
    /// A path of indices: at each level, which unexpanded call to expand further, by its position
    /// among the calls left unexpanded at that level. Empty to expand only the first level, which
    /// is what a view opens with.
    /// </param>
    /// <param name="all">Whether to expand every nested call, however deep.</param>
    public static MacroExpansion? At(
        ProgramAnalysis analysis, SemanticModel model, int position,
        IReadOnlyList<int>? into = null, bool all = false) =>
        CallAt(model, position) is { } call ? Of(analysis, model, call, into, all) : null;

    /// <summary>The same, for a caller that already has the call.</summary>
    public static MacroExpansion? Of(
        ProgramAnalysis analysis, SemanticModel model, MacroCallSyntax call,
        IReadOnlyList<int>? into = null, bool all = false)
    {
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition } macro)
            return null;
        var (bytes, cycles) = Laid(analysis, model, call);
        var written = new MacroExpansion(analysis, model, macro, call, all) { Bytes = bytes, Cycles = cycles };
        written.Body(definition, Expansion.Of(null, call, definition), "", into ?? [], []);
        return written;
    }

    /// <summary>
    /// The call the line at <paramref name="position"/> holds, whether it stands alone or
    /// follows a label, or null for a line that holds none.
    /// </summary>
    public static MacroCallSyntax? CallAt(SemanticModel model, int position)
    {
        var tree = model.Tree;
        if (position < 0 || position > tree.Text.Length || tree.LineCount == 0)
            return null;
        return Macros.CallIn(tree.GetLine(tree.GetLineIndex(position)).Statement);
    }

    /// <summary>
    /// A one-line summary of what the call expands to, as the hover's summary row shows it: the
    /// number of lines, then bytes, then cycles. Cycles are left out where the expansion writes
    /// no code.
    /// </summary>
    public string Becomes()
    {
        var written = lines.Count == 1 ? "1 line" : $"{lines.Count} lines";
        var bytes = Bytes == 1 ? "1 byte" : $"{Bytes} bytes";
        var cycles = Cycles is { } count
            ? $" · {count} cycle{(count is { IsExact: true, Least: 1 } ? "" : "s")}"
            : "";
        return $"{written} · {bytes}{cycles}";
    }

    /// <summary>The same summary, as the sentence the expansion view opens with.</summary>
    public string Summary() => $"expands to {Becomes()}";

    /// <summary>The bytes and cycles the call assembles to, taken from the layout the build uses.</summary>
    private static (int Bytes, CycleCount? Cycles) Laid(
        ProgramAnalysis analysis, SemanticModel model, MacroCallSyntax call)
    {
        if (analysis.LayoutFor(model.Tree.Path) is not { } layout)
            return (0, null);
        var bytes = 0;
        CycleCount? cycles = null;
        foreach (var step in layout.Steps)
        {
            if (step.IsMarker || !Within(step.On, call) || layout.Of(step.Statement, step.On) is not { } written)
                continue;
            if (written.Length > 0)
                bytes += written.Length;
            if (written.Cycles is { } count)
                cycles = cycles is { } running ? running + count : count;
        }
        return (bytes, cycles);
    }

    /// <summary>
    /// Whether a macro body declares any names of its own. Each expansion gets its own copy of
    /// those names, so two expansions written out in the same place would declare them twice.
    /// The line that opens the block declares the macro and its parameters and is not counted as
    /// part of the body.
    /// </summary>
    public static bool Declares(ProgramAnalysis analysis, BlockSyntax definition) =>
        analysis.ModelFor(definition.Tree.Path) is { } declaring
        && declaring.Symbols.Any(symbol => symbol.Tree == definition.Tree
            && symbol.NameSpan.Start >= definition.Opener.FullSpan.End
            && symbol.NameSpan.Start < definition.FullSpan.End);

    /// <summary>
    /// Whether a line emitted under expansion <paramref name="on"/> comes from inside
    /// <paramref name="call"/>'s expansion, at any depth of nesting.
    /// </summary>
    private static bool Within(Expansion? on, MacroCallSyntax call)
    {
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Call == call)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes out the lines of one block at <paramref name="indent"/>.
    /// <paramref name="into"/> is which call to expand further at this level and below, and
    /// <paramref name="reached"/> is the index path to this level, which each link records.
    /// </summary>
    private void Body(
        BlockSyntax block, Expansion at, string indent, IReadOnlyList<int> into, IReadOnlyList<int> reached) =>
        Members(Macros.LinesOf(block), at, indent, into, reached, new Counter());

    /// <summary>
    /// The same, for lines that are not a whole block: a block argument's contents, or the
    /// branch of an <c>.if</c> chain this call takes.
    /// </summary>
    private void Members(
        IReadOnlyList<SyntaxNode> members, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        var chain = new ConditionChain();
        foreach (var member in members)
        {
            if (member is BlockSyntax block)
            {
                Block(block, at, indent, into, reached, counter, chain);
                continue;
            }
            chain.Break();
            if (member is LineSyntax line)
                Statement(line, at, indent, into, reached, counter);
        }
    }

    /// <summary>One block of a body: a chain, a repetition, a call with a block, or a plain block.</summary>
    private void Block(
        BlockSyntax block, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter, ConditionChain chain)
    {
        // Resolve conditions on the arguments here: a programmer writing this call out by hand
        // would have written only the branch it takes.
        if (block.BlockKind == BlockKind.If)
        {
            if (chain.Includes(model, block, at))
                Members(Macros.LinesOf(block), at, indent, into, reached, counter);
            return;
        }
        chain.Break();

        // nt65 has no way to write a call's arguments as a list, so a repetition over one is
        // written out iteration by iteration; so is every other repetition, since the reader
        // asked what this call becomes rather than what the body says.
        if (block.BlockKind is BlockKind.Repeat or BlockKind.Each or BlockKind.MultiProc)
        {
            foreach (var turn in Repetitions.Of(model, block, at, null))
                Members(Macros.LinesOf(block), turn, indent, into, reached, counter);
            return;
        }

        if (block.BlockKind == BlockKind.MacroBlock)
        {
            // A `} name {` continuation is a sibling of the block it continues, and was already
            // written out with the call that opened the chain; only the chain's first block
            // holds the call.
            if (Macros.CallIn(block.Opener.Statement) is { } call)
                Called(block, call, at, indent, into, reached, counter);
            return;
        }

        Written(block.Opener.Statement, at, indent);
        Members(Macros.LinesOf(block), at, indent + Step, into, reached, counter);
        Emit(indent + "}");
    }

    /// <summary>One line of a body that opens no block.</summary>
    private void Statement(
        LineSyntax line, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        switch (line.Statement)
        {
            case BlockCloseLineSyntax or BlankLineSyntax:
                return;

            // A line naming a `block` parameter writes out the caller's own code, which needs
            // no substitution: it was written where it stands.
            case BlockSpliceSyntax splice:
                Spliced(splice, at, indent, into, reached, counter);
                return;
            case MacroCallSyntax call:
                Called(null, call, at, indent, into, reached, counter);
                return;
            case LabeledLineSyntax { Statement: MacroCallSyntax inner } labeled:
                Emit(indent + labeled.Label.GetText().Trim());
                Called(null, inner, at, indent, into, reached, counter);
                return;
            default:
                Written(line.Statement, at, indent);
                return;
        }
    }

    /// <summary>The lines a <c>block</c> argument gives, written where the body names it.</summary>
    private void Spliced(
        BlockSpliceSyntax splice, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        if (model.SymbolAt(splice.Name) is not { Parameter: { IsBlock: true } parameter })
        {
            Refuse($"`{splice.Name.Text}` is not a `block` parameter of this macro");
            return;
        }

        // A block argument the call omitted writes nothing; this is the case `.empty` tests for.
        if (model.ArgumentFor(parameter.Symbol, at) is not { Block: { } block })
            return;
        Members(Macros.LinesOf(block), Expansion.Spliced(at, splice, block), indent, into, reached, counter);
    }

    /// <summary>
    /// A call inside the body. It is left as a call — one level at a time — unless it is the one
    /// the reader asked to see, or everything was asked for.
    /// </summary>
    private void Called(
        BlockSyntax? opened, MacroCallSyntax call, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        var index = counter.Next();
        IReadOnlyList<int> way = [.. reached, index];
        var chosen = into is [var first, ..] && first == index;
        if (all || chosen)
        {
            if (model.MacroAt(call) is not { Definition: BlockSyntax definition })
            {
                Refuse($"no macro named `{call.Name.Text}!` is declared in this program");
                return;
            }
            if (Expansion.Expanding(at, definition))
            {
                Refuse($"`{call.Name.Text}!` calls itself, directly or through another macro, so its expansion never ends");
                return;
            }

            // Each expansion has its own locals, so two of them written out in one body would
            // declare the same name twice; an anonymous scope is inline code and keeps them
            // apart, which is what the language offers for exactly this.
            var inner = Expansion.Of(at, call, definition);
            IReadOnlyList<int> next = chosen ? [.. into.Skip(1)] : [];
            if (!Declares(analysis, definition))
            {
                Body(definition, inner, indent, next, way);
                return;
            }
            Emit(indent + ".scope {");
            Body(definition, inner, indent + Step, next, way);
            Emit(indent + "}");
            return;
        }

        // Left unexpanded: write the call line itself, then the block arguments under it, which
        // are code of this body rather than of the called macro and are written out like any
        // other line of it.
        var written = Written(call, at, indent);
        links.Add(new Link(lines.Count - 1, written.TrimEnd('{').Trim(), way));
        if (opened is null)
            return;
        Members(Macros.LinesOf(opened), at, indent + Step, [], way, new Counter());
        foreach (var next in Continuations(opened))
        {
            Emit(indent + next.Opener.Statement.GetText().Trim());
            Members(Macros.LinesOf(next), at, indent + Step, [], way, new Counter());
        }
        Emit(indent + "}");
    }

    /// <summary>The <c>} name {</c> blocks that carry on a call's block arguments.</summary>
    private static IEnumerable<BlockSyntax> Continuations(BlockSyntax opened)
    {
        if (opened.Parent is not { } container)
            yield break;
        foreach (var sibling in container.ChildNodes.SkipWhile(node => node != opened).Skip(1))
        {
            if (sibling is not BlockSyntax { BlockKind: BlockKind.MacroBlock, Opener.Statement: BlockContinuationSyntax } next)
                yield break;
            yield return next;
        }
    }

    /// <summary>One statement as it would have been written by hand, at <paramref name="indent"/>.</summary>
    private string Written(SyntaxNode statement, Expansion at, string indent)
    {
        var text = Substituted(statement, at);
        if (text.Length > 0)
            Emit(indent + text);
        return text;
    }

    /// <summary>
    /// One statement's text, with each parameter name replaced by the argument the call passed
    /// for it.
    /// <para>
    /// An <c>operand</c> parameter stands for a whole operand, and the <c>+ n</c> and
    /// <c>.byteof</c> a body may write on one are worked out here rather than left as text,
    /// because <c>dest+1</c> given <c>{buf,x}</c> is <c>buf+1,x</c> and not <c>{buf,x}+1</c>.
    /// Every other name is written as the argument was written, in parentheses where it stands
    /// inside a larger expression and would otherwise be read a different way.
    /// </para>
    /// </summary>
    private string Substituted(SyntaxNode statement, Expansion at)
    {
        var span = statement.Span;
        var text = statement.Tree.Text;
        var edits = new SortedDictionary<int, (int End, string Text)>();
        foreach (var operand in Under(statement).OfType<AbsoluteOperandSyntax>())
        {
            if (Operands.Substituted(model, operand, at) is { } given && Argument(given) is { } written)
                edits[operand.Span.Start] = (operand.Span.End, written);
        }
        // `.exprof(p)` is replaced by the expression inside the operand the call passed as `p`:
        // `5` for `{#5}`, `ptr` for `{(ptr),y}`.
        foreach (var exprOf in Under(statement).OfType<CallExpressionSyntax>().Where(Operands.IsExprOf))
        {
            if (model.ExprOf(exprOf, at) is { } inner)
                edits[exprOf.Span.Start] = (exprOf.Span.End, "(" + inner.GetText().Trim() + ")");
        }
        foreach (var name in Under(statement).OfType<NameExpressionSyntax>())
        {
            if (edits.Any(edit => edit.Key <= name.Span.Start && edit.Value.End >= name.Span.End))
                continue;
            if (model.SymbolOf(name) is not { Kind: SymbolKind.MacroParameter, Parameter: { } parameter })
                continue;
            if (Given(parameter, name, at) is { } written)
                edits[name.Span.Start] = (name.Span.End, written);
            else
                Refuse($"the argument for `{parameter.Name}` cannot be written out as text here");
        }

        var built = new StringBuilder();
        var was = span.Start;
        foreach (var (start, (end, written)) in edits)
        {
            if (start < was || end > span.End)
                continue;
            built.Append(text, was, start - was).Append(written);
            was = end;
        }
        return built.Append(text, was, span.End - was).ToString().Trim();
    }

    /// <summary>A statement and all the nodes under it: everywhere a parameter name may appear.</summary>
    private static IEnumerable<SyntaxNode> Under(SyntaxNode statement) =>
        [statement, .. statement.DescendantNodes()];

    /// <summary>
    /// The text that replaces one use of a parameter name: the argument as it was written, in
    /// parentheses where it sits inside a larger expression, since <c>#&lt;value</c> given
    /// <c>a + b</c> means <c>#&lt;(a + b)</c> and not <c>(#&lt;a) + b</c>.
    /// </summary>
    private string? Given(MacroParameter parameter, NameExpressionSyntax name, Expansion at)
    {
        if (parameter.IsBlock)
            return null;

        // A `list` parameter cannot appear where a single value is expected, so any use of one
        // that reaches here is a count or a test, which evaluates to a number.
        if (model.ArgumentFor(parameter.Symbol, at) is not { Value: { } value }
            || parameter.Kind == ParameterKind.List)
        {
            return model.ValueOf(name, at) is { Kind: ValueKind.Number } counted
                ? counted.Number.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        // An enum member may have been passed by its bare name, which resolves only through the
        // parameter's enum type, so write it as a path that resolves where the call is.
        if (parameter.Kind == ParameterKind.Enum
            && model.GivenAt(parameter.Symbol, at) is { } given && model.MemberFor(given.Argument, given.Caller) is { } member)
        {
            return member.Tree == model.Tree ? member.QualifiedName : "::" + member.PathName;
        }

        var written = (value as BracedOperandSyntax)?.Operand ?? value;
        var text = written.GetText().Trim();
        return name.Parent is ExpressionSyntax && written is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? $"({text})"
            : text;
    }

    /// <summary>
    /// An <c>operand</c> argument in the form the body used it: the operand itself where the
    /// body used it whole, the byte a <c>.byteof</c> selects, or for <c>+ n</c> the offset
    /// address followed by the argument's own index register.
    /// </summary>
    private static string? Argument(OperandSubstitution given)
    {
        if (given is { ByteOf: true, Operand: ImmediateOperandSyntax })
        {
            if (given.Expression is not { } value)
                return null;
            var text = value.GetText().Trim();
            var whole = value is BinaryExpressionSyntax or UnaryExpressionSyntax ? $"({text})" : text;
            return given.Offset switch
            {
                0 => $"#<{whole}",
                1 => $"#>{whole}",
                _ => string.Create(CultureInfo.InvariantCulture, $"#(({text} >> {8 * given.Offset}) & $ff)"),
            };
        }

        if (given.Offset == 0)
            return given.Operand.GetText().Trim();
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var written = addressed.GetText().Trim();
        return given.Offset > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{written}+{given.Offset}{index}")
            : string.Create(CultureInfo.InvariantCulture, $"{written}{given.Offset}{index}");
    }

    /// <summary>Adds one line of the expansion.</summary>
    private void Emit(string text) => lines.Add(text.TrimEnd());

    /// <summary>Records why the expansion cannot be written into the file, keeping only the first reason.</summary>
    private void Refuse(string why) => refusal ??= why;

    /// <summary>A call left unexpanded, so that a reader can ask for its expansion one level down.</summary>
    /// <param name="Line">Which line of the expansion it is on, counting from zero.</param>
    /// <param name="Text">The call as written, for the link's label.</param>
    /// <param name="Into">The index path that expands it: which call to expand at each level.</param>
    internal sealed record Link(int Line, string Text, IReadOnlyList<int> Into);

    /// <summary>Numbers the calls left unexpanded at one level; a link identifies a call by that number.</summary>
    private sealed class Counter
    {
        private int next;

        public int Next() => next++;
    }
}
