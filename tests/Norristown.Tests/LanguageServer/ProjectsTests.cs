using System.Text.RegularExpressions;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the projects of a workspace. Each project is found beneath the folder the client opened,
/// is a program of its own, is built with the configuration the client chooses, and is read again
/// when a file it reads changes on disk.
/// </summary>
public sealed class ProjectsTests : IDisposable
{
    private const string Caller = ".module main\n.segment CODE\n.export .proc main {\n    jsr gfx::clear\n    rts\n}\n";

    private readonly TempFolder root = new("nt65-projects-");

    public void Dispose() => root.Dispose();

    /// <summary>
    /// With two projects in subfolders and a file in neither, each project's files see one another
    /// and nothing of the other project, and the file in neither is a program of its own.
    /// </summary>
    [Fact]
    public async Task EachProjectBeneathTheFolderIsItsOwnProgram()
    {
        var timeout = TestTimeout.Token();
        root.Write("games/snake/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        root.Write("games/snake/gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        root.Write("games/snake/main.nt65", Caller);
        root.Write("tools/nt65.json", """{ "cpu": "6502", "files": ["**/*.nt65"] }""");
        root.Write("tools/src/main.nt65", Caller);
        root.Write("scratch.nt65", Caller);
        await using var client = await TestClient.StartAsync(Uri(""), null, timeout);

        Assert.Empty(await DiagnosticsAsync(client, "games/snake/main.nt65", timeout));
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            await DiagnosticsAsync(client, "tools/src/main.nt65", timeout));
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            await DiagnosticsAsync(client, "scratch.nt65", timeout));
    }

    /// <summary>
    /// VS Code gives a Windows drive in lower case with its colon escaped, as in
    /// <c>file:///c%3A/...</c>, and the project beneath the folder is still found.
    /// </summary>
    [Fact]
    public async Task AFolderWithAnEscapedDriveFindsItsProject()
    {
        var timeout = TestTimeout.Token();
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["src/*.nt65"] }""");
        root.Write("src/gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        root.Write("src/main.nt65", Caller);
        static string AsVsCode(string uri) =>
            Regex.Replace(uri, "^file:///([A-Za-z]):", m => $"file:///{m.Groups[1].Value.ToLowerInvariant()}%3A");
        await using var client = await TestClient.StartAsync(AsVsCode(Uri("")), null, timeout);

        // Once the client has opened a file under its own form of the URI, diagnostics are
        // published under that form.
        await client.OpenAsync(AsVsCode(Uri("src/main.nt65")), root.Read("src/main.nt65"));
        Assert.Empty((await client.NextDiagnosticsAsync(AsVsCode(Uri("src/main.nt65")), timeout)).Diagnostics);
    }

    /// <summary>
    /// A change on disk to any file a program reads makes the program publish again. Such files
    /// include the project file, a source no one has open, and a file an <c>.incbin</c> includes.
    /// A change to a file nothing reads publishes nothing. No file here is opened, because every
    /// file of a project is reported on regardless.
    /// </summary>
    [Fact]
    public async Task WhatAProgramReadsChangingOnDiskIsPublishedAgain()
    {
        var timeout = TestTimeout.Token();
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["main.nt65"] }""");
        root.Write("gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        root.Write("tiles.bin", "1234");
        root.Write("main.nt65", Caller + """
            .segment RODATA
            .data tiles: .incbin "tiles.bin"
            .assert .sizeof(tiles) == 4, "four tiles"
            """);
        await using var client = await TestClient.StartAsync(Uri(""), null, timeout);
        Assert.Equal(["`gfx` is not declared, and no module `gfx` is in this build"],
            (await NextForAsync(client, "main.nt65", timeout)).Diagnostics.Select(d => d.Message));

        // The project names gfx.nt65 as well from here on.
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        await client.ChangedOnDiskAsync(Uri("nt65.json"));
        Assert.Empty((await NextForAsync(client, "main.nt65", timeout)).Diagnostics);

        root.Write("gfx.nt65", ".module gfx\n.segment CODE\n.proc clear {\n    rts\n}\n");
        await client.ChangedOnDiskAsync(Uri("gfx.nt65"));
        Assert.Equal(["`gfx::clear` is not exported by module `gfx`"],
            (await NextForAsync(client, "main.nt65", timeout)).Diagnostics.Select(d => d.Message));

        root.Write("tiles.bin", "123");
        root.Write("unrelated.txt", "");
        await client.ChangedOnDiskAsync(Uri("unrelated.txt"));
        await client.ChangedOnDiskAsync(Uri("tiles.bin"));
        Assert.Equal(["`gfx::clear` is not exported by module `gfx`", "four tiles"],
            (await NextForAsync(client, "main.nt65", timeout)).Diagnostics.Select(d => d.Message));
    }

    /// <summary>
    /// Diagnostics are published for the project file as well. It is not a source of the
    /// program, but a mistake in it can stop the program being read at all, so it is worth a
    /// squiggle where the mistake is written.
    /// </summary>
    [Fact]
    public async Task WhatIsWrongWithTheProjectFileIsPublishedForIt()
    {
        var timeout = TestTimeout.Token();
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["*.nt65"], "define": {} }""");
        root.Write("main.nt65", ".module main\n");
        await using var client = await TestClient.StartAsync(Uri(""), null, timeout);

        var published = await NextForAsync(client, "nt65.json", timeout);

        Assert.Equal("`define` is not a key of nt65.json; did you mean `defines`?",
            Assert.Single(published.Diagnostics).Message);
        Assert.Null(published.Version);
    }

    /// <summary>
    /// A project is built with the configuration the client chooses, and the conditional branches
    /// that configuration leaves out are dimmed; a project with no configuration of that name is
    /// built with its default settings.
    /// </summary>
    [Fact]
    public async Task TheChosenConfigurationDecidesWhatIsDimmed()
    {
        var timeout = TestTimeout.Token();
        root.Write("app/nt65.json", """
            { "cpu": "6502", "files": ["*.nt65"], "defines": { "DEBUG": 0 },
              "configurations": { "debug": { "defines": { "DEBUG": 1 } } } }
            """);
        root.Write("app/main.nt65", ".module main\n.if DEBUG {\n    X = 1\n} .else {\n    X = 2\n}\n.assert X > 0\n");
        root.Write("lib/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        root.Write("lib/main.nt65", ".module lib\n");
        await using var client = await TestClient.StartAsync(Uri(""), "debug", timeout);

        Assert.Equal(["debug"], await client.RequestAsync<IReadOnlyList<string>>("nt65/configurations", new { }, timeout));
        Assert.Equal([3], Dimmed(await NextForAsync(client, "app/main.nt65", timeout)));
        Assert.Empty((await NextForAsync(client, "lib/main.nt65", timeout)).Diagnostics);

        // The project with no `debug` configuration builds its default settings either way, so
        // the app project is the only one whose dimmed lines move.
        await client.ConfigureAsync(null);
        Assert.Equal([1], Dimmed(await NextForAsync(client, "app/main.nt65", timeout)));
    }

    private static IReadOnlyList<int> Dimmed(PublishDiagnosticsParams published) =>
        [.. published.Diagnostics.Where(d => d.Tags?.Contains(DiagnosticTag.Unnecessary) == true).Select(d => d.Range.Start.Line)];

    private async Task<IReadOnlyList<string>> DiagnosticsAsync(TestClient client, string path, CancellationToken timeout)
    {
        await client.OpenAsync(Uri(path), root.Read(path));
        return [.. (await NextForAsync(client, path, timeout)).Diagnostics.Select(d => d.Message)];
    }

    /// <summary>
    /// Returns the next diagnostics published for <paramref name="path"/>. Every file of every
    /// project is published after any change, so the first file published is rarely the one a
    /// test is about.
    /// </summary>
    private Task<PublishDiagnosticsParams> NextForAsync(TestClient client, string path, CancellationToken timeout) =>
        client.NextDiagnosticsAsync(Uri(path), timeout);

    private string Uri(string path) => new Uri(Path.Combine(root.FullName, path)).AbsoluteUri;
}
