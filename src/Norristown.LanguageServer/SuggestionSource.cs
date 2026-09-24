namespace Norristown.LanguageServer;

/// <summary>
/// Specifies where a <see cref="Suggestion"/> comes from. Completion reshapes some items by their
/// source once they are gathered, so the source is recorded apart from the item's kind, which
/// only chooses its icon.
/// </summary>
internal enum SuggestionSource
{
    /// <summary>A directive, which a client that accepts snippets gets as its whole block.</summary>
    Directive,

    /// <summary>
    /// A word of the language or of a parameter's kind, such as a signature item, an operand form
    /// or a word a <c>one(...)</c> lists.
    /// </summary>
    Word,

    /// <summary>An instruction this CPU has.</summary>
    Instruction,

    /// <summary>A declared name other than a macro, or a macro parameter named as an argument.</summary>
    Symbol,

    /// <summary>A macro, which the start of a statement calls with <c>!(</c>.</summary>
    Macro,

    /// <summary>A module, or the next part of a module path.</summary>
    Module,

    /// <summary>A built-in function.</summary>
    Builtin,
}
