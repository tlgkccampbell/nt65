using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents the program's segments. Every segment is declared exactly once, by a
/// <c>.segment NAME: size</c> item in one file or in <c>nt65.json</c>. The standard names are
/// predeclared, and the predeclaration applies when the program does not declare them. A
/// program may therefore declare one of them once, at its standard size, to give it a direct
/// page, a bank or mirrors. A segment block that names an undeclared segment is an error, so a
/// misspelled name is caught before ld65 runs.
/// </summary>
public sealed class SegmentTable
{
    // A file's segment declarations, read once per tree. After an edit, only one file of the
    // program has a tree that has not been read yet.
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

    // The `dp = e` and `bank = e` attributes in the files' declarations, by segment. They are
    // expressions that have values only once the program's constants do, which is after the
    // table is needed.
    private readonly Dictionary<string, SeparatedSyntaxList<SegmentAttributeSyntax>> attributes = new(StringComparer.Ordinal);

    private SegmentTable(Dictionary<string, Segment> segments) => this.segments = segments;

    /// <summary>
    /// Gets a table holding only the predeclared names, which a file compiled on its own starts
    /// from.
    /// </summary>
    public static SegmentTable Standard => new(Predeclared());

    /// <summary>Gets the segments in the table, ordered by name.</summary>
    public IEnumerable<Segment> Segments => segments.Values.OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>Gets the address spaces other than the host's, ordered by name.</summary>
    public IEnumerable<AddressSpace> Spaces => spaces.Values.OrderBy(s => s.Name, StringComparer.Ordinal);

    /// <summary>
    /// Builds the table for a program from the declarations found in <paramref name="trees"/>.
    /// Declarations are read in file and line order, so that a program built from the same files
    /// reports the same diagnostics in whatever order the files arrive.
    /// </summary>
    public static SegmentTable Build(IEnumerable<SyntaxTree> trees, List<Diagnostic> diagnostics) =>
        Build(trees, [], [], Configuration.Everything, diagnostics);

    /// <summary>
    /// Builds the table for a program whose project file declares some of its segments. Those
    /// are read first, so when a file also declares one of them, the file's declaration is the one
    /// reported as the duplicate.
    /// </summary>
    public static SegmentTable Build(
        IEnumerable<SyntaxTree> trees, IEnumerable<Segment> configured, IEnumerable<AddressSpace> configuredSpaces,
        Configuration configuration, List<Diagnostic> diagnostics)
    {
        var segments = Predeclared();
        var table = new SegmentTable(segments);
        var ordered = trees.ToList();

        // The spaces come first, because a segment names the space it is in. Only the project
        // declares spaces, because a program that links another processor's image has a project.
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

        // A declaration under an `.if` branch the build does not take is not a declaration, so
        // two branches may declare the same segment differently.
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
    /// Returns the value of a segment's <c>dp</c> or <c>bank</c>, or null after reporting the
    /// reason. The value must be a constant in range and given once. A <c>dp</c> is allowed only
    /// on a <c>zp</c> segment, the only kind whose symbols are reached through the direct page.
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
                word == "dp" ? "`dp` must be $0000 to $ffff: the direct page is a 16-bit address" : "`bank` must be $00 to $ff: a bank is one byte");
        }
        if (problem is not { } said)
            return value;
        diagnostics.Add(new Diagnostic(at, said));
        return null;
    }

    /// <summary>
    /// Returns the diagnostic for a segment that declares mirrors but no home bank for them to
    /// mirror.
    /// </summary>
    public static DiagnosticMessage MirrorsNeedABank(string segment) =>
        Catalogue.SegmentMirrorsNeedABank.Says(segment);

    /// <summary>
    /// Evaluates the <c>dp = e</c>, <c>bank = e</c> and <c>mirrors = [...]</c> attributes in the
    /// files' declarations, now that <paramref name="valueOf"/> can evaluate expressions.
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

    /// <summary>Returns the segment <paramref name="name"/>, or null when nothing declares it.</summary>
    public Segment? Find(string name) => segments.GetValueOrDefault(name);

    /// <summary>
    /// Returns the space the segment <paramref name="name"/> is in, or null for the host's space
    /// or when there is no such segment.
    /// </summary>
    public AddressSpace? SpaceOf(string? name) =>
        name is not null && Find(name)?.Space is { } space ? spaces.GetValueOrDefault(space) : null;

    /// <summary>Returns the space <paramref name="name"/>, or null when nothing declares it.</summary>
    public AddressSpace? FindSpace(string name) => spaces.GetValueOrDefault(name);

    /// <summary>
    /// Returns a value indicating whether <paramref name="tree"/> declares a segment, whether or
    /// not the build takes the declaration.
    /// </summary>
    internal static bool Declares(SyntaxTree tree) => Declarations(tree).Count > 0;

    /// <summary>
    /// Returns the space a file's segment declaration puts the segment in with
    /// <c>space = name</c>, which must be given once and name a declared space. Returns null for
    /// the host's space, after reporting any problem.
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

    /// <summary>
    /// Returns the bank ranges a <c>mirrors = [$00..$3f, $80]</c> gives, after checking that each
    /// is a constant bank.
    /// </summary>
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
    /// Returns a value indicating whether a declaration of a segment the table already holds
    /// replaces it. It does when the existing entry is the predeclaration of a standard name at
    /// the same size; otherwise the declaration is reported. A standard name is declared at most
    /// once, like any other.
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

    /// <summary>Formats an address size as a declaration gives it.</summary>
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

    /// <summary>
    /// Returns the address size, <c>zp</c>, <c>abs</c> or <c>far</c>, that a declaration gives
    /// after its <c>:</c>.
    /// </summary>
    private static AddressSize SizeOf(SegmentDeclarationSyntax declaration)
    {
        if (SegmentNames.ParseSize(declaration.AddressSize.Text) is { } size)
            return size;

        // The parser has already reported the missing size. Absolute is the default that
        // produces the fewest further diagnostics.
        return AddressSize.Absolute;
    }

    /// <summary>Represents one <c>.segment NAME: size</c> item before it reaches the table.</summary>
    private readonly record struct Declaration(SegmentDeclarationSyntax Node, string Name, TextSpan Span);
}
