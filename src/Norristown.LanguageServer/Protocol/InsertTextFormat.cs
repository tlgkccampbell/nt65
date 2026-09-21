namespace Norristown.LanguageServer.Protocol;

/// <summary>How the text a completion writes is read.</summary>
internal enum InsertTextFormat
{
    /// <summary>As it is written, which is what a client that declares nothing gets.</summary>
    PlainText = 1,

    /// <summary>With <c>${1:name}</c> stops in it, for a client that declared it takes them.</summary>
    Snippet = 2,
}
