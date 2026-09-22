using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which modules place which, and so which translation units the program is written as.
/// <para>
/// Everything here is read from the files alone: a <c>.place</c> stands at file level and never
/// under an <c>.if</c>, and whether a module may be placed is said in its declaration, so the
/// units follow from the text and from nothing a build decides. That also makes them cheap
/// enough to work out again after every edit, whichever file it was in.
/// </para>
/// </summary>
public sealed class Placements
{
    private readonly Dictionary<string, SyntaxTree> modules;
    private readonly Dictionary<string, ModulePlacement> declared;
    private readonly Dictionary<string, (SyntaxTree Placer, PlaceDirectiveSyntax At)> placedBy;
    private readonly Dictionary<PlaceDirectiveSyntax, SyntaxTree> placing;
    private readonly Dictionary<string, TranslationUnit> units;

    private Placements(
        Dictionary<string, SyntaxTree> modules, Dictionary<string, ModulePlacement> declared,
        Dictionary<string, (SyntaxTree, PlaceDirectiveSyntax)> placedBy, Dictionary<PlaceDirectiveSyntax, SyntaxTree> placing,
        Dictionary<string, TranslationUnit> units, IReadOnlyList<Diagnostic> diagnostics)
    {
        this.modules = modules;
        this.declared = declared;
        this.placedBy = placedBy;
        this.placing = placing;
        this.units = units;
        Diagnostics = diagnostics;
    }

    /// <summary>A program in which nothing places anything.</summary>
    public static Placements None { get; } = new([], [], [], [], [], []);

    /// <summary>What is wrong with the program's placements.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Works out the placements of <paramref name="trees"/>, leaving out
    /// <paramref name="defines"/>, which is no module anyone wrote.
    /// </summary>
    public static Placements Of(IEnumerable<SyntaxTree> trees, SyntaxTree? defines)
    {
        var files = trees.Where(tree => tree != defines).OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();
        var diagnostics = new List<Diagnostic>();
        var modules = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var declared = new Dictionary<string, ModulePlacement>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, ModuleDirectiveSyntax>(StringComparer.Ordinal);
        foreach (var tree in files)
        {
            if (Declaration(tree) is not { } module || PathOf(module.Name) is not { } name)
                continue;

            // Two files that are one module are reported where modules are; the first stands.
            modules.TryAdd(name, tree);
            declarations[tree.Path] = module;
            declared[tree.Path] = MarkerOf(module);
        }

        var placedBy = new Dictionary<string, (SyntaxTree, PlaceDirectiveSyntax)>(StringComparer.Ordinal);
        var placing = new Dictionary<PlaceDirectiveSyntax, SyntaxTree>();
        var children = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in files)
        {
            foreach (var place in tree.Root.DescendantNodes().OfType<PlaceDirectiveSyntax>())
            {
                if (PathOf(place.Name) is not { } path)
                    continue;
                if (!AtFileLevel(place))
                {
                    // It names the module all the same, which is not then placed nowhere as well.
                    diagnostics.Add(new Diagnostic(tree.GetSpan(place.Keyword.Span), Catalogue.PlaceMisplaced));
                    if (modules.TryGetValue(path, out var meant))
                        named.Add(meant.Path);
                    continue;
                }
                var at = tree.GetSpan(place.Name.Span);
                if (!modules.TryGetValue(path, out var target))
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.ModuleUnknown.Says(path)));
                    continue;
                }
                named.Add(target.Path);
                if (declared[target.Path] == ModulePlacement.Alone)
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.PlaceNotPlaceable.Says(path, path))
                    {
                        Fix = new DiagnosticFix(FixKind.Placed, "placed", target.GetSpan(declarations[target.Path].Name.Span)),
                    });
                    continue;
                }
                if (placedBy.TryGetValue(target.Path, out var already))
                {
                    diagnostics.Add(new Diagnostic(
                        at, Catalogue.PlacedTwice.Says(path, ModuleOf(already.Item1, declarations)),
                        [new RelatedSpan(already.Item1.GetSpan(already.Item2.Name.Span), "placed here")]));
                    continue;
                }
                if (Cycle(tree, target, placedBy, declarations) is { } cycle)
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.PlacementCycle.Says(cycle)));
                    continue;
                }
                placedBy[target.Path] = (tree, place);
                placing[place] = target;
                if (!children.TryGetValue(tree.Path, out var list))
                    children[tree.Path] = list = [];
                list.Add(target);
            }
        }

        // A module that must be placed and that no `.place` names is the mistake of forgetting
        // it. One that some `.place` names wrongly has been reported there already.
        foreach (var tree in files)
        {
            if (declared.GetValueOrDefault(tree.Path) == ModulePlacement.Placed && !named.Contains(tree.Path)
                && PathOf(declarations[tree.Path].Name) is { } name)
            {
                diagnostics.Add(new Diagnostic(
                    tree.GetSpan(declarations[tree.Path].Name.Span), Catalogue.PlacedNowhere.Says(name, name)));
            }
        }

        // Every file that nothing places is the root of a unit, which holds what it places in the
        // order it places them, each followed by what that one places in turn.
        var units = new Dictionary<string, TranslationUnit>(StringComparer.Ordinal);
        foreach (var root in files.Where(tree => !placedBy.ContainsKey(tree.Path)))
        {
            var members = new List<SyntaxTree>();
            Gather(root);
            var unit = new TranslationUnit(root, members);
            foreach (var member in members)
                units[member.Path] = unit;

            void Gather(SyntaxTree tree)
            {
                members.Add(tree);
                foreach (var child in children.GetValueOrDefault(tree.Path) ?? [])
                    Gather(child);
            }
        }
        return new Placements(modules, declared, placedBy, placing, units, diagnostics);
    }

    /// <summary>
    /// Whether <paramref name="statement"/> stands at file level: in no block but a
    /// <c>.segment</c> region, which is where file level is once one has been opened.
    /// </summary>
    public static bool AtFileLevel(StatementSyntax statement)
    {
        for (var at = statement.Parent?.FirstAncestorOrSelf<LineSyntax>()?.Parent; at is not null; at = at.Parent)
        {
            if (at is BlockSyntax { BlockKind: not BlockKind.Region })
                return false;
        }
        return true;
    }

    /// <summary>The <c>.module</c> line of <paramref name="tree"/>, or null when it has none.</summary>
    public static ModuleDirectiveSyntax? Declaration(SyntaxTree tree)
    {
        foreach (var child in tree.Root.Members)
        {
            if (child is LineSyntax { Statement: ModuleDirectiveSyntax module })
                return module;
            if (child is not LineSyntax { Statement: BlankLineSyntax })
                return null;
        }
        return null;
    }

    /// <summary>What a declaration says about placing its module.</summary>
    public static ModulePlacement MarkerOf(ModuleDirectiveSyntax module) =>
        module.Placement is not { IsMissing: false } word ? ModulePlacement.Alone
        : word.Text.Equals("placed", StringComparison.OrdinalIgnoreCase) ? ModulePlacement.Placed
        : ModulePlacement.Placeable;

    /// <summary>A module path as written, or null where a part of it is missing.</summary>
    public static string? PathOf(NameExpressionSyntax name) =>
        name.Names.Length == 0 || name.Names.Any(part => part.IsMissing)
            ? null
            : string.Join("::", name.Names.Select(part => part.Text));

    /// <summary>What <paramref name="tree"/>'s declaration says about placing it.</summary>
    public ModulePlacement DeclaredFor(SyntaxTree tree) => declared.GetValueOrDefault(tree.Path);

    /// <summary>The unit <paramref name="tree"/> is written in, or null for a file this program does not have.</summary>
    public TranslationUnit? UnitOf(SyntaxTree tree) => units.GetValueOrDefault(tree.Path);

    /// <summary>The file and the <c>.place</c> that place <paramref name="tree"/>, or null when nothing does.</summary>
    public (SyntaxTree Placer, PlaceDirectiveSyntax At)? PlacerOf(SyntaxTree tree) =>
        placedBy.TryGetValue(tree.Path, out var found) ? found : null;

    /// <summary>The file a <c>.place</c> places, or null when it places nothing because it is wrong.</summary>
    public SyntaxTree? Placed(PlaceDirectiveSyntax place) => placing.GetValueOrDefault(place);

    /// <summary>The file of module <paramref name="path"/>, or null when the program has none.</summary>
    public SyntaxTree? ModuleNamed(string path) => modules.GetValueOrDefault(path);

    /// <summary>The module a file is, as its declaration writes it.</summary>
    private static string ModuleOf(SyntaxTree tree, Dictionary<string, ModuleDirectiveSyntax> declarations) =>
        declarations.TryGetValue(tree.Path, out var module) && PathOf(module.Name) is { } name ? name : tree.Path;

    /// <summary>
    /// The cycle that <paramref name="placer"/> placing <paramref name="target"/> would close,
    /// spelled out from the target, or null when it closes none. It closes one when the target
    /// already places, however indirectly, the module that would place it.
    /// </summary>
    private static string? Cycle(
        SyntaxTree placer, SyntaxTree target, Dictionary<string, (SyntaxTree Placer, PlaceDirectiveSyntax At)> placedBy,
        Dictionary<string, ModuleDirectiveSyntax> declarations)
    {
        var chain = new List<SyntaxTree> { placer };
        for (var at = placer; at.Path != target.Path;)
        {
            if (!placedBy.TryGetValue(at.Path, out var above))
                return null;
            at = above.Placer;
            chain.Add(at);
        }
        chain.Reverse();
        var names = chain.Select(tree => $"`{ModuleOf(tree, declarations)}`").ToList();
        return names.Count == 1
            ? $"{names[0]} places {names[0]}"
            : $"{names[0]} places {string.Join(", which places ", names.Skip(1))}, which places {names[0]}";
    }
}
