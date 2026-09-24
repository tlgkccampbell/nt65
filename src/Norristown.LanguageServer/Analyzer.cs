using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents the analysis of a program. The workspace takes an analyzer rather than calling
/// <see cref="Compiler.Analyze(IReadOnlyCollection{SyntaxTree}, ProjectSettings, ProgramAnalysis?, CancellationToken)"/>
/// directly, so that a test can hold an analysis back and see what the server does meanwhile.
/// </summary>
/// <param name="files">The program's files.</param>
/// <param name="project">The settings the program is built with.</param>
/// <param name="previous">The analysis to start from, or null for none.</param>
/// <param name="cancellation">Stops the analysis when nothing is waiting for it any more.</param>
internal delegate ProgramAnalysis Analyzer(
    IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, ProgramAnalysis? previous,
    CancellationToken cancellation);
