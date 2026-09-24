namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>window/logMessage</c> notification.</summary>
/// <param name="Type">The severity of the message.</param>
/// <param name="Message">The text to log.</param>
internal sealed record LogMessageParams(MessageType Type, string Message);
