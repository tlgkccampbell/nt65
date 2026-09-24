using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Evaluates the two forms that test a value against a set: <c>value .in set</c>, which is 1 or
/// 0, and <c>.switch(value, set, result, ..., otherwise)</c>, which gives the result of the first
/// arm whose set holds the value. A set is written in brackets, as values and ranges such as
/// <c>[Mode::zpx, $00..$3f]</c>, or is the name of a <c>.list</c>, whose items are its values.
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Returns the arguments of a call that chooses one of its values, which is a <c>.select</c>
    /// or a <c>.switch</c>, or null when <paramref name="node"/> is neither. Only the first
    /// argument always has to be read, since it decides which of the others is.
    /// </summary>
    internal static IReadOnlyList<SyntaxNode>? ChoiceArguments(SyntaxNode node) =>
        node is CallExpressionSyntax { BuiltinKind: BuiltinKind.Select or BuiltinKind.Switch } call
            ? call.Arguments.Arguments
            : null;

    /// <summary>
    /// Returns a value indicating whether <paramref name="op"/> is <c>.in</c>, which arrives as a
    /// directive token, as <c>.mod</c> does.
    /// </summary>
    internal static bool IsIn(SyntaxToken op) =>
        op.Kind == SyntaxKind.Directive && op.Text.Equals(".in", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates <c>value .in set</c>, which is 1 when the set holds the value and 0 when it does
    /// not. It has no value while the value, or an item it would be compared with, is unknown.
    /// </summary>
    private Value In(BinaryExpressionSyntax binary)
    {
        var value = Evaluate(binary.Left);
        if (value.IsString)
            return Reject(binary.OperatorToken, value);
        return Holds(value, binary.Left, binary.Right, "`.in`") is { } holds ? Value.Of(holds) : Value.Unknown;
    }

    /// <summary>
    /// Evaluates <c>.switch(value, set, result, ..., otherwise)</c>. The value must be a constant,
    /// the arms are tried in order, and only the result that is chosen is read, so an arm after
    /// it may name what this build does not declare.
    /// </summary>
    private Value Switch(IReadOnlyList<SyntaxNode> arguments)
    {
        var value = Evaluate(arguments[0]);
        if (!value.IsWord && value.AsNumber() is null)
        {
            // A function's body is read once with no arguments, as for `.select`, and then every
            // arm is read, so that a cycle through any of them is found.
            if (context.ReadingBody)
            {
                foreach (var argument in arguments.Skip(1))
                {
                    if (argument is not SetExpressionSyntax)
                        Evaluate(argument);
                }
            }
            else
            {
                Report(arguments[0], Catalogue.SwitchValueNotConstant);
            }
            return Value.Unknown;
        }
        if (Chosen(value, arguments, report: true) is not { } chosen)
            return Value.Unknown;
        using (Enter(context with { Choosing = context.Choosing + 1 }))
            return Evaluate(chosen);
    }

    /// <summary>
    /// Returns the result a <c>.switch</c> chooses for <paramref name="value"/>, which is the one
    /// after the first set that holds it, or the last argument when no set does and the arms leave
    /// one over. Returns null when a set cannot be read, or when no arm holds the value and there
    /// is no last argument to fall back on; <paramref name="report"/> says whether that is
    /// reported.
    /// </summary>
    private SyntaxNode? Chosen(Value value, IReadOnlyList<SyntaxNode> arguments, bool report)
    {
        var arms = (arguments.Count - 1) / 2;
        for (var arm = 0; arm < arms; arm++)
        {
            var set = arguments[1 + (2 * arm)];
            switch (Holds(value, arguments[0], set, "an arm of `.switch`"))
            {
                case true:
                    return arguments[2 + (2 * arm)];
                case null:
                    if (report)
                        Report(set, Catalogue.SwitchSetNotConstant);
                    return null;
            }
        }
        if (arguments.Count % 2 == 0)
            return arguments[^1];
        if (report)
            Report(arguments[0], Catalogue.SwitchNoArm.Message(value.IsWord ? value.Text! : value.ToString()));
        return null;
    }

    /// <summary>
    /// Returns whether <paramref name="set"/> holds <paramref name="value"/>, or null when that is
    /// not known. The items are compared in order, and the first that holds the value ends the
    /// search. A word, such as a <c>one(...)</c> parameter's, is compared with the bare name of
    /// each item, as <c>==</c> compares it.
    /// </summary>
    /// <param name="value">The value being looked for.</param>
    /// <param name="valueNode">The expression the value came from, for comparing words.</param>
    /// <param name="set">The set in brackets, or the name of a list.</param>
    /// <param name="asker">What takes the set, as a message names it.</param>
    private bool? Holds(Value value, SyntaxNode valueNode, SyntaxNode set, string asker)
    {
        if (ItemsOf(set, asker) is not { } items)
            return null;
        var known = true;
        foreach (var (first, last) in items)
        {
            if (last is null)
            {
                var item = Evaluate(first);
                if ((value.IsWord || item.IsWord) && valueNode is ExpressionSyntax expression && first is ExpressionSyntax named)
                {
                    if (WordOf(value, expression) is { } word && WordOf(item, named) is { } other)
                    {
                        if (string.Equals(word, other, StringComparison.OrdinalIgnoreCase))
                            return true;
                        continue;
                    }
                }
                if (value.AsNumber() is { } number && item.AsNumber() is { } candidate)
                {
                    if (number == candidate)
                        return true;
                    continue;
                }
                if (item.IsString)
                    Report(first, Catalogue.SetItemIsText);
                known = false;
                continue;
            }

            if (value.AsNumber() is { } v && Evaluate(first).AsNumber() is { } low && Evaluate(last).AsNumber() is { } high)
            {
                if (v >= low && v <= high)
                    return true;
                continue;
            }
            known = false;
        }
        return known ? false : null;
    }

    /// <summary>
    /// Returns the items of a set, each a value or the two ends of a range, or null after
    /// reporting that <paramref name="set"/> is not a set. The items of a list are values.
    /// </summary>
    private IEnumerable<(SyntaxNode First, SyntaxNode? Last)>? ItemsOf(SyntaxNode set, string asker)
    {
        switch (set)
        {
            case SetExpressionSyntax brackets:
                return brackets.Items.Select(range => ((SyntaxNode)range.First, (SyntaxNode?)range.Last));
            case ParenthesizedExpressionSyntax parenthesized:
                return ItemsOf(parenthesized.Expression, asker);
            case NameExpressionSyntax name when SymbolOf(name) is { Kind: SymbolKind.List } list:
                return list.Items.Select(item => (item, (SyntaxNode?)null));
            default:
                Report(set, Catalogue.SetExpected.Message(asker));
                return null;
        }
    }

    /// <summary>
    /// Returns the value a <c>.select</c> or a <c>.switch</c> chooses, or null when
    /// <paramref name="node"/> is neither, or when what decides the choice is not a constant.
    /// </summary>
    private SyntaxNode? ChosenBy(SyntaxNode node)
    {
        if (ChoiceArguments(node) is not { } arguments)
            return null;
        if (node is CallExpressionSyntax { BuiltinKind: BuiltinKind.Select })
        {
            return arguments is [var condition, var ifHolds, var otherwise] && Evaluate(condition).AsNumber() is { } holds
                ? holds != 0 ? ifHolds : otherwise
                : null;
        }
        // A half-typed `.switch(` has fewer arguments than a value and one arm.
        if (arguments.Count < 3)
            return null;
        var value = Evaluate(arguments[0]);
        return value.IsWord || value.AsNumber() is not null ? Chosen(value, arguments, report: false) : null;
    }
}
