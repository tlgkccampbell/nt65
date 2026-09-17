namespace Norristown.LanguageServer.Protocol;

/// <summary>What a completion is, as LSP numbers it. Only the kinds nt65 uses are listed.</summary>
internal enum CompletionItemKind
{
    /// <summary>A mnemonic.</summary>
    Text = 1,

    /// <summary>A routine or a function.</summary>
    Function = 3,

    /// <summary>A struct or union member.</summary>
    Field = 5,

    /// <summary>A data declaration or a list.</summary>
    Variable = 6,

    /// <summary>A module, or a scope.</summary>
    Module = 9,

    /// <summary>A named argument.</summary>
    Property = 10,

    /// <summary>An enum.</summary>
    Enum = 13,

    /// <summary>A processor-state item.</summary>
    Keyword = 14,

    /// <summary>A macro.</summary>
    Snippet = 15,

    /// <summary>A label.</summary>
    Reference = 18,

    /// <summary>An enum member.</summary>
    EnumMember = 20,

    /// <summary>A constant or a define.</summary>
    Constant = 21,

    /// <summary>A struct or union.</summary>
    Struct = 22,

    /// <summary>A macro parameter, a repetition binding or a signature set.</summary>
    TypeParameter = 25,
}
