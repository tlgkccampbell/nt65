namespace Norristown.LanguageServer;

/// <summary>
/// The kind of context a line is written in, which decides what the line may contain. It comes
/// from the enclosing blocks: a <c>.scope</c>, a segment block, a condition or a repetition
/// allows whatever the block around it allows, and every other block sets the context itself.
/// </summary>
internal enum Place
{
    /// <summary>A file's top level: declarations, and the items that configure the program.</summary>
    Item,

    /// <summary>
    /// A <c>.proc</c> body, a macro body or a block argument: declarations, and the
    /// instructions, labels and data that only code holds.
    /// </summary>
    Code,

    /// <summary>A <c>.data name { }</c> block: data, declarations of its own, and positions.</summary>
    Data,

    /// <summary>
    /// The lines of values in an array's block, a <c>.list</c> or a <c>.charmap</c>: each
    /// starts with an expression.
    /// </summary>
    Values,

    /// <summary>A <c>.type T { }</c> initializer: one <c>member = value</c> a line.</summary>
    Record,

    /// <summary>An <c>.enum</c> body: a name a line, each optionally given a value.</summary>
    EnumMembers,

    /// <summary>A <c>.struct</c> or <c>.union</c> body: a name a line, each given what it holds.</summary>
    TypeMembers,

    /// <summary>A block whose opener names no construct, where anything may be meant.</summary>
    Unknown,
}
