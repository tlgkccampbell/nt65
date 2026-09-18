using System.Text;
using System.Threading.Channels;
using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// <c>nt65 build --watch</c>: one build, then one more whenever the program changes, until it is
/// interrupted. Each build ends with the line that says it is waiting, which is what a test
/// waits for too.
/// </summary>
public sealed class WatchCommandTests : IDisposable
{
    private const string Good = ".module main\n.export main\n.segment CODE\n.proc main {\n    rts\n}\n";
    private const string Wrong = ".module main\n.export main\n.segment CODE\n.proc main {\n    lda nowhere\n    rts\n}\n";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-watch-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>
    /// A source changing is built again, whether it fixed the program or broke it, and a file
    /// that was not there when the globs were matched is part of the program once it is written.
    /// </summary>
    [Fact]
    public async Task ItBuildsAgainWheneverTheProgramChanges()
    {
        var timeout = TestContext.Current.CancellationToken;
        Write("nt65.json", """{ "cpu": "6502", "files": ["src/*.nt65"], "out": "build" }""");
        Write("src/main.nt65", Good);

        var said = new Lines();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(timeout);
        var watching = Task.Run(
            () => Commands.Run(["build", "--watch"], root.FullName, TextWriter.Null, said, false, stopping.Token),
            CancellationToken.None);

        Assert.Empty(await WaitAsync(said, timeout));
        Assert.True(File.Exists(Path.Combine(root.FullName, "build", "main.s")));

        Write("src/main.nt65", Wrong);
        Assert.Equal(["src/main.nt65:5:9: error: `nowhere` is not declared"], await WaitAsync(said, timeout));

        Write("src/main.nt65", Good);
        Assert.Empty(await WaitAsync(said, timeout));

        // A module written after the watch began is part of the program from the next build on.
        Write("src/gfx.nt65", ".module gfx\n.export clear\n.segment CODE\n.proc clear {\n    rts\n}\n");
        Assert.Empty(await WaitAsync(said, timeout));
        Assert.True(File.Exists(Path.Combine(root.FullName, "build", "gfx.s")));

        await stopping.CancelAsync();
        Assert.Equal(0, await watching);
    }

    /// <summary>
    /// A command line that is wrong is not something a file changing fixes, so a watch that is
    /// asked for one comes straight back rather than waiting for a change that cannot help.
    /// </summary>
    [Fact]
    public async Task AWrongCommandLineComesStraightBack()
    {
        var timeout = TestContext.Current.CancellationToken;
        var said = new Lines();

        var code = await Task.Run(
            () => Commands.Run(["build", "--watch"], root.FullName, TextWriter.Null, said, false, timeout),
            CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Equal("nt65: no input files, and no nt65.json", await said.NextAsync(timeout));
    }

    /// <summary>What the build said, up to the line that says it is waiting for the next change.</summary>
    private static async Task<IReadOnlyList<string>> WaitAsync(Lines said, CancellationToken timeout)
    {
        var lines = new List<string>();
        while (await said.NextAsync(timeout) is var line && !line.StartsWith("nt65: watching", StringComparison.Ordinal))
            lines.Add(line);
        return lines;
    }

    private void Write(string path, string text) => Repo.WriteText(Path.Combine(root.FullName, path), text);

    /// <summary>
    /// A writer whose lines can be waited for one at a time, because a watch writes them as it
    /// goes and a test has to know which build it is looking at.
    /// </summary>
    private sealed class Lines : TextWriter
    {
        private readonly Channel<string> written = Channel.CreateUnbounded<string>();
        private readonly StringBuilder pending = new();

        public Lines() => NewLine = "\n";

        public override Encoding Encoding => Encoding.UTF8;

        public ValueTask<string> NextAsync(CancellationToken cancellation) => written.Reader.ReadAsync(cancellation);

        public override void Write(char value)
        {
            if (value != '\n')
            {
                pending.Append(value);
                return;
            }
            written.Writer.TryWrite(pending.ToString());
            pending.Clear();
        }
    }
}
