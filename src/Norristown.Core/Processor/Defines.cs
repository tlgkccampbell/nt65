namespace Norristown.Processor;

/// <summary>
/// The build configuration as a file of constants. Defines are visible everywhere,
/// as if declared and exported once, so nt65 gives them a source file of their own: they
/// are then ordinary constants to scoping, evaluation and the editor, and only emission
/// treats them differently by writing each use as its value.
/// <para>
/// The file has no path on disk. It is named so that a diagnostic about a define says where
/// the define came from rather than pointing into a source file that merely used it.
/// </para>
/// </summary>
public static class Defines
{
    /// <summary>The logical path of the file the defines are read as.</summary>
    public const string Path = "(defines)";

    /// <summary>
    /// The defines as a source file, or null when there are none. Declarations come out in
    /// name order, so the same configuration always reads the same way.
    /// </summary>
    public static SourceFile? Source(IReadOnlyList<Define> defines)
    {
        if (defines.Count == 0)
            return null;
        var text = string.Concat(defines
            .OrderBy(define => define.Name, StringComparer.Ordinal)
            .Select(define => $"{define.Name} = {define.Value}\n"));
        return new SourceFile(Path, text);
    }
}
