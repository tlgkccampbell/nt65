namespace Norristown.Flow;

/// <summary>Specifies how sure <see cref="InputSources"/> is of a source it reports.</summary>
public enum SourceConfidence
{
    /// <summary>The analysis followed every path to the source.</summary>
    Proven,

    /// <summary>
    /// The source is a best guess, as a store to memory is when something else might have written
    /// the same location. A best-effort source feeds no check.
    /// </summary>
    BestEffort,
}
