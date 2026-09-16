using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One turn of a <c>.repeat</c> or an <c>.each</c>, and the turns of the repetitions around
/// it. A body is read once and written out once per turn, so this is what tells the two
/// apart: what every name a repetition binds is worth, and which turn a line belongs to.
/// <para>
/// Layout and emission walk the same blocks in the same order and work these out
/// independently, so they are compared by what they hold rather than by identity.
/// </para>
/// </summary>
/// <param name="Outer">The turn of the repetition around this one, or null at the top.</param>
/// <param name="Binding">The name this repetition binds, or null when it names none.</param>
/// <param name="Value">What the name is worth on this turn.</param>
/// <param name="Item">The item it stands for, for an <c>.each</c> over a list.</param>
/// <param name="Index">Which turn it is, from zero.</param>
public sealed record Iteration(Iteration? Outer, Symbol? Binding, Value Value, SyntaxNode? Item, int Index)
{
    /// <summary>
    /// What every name bound on this turn and on the turns around it is worth. A repetition
    /// inside another sees both, and the inner one wins where they share a name.
    /// <para>
    /// This is worked out each time rather than kept: a record compares every field it has,
    /// including a private one, so a cache would make two turns that hold the same thing
    /// unequal depending on which of them had been asked.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<Symbol, Bound> Bindings()
    {
        var bound = new Dictionary<Symbol, Bound>();
        for (var turn = this; turn is not null; turn = turn.Outer)
        {
            if (turn.Binding is { } symbol)
                bound.TryAdd(symbol, new Bound(turn.Value, turn.Item));
        }
        return bound;
    }

    /// <summary>The bindings of <paramref name="iteration"/>, which may be no iteration at all.</summary>
    public static IReadOnlyDictionary<Symbol, Bound>? BindingsOf(Iteration? iteration) => iteration?.Bindings();

    /// <summary>
    /// What one name is worth on one turn. An item is kept as the expression it was written
    /// as, not only as a number: a list of labels has no numbers, and a name standing for one
    /// has to be that label everywhere — in what it is worth, how wide an address it is, and
    /// what is written out for it.
    /// </summary>
    /// <param name="Value">The number, where there is one.</param>
    /// <param name="Item">The expression it stands for, or null when it stands for a number.</param>
    public readonly record struct Bound(Value Value, SyntaxNode? Item);
}
