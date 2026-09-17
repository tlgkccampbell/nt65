namespace Norristown;

/// <summary>What a generated file is, of the kinds a build writes.</summary>
public enum OutputKind
{
    /// <summary>ca65 source: the program, which is what is assembled.</summary>
    Ca65,

    /// <summary>
    /// The line map beside one, which is not assembled: it says where the lines of that ca65
    /// came from, for <c>nt65 remap-dbg</c> to put into ld65's debug file after the link.
    /// </summary>
    LineMap,
}
