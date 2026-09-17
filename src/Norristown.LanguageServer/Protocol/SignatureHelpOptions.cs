namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server helps with calls.</summary>
/// <param name="TriggerCharacters">What, typed, asks for help without the programmer asking.</param>
internal sealed record SignatureHelpOptions(IReadOnlyList<string> TriggerCharacters);
