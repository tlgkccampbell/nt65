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
    /// The body of an <c>.enum</c>, <c>.struct</c> or <c>.union</c>: a scope of constants
    /// and offsets rather than of code.
    /// </summary>
    Type,
}
