namespace Norristown.LanguageServer.Protocol;

/// <summary>One routine in a call hierarchy.</summary>
/// <param name="Name">The name as the source writes it.</param>
/// <param name="Kind">What it is, which picks its icon.</param>
/// <param name="Uri">The file it is declared in.</param>
/// <param name="Range">Everything the declaration covers, its body included.</param>
/// <param name="SelectionRange">Just the name, which is what the client reveals and selects.</param>
/// <param name="Detail">The module and the scopes around it, or null.</param>
internal sealed record CallHierarchyItem(
    string Name, SymbolKind Kind, string Uri, Range Range, Range SelectionRange, string? Detail = null);
