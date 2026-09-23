namespace Norristown;

/// <summary>What a generated file is, of the kinds a build writes.</summary>
public enum OutputKind
{
    /// <summary>ca65 source: the program, which is what is assembled.</summary>
    Ca65,

    /// <summary>
    /// The line map written beside a ca65 file, which is not assembled: it says which source
    /// lines that file's lines came from, for <c>nt65 remap-dbg</c> to put into ld65's debug
    /// file after the link.
    /// </summary>
    LineMap,
}
