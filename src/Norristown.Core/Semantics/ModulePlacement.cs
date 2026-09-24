namespace Norristown.Semantics;

/// <summary>
/// Specifies whether a module's declaration allows another module to place it with
/// <c>.place</c>.
/// </summary>
public enum ModulePlacement
{
    /// <summary>Declared with <c>.module m</c>. The module stands alone, and placing it is an error.</summary>
    Alone,

    /// <summary>
    /// Declared with <c>.module m: placed</c>. Exactly one module places it, and it has no output
    /// of its own.
    /// </summary>
    Placed,

    /// <summary>
    /// Declared with <c>.module m: placeable</c>. At most one module places it, and it stands
    /// alone when no module does.
    /// </summary>
    Placeable,
}
