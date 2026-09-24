namespace Norristown.Tests.Oracle;

/// <summary>Represents what ca65 did with one source file.</summary>
/// <param name="Succeeded">Whether ca65 exited cleanly and printed nothing.</param>
/// <param name="Messages">Everything ca65 printed.</param>
/// <param name="LineBytes">
/// The number of bytes ca65 generated for each source line. <c>LineBytes[0]</c> is line 1.
/// </param>
internal sealed record AssemblyResult(bool Succeeded, string Messages, IReadOnlyList<int> LineBytes);
