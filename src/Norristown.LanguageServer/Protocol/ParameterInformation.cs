namespace Norristown.LanguageServer.Protocol;

/// <summary>One parameter of a signature.</summary>
/// <param name="Label">Where in the signature's label it is written, as start and end offsets.</param>
/// <param name="Documentation">What it takes, or null.</param>
internal sealed record ParameterInformation(IReadOnlyList<int> Label, string? Documentation);
