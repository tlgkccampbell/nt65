using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What the modules of a program can see of one another. Every file is a module with a name,
/// and its symbols are private unless exported. A name from another module is written with
/// the module's path, <c>hw::vic::border</c>, or brought in with <c>.use</c>; nothing from
/// another module is ever visible without one of those.
/// <para>
/// A module's name is only a name: <c>gfx::sprite</c> needs no module <c>gfx</c>, and the
/// modules whose names start the same way are no closer to one another than any others. What
/// the path does make is a tree of names, so <c>gfx</c> alone is a prefix a path may walk
/// through on its way to a module.
/// </para>
/// <para>
/// Lookup finds a module's private names too, because a better diagnostic for one is that
/// it exists and is not exported. Whether it may actually be used is asked of the whole name
/// once it resolves: an interior label reached as <c>hw::outer::inner</c> is exported by
/// <c>inner</c>, and <c>outer</c> itself need not be.
/// </para>
/// </summary>
public sealed class ProgramSymbols
{
    private readonly Dictionary<string, Module> modules;
    private readonly HashSet<string> prefixes;
    private readonly Dictionary<string, Symbol> defines;

    private ProgramSymbols(Dictionary<string, Module> modules, HashSet<string> prefixes, Dictionary<string, Symbol> defines)
    {
        this.modules = modules;
        this.prefixes = prefixes;
        this.defines = defines;
    }

    /// <summary>A program of one file, which can see nothing beyond itself.</summary>
    public static ProgramSymbols Empty { get; } = new([], [], []);

    /// <summary>
    /// The table for a program whose files have been collected but not yet resolved. What is
    /// wrong with the modules rather than with one file is reported here: two files that are
    /// the same module, a name that is also the path of a module, and two exports that meet
    /// under one linker name.
    /// </summary>
    internal static ProgramSymbols Build(
        IReadOnlyList<Module> modules, IEnumerable<Symbol> defines, List<Diagnostic> diagnostics)
    {
        var byName = new Dictionary<string, Module>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules.OrderBy(module => module.Tree.Path, StringComparer.Ordinal))
        {
            if (module.Name is not { } name)
                continue;
            if (byName.TryGetValue(name, out var other))
            {
                diagnostics.Add(new Diagnostic(module.Tree.GetSpan(module.NameSpan),
                    Catalogue.ModuleNameTaken.Says(name, FileName(other.Tree)),
                    [new RelatedSpan(other.Tree.GetSpan(other.NameSpan), "declared here")]));
                continue;
            }
            // A module's output is named after it, and a file system that ignores case would
            // write two whose names differ only in case to one file.
            if (byName.Values.FirstOrDefault(named => string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                diagnostics.Add(new Diagnostic(module.Tree.GetSpan(module.NameSpan),
                    Catalogue.ModuleNamesDifferInCase.Says(name, same.Name),
                    [new RelatedSpan(same.Tree.GetSpan(same.NameSpan), "the other module")]));
            }
            byName[name] = module;
            for (var at = name.LastIndexOf("::", StringComparison.Ordinal); at > 0; at = name.LastIndexOf("::", at - 1, StringComparison.Ordinal))
                prefixes.Add(name[..at]);
        }

        // `hw::vic` cannot be both a module and a name module `hw` declares: a path to one of
        // them would reach the other just as well.
        foreach (var module in byName.Values)
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                var path = $"{module.Name}::{symbol.Name}";
                if (!symbol.IsCheapLocal && (byName.ContainsKey(path) || prefixes.Contains(path)))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan,
                        Catalogue.NameIsAModulePath.Says(path, module.Name, symbol.Name)));
                }
            }
        }

        var byLinkerName = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var module in byName.Values.OrderBy(module => module.Tree.Path, StringComparer.Ordinal))
        {
            foreach (var symbol in module.Exported)
            {
                if (symbol.LinkerName is not { } linked || !IsLinked(symbol))
                    continue;
                if (byLinkerName.TryGetValue(linked, out var other))
                {
                    diagnostics.Add(new Diagnostic(symbol.ExportSpan is { } at ? symbol.Tree.GetSpan(at) : symbol.DeclarationSpan,
                        Catalogue.ExportNameTaken.Says(symbol.PathName, other.PathName, linked),
                        [new RelatedSpan(other.DeclarationSpan, "the other export")]));
                    continue;
                }
                byLinkerName[linked] = symbol;
            }
        }

        var defined = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var define in defines)
            defined.TryAdd(define.Name, define);
        return new ProgramSymbols(byName, prefixes, defined);
    }

    /// <summary>
    /// Whether an export is a symbol to the linker. A macro, a charmap, a function, a list and a
    /// signature set are used by value, a scope and a type are only the way to their members, and an import
    /// is defined by somebody else.
    /// </summary>
    internal static bool IsLinked(Symbol symbol) => symbol.Kind is not (SymbolKind.Macro or SymbolKind.Charmap
        or SymbolKind.Func or SymbolKind.List or SymbolKind.Scope or SymbolKind.Enum or SymbolKind.Struct
        or SymbolKind.Union or SymbolKind.ImportedAddress or SymbolKind.ImportedConstant or SymbolKind.Frame
        or SymbolKind.Binding or SymbolKind.MacroParameter or SymbolKind.SignatureSet) && !symbol.IsDefine
        && !symbol.IsConfig && !symbol.Value.IsString;

    /// <summary>Every module of the program, by name.</summary>
    public IEnumerable<Module> Modules => modules.Values;

    /// <summary>Every define, which every file sees.</summary>
    public IEnumerable<Symbol> Defines => defines.Values;

    /// <summary>The module named <paramref name="name"/>, or null when no file is.</summary>
    public Module? ModuleNamed(string name) => modules.GetValueOrDefault(name);

    /// <summary>Whether <paramref name="path"/> is a module, or the start of one's name.</summary>
    public bool IsModulePath(string path) => modules.ContainsKey(path) || prefixes.Contains(path);

    /// <summary>The define named <paramref name="name"/>, which every file sees, or null.</summary>
    public Symbol? Define(string name) => defines.GetValueOrDefault(name);

    /// <summary>
    /// What <paramref name="name"/> is in <paramref name="module"/>: a name its file declares at
    /// its top level, exported or not, or a name it re-exports. <paramref name="touched"/> hears
    /// of every name looked for, as <c>module::name</c>, those a re-export leads through included.
    /// </summary>
    public Symbol? Member(Module module, string name, Action<string>? touched = null) =>
        Member(module, name, touched, []);

    /// <summary>The modules that export a top-level name <paramref name="name"/>, which is what a name no one declared may have meant.</summary>
    public IEnumerable<string> ModulesExporting(string name) =>
        modules.Values
            .Where(module => module.FileScope.FindMember(name) is { IsExported: true }
                || module.Reexports.Any(reexport => reexport.Name == name))
            .Select(module => module.Name!)
            .Order(StringComparer.Ordinal);

    private static string FileName(SyntaxTree tree) => tree.Path[(tree.Path.LastIndexOf('/') + 1)..];

    private Symbol? Member(Module module, string name, Action<string>? touched, HashSet<(string, string)> visiting)
    {
        touched?.Invoke($"{module.Name}::{name}");
        if (module.FileScope.FindMember(name) is { } declared)
            return declared;
        foreach (var reexport in module.Reexports)
        {
            if (reexport.Name == name && visiting.Add((module.Name!, name)))
                return Resolve(reexport.Path, touched, visiting);
        }
        return null;
    }

    /// <summary>
    /// The symbol a path written from the root of the modules leads to, or null. A re-export
    /// is resolved the same way, and one that leads back to itself leads nowhere.
    /// </summary>
    private Symbol? Resolve(IReadOnlyList<string> path, Action<string>? touched, HashSet<(string, string)> visiting)
    {
        var at = 0;
        Module? module = null;
        var name = "";
        for (var i = 0; i < path.Count; i++)
        {
            name = i == 0 ? path[0] : $"{name}::{path[i]}";
            if (modules.TryGetValue(name, out var found))
            {
                module = found;
                at = i + 1;
            }
            else if (!prefixes.Contains(name))
            {
                break;
            }
        }
        if (module is null || at >= path.Count || Member(module, path[at], touched, visiting) is not { } symbol)
            return null;
        for (var i = at + 1; i < path.Count; i++)
        {
            if (symbol.Body?.FindMember(path[i]) is not { } inner)
                return null;
            symbol = inner;
        }
        return symbol;
    }

    /// <summary>One file as the program sees it before its own names are resolved.</summary>
    /// <param name="Tree">The file.</param>
    /// <param name="Name">The module its <c>.module</c> names, or null when it names none.</param>
    /// <param name="NameSpan">Where that name is written.</param>
    /// <param name="FileScope">Its top-level scope, which is what another module can reach into.</param>
    /// <param name="Exported">Every symbol it exports, members of what it exports included.</param>
    /// <param name="Reexports">The names its <c>.export .use</c> items make part of it.</param>
    public sealed record Module(
        SyntaxTree Tree, string? Name, TextSpan NameSpan, Scope FileScope, IReadOnlyList<Symbol> Exported,
        IReadOnlyList<Reexport> Reexports);

    /// <summary>A name a module re-exports: <c>.export .use hw::vic::border</c>.</summary>
    /// <param name="Name">The name it is part of the module as.</param>
    /// <param name="Path">The path it was brought in from, from the root of the modules.</param>
    public sealed record Reexport(string Name, IReadOnlyList<string> Path);
}
