namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the build whose debug file gave the map its built addresses.
/// </summary>
/// <param name="Path">The full path of the ld65 debug file.</param>
/// <param name="At">The time at which the debug file was written, in UTC, in ISO 8601 form.</param>
/// <param name="Stale">Whether a source of the program was saved after the debug file was written.</param>
internal sealed record DirectPageBuild(string Path, string At, bool Stale);
