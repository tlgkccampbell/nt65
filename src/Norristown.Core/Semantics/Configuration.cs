using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which <c>.if</c> branches a build takes, and so which of the program is there at all.
/// <para>
/// A condition tests the build configuration and never the program: it may use literals,
/// operators, built-in functions and defines, and nothing a file declares. That is what
/// lets every condition be answered here, before a single declaration has been collected —
/// which declarations exist follows from the configuration alone, so the same name may be
/// declared under two conditions and only one of them is real. A check that depends on the
/// program is an <c>.assert</c>, which is evaluated last.
/// </para>
/// </summary>
public sealed class Configuration
{
    private readonly Dictionary<SyntaxTree, List<TextSpan>> omitted;
    private readonly HashSet<(SyntaxTree Tree, int Position)> answered;

    private Configuration(
        Dictionary<SyntaxTree, List<TextSpan>> omitted, HashSet<(SyntaxTree, int)> answered)
    {
        this.omitted = omitted;
        this.answered = answered;
    }

    /// <summary>A build that leaves nothing out, for a caller with no conditions to resolve.</summary>
    public static Configuration Everything { get; } = new([], []);

    /// <summary>
    /// Works out which branches <paramref name="trees"/> take when built for
    /// <paramref name="cpu"/> with <paramref name="defines"/>.
    /// </summary>
    public static Configuration Resolve(
        IEnumerable<SyntaxTree> trees, Cpu cpu, IEnumerable<Define> defines, List<Diagnostic> diagnostics)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var define in defines)
            values[define.Name] = define.Value;

        var omitted = new Dictionary<SyntaxTree, List<TextSpan>>();
        var answered = new HashSet<(SyntaxTree, int)>();
        foreach (var tree in trees)
        {
            var left = new List<TextSpan>();
            new Reader(tree, cpu, values, diagnostics, left, answered).Container(tree.Root);
            if (left.Count > 0)
                omitted[tree] = left;
        }
        return new Configuration(omitted, answered);
    }

    /// <summary>
    /// Whether this pass answered the condition on <paramref name="block"/>. It answers every
    /// one it can reach before a declaration is looked up; the ones it cannot are inside a
    /// macro body, a <c>.repeat</c> or an <c>.each</c>, where a condition may name what the
    /// expansion binds, and those are answered once per expansion instead.
    /// </summary>
    public bool Answered(SyntaxNode block) => answered.Contains((block.Tree, block.Position));

    /// <summary>Whether the build includes what is written at <paramref name="node"/>.</summary>
    public bool Includes(SyntaxNode node)
    {
        if (!omitted.TryGetValue(node.Tree, out var left))
            return true;
        foreach (var span in left)
        {
            if (node.Position >= span.Start && node.Position < span.End)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The branches of <paramref name="tree"/> this build leaves out, for an editor to dim.
    /// A branch inside one that is already left out is not listed again.
    /// </summary>
    public IReadOnlyList<TextSpan> Omitted(SyntaxTree tree) =>
        omitted.TryGetValue(tree, out var left) ? left : [];

    /// <summary>
    /// The configuration of a program in which <paramref name="before"/> became
    /// <paramref name="after"/>. A condition depends on nothing but the file it is in and the
    /// build, so every other file's answers stand.
    /// </summary>
    internal Configuration Replacing(
        SyntaxTree before, SyntaxTree after, Cpu cpu, IEnumerable<Define> defines, List<Diagnostic> diagnostics)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var define in defines)
            values[define.Name] = define.Value;

        var replaced = new Dictionary<SyntaxTree, List<TextSpan>>(omitted);
        replaced.Remove(before);
        var reanswered = new HashSet<(SyntaxTree, int)>(answered.Where(at => at.Tree != before));
        var left = new List<TextSpan>();
        new Reader(after, cpu, values, diagnostics, left, reanswered).Container(after.Root);
        if (left.Count > 0)
            replaced[after] = left;
        return new Configuration(replaced, reanswered);
    }

    /// <summary>
    /// Reads one file's conditions. A chain is a run of sibling blocks: the <c>.if</c> that
    /// starts it, then whichever <c>.elseif</c>s and <c>.else</c> continue it. The first
    /// branch whose condition holds is the one the build takes; the conditions after it are
    /// never evaluated, so nothing is reported about a branch that is not there.
    /// </summary>
    private sealed class Reader(
        SyntaxTree tree,
        Cpu cpu,
        Dictionary<string, long> defines,
        List<Diagnostic> diagnostics,
        List<TextSpan> omitted,
        HashSet<(SyntaxTree, int)> answered)
    {
        public void Container(SyntaxNode container)
        {
            var chaining = false;
            var taken = false;
            foreach (var child in container.ChildNodes)
            {
                if (child.Green is not GreenBlock block)
                {
                    chaining = false;
                    continue;
                }

                // A condition inside one of these may name what the expansion binds, so it
                // has no answer until there is an expansion to answer it for.
                if (block.BlockKind is BlockKind.Macro or BlockKind.Repeat or BlockKind.Each)
                {
                    chaining = false;
                    continue;
                }

                var opener = child.ChildNodes.Length > 0 ? child.ChildNodes[0].Statement : null;
                switch (opener?.Kind)
                {
                    case SyntaxKind.IfDirective:
                        chaining = true;
                        answered.Add((tree, child.Position));
                        taken = Branch(child, opener, already: false);
                        continue;

                    case SyntaxKind.ElseIfDirective:
                    case SyntaxKind.ElseDirective:
                        if (!chaining)
                        {
                            Report(opener.Span, $"`{Directive(opener)}` continues an `.if`, and there is none to continue");
                            Leave(child);
                            continue;
                        }
                        answered.Add((tree, child.Position));
                        taken |= Branch(child, opener, taken);
                        continue;

                    default:
                        chaining = false;
                        Container(child);
                        continue;
                }
            }
        }

        /// <summary>
        /// One branch of a chain: whether the build takes it. <paramref name="already"/> says
        /// an earlier branch was taken, in which case this one is left out whatever it says.
        /// </summary>
        private bool Branch(SyntaxNode block, SyntaxNode opener, bool already)
        {
            // The CPU is configuration, and a condition may test it with `.target`, so a
            // `.cpu` under an `.if` would be deciding what decides it.
            foreach (var node in block.DescendantNodes())
            {
                if (node.Kind == SyntaxKind.CpuDirective)
                    Report(node.Span, "`.cpu` states the program's processor, which a condition may test, so it may not be written under an `.if`");
            }

            var take = !already && Holds(opener);
            if (take)
                Container(block);
            else
                Leave(block);
            return take;
        }

        /// <summary>Whether the condition an <c>.if</c> or <c>.elseif</c> writes holds.</summary>
        private bool Holds(SyntaxNode opener)
        {
            if (opener.Kind == SyntaxKind.ElseDirective)
                return true;
            if (opener.ChildNodes.FirstOrDefault() is not { } condition)
                return false;

            var value = Evaluate(condition);
            if (value.IsString)
            {
                Report(condition.Span, "a condition is a number, and this is text");
                return false;
            }
            return value.AsNumber() is { } number && number != 0;
        }

        private void Leave(SyntaxNode block) => omitted.Add(block.Span);

        private Value Evaluate(SyntaxNode node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.NumberExpression:
                    return Number(Literals.Number(Text(node)));

                case SyntaxKind.CharacterExpression:
                    return Number(Literals.Character(Text(node)));

                case SyntaxKind.StringExpression:
                    return Literals.Text(Text(node)) is { } text ? Value.Of(text) : Value.Unknown;

                case SyntaxKind.ParenthesizedExpression:
                    return node.ChildNodes.Length > 0 ? Evaluate(node.ChildNodes[0]) : Value.Unknown;

                case SyntaxKind.UnaryExpression:
                    return node.ChildNodes.Length > 0 && node.ChildTokens.Length > 0
                        ? Unary(node.ChildTokens[0], Evaluate(node.ChildNodes[0]))
                        : Value.Unknown;

                case SyntaxKind.BinaryExpression:
                    return Binary(node);

                case SyntaxKind.NameExpression:
                    return ValueOfName(node);

                case SyntaxKind.CallExpression:
                    return Call(node);

                default:
                    return Value.Unknown;
            }
        }

        private Value Binary(SyntaxNode node)
        {
            var children = node.ChildNodes;
            if (children.Length != 2 || node.ChildTokens.Length == 0)
                return Value.Unknown;

            // The right operand is neither evaluated nor looked up once the left decides the
            // result, which is what makes `.defined(TRACE) && TRACE` a question with an answer.
            var op = node.ChildTokens[0];
            var left = Evaluate(children[0]);
            if (left.AsNumber() is { } decided && Operators.ShortCircuits(op.Kind, decided))
                return Value.Of(decided != 0);

            var right = Evaluate(children[1]);
            if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
                return Reject(op, left.IsString ? left : right);
            if (b == 0 && Operators.Divides(op.Green))
            {
                Report(op.Span, "division by zero");
                return Value.Unknown;
            }
            return Operators.Binary(op.Green, a, b) is { } result ? Value.Of(result) : Value.Unknown;
        }

        private Value Unary(SyntaxToken op, Value operand)
        {
            if (operand.AsNumber() is not { } value)
                return Reject(op, operand);
            return Operators.Unary(op.Kind, value) is { } result ? Value.Of(result) : Value.Unknown;
        }

        private Value Reject(SyntaxToken op, Value operand)
        {
            if (operand.IsString)
                Report(op.Span, $"`{op.Text}` cannot be used on a string");
            return Value.Unknown;
        }

        /// <summary>
        /// A name in a condition, which can only be a define. Anything else the program
        /// declares is refused here rather than looked up: a check about the program is an
        /// <c>.assert</c>, which is evaluated once the program is known.
        /// </summary>
        private Value ValueOfName(SyntaxNode name)
        {
            var written = name.GetText().Trim();
            if (name.ChildTokens.Length == 1 && defines.TryGetValue(name.ChildTokens[0].Text, out var value))
                return Value.Of(value);
            Report(name.Span, $"`{written}` is not a define. A condition tests the build "
                + "configuration, and a check on the program is an `.assert`");
            return Value.Unknown;
        }

        private Value Call(SyntaxNode call)
        {
            var given = call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? [];
            if (call.ChildTokens.Length == 0 || call.ChildTokens[0].Kind != SyntaxKind.Directive)
            {
                // A charmap or a `.func` called by name. Both are declarations, and reaching
                // them means resolving a name before the declarations exist.
                Report(call.Span, "a condition may not call a function the program declares");
                return Value.Unknown;
            }

            var function = call.ChildTokens[0];
            var name = function.Text.ToLowerInvariant();

            // `.defined` asks whether a name is a define, so the name is not looked up at all
            // and one that is not a define is the answer rather than a mistake.
            if (name == ".defined")
            {
                return given.Length == 1 && given[0].Kind == SyntaxKind.NameExpression
                    && given[0].ChildTokens.Length == 1
                    ? Value.Of(defines.ContainsKey(given[0].ChildTokens[0].Text))
                    : Value.Unknown;
            }

            if (name == ".target")
            {
                if (given.Length != 1 || CpuNames.Parse(Text(given[0])) is not { } named)
                {
                    Report(function.Span, "`.target` takes `6502`, `65c02` or `65816`");
                    return Value.Unknown;
                }
                return Value.Of(named == cpu);
            }

            // What the function asks about is decided before its arguments are read: a
            // `.sizeof(Point)` is one mistake, not that plus a `Point` that is not a define.
            if (!Answerable(name))
                return Measures(function);

            var values = given.Select(Evaluate).ToArray();
            return name switch
            {
                ".lobyte" => Number1(values, v => v & 0xff),
                ".hibyte" => Number1(values, v => (v >> 8) & 0xff),
                ".bankbyte" => Number1(values, v => (v >> 16) & 0xff),
                ".loword" => Number1(values, v => v & 0xffff),
                ".hiword" => Number1(values, v => (v >> 16) & 0xffff),
                ".min" => Number2(values, Math.Min),
                ".max" => Number2(values, Math.Max),
                ".strlen" => values is [{ Kind: ValueKind.String, Text: { } s }] ? Value.Of(s.Length) : Value.Unknown,
                ".strat" => values is [{ Kind: ValueKind.String, Text: { } t }, { Kind: ValueKind.Number } at]
                    && at.Number >= 0 && at.Number < t.Length
                    ? Value.Of(t[(int)at.Number])
                    : Value.Unknown,
                _ => Value.Unknown,
            };
        }

        /// <summary>The built-in functions the configuration alone can answer.</summary>
        private static bool Answerable(string name) => name is ".lobyte" or ".hibyte" or ".bankbyte"
            or ".loword" or ".hiword" or ".min" or ".max" or ".strlen" or ".strat";

        /// <summary>
        /// A built-in that measures the program, or one only a macro body has. Neither can be
        /// answered from the configuration alone.
        /// </summary>
        private Value Measures(SyntaxToken function)
        {
            Report(function.Span, $"`{function.Text}` asks about the program. A condition tests the "
                + "build configuration, and a check on the program is an `.assert`");
            return Value.Unknown;
        }

        private static Value Number1(Value[] arguments, Func<long, long> apply) =>
            arguments is [{ Kind: ValueKind.Number } only] ? Value.Of(apply(only.Number)) : Value.Unknown;

        private static Value Number2(Value[] arguments, Func<long, long, long> apply) =>
            arguments is [{ Kind: ValueKind.Number } a, { Kind: ValueKind.Number } b]
                ? Value.Of(apply(a.Number, b.Number))
                : Value.Unknown;

        private static Value Number(long? value) => value is { } number ? Value.Of(number) : Value.Unknown;

        private static string Text(SyntaxNode node) => node.ChildTokens.Length > 0 ? node.ChildTokens[0].Text : "";

        private static string Directive(SyntaxNode opener) =>
            opener.Kind == SyntaxKind.ElseDirective ? ".else" : ".elseif";

        private void Report(TextSpan span, string message) =>
            diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message));
    }
}
