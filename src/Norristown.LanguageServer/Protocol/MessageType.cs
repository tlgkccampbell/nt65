namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies the severity of a message sent to the client, as LSP numbers it.</summary>
internal enum MessageType { Error = 1, Warning = 2, Info = 3, Log = 4 }
