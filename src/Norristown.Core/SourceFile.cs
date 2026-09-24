namespace Norristown;

/// <summary>
/// Represents one input file. <see cref="Path"/> is the logical path used in diagnostics and
/// output, with <c>/</c> separators on every platform.
/// </summary>
public sealed record SourceFile(string Path, string Text);
