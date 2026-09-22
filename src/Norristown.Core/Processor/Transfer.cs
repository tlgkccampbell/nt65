namespace Norristown.Processor;

/// <summary>What a statement does to the path running through it.</summary>
public enum Transfer
{
    /// <summary>Carries on to the statement after it.</summary>
    Through,

    /// <summary>Goes to its target when taken, and carries on when it is not.</summary>
    Branch,

    /// <summary>Goes to its target and never carries on.</summary>
    Jump,

    /// <summary>Calls, and carries on where the call returns to.</summary>
    Call,

    /// <summary>Returns, and the path ends here.</summary>
    Return,

    /// <summary>
    /// Goes somewhere the operand does not say: an indirect jump or call, or a computed
    /// target. This is what <c>.next</c> exists to answer.
    /// </summary>
    Elsewhere,
}
