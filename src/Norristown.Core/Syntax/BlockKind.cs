namespace Norristown.Syntax;

/// <summary>The kind of block a line opens, from the statement that ends in <c>{</c>.</summary>
public enum BlockKind
{
    /// <summary>The line opens no block.</summary>
    None,

    /// <summary>A block whose opener names no known block construct.</summary>
    Unknown,

    /// <summary><c>.proc</c>.</summary>
    Proc,

    /// <summary><c>.scope</c>.</summary>
    Scope,

    /// <summary><c>.macro</c>.</summary>
    Macro,

    /// <summary><c>.enum</c>.</summary>
    Enum,

    /// <summary><c>.struct</c>.</summary>
    Struct,

    /// <summary><c>.union</c>.</summary>
    Union,

    /// <summary><c>.charmap</c>.</summary>
    Charmap,

    /// <summary><c>.list</c>.</summary>
    List,

    /// <summary><c>.segment</c>, or one of the shortcuts such as <c>.rodata</c>.</summary>
    Segment,

    /// <summary><c>.if</c>, <c>.elseif</c> or <c>.else</c>.</summary>
    If,

    /// <summary><c>.repeat</c>.</summary>
    Repeat,

    /// <summary><c>.each</c>.</summary>
    Each,

    /// <summary>A multi-line <c>.tag T {</c> initializer.</summary>
    TagInitializer,

    /// <summary>A block argument of a macro call, first or continuation.</summary>
    MacroBlock,
}
