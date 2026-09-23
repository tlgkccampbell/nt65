using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What moving a file requires the program to change. A module's name comes from its
/// <c>.module</c> line and its output is named after that, so moving a source changes less than
/// it might seem: only a <c>files</c> entry that names it, and the <c>.incbin</c> paths that are
/// resolved relative to whichever file moved, the including source or the binary.
/// </summary>
public sealed class MovedFileTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-moved-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>
    /// A <c>files</c> entry naming the file literally is rewritten; the <c>.incbin</c> paths in
    /// a file that went to another folder are rewritten to reach the same binaries from it; and
    /// a glob that stops matching is reported in a warning rather than rewritten.
    /// </summary>
    [Fact]
    public async Task WhatMovesWithAFileIsWrittenAndWhatCannotBeIsSaid()
    {
        var timeout = TestContext.Current.CancellationToken;
        Write("nt65.json", """
            {
              // The sources, one of them named outright.
              "cpu": "6502",
              "files": ["src/main.nt65", "gfx/*.nt65"],
              "out": "build"
            }
            """);
        Write("src/main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    rts\n}\n");
        Write("gfx/sprite.nt65",
            ".module gfx::sprite\n.segment RODATA\n.export .data tiles: .incbin \"../data/tiles.bin\"\n");
        Write("data/tiles.bin", "0123");

        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: Folder(""));
        await client.OpenAsync(Folder("src/main.nt65"), Read("src/main.nt65"));
        await client.NextDiagnosticsAsync(Folder("src/main.nt65"), timeout);

        // The named source moves: the entry that names it has to name it where it now is.
        var moved = await RenameAsync(client, timeout, ("src/main.nt65", "src/app/main.nt65"));
        Assert.NotNull(moved);
        var project = Assert.Single(moved.Changes[Folder("nt65.json")]);
        Assert.Equal("\"src/app/main.nt65\"", project.NewText);
        Assert.Equal(3, project.Range.Start.Line);

        // The file that writes an `.incbin` goes a folder deeper, so the path it writes has to
        // reach the same file from there. The glob that named it no longer matches, and only the
        // programmer can say which glob should cover it now, so the server warns and edits nothing.
        var included = await RenameAsync(client, timeout, ("gfx/sprite.nt65", "gfx/tiles/sprite.nt65"));
        Assert.NotNull(included);
        var path = Assert.Single(included.Changes[Folder("gfx/sprite.nt65")]);
        Assert.Equal("\"../../data/tiles.bin\"", path.NewText);
        Assert.Equal(2, path.Range.Start.Line);

        var said = await client.NextShowMessageAsync(timeout);
        Assert.Equal(MessageType.Warning, said.Type);
        Assert.Contains("`gfx/*.nt65` in nt65.json does not match sprite.nt65, the file's new path", said.Message, StringComparison.Ordinal);

        // The binary moves instead: the file that includes it names it where it now is.
        var binary = await RenameAsync(client, timeout, ("data/tiles.bin", "data/art/tiles.bin"));
        Assert.NotNull(binary);
        Assert.Equal(
            "\"../data/art/tiles.bin\"",
            Assert.Single(binary.Changes[Folder("gfx/sprite.nt65")]).NewText);
    }

    /// <summary>A client that does not declare <c>willRename</c> support is not registered for it; one that does is registered for every file.</summary>
    [Fact]
    public async Task AClientThatDoesNotAskIsNotRegisteredFor()
    {
        var timeout = TestContext.Current.CancellationToken;
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

    private void Write(string path, string text)
    {
        var file = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.ReplaceLineEndings("\n"));
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(root.FullName, path));

    private string Folder(string path) =>
        new Uri(Path.Combine(root.FullName, path.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;
}
