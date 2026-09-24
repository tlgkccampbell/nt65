using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what moving a file requires the program to change. A module's name comes from its
/// <c>.module</c> line and its output is named after the module, so moving a source changes less
/// than it might seem. Only a <c>files</c> entry that names the file changes, along with the
/// <c>.incbin</c> paths resolved relative to the file that moved, whether that is the including
/// source or the binary.
/// </summary>
public sealed class MovedFileTests : IDisposable
{
    private readonly TempFolder root = new("nt65-moved-");

    public void Dispose() => root.Dispose();

    /// <summary>
    /// A <c>files</c> entry naming the file literally is rewritten, and the <c>.incbin</c> paths
    /// in a file that moved to another folder are rewritten to reach the same binaries from there.
    /// A glob that stops matching is reported in a warning rather than rewritten.
    /// </summary>
    [Fact]
    public async Task WhatMovesWithAFileIsRewrittenAndWhatCannotBeIsReported()
    {
        var timeout = TestTimeout.Token();
        root.Write("nt65.json", """
            {
              // The sources, one of them named outright.
              "cpu": "6502",
              "files": ["src/main.nt65", "gfx/*.nt65"],
              "out": "build"
            }
            """);
        root.Write("src/main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    rts\n}\n");
        root.Write("gfx/sprite.nt65",
            ".module gfx::sprite\n.segment RODATA\n.export .data tiles: .incbin \"../data/tiles.bin\"\n");
        root.Write("data/tiles.bin", "0123");

        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: Folder(""));
        await client.OpenAsync(Folder("src/main.nt65"), root.Read("src/main.nt65"));
        await client.NextDiagnosticsAsync(Folder("src/main.nt65"), timeout);

        // The named source moves: the entry that names it has to name it where it now is.
        var moved = await RenameAsync(client, timeout, ("src/main.nt65", "src/app/main.nt65"));
        Assert.NotNull(moved);
        var project = Assert.Single(moved.Changes[Folder("nt65.json")]);
        Assert.Equal("\"src/app/main.nt65\"", project.NewText);
        Assert.Equal(3, project.Range.Start.Line);

        // The file that contains an `.incbin` moves a folder deeper, so the path in it has to
        // reach the same file from there. The glob that matched the file no longer does, and only
        // the programmer can decide which glob should cover it now, so the server warns and edits
        // nothing.
        var included = await RenameAsync(client, timeout, ("gfx/sprite.nt65", "gfx/tiles/sprite.nt65"));
        Assert.NotNull(included);
        var path = Assert.Single(included.Changes[Folder("gfx/sprite.nt65")]);
        Assert.Equal("\"../../data/tiles.bin\"", path.NewText);
        Assert.Equal(2, path.Range.Start.Line);

        var warning = await client.NextShowMessageAsync(timeout);
        Assert.Equal(MessageType.Warning, warning.Type);
        Assert.Contains("`gfx/*.nt65` in nt65.json does not match sprite.nt65, the file's new path", warning.Message, StringComparison.Ordinal);

        // The binary moves instead: the file that includes it names it where it now is.
        var binary = await RenameAsync(client, timeout, ("data/tiles.bin", "data/art/tiles.bin"));
        Assert.NotNull(binary);
        Assert.Equal(
            "\"../data/art/tiles.bin\"",
            Assert.Single(binary.Changes[Folder("gfx/sprite.nt65")]).NewText);
    }

    /// <summary>
    /// A rewritten path is placed by the same line rule as every other answer, so in a file whose
    /// lines end with a lone <c>\r</c> the edit lands on the line that holds the path.
    /// </summary>
    [Fact]
    public async Task ARewrittenPathIsPlacedInAFileWithCarriageReturnLineEnds()
    {
        var timeout = TestTimeout.Token();
        root.Write("nt65.json", """{ "cpu": "6502", "files": ["gfx/*.nt65"], "out": "build" }""");
        const string Sprite = ".module gfx::sprite\r.segment RODATA\r.export .data tiles: .incbin \"../data/tiles.bin\"\r";
        Directory.CreateDirectory(root.PathOf("gfx"));
        File.WriteAllText(root.PathOf("gfx/sprite.nt65"), Sprite);
        root.Write("data/tiles.bin", "0123");

        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: Folder(""));
        await client.OpenAsync(Folder("gfx/sprite.nt65"), Sprite);
        await client.NextDiagnosticsAsync(Folder("gfx/sprite.nt65"), timeout);

        var binary = await RenameAsync(client, timeout, ("data/tiles.bin", "data/art/tiles.bin"));
        Assert.NotNull(binary);
        var path = Assert.Single(binary.Changes[Folder("gfx/sprite.nt65")]);
        Assert.Equal(2, path.Range.Start.Line);
        Assert.Equal(".export .data tiles: .incbin ".Length, path.Range.Start.Character);
    }

    /// <summary>
    /// A client that does not declare <c>willRename</c> support is not registered for it, and a
    /// client that does is registered for every file.
    /// </summary>
    [Fact]
    public async Task AClientThatDoesNotAskIsNotRegisteredFor()
    {
        var timeout = TestTimeout.Token();
        await using var quiet = await TestClient.StartAsync(new { }, timeout);
        Assert.Null(quiet.Initialized.Capabilities.Workspace);

        await using var client = await TestClient.StartAsync(TestClient.Capable(), timeout);
        var operations = client.Initialized.Capabilities.Workspace!.FileOperations;
        Assert.NotNull(operations);
        var filter = Assert.Single(operations.WillRename!.Filters);
        Assert.Equal("**/*", filter.Pattern.Glob);
        Assert.Equal("file", filter.Pattern.Matches);
    }

    private Task<WorkspaceEdit?> RenameAsync(
        TestClient client, CancellationToken cancellation, params (string From, string To)[] files) =>
        client.RequestAsync<WorkspaceEdit?>("workspace/willRenameFiles",
            new RenameFilesParams([.. files.Select(file => new FileRename(Folder(file.From), Folder(file.To)))]),
            cancellation);

    private string Folder(string path) =>
        new Uri(Path.Combine(root.FullName, path.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;
}
