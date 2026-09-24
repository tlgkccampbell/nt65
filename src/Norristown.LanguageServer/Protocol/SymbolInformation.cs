namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a declaration found across the workspace.</summary>
/// <param name="Name">The name as it appears in the source.</param>
/// <param name="Kind">The kind of declaration.</param>
/// <param name="Location">The location of the declared name.</param>
/// <param name="ContainerName">The module and the scopes that contain the declaration, or null.</param>
internal sealed record SymbolInformation(string Name, SymbolKind Kind, Location Location, string? ContainerName);
