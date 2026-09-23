using System.Collections.Immutable;
using System.Reflection;
using Norristown.Syntax;
using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

public sealed class GeneratedSyntaxTests
{
    /// <summary>
    /// Every kind a node can have is in the table and has a class. The node kinds are
    /// <see cref="SyntaxKind.Line"/> and everything after it; what comes before is trivia and
    /// tokens.
    /// </summary>
    [Fact]
    public void EveryNodeKindHasARowAndAClass()
    {
        var table = Table();
        var described = table.SelectMany(node => node.Kinds).ToHashSet(StringComparer.Ordinal);
        var kinds = Enum.GetValues<SyntaxKind>().Where(kind => kind >= SyntaxKind.Line).Select(kind => kind.ToString());
        Assert.Equal([.. kinds.Order(StringComparer.Ordinal)], [.. described.Order(StringComparer.Ordinal)]);

        var assembly = typeof(SyntaxNode).Assembly;
        foreach (var node in table)
        {
            var type = assembly.GetType($"Norristown.Syntax.{node.Name}");
            Assert.NotNull(type);
            Assert.Equal(node.IsAbstract, type.IsAbstract);
            Assert.Equal(node.Base, type.BaseType?.Name);
        }
    }

    /// <summary>
    /// What the table records about a piece of a node: its type, whether it is required, and the
    /// kinds a token may be. A list is always required: when nothing is written it is an empty
    /// list, never a missing one.
    /// </summary>
    [Fact]
    public void APieceSaysItsTypeAndItsKinds()
    {
        foreach (var piece in Table().SelectMany(node => node.Slots).Where(slot => slot.IsPiece))
        {
            Assert.NotEqual("", piece.Type);
            if (piece.List != ListShape.None)
                Assert.True(piece.IsRequired, $"{piece.Name} is a list and optional");
            Assert.Equal(piece.IsToken, piece.Kinds.Length > 0);
        }
    }

    /// <summary>
    /// Every slot of every node's layout can be read from the node: a slot declared by a base
    /// class is abstract there and overridden in the derived class, so the property reads the
    /// right slot.
    /// </summary>
    [Fact]
    public void EverySlotOfALayoutIsAPropertyOfTheClass()
    {
        var tree = new NodeTree(Table());
        var assembly = typeof(SyntaxNode).Assembly;
        foreach (var node in tree.Nodes.Where(node => !node.IsAbstract && !node.IsHandWritten))
        {
            var type = assembly.GetType($"Norristown.Syntax.{node.Name}")!;
            var green = assembly.GetType($"Norristown.Syntax.InternalSyntax.{node.Name}");
            Assert.NotNull(green);
            var slots = tree.Layout(node);
            Assert.Contains(
                green.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
                constructor => constructor.GetParameters().Length == slots.Length);
            var properties = type.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (_, slot, _) in slots)
                Assert.Contains(slot.Name, properties);
        }
    }

    /// <summary>
    /// Each concrete node class carries both its own <c>Accept</c> overrides, and the visitors
    /// carry exactly one method per class of a node that is not internal. Together with the
    /// overloads the compiler has to pick from — one per sealed class — that is what makes
    /// <c>Accept</c> land on the method for the node's own class.
    /// </summary>
    [Fact]
    public void EveryNodeClassAcceptsAndEveryClassHasAVisitMethod()
    {
        var table = Table();
        var assembly = typeof(SyntaxNode).Assembly;
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var node in table.Where(node => !node.IsAbstract))
        {
            var type = assembly.GetType($"Norristown.Syntax.{node.Name}")!;
            var accepts = type.GetMethods(declared).Where(method => method.Name == "Accept").ToList();
            Assert.Equal(2, accepts.Count);
            Assert.Contains(accepts, method => !method.IsGenericMethodDefinition);
            Assert.Contains(accepts, method => method.IsGenericMethodDefinition);
        }

        foreach (var visitor in new[] { typeof(SyntaxVisitor), typeof(SyntaxVisitor<>) })
        {
            var methods = visitor.GetMethods(declared | BindingFlags.NonPublic)
                .Where(method => method.Name.StartsWith("Visit", StringComparison.Ordinal) && method.Name != "Visit")
                .ToDictionary(method => method.Name, method => method.GetParameters()[0].ParameterType.Name);
            var wanted = table.Where(node => node.HasVisitMethod)
                .ToDictionary(node => $"Visit{node.BareName}", node => node.Name);
            Assert.Equal(wanted.OrderBy(pair => pair.Key, StringComparer.Ordinal), methods.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
    }

    /// <summary>A walker reaches every node of every source in the repository, each exactly once.</summary>
    [Fact]
    public void AWalkerVisitsEveryNodeExactlyOnce()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), path =>
        {
            var tree = SyntaxTree.Parse(Repo.Named(path), Repo.ReadText(path));
            var walker = new Collector();
            walker.Visit(tree.Root);
            var expected = tree.Root.DescendantNodes().Prepend(tree.Root).ToList();
            if (walker.Visited.Count != expected.Count)
                return [$"{Repo.Named(path)}: walked {walker.Visited.Count} nodes of {expected.Count}"];
            return expected.Where((node, i) => !ReferenceEquals(node, walker.Visited[i]))
                .Select(node => $"{Repo.Named(path)}: {node} is not where the walk reached it").Take(3);
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary><c>Accept</c> hands a node to the method for its own class, and nothing else to it.</summary>
    [Fact]
    public void AcceptHandsANodeToTheMethodForItsClass()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc main {\n@loop: lda base + 1,x\n}\n");
        var visitor = new MethodNames();
        foreach (var node in tree.Root.DescendantNodes().Prepend(tree.Root))
            Assert.Equal(MethodNames.Expected(node), node.Accept(visitor));

        // Every kind the visitor overrides occurs in that source, so every override was tested.
        Assert.Equal(
            ["AbsoluteOperand", "Block", "File", "InstructionStatement", "Label", "LabeledLine", "Line", "ProcDeclaration"],
            tree.Root.DescendantNodes().Prepend(tree.Root).Select(node => node.Kind.ToString())
                .Where(MethodNames.Overridden.Contains).Distinct().Order(StringComparer.Ordinal));
    }

    private static ImmutableArray<NodeRow> Table() =>
        NodeTable.Read(Repo.ReadText(Repo.Path(NodeTable.File.Split('/'))));

    private sealed class Collector : SyntaxWalker
    {
        public List<SyntaxNode> Visited { get; } = [];

        public override void DefaultVisit(SyntaxNode node)
        {
            Visited.Add(node);
            base.DefaultVisit(node);
        }
    }

    private sealed class MethodNames : SyntaxVisitor<string>
    {
        public static readonly HashSet<string> Overridden = new(StringComparer.Ordinal)
        {
            "File", "Block", "Line", "ProcDeclaration", "LabeledLine", "Label", "InstructionStatement", "AbsoluteOperand",
        };

        public static string Expected(SyntaxNode node) =>
            Overridden.Contains(node.Kind.ToString()) ? $"Visit{node.Kind}" : "DefaultVisit";

        public override string DefaultVisit(SyntaxNode node) => nameof(DefaultVisit);

        public override string VisitFile(FileSyntax node) => nameof(VisitFile);

        public override string VisitBlock(BlockSyntax node) => nameof(VisitBlock);

        public override string VisitLine(LineSyntax node) => nameof(VisitLine);

        public override string VisitProcDeclaration(ProcDeclarationSyntax node) => nameof(VisitProcDeclaration);

        public override string VisitLabeledLine(LabeledLineSyntax node) => nameof(VisitLabeledLine);

        public override string VisitLabel(LabelSyntax node) => nameof(VisitLabel);

        public override string VisitInstructionStatement(InstructionStatementSyntax node) => nameof(VisitInstructionStatement);

        public override string VisitAbsoluteOperand(AbsoluteOperandSyntax node) => nameof(VisitAbsoluteOperand);
    }
}
