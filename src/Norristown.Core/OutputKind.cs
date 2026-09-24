namespace Norristown;

/// <summary>Identifies the kind of a generated file that a build writes.</summary>
public enum OutputKind
{
    /// <summary>ca65 source, which holds the program and is what is assembled.</summary>
    Ca65,

    /// <summary>
    /// The line map written beside a ca65 file. It is not assembled. It records which source
    /// lines that file's lines came from, for <c>nt65 remap-dbg</c> to put into ld65's debug
    /// file after the link.
    /// </summary>
    LineMap,
}
