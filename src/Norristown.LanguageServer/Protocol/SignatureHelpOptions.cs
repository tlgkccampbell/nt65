namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides signature help.</summary>
/// <param name="TriggerCharacters">
/// The characters that, when typed, request signature help without the programmer asking.
/// </param>
internal sealed record SignatureHelpOptions(IReadOnlyList<string> TriggerCharacters);
