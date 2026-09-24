namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>window/showMessage</c> notification, which asks the client to show a
/// message to the user rather than log it.
/// </summary>
/// <param name="Type">The severity of the message.</param>
/// <param name="Message">The text to show.</param>
internal sealed record ShowMessageParams(MessageType Type, string Message);
