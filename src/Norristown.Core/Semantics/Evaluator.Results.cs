using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Decides which functions are closed, whose calls <see cref="FunctionResults"/> may keep, and
/// which functions can return text.
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Gets a value indicating whether this evaluator may use kept results and keep new ones.
    /// Only a query and a check may, since they come after the pass over the symbols, when every
    /// value a closed function can read is final.
    /// </summary>
    private bool KeepsResults => mode is EvaluationMode.Query or EvaluationMode.Check;

    /// <summary>
    /// Determines whether a built-in function's value depends on nothing but its arguments. The
    /// built-ins that read layout, the build or a macro's arguments do not qualify.
    /// </summary>
    private static bool Closed(BuiltinKind kind) => kind is BuiltinKind.Lobyte or BuiltinKind.Hibyte
        or BuiltinKind.Bankbyte or BuiltinKind.Loword or BuiltinKind.Hiword or BuiltinKind.Strlen
        or BuiltinKind.Strat or BuiltinKind.Strsub or BuiltinKind.Strcat or BuiltinKind.Min
        or BuiltinKind.Max or BuiltinKind.Select or BuiltinKind.Switch or BuiltinKind.Sqrt
        or BuiltinKind.Muldiv or BuiltinKind.Sin or BuiltinKind.Cos;

    /// <summary>
    /// Determines whether <paramref name="function"/> is closed. Its body may name only its own
    /// parameters, constants, members, lists, charmaps and other closed functions, and may call
    /// only closed built-ins. A repetition's name, which a function declared inside the
    /// repetition may read, is not allowed. Its defaults are held to the same rule, since a call to
    /// the function from another closed one may take them. A function that reaches itself is
    /// never closed.
    /// </summary>
    private bool IsClosed(Symbol function) => IsClosed(function, []);

    private bool IsClosed(Symbol function, HashSet<Symbol> visiting)
    {
        if (function.Items.Count == 0 || !visiting.Add(function))
            return false;
        try
        {
            return IsClosed(function.Items[0], function, visiting)
                && function.ParameterSymbols.All(parameter =>
                    parameter.Default is not { } given || IsClosed(given, function, visiting));
        }
        finally
        {
            visiting.Remove(function);
        }
    }

    private bool IsClosed(SyntaxNode node, Symbol function, HashSet<Symbol> visiting)
    {
        switch (node)
        {
            // A literal with a problem, or with a character outside ASCII, is reported at each
            // use, so a function holding one is evaluated at each use.
            case LiteralExpressionSyntax literal:
                return !literal.Token.ContainsDiagnostics && !literal.Token.Text.Any(c => c > 127);

            case NameExpressionSyntax name:
                return IsClosedName(name, function, visiting);

            case CallExpressionSyntax call:
                if (call.Callee is { } callee
                    ? !IsClosedName(callee, function, visiting)
                    : !Closed(call.BuiltinKind))
                {
                    return false;
                }
                return call.Arguments.Arguments.All(argument => IsClosed(argument, function, visiting));

            case NamedArgumentSyntax named:
                return IsClosed(named.Value, function, visiting);

            case ParenthesizedExpressionSyntax or UnaryExpressionSyntax or BinaryExpressionSyntax
                or SetExpressionSyntax or RangeSyntax:
                return node.ChildNodes.All(child => IsClosed(child, function, visiting));

            default:
                return false;
        }
    }

    private bool IsClosedName(NameExpressionSyntax name, Symbol function, HashSet<Symbol> visiting)
    {
        if (ResolvedInBody(name) is not { } symbol)
            return false;
        return symbol.Kind switch
        {
            // A parameter is a constant too, whose value is the argument the call binds.
            SymbolKind.Constant or SymbolKind.Member or SymbolKind.List or SymbolKind.Charmap => true,
            SymbolKind.Func => IsClosed(symbol, visiting),
            _ => false,
        };
    }

    /// <summary>
    /// Determines whether a call to <paramref name="function"/> can return text, judged from its
    /// body alone. Arithmetic, a number and a built-in that computes a number never give text, so
    /// a function whose body is one of these is a number for any arguments. Anything else, such
    /// as a name, which may be a parameter given text, counts as possibly text. A function that
    /// reaches itself counts as possibly text too.
    /// </summary>
    private bool MayBeText(Symbol function) =>
        FunctionResults.For(resolved).MayBeText(function, static (symbol, evaluator) => evaluator.MayBeText(symbol, []), this);

    private bool MayBeText(Symbol function, HashSet<Symbol> visiting)
    {
        if (function.Items.Count == 0 || !visiting.Add(function))
            return true;
        try
        {
            return MayBeText(function.Items[0], visiting);
        }
        finally
        {
            visiting.Remove(function);
        }
    }

    private bool MayBeText(SyntaxNode node, HashSet<Symbol> visiting) => node switch
    {
        // An operator reads its operands as numbers, and refuses text.
        NumberExpressionSyntax or CharacterExpressionSyntax or UnaryExpressionSyntax or BinaryExpressionSyntax => false,
        ParenthesizedExpressionSyntax parenthesized => MayBeText(parenthesized.Expression, visiting),

        // A charmap maps one character to its byte.
        CallExpressionSyntax { Callee: { } callee } => ResolvedInBody(callee) switch
        {
            { Kind: SymbolKind.Charmap } => false,
            { Kind: SymbolKind.Func } called => MayBeText(called, visiting),
            _ => true,
        },
        CallExpressionSyntax call => call.BuiltinKind is BuiltinKind.None or BuiltinKind.Strsub or BuiltinKind.Strcat
            or BuiltinKind.Select or BuiltinKind.Switch or BuiltinKind.Exprof,
        _ => true,
    };

    /// <summary>
    /// Returns the symbol a name in a function's body resolved to, or null for an indexed name
    /// and for a name that resolved to nothing.
    /// </summary>
    private Symbol? ResolvedInBody(NameExpressionSyntax name) =>
        !name.IsIndexed && name.LastPart is { } last && resolved.TryGetValue((name.Tree, last.Name.Span.Start), out var symbol)
            ? symbol
            : null;
}
