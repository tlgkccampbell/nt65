using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Identifies one statement in one <see cref="Expansion"/>. A statement inside a macro or a
/// repetition is laid out and analyzed once for each expansion it appears in, so its position
/// alone does not say which of them is meant.
/// </summary>
/// <param name="Position">The position of the statement in its syntax tree.</param>
/// <param name="On">The expansion the statement belongs to, or null outside every expansion.</param>
internal readonly record struct StepKey(int Position, Expansion? On)
{
    /// <summary>
    /// Returns the key that identifies <paramref name="statement"/> in the expansion
    /// <paramref name="on"/>.
    /// </summary>
    public static StepKey Of(SyntaxNode statement, Expansion? on) => new(statement.Position, on);
}
