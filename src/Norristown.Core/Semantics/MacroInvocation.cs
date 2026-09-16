using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One call, with each parameter matched to what it was given. Binding and expansion both
/// ask the same question of the same line, so they work this out the same way and the
/// answer is nobody's to keep.
/// <para>
/// A diagnostic lands on the side of the call that can fix it (§11.6): everything here is
/// about one call's arguments, so everything here is reported at the call.
/// </para>
/// </summary>
public sealed class MacroInvocation
{
    private readonly Dictionary<Symbol, MacroArgument> byParameter = [];

    private MacroInvocation(Symbol macro, SyntaxNode call)
    {
        Macro = macro;
        Call = call;
    }

    /// <summary>The macro being called.</summary>
    public Symbol Macro { get; }

    /// <summary>The call itself.</summary>
    public SyntaxNode Call { get; }

    /// <summary>What each parameter was given, in the order the parameters are declared.</summary>
    public IReadOnlyList<MacroArgument> Arguments { get; private set; } = [];

    /// <summary>What one parameter was given, or null when the call gave it nothing.</summary>
    public MacroArgument? For(Symbol parameter) => byParameter.GetValueOrDefault(parameter);

    /// <summary>
    /// Matches <paramref name="call"/>'s arguments to <paramref name="macro"/>'s parameters,
    /// reporting what is wrong with them into <paramref name="diagnostics"/> when a caller
    /// wants to hear about it.
    /// </summary>
    public static MacroInvocation Of(
        SyntaxNode call, Symbol macro, SyntaxTree tree, List<Diagnostic>? diagnostics,
        Func<string, Symbol?>? lookup = null)
    {
        var invocation = new MacroInvocation(macro, call);
        void Report(TextSpan span, string message) =>
            diagnostics?.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message));

        var positional = macro.Parameters.Where(parameter => !parameter.IsBlock).ToList();
        var given = new Dictionary<Symbol, MacroArgument>();
        var listed = new Dictionary<Symbol, List<SyntaxNode>>();
        var next = 0;
        var named = false;

        foreach (var argument in Macros.ArgumentsOf(call))
        {
            if (argument.Kind == SyntaxKind.NamedArgument)
            {
                named = true;
                BindNamed(argument);
                continue;
            }
            if (named)
            {
                Report(argument.Span, "a positional argument comes before the named ones");
                continue;
            }
            BindPositional(argument);
        }

        // A block is written after the parentheses, so the blocks a call opens are matched to
        // the block parameters separately: the first by position, the rest by the name on
        // each `} name {`.
        BindBlocks();
        Finish();
        return invocation;

        void BindNamed(SyntaxNode argument)
        {
            var name = argument.ChildTokens.Length > 0 ? argument.ChildTokens[0] : default;
            if (name.Parent is null)
                return;
            var parameter = macro.Parameters.FirstOrDefault(p => p.Name == name.Text);
            if (parameter is null)
            {
                Report(name.Span, $"`{macro.Name}` has no parameter called `{name.Text}`");
                return;
            }
            if (parameter.IsBlock)
            {
                Report(name.Span, $"`{parameter.Name}` takes a block, which is written after the parentheses");
                return;
            }
            if (given.ContainsKey(parameter.Symbol) || listed.ContainsKey(parameter.Symbol))
            {
                Report(name.Span, $"`{parameter.Name}` is given twice");
                return;
            }
            Take(parameter, argument.ChildNodes.FirstOrDefault());
        }

        void BindPositional(SyntaxNode argument)
        {
            if (next >= positional.Count)
            {
                Report(argument.Span, Count(macro));
                return;
            }
            var parameter = positional[next];

            // A `list` takes every remaining positional argument, so it stays the one being
            // filled rather than moving the next one along.
            if (parameter.Kind == ParameterKind.List)
            {
                Check(parameter.Accepts.Element ?? ArgumentKind.Expression, parameter, argument);
                if (!listed.TryGetValue(parameter.Symbol, out var items))
                    listed[parameter.Symbol] = items = [];
                items.Add(argument);
                return;
            }
            next++;
            Take(parameter, argument);
        }

        void Take(MacroParameter parameter, SyntaxNode? value)
        {
            if (parameter.Kind == ParameterKind.List)
            {
                if (value is not null)
                    listed[parameter.Symbol] = [value];
                return;
            }
            if (value is not null)
                Check(parameter.Accepts, parameter, value);
            given[parameter.Symbol] = new MacroArgument(parameter, value, [], null, Written: true);
        }

        void BindBlocks()
        {
            var blocks = Macros.BlocksOf(call);
            var parameters = macro.Parameters.Where(parameter => parameter.IsBlock).ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                var opener = blocks[i].ChildNodes.Length > 0 ? blocks[i].ChildNodes[0].Statement : null;

                // The first block binds by position; each `} name {` after it says which
                // parameter it is, because a macro may take several and skip none silently.
                MacroParameter? parameter;
                if (opener is { Kind: SyntaxKind.BlockContinuation } && opener.ChildTokens.Length > 1)
                {
                    var name = opener.ChildTokens[1];
                    parameter = parameters.FirstOrDefault(p => p.Name == name.Text);
                    if (parameter is null)
                    {
                        Report(name.Span, $"`{macro.Name}` has no `block` parameter called `{name.Text}`");
                        continue;
                    }
                    if (given.ContainsKey(parameter.Symbol))
                    {
                        Report(name.Span, $"`{parameter.Name}` is given twice");
                        continue;
                    }
                }
                else if (i < parameters.Count)
                {
                    parameter = parameters[i];
                }
                else
                {
                    Report(blocks[i].ChildNodes[0].Span,
                        $"`{macro.Name}` takes no block, and this call gives it one");
                    continue;
                }
                given[parameter.Symbol] = new MacroArgument(parameter, null, [], blocks[i], Written: true);
            }
        }

        void Finish()
        {
            var missing = new List<string>();
            var arguments = new List<MacroArgument>();
            foreach (var parameter in macro.Parameters)
            {
                if (listed.TryGetValue(parameter.Symbol, out var items))
                {
                    arguments.Add(new MacroArgument(parameter, null, items, null, Written: true));
                }
                else if (given.TryGetValue(parameter.Symbol, out var argument))
                {
                    arguments.Add(argument);
                }
                else if (parameter.IsOptional)
                {
                    arguments.Add(new MacroArgument(parameter, parameter.Default, [], null, Written: false));
                }
                else
                {
                    missing.Add(parameter.Name);
                    continue;
                }
                invocation.byParameter[parameter.Symbol] = arguments[^1];
            }
            invocation.Arguments = arguments;
            if (missing.Count > 0)
            {
                Report(NameSpan(call),
                    $"`{macro.Name}` is not given {string.Join(", ", missing.Select(name => $"`{name}`"))}");
            }
        }

        void Check(ArgumentKind accepts, MacroParameter parameter, SyntaxNode argument)
        {
            var value = argument.Kind == SyntaxKind.NamedArgument
                ? argument.ChildNodes.FirstOrDefault()
                : argument;
            if (value is null)
                return;

            switch (accepts.Kind)
            {
                case ParameterKind.Operand:
                    // Only a braced argument is an operand; an unbraced `(ptr)` reads as
                    // indirect addressing and so can only be a mistake here.
                    if (value.Kind == SyntaxKind.ParenthesizedExpression)
                    {
                        Report(value.Span, $"`{parameter.Name}` takes an operand, and `{value.GetText()}` "
                            + "reads as an expression in parentheses. Brace it to pass indirect addressing");
                    }
                    break;

                case ParameterKind.One:
                    var word = value is { Kind: SyntaxKind.NameExpression, ChildTokens.Length: 1 }
                        ? value.ChildTokens[0].Text
                        : null;

                    // A word may be passed on from a `one` parameter of the macro whose body
                    // writes the call, so long as this list holds everything that one allows
                    // (§11.2): which word it is is not known until there is an expansion.
                    if (word is not null && lookup?.Invoke(word) is
                        { Kind: SymbolKind.MacroParameter, Parameter.Accepts: { Kind: ParameterKind.One } passed })
                    {
                        var missing = passed.Words
                            .Where(w => !accepts.Words.Any(o => o.Equals(w, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (missing.Count > 0)
                        {
                            Report(value.Span, $"`{parameter.Name}` takes one of "
                                + $"{string.Join(", ", accepts.Words.Select(w => $"`{w}`"))}, and `{word}` may "
                                + $"also be {string.Join(", ", missing.Select(w => $"`{w}`"))}");
                        }
                        break;
                    }

                    if (word is null || !accepts.Words.Any(w => w.Equals(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        Report(value.Span, $"`{parameter.Name}` takes one of "
                            + $"{string.Join(", ", accepts.Words.Select(w => $"`{w}`"))}, and this is "
                            + (word is null ? "not a word" : $"`{word}`"));
                    }
                    break;

                case ParameterKind.Ident:
                    if (value.Kind != SyntaxKind.NameExpression)
                        Report(value.Span, $"`{parameter.Name}` takes a name, and this is not one");
                    break;

                case ParameterKind.Expr:
                case ParameterKind.Const:
                    if (value.Kind == SyntaxKind.BracedOperand)
                    {
                        Report(value.Span, $"`{parameter.Name}` takes an expression, and a braced "
                            + "argument is a whole operand");
                    }
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>How many arguments a macro takes, as the message for a call that gives more.</summary>
    private static string Count(Symbol macro)
    {
        var positional = macro.Parameters.Count(parameter => !parameter.IsBlock);
        var least = macro.Parameters.Count(parameter => !parameter.IsBlock && !parameter.IsOptional);
        return positional == least
            ? $"`{macro.Name}` takes {positional} argument(s), and this call gives more"
            : $"`{macro.Name}` takes {least} to {positional} arguments, and this call gives more";
    }

    /// <summary>The name a call writes, which is where a diagnostic about the call as a whole goes.</summary>
    private static TextSpan NameSpan(SyntaxNode call) =>
        Macros.CalleeOf(call) is { } callee ? callee.Span : call.Span;
}
