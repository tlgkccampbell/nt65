namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a signature, as written, and each parameter in it.</summary>
/// <param name="Label">The whole signature, as it would be written in the source.</param>
/// <param name="Documentation">A description of the signature, or null.</param>
/// <param name="Parameters">Each parameter, in order.</param>
internal sealed record SignatureInformation(string Label, MarkupContent? Documentation, IReadOnlyList<ParameterInformation> Parameters);
