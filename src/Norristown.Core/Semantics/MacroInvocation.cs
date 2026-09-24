using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one macro call, with each parameter matched to the argument it was given. Binding
/// and expansion both need this matching for the same line, so both compute it here in the same
/// way, and neither keeps the result.
/// <para>
/// A diagnostic goes where the fix belongs. Everything here concerns one call's arguments, so
/// every diagnostic here is reported at the call.
/// </para>
/// </summary>
public sealed class MacroInvocation
{
    private readonly Dictionary<Symbol, MacroArgument> byParameter = [];

    private MacroInvocation(Symbol macro, MacroCallSyntax call)
    {
        Macro = macro;
        Call = call;
    }

    /// <summary>Gets the macro being called.</summary>
    public Symbol Macro { get; }

    /// <summary>Gets the call itself.</summary>
    public MacroCallSyntax Call { get; }

    /// <summary>
    /// Gets the argument each parameter was given, in the order the parameters are declared.
    /// </summary>
    public IReadOnlyList<MacroArgument> Arguments { get; private set; } = [];

    /// <summary>
    /// Matches <paramref name="call"/>'s arguments to <paramref name="macro"/>'s parameters,
    /// reporting what is wrong with them into <paramref name="diagnostics"/> when it is not
    /// null.
    /// </summary>
    public static MacroInvocation Of(
        MacroCallSyntax call, Symbol macro, SyntaxTree tree, List<Diagnostic>? diagnostics,
        Func<string, Symbol?>? lookup = null)
    {
        var invocation = new MacroInvocation(macro, call);
        void Report(TextSpan span, DiagnosticMessage message) =>
            diagnostics?.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message));

        var positional = macro.Parameters.Where(parameter => !parameter.IsBlock).ToList();
        var given = new Dictionary<Symbol, MacroArgument>();
        var listed = new Dictionary<Symbol, List<SyntaxNode>>();
        var next = 0;
        var named = false;

        foreach (var argument in call.Arguments?.Arguments ?? [])
        {
            if (argument is NamedArgumentSyntax byName)
            {
                named = true;
                BindNamed(byName);
                continue;
            }
            if (named)
            {
                Report(argument.Span, Catalogue.ArgumentAfterANamedOne);
                continue;
            }
            BindPositional(argument);
        }

        // A block comes after the parentheses, so the blocks a call opens are matched to the
        // block parameters separately. The first is matched by position, and each later one by
        // the name on its `} name {`.
        BindBlocks();
        Finish();
        return invocation;

        void BindNamed(NamedArgumentSyntax argument)
        {
            var name = argument.Name;
            var parameter = macro.Parameters.FirstOrDefault(p => p.Name == name.Text);
            if (parameter is null)
            {
                Report(name.Span, Catalogue.ParameterUnknown.Message(macro.Name, name.Text));
                return;
            }
            if (parameter.IsBlock)
            {
                Report(name.Span, Catalogue.BlockArgumentInParentheses.Message(parameter.Name));
                return;
            }
            if (given.ContainsKey(parameter.Symbol) || listed.ContainsKey(parameter.Symbol))
            {
                Report(name.Span, Catalogue.ArgumentGivenTwice.Message(parameter.Name));
                return;
            }
            Take(parameter, argument.Value);
        }

        void BindPositional(SyntaxNode argument)
        {
            if (next >= positional.Count)
            {
                Report(argument.Span, Count(macro));
                return;
            }
            var parameter = positional[next];

            // A `list` takes every remaining positional argument, so it remains the parameter
            // being filled and the position does not advance.
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
            given[parameter.Symbol] = new MacroArgument(parameter, value, [], null, IsGiven: true);
        }

        void BindBlocks()
        {
            var blocks = Macros.BlocksOf(call);
            var parameters = macro.Parameters.Where(parameter => parameter.IsBlock).ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                // The first block binds by position. Each `} name {` after it names its
                // parameter, because a macro may take several blocks and none may be skipped
                // silently.
                MacroParameter? parameter;
                if (blocks[i].Opener.Statement is BlockContinuationSyntax continuation)
                {
                    var name = continuation.Name;
                    parameter = parameters.FirstOrDefault(p => p.Name == name.Text);
                    if (parameter is null)
                    {
                        Report(name.Span, Catalogue.BlockParameterUnknown.Message(macro.Name, name.Text));
                        continue;
                    }
                    if (given.ContainsKey(parameter.Symbol))
                    {
                        Report(name.Span, Catalogue.ArgumentGivenTwice.Message(parameter.Name));
                        continue;
                    }
                }
                else if (i < parameters.Count)
                {
                    parameter = parameters[i];
                }
                else
                {
                    Report(blocks[i].Opener.Span,
                        Catalogue.BlockArgumentUnexpected.Message(macro.Name));
                    continue;
                }
                given[parameter.Symbol] = new MacroArgument(parameter, null, [], blocks[i], IsGiven: true);
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
                    arguments.Add(new MacroArgument(parameter, null, items, null, IsGiven: true));
                }
                else if (given.TryGetValue(parameter.Symbol, out var argument))
                {
                    arguments.Add(argument);
                }
                else if (parameter.IsOptional)
                {
                    arguments.Add(new MacroArgument(parameter, parameter.Default, [], null, IsGiven: false));
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
                    Catalogue.ArgumentMissing.Message(
                        macro.Name, string.Join(", ", missing.Select(name => $"`{name}`"))));
            }
        }

        void Check(ArgumentKind accepts, MacroParameter parameter, SyntaxNode argument)
        {
            var value = argument is NamedArgumentSyntax byName ? byName.Value : argument;
            if (value is null)
                return;

            switch (accepts.Kind)
            {
                case ParameterKind.Operand:
                    // Only a braced argument is an operand. An unbraced `(ptr)` reads as
                    // indirect addressing, so it can only be a mistake here.
                    if (value is ParenthesizedExpressionSyntax)
                    {
                        Report(value.Span, Catalogue.OperandArgumentParenthesized.Message(
                            parameter.Name, value.GetText()));
                    }
                    break;

                case ParameterKind.One:
                    var word = value is NameExpressionSyntax { SimpleName: { } only } ? only.Text : null;

                    // A word may be passed on from a `one` parameter of the macro whose body
                    // contains the call. The word is not known until there is an expansion, so
                    // this parameter's list must hold every word that the outer parameter allows.
                    if (word is not null && lookup?.Invoke(word) is
                        { Kind: SymbolKind.MacroParameter, Parameter.Accepts: { Kind: ParameterKind.One } passed })
                    {
                        var missing = passed.Words
                            .Where(w => !accepts.Words.Any(o => o.Equals(w, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (missing.Count > 0)
                        {
                            Report(value.Span, Catalogue.WordArgumentAmbiguous.Message(
                                parameter.Name,
                                string.Join(", ", accepts.Words.Select(w => $"`{w}`")),
                                word,
                                string.Join(", ", missing.Select(w => $"`{w}`"))));
                        }
                        break;
                    }

                    if (word is null || !accepts.Words.Any(w => w.Equals(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        Report(value.Span, Catalogue.WordArgumentNotListed.Message(
                            parameter.Name,
                            string.Join(", ", accepts.Words.Select(w => $"`{w}`")),
                            word is null ? "not a word" : $"`{word}`"));
                    }
                    break;

                case ParameterKind.Ident:
                    if (value is not NameExpressionSyntax)
                        Report(value.Span, Catalogue.IdentArgumentNotAName.Message(parameter.Name));
                    break;

                case ParameterKind.Expr:
                case ParameterKind.Const:
                case ParameterKind.Enum:
                    if (value is BracedOperandSyntax)
                    {
                        Report(value.Span, Catalogue.ExpressionArgumentBraced.Message(parameter.Name));
                    }
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Returns the argument <paramref name="parameter"/> was given, or null when the call gave it
    /// nothing.
    /// </summary>
    public MacroArgument? For(Symbol parameter) => byParameter.GetValueOrDefault(parameter);

    /// <summary>
    /// Returns the message for a call that gives <paramref name="macro"/> more arguments than it
    /// takes, stating how many it takes.
    /// </summary>
    private static DiagnosticMessage Count(Symbol macro)
    {
        var positional = macro.Parameters.Count(parameter => !parameter.IsBlock);
        var least = macro.Parameters.Count(parameter => !parameter.IsBlock && !parameter.IsOptional);
        return Catalogue.ArgumentCount.Message(
            macro.Name,
            positional == least ? $"{positional} argument(s)" : $"{least} to {positional} arguments");
    }

    /// <summary>
    /// Returns the span of the macro name in <paramref name="call"/>, where a diagnostic about the
    /// call as a whole is reported.
    /// </summary>
    private static TextSpan NameSpan(MacroCallSyntax call) =>
        Macros.CalleeOf(call) is { } callee ? callee.Span : call.Span;
}
