using System.Collections.Frozen;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The program's segments. The standard names are predeclared; every other segment
/// is declared exactly once, by a <c>.segment "NAME": size</c> item in one file or (from
/// Stage 6) in <c>nt65.json</c>. A segment block naming a segment declared nowhere is an
/// error, so a misspelled name is caught before ld65 runs.
/// </summary>
public sealed class SegmentTable
{
    /// <summary>Where items outside any segment block go.</summary>
    public const string DefaultSegment = "CODE";

    private static readonly FrozenDictionary<string, AddressSize> standard = new Dictionary<string, AddressSize>(
        StringComparer.Ordinal)
    {
        ["ZEROPAGE"] = AddressSize.ZeroPage,
        ["CODE"] = AddressSize.Absolute,
        ["DATA"] = AddressSize.Absolute,
        ["BSS"] = AddressSize.Absolute,
        ["RODATA"] = AddressSize.Absolute,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly Dictionary<string, Segment> segments;

    private SegmentTable(Dictionary<string, Segment> segments) => this.segments = segments;

    /// <summary>Just the predeclared names: what a file compiled on its own starts from.</summary>
    public static SegmentTable Standard => new(Predeclared());

    /// <summary>The segments in the table, ordered by name.</summary>
    public IEnumerable<Segment> Segments => segments.Values.OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>
    /// The table for a program, with the declarations found in <paramref name="trees"/>.
    /// Declarations are read in file and line order so that a program built from the same
    /// files reports the same thing whatever order they arrive in.
    /// </summary>
    public static SegmentTable Build(IEnumerable<SyntaxTree> trees, List<Diagnostic> diagnostics) =>
        Build(trees, [], diagnostics);

    /// <summary>
    /// The table for a program whose project file declares some of its segments. Those
    /// are read first, so a file declaring one of them is the declaration that is reported.
    /// </summary>
    public static SegmentTable Build(
        IEnumerable<SyntaxTree> trees, IEnumerable<Segment> configured, List<Diagnostic> diagnostics)
    {
        var segments = Predeclared();
        foreach (var segment in configured.OrderBy(segment => segment.Name, StringComparer.Ordinal))
        {
            if (segments.TryGetValue(segment.Name, out var predeclared))
            {
                diagnostics.Add(new Diagnostic(segment.Declaration!.Value, Severity.Error,
                    $"segment \"{segment.Name}\" is already declared",
                    [new RelatedSpan(segment.Declaration.Value,
                        $"\"{predeclared.Name}\" is one of the standard segment names, which are predeclared")]));
                continue;
            }
            segments[segment.Name] = segment;
        }

        var declarations = trees
            .SelectMany(Declarations)
            .OrderBy(d => d.Node.Tree.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start);
        foreach (var (node, name, span) in declarations)
        {
            var declared = node.Tree.GetSpan(span);
            if (segments.TryGetValue(name, out var existing))
            {
                diagnostics.Add(new Diagnostic(declared, Severity.Error,
                    $"segment \"{name}\" is already declared",
                    existing.Declaration is { } first
                        ? [new RelatedSpan(first, "declared here")]
                        : [new RelatedSpan(declared, "it is one of the standard segment names, which are predeclared")]));
                continue;
            }
            segments[name] = new Segment(name, SizeOf(node), declared);
        }
        return new SegmentTable(segments);
    }

    /// <summary>The segment <paramref name="name"/>, or null when nothing declares it.</summary>
    public Segment? Find(string name) => segments.GetValueOrDefault(name);

    private static Dictionary<string, Segment> Predeclared() =>
        standard.ToDictionary(pair => pair.Key, pair => new Segment(pair.Key, pair.Value, null), StringComparer.Ordinal);

    private static IEnumerable<Declaration> Declarations(SyntaxTree tree)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (node.Kind != SyntaxKind.SegmentDeclaration)
                continue;
            foreach (var token in node.ChildTokens)
            {
                if (token.Kind == SyntaxKind.StringLiteral)
                {
                    yield return new Declaration(node, SegmentNames.Unquote(token.Text), token.Span);
                    break;
                }
            }
        }
    }

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c> a declaration writes after its <c>:</c>.</summary>
    private static AddressSize SizeOf(SyntaxNode declaration)
    {
        foreach (var token in declaration.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Identifier && SegmentNames.ParseSize(token.Text) is { } size)
                return size;
        }

        // The parser has already reported the missing size; absolute is the default that
        // makes the fewest further complaints.
        return AddressSize.Absolute;
    }

    /// <summary>One <c>.segment "NAME": size</c> item, before it reaches the table.</summary>
    private readonly record struct Declaration(SyntaxNode Node, string Name, TextSpan Span);
}
