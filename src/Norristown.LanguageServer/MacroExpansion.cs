using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents a macro call expanded into nt65 text, which is the body with the arguments in
/// place, as the programmer would have written it by hand. It is not the ca65 that the emitter
/// writes, which the output view shows. It is also not flattened. A call inside the body stays a
/// call, because a fully expanded nest of macros is unreadable and nobody wrote it that way.
/// <para>
/// Choices the body makes based on its arguments are resolved here, because the expanded text
/// has no way to leave them open. An <c>.if</c> over a <c>one</c> parameter becomes the branch it
/// takes, and an <c>.each</c> over a <c>list</c> parameter becomes its iterations, since nt65 has
/// no syntax for a call's arguments as a list literal.
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

    /// <summary>Gets the macro the call names.</summary>
    public Symbol Macro { get; }

    /// <summary>Gets the call itself.</summary>
    public MacroCallSyntax Call { get; }

    /// <summary>Gets the expansion, one line at a time.</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>
    /// Gets the macro calls left unexpanded, in the order they appear, each with the index path
    /// that requests its expansion.
    /// </summary>
    public IReadOnlyList<Link> Links => links;

    /// <summary>
    /// Gets the number of bytes the call assembles to, taken from the same layout the build uses.
    /// </summary>
    public int Bytes { get; private init; }

    /// <summary>Gets the call's cost to run, or null where none of it is code.</summary>
    public CycleCount? Cycles { get; private init; }

    /// <summary>
    /// Gets the reason the expansion cannot be inserted into the file where the call is, or null
    /// when it can.
    /// </summary>
    public string? Refusal => refusal;

    /// <summary>
    /// Returns the expansion of the call on the line <paramref name="position"/> is in, or null
    /// where the line holds no call or the program does not know what the call names.
    /// </summary>
    /// <param name="analysis">The program, for what the call lays out to.</param>
    /// <param name="model">The file the call is in.</param>
    /// <param name="position">The position in that file's text.</param>
    /// <param name="into">
    /// A path of indices that gives, at each level, which unexpanded call to expand further, by
    /// its position among the calls left unexpanded at that level. It is empty to expand only the
    /// first level, which is what a view opens with.
    /// </param>
    /// <param name="all">Whether to expand every nested call, at any depth.</param>
    public static MacroExpansion? At(
        ProgramAnalysis analysis, SemanticModel model, int position,
        IReadOnlyList<int>? into = null, bool all = false) =>
        CallAt(model, position) is { } call ? Of(analysis, model, call, into, all) : null;

    /// <summary>
    /// Returns the expansion of <paramref name="call"/>, for a caller that already has the call,
    /// or null where the program does not know what the call names.
    /// </summary>
    public static MacroExpansion? Of(
        ProgramAnalysis analysis, SemanticModel model, MacroCallSyntax call,
        IReadOnlyList<int>? into = null, bool all = false)
    {
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition } macro)
            return null;
        var (bytes, cycles) = Laid(analysis, model, call);
        var expansion = new MacroExpansion(analysis, model, macro, call, all) { Bytes = bytes, Cycles = cycles };
        expansion.Body(definition, Expansion.Of(null, call, definition), "", into ?? [], []);
        return expansion;
    }

    /// <summary>
    /// Returns the call the line at <paramref name="position"/> holds, whether it stands alone or
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
    /// Returns a one-line summary of what the call expands to, as the hover's summary row shows
    /// it. The summary gives the number of lines, then bytes, then cycles. Cycles are left out
    /// where the expansion contains no code.
    /// </summary>
    public string Becomes()
    {
        var lineCount = lines.Count == 1 ? "1 line" : $"{lines.Count} lines";
        var bytes = Bytes == 1 ? "1 byte" : $"{Bytes} bytes";
        var cycles = Cycles is { } count
            ? $" · {count} cycle{(count is { IsExact: true, Minimum: 1 } ? "" : "s")}"
            : "";
        return $"{lineCount} · {bytes}{cycles}";
    }

    /// <summary>
    /// Returns the summary from <see cref="Becomes"/> as the sentence the expansion view opens
    /// with.
    /// </summary>
    public string Summary() => $"expands to {Becomes()}";

    /// <summary>
    /// Returns the bytes and cycles the call assembles to, taken from the layout the build uses.
    /// </summary>
    private static (int Bytes, CycleCount? Cycles) Laid(
        ProgramAnalysis analysis, SemanticModel model, MacroCallSyntax call)
    {
        if (analysis.LayoutFor(model.Tree.Path) is not { } layout)
            return (0, null);
        var bytes = 0;
        CycleCount? cycles = null;
        foreach (var step in layout.Steps)
        {
            if (step.IsMarker || !Within(step.On, call) || layout.Of(step.Statement, step.On) is not { } laid)
                continue;
            if (laid.Length > 0)
                bytes += laid.Length;
            if (laid.Cycles is { } count)
                cycles = cycles is { } running ? running + count : count;
        }
        return (bytes, cycles);
    }

    /// <summary>
    /// Checks whether a macro body declares any names of its own. Each expansion gets its own
    /// copy of those names, so two expansions inserted in the same place would declare them twice.
    /// The line that opens the block declares the macro and its parameters and is not counted as
    /// part of the body.
    /// </summary>
    public static bool Declares(ProgramAnalysis analysis, BlockSyntax definition) =>
        analysis.ModelFor(definition.Tree.Path) is { } declaring
        && declaring.Symbols.Any(symbol => symbol.Tree == definition.Tree
            && symbol.NameSpan.Start >= definition.Opener.FullSpan.End
            && symbol.NameSpan.Start < definition.FullSpan.End);

    /// <summary>
    /// Checks whether a line emitted under expansion <paramref name="on"/> comes from inside
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
    /// Adds the lines of one block to the expansion at <paramref name="indent"/>.
    /// <paramref name="into"/> gives which call to expand further at this level and below, and
    /// <paramref name="reached"/> is the index path to this level, which each link records.
    /// </summary>
    private void Body(
        BlockSyntax block, Expansion at, string indent, IReadOnlyList<int> into, IReadOnlyList<int> reached) =>
        Members(Macros.LinesOf(block), at, indent, into, reached, new Counter());

    /// <summary>
    /// Adds lines that do not form a whole block to the expansion at <paramref name="indent"/>.
    /// Such lines are a block argument's contents, or the branch of an <c>.if</c> chain that this
    /// call takes.
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

    /// <summary>
    /// Adds one block of a body, which is a condition chain, a repetition, a call with a block, or
    /// a plain block.
    /// </summary>
    private void Block(
        BlockSyntax block, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter, ConditionChain chain)
    {
        // Conditions on the arguments are resolved here, because a programmer expanding this
        // call by hand would have kept only the branch it takes.
        if (block.BlockKind == BlockKind.If)
        {
            if (chain.Includes(model, block, at))
                Members(Macros.LinesOf(block), at, indent, into, reached, counter);
            return;
        }
        chain.Break();

        // nt65 has no syntax for a call's arguments as a list, so a repetition over one is
        // expanded iteration by iteration. Every other repetition is expanded the same way, since
        // the reader asked what this call becomes rather than what the body says.
        if (block.BlockKind is BlockKind.Repeat or BlockKind.Each or BlockKind.MultiProc)
        {
            foreach (var iteration in Repetitions.Of(model, block, at, null))
                Members(Macros.LinesOf(block), iteration, indent, into, reached, counter);
            return;
        }

        if (block.BlockKind == BlockKind.MacroBlock)
        {
            // A `} name {` continuation is a sibling of the block it continues, and was already
            // expanded with the call that opened the chain. Only the chain's first block holds
            // the call.
            if (Macros.CallIn(block.Opener.Statement) is { } call)
                Called(block, call, at, indent, into, reached, counter);
            return;
        }

        AddStatement(block.Opener.Statement, at, indent);
        Members(Macros.LinesOf(block), at, indent + Step, into, reached, counter);
        Emit(indent + "}");
    }

    /// <summary>Adds one line of a body that opens no block.</summary>
    private void Statement(
        LineSyntax line, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        switch (line.Statement)
        {
            case BlockCloseLineSyntax or BlankLineSyntax:
                return;

            // A line naming a `block` parameter inserts the caller's own code, which needs no
            // substitution because the caller wrote it in the place where it stands.
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
                AddStatement(line.Statement, at, indent);
                return;
        }
    }

    /// <summary>
    /// Adds the lines a <c>block</c> argument gives, at the place where the body names the
    /// parameter.
    /// </summary>
    private void Spliced(
        BlockSpliceSyntax splice, Expansion at, string indent,
        IReadOnlyList<int> into, IReadOnlyList<int> reached, Counter counter)
    {
        if (model.SymbolAt(splice.Name) is not { Parameter: { IsBlock: true } parameter })
        {
            Refuse($"`{splice.Name.Text}` is not a `block` parameter of this macro");
            return;
        }

        // A block argument the call omitted adds nothing. This is the case `.empty` tests for.
        if (model.ArgumentFor(parameter.Symbol, at) is not { Block: { } block })
            return;
        Members(Macros.LinesOf(block), Expansion.Spliced(at, splice, block), indent, into, reached, counter);
    }

    /// <summary>
    /// Adds a call inside the body. It is left as a call, so that expansion goes one level at a
    /// time, unless it is the call the reader asked to see or every call was asked for.
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

            // Each expansion has its own locals, so two of them expanded in one body would
            // declare the same name twice. An anonymous scope is inline code and keeps them
            // apart, which is what the language offers for exactly this case.
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

        // A call left unexpanded adds the call line itself, then the block arguments under it.
        // Those arguments are code of this body rather than of the called macro, and are
        // expanded like any other line of the body.
        var callText = AddStatement(call, at, indent);
        links.Add(new Link(lines.Count - 1, callText.TrimEnd('{').Trim(), way));
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

    /// <summary>Returns the <c>} name {</c> blocks that continue a call's block arguments.</summary>
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

    /// <summary>
    /// Adds one statement as it would have been written by hand, at <paramref name="indent"/>,
    /// and returns its text.
    /// </summary>
    private string AddStatement(SyntaxNode statement, Expansion at, string indent)
    {
        var text = Substituted(statement, at);
        if (text.Length > 0)
            Emit(indent + text);
        return text;
    }

    /// <summary>
    /// Returns one statement's text, with each parameter name replaced by the argument the call
    /// passed for it.
    /// <para>
    /// An <c>operand</c> parameter stands for a whole operand, and the <c>+ n</c> and
    /// <c>.byteof</c> that a body may apply to one are computed here rather than left as text.
    /// That is because <c>dest+1</c> given <c>{buf,x}</c> is <c>buf+1,x</c> and not
    /// <c>{buf,x}+1</c>. Every other name is replaced by the argument's text, in parentheses where
    /// it stands inside a larger expression and would otherwise be read differently.
    /// </para>
    /// </summary>
    private string Substituted(SyntaxNode statement, Expansion at)
    {
        var span = statement.Span;
        var text = statement.Tree.Text;
        var edits = new SortedDictionary<int, (int End, string Text)>();
        foreach (var operand in Under(statement).OfType<AbsoluteOperandSyntax>())
        {
            if (Operands.Substituted(model, operand, at) is { } given && Argument(given) is { } replacement)
                edits[operand.Span.Start] = (operand.Span.End, replacement);
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
            if (Given(parameter, name, at) is { } replacement)
                edits[name.Span.Start] = (name.Span.End, replacement);
            else
                Refuse($"the argument for `{parameter.Name}` cannot be substituted as text here");
        }

        var built = new StringBuilder();
        var was = span.Start;
        foreach (var (start, (end, replacement)) in edits)
        {
            if (start < was || end > span.End)
                continue;
            built.Append(text, was, start - was).Append(replacement);
            was = end;
        }
        return built.Append(text, was, span.End - was).ToString().Trim();
    }

    /// <summary>
    /// Returns a statement and all the nodes under it, which covers everywhere a parameter name
    /// may appear.
    /// </summary>
    private static IEnumerable<SyntaxNode> Under(SyntaxNode statement) =>
        [statement, .. statement.DescendantNodes()];

    /// <summary>
    /// Returns the text that replaces one use of a parameter name. That is the argument's text,
    /// in parentheses where it sits inside a larger expression, since <c>#&lt;value</c> given
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
        // parameter's enum type, so it is given as a path that resolves where the call is.
        if (parameter.Kind == ParameterKind.Enum
            && model.GivenAt(parameter.Symbol, at) is { } given && model.MemberFor(given.Argument, given.Caller) is { } member)
        {
            return member.Tree == model.Tree ? member.QualifiedName : "::" + member.PathName;
        }

        var unbraced = (value as BracedOperandSyntax)?.Operand ?? value;
        var text = unbraced.GetText().Trim();
        return name.Parent is ExpressionSyntax && unbraced is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? $"({text})"
            : text;
    }

    /// <summary>
    /// Returns an <c>operand</c> argument in the form the body used it. That is the operand
    /// itself where the body used it whole, the byte a <c>.byteof</c> selects, or, for
    /// <c>+ n</c>, the offset address followed by the argument's own index register.
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
        var address = addressed.GetText().Trim();
        return given.Offset > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{address}+{given.Offset}{index}")
            : string.Create(CultureInfo.InvariantCulture, $"{address}{given.Offset}{index}");
    }

    /// <summary>Adds one line of the expansion.</summary>
    private void Emit(string text) => lines.Add(text.TrimEnd());

    /// <summary>
    /// Records why the expansion cannot be inserted into the file, keeping only the first reason.
    /// </summary>
    private void Refuse(string why) => refusal ??= why;

    /// <summary>
    /// Represents a call left unexpanded, so that a reader can ask for its expansion one level
    /// down.
    /// </summary>
    /// <param name="Line">Which line of the expansion it is on, counting from zero.</param>
    /// <param name="Text">The call's text, for the link's label.</param>
    /// <param name="Into">The index path that expands it, giving which call to expand at each level.</param>
    internal sealed record Link(int Line, string Text, IReadOnlyList<int> Into);

    /// <summary>
    /// Numbers the calls left unexpanded at one level. A link identifies a call by that number.
    /// </summary>
    private sealed class Counter
    {
        private int next;

        public int Next() => next++;
    }
}
