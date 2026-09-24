namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies how a client interprets the text a completion inserts.</summary>
internal enum InsertTextFormat
{
    /// <summary>The text is inserted as it is. A client that declares nothing gets this format.</summary>
    PlainText = 1,

    /// <summary>
    /// The text contains <c>${1:name}</c> tab stops, for a client that declared it supports them.
    /// </summary>
    Snippet = 2,
}
