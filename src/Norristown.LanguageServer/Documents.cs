using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The documents the client has open. An open document's text lives here rather than on
/// disk, and an edit re-parses it incrementally, so the lines a change did not touch keep
/// the green nodes they already had.
/// </summary>
internal sealed class Documents
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> open = new(StringComparer.Ordinal);

    /// <summary>
    /// The logical path a URI names, with <c>/</c> separators, which is what diagnostics and
    /// output carry. A URI that is not a file keeps its own spelling.
    /// </summary>
    public static string PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath.Replace('\\', '/')
            : uri;

    /// <summary>Takes a newly opened document and parses it.</summary>
    public Document Open(TextDocumentItem item)
    {
        var document = new Document(item.Uri, item.Version, SyntaxTree.Parse(PathOf(item.Uri), item.Text));
        lock (gate)
        {
            open[item.Uri] = document;
        }
        return document;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> in order, or null when the client changed a
    /// document it never opened, which is the client's mistake and not worth a crash.
    /// </summary>
    public Document? Change(VersionedTextDocumentIdentifier id, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        lock (gate)
        {
            if (!open.TryGetValue(id.Uri, out var document))
                return null;
            var tree = document.Tree;
            foreach (var change in changes)
                tree = Apply(tree, change);
            return open[id.Uri] = document with { Version = id.Version, Tree = tree };
        }
    }

    /// <summary>Forgets a document. From here on the file on disk is what it is.</summary>
    public void Close(string uri)
    {
        lock (gate)
        {
            open.Remove(uri);
        }
    }

    /// <summary>The syntax of an open document, or null when it is not open.</summary>
    public SyntaxTree? Tree(string uri)
    {
        lock (gate)
        {
            return open.TryGetValue(uri, out var document) ? document.Tree : null;
        }
    }

    private static SyntaxTree Apply(SyntaxTree tree, TextDocumentContentChangeEvent change)
    {
        // No range means the whole document, which a client sends when it cannot describe the
        // edit; there is nothing to reuse then.
        if (change.Range is not { } range)
            return SyntaxTree.Parse(tree.Path, change.Text);

        var start = tree.GetPosition(range.Start.Line, range.Start.Character);
        var end = tree.GetPosition(range.End.Line, range.End.Character);
        return tree.WithChange(new TextChange(start, Math.Max(0, end - start), change.Text));
    }
}
