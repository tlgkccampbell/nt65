namespace Norristown.Semantics;

/// <summary>
/// What kind of scope a <see cref="Scope"/> is. Only these three start one: segment blocks
/// and <c>.if</c> bodies place their contents in the scope around them (§5.2, §6.2).
/// </summary>
public enum ScopeKind
{
    /// <summary>One file's top level.</summary>
    File,

    /// <summary>A <c>.proc</c> body.</summary>
    Proc,

    /// <summary>A <c>.scope</c> body, named or anonymous.</summary>
    Scope,
}
