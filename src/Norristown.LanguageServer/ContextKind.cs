namespace Norristown.LanguageServer;

/// <summary>
/// Specifies the kind of context a line appears in, which decides what the line may contain. The
/// context comes from the enclosing blocks. A <c>.scope</c>, a segment block, a condition or a
/// repetition allows whatever the block around it allows, and every other block sets the context
/// itself.
/// </summary>
internal enum ContextKind
{
    /// <summary>
    /// A file's top level, which holds declarations and the items that configure the program.
    /// </summary>
    Item,

    /// <summary>
    /// A <c>.proc</c> or <c>.multiproc</c> body, a macro body or a block argument, which holds
    /// declarations and the instructions, labels and data that only code holds.
    /// </summary>
    Code,

    /// <summary>
    /// A <c>.data name { }</c> block, which holds data, declarations of its own, and positions.
    /// </summary>
    Data,

    /// <summary>
    /// The lines of values in an array's block, a <c>.list</c> or a <c>.charmap</c>, each of which
    /// starts with an expression.
    /// </summary>
    Values,

    /// <summary>A <c>.type T { }</c> initializer, which holds one <c>member = value</c> per line.</summary>
    Record,

    /// <summary>An <c>.enum</c> body, which holds one name per line, each optionally given a value.</summary>
    EnumMembers,

    /// <summary>
    /// A <c>.struct</c> or <c>.union</c> body, which holds one name per line, each given what it
    /// holds.
    /// </summary>
    TypeMembers,

    /// <summary>A block whose opener names no construct, where anything may be meant.</summary>
    Unknown,
}
