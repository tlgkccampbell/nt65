namespace Norristown;

/// <summary>
/// One input file. <see cref="Path"/> is the logical path used in diagnostics and output,
/// with <c>/</c> separators whatever the platform.
/// </summary>
public sealed record SourceFile(string Path, string Text);
