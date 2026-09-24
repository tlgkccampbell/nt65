namespace Norristown;

/// <summary>
/// Specifies why an analysis analyzed every file of the program rather than only what an edit
/// changed.
/// </summary>
public enum WholeProgramReason
{
    /// <summary>There was no analysis of the program to start from.</summary>
    NoPreviousAnalysis,

    /// <summary>The previous analysis was of a different project.</summary>
    ProjectChanged,

    /// <summary>A file was added to the program or removed from it.</summary>
    FilesAddedOrRemoved,

    /// <summary>An <c>.incbin</c> file is not the length it was.</summary>
    BinaryFileChanged,

    /// <summary>The changed file declares a segment, before or after the edit.</summary>
    SegmentsDeclared,

    /// <summary>The program's CPU changed.</summary>
    CpuChanged,

    /// <summary>
    /// The edit changed which branches another file's conditions take, or what is wrong with
    /// them, through a value the configuration decides that a condition there tests.
    /// </summary>
    ConditionsChanged,

    /// <summary>
    /// A diagnostic from analyzing the program as a whole points at text the edit changed. Such a
    /// diagnostic is about a segment's <c>dp</c> or <c>bank</c>, or about the segment table.
    /// </summary>
    DiagnosticInEditedText,
}
