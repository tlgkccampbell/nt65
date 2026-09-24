namespace Norristown.Cli;

/// <summary>
/// Represents the outcome of one build, which is its exit code and the files it read. The files
/// are what <c>--watch</c> watches for changes. The paths are absolute, because a file watcher
/// reports absolute paths rather than paths relative to a project.
/// </summary>
/// <param name="Code">
/// The exit code, which is 0 when the build succeeded, 1 when the program has errors, and 2 when
/// the command line is wrong.
/// </param>
/// <param name="Root">The project root, or where nt65 ran when there is no project.</param>
/// <param name="Watched">
/// The project file, every source file in the program, and every binary file an <c>.incbin</c>
/// read to find its size. A build that stopped early lists the files it had read by then, which is
/// enough for a watch to notice when the missing file is written.
/// </param>
internal sealed record BuildResult(int Code, string Root, IReadOnlyList<string> Watched);
