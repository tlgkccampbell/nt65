using System.Runtime.CompilerServices;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Builds the table of names each output defines, and finds the names that collide in it. The
/// analysis reports the collisions, so that an editor shows them as a build would, and emitting
/// builds the same tables again to write from.
/// </summary>
internal static class OutputNames
{
    // What each file measures with `.endof` and `.spanof`, which depends only on the file's model.
    private static readonly ConditionalWeakTable<SemanticModel, IReadOnlySet<Symbol>> measuredIn = new();

    // What was found for each output, by the model of its first module. An edit leaves most
    // outputs as they were, and one whose modules and measured symbols are unchanged is not
    // checked again.
    private static readonly ConditionalWeakTable<SemanticModel, Checked> found = new();

    /// <summary>
    /// Returns the names that collide in the program's outputs. Each output is a file with an
    /// output of its own or a translation unit of the modules one module places.
    /// </summary>
    public static List<Diagnostic> Collisions(ProgramAnalysis analysis, Placements placements)
    {
        var diagnostics = new List<Diagnostic>();
        var elsewhere = MeasuredElsewhere(analysis);
        foreach (var file in analysis.Files)
        {
            var model = file.Model;
            if (StandardModules.IsStandard(model.Tree.Path) || placements.PlacerOf(model.Tree) is not null)
                continue;
            SemanticModel[] members = placements.UnitOf(model.Tree) is { IsPlaced: true } unit
                ? [.. unit.Members.Select(tree => analysis.FileFor(tree.Path)?.Model).OfType<SemanticModel>()]
                : [model];
            IReadOnlySet<Symbol>[] measured = [.. members.Select(member => Of(elsewhere, member.Tree))];
            if (found.TryGetValue(model, out var before) && before.Matches(analysis.Cpu, members, measured))
            {
                diagnostics.AddRange(before.Diagnostics);
                continue;
            }
            var here = new List<Diagnostic>();
            foreach (var (member, _, names, measuredHere) in Tables(analysis, placements, file, elsewhere, here))
                Claim(member, names, measuredHere, new HashSet<Symbol>(), here);
            found.AddOrUpdate(model, new Checked(analysis.Cpu, members, measured, here));
            diagnostics.AddRange(here);
        }
        return diagnostics;
    }

    /// <summary>
    /// Returns, for each file of the program, the symbols it declares that other files measure
    /// with <c>.endof</c> and <c>.spanof</c>. Those are the ones its output has to label.
    /// </summary>
    public static Dictionary<SyntaxTree, IReadOnlySet<Symbol>> MeasuredElsewhere(ProgramAnalysis analysis)
    {
        var elsewhere = new Dictionary<SyntaxTree, HashSet<Symbol>>();
        foreach (var file in analysis.Files)
        {
            foreach (var symbol in measuredIn.GetValue(file.Model, Extents.MeasuredIn))
            {
                if (symbol.Tree == file.Model.Tree)
                    continue;
                if (!elsewhere.TryGetValue(symbol.Tree, out var set))
                    elsewhere[symbol.Tree] = set = [];
                set.Add(symbol);
            }
        }
        return elsewhere.ToDictionary(pair => pair.Key, IReadOnlySet<Symbol> (pair) => pair.Value);
    }

    /// <summary>
    /// Returns the name table of each module written into <paramref name="file"/>'s output, with
    /// the module's model and layout and the symbols of it that other files measure. That is the
    /// file alone, or every module of its translation unit, root first. The modules of a unit
    /// share one output, and so one table, in which the root claims its names first. Names that
    /// collide are reported to <paramref name="diagnostics"/>.
    /// </summary>
    public static List<(SemanticModel Model, CodeLayout Layout, FlatNames Names, IReadOnlySet<Symbol> MeasuredElsewhere)> Tables(
        ProgramAnalysis analysis, Placements placements, FileAnalysis file,
        IReadOnlyDictionary<SyntaxTree, IReadOnlySet<Symbol>> elsewhere, List<Diagnostic> diagnostics)
    {
        var model = file.Model;
        if (placements.UnitOf(model.Tree) is not { IsPlaced: true } unit)
            return [(model, file.Layout, FlatNames.Create(model, analysis.Cpu, diagnostics), Of(elsewhere, model.Tree))];

        var members = new List<(SemanticModel, CodeLayout, FlatNames, IReadOnlySet<Symbol>)>();
        FlatNames? names = null;
        foreach (var tree in unit.Members)
        {
            if (analysis.FileFor(tree.Path) is not { } member)
                continue;
            names = FlatNames.Create(member.Model, analysis.Cpu, diagnostics, names, placed: members.Count > 0);
            members.Add((member.Model, member.Layout, names, Of(elsewhere, member.Model.Tree)));
        }
        return members;
    }

    /// <summary>
    /// Adds to <paramref name="ends"/> the routines and data declarations of
    /// <paramref name="model"/>'s file that are measured, and claims their end labels and the
    /// size constants of the types it exports. The end of <c>f</c> is written <c>f__end</c>. That
    /// spelling is fixed, because other files and hand-written ca65 refer to it once it is
    /// exported, so a name that collides with it is reported rather than renamed.
    /// </summary>
    public static void Claim(
        SemanticModel model, FlatNames names, IReadOnlySet<Symbol> measuredElsewhere, ISet<Symbol> ends,
        List<Diagnostic> diagnostics)
    {
        var own = Extents.MeasuredIn(model).Concat(measuredElsewhere)
            .Where(symbol => symbol.Tree == model.Tree)
            .Distinct()
            .OrderBy(s => s.NameSpan.Start);
        foreach (var measured in own)
        {
            ends.Add(measured);
            var end = EndLabelOf(names, measured);
            if (names.Claimed(end) is { } other)
            {
                diagnostics.Add(new Diagnostic(measured.DeclarationSpan,
                    Catalogue.OutputNameCollision.Message(other.QualifiedName, $"the end of `{measured.QualifiedName}`", end),
                    [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(end);
        }

        foreach (var type in model.Symbols.Where(symbol => symbol.Tree == model.Tree && IsSized(symbol)))
        {
            var size = SizeConstantOf(names, type);
            if (names.Claimed(size) is { } other)
            {
                diagnostics.Add(new Diagnostic(type.DeclarationSpan,
                    Catalogue.OutputNameCollision.Message(other.QualifiedName, $"the size of `{type.QualifiedName}`", size),
                    [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(size);
        }
    }

    /// <summary>Returns the label just past a symbol's last byte, which is what <c>.endof</c> stands for.</summary>
    public static string EndLabelOf(FlatNames names, Symbol symbol) => names.Of(symbol) + "__end";

    /// <summary>
    /// Returns the name of the constant that an exported struct or union's size is exported as,
    /// so that ca65 and C code can size what they allocate by it. Like an end label, its
    /// spelling is fixed.
    /// </summary>
    public static string SizeConstantOf(FlatNames names, Symbol type) => names.Of(type) + "__sizeof";

    /// <summary>Returns whether a type's size is exported as a constant of its own.</summary>
    public static bool IsSized(Symbol symbol) => symbol is { IsExported: true, IsLayout: true, Size: not null };

    /// <summary>Returns what other files measure of <paramref name="tree"/>'s symbols.</summary>
    private static IReadOnlySet<Symbol> Of(IReadOnlyDictionary<SyntaxTree, IReadOnlySet<Symbol>> elsewhere, SyntaxTree tree) =>
        elsewhere.TryGetValue(tree, out var symbols) ? symbols : new HashSet<Symbol>();

    /// <summary>Represents what was found for one output, with what it was found from.</summary>
    /// <param name="Cpu">The processor the program was built for, which decides the names ca65 misreads.</param>
    /// <param name="Members">The models of the output's modules, root first.</param>
    /// <param name="Measured">What other files measured of each module.</param>
    /// <param name="Diagnostics">The names that collided.</param>
    private sealed record Checked(
        Processor.Cpu Cpu, SemanticModel[] Members, IReadOnlySet<Symbol>[] Measured, List<Diagnostic> Diagnostics)
    {
        /// <summary>Determines whether this was found from the same modules and measured symbols.</summary>
        public bool Matches(Processor.Cpu cpu, SemanticModel[] members, IReadOnlySet<Symbol>[] measured) =>
            cpu == Cpu && members.AsSpan().SequenceEqual(Members)
            && measured.Length == Measured.Length && measured.Zip(Measured).All(pair => pair.First.SetEquals(pair.Second));
    }
}
