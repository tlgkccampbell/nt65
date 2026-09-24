using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// Implements <c>nt65 build --watch</c>, which builds, then builds again whenever the program
/// changes, until it is interrupted.
/// <para>
/// It rebuilds when a file the last build read changes. Those files are the project file, the
/// sources, and the binaries an <c>.incbin</c> read, which are the files the dependency file
/// lists. It also rebuilds when any <c>.nt65</c> file under the project root changes, because a
/// source created after the globs were matched is not in that list. nt65 writes no file of
/// either kind, so a build does not trigger the next one.
/// </para>
/// </summary>
internal static class WatchCommand
{
    /// <summary>
    /// The time, in milliseconds, to let a burst of file events finish before building.
    /// </summary>
    private const int QuietMilliseconds = 120;

    /// <summary>
    /// Builds until <paramref name="cancellation"/> is cancelled, and returns 0. A command-line
    /// error (exit code 2) is returned at once, since no change to a file can fix it.
    /// </summary>
    public static int Run(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour,
        CancellationToken cancellation)
    {
        var root = ProjectRoot.Chosen(command.Project, directory) is { } file
            ? Path.GetDirectoryName(file)!
            : directory;

        // The watcher is started before the first build, so a file saved while that build is
        // running still triggers a rebuild rather than being missed.
        var watched = new HashSet<string>(FilePaths.Comparer);
        using var changed = new SemaphoreSlim(0, 1);
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void Touched(object? sender, FileSystemEventArgs change)
        {
            if (!Matters(change.FullPath, watched))
                return;
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // A change is already pending, and one build covers any number of changes.
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

            // An editor may write a file in several steps, and saving many files at once raises
            // many events, so wait for the events to stop, then clear the signal and build once.
            if (cancellation.WaitHandle.WaitOne(QuietMilliseconds))
                return 0;
            changed.Wait(0, CancellationToken.None);
        }
    }

    /// <summary>
    /// Returns a value indicating whether a change to <paramref name="path"/> should trigger a
    /// build. It should when the path is a file the last build read, any nt65 source, or a project
    /// file. nt65 writes none of those.
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
