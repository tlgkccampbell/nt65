using System.Reflection;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class TypedNodeTests
{
    /// <summary>
    /// Every property of every node can be read on any line. The check covers each source as it
    /// stands, and each source with every line cut short after its first few tokens or before its
    /// last, which is what a line being typed looks like. A property that assumes a child element
    /// is present when the parser may leave it out throws here.
    /// </summary>
    [Fact]
    public void EveryPropertyReadsOnWholeAndBrokenLines()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var text = Repo.ReadText(path);
            var problems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variant in BrokenLines.Variants(text))
                problems.UnionWith(Problems(SyntaxTree.Parse(Repo.Named(path), variant)));
            return problems.Take(5);
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    /// <summary>A node's class is named after its kind, so a kind test and a type test agree.</summary>
    [Fact]
    public void EveryNodeIsTheClassOfItsKind()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
            Nodes(SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path)))
                .Where(node => node.GetType().Name != node.Kind + "Syntax")
                .Select(node => $"{node.Kind} is a {node.GetType().Name}")
                .Distinct());
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
    }

    [Fact]
    public void AnExportedDeclarationIsItsLinesStatement()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".export .proc main {\n}\n");
        var line = Assert.IsType<LineSyntax>(Assert.IsType<BlockSyntax>(tree.Root.Members[0]).Opener);
        var proc = Assert.IsType<ProcDeclarationSyntax>(line.Statement);
        Assert.Equal(".export", line.ExportKeyword?.Text);
        Assert.True(proc.IsExported);
        Assert.Equal(".export", proc.ExportToken?.Text);
        Assert.Equal(".proc main {", proc.GetText());
        Assert.Equal("main", proc.Name.Text);
        Assert.Null(proc.Signature);
    }

    [Fact]
    public void AMissingPieceIsNull()
    {
        var tree = SyntaxTree.Parse("test.nt65", "lda (ptr),y\n.repeat 4 {\n}\n.use a::b as\n");
        var instruction = Assert.IsType<InstructionStatementSyntax>(((LineSyntax)tree.Root.Members[0]).Statement);
        var operand = Assert.IsType<IndirectOperandSyntax>(instruction.Operand);
        Assert.Equal("y", operand.IndexRegister?.Text);
        Assert.Equal("ptr", operand.Address.GetText());

        var repeat = Assert.IsType<RepeatDirectiveSyntax>(((BlockSyntax)tree.Root.Members[1]).Opener.Statement);
        Assert.Null(repeat.Name);
        Assert.Equal("4", repeat.Expression.GetText());

        var use = Assert.IsType<UseDirectiveSyntax>(((LineSyntax)tree.Root.Members[2]).Statement);
        Assert.Equal(["a", "b"], use.Path.Names.Select(name => name.Text));
        Assert.Equal("as", use.AsKeyword?.Text);
        Assert.Null(use.Alias);
    }

    private static IEnumerable<SyntaxNode> Nodes(SyntaxTree tree) => tree.Root.DescendantNodes().Prepend(tree.Root);

    private static IEnumerable<string> Problems(SyntaxTree tree)
    {
        foreach (var node in Nodes(tree))
        {
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string? problem = null;
                try
                {
                    property.GetValue(node);
                }
                catch (TargetInvocationException e)
                {
                    problem = $"{node.GetType().Name}.{property.Name} throws {e.InnerException?.GetType().Name} on `{node.GetText()}`";
                }
                if (problem is not null)
                    yield return problem;
            }
        }
    }
}
