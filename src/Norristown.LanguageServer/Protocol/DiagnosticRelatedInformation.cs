namespace Norristown.LanguageServer.Protocol;

/// <summary>A second place a diagnostic points at, such as the first of two declarations.</summary>
/// <param name="Location">Where it is.</param>
/// <param name="Message">What it has to do with the diagnostic.</param>
internal sealed record DiagnosticRelatedInformation(Location Location, string Message);
