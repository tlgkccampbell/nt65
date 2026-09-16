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

    /// <summary><c>.segment NAME {</c>.</summary>
    Segment,

    /// <summary>
    /// <c>.segment NAME</c> at file level, with no brace: the region runs to the next such line
    /// or to the end of the file.
    /// </summary>
    Region,

    /// <summary><c>.data name {</c>: mixed data, with members and positions of its own.</summary>
    Data,

    /// <summary><c>.data name: .byte[] {</c>: the values of an array, one line of them at a time.</summary>
    DataBody,

    /// <summary><c>.if</c>, <c>.elseif</c> or <c>.else</c>.</summary>
    If,

    /// <summary><c>.repeat</c>.</summary>
    Repeat,

    /// <summary><c>.each</c>.</summary>
    Each,

    /// <summary>A multi-line <c>.type T {</c> initializer, one <c>member = value</c> per line.</summary>
    RecordInitializer,

    /// <summary>A block argument of a macro call, first or continuation.</summary>
    MacroBlock,
}
