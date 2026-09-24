using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Records which modules place which, and therefore which translation units make up the
/// program.
/// <para>
/// Everything here is read from the files alone. A <c>.place</c> must be at file level and never
/// under an <c>.if</c>, and a module's declaration states whether it may be placed, so the units
/// follow from the text and from nothing a build decides. That also makes them cheap enough to
/// recompute after every edit, in any file.
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

    /// <summary>Gets the placements of a program in which no module places another.</summary>
    public static Placements None { get; } = new([], [], [], [], [], []);

    /// <summary>Gets the diagnostics for the program's placements.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Computes the placements of <paramref name="trees"/>.</summary>
    public static Placements Of(IEnumerable<SyntaxTree> trees)
    {
        var files = trees.OrderBy(tree => tree.Path, StringComparer.Ordinal).ToList();
        var diagnostics = new List<Diagnostic>();
        var modules = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var declared = new Dictionary<string, ModulePlacement>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, ModuleDirectiveSyntax>(StringComparer.Ordinal);
        foreach (var tree in files)
        {
            if (Declaration(tree) is not { } module || PathOf(module.Name) is not { } name)
                continue;

            // Two files that declare the same module are reported where modules are checked.
            // Here the first one is kept.
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
                if (!IsWellPlaced(place))
                {
                    // The misplaced `.place` still names the module, so the module is not also
                    // reported as placed nowhere.
                    diagnostics.Add(new Diagnostic(tree.GetSpan(place.Keyword.Span), Catalogue.PlaceMisplaced));
                    if (modules.TryGetValue(path, out var meant))
                        named.Add(meant.Path);
                    continue;
                }
                var at = tree.GetSpan(place.Name.Span);
                if (!modules.TryGetValue(path, out var target))
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.ModuleUnknown.Message(path)));
                    continue;
                }
                named.Add(target.Path);
                if (declared[target.Path] == ModulePlacement.Alone)
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.PlaceNotPlaceable.Message(path, path))
                    {
                        Fix = new DiagnosticFix(FixKind.Placed, "placed", target.GetSpan(declarations[target.Path].Name.Span)),
                    });
                    continue;
                }
                if (placedBy.TryGetValue(target.Path, out var already))
                {
                    diagnostics.Add(new Diagnostic(
                        at, Catalogue.PlacedTwice.Message(path, ModuleOf(already.Item1, declarations)),
                        [new RelatedSpan(already.Item1.GetSpan(already.Item2.Name.Span), "placed here")]));
                    continue;
                }
                if (Cycle(tree, target, placedBy, declarations) is { } cycle)
                {
                    diagnostics.Add(new Diagnostic(at, Catalogue.PlacementCycle.Message(cycle)));
                    continue;
                }
                placedBy[target.Path] = (tree, place);
                placing[place] = target;
                if (!children.TryGetValue(tree.Path, out var list))
                    children[tree.Path] = list = [];
                list.Add(target);
            }
        }

        // A module that must be placed but that no `.place` names means the `.place` was
        // forgotten. A module that some `.place` names wrongly has already been reported there.
        foreach (var tree in files)
        {
            if (declared.GetValueOrDefault(tree.Path) == ModulePlacement.Placed && !named.Contains(tree.Path)
                && PathOf(declarations[tree.Path].Name) is { } name)
            {
                diagnostics.Add(new Diagnostic(
                    tree.GetSpan(declarations[tree.Path].Name.Span), Catalogue.PlacedNowhere.Message(name, name)));
            }
        }

        // Every file that no module places is the root of a unit. The unit holds the modules the
        // root places, in order, each followed by the modules it places in turn.
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
    /// Returns a value indicating whether <paramref name="place"/> is where its placement allows
    /// it, which is at file level, in no block other than a <c>.segment</c> region. Once a region
    /// has been opened, file level is inside it.
    /// </summary>
    public static bool IsWellPlaced(PlaceDirectiveSyntax place) =>
        !SyntaxFacts.PlacementOf(DirectiveKind.Place).IsBarredBy(SyntaxFacts.NestingOf(place));

    /// <summary>
    /// Returns the <c>.module</c> line of <paramref name="tree"/>, or null when it has none.
    /// </summary>
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

    /// <summary>Returns what <paramref name="module"/> declares about placing its module.</summary>
    public static ModulePlacement MarkerOf(ModuleDirectiveSyntax module) =>
        module.Placement is not { IsMissing: false } word ? ModulePlacement.Alone
        : word.Text.Equals("placed", StringComparison.OrdinalIgnoreCase) ? ModulePlacement.Placed
        : ModulePlacement.Placeable;

    /// <summary>
    /// Returns a module path as it appears in the source, or null when a part of it is missing.
    /// </summary>
    public static string? PathOf(NameExpressionSyntax name) =>
        name.Names.Length == 0 || name.Names.Any(part => part.IsMissing)
            ? null
            : string.Join("::", name.Names.Select(part => part.Text));

    /// <summary>Returns what <paramref name="tree"/>'s declaration says about placing it.</summary>
    public ModulePlacement DeclaredFor(SyntaxTree tree) => declared.GetValueOrDefault(tree.Path);

    /// <summary>
    /// Returns the unit <paramref name="tree"/> belongs to, or null for a file this program does
    /// not have.
    /// </summary>
    public TranslationUnit? UnitOf(SyntaxTree tree) => units.GetValueOrDefault(tree.Path);

    /// <summary>
    /// Returns the file and the <c>.place</c> that place <paramref name="tree"/>, or null when no
    /// module places it.
    /// </summary>
    public (SyntaxTree Placer, PlaceDirectiveSyntax At)? PlacerOf(SyntaxTree tree) =>
        placedBy.TryGetValue(tree.Path, out var found) ? found : null;

    /// <summary>
    /// Returns the file <paramref name="place"/> places, or null when the directive is in error
    /// and places nothing.
    /// </summary>
    public SyntaxTree? Placed(PlaceDirectiveSyntax place) => placing.GetValueOrDefault(place);

    /// <summary>
    /// Returns the file of the module <paramref name="path"/>, or null when the program has no
    /// such module.
    /// </summary>
    public SyntaxTree? ModuleNamed(string path) => modules.GetValueOrDefault(path);

    /// <summary>
    /// Returns the module name of <paramref name="tree"/> as its declaration gives it, or the
    /// file's path when it has none.
    /// </summary>
    private static string ModuleOf(SyntaxTree tree, Dictionary<string, ModuleDirectiveSyntax> declarations) =>
        declarations.TryGetValue(tree.Path, out var module) && PathOf(module.Name) is { } name ? name : tree.Path;

    /// <summary>
    /// Returns a description, starting from the target, of the cycle that
    /// <paramref name="placer"/> placing <paramref name="target"/> would close, or null when it
    /// closes none. It closes a cycle when the target already places, directly or indirectly,
    /// the module that would place it.
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
