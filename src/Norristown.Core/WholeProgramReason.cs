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

    /// <summary>More than one file changed since the previous analysis.</summary>
    SeveralFilesChanged,

    /// <summary>An <c>.incbin</c> file is not the length it was.</summary>
    BinaryFileChanged,

    /// <summary>The changed file declares a segment, before or after the edit.</summary>
    SegmentsDeclared,

    /// <summary>The program's CPU changed.</summary>
    CpuChanged,

    /// <summary>
    /// A symbol of another file holds one of the changed file's symbols itself, rather than
    /// naming it where it is written: a <c>.tag</c> type, or a macro called or used.
    /// </summary>
    SymbolsHeldElsewhere,

    /// <summary>The changed file declares two symbols under one qualified name, before or after the edit.</summary>
    DuplicateNames,

    /// <summary>A constant of another file that the changed file reads is worth what it is through the changed file.</summary>
    EvaluationReachesBack,

    /// <summary>What other files can see of the changed file changed.</summary>
    InterfaceChanged,

    /// <summary>Another file says something about a place in the changed file that the edit rewrote.</summary>
    DiagnosticInEditedText,
}
