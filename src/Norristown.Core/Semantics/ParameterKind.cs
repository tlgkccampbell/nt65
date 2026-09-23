namespace Norristown.Semantics;

/// <summary>
/// What a macro parameter takes, and so what it can be used as in the body. The kind is
/// checked where the argument is written, at the call; it never changes how the argument
/// is read.
/// </summary>
public enum ParameterKind
{
    /// <summary>An expression, a constant or an address. The default when none is written.</summary>
    Expr,

    /// <summary>An expression nt65 can work out, which a condition may test.</summary>
    Const,

    /// <summary>A name, usable in an operand and as a branch target.</summary>
    Ident,

    /// <summary>A whole operand, braced at the call unless it is a plain address.</summary>
    Operand,

    /// <summary>One of the words listed after it, compared with <c>==</c> and <c>!=</c>.</summary>
    One,

    /// <summary>Every remaining positional argument, each of the kind inside it.</summary>
    List,

    /// <summary>A trailing block, which a line naming the parameter splices.</summary>
    Block,

    /// <summary>A member of the enum the kind names, by its bare name or its path.</summary>
    Enum,
}
