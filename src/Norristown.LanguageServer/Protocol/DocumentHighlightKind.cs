namespace Norristown.LanguageServer.Protocol;

/// <summary>How a highlighted name is used, as LSP numbers it.</summary>
internal enum DocumentHighlightKind
{
    /// <summary>Neither read nor written.</summary>
    Text = 1,

    /// <summary>A use of the name.</summary>
    Read = 2,

    /// <summary>Where the name is declared.</summary>
    Write = 3,
}
