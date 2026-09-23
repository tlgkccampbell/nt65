using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// One file whose diagnostics are published to the editor. Diagnostics are published for every
/// file of every program, not only the files the client has open, because an export broken in
/// one file breaks every module that uses it.
/// </summary>
/// <param name="Uri">How the client names it: its own spelling for a document it has opened.</param>
/// <param name="Version">The revision the client holds, or null for a file it has not opened.</param>
/// <param name="Tree">The file, or null for a project file, which is JSON and not a program's.</param>
/// <param name="Diagnostics">What is wrong with it.</param>
/// <param name="Configuration">Which <c>.if</c> branches the build takes, used to fade the lines of branches it omits.</param>
internal readonly record struct Published(
    string Uri,
    int? Version,
    SyntaxTree? Tree,
    IReadOnlyList<Diagnostic> Diagnostics,
    Configuration Configuration);
