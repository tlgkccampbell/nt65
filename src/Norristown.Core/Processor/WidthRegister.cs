namespace Norristown.Processor;

/// <summary>Specifies which register's width sizes a 65816 immediate.</summary>
public enum WidthRegister
{
    /// <summary>The accumulator, set by the M flag.</summary>
    A,

    /// <summary>X and Y, set together by the X flag.</summary>
    Index,
}
