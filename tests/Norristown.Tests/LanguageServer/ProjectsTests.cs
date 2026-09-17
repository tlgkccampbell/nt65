using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The projects of a workspace: found beneath the folder the client opened, each its own
/// program, built as the configuration the client chooses, and read again when a file they read
/// changes on disk.
/// </summary>
public sealed class ProjectsTests : IDisposable
{
    private const string Caller = ".module main\n.segment CODE\n.proc main {\n    jsr gfx::clear\n    rts\n}\n";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-projects-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>
    /// Two projects in subfolders, and a file in neither: each project's files see one another
    /// and nothing of the other project, and the file in neither is a program of its own.
    /// </summary>
    [Fact]
    public async Task EachProjectBeneathTheFolderIsItsOwnProgram()
    {
        var timeout = TestContext.Current.CancellationToken;
        Write("games/snake/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        Write("games/snake/gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        Write("games/snake/main.nt65", Caller);
        Write("tools/nt65.json", """{ "cpu": "6502", "files": ["**/*.nt65"] }""");
        Write("tools/src/main.nt65", Caller);
        Write("scratch.nt65", Caller);
        await using var client = await TestClient.StartAsync(Uri(""), null, timeout);

        Assert.Empty(await DiagnosticsAsync(client, "games/snake/main.nt65", timeout));
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            await DiagnosticsAsync(client, "tools/src/main.nt65", timeout));
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            await DiagnosticsAsync(client, "scratch.nt65", timeout));
    }

    /// <summary>
    /// A change on disk to what a program reads is published again: the project file, a source
    /// no one has open, and a file an <c>.incbin</c> measured. A file nothing reads is not news.
    /// </summary>
    [Fact]
    public async Task WhatAProgramReadsChangingOnDiskIsPublishedAgain()
    {
        var timeout = TestContext.Current.CancellationToken;
        Write("nt65.json", """{ "cpu": "6502", "files": ["main.nt65"] }""");
        Write("gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        Write("tiles.bin", "1234");
        Write("main.nt65", Caller + """
            .segment RODATA
            .data tiles: .incbin "tiles.bin"
            .assert .sizeof(tiles) == 4, "four tiles"
            """);
        await using var client = await TestClient.StartAsync(Uri(""), null, timeout);
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            await DiagnosticsAsync(client, "main.nt65", timeout));

        // The project names gfx.nt65 as well from here on.
        Write("nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        await client.ChangedOnDiskAsync(Uri("nt65.json"));
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        Write("gfx.nt65", ".module gfx\n.segment CODE\n.proc clear {\n    rts\n}\n");
        await client.ChangedOnDiskAsync(Uri("gfx.nt65"));
        Assert.Equal(["`gfx::clear` is not exported by module `gfx`"],
            (await client.NextDiagnosticsAsync(timeout)).Diagnostics.Select(d => d.Message));

        Write("tiles.bin", "123");
        Write("unrelated.txt", "");
        await client.ChangedOnDiskAsync(Uri("unrelated.txt"));
        await client.ChangedOnDiskAsync(Uri("tiles.bin"));
        Assert.Equal(["`gfx::clear` is not exported by module `gfx`", "four tiles"],
            (await client.NextDiagnosticsAsync(timeout)).Diagnostics.Select(d => d.Message));
    }

    /// <summary>
    /// The configuration the client chooses is what a project is built as, and the branches it
    /// leaves out are the ones dimmed; a project without that configuration builds its own settings.
    /// </summary>
    [Fact]
    public async Task TheChosenConfigurationDecidesWhatIsDimmed()
    {
        var timeout = TestContext.Current.CancellationToken;
        Write("app/nt65.json", """
            { "cpu": "6502", "files": ["*.nt65"], "defines": { "DEBUG": 0 },
              "configurations": { "debug": { "defines": { "DEBUG": 1 } } } }
            """);
        Write("app/main.nt65", ".module main\n.if DEBUG {\n    X = 1\n} .else {\n    X = 2\n}\n.assert X > 0\n");
        Write("lib/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        Write("lib/main.nt65", ".module lib\n");
        await using var client = await TestClient.StartAsync(Uri(""), "debug", timeout);

        Assert.Equal(["debug"], await client.RequestAsync<IReadOnlyList<string>>("nt65/configurations", new { }, timeout));
        await client.OpenAsync(Uri("app/main.nt65"), Read("app/main.nt65"));
        Assert.Equal([3], Dimmed(await client.NextDiagnosticsAsync(timeout)));
        await client.OpenAsync(Uri("lib/main.nt65"), Read("lib/main.nt65"));
        Assert.Empty((await NextForAsync(client, "lib/main.nt65", timeout)).Diagnostics);

        // Every open document is published again, the app first because it was opened first.
        await client.ConfigureAsync(null);
        Assert.Equal([1], Dimmed(await client.NextDiagnosticsAsync(timeout)));
        Assert.Empty((await NextForAsync(client, "lib/main.nt65", timeout)).Diagnostics);
    }

    private static IReadOnlyList<int> Dimmed(PublishDiagnosticsParams published) =>
        [.. published.Diagnostics.Where(d => d.Tags?.Contains(DiagnosticTag.Unnecessary) == true).Select(d => d.Range.Start.Line)];

    private async Task<IReadOnlyList<string>> DiagnosticsAsync(TestClient client, string path, CancellationToken timeout)
    {
        await client.OpenAsync(Uri(path), Read(path));
        return [.. (await NextForAsync(client, path, timeout)).Diagnostics.Select(d => d.Message)];
    }

    /// <summary>
    /// What is published next for <paramref name="path"/>. Every open document is published again
    /// after any change, so what comes first may be another document's.
    /// </summary>
    private async Task<PublishDiagnosticsParams> NextForAsync(TestClient client, string path, CancellationToken timeout)
    {
        while (true)
        {
            var published = await client.NextDiagnosticsAsync(timeout);
            if (published.Uri == Uri(path))
                return published;
        }
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(root.FullName, path));

    private string Uri(string path) => new Uri(Path.Combine(root.FullName, path)).AbsoluteUri;

    private void Write(string path, string text)
    {
        var full = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }
}
