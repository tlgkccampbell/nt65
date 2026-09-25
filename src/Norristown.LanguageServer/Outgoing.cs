using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Converts each answer to the client's form as the last step before it is sent. The two
/// conversions made here are the two that depend on the client rather than on the program.
/// <list type="bullet">
/// <item>Every URI in the answer is converted to the form the client uses for that file. VS Code
/// escapes a drive's colon, as in <c>file:///c%3A/src</c>, and nt65 does not; a file named one
/// way in a diagnostic and another way in a go-to-definition is two files to an editor.</item>
/// <item>An edit is put in the shape the client accepts. That is a list of per-document edits,
/// each naming the version it was computed against, when the client declared
/// <c>documentChanges</c>, and a plain map of edits when it did not.</item>
/// </list>
/// Nothing here interprets an answer; it only rewrites the file URIs in it and reshapes edits.
/// </summary>
/// <param name="workspace">
/// The workspace, which knows the form of each file's URI that the client uses.
/// </param>
/// <param name="client">The capabilities the client declared.</param>
internal sealed class Outgoing(Workspace workspace, ClientCapabilities client)
{
    /// <summary>
    /// Returns the client's form of the URI for the file that a URI produced by the compiler
    /// points at.
    /// </summary>
    public string ToClient(string uri) => workspace.UriOf(Uris.ToPath(uri));

    /// <summary>Returns a location with its URI converted to the client's form.</summary>
    public Location ToClient(Location location) => location with { Uri = ToClient(location.Uri) };

    /// <summary>Returns the locations with their URIs converted to the client's form.</summary>
    public IReadOnlyList<Location> ToClient(IReadOnlyList<Location> locations) => [.. locations.Select(ToClient)];

    /// <summary>
    /// Returns a diagnostic with the URIs of its related locations, which may be in other files,
    /// converted to the client's form.
    /// </summary>
    public Protocol.Diagnostic ToClient(Protocol.Diagnostic diagnostic) =>
        diagnostic.RelatedInformation is not { Count: > 0 } related
            ? diagnostic
            : diagnostic with
            {
                RelatedInformation = [.. related.Select(one => one with { Location = ToClient(one.Location) })],
            };

    /// <summary>
    /// Returns the diagnostics with the URIs of their related locations converted to the client's
    /// form.
    /// </summary>
    public IReadOnlyList<Protocol.Diagnostic> ToClient(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        [.. diagnostics.Select(ToClient)];

    /// <summary>
    /// Returns a workspace edit with its URIs converted and its shape chosen for the client, or
    /// null when <paramref name="edit"/> is null. When the client accepts that, the edits of each
    /// file are given against the version of the document they were computed from, so that a
    /// client that has changed the document since rejects them rather than applying them at the
    /// wrong places. When it does not, they are given as a plain map of edits.
    /// </summary>
    /// <param name="edit">The edit.</param>
    /// <param name="from">The analyses the edit was computed from.</param>
    public WorkspaceEdit? ToClient(WorkspaceEdit? edit, IReadOnlyList<ProgramAnalysis> from)
    {
        if (edit is null)
            return null;
        var named = edit.Changes.ToDictionary(file => ToClient(file.Key), file => file.Value, StringComparer.Ordinal);
        return new WorkspaceEdit(
            named,
            !client.DocumentChanges
                ? null
                : [.. edit.Changes
                    .Select(file => (Uri: ToClient(file.Key), Version: VersionIn(from, file.Key), Edits: file.Value))
                    .OrderBy(file => file.Uri, StringComparer.Ordinal)
                    .Select(file => new TextDocumentEdit(
                        new OptionalVersionedTextDocumentIdentifier(file.Uri, file.Version), file.Edits))]);
    }

    /// <summary>
    /// Returns a code action with its edit, the diagnostics it answers and the command it runs
    /// converted to the client's form.
    /// </summary>
    /// <param name="action">The code action.</param>
    /// <param name="from">The analysis the action was computed from.</param>
    public CodeAction ToClient(CodeAction action, ProgramAnalysis from) => action with
    {
        Diagnostics = ToClient(action.Diagnostics),
        Edit = ToClient(action.Edit, [from]),
        Command = ToClient(action.Command),
    };

    /// <summary>
    /// Returns the code actions, each converted to the client's form, which were computed from
    /// the analysis <paramref name="from"/>.
    /// </summary>
    public IReadOnlyList<CodeAction> ToClient(IReadOnlyList<CodeAction> actions, ProgramAnalysis from) =>
        [.. actions.Select(action => ToClient(action, from))];

    /// <summary>
    /// Returns a command the client runs once a change is applied, converted to the client's form.
    /// When its first argument is a string, that argument is the URI of the file to put the caret
    /// in, and it is converted like any other URI.
    /// </summary>
    public Command? ToClient(Command? command) =>
        command?.Arguments is [string uri, ..] arguments
            ? command with { Arguments = [ToClient(uri), .. arguments.Skip(1)] }
            : command;

    /// <summary>
    /// Returns a routine in the call hierarchy with its URI converted to the client's form. The
    /// client later sends the item back unchanged in follow-up requests.
    /// </summary>
    public CallHierarchyItem ToClient(CallHierarchyItem item) => item with { Uri = ToClient(item.Uri) };

    /// <summary>Returns the call-hierarchy items, each with its URI converted to the client's form.</summary>
    public IReadOnlyList<CallHierarchyItem> ToClient(IReadOnlyList<CallHierarchyItem> items) =>
        [.. items.Select(ToClient)];

    /// <summary>Returns the incoming calls, with each caller's URI converted to the client's form.</summary>
    public IReadOnlyList<CallHierarchyIncomingCall> ToClient(IReadOnlyList<CallHierarchyIncomingCall> calls) =>
        [.. calls.Select(call => call with { From = ToClient(call.From) })];

    /// <summary>Returns the outgoing calls, with each callee's URI converted to the client's form.</summary>
    public IReadOnlyList<CallHierarchyOutgoingCall> ToClient(IReadOnlyList<CallHierarchyOutgoingCall> calls) =>
        [.. calls.Select(call => call with { To = ToClient(call.To) })];

    /// <summary>
    /// Returns declarations found across the workspace, with the URI of the file each is in
    /// converted to the client's form.
    /// </summary>
    public IReadOnlyList<SymbolInformation> ToClient(IReadOnlyList<SymbolInformation> symbols) =>
        [.. symbols.Select(symbol => symbol with { Location = ToClient(symbol.Location) })];

    /// <summary>
    /// Returns links from <c>.incbin</c> directives, with the URI of the file each names converted
    /// to the client's form.
    /// </summary>
    public IReadOnlyList<DocumentLink> ToClient(IReadOnlyList<DocumentLink> links) =>
        [.. links.Select(link => link with { Target = ToClient(link.Target) })];

    /// <summary>
    /// Returns a file's outline in the form the client supports. That is the tree formed by the
    /// file's segments and scopes when the client supports one, or else the older flat list, in
    /// which each entry names its container.
    /// </summary>
    /// <param name="uri">The URI of the file the outline is of, as the client named it.</param>
    /// <param name="outline">The file's declarations.</param>
    public object ToClient(string uri, IReadOnlyList<DocumentSymbol> outline) =>
        client.HierarchicalSymbols ? outline : Flat(ToClient(uri), outline, null);

    private static IReadOnlyList<SymbolInformation> Flat(
        string uri, IReadOnlyList<DocumentSymbol> outline, string? container) =>
        [.. outline.SelectMany(symbol => (IEnumerable<SymbolInformation>)
            [
                new SymbolInformation(symbol.Name, symbol.Kind, new Location(uri, symbol.Range), container),
                .. Flat(uri, symbol.Children ?? [], symbol.Name),
            ])];

    /// <summary>
    /// Returns the version of the document that the analyses read the file at
    /// <paramref name="uri"/> from, or null when they read it from disk.
    /// </summary>
    private int? VersionIn(IReadOnlyList<ProgramAnalysis> from, string uri)
    {
        var path = Uris.ToPath(uri);
        return from.Select(analysis => analysis.ModelFor(path)?.Tree)
            .OfType<SyntaxTree>()
            .Select(workspace.VersionOf)
            .FirstOrDefault(version => version is not null);
    }
}
