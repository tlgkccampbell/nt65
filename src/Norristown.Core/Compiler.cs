using Norristown.Syntax;

namespace Norristown;

public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The whole pipeline, from a set of source files to ca65 output and diagnostics. The
/// order of <c>files</c> must not affect the result.
/// </summary>
public static class Compiler
{
    // Stage 1: lexing and blocks only, so a program yields syntax diagnostics and no output.
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files)
    {
        var diagnostics = files
            .SelectMany(file => SyntaxTree.Parse(file).Diagnostics)
            .OrderBy(d => d.Span.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
        return new([], diagnostics);
    }
}
