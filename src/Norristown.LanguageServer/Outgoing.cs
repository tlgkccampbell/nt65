using Norristown.LanguageServer.Protocol;

namespace Norristown.LanguageServer;

/// <summary>
/// The edge every answer leaves by. Two things happen to an answer here, and they are the two
/// things that depend on the client rather than on the program:
/// <list type="bullet">
/// <item>every URI in it is spelled the way the client spells that file. VS Code escapes a
/// drive's colon, <c>file:///c%3A/src</c>, and nt65 does not; a file named one way in a
/// diagnostic and another in a go-to-definition is two files to an editor.</item>
/// <item>an edit is put in the shape the client takes: a list of per-document edits, each
/// naming the revision it was worked out against, where the client declared
/// <c>documentChanges</c>, and a plain map of edits where it did not.</item>
/// </list>
/// Nothing here knows what an answer means; it only names its files.
/// </summary>
/// <param name="workspace">What the editor is working on, which knows how the client names a file.</param>
/// <param name="client">What the client declared it can take.</param>
internal sealed class Outgoing(Workspace workspace, ClientCapabilities client)
{
    /// <summary>How the client names the file a URI from inside the compiler points at.</summary>
    public string Spell(string uri) => workspace.UriOf(Workspace.PathOf(uri));

    /// <summary>A place in a file, named the way the client names it.</summary>
    public Location Spell(Location location) => location with { Uri = Spell(location.Uri) };

    public IReadOnlyList<Location> Spell(IReadOnlyList<Location> locations) => [.. locations.Select(Spell)];

    /// <summary>A diagnostic, whose related places may be in other files.</summary>
    public Protocol.Diagnostic Spell(Protocol.Diagnostic diagnostic) =>
        diagnostic.RelatedInformation is not { Count: > 0 } related
            ? diagnostic
            : diagnostic with
            {
                RelatedInformation = [.. related.Select(one => one with { Location = Spell(one.Location) })],
            };

    public IReadOnlyList<Protocol.Diagnostic> Spell(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        [.. diagnostics.Select(Spell)];

    /// <summary>
    /// A workspace edit, named and shaped for the client: the edits of each file against the
    /// revision the client holds of it, where the client takes that, and the plain map of edits
    /// where it does not.
    /// </summary>
    public WorkspaceEdit? Spell(WorkspaceEdit? edit)
    {
        if (edit is null)
            return null;
        var named = edit.Changes.ToDictionary(file => Spell(file.Key), file => file.Value, StringComparer.Ordinal);
        return new WorkspaceEdit(
            named,
            !client.DocumentChanges
                ? null
                : [.. named
                    .OrderBy(file => file.Key, StringComparer.Ordinal)
                    .Select(file => new TextDocumentEdit(
                        new OptionalVersionedTextDocumentIdentifier(file.Key, workspace.VersionOf(file.Key)),
                        file.Value))]);
    }

    /// <summary>A change the editor offers: its edit, the diagnostics it answers and the command it runs.</summary>
    public CodeAction Spell(CodeAction action) => action with
    {
        Diagnostics = Spell(action.Diagnostics),
        Edit = Spell(action.Edit)!,
        Command = Spell(action.Command),
    };

    public IReadOnlyList<CodeAction> Spell(IReadOnlyList<CodeAction> actions) => [.. actions.Select(Spell)];

    /// <summary>
    /// A command the client runs once a change is written. Its first argument is the file to
    /// put the caret in, which is a URI like any other.
    /// </summary>
    public Command? Spell(Command? command) =>
        command?.Arguments is [string uri, ..] arguments
            ? command with { Arguments = [Spell(uri), .. arguments.Skip(1)] }
            : command;

    /// <summary>A routine in the call hierarchy, which the client hands back as the server gave it.</summary>
    public CallHierarchyItem Spell(CallHierarchyItem item) => item with { Uri = Spell(item.Uri) };

    public IReadOnlyList<CallHierarchyItem> Spell(IReadOnlyList<CallHierarchyItem> items) =>
        [.. items.Select(Spell)];

    public IReadOnlyList<CallHierarchyIncomingCall> Spell(IReadOnlyList<CallHierarchyIncomingCall> calls) =>
        [.. calls.Select(call => call with { From = Spell(call.From) })];

    public IReadOnlyList<CallHierarchyOutgoingCall> Spell(IReadOnlyList<CallHierarchyOutgoingCall> calls) =>
        [.. calls.Select(call => call with { To = Spell(call.To) })];

    /// <summary>A declaration found across the workspace, which names the file it is in.</summary>
    public IReadOnlyList<SymbolInformation> Spell(IReadOnlyList<SymbolInformation> symbols) =>
        [.. symbols.Select(symbol => symbol with { Location = Spell(symbol.Location) })];

    /// <summary>A link from an <c>.incbin</c> to the file it names.</summary>
    public IReadOnlyList<DocumentLink> Spell(IReadOnlyList<DocumentLink> links) =>
        [.. links.Select(link => link with { Target = Spell(link.Target) })];
}
