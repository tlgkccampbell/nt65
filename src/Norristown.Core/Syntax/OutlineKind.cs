namespace Norristown.Syntax;

/// <summary>Specifies the kind of declaration an <see cref="OutlineItem"/> represents.</summary>
public enum OutlineKind
{
    /// <summary>A <c>.proc</c>, with or without a body.</summary>
    Proc,

    /// <summary>A <c>.scope</c>, named or anonymous.</summary>
    Scope,

    /// <summary>A segment block, or a region.</summary>
    Segment,

    /// <summary>A <c>.macro</c>, with its parameters.</summary>
    Macro,

    /// <summary>A label, <c>name:</c> or <c>@name:</c>.</summary>
    Label,

    /// <summary>A constant or address alias, <c>name = expr</c>.</summary>
    Constant,

    /// <summary>A data declaration, <c>.data name: ...</c> or <c>.data name { }</c>.</summary>
    Data,

    /// <summary>A named <c>.enum</c>, <c>.struct</c> or <c>.union</c>.</summary>
    Type,

    /// <summary>A <c>.func</c>, with its parameters and body.</summary>
    Function,
}
