using System.Text.Json;

namespace Norristown.LanguageServer;

/// <summary>
/// What the client at the other end can take, read once from the <c>initialize</c> request and
/// asked of from then on. A capability nobody declares is off: a client that says nothing gets
/// the plain answer, which every client understands.
/// </summary>
/// <param name="RefreshesTokens">Whether the client can be asked to fetch semantic tokens again.</param>
/// <param name="RefreshesLenses">Whether it can be asked to fetch code lenses again.</param>
/// <param name="Snippets">Whether a completion may write more than a word, with stops in it.</param>
/// <param name="DocumentChanges">
/// Whether a workspace edit may be a list of per-document edits, each naming the revision it was
/// worked out against, so that one worked out against a buffer that has since changed is refused
/// rather than applied.
/// </param>
/// <param name="HierarchicalSymbols">Whether an outline may be a tree rather than a flat list.</param>
/// <param name="WorkspaceFolders">Whether the client opened folders, and will say when they change.</param>
internal sealed record ClientCapabilities(
    bool RefreshesTokens,
    bool RefreshesLenses,
    bool Snippets,
    bool DocumentChanges,
    bool HierarchicalSymbols,
    bool WorkspaceFolders)
{
    /// <summary>A client that has declared nothing, which is what a server assumes until it has.</summary>
    public static ClientCapabilities None { get; } = new(false, false, false, false, false, false);

    /// <summary>What the <c>capabilities</c> of an <c>initialize</c> request declare.</summary>
    public static ClientCapabilities Of(JsonElement? capabilities) => new(
        RefreshesTokens: Flag(capabilities, "workspace", "semanticTokens", "refreshSupport"),
        RefreshesLenses: Flag(capabilities, "workspace", "codeLens", "refreshSupport"),
        Snippets: Flag(capabilities, "textDocument", "completion", "completionItem", "snippetSupport"),
        DocumentChanges: Flag(capabilities, "workspace", "workspaceEdit", "documentChanges"),
        HierarchicalSymbols: Flag(
            capabilities, "textDocument", "documentSymbol", "hierarchicalDocumentSymbolSupport"),
        WorkspaceFolders: Flag(capabilities, "workspace", "workspaceFolders"));

    /// <summary>Whether the nested property <paramref name="path"/> names is declared true.</summary>
    private static bool Flag(JsonElement? capabilities, params string[] path)
    {
        var at = capabilities;
        foreach (var step in path)
        {
            if (at is not { ValueKind: JsonValueKind.Object } held || !held.TryGetProperty(step, out var next))
                return false;
            at = next;
        }
        return at?.ValueKind == JsonValueKind.True;
    }
}
