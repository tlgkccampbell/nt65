namespace Norristown.Flow;

/// <summary>Specifies why the direct page is not known where an instruction reaches memory through it.</summary>
public enum UnknownPageReason
{
    /// <summary>
    /// An interrupt handler, or a routine it reaches, runs with whatever direct page the code it
    /// interrupted held, and has not set its own.
    /// </summary>
    Interrupted,

    /// <summary>The analysis lost track of D for another reason, such as a <c>pld</c> of an unknown value.</summary>
    Unknown,
}
