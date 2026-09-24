namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one parameter of a signature.</summary>
/// <param name="Label">The parameter's start and end offsets within the signature's label.</param>
/// <param name="Documentation">A description of what the parameter takes, or null.</param>
internal sealed record ParameterInformation(IReadOnlyList<int> Label, string? Documentation);
