namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies the severity of a message sent to the client, as LSP numbers it.</summary>
internal enum MessageType
{
    /// <summary>An error.</summary>
    Error = 1,

    /// <summary>A warning.</summary>
    Warning = 2,

    /// <summary>Information.</summary>
    Info = 3,

    /// <summary>A line for the client's log.</summary>
    Log = 4,
}
