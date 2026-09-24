namespace Norristown.Processor;

/// <summary>
/// Provides the build configuration as a file of constants. Defines are visible everywhere,
/// as if declared and exported once, so nt65 gives them a source file of their own. Scoping,
/// evaluation and the editor then treat them as ordinary constants, and only emission treats
/// them differently, by writing each use as its value.
/// <para>
/// The file has no path on disk. It has a name so that a diagnostic about a define says where
/// the define came from, rather than pointing into a source file that merely used it.
/// </para>
/// </summary>
public static class Defines
{
    /// <summary>The logical path of the source file that holds the defines.</summary>
    public const string Path = "(defines)";

    /// <summary>
    /// Returns the defines as a source file, or null if there are none. The declarations are in
    /// name order, so the same configuration always produces the same text.
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
