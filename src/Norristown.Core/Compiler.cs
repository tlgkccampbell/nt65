using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// The whole pipeline, from a set of source files to ca65 output and diagnostics. The
/// order of <c>files</c> must not affect the result.
/// </summary>
public static class Compiler
{
    // Syntax and names are the layers so far, so a program yields diagnostics and no output.
    /// <summary>Compiles <paramref name="files"/> as one program.</summary>
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files)
    {
        var trees = files
            .Select(SyntaxTree.Parse)
            .OrderBy(tree => tree.Path, StringComparer.Ordinal)
            .ToList();

        // The segment table is the program's, because a segment is declared exactly once
        // across it (§5.2); everything else at this stage is one file at a time.
        var diagnostics = new List<Diagnostic>();
        var segments = SegmentTable.Build(trees, diagnostics);
        foreach (var tree in trees)
        {
            diagnostics.AddRange(tree.Diagnostics);
            diagnostics.AddRange(SemanticModel.Create(tree, segments).Diagnostics);
        }

        return new([], Diagnostics.Ordered(diagnostics));
    }
}
