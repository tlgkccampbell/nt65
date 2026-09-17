namespace Norristown.LanguageServer.Protocol;

/// <summary>What a call at the caret takes.</summary>
/// <param name="Signatures">The one signature of what is called.</param>
/// <param name="ActiveSignature">Always 0.</param>
/// <param name="ActiveParameter">The parameter the caret is in the argument for.</param>
internal sealed record SignatureHelp(IReadOnlyList<SignatureInformation> Signatures, int ActiveSignature, int ActiveParameter);
