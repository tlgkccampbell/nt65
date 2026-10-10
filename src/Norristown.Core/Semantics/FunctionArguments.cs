using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Matches the arguments of a call to the parameters of the <c>.func</c> it calls. The rules are
/// those of a macro call, which <see cref="MacroInvocation"/> applies, and the diagnostics are the
/// same. Positional arguments come first and bind in order. After them a call may name
/// parameters, each at most once, and a parameter the call leaves out takes its
/// <see cref="Symbol.Default"/>.
/// </summary>
public static class FunctionArguments
{
    /// <summary>
    /// Returns the value each of <paramref name="function"/>'s parameters takes in
    /// <paramref name="call"/>, in the order the parameters are declared, or null when the
    /// arguments do not match the parameters. Each value is an argument the call gives or a
    /// parameter's default.
    /// </summary>
    /// <param name="function">The function called.</param>
    /// <param name="call">The call.</param>
    /// <param name="report">
    /// Receives each problem with the arguments and where it is, or null to report nothing.
    /// </param>
    /// <param name="named">
    /// Receives each named argument that names one of the parameters, with that parameter, or null.
    /// </param>
    public static IReadOnlyList<ExpressionSyntax>? Match(
        Symbol function, CallExpressionSyntax call, Action<TextSpan, DiagnosticMessage>? report = null,
        Action<NamedArgumentSyntax, Symbol>? named = null)
    {
        var parameters = function.ParameterSymbols;
        var given = new ExpressionSyntax?[parameters.Count];
        var matched = true;
        var next = 0;
        var byName = false;
        var misplaced = false;
        // Every argument of a call is an expression: only a macro call parses a braced operand,
        // and a call that tries to give one is reported as missing an expression while it is
        // parsed. The casts below therefore change nothing for parsed source and only keep a
        // tree built by hand from being mistaken for a match.
        foreach (var argument in call.Arguments.Arguments)
        {
            if (argument is NamedArgumentSyntax namedArgument)
            {
                byName = true;
                var name = namedArgument.Name;
                var at = IndexOf(parameters, name.Text);
                if (at < 0)
                {
                    Fail(name.Span, Catalog.ParameterUnknown.Message(function.Name, name.Text));
                    continue;
                }
                named?.Invoke(namedArgument, parameters[at]);
                if (given[at] is not null)
                {
                    Fail(name.Span, Catalog.ArgumentGivenTwice.Message(parameters[at].Name));
                    continue;
                }
                given[at] = namedArgument.Value as ExpressionSyntax;
                continue;
            }
            if (byName)
            {
                Fail(argument.Span, Catalog.ArgumentAfterANamedOne);
                misplaced = true;
                continue;
            }
            if (next >= parameters.Count)
            {
                Fail(argument.Span, Count(function, call.Arguments.Arguments.TakeWhile(a => a is not NamedArgumentSyntax).Count()));
                continue;
            }
            given[next++] = argument as ExpressionSyntax;
        }

        var missing = new List<string>();
        for (var i = 0; i < parameters.Count; i++)
        {
            given[i] ??= parameters[i].Default;
            if (given[i] is null)
                missing.Add($"`{parameters[i].Name}`");
        }
        // A positional argument after a named one is most likely the one that is missing, and it
        // has been reported already, as a macro call reports it.
        if (missing.Count > 0 && !misplaced)
            Fail(call.Callee?.Span ?? call.Span, Catalog.ArgumentMissing.Message(function.Name, string.Join(", ", missing)));
        return matched ? [.. given.OfType<ExpressionSyntax>()] : null;

        void Fail(TextSpan span, DiagnosticMessage message)
        {
            matched = false;
            report?.Invoke(span, message);
        }
    }

    /// <summary>
    /// Returns the index of the parameter called <paramref name="name"/> among
    /// <paramref name="parameters"/>, or -1 when there is none.
    /// </summary>
    public static int IndexOf(IReadOnlyList<Symbol> parameters, string name)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Name == name)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Returns how many arguments a function or macro takes, such as <c>1 argument</c> or
    /// <c>1 to 2 arguments</c>.
    /// </summary>
    internal static string Arguments(int least, int most) =>
        least == most ? $"{most} {(most == 1 ? "argument" : "arguments")}" : $"{least} to {most} arguments";

    /// <summary>
    /// Returns the message for a call that gives <paramref name="function"/> more positional
    /// arguments than it has parameters, stating how many it takes and how many the call gives.
    /// </summary>
    private static DiagnosticMessage Count(Symbol function, int given)
    {
        var all = function.ParameterSymbols.Count;
        var least = function.ParameterSymbols.Count(parameter => parameter.Default is null);
        return Catalog.ArgumentCount.Message(function.Name, Arguments(least, all), given);
    }
}
