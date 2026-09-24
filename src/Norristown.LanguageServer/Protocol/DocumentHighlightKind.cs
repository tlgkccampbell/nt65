namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies how a highlighted name is used, as LSP numbers it.</summary>
internal enum DocumentHighlightKind
{
    /// <summary>A use that neither reads nor declares the name.</summary>
    Text = 1,

    /// <summary>A use of the name.</summary>
    Read = 2,

    /// <summary>The declaration of the name.</summary>
    Write = 3,
}
