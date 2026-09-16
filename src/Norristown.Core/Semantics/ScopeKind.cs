namespace Norristown.Semantics;

/// <summary>
/// What kind of scope a <see cref="Scope"/> is. Only these three start one: segment blocks
/// and <c>.if</c> bodies place their contents in the scope around them.
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
    /// The body of an <c>.enum</c>, <c>.struct</c> or <c>.union</c>: a scope of constants
    /// and offsets rather than of code.
    /// </summary>
    Type,
}
