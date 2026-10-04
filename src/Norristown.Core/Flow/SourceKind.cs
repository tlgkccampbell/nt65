namespace Norristown.Flow;

/// <summary>
/// Specifies what set a value that an instruction reads, as <see cref="InputSources"/> reports it.
/// </summary>
public enum SourceKind
{
    /// <summary>An instruction in the routine wrote the value. A transfer such as <c>tax</c> counts as a write.</summary>
    Instruction,

    /// <summary>
    /// An instruction from a macro expansion wrote the value. The source is the outermost macro call
    /// in the caret's file.
    /// </summary>
    Macro,

    /// <summary>A call wrote the value, or the routine it calls does not keep it.</summary>
    Call,

    /// <summary>No write in the routine reaches the caret on some path, so the routine's caller set the value.</summary>
    Entry,

    /// <summary>
    /// The analysis lost track of the value. The source names the line that caused that, which is
    /// called the blocker.
    /// </summary>
    Unknown,
}
