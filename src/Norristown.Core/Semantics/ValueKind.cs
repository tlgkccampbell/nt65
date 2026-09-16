namespace Norristown.Semantics;

/// <summary>What a <see cref="Value"/> holds.</summary>
public enum ValueKind
{
    /// <summary>Nothing: the expression is not constant, or a later stage will evaluate it.</summary>
    Unknown,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A string, which only <c>.strlen</c>, <c>.strat</c> and data directives accept.</summary>
    String,
}
