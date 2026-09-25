using System.Text.Json;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents the optional protocol features the client supports, read once from the
/// <c>initialize</c> request and consulted from then on. A capability the client does not
/// declare is treated as off, so a client that declares nothing gets the plain form of each
/// answer, which every client understands.
/// </summary>
/// <param name="RefreshesTokens">Whether the client can be asked to fetch semantic tokens again.</param>
/// <param name="RefreshesLenses">Whether it can be asked to fetch code lenses again.</param>
/// <param name="RefreshesHints">Whether it can be asked to fetch inlay hints again.</param>
/// <param name="Snippets">Whether a completion may insert a snippet with tab stops rather than plain text.</param>
/// <param name="DocumentChanges">
/// Whether a workspace edit may be a list of per-document edits, each naming the revision it was
/// computed against, so that an edit computed against a buffer that has since changed is
/// refused rather than applied.
/// </param>
/// <param name="HierarchicalSymbols">Whether an outline may be a tree rather than a flat list.</param>
/// <param name="WorkspaceFolders">
/// Whether the client opened folders, and will report when they change.
/// </param>
/// <param name="WillRenameFiles">
/// Whether the client asks the server before it moves a file, so that the edits the move
/// requires are applied together with it rather than afterwards.
/// </param>
/// <param name="WatchesWhatItIsAsked">
/// Whether the client supports registering file watchers dynamically, so it can be asked after
/// connecting to watch more files. This matters because the files an <c>.incbin</c> reads are
/// named by the program and are not known until it has been read.
/// </param>
/// <param name="ResolvesActionEdits">
/// Whether the client can be sent a code action without its edits and ask for them with
/// <c>codeAction/resolve</c> once the programmer picks it. A caret move then costs no edits.
/// </param>
internal sealed record ClientCapabilities(
    bool RefreshesTokens,
    bool RefreshesLenses,
    bool RefreshesHints,
    bool Snippets,
    bool DocumentChanges,
    bool HierarchicalSymbols,
    bool WorkspaceFolders,
    bool WillRenameFiles,
    bool WatchesWhatItIsAsked,
    bool ResolvesActionEdits)
{
    /// <summary>
    /// Gets the capabilities of a client that has declared nothing, which is what the server
    /// assumes until <c>initialize</c> arrives.
    /// </summary>
    public static ClientCapabilities None { get; } =
        new(false, false, false, false, false, false, false, false, false, false);

    /// <summary>
    /// Returns the capabilities that the <c>capabilities</c> of an <c>initialize</c> request
    /// declare.
    /// </summary>
    public static ClientCapabilities Of(JsonElement? capabilities) => new(
        RefreshesTokens: Flag(capabilities, "workspace", "semanticTokens", "refreshSupport"),
        RefreshesLenses: Flag(capabilities, "workspace", "codeLens", "refreshSupport"),
        RefreshesHints: Flag(capabilities, "workspace", "inlayHint", "refreshSupport"),
        Snippets: Flag(capabilities, "textDocument", "completion", "completionItem", "snippetSupport"),
        DocumentChanges: Flag(capabilities, "workspace", "workspaceEdit", "documentChanges"),
        HierarchicalSymbols: Flag(
            capabilities, "textDocument", "documentSymbol", "hierarchicalDocumentSymbolSupport"),
        WorkspaceFolders: Flag(capabilities, "workspace", "workspaceFolders"),
        WillRenameFiles: Flag(capabilities, "workspace", "fileOperations", "willRename"),
        WatchesWhatItIsAsked: Flag(
            capabilities, "workspace", "didChangeWatchedFiles", "dynamicRegistration"),
        ResolvesActionEdits: Lists(
            capabilities, "edit", "textDocument", "codeAction", "resolveSupport", "properties"));

    /// <summary>Checks whether the nested property <paramref name="path"/> names is declared true.</summary>
    private static bool Flag(JsonElement? capabilities, params string[] path) =>
        At(capabilities, path)?.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Checks whether the nested property <paramref name="path"/> names is a list that holds the
    /// string <paramref name="value"/>.
    /// </summary>
    private static bool Lists(JsonElement? capabilities, string value, params string[] path) =>
        At(capabilities, path) is { ValueKind: JsonValueKind.Array } list
            && list.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);

    /// <summary>Returns the nested property <paramref name="path"/> names, or null where there is none.</summary>
    private static JsonElement? At(JsonElement? capabilities, string[] path)
    {
        var at = capabilities;
        foreach (var step in path)
        {
            if (at is not { ValueKind: JsonValueKind.Object } held || !held.TryGetProperty(step, out var next))
                return null;
            at = next;
        }
        return at;
    }
}
