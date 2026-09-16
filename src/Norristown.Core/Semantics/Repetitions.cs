using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a <c>.repeat</c> or an <c>.each</c> unrolls to: one turn per count, per list item or
/// per enum member. Nothing of it reaches the output, so layout and emission each ask for
/// the turns and walk the body once per turn.
/// </summary>
public static class Repetitions
{
    /// <summary>
    /// The turns <paramref name="block"/> stands for, inside <paramref name="outer"/>.
    /// A count or a list nt65 cannot read is reported into
    /// <paramref name="diagnostics"/>, when a caller wants to hear about it, and stands for
    /// no turns at all.
    /// </summary>
    public static IReadOnlyList<Expansion> Of(
        SemanticModel model, SyntaxNode block, Expansion? outer, List<Diagnostic>? diagnostics)
    {
        if (block.ChildNodes.Length == 0 || block.ChildNodes[0].Statement is not { } opener)
            return [];

        var binding = BindingOf(model, opener);
        var counted = opener.ChildNodes.FirstOrDefault();
        if (counted is null)
            return [];

        return opener.Kind == SyntaxKind.RepeatDirective
            ? Counted(model, block, counted, binding, outer, diagnostics)
            : Walked(model, block, counted, binding, outer, diagnostics);
    }

    /// <summary>The name a repetition binds, or null when it names none.</summary>
    public static Symbol? BindingOf(SemanticModel model, SyntaxNode opener)
    {
        foreach (var token in opener.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && model.ReferenceAt(token.Span.Start) is { IsDeclaration: true } declared)
            {
                return declared.Symbol;
            }
        }
        return null;
    }

    /// <summary><c>.repeat count, i</c>: the name counts from zero, as an index does.</summary>
    private static IReadOnlyList<Expansion> Counted(
        SemanticModel model, SyntaxNode block, SyntaxNode counted, Symbol? binding, Expansion? outer,
        List<Diagnostic>? diagnostics)
    {
        if (model.ValueOf(counted, outer).AsNumber() is not { } count)
        {
            Report(model, diagnostics, counted, "a `.repeat` count is a constant, and this is not one");
            return [];
        }
        if (count < 0)
        {
            Report(model, diagnostics, counted, $"a `.repeat` count cannot be negative, and this one is {count}");
            return [];
        }

        var turns = new List<Expansion>((int)count);
        for (var i = 0; i < count; i++)
            turns.Add(Expansion.Turn(outer, block, binding, Value.Of(i), null, i));
        return turns;
    }

    /// <summary>
    /// <c>.each what, h</c>: the name is each item of a list, or each member of an enum, in
    /// the order they are written.
    /// </summary>
    private static IReadOnlyList<Expansion> Walked(
        SemanticModel model, SyntaxNode block, SyntaxNode walked, Symbol? binding, Expansion? outer,
        List<Diagnostic>? diagnostics)
    {
        // A `list` parameter walks whatever the call gave it. Where its items are words, the
        // binding is the word itself, which is all a condition can do with one.
        if (model.SymbolOf(walked) is { Kind: SymbolKind.MacroParameter, Parameter: { } parameter }
            && parameter.Kind == ParameterKind.List)
        {
            if (model.ArgumentFor(parameter.Symbol, outer) is not { } argument)
                return [];
            var words = parameter.Accepts.Element?.Kind == ParameterKind.One;
            return [.. argument.Items.Select((item, i) => Expansion.Turn(
                outer, block, binding, words ? Value.Word(Word(item)) : Value.Unknown, words ? null : item, i))];
        }

        // A list item is kept as it was written: the items may be labels, which have no
        // value at all, and a name standing for one has to be that label.
        if (model.ItemsOf(walked) is { } items)
            return [.. items.Select((item, i) => Expansion.Turn(outer, block, binding, Value.Unknown, item, i))];

        if (model.SymbolOf(walked) is { Kind: SymbolKind.Enum, Body: { } members })
            return [.. members.Symbols.Select(
                (member, i) => Expansion.Turn(outer, block, binding, member.Value, null, i))];

        Report(model, diagnostics, walked, "`.each` walks a list or an enum, and this is neither");
        return [];
    }

    /// <summary>The word an item was written as, for a list of them.</summary>
    private static string Word(SyntaxNode item) =>
        item.ChildTokens.Length > 0 ? item.ChildTokens[0].Text : item.GetText().Trim();

    private static void Report(
        SemanticModel model, List<Diagnostic>? diagnostics, SyntaxNode node, string message) =>
        diagnostics?.Add(new Diagnostic(model.Tree.GetSpan(node.Span), Severity.Error, message));
}
