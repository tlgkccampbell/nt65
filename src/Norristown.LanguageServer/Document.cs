using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// One document the client has open, at the revision the client last sent. Its analysis is
/// built the first time something asks for it and then kept, so an edit that nothing looks
/// at costs a parse and no more.
/// </summary>
internal sealed class Document
{
    private readonly Lazy<Analysis> analysis;

    public Document(string uri, int version, SyntaxTree tree)
    {
        Uri = uri;
        Version = version;
        Tree = tree;
        analysis = new Lazy<Analysis>(() => Analyze(tree));
    }

    /// <summary>The document's URI, which is how the client names it.</summary>
    public string Uri { get; }

    /// <summary>The revision this tree was parsed from.</summary>
    public int Version { get; }

    /// <summary>The document's syntax.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>What the document's names and constants mean.</summary>
    public SemanticModel Model => analysis.Value.Model;

    /// <summary>Everything wrong with the document, from every layer, in order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => analysis.Value.Diagnostics;

    /// <summary>
    /// An open file is analyzed on its own, against the segments it declares itself. A
    /// project's files and its <c>nt65.json</c> arrive with Stage 6.
    /// </summary>
    private static Analysis Analyze(SyntaxTree tree)
    {
        var problems = new List<Diagnostic>();
        var model = SemanticModel.Create(tree, SegmentTable.Build([tree], problems));
        problems.AddRange(tree.Diagnostics);
        problems.AddRange(model.Diagnostics);

        // What the CPU makes wrong is shown as it is typed (§5.1, §7.2). The 65816 needs the
        // processor-state analysis of §7.3 before its instructions can be sized, so until
        // Stage 11 there is nothing to say about them.
        var cpu = ProgramCpu.Resolve([tree], configured: null, problems);
        if (cpu != Cpu.Wdc65816)
            problems.AddRange(CodeLayout.Create(model, cpu).Diagnostics);
        return new Analysis(model, Norristown.Diagnostics.Ordered(problems));
    }

    /// <summary>One document's analysis, built once.</summary>
    /// <param name="Model">What its names mean.</param>
    /// <param name="Diagnostics">Everything wrong with it.</param>
    private sealed record Analysis(SemanticModel Model, IReadOnlyList<Diagnostic> Diagnostics);
}
