namespace Norristown;

/// <summary>
/// One source a generated ca65 file was written from, and which of its lines are that
/// source's: the whole file for the module it is named after, and for a module placed in it,
/// the lines from the comment that opens its part to the one that closes it.
/// </summary>
/// <param name="Path">The source's logical path.</param>
/// <param name="Size">How many bytes the source is, which the line map records.</param>
/// <param name="First">The first line of its part, counting from 0.</param>
/// <param name="Count">How many lines its part is.</param>
public sealed record OutputSource(string Path, int Size, int First, int Count);
