namespace Norristown.Semantics;

/// <summary>
/// One name resolving a file looked for in the rest of the program, found or not. A file that
/// declares one of these, stops declaring it, or changes what it means is news to the file
/// that looked for it, which is then read again.
/// </summary>
/// <param name="Module">The module it was looked for in, or null when no module was named.</param>
/// <param name="Name">The name, as the file writes it.</param>
internal readonly record struct LookedUpName(string? Module, string Name);
