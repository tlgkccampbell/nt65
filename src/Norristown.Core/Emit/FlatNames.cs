using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Emit;

/// <summary>
/// The name every symbol gets in the output. Names in the output are flat: the
/// output contains no ca65 <c>.proc</c>, <c>.scope</c> or cheap local labels, so it never
/// depends on how ca65 resolves names.
/// <para>
/// An export is its linker name: its path with its module's in front, <c>gfx__clear</c> for
/// <c>clear</c> in module <c>gfx</c>, or the name its <c>as</c> gives. A top-level name that is
/// not exported keeps its spelling, and a scoped name <c>outer::inner</c> becomes
/// <c>outer__inner</c>. Those spellings are fixed, because other modules and hand-written
/// ca65 refer to them, so two of them that collide is an error rather than a
/// rename. Everything else — cheap locals, and names inside an anonymous scope — is derived
/// from the source and made unique, as <c>draw__loop</c> and <c>draw__loop_2</c>.
/// </para>
/// <para>
/// The one name that does not keep its spelling is one ca65 would read as an instruction
/// where the output defines it: <c>swa</c> is an alias ca65 has on the 65816, and a line
/// starting <c>swa:</c> is an instruction to it. Such a name is written with its module in
/// front, <c>main__swa</c>, which is the spelling an export already has, and which holds a
/// <c>__</c> that no word of ca65's does.
/// </para>
/// <para>
/// What a macro body declares is local to each expansion, and what a repetition declares to
/// each turn, so one symbol there is many names in the output, one per writing. Those are handed out as the expansions are written, which is
/// as deterministic as the writing itself, and each is made unique against everything
/// already claimed.
/// </para>
/// </summary>
public sealed class FlatNames
{
    private IReadOnlyList<Family> families = [];
    private readonly Dictionary<Symbol, string> names = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), string> perExpansion = [];
    private readonly Dictionary<string, Symbol?> taken = new(StringComparer.Ordinal);

    // What ca65 would read as an instruction in the file being written, and what a name it
    // would misread is written with in front. The module is null only for a file that names
    // none, which is an error, and a wrong program writes no output.
    private IReadOnlySet<string> instructions = new HashSet<string>();
    private string? module;

    private FlatNames() { }

    /// <summary>
    /// Assigns every symbol in <paramref name="model"/> its output name, for a program built
    /// for <paramref name="cpu"/>, whose instructions decide which names ca65 would misread.
    /// </summary>
    public static FlatNames Create(SemanticModel model, Cpu cpu, List<Diagnostic> diagnostics)
    {
        var flat = new FlatNames
        {
            families = model.Families,
            instructions = Ca65Instructions.Of(cpu),
            module = model.FileScope.Module,
        };
        var taken = flat.taken;

        // What another file exports keeps the spelling it was exported under, because that is
        // the name in the object file; a local name claims its spelling after them.
        foreach (var symbol in model.ExternalSymbols)
            taken[symbol.OutputName] = symbol;

        // The fixed spellings next, so that a generated name gives way to them and not the
        // other way round.
        foreach (var symbol in model.Symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var name = symbol.LinkerName ?? flat.Spelled(symbol.FlatName);
            if (taken.TryGetValue(name, out var other))
            {
                // Two declarations of the same name in the same scope are one problem, which
                // binding has already reported; only names that differ in the source and meet
                // in the output are news.
                // Two exports that meet are the program's to report, whichever modules they are in.
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

        // What a macro body or a repetition declares is not one name but one per writing, so
        // those are claimed as the expansions and turns are written rather than here.
        foreach (var symbol in model.Symbols.Where(symbol =>
            !symbol.IsReachableByPath && !IsLocalToAnExpansion(symbol)))
        {
            var basis = flat.Spelled(symbol.FlatName);
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
        return symbol.FlatName;
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
    /// A name as the output may define it. ca65 reads a word of its own instruction table at
    /// the start of a line as an instruction, whatever the rest of the file says that name is,
    /// so a name it would misread is written with its module in front instead — the spelling
    /// an export already has, and one holding a <c>__</c> that no word of ca65's holds.
    /// </summary>
    private string Spelled(string name) => Prefixed(name, instructions, module) ?? name;

    /// <summary>
    /// The name the output gives <paramref name="name"/> in module <paramref name="module"/>
    /// because ca65 would read the spelling the source gave as an instruction, or null where
    /// the output keeps that spelling. What an editor shows about such a name comes from here,
    /// so that it says what the emitter does and not what it once did.
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
    /// Whether the symbol is one a macro body or a repetition declares, and so one name per
    /// expansion or turn rather than one name. Nothing outside a body can reach it, which is
    /// why it can be renamed.
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
