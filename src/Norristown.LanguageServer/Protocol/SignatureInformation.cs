namespace Norristown.LanguageServer.Protocol;

/// <summary>One signature: how it is written, and each parameter in it.</summary>
/// <param name="Label">The whole signature, as it would be written.</param>
/// <param name="Documentation">What it is, or null.</param>
/// <param name="Parameters">Each parameter, in order.</param>
internal sealed record SignatureInformation(string Label, MarkupContent? Documentation, IReadOnlyList<ParameterInformation> Parameters);
