namespace Norristown.Cli;

/// <summary>
/// What one build came to: the exit code, and what it read, which is what <c>--watch</c> waits
/// on. The files are absolute, because a watcher is told about paths and not about projects.
/// </summary>
/// <param name="Code">0 when it built, 1 when the program is wrong, 2 when the command is.</param>
/// <param name="Root">The project root, or where nt65 ran when there is no project.</param>
/// <param name="Watched">
/// The project file, every source the program is, and every binary an <c>.incbin</c> measured.
/// A build that found nothing to build names what it got that far with, which is enough for a
/// watch to notice the file that was missing being written.
/// </param>
internal sealed record BuildResult(int Code, string Root, IReadOnlyList<string> Watched);
