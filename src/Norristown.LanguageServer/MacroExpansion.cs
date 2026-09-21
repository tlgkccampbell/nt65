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
/// What the body decides from its arguments is decided here, because the text has no way to
/// leave it open: an <c>.if</c> over a <c>one</c> parameter becomes the branch it takes, and an
/// <c>.each</c> over a <c>list</c> parameter becomes its turns, since nt65 has no way to write
/// a call's arguments as a list literal.
/// </para>
/// </summary>
internal sealed class MacroExpansion
{
    /// <summary>One level of the expansion's own indentation.</summary>
    private const string Step = "    ";

    private readonly SemanticModel model;
    private readonly bool all;
    private readonly List<string> lines = [];
    private readonly List<Link> links = [];
    private string? refusal;

    private MacroExpansion(SemanticModel model, Symbol macro, MacroCallSyntax call, bool all)
    {
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

    /// <summary>The calls left as calls, in the order they stand, each with the way back to it.</summary>
    public IReadOnlyList<Link> Links => links;

    /// <summary>How many bytes the call lays out to, from the layout the build writes from.</summary>
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
    /// Which call to write out further at each level, by its place among the calls left at that
    /// level; empty for the one level a view opens with.
    /// </param>
    /// <param name="all">Whether to write out every call, however deep, for whoever wants that.</param>
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
        var written = new MacroExpansion(model, macro, call, all) { Bytes = bytes, Cycles = cycles };
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
    /// What the call becomes, as the row of a hover's grid says it: how much it is, words
    /// before numbers. Cycles are left out where the expansion writes no code.
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

    /// <summary>The same as the sentence a view of its own leads with.</summary>
    public string Summary() => $"expands to {Becomes()}";

    /// <summary>What the call lays out to, from the same walk the build lays out.</summary>
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

    /// <summary>Whether a writing of a line is inside <paramref name="call"/>'s expansion.</summary>
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
    /// <paramref name="into"/> is which call to write out further at this level and below, and
    /// <paramref name="reached"/> is the way back to this level, which a link carries.
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
        // What the body decides from its arguments it decides here: only the branch this call
        // takes is what the programmer would have written by hand.
        if (block.BlockKind == BlockKind.If)
        {
            if (chain.Includes(model, block, at))
                Members(Macros.LinesOf(block), at, indent, into, reached, counter);
            return;
        }
        chain.Break();

        // nt65 has no way to write a call's arguments as a list, so a repetition over one is
        // written out as its turns; so is every other repetition, since the reader asked what
        // this call becomes rather than what the body says.
        if (block.BlockKind is BlockKind.Repeat or BlockKind.Each or BlockKind.MultiProc)
        {
            foreach (var turn in Repetitions.Of(model, block, at, null))
                Members(Macros.LinesOf(block), turn, indent, into, reached, counter);
            return;
        }

        if (block.BlockKind == BlockKind.MacroBlock)
        {
            // A `} name {` is a sibling of the block it carries on, and was written out with the
            // call that opened the chain; only the first block of one is a call's.
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
            Refuse($"`{splice.Name.Text}` is a name this expansion cannot work out");
            return;
        }

        // A block the call left out writes nothing, which is what `.empty` asks about.
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
                Refuse($"`{call.Name.Text}!` names no macro this program has");
                return;
            }
            if (Expansion.Expanding(at, definition))
            {
                Refuse($"`{call.Name.Text}!` reaches itself");
                return;
            }
            Body(definition, Expansion.Of(at, call, definition), indent, chosen ? [.. into.Skip(1)] : [], way);
            return;
        }

        // Left as a call: the line as it would be written, and the blocks under it, whose lines
        // are the caller's own code and stand as they were written.
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
    /// One statement's text, with every name the call gave a value standing for what it was
    /// given.
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
        foreach (var name in Under(statement).OfType<NameExpressionSyntax>())
        {
            if (edits.Any(edit => edit.Key <= name.Span.Start && edit.Value.End >= name.Span.End))
                continue;
            if (model.SymbolOf(name) is not { Kind: SymbolKind.MacroParameter, Parameter: { } parameter })
                continue;
            if (Given(parameter, name, at) is { } written)
                edits[name.Span.Start] = (name.Span.End, written);
            else
                Refuse($"`{parameter.Name}` is a name this expansion cannot write out");
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

    /// <summary>A statement and the nodes under it, which is where a name may stand.</summary>
    private static IEnumerable<SyntaxNode> Under(SyntaxNode statement) =>
        [statement, .. statement.DescendantNodes()];

    /// <summary>
    /// What one name standing for a parameter is written as: the argument as it was written, in
    /// parentheses where it sits inside a larger expression, since <c>#&lt;value</c> given
    /// <c>a + b</c> means <c>#&lt;(a + b)</c> and not <c>(#&lt;a) + b</c>.
    /// </summary>
    private string? Given(MacroParameter parameter, NameExpressionSyntax name, Expansion at)
    {
        if (parameter.IsBlock)
            return null;

        // A `list` stands nowhere a single name can, so what is left of one here is a count or
        // a test, which is a number by the time it is written out.
        if (model.ArgumentFor(parameter.Symbol, at) is not { Value: { } value }
            || parameter.Kind == ParameterKind.List)
        {
            return model.ValueOf(name, at) is { Kind: ValueKind.Number } counted
                ? counted.Number.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        var written = (value as BracedOperandSyntax)?.Operand ?? value;
        var text = written.GetText().Trim();
        return name.Parent is ExpressionSyntax && written is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? $"({text})"
            : text;
    }

    /// <summary>
    /// An <c>operand</c> argument as the body asked for it: the operand itself where the body
    /// named it whole, the byte a <c>.byteof</c> asked for, and the address a <c>+ n</c> reaches
    /// with the argument's own index put back after it.
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

    /// <summary>Says why it is not something to write into a file, keeping the first reason.</summary>
    private void Refuse(string why) => refusal ??= why;

    /// <summary>A call left as a call, so that a reader can ask for it one level down.</summary>
    /// <param name="Line">Which line of the expansion it is on, counting from zero.</param>
    /// <param name="Text">The call as it stands, for the link to name.</param>
    /// <param name="Into">The way back to it: which call to write out at each level.</param>
    internal sealed record Link(int Line, string Text, IReadOnlyList<int> Into);

    /// <summary>How many calls one level has left as calls, which is what a link names one by.</summary>
    private sealed class Counter
    {
        private int next;

        public int Next() => next++;
    }
}
