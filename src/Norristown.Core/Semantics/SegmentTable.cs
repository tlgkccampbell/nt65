using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The program's segments. The standard names are predeclared; every other segment
/// is declared exactly once, by a <c>.segment NAME: size</c> item in one file or in
/// <c>nt65.json</c>. A segment block naming a segment declared nowhere is an
/// error, so a misspelled name is caught before ld65 runs.
/// </summary>
public sealed class SegmentTable
{
    // A file's segment declarations, read once per tree: after an edit, only one file of the
    // program is a tree nothing has read yet.
    private static readonly ConditionalWeakTable<SyntaxTree, List<Declaration>> written = new();

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

    // The `dp = e` and `bank = e` a file's declarations write, by segment. They are expressions,
    // worth something only once the program's constants are, which is after the table is needed.
    private readonly Dictionary<string, List<SyntaxNode>> attributes = new(StringComparer.Ordinal);

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
        Build(trees, [], Configuration.Everything, diagnostics);

    /// <summary>
    /// The table for a program whose project file declares some of its segments. Those
    /// are read first, so a file declaring one of them is the declaration that is reported.
    /// </summary>
    public static SegmentTable Build(
        IEnumerable<SyntaxTree> trees, IEnumerable<Segment> configured, Configuration configuration,
        List<Diagnostic> diagnostics)
    {
        var segments = Predeclared();
        var table = new SegmentTable(segments);
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

        // A declaration under an `.if` the build does not take is not a declaration, which
        // is what lets two branches declare the same segment differently.
        var declarations = trees
            .SelectMany(Declarations)
            .Where(d => configuration.Includes(d.Node))
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
            table.attributes[name] = [.. node.ChildNodes.Where(child => child.Kind == SyntaxKind.SegmentAttribute)];
        }
        return table;
    }

    /// <summary>
    /// The value of a segment's <c>dp</c> or <c>bank</c>, or null with the reason reported: it
    /// is a constant in range, given once, and a <c>dp</c> is for a <c>zp</c> segment, the only
    /// kind whose symbols are reached through the direct page.
    /// </summary>
    public static long? Check(
        string segment, AddressSize size, string word, long? value, Span at, (long? DirectPage, long? Bank) already,
        List<Diagnostic> diagnostics)
    {
        string? problem = null;
        if ((word == "dp" ? already.DirectPage : already.Bank) is not null)
            problem = $"segment \"{segment}\" already gives its `{word}`";
        else if (word == "dp" && size != AddressSize.ZeroPage)
            problem = $"`dp` says which direct page a `zp` segment is reached through, and \"{segment}\" is not `zp`";
        else if (value is null)
            problem = $"`{word}` needs a constant";
        else if (value < 0 || value > (word == "dp" ? 0xffff : 0xff))
            problem = word == "dp" ? "the direct page is a 16-bit address" : "a bank is one byte";
        if (problem is null)
            return value;
        diagnostics.Add(new Diagnostic(at, Severity.Error, problem));
        return null;
    }

    /// <summary>What is said of a segment that declares mirrors and no home bank for them to mirror.</summary>
    public static string MirrorsNeedABank(string segment) =>
        $"segment \"{segment}\" gives `mirrors` and no `bank`: a mirror shows a segment's home bank in another bank";

    /// <summary>
    /// Works out the <c>dp = e</c>, <c>bank = e</c> and <c>mirrors = [...]</c> the files' declarations write, now that
    /// <paramref name="valueOf"/> can answer what an expression is worth.
    /// </summary>
    public void Evaluate(Func<SyntaxNode, long?> valueOf, List<Diagnostic> diagnostics)
    {
        foreach (var (name, written) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var segment = segments[name];
            SyntaxNode? mirrors = null;
            foreach (var attribute in written)
            {
                if (attribute.ChildTokens.Length == 0)
                    continue;
                var word = attribute.ChildTokens[0].Text.ToLowerInvariant();
                var at = attribute.Tree.GetSpan(attribute.Span);
                if (word == "mirrors")
                {
                    if (mirrors is not null)
                    {
                        diagnostics.Add(new Diagnostic(at, Severity.Error, $"segment \"{name}\" already gives its `mirrors`"));
                        continue;
                    }
                    mirrors = attribute;
                    segment = segment with { Mirrors = Mirrors(attribute, valueOf, diagnostics) };
                    continue;
                }
                if (attribute.ChildNodes.FirstOrDefault() is not { } expression)
                    continue;
                var value = valueOf(expression);
                if (Check(name, segment.Size, word, value, at, (segment.DirectPage, segment.Bank), diagnostics) is not { } valid)
                    continue;
                segment = word == "dp" ? segment with { DirectPage = valid } : segment with { Bank = valid };
            }
            if (mirrors is not null && segment.Bank is null)
            {
                diagnostics.Add(new Diagnostic(mirrors.Tree.GetSpan(mirrors.Span), Severity.Error, MirrorsNeedABank(name)));
                segment = segment with { Mirrors = [] };
            }
            segments[name] = segment;
        }
    }

    /// <summary>The segment <paramref name="name"/>, or null when nothing declares it.</summary>
    public Segment? Find(string name) => segments.GetValueOrDefault(name);

    /// <summary>Whether <paramref name="tree"/> declares a segment, taken or not by the build.</summary>
    internal static bool Declares(SyntaxTree tree) => Declarations(tree).Count > 0;

    /// <summary>The banks a <c>mirrors = [$00..$3f, $80]</c> gives, each checked to be a constant bank.</summary>
    private static List<(long First, long Last)> Mirrors(
        SyntaxNode attribute, Func<SyntaxNode, long?> valueOf, List<Diagnostic> diagnostics)
    {
        var banks = new List<(long First, long Last)>();
        foreach (var range in attribute.ChildNodes.Where(child => child.Kind == SyntaxKind.BankRange))
        {
            var ends = range.ChildNodes.Select(valueOf).ToList();
            if (ends is not [{ } first, ..] || ends[^1] is not { } last || first is < 0 or > 0xff || last is < 0 or > 0xff
                || first > last)
            {
                diagnostics.Add(new Diagnostic(range.Tree.GetSpan(range.Span), Severity.Error,
                    "a mirror is a constant bank, or a range of banks from the lower to the higher, such as `$00..$3f`"));
                continue;
            }
            banks.Add((first, last));
        }
        return banks;
    }

    private static Dictionary<string, Segment> Predeclared() =>
        standard.ToDictionary(pair => pair.Key, pair => new Segment(pair.Key, pair.Value, null), StringComparer.Ordinal);

    private static List<Declaration> Declarations(SyntaxTree tree) =>
        written.GetValue(tree, tree => [.. Read(tree)]);

    private static IEnumerable<Declaration> Read(SyntaxTree tree)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (node.Kind != SyntaxKind.SegmentDeclaration)
                continue;
            if (node.ChildTokens.Length > 1 && SegmentNames.Of(node.ChildTokens[1]) is { } name)
                yield return new Declaration(node, name, node.ChildTokens[1].Span);
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

    /// <summary>One <c>.segment NAME: size</c> item, before it reaches the table.</summary>
    private readonly record struct Declaration(SyntaxNode Node, string Name, TextSpan Span);
}
