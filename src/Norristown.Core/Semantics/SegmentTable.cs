using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The program's segments. Every segment is declared exactly once, by a
/// <c>.segment NAME: size</c> item in one file or in <c>nt65.json</c>; the standard names are
/// predeclared, and that predeclaration is what stands when the program says nothing, so a
/// program may declare one of them once, at its own size, to give it a direct page, a bank or
/// mirrors. A segment block naming a segment declared nowhere is an error, so a misspelled
/// name is caught before ld65 runs.
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

    // The address spaces other than the host's, by name.
    private readonly Dictionary<string, AddressSpace> spaces = new(StringComparer.Ordinal);

    // The `dp = e` and `bank = e` a file's declarations write, by segment. They are expressions,
    // worth something only once the program's constants are, which is after the table is needed.
    private readonly Dictionary<string, SeparatedSyntaxList<SegmentAttributeSyntax>> attributes = new(StringComparer.Ordinal);

    private SegmentTable(Dictionary<string, Segment> segments) => this.segments = segments;

    /// <summary>Just the predeclared names: what a file compiled on its own starts from.</summary>
    public static SegmentTable Standard => new(Predeclared());

    /// <summary>The segments in the table, ordered by name.</summary>
    public IEnumerable<Segment> Segments => segments.Values.OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>The address spaces other than the host's, ordered by name.</summary>
    public IEnumerable<AddressSpace> Spaces => spaces.Values.OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>
    /// The table for a program, with the declarations found in <paramref name="trees"/>.
    /// Declarations are read in file and line order so that a program built from the same
    /// files reports the same thing whatever order they arrive in.
    /// </summary>
    public static SegmentTable Build(IEnumerable<SyntaxTree> trees, List<Diagnostic> diagnostics) =>
        Build(trees, [], [], Configuration.Everything, diagnostics);

    /// <summary>
    /// The table for a program whose project file declares some of its segments. Those
    /// are read first, so a file declaring one of them is the declaration that is reported.
    /// </summary>
    public static SegmentTable Build(
        IEnumerable<SyntaxTree> trees, IEnumerable<Segment> configured, IEnumerable<AddressSpace> configuredSpaces,
        Configuration configuration, List<Diagnostic> diagnostics)
    {
        var segments = Predeclared();
        var table = new SegmentTable(segments);
        var ordered = trees.ToList();

        // The spaces come first, since a segment names the one it is in. Only the project
        // declares them: a program that links another processor's image has a project.
        foreach (var space in configuredSpaces.OrderBy(space => space.Name, StringComparer.Ordinal))
            table.spaces[space.Name] = space;
        foreach (var written in configured.OrderBy(segment => segment.Name, StringComparer.Ordinal))
        {
            var segment = written;
            if (segments.TryGetValue(segment.Name, out var predeclared)
                && !Redeclares(predeclared, segment.Size, segment.Declaration!.Value, diagnostics))
            {
                continue;
            }
            if (segment.Space is { } named && !table.spaces.ContainsKey(named))
            {
                diagnostics.Add(new Diagnostic(segment.Declaration!.Value, Catalogue.SpaceUndeclared.Says(named)));
                segment = segment with { Space = null };
            }
            segments[segment.Name] = segment;
        }

        // A declaration under an `.if` the build does not take is not a declaration, which
        // is what lets two branches declare the same segment differently.
        var declarations = ordered
            .SelectMany(Declarations)
            .Where(d => configuration.Includes(d.Node))
            .OrderBy(d => d.Node.Tree.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start);
        foreach (var (node, name, span) in declarations)
        {
            var declared = node.Tree.GetSpan(span);
            var size = SizeOf(node);
            if (segments.TryGetValue(name, out var existing) && !Redeclares(existing, size, declared, diagnostics))
                continue;
            segments[name] = new Segment(name, size, declared) { Space = table.SpaceOf(name, node, diagnostics) };
            table.attributes[name] = node.Attributes;
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
        DiagnosticMessage? problem = null;
        if ((word == "dp" ? already.DirectPage : already.Bank) is not null)
            problem = Catalogue.SegmentAttributeTwice.Says(segment, word);
        else if (word == "dp" && size != AddressSize.ZeroPage)
            problem = Catalogue.SegmentDpNotZp.Says(segment);
        else if (value is null)
            problem = Catalogue.SegmentAttributeNotConstant.Says(word);
        else if (value < 0 || value > (word == "dp" ? 0xffff : 0xff))
        {
            problem = Catalogue.SegmentAttributeOutOfRange.Says(
                word == "dp" ? "the direct page is a 16-bit address" : "a bank is one byte");
        }
        if (problem is not { } said)
            return value;
        diagnostics.Add(new Diagnostic(at, said));
        return null;
    }

    /// <summary>What is said of a segment that declares mirrors and no home bank for them to mirror.</summary>
    public static DiagnosticMessage MirrorsNeedABank(string segment) =>
        Catalogue.SegmentMirrorsNeedABank.Says(segment);

    /// <summary>
    /// Works out the <c>dp = e</c>, <c>bank = e</c> and <c>mirrors = [...]</c> the files' declarations write, now that
    /// <paramref name="valueOf"/> can answer what an expression is worth.
    /// </summary>
    public void Evaluate(Func<ExpressionSyntax, long?> valueOf, List<Diagnostic> diagnostics)
    {
        foreach (var (name, written) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var segment = segments[name];
            SegmentAttributeSyntax? mirrors = null;
            foreach (var attribute in written)
            {
                if (attribute.Name.IsMissing)
                    continue;
                var word = attribute.Name.Text.ToLowerInvariant();
                if (word == "space")
                    continue;
                var at = attribute.Tree.GetSpan(attribute.Span);
                if (word == "mirrors")
                {
                    if (mirrors is not null)
                    {
                        diagnostics.Add(new Diagnostic(at, Catalogue.SegmentAttributeTwice.Says(name, "mirrors")));
                        continue;
                    }
                    mirrors = attribute;
                    segment = segment with { Mirrors = Mirrors(attribute, valueOf, diagnostics) };
                    continue;
                }
                if (attribute.Value is not { } expression)
                    continue;
                var value = valueOf(expression);
                if (Check(name, segment.Size, word, value, at, (segment.DirectPage, segment.Bank), diagnostics) is not { } valid)
                    continue;
                segment = word == "dp" ? segment with { DirectPage = valid } : segment with { Bank = valid };
            }
            if (mirrors is not null && segment.Bank is null)
            {
                diagnostics.Add(new Diagnostic(mirrors.Tree.GetSpan(mirrors.Span), MirrorsNeedABank(name)));
                segment = segment with { Mirrors = [] };
            }
            segments[name] = segment;
        }
    }

    /// <summary>The segment <paramref name="name"/>, or null when nothing declares it.</summary>
    public Segment? Find(string name) => segments.GetValueOrDefault(name);

    /// <summary>The space the segment <paramref name="name"/> is in, or null for the host's or for no segment.</summary>
    public AddressSpace? SpaceOf(string? name) =>
        name is not null && Find(name)?.Space is { } space ? spaces.GetValueOrDefault(space) : null;

    /// <summary>The space <paramref name="name"/>, or null when nothing declares it.</summary>
    public AddressSpace? FindSpace(string name) => spaces.GetValueOrDefault(name);

    /// <summary>Whether <paramref name="tree"/> declares a segment, taken or not by the build.</summary>
    internal static bool Declares(SyntaxTree tree) => Declarations(tree).Count > 0;

    /// <summary>
    /// The space a file's segment declaration puts it in with <c>space = name</c>, once and
    /// naming a declared space; null for the host's, with what is wrong reported.
    /// </summary>
    private string? SpaceOf(string segment, SegmentDeclarationSyntax node, List<Diagnostic> diagnostics)
    {
        string? found = null;
        var given = false;
        foreach (var attribute in node.Attributes)
        {
            if (attribute.Name.IsMissing || !attribute.Name.Text.Equals("space", StringComparison.OrdinalIgnoreCase))
                continue;
            var at = attribute.Tree.GetSpan(attribute.Span);
            if (given)
            {
                diagnostics.Add(new Diagnostic(at, Catalogue.SegmentAttributeTwice.Says(segment, "space")));
                continue;
            }
            given = true;
            if (attribute.Value is not NameExpressionSyntax { Names.Length: 1, SimpleName: { } name })
            {
                diagnostics.Add(new Diagnostic(at, Catalogue.SpaceNotAName));
                continue;
            }
            if (!spaces.ContainsKey(name.Text))
            {
                diagnostics.Add(new Diagnostic(attribute.Tree.GetSpan(name.Span), Catalogue.SpaceUndeclared.Says(name.Text)));
                continue;
            }
            found = name.Text;
        }
        return found;
    }

    /// <summary>The banks a <c>mirrors = [$00..$3f, $80]</c> gives, each checked to be a constant bank.</summary>
    private static List<(long First, long Last)> Mirrors(
        SegmentAttributeSyntax attribute, Func<ExpressionSyntax, long?> valueOf, List<Diagnostic> diagnostics)
    {
        var banks = new List<(long First, long Last)>();
        foreach (var range in attribute.Ranges)
        {
            var start = valueOf(range.First);
            var end = range.Last is { } written ? valueOf(written) : start;
            if (start is not { } first || end is not { } last || first is < 0 or > 0xff || last is < 0 or > 0xff
                || first > last)
            {
                diagnostics.Add(new Diagnostic(range.Tree.GetSpan(range.Span),
                    Catalogue.SegmentMirrorInvalid));
                continue;
            }
            banks.Add((first, last));
        }
        return banks;
    }

    /// <summary>
    /// Whether a declaration of a segment the table already holds takes its place: it does when
    /// what is there is the predeclaration of a standard name, at the same size, and is otherwise
    /// reported. A standard name is declared at most once, like any other.
    /// </summary>
    private static bool Redeclares(Segment existing, AddressSize size, Span declared, List<Diagnostic> diagnostics)
    {
        if (existing.Declaration is { } first)
        {
            diagnostics.Add(new Diagnostic(declared,
                Catalogue.SegmentDeclaredTwice.Says(existing.Name), [new RelatedSpan(first, "declared here")]));
            return false;
        }
        if (size == existing.Size)
            return true;
        diagnostics.Add(new Diagnostic(declared,
            Catalogue.SegmentStandardSize.Says(existing.Name, SpellSize(existing.Size))));
        return false;
    }

    /// <summary>An address size as a declaration writes it.</summary>
    private static string SpellSize(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    private static Dictionary<string, Segment> Predeclared() =>
        standard.ToDictionary(pair => pair.Key, pair => new Segment(pair.Key, pair.Value, null), StringComparer.Ordinal);

    private static List<Declaration> Declarations(SyntaxTree tree) =>
        written.GetValue(tree, tree => [.. Read(tree)]);

    private static IEnumerable<Declaration> Read(SyntaxTree tree)
    {
        foreach (var node in tree.Root.DescendantNodes().OfType<SegmentDeclarationSyntax>())
        {
            if (SegmentNames.Of(node.Name) is { } name)
                yield return new Declaration(node, name, node.Name.Span);
        }
    }

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c> a declaration writes after its <c>:</c>.</summary>
    private static AddressSize SizeOf(SegmentDeclarationSyntax declaration)
    {
        if (SegmentNames.ParseSize(declaration.AddressSize.Text) is { } size)
            return size;

        // The parser has already reported the missing size; absolute is the default that
        // makes the fewest further complaints.
        return AddressSize.Absolute;
    }

    /// <summary>One <c>.segment NAME: size</c> item, before it reaches the table.</summary>
    private readonly record struct Declaration(SegmentDeclarationSyntax Node, string Name, TextSpan Span);
}
