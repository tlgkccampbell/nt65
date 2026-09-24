namespace Norristown;

/// <summary>
/// Represents one source that a generated ca65 file was generated from, and which of the file's
/// lines come from that source. For the module the file is named after, that is the whole file.
/// For a module placed in it, it is the lines from the comment that opens the module's part to
/// the comment that closes it.
/// </summary>
/// <param name="Path">The source's logical path.</param>
/// <param name="Size">The size of the source in bytes, which the line map records.</param>
/// <param name="First">The first line of its part, counting from 0.</param>
/// <param name="Count">How many lines its part is.</param>
public sealed record OutputSource(string Path, int Size, int First, int Count);
