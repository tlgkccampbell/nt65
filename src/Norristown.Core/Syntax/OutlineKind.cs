namespace Norristown.Syntax;

/// <summary>What an <see cref="OutlineItem"/> declares.</summary>
public enum OutlineKind
{
    /// <summary>A <c>.proc</c>, with or without a body.</summary>
    Proc,

    /// <summary>A <c>.scope</c>, named or anonymous.</summary>
    Scope,

    /// <summary>A segment block, written in full or as a shortcut.</summary>
    Segment,

    /// <summary>A <c>.macro</c>, with its parameters.</summary>
    Macro,

    /// <summary>A label, <c>name:</c> or <c>@name:</c>.</summary>
    Label,

    /// <summary>A constant or address alias, <c>name = expr</c>.</summary>
    Constant,
}
