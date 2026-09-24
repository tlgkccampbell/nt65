namespace Norristown.Processor;

/// <summary>Specifies how a statement affects the control flow through it.</summary>
public enum Transfer
{
    /// <summary>Continues to the statement after it.</summary>
    Through,

    /// <summary>Goes to its target when taken, and continues when it is not.</summary>
    Branch,

    /// <summary>Goes to its target and never continues.</summary>
    Jump,

    /// <summary>Calls its target, and continues where the call returns.</summary>
    Call,

    /// <summary>Returns, which ends the path through it.</summary>
    Return,

    /// <summary>
    /// Goes somewhere the operand does not name, as with an indirect jump or call, or a
    /// computed target. The source uses a <c>.next</c> to say where such a statement goes.
    /// </summary>
    Elsewhere,
}
