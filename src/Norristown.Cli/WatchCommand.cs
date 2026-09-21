using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 build --watch</c>: builds, then builds again whenever the program changes, until it
/// is interrupted.
/// <para>
/// What it waits for is what the last build read — the project file, the sources, and the
/// binaries an <c>.incbin</c> measured, which is the set the dependency file names — and, on
/// top of that, any <c>.nt65</c> under the project root, because a file that did not exist when
/// the globs were matched is not in a set that was worked out before it was written. Nothing
/// nt65 writes is either, so a build does not set off the next one.
/// </para>
/// </summary>
internal static class WatchCommand
{
    /// <summary>How long to let a burst of file events finish before building, in milliseconds.</summary>
    private const int Settle = 120;

    /// <summary>
    /// Builds until <paramref name="cancellation"/> stops it, and returns 0; a command line that
    /// is wrong is not something a file changing fixes, so that comes straight back.
    /// </summary>
    public static int Run(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour,
        CancellationToken cancellation)
    {
        var root = ProjectRoot.Chosen(command.Project, directory) is { } file
            ? Path.GetDirectoryName(file)!
            : directory;

        // The watcher is started before the first build, so a file saved while that build is
        // running is a change it hears about rather than one it slept through.
        var watched = new HashSet<string>(FilePaths.Comparer);
        using var changed = new SemaphoreSlim(0, 1);
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void Touched(object? sender, FileSystemEventArgs said)
        {
            if (!Matters(said.FullPath, watched))
                return;
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // A change is already waiting, and one build answers however many there were.
            }
        }
        watcher.Changed += Touched;
        watcher.Created += Touched;
        watcher.Deleted += Touched;
        watcher.Renamed += Touched;
        watcher.EnableRaisingEvents = true;

        while (true)
        {
            var built = BuildCommand.Run(command, directory, output, error, colour);
            if (built.Code == 2)
                return 2;
            lock (watched)
            {
                watched.Clear();
                watched.UnionWith(built.Watched);
            }
            error.WriteLine($"nt65: watching {ProjectRoot.Shown(directory, root)}");

            try
            {
                changed.Wait(cancellation);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            // An editor writes a file in more than one step, and a save across a folder is
            // several files; one build answers all of it.
            if (cancellation.WaitHandle.WaitOne(Settle))
                return 0;
            changed.Wait(0, CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether a path that changed is one to build for: what the last build read, any nt65
    /// source, or the project file. Nothing nt65 writes is any of those.
    /// </summary>
    private static bool Matters(string path, HashSet<string> watched)
    {
        if (path.EndsWith(".nt65", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals(ProjectFile.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        lock (watched)
            return watched.Contains(path);
    }
}
