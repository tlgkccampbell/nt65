namespace Norristown.LanguageServer.Protocol;

/// <summary>A message for the client to put in front of the person, rather than in a log.</summary>
/// <param name="Type">How bad the news is.</param>
/// <param name="Message">What to show.</param>
internal sealed record ShowMessageParams(MessageType Type, string Message);
