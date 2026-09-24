using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Emit;

/// <summary>
/// Assigns the name every symbol gets in the output. Names in the output are flat. The output
/// contains no ca65 <c>.proc</c>, <c>.scope</c> or cheap local labels, so it never depends on
/// how ca65 resolves names.
/// <para>
/// An export is emitted under its linker name. That is its path with its module's path in
/// front, such as <c>gfx__clear</c> for <c>clear</c> in module <c>gfx</c>, or the name its
/// <c>as</c> gives. A top-level name that is not exported keeps its spelling, and a scoped name
/// <c>outer::inner</c> becomes <c>outer__inner</c>. Those spellings are fixed, because other
/// modules and hand-written ca65 refer to them, so a collision between two of them is an error
/// rather than a reason to rename one. Every other name, such as a cheap local or a name inside
/// an anonymous scope, is derived from the source and made unique, as <c>draw__loop</c> and
/// <c>draw__loop_2</c>.
/// </para>
/// <para>
/// The one exception is a name ca65 would read as an instruction where the output defines it.
/// For example, <c>swa</c> is an alias ca65 has on the 65816, and ca65 reads a line starting
/// <c>swa:</c> as that instruction. Such a name is emitted with its module in front, as
/// <c>main__swa</c>. That is the spelling an export already has, and it contains a <c>__</c>
/// that no ca65 instruction name does.
/// </para>
/// <para>
/// A module that another module places is emitted into the placing module's output, beside the
/// other modules of its translation unit. Each name it does not export is therefore emitted with
/// its module in front, as <c>iscntc__loop</c>. All the names in one output are handed out from
/// one table, so two modules' private names never collide in the file they share.
/// </para>
/// <para>
/// What a macro body declares is local to each expansion, and what a repetition declares is
/// local to each iteration, so one such symbol becomes many names in the output, one per
/// expansion or iteration. Those names are handed out as the expansions are emitted, which makes
/// them as deterministic as the emitting itself, and each is made unique against every name
/// already claimed.
/// </para>
/// </summary>
public sealed class FlatNames
{
    private readonly Dictionary<Symbol, string> names = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), string> perExpansion = [];
    private readonly Dictionary<string, Symbol?> taken;
    private IReadOnlyList<Family> families = [];

    // The words ca65 would read as instructions in the file being emitted, and the module path
    // emitted in front of a name it would misread. The module is null only for a file that
    // declares no module, which is an error, and a program with errors writes no output.
    private IReadOnlySet<string> instructions = new HashSet<string>();
    private string? module;

    // The prefix a placed module (one that another module places) emits in front of each name
    // it does not export, or the empty string for a module that has its own output.
    private string placedPrefix = "";

    private FlatNames(Dictionary<string, Symbol?> taken) => this.taken = taken;

    /// <summary>
    /// Assigns every symbol in <paramref name="model"/> its output name, for a program built
    /// for <paramref name="cpu"/>, whose instructions decide which names ca65 would misread.
    /// </summary>
    public static FlatNames Create(SemanticModel model, Cpu cpu, List<Diagnostic> diagnostics) =>
        Create(model, cpu, diagnostics, sharing: null, placed: false);

    /// <summary>
    /// Assigns every symbol in <paramref name="model"/>, one module of a translation unit, its
    /// output name. The module's names share one output, and so one table of taken names, with
    /// <paramref name="sharing"/>, or with no other module when <paramref name="sharing"/> is
    /// null. <paramref name="placed"/> indicates that the module is placed in another module's
    /// output, and so emits each name it does not export with its module in front.
    /// </summary>
    internal static FlatNames Create(
        SemanticModel model, Cpu cpu, List<Diagnostic> diagnostics, FlatNames? sharing, bool placed)
    {
        var flat = new FlatNames(sharing?.taken ?? new Dictionary<string, Symbol?>(StringComparer.Ordinal))
        {
            families = model.Families,
            instructions = Ca65Instructions.Of(cpu),
            module = model.FileScope.Module,
            placedPrefix = placed && model.FileScope.Module is { } path
                ? path.Replace("::", "__", StringComparison.Ordinal) + "__"
                : "",
        };
        var taken = flat.taken;

        // What another file exports keeps the spelling it was exported under, because that is
        // the name in the object file. Local names claim their spellings after these. In an
        // output that several modules share, a name another of them already uses is a collision.
        foreach (var symbol in model.ExternalSymbols)
        {
            if (sharing is not null && taken.TryGetValue(symbol.OutputName, out var other) && other is not null
                && other != symbol && other.QualifiedName != symbol.QualifiedName && other.Tree != symbol.Tree)
            {
                diagnostics.Add(new Diagnostic(other.DeclarationSpan,
                    Catalogue.OutputNameCollision.Message(other.QualifiedName, $"`{symbol.QualifiedName}`", symbol.OutputName)));
            }
            taken[symbol.OutputName] = symbol;
        }

        // The fixed spellings next, so that a generated name gives way to them and not the
        // other way round.
        foreach (var symbol in model.Symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var name = symbol.LinkerName ?? flat.Definable(flat.placedPrefix + symbol.FlatName);
            if (taken.TryGetValue(name, out var other))
            {
                // Two declarations of the same name in the same scope are one problem, which
                // binding has already reported. Only names that differ in the source but collide
                // in the output are reported here. A collision between two exports is reported
                // once for the program as a whole, so it is skipped here.
                if (other is not null && other.QualifiedName != symbol.QualifiedName
                    && (other.LinkerName is null || symbol.LinkerName is null))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan,
                        Catalogue.OutputNameCollision.Message(symbol.QualifiedName, $"`{other.QualifiedName}`", name),
                        [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
                }
                flat.names[symbol] = name;
                continue;
            }
            taken[name] = symbol;
            flat.names[symbol] = name;
        }

        // What a macro body or a repetition declares is not one name but one per expansion or
        // iteration, so those names are claimed as the expansions are emitted rather than here.
        foreach (var symbol in model.Symbols.Where(symbol =>
            !symbol.IsReachableByPath && !IsLocalToAnExpansion(symbol)))
        {
            var basis = flat.Definable(flat.placedPrefix + symbol.FlatName);
            var name = basis;
            for (var n = 2; taken.ContainsKey(name); n++)
                name = $"{basis}_{n}";
            taken[name] = symbol;
            flat.names[symbol] = name;
        }
        return flat;
    }

    /// <summary>
    /// Returns the name <paramref name="symbol"/> has in the output. A symbol that another file
    /// declares keeps its own file's spelling, which is what the linker sees.
    /// </summary>
    public string Of(Symbol symbol) => names.GetValueOrDefault(symbol, symbol.OutputName);

    /// <summary>
    /// Returns the name <paramref name="symbol"/> has in the output where it is emitted in the
    /// <see cref="Expansion"/> <paramref name="on"/>. A name that a macro body declares is a
    /// different name in every expansion, so each expansion gets a name of its own, derived from
    /// the source and made unique, such as <c>times_x__loop</c> and then <c>times_x__loop_2</c>.
    /// </summary>
    public string Of(Symbol symbol, Expansion? on)
    {
        if (on is null || !IsLocalToAnExpansion(symbol))
            return Of(symbol);

        var at = (symbol, Expansion.Owning(on, symbol));
        if (at.Item2 is null)
            return Of(symbol);
        if (perExpansion.TryGetValue(at, out var already))
            return already;

        var basis = Definable(Basis(symbol, at.Item2));
        var name = basis;
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = symbol;
        perExpansion[at] = name;
        return name;
    }

    /// <summary>
    /// Returns the basis from which a name emitted in <paramref name="owning"/> is derived. Inside
    /// the body of a <see cref="Family"/>, the basis is the instance being emitted, so a cheap
    /// local of <c>play::triangle</c> is <c>play__triangle__loop</c> rather than one spelling
    /// shared by every instance.
    /// </summary>
    private string Basis(Symbol symbol, Expansion owning)
    {
        foreach (var family in families)
        {
            if (family.Block == owning.Body && family.InstanceFor(owning.Member) is { } instance)
                return $"{Of(instance)}__{symbol.Name}";
        }
        return placedPrefix + symbol.FlatName;
    }

    /// <summary>
    /// Returns the symbol that already has <paramref name="name"/> in the output, or null when no
    /// symbol has it.
    /// </summary>
    public Symbol? Claimed(string name) => taken.GetValueOrDefault(name);

    /// <summary>
    /// Claims <paramref name="name"/> for a label the output needs and the source never
    /// declared, such as the end of a routine.
    /// </summary>
    public void Claim(string name) => taken[name] = null;

    /// <summary>
    /// Returns a name for something the output needs and the source never declared, such as the
    /// label a long branch skips over. It is derived from the source like every other generated
    /// name, and made unique against everything already claimed.
    /// </summary>
    public string Generated(string basis)
    {
        var name = basis = Definable(basis);
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = null;
        return name;
    }

    /// <summary>
    /// Returns <paramref name="name"/> in a form the output can safely define. ca65 reads a word
    /// from its instruction table at the start of a line as an instruction, regardless of what the
    /// rest of the file says that name is. A name it would misread is therefore emitted with its
    /// module in front instead. That is the spelling an export already has, and it contains a
    /// <c>__</c> that no ca65 instruction name contains.
    /// </summary>
    private string Definable(string name) => Prefixed(name, instructions, module) ?? name;

    /// <summary>
    /// Returns the name the output gives <paramref name="name"/> in module
    /// <paramref name="module"/> because ca65 would read the source's spelling as an instruction,
    /// or null where the output keeps that spelling. What an editor shows about such a name comes from here,
    /// so that it always matches what the emitter actually does.
    /// </summary>
    /// <param name="name">The name as it appears in the source.</param>
    /// <param name="cpu">The processor the program is built for, whose ca65 table decides.</param>
    /// <param name="module">The module the name is declared in, or null for a file that names none.</param>
    public static string? Prefixed(string name, Cpu cpu, string? module) =>
        Prefixed(name, Ca65Instructions.Of(cpu), module);

    private static string? Prefixed(string name, IReadOnlySet<string> instructions, string? module) =>
        instructions.Contains(name) && module is { } own
            ? $"{own.Replace("::", "__", StringComparison.Ordinal)}__{name}"
            : null;

    /// <summary>
    /// Returns whether the symbol is declared in a macro body or a repetition, and so gets one
    /// name per expansion or iteration rather than a single name. Nothing outside the body can
    /// refer to it, so it can be renamed freely.
    /// </summary>
    private static bool IsLocalToAnExpansion(Symbol symbol)
    {
        for (var scope = symbol.Scope; scope is not null; scope = scope.Parent)
        {
            if (scope.Kind is ScopeKind.Macro or ScopeKind.Repetition)
                return true;
        }
        return false;
    }
}
