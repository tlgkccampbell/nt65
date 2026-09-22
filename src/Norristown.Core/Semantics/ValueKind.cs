namespace Norristown.Semantics;

/// <summary>What a <see cref="Value"/> holds.</summary>
public enum ValueKind
{
    /// <summary>Nothing: the expression is not constant, or only the linker knows it.</summary>
    Unknown,

    /// <summary>A number.</summary>
    Number,

    /// <summary>
    /// A string of bytes, which data directives, <c>.strz</c>, a charmap, <c>.strlen</c>,
    /// <c>.strat</c>, <c>.strsub</c> and <c>.strcat</c> accept, and a function may return.
    /// </summary>
    String,

    /// <summary>
    /// A bare word: what a <c>one</c> parameter stands for, and what an <c>.each</c> over a
    /// list of them binds. A word is never looked up and is only ever compared with another.
    /// </summary>
    Word,
}
