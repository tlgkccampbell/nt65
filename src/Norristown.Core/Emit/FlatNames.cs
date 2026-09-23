using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Emit;

/// <summary>
/// The name every symbol gets in the output. Names in the output are flat: the
/// output contains no ca65 <c>.proc</c>, <c>.scope</c> or cheap local labels, so it never
/// depends on how ca65 resolves names.
/// <para>
/// An export is written under its linker name: its path with its module's path in front,
/// <c>gfx__clear</c> for <c>clear</c> in module <c>gfx</c>, or the name its <c>as</c> gives. A
/// top-level name that is not exported keeps its spelling, and a scoped name
/// <c>outer::inner</c> becomes <c>outer__inner</c>. Those spellings are fixed, because other
/// modules and hand-written ca65 refer to them, so a collision between two of them is an error
/// rather than a reason to rename one. Everything else — cheap locals, and names inside an
/// anonymous scope — is derived from the source and made unique, as <c>draw__loop</c> and
/// <c>draw__loop_2</c>.
/// </para>
/// <para>
/// The one exception is a name ca65 would read as an instruction at the place the output
/// defines it: <c>swa</c> is an alias ca65 has on the 65816, and ca65 reads a line starting
/// <c>swa:</c> as that instruction. Such a name is written with its module in front,
/// <c>main__swa</c>, which is the spelling an export already has, and which contains a
/// <c>__</c> that no ca65 instruction name does.
/// </para>
/// <para>
/// A module that another module places is written into the placing module's output, beside
/// the other modules of its translation unit, so each name it does not export is written with
/// its module in front, <c>iscntc__loop</c>, and all the names in one output are handed out
/// from one table: two modules' private names never collide in the file they share.
/// </para>
/// <para>
/// What a macro body declares is local to each expansion, and what a repetition declares is
/// local to each iteration, so one such symbol becomes many names in the output, one per
/// expansion or iteration. Those names are handed out as the expansions are written, which is
/// as deterministic as the writing itself, and each is made unique against everything
/// already claimed.
/// </para>
/// </summary>
public sealed class FlatNames
{
    private readonly Dictionary<Symbol, string> names = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), string> perExpansion = [];
    private readonly Dictionary<string, Symbol?> taken;
    private IReadOnlyList<Family> families = [];

    // The words ca65 would read as instructions in the file being written, and the module path
    // written in front of a name it would misread. The module is null only for a file that
    // declares no module, which is an error, and a program with errors writes no output.
    private IReadOnlySet<string> instructions = new HashSet<string>();
    private string? module;

    // The prefix a placed module (one that another module places) writes in front of each
    // name it does not export, or the empty string for a module that has its own output.
    private string placedPrefix = "";

    private FlatNames(Dictionary<string, Symbol?> taken) => this.taken = taken;

    /// <summary>
    /// Assigns every symbol in <paramref name="model"/> its output name, for a program built
    /// for <paramref name="cpu"/>, whose instructions decide which names ca65 would misread.
    /// </summary>
    public static FlatNames Create(SemanticModel model, Cpu cpu, List<Diagnostic> diagnostics) =>
        Create(model, cpu, diagnostics, sharing: null, placed: false);

    /// <summary>
    /// The same for one module of a translation unit, whose names share one output, and so one
    /// table of taken names, with <paramref name="sharing"/>, or with no other module when it is null.
    /// <paramref name="placed"/> says the module is placed in another's output, and so writes
    /// each name it does not export with its module in front.
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
        // the name in the object file; local names claim their spellings after these. In an
        // output several modules share, a name another of them already uses is a collision.
        foreach (var symbol in model.ExternalSymbols)
        {
            if (sharing is not null && taken.TryGetValue(symbol.OutputName, out var other) && other is not null
                && other != symbol && other.QualifiedName != symbol.QualifiedName && other.Tree != symbol.Tree)
            {
                diagnostics.Add(new Diagnostic(other.DeclarationSpan,
                    Catalogue.OutputNameCollision.Says(other.QualifiedName, $"`{symbol.QualifiedName}`", symbol.OutputName)));
            }
            taken[symbol.OutputName] = symbol;
        }

        // The fixed spellings next, so that a generated name gives way to them and not the
        // other way round.
        foreach (var symbol in model.Symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var name = symbol.LinkerName ?? flat.Spelled(flat.placedPrefix + symbol.FlatName);
            if (taken.TryGetValue(name, out var other))
            {
                // Two declarations of the same name in the same scope are one problem, which
                // binding has already reported; only names that differ in the source but collide
                // in the output are reported here. A collision between two exports is reported
                // for the program as a whole, whichever modules they are in, so it is skipped here.
                if (other is not null && other.QualifiedName != symbol.QualifiedName
                    && (other.LinkerName is null || symbol.LinkerName is null))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan,
                        Catalogue.OutputNameCollision.Says(symbol.QualifiedName, $"`{other.QualifiedName}`", name),
                        [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
                }
                flat.names[symbol] = name;
                continue;
            }
            taken[name] = symbol;
            flat.names[symbol] = name;
        }

        // What a macro body or a repetition declares is not one name but one per expansion or
        // iteration, so those names are claimed as the expansions are written rather than here.
        foreach (var symbol in model.Symbols.Where(symbol =>
            !symbol.IsReachableByPath && !IsLocalToAnExpansion(symbol)))
        {
            var basis = flat.Spelled(flat.placedPrefix + symbol.FlatName);
            var name = basis;
            for (var n = 2; taken.ContainsKey(name); n++)
                name = $"{basis}_{n}";
            taken[name] = symbol;
            flat.names[symbol] = name;
        }
        return flat;
    }

    /// <summary>
    /// What <paramref name="symbol"/> is called in the output. A symbol another file
    /// declares keeps its own file's spelling, which is what the linker sees.
    /// </summary>
    public string Of(Symbol symbol) => names.GetValueOrDefault(symbol, symbol.OutputName);

    /// <summary>
    /// The same, for a symbol being written out at <paramref name="on"/>. A name a macro
    /// body declares is a different name at every expansion, so each gets one of its own,
    /// derived from the source and made unique: <c>times_x__loop</c>, then
    /// <c>times_x__loop_2</c>.
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

        var basis = Spelled(Basis(symbol, at.Item2));
        var name = basis;
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = symbol;
        perExpansion[at] = name;
        return name;
    }

    /// <summary>
    /// What a name written out at <paramref name="owning"/> is derived from. Inside a family's
    /// body it is the instance being written, so a cheap local of <c>play::triangle</c> is
    /// <c>play__triangle__loop</c> rather than one spelling shared by every instance.
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

    /// <summary>What already has <paramref name="name"/> in the output, or null when nothing has.</summary>
    public Symbol? Claimed(string name) => taken.GetValueOrDefault(name);

    /// <summary>
    /// Claims <paramref name="name"/> for a label the output needs and the source never
    /// wrote, such as the end of a routine.
    /// </summary>
    public void Claim(string name) => taken[name] = null;

    /// <summary>
    /// A name for something the output needs and the source never wrote, such as the label a
    /// long branch skips over. It is derived from the source like every other generated
    /// name, and made unique against everything already claimed.
    /// </summary>
    public string Generated(string basis)
    {
        var name = basis = Spelled(basis);
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = null;
        return name;
    }

    /// <summary>
    /// <paramref name="name"/> as the output can safely define it. ca65 reads a word from its
    /// instruction table at the start of a line as an instruction, whatever the rest of the file
    /// says that name is, so a name it would misread is written with its module in front instead
    /// — the spelling an export already has, and one containing a <c>__</c> that no ca65
    /// instruction name contains.
    /// </summary>
    private string Spelled(string name) => Prefixed(name, instructions, module) ?? name;

    /// <summary>
    /// The name the output gives <paramref name="name"/> in module <paramref name="module"/>
    /// because ca65 would read the spelling the source gave as an instruction, or null where
    /// the output keeps that spelling. What an editor shows about such a name comes from here,
    /// so that it always matches what the emitter actually does.
    /// </summary>
    /// <param name="name">The name as the source writes it.</param>
    /// <param name="cpu">The processor the program is built for, whose ca65 table decides.</param>
    /// <param name="module">The module the name is written in, or null for a file that names none.</param>
    public static string? Prefixed(string name, Cpu cpu, string? module) =>
        Prefixed(name, Ca65Instructions.Of(cpu), module);

    private static string? Prefixed(string name, IReadOnlySet<string> instructions, string? module) =>
        instructions.Contains(name) && module is { } own
            ? $"{own.Replace("::", "__", StringComparison.Ordinal)}__{name}"
            : null;

    /// <summary>
    /// Whether the symbol is declared in a macro body or a repetition, and so gets one name per
    /// expansion or iteration rather than a single name. Nothing outside the body can refer to
    /// it, which is why it can be renamed freely.
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
