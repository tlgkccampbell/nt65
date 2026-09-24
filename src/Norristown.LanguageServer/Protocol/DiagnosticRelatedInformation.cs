namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a second location a diagnostic points at, such as the first of two declarations.
/// </summary>
/// <param name="Location">The location.</param>
/// <param name="Message">How the location relates to the diagnostic.</param>
internal sealed record DiagnosticRelatedInformation(Location Location, string Message);
