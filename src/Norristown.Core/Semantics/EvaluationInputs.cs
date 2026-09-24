namespace Norristown.Semantics;

/// <summary>
/// Represents what an <see cref="Evaluator"/> reads besides the syntax it is given. The
/// segments and the names are always known. Everything else is known only to some callers, and
/// a value that needs something its caller does not know is unknown.
/// </summary>
/// <param name="Segments">The segments of the program, which give <c>*</c> and a label their address size.</param>
/// <param name="Names">The resolved names, and what each name bound where evaluation starts stands for.</param>
internal sealed record EvaluationInputs(SegmentTable Segments, BoundNames Names)
{
    /// <summary>Gets the length of the binary file at a path, for a directive that includes one.</summary>
    public Func<string, long?>? BinaryLength { get; init; }

    /// <summary>
    /// Gets how many bytes a routine or a data declaration takes. Only layout knows this, so
    /// only a caller that has laid out the file can provide it.
    /// </summary>
    public Func<Symbol, long?>? Spans { get; init; }

    /// <summary>
    /// Gets the cycles one pass over a span of code costs. Only layout knows this, so only a
    /// caller that has laid out the file can provide it.
    /// </summary>
    public Func<Symbol, Symbol, bool, CycleSpan>? Cycles { get; init; }

    /// <summary>
    /// Gets which branches the build takes, for the conditionals in a data body. Without it,
    /// every condition is evaluated as a condition inside an expansion would be.
    /// </summary>
    public Configuration? Configuration { get; init; }
}
