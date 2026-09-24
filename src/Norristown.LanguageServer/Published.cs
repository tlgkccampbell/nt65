using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents a file whose diagnostics are published to the editor. Diagnostics are published for every
/// file of every program, not only the files the client has open, because an export broken in
/// one file breaks every module that uses it.
/// </summary>
/// <param name="Uri">
/// The client's URI for the file, which for a document the client has opened is the client's own
/// form of it.
/// </param>
/// <param name="Version">The version the client holds, or null for a file it has not opened.</param>
/// <param name="Tree">
/// The file's syntax tree, or null for a project file, which is JSON rather than part of a program.
/// </param>
/// <param name="Diagnostics">The file's diagnostics.</param>
/// <param name="Configuration">
/// The configuration that decides which <c>.if</c> branches the build takes, used to fade the
/// lines of branches it omits.
/// </param>
internal readonly record struct Published(
    string Uri,
    int? Version,
    SyntaxTree? Tree,
    IReadOnlyList<Diagnostic> Diagnostics,
    Configuration Configuration);
