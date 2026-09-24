namespace Norristown.Semantics;

/// <summary>Specifies what an <see cref="Evaluator"/> is evaluating for.</summary>
internal enum EvaluationMode
{
    /// <summary>
    /// Answers a query about a program whose symbols have all been evaluated. Nothing is
    /// reported and no symbol is changed, because another thread may be reading it.
    /// </summary>
    Query,

    /// <summary>
    /// Evaluates the symbols of a program that is being built, and the symbols each one reaches,
    /// and reports the problems it finds.
    /// </summary>
    Report,

    /// <summary>
    /// Evaluates an expression that is no symbol's value, once every symbol has been evaluated,
    /// and reports the problems it finds. A symbol it names is read as it stands and never
    /// evaluated again, because another thread may be reading it.
    /// </summary>
    Check,

    /// <summary>
    /// Evaluates a build's condition and reports the problems it finds. A condition is read
    /// before any declaration exists, so a name or a call in it can mean much less than it does
    /// elsewhere.
    /// </summary>
    Conditions,
}
