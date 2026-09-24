namespace Norristown.Semantics;

/// <summary>
/// Represents one name that a file looked for in the rest of the program while it was resolved,
/// whether or not the name was found. A file that starts declaring such a name, stops declaring
/// it or changes what it means affects the file that looked for it, which is then read again.
/// </summary>
/// <param name="Module">The module the name was looked for in, or null when no module was named.</param>
/// <param name="Name">The name, as it appears in the file.</param>
internal readonly record struct LookedUpName(string? Module, string Name);
