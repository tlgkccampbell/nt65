using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Represents a program analyzed through a <see cref="Workspace"/> of its own, as the requests
/// that bypass the protocol see it. A test that calls the server's code directly, such as the
/// code actions or the hovers, opens its files here and reads the answers off the analysis.
/// </summary>
/// <param name="Analysis">The analysis of the program.</param>
/// <param name="Path">The path of the last file opened, which is the file the test is about.</param>
internal sealed record AnalyzedDocument(ProgramAnalysis Analysis, string Path)
{
    /// <summary>Gets the semantic model of the file at <see cref="Path"/>.</summary>
    public SemanticModel Model => Analysis.ModelFor(Path)!;

    /// <summary>
    /// Opens each of <paramref name="files"/> in a new workspace, in order, and analyzes the
    /// program for the last of them.
    /// </summary>
    public static AnalyzedDocument Of(params (string Uri, string Text)[] files)
    {
        var workspace = new Workspace();
        Document? document = null;
        foreach (var (uri, text) in files)
            document = workspace.Open(new TextDocumentItem(uri, "nt65", 1, text));
        var path = document!.Tree.Path;
        var analysis = workspace.AnalysisForAsync(path, TestTimeout.Token()).GetAwaiter().GetResult();
        return new AnalyzedDocument(analysis, path);
    }
}
