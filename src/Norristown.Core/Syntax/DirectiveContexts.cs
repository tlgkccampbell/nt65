namespace Norristown.Syntax;

/// <summary>
/// Specifies the kinds of body a line can be in, as far as they decide which directives may
/// begin it. The innermost block around a line gives its context. A <c>.scope</c>, a segment
/// block, a condition or a repetition takes the context of the block around it.
/// </summary>
[Flags]
public enum DirectiveContexts
{
    /// <summary>No context.</summary>
    None = 0,

    /// <summary>
    /// A file's top level, which holds declarations and the items that configure the program.
    /// </summary>
    Items = 1 << 0,

    /// <summary>A <c>.proc</c> or <c>.multiproc</c> body, a macro body or a block argument.</summary>
    Code = 1 << 1,

    /// <summary>A <c>.data name { }</c> block.</summary>
    Data = 1 << 2,

    /// <summary>The lines of values in an array's block, a <c>.list</c> or a <c>.charmap</c>.</summary>
    Values = 1 << 3,

    /// <summary>An <c>.enum</c> body.</summary>
    EnumMembers = 1 << 4,

    /// <summary>A <c>.struct</c> or <c>.union</c> body.</summary>
    TypeMembers = 1 << 5,
}
