using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// Implements <c>nt65 build --watch</c>, which builds, then builds again whenever the program
/// changes, until it is interrupted.
/// <para>
/// It rebuilds when a file the last build read changes. Those files are the project file, the
/// sources, and the binaries an <c>.incbin</c> read, which are the files the dependency file
/// lists. It also rebuilds when any <c>.nt65</c> file it watches changes, because a source
/// created after the globs were matched is not in that list. nt65 writes no file of either kind,
/// so a build does not trigger the next one.
/// </para>
/// <para>
/// It watches the project root and everything under it, the directories that the project's globs
/// search, and the directory of each file the last build read. A glob may reach above the root,
/// and so may an <c>.incbin</c>, so the root alone is not enough.
/// </para>
/// </summary>
internal static class WatchCommand
{
    /// <summary>
    /// The time, in milliseconds, that must pass with no file events before a build starts.
    /// </summary>
    private const int QuietMilliseconds = 120;

    /// <summary>
    /// Builds until <paramref name="cancellation"/> is cancelled, and returns
    /// <see cref="ExitCode.Success"/>. A <see cref="ExitCode.UsageError"/> is returned at once,
    /// since no change to a file can fix it.
    /// </summary>
    public static ExitCode Run(
        CommandLine command, string directory, TextWriter output, TextWriter error, bool colour,
        CancellationToken cancellation)
    {
        var root = Path.GetFullPath(ProjectRoot.Chosen(command.Project, directory) is { } file
            ? Path.GetDirectoryName(file)!
            : directory);

        // A project named in a folder that does not exist cannot be watched, and the build
        // reports why before returning the usage error.
        if (!Directory.Exists(root))
            return BuildCommand.Run(command, directory, output, error, colour).Code;

        // The root is watched before the first build, so a file saved while that build is running
        // still triggers a rebuild rather than being missed.
        var watched = new HashSet<string>(FilePaths.Comparer);
        using var changed = new SemaphoreSlim(0, 1);
        var last = Environment.TickCount64;
        void Signal()
        {
            Interlocked.Exchange(ref last, Environment.TickCount64);
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // A change is already pending, and one build covers any number of changes.
            }
        }
        void Touched(object? sender, FileSystemEventArgs change)
        {
            if (Matters(change.FullPath, watched)
                || (change is RenamedEventArgs renamed && Matters(renamed.OldFullPath, watched)))
            {
                Signal();
            }
        }
        using var watchers = new Watchers(Touched, Signal);
        watchers.Update([(root, true)]);

        while (true)
        {
            var built = BuildCommand.Run(command, directory, output, error, colour);
            if (built.Code == ExitCode.UsageError)
                return ExitCode.UsageError;
            lock (watched)
            {
                watched.Clear();
                watched.UnionWith(built.Watched);
            }
            watchers.Update(Directories(root, built));
            error.WriteLine($"nt65: watching {ProjectRoot.Shown(directory, root)}");

            try
            {
                changed.Wait(cancellation);
            }
            catch (OperationCanceledException)
            {
                return ExitCode.Success;
            }

            // An editor may write a file in several steps, and saving many files at once raises
            // many events. The build therefore waits until no event has arrived for a while, so
            // that one build covers them all, then clears the signal they left.
            while (Environment.TickCount64 - Interlocked.Read(ref last) is var quiet && quiet < QuietMilliseconds)
            {
                if (cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(QuietMilliseconds - quiet)))
                    return ExitCode.Success;
            }
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

    /// <summary>
    /// Returns the directories to watch after <paramref name="built"/>, each with whether its
    /// subdirectories are watched too. They are the root, the directories the globs search, and
    /// the directory of each file the build read.
    /// </summary>
    private static List<(string Directory, bool Recursive)> Directories(string root, BuildResult built) =>
    [
        (root, true),
        .. built.Searched,
        .. built.Watched.Select(path => (Path.GetDirectoryName(path)!, false)),
    ];

    /// <summary>
    /// Represents the file watchers of one watch, one for each directory it watches. A directory
    /// under another that is watched with its subdirectories needs no watcher of its own.
    /// </summary>
    /// <param name="touched">The handler for a file that is created, changed, deleted or renamed.</param>
    /// <param name="lost">
    /// The handler for events that were lost, because the watcher's buffer overflowed or the
    /// watcher failed. Any file may have changed, so the handler should build again.
    /// </param>
    private sealed class Watchers(FileSystemEventHandler touched, Action lost) : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> active = new(FilePaths.Comparer);
        private readonly HashSet<FileSystemWatcher> failed = [];

        /// <summary>
        /// Watches exactly the directories in <paramref name="wanted"/> that exist, keeping the
        /// watchers it already has for them. A watcher that failed is replaced.
        /// </summary>
        public void Update(IEnumerable<(string Directory, bool Recursive)> wanted)
        {
            var chosen = new Dictionary<string, bool>(FilePaths.Comparer);
            foreach (var (directory, recursive) in wanted)
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                chosen[full] = recursive || (chosen.TryGetValue(full, out var already) && already);
            }
            var needed = chosen
                .Where(entry => Directory.Exists(entry.Key)
                    && !chosen.Any(other => other.Value && Within(entry.Key, other.Key)))
                .ToDictionary(entry => entry.Key, entry => entry.Value, FilePaths.Comparer);

            lock (failed)
            {
                foreach (var (directory, watcher) in active.ToList())
                {
                    if (!needed.TryGetValue(directory, out var recursive)
                        || recursive != watcher.IncludeSubdirectories || failed.Contains(watcher))
                    {
                        watcher.Dispose();
                        active.Remove(directory);
                    }
                }
                failed.Clear();
            }
            foreach (var (directory, recursive) in needed)
            {
                if (!active.ContainsKey(directory))
                    active[directory] = Start(directory, recursive);
            }
        }

        /// <summary>Stops every watcher.</summary>
        public void Dispose()
        {
            foreach (var watcher in active.Values)
                watcher.Dispose();
            active.Clear();
        }

        /// <summary>
        /// Returns a value indicating whether <paramref name="directory"/> is strictly below
        /// <paramref name="ancestor"/>.
        /// </summary>
        private static bool Within(string directory, string ancestor) =>
            directory.Length > ancestor.Length
            && directory.StartsWith(ancestor, FilePaths.Comparison)
            && (Path.EndsInDirectorySeparator(ancestor) || directory[ancestor.Length] == Path.DirectorySeparatorChar);

        /// <summary>Returns a watcher of <paramref name="directory"/> that is already raising events.</summary>
        private FileSystemWatcher Start(string directory, bool recursive)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,

                // A switch of git branch can change thousands of files at once. The largest buffer
                // makes an overflow less likely, and an overflow still triggers a build.
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += touched;
            watcher.Created += touched;
            watcher.Deleted += touched;
            watcher.Renamed += (sender, renamed) => touched(sender, renamed);
            watcher.Error += (sender, problem) =>
            {
                // An overflow loses events but leaves the watcher running. Any other error stops
                // it, so the next update starts another in its place.
                if (problem.GetException() is not InternalBufferOverflowException)
                {
                    lock (failed)
                        failed.Add(watcher);
                }
                lost();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
    }
}
