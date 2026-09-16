namespace Norristown;

/// <summary>
/// Why an analysis analyzed every file of the program rather than only what an edit changed.
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
    /// What the program as a whole found says something about a place an edit rewrote: a
    /// segment's <c>dp</c> or <c>bank</c>, or the segment table.
    /// </summary>
    DiagnosticInEditedText,
}
