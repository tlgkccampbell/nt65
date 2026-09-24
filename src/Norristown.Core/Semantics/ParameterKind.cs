namespace Norristown.Semantics;

/// <summary>
/// Specifies what a macro parameter accepts, and therefore how it can be used in the body. The
/// kind is checked where the argument appears, at the call. It never changes how the argument is
/// read.
/// </summary>
public enum ParameterKind
{
    /// <summary>An expression, a constant or an address. This is the default when no kind is given.</summary>
    Expr,

    /// <summary>An expression nt65 can evaluate, which a condition may test.</summary>
    Const,

    /// <summary>A name, usable in an operand and as a branch target.</summary>
    Ident,

    /// <summary>A whole operand, braced at the call unless it is a plain address.</summary>
    Operand,

    /// <summary>One of the words listed after the kind, compared with <c>==</c> and <c>!=</c>.</summary>
    One,

    /// <summary>Every remaining positional argument, each of the element kind the list names.</summary>
    List,

    /// <summary>A trailing block, which a line naming the parameter splices into the body.</summary>
    Block,

    /// <summary>A member of the enum the kind names, by its bare name or its path.</summary>
    Enum,
}
