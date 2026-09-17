namespace Norristown.LanguageServer.Protocol;

/// <summary>One declaration found across the workspace.</summary>
/// <param name="Name">The name as the source writes it.</param>
/// <param name="Kind">What it declares.</param>
/// <param name="Location">Where its name is written.</param>
/// <param name="ContainerName">The module and the scopes it is in, or null.</param>
internal sealed record SymbolInformation(string Name, SymbolKind Kind, Location Location, string? ContainerName);
