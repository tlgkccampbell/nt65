namespace Norristown.Semantics;

/// <summary>Specifies what an <see cref="Evaluator"/> is evaluating for.</summary>
internal enum EvaluationMode
{
    /// <summary>
    /// Answers a query about a program whose symbols have all been evaluated. Nothing is
    /// reported and no symbol is changed, because another thread may be reading it.
    /// </summary>
    Query,

    /// <summary>Evaluates the symbols it reaches and reports the problems it finds.</summary>
    Report,

    /// <summary>
    /// Evaluates a build's condition and reports the problems it finds. A condition is read
    /// before any declaration exists, so a name or a call in it can mean much less than it does
    /// elsewhere.
    /// </summary>
    Conditions,
}
