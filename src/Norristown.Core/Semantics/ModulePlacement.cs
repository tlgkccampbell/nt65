namespace Norristown.Semantics;

/// <summary>What a module's declaration says about another module placing it.</summary>
public enum ModulePlacement
{
    /// <summary><c>.module m</c>: it stands alone, and placing it is an error.</summary>
    Alone,

    /// <summary><c>.module m: placed</c>: exactly one module places it, and it has no output of its own.</summary>
    Placed,

    /// <summary><c>.module m: placeable</c>: at most one module places it, and it stands alone when none does.</summary>
    Placeable,
}
