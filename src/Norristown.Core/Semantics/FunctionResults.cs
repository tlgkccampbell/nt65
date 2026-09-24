using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Norristown.Semantics;

/// <summary>
/// Holds the results of the calls to <c>.func</c> functions that one analysis of a program has
/// evaluated, so that a call made again with the same arguments is not evaluated again.
/// <para>
/// A result is kept only for a <em>closed</em> function, whose value depends on nothing but its
/// arguments once every symbol has been evaluated. The results belong to the program's resolved
/// names, which every analysis builds afresh, so a result never outlives the values it read. A
/// data declaration's values are evaluated by each check that layout makes and again as they
/// are written out, so a table built by functions is otherwise evaluated many times over.
/// </para>
/// </summary>
internal sealed class FunctionResults
{
    private static readonly ConditionalWeakTable<object, FunctionResults> ByNames = new();

    private readonly ConcurrentDictionary<Symbol, bool> closed = new();
    private readonly ConcurrentDictionary<Call, Value> results = new();

    /// <summary>Returns the results kept for the analysis whose resolved names are <paramref name="names"/>.</summary>
    public static FunctionResults For(object names) => ByNames.GetValue(names, static _ => new FunctionResults());

    /// <summary>
    /// Checks whether <paramref name="function"/> is closed, asking <paramref name="decide"/> the
    /// first time and remembering the answer.
    /// </summary>
    public bool IsClosed(Symbol function, Func<Symbol, bool> decide) => closed.GetOrAdd(function, decide);

    /// <summary>Returns the result kept for a call, or null when none is kept.</summary>
    public Value? Find(Symbol function, Value[] arguments) =>
        results.TryGetValue(new Call(function, arguments), out var value) ? value : null;

    /// <summary>Keeps the result of a call.</summary>
    public void Keep(Symbol function, Value[] arguments, Value value) => results[new Call(function, arguments)] = value;

    /// <summary>Represents a call of a function with the values of its arguments.</summary>
    /// <param name="Function">The function called.</param>
    /// <param name="Arguments">The values of its arguments, in order.</param>
    private readonly record struct Call(Symbol Function, Value[] Arguments)
    {
        /// <inheritdoc/>
        public bool Equals(Call other) =>
            ReferenceEquals(Function, other.Function) && Arguments.AsSpan().SequenceEqual(other.Arguments);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(Function));
            foreach (var argument in Arguments)
                hash.Add(argument);
            return hash.ToHashCode();
        }
    }
}
