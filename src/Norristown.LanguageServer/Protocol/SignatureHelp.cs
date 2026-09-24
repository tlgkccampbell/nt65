namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the signature of the call at the caret.</summary>
/// <param name="Signatures">A list holding the one signature of the called item.</param>
/// <param name="ActiveSignature">The index of the active signature, which is always 0.</param>
/// <param name="ActiveParameter">The index of the parameter whose argument the caret is in.</param>
internal sealed record SignatureHelp(IReadOnlyList<SignatureInformation> Signatures, int ActiveSignature, int ActiveParameter);
