namespace Norristown.Flow;

/// <summary>Specifies what kind of thing an input that <see cref="InputSources"/> reports is.</summary>
public enum InputCategory
{
    /// <summary>A register: A, X or Y.</summary>
    Register,

    /// <summary>A processor flag: C, Z, N or V.</summary>
    Flag,

    /// <summary>One of the 65816's register widths, M or the index width.</summary>
    Width,

    /// <summary>A location in memory.</summary>
    Memory,
}
