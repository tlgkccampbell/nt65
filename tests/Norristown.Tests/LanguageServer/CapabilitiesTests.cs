using System.Text.Json;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The capabilities the client declares, read once and used from then on to decide the form of
/// each answer. A client that declares nothing is given the plain form, which every client
/// understands, and one that declares more is given the richer forms it declared.
/// </summary>
public sealed class CapabilitiesTests : IDisposable
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Main = """
        .module main
        .segment CODE
        .proc reset {
            jsr step
            rts
        }
        .proc step {
            rts
        }
        """;

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-folders-");

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void EveryCapabilityIsReadFromWhereTheProtocolPutsIt()
    {
        var declared = ClientCapabilities.Of(JsonDocument.Parse("""
            {
              "workspace": {
                "semanticTokens": { "refreshSupport": true },
                "codeLens": { "refreshSupport": true },
                "inlayHint": { "refreshSupport": true },
                "workspaceEdit": { "documentChanges": true },
                "workspaceFolders": true,
                "fileOperations": { "willRename": true },
                "didChangeWatchedFiles": { "dynamicRegistration": true }
              },
              "textDocument": {
                "completion": { "completionItem": { "snippetSupport": true } },
                "documentSymbol": { "hierarchicalDocumentSymbolSupport": true }
              }
            }
            """).RootElement);

        Assert.Equal(new ClientCapabilities(true, true, true, true, true, true, true, true, true), declared);
        Assert.Equal(ClientCapabilities.None, ClientCapabilities.Of(JsonDocument.Parse("{}").RootElement));
        Assert.Equal(ClientCapabilities.None, ClientCapabilities.Of(null));

        // A capability spelled as something other than true is not declared.
        Assert.Equal(
            ClientCapabilities.None,
            ClientCapabilities.Of(JsonDocument.Parse("""
                {"workspace":{"workspaceFolders":"yes","workspaceEdit":{"documentChanges":null}}}
                """).RootElement));
    }

    /// <summary>
    /// A client that declares nothing gets the outline as the flat list the protocol had first,
    /// and an edit as the plain map of changes.
    /// </summary>
    [Fact]
    public async Task AClientThatDeclaresNothingGetsThePlainAnswers()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(new { }, timeout);
        await client.OpenAsync(Uri, Main);
        await client.NextDiagnosticsAsync(Uri, timeout);

        // A position is a UTF-16 offset in a line, which is the protocol's own default and
        // what nt65 has always counted.
        Assert.Equal("utf-16", client.Initialized.Capabilities.PositionEncoding);
        Assert.Null(client.Initialized.Capabilities.Workspace);

        var outline = await client.FlatSymbolsAsync(Uri, timeout);
        Assert.Equal(["CODE", "reset", "step"], outline.Select(item => item.Name));
        Assert.Equal([null, "CODE", "CODE"], outline.Select(item => item.ContainerName));
        Assert.All(outline, item => Assert.Equal(Uri, item.Location.Uri));

        var edit = await client.RenameAsync(Uri, Locate.At(Main, ".proc s|tep"), "later", timeout);
        Assert.NotNull(edit);
        Assert.Equal([Uri], edit.Changes.Keys);
        Assert.Null(edit.DocumentChanges);
    }

    /// <summary>
    /// A client that declares them gets the outline as a tree, and the edit a second time as
    /// document changes naming the document version it was computed against, so that an edit
    /// computed against a buffer that has since changed is refused rather than applied in the
    /// wrong place.
    /// </summary>
    [Fact]
    public async Task AClientThatTakesThemGetsATreeAndAnEditAgainstARevision()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(TestClient.Capable(), timeout);
        await client.OpenAsync(Uri, Main);
        await client.NextDiagnosticsAsync(Uri, timeout);
        var inserted = Locate.At(Main, "rts");
        await client.ChangeAsync(Uri, 7, new TextDocumentContentChangeEvent(
            new Range(inserted, inserted), "nop\n    "));
        await client.NextDiagnosticsAsync(Uri, timeout);

        var segment = Assert.Single(await client.SymbolsAsync(Uri, timeout));
        Assert.Equal("CODE", segment.Name);
        Assert.Equal(["reset", "step"], segment.Children!.Select(item => item.Name));

        var edited = Main.Insert(Main.IndexOf("rts", StringComparison.Ordinal), "nop\n    ");
        var edit = await client.RenameAsync(Uri, Locate.At(edited, ".proc s|tep"), "later", timeout);
        Assert.NotNull(edit);
        var one = Assert.Single(edit.DocumentChanges!);
        Assert.Equal(Uri, one.TextDocument.Uri);
        Assert.Equal(7, one.TextDocument.Version);
        Assert.Equal(edit.Changes[Uri], one.Edits);

        // The client declared workspace folder support, so the server asks to be told when they change.
        Assert.True(client.Initialized.Capabilities.Workspace!.WorkspaceFolders!.ChangeNotifications);
    }

    /// <summary>
    /// A folder added to the workspace brings whatever projects are in it, and a file that was
    /// a program of its own becomes part of one.
    /// </summary>
    [Fact]
    public async Task AFolderAddedToTheWorkspaceBringsItsProjects()
    {
        var timeout = TestTimeout.Token();
        Write("opened/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        Write("later/nt65.json", """{ "cpu": "6502", "files": ["*.nt65"] }""");
        Write("later/gfx.nt65", ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n");
        var caller = ".module main\n.segment CODE\n.export .proc main {\n    jsr gfx::clear\n    rts\n}\n";
        Write("later/main.nt65", caller);

        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: Folder("opened"));
        await client.OpenAsync(Folder("later/main.nt65"), caller);
        Assert.NotEmpty((await client.NextDiagnosticsAsync(Folder("later/main.nt65"), timeout)).Diagnostics);

        await client.FoldersChangedAsync([Folder("later")], []);
        Assert.Empty((await client.NextDiagnosticsAsync(Folder("later/main.nt65"), timeout)).Diagnostics);
    }

    private void Write(string path, string text)
    {
        var file = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.ReplaceLineEndings("\n"));
    }

    private string Folder(string path) => new Uri(Path.Combine(root.FullName, path)).AbsoluteUri;
}
