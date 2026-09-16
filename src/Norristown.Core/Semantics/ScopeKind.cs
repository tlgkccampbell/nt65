namespace Norristown.Semantics;

/// <summary>
/// What kind of scope a <see cref="Scope"/> is. Segment blocks and <c>.if</c> bodies start
/// none: they place their contents in the scope around them.
/// </summary>
public enum ScopeKind
{
    /// <summary>One file's top level.</summary>
    File,

    /// <summary>A <c>.proc</c> body.</summary>
    Proc,

    /// <summary>A <c>.scope</c> body, named or anonymous.</summary>
    Scope,

    /// <summary>
    /// A <c>.macro</c> body. It carries the macro's name, so what it declares is named after
    /// it in the output, but nothing outside the body can reach any of it.
    /// </summary>
    Macro,

    /// <summary>
    /// A block argument of a macro call. It is the caller's code and reads the caller's
    /// names, but the same block may be spliced in more than one place, so what it declares
    /// is private to it and may only be a cheap local.
    /// </summary>
    BlockArgument,

    /// <summary>
    /// A <c>.repeat</c> or <c>.each</c> body, which holds the name the repetition binds. It is
    /// written out once per turn, so what it declares is a different name on every turn, and
    /// nothing outside the body can reach any of it.
    /// </summary>
    Repetition,

    /// <summary>
    /// The body of an <c>.enum</c>, <c>.struct</c> or <c>.union</c>: a scope of constants
    /// and offsets rather than of code.
    /// </summary>
    Type,
}
