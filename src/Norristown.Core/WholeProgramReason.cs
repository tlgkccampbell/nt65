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

    /// <summary>
    /// The changed file declares a <c>.config</c>, before or after the edit, which any file's
    /// conditions may read.
    /// </summary>
    SettingsDeclared,

    /// <summary>
    /// The changed file names a different module or re-exports different names, in a program
    /// that declares a <c>.config</c>. Another file's condition may reach a setting through that
    /// module.
    /// </summary>
    SettingPathsChanged,

    /// <summary>The program's CPU changed.</summary>
    CpuChanged,

    /// <summary>
    /// A diagnostic from analyzing the program as a whole points at text the edit changed. Such a
    /// diagnostic is about a segment's <c>dp</c> or <c>bank</c>, or about the segment table.
    /// </summary>
    DiagnosticInEditedText,
}
