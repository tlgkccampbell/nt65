using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents what the modules of a program can see of one another. Every file is a module with
/// a name, and its symbols are private unless exported. A name from another module is written
/// with the module's path, as in <c>hw::vic::border</c>, or brought in with <c>.use</c>. Nothing
/// from another module is ever visible without one of those.
/// <para>
/// A module's name is only a name. <c>gfx::sprite</c> needs no module <c>gfx</c>, and modules
/// whose names start the same way are no more related than any others. The paths do form a
/// tree of names, however, so <c>gfx</c> alone is a prefix a path may pass through on its way to
/// a module.
/// </para>
/// <para>
/// Lookup also finds a module's private names, because the more useful diagnostic for such a
/// name is that it exists and is not exported. Whether it may actually be used is checked on the
/// whole name once it resolves. An interior label reached as <c>hw::outer::inner</c> needs
/// <c>inner</c> to be exported, but <c>outer</c> itself need not be.
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

    /// <summary>Gets the table for a program of one file, which can see nothing beyond itself.</summary>
    public static ProgramSymbols Empty { get; } = new([], [], []);

    /// <summary>Gets every module of the program.</summary>
    public IEnumerable<Module> Modules => modules.Values;

    /// <summary>Gets every define, which every file sees.</summary>
    public IEnumerable<Symbol> Defines => defines.Values;

    /// <summary>
    /// Returns the module named <paramref name="name"/>, or null when no file declares that
    /// module.
    /// </summary>
    public Module? ModuleNamed(string name) => modules.GetValueOrDefault(name);

    /// <summary>
    /// Returns a value indicating whether <paramref name="path"/> is a module or the start of a
    /// module's name.
    /// </summary>
    public bool IsModulePath(string path) => modules.ContainsKey(path) || prefixes.Contains(path);

    /// <summary>
    /// Returns the define named <paramref name="name"/>, which every file sees, or null if there
    /// is none.
    /// </summary>
    public Symbol? Define(string name) => defines.GetValueOrDefault(name);

    /// <summary>
    /// Returns what <paramref name="name"/> means in <paramref name="module"/>, which is either a
    /// name its file declares at its top level, exported or not, or a name it re-exports.
    /// <paramref name="touched"/> is called with every name looked for and the module it was
    /// looked for in, including those a re-export leads through.
    /// </summary>
    public Symbol? Member(Module module, string name, Action<string?, string>? touched = null) =>
        Member(module, name, touched, []);

    /// <summary>
    /// Returns the modules that export a top-level name <paramref name="name"/>. These are what an
    /// undeclared name may have meant.
    /// </summary>
    public IEnumerable<string> ModulesExporting(string name) =>
        modules.Values
            .Where(module => module.FileScope.FindMember(name) is { IsExported: true }
                || module.Reexports.Any(reexport => reexport.Name == name))
            .Select(module => module.Name!)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Builds the table for a program whose files have been collected but not yet resolved.
    /// Problems with the modules, rather than with one file, are reported here. These are two
    /// files that declare the same module, a name that is also the path of a module, and two
    /// exports under one linker name.
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
            if (StandardModules.IsReserved(name) && !StandardModules.IsStandard(module.Tree.Path))
            {
                diagnostics.Add(new Diagnostic(module.Tree.GetSpan(module.NameSpan), Catalogue.ModuleNameReserved.Message(name)));
                continue;
            }
            if (byName.TryGetValue(name, out var other))
            {
                diagnostics.Add(new Diagnostic(module.Tree.GetSpan(module.NameSpan),
                    Catalogue.ModuleNameTaken.Message(name, FileName(other.Tree)),
                    [new RelatedSpan(other.Tree.GetSpan(other.NameSpan), "declared here")]));
                continue;
            }
            // A module's output is named after it, and a file system that ignores case would
            // write two modules whose names differ only in case to the same file.
            if (byName.Values.FirstOrDefault(named => string.Equals(named.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                diagnostics.Add(new Diagnostic(module.Tree.GetSpan(module.NameSpan),
                    Catalogue.ModuleNamesDifferInCase.Message(name, same.Name),
                    [new RelatedSpan(same.Tree.GetSpan(same.NameSpan), "the other module")]));
            }
            byName[name] = module;
            for (var at = name.LastIndexOf("::", StringComparison.Ordinal); at > 0; at = name.LastIndexOf("::", at - 1, StringComparison.Ordinal))
                prefixes.Add(name[..at]);
        }

        // `hw::vic` cannot be both a module and a name that module `hw` declares, because a path
        // to one of them would reach the other just as well.
        foreach (var module in byName.Values)
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                var path = $"{module.Name}::{symbol.Name}";
                if (!symbol.IsCheapLocal && (byName.ContainsKey(path) || prefixes.Contains(path)))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan,
                        Catalogue.NameIsAModulePath.Message(path, module.Name, symbol.Name)));
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
                        Catalogue.ExportNameTaken.Message(symbol.PathName, other.PathName, linked),
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
    /// Returns a value indicating whether an export is a symbol to the linker. A macro, a charmap,
    /// a function, a list and a signature set are used by value. A scope and a type only lead to
    /// their members, and an import is defined elsewhere.
    /// </summary>
    internal static bool IsLinked(Symbol symbol) => symbol.Kind is not (SymbolKind.Macro or SymbolKind.Charmap
        or SymbolKind.Func or SymbolKind.List or SymbolKind.Scope or SymbolKind.Enum or SymbolKind.Struct
        or SymbolKind.Union or SymbolKind.ImportedAddress or SymbolKind.ImportedConstant or SymbolKind.Frame
        or SymbolKind.Binding or SymbolKind.MacroParameter or SymbolKind.SignatureSet) && !symbol.IsDefine
        && !symbol.IsConfig && !symbol.Value.IsString;

    private static string FileName(SyntaxTree tree) => tree.Path[(tree.Path.LastIndexOf('/') + 1)..];

    private Symbol? Member(Module module, string name, Action<string?, string>? touched, HashSet<(string, string)> visiting)
    {
        touched?.Invoke(module.Name, name);
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
    /// Returns the symbol that a path from the root of the modules leads to, or null. A re-export
    /// is resolved the same way, and a re-export that leads back to itself resolves to nothing.
    /// </summary>
    private Symbol? Resolve(IReadOnlyList<string> path, Action<string?, string>? touched, HashSet<(string, string)> visiting)
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

    /// <summary>Represents one file as the program sees it before the file's own names are resolved.</summary>
    /// <param name="Tree">The file.</param>
    /// <param name="Name">The module the file's <c>.module</c> names, or null when it names none.</param>
    /// <param name="NameSpan">The span of that name.</param>
    /// <param name="FileScope">The file's top-level scope, which another module can reach into.</param>
    /// <param name="Exported">Every symbol the file exports, including members of what it exports.</param>
    /// <param name="Reexports">The names the file's <c>.export .use</c> items make part of the module.</param>
    public sealed record Module(
        SyntaxTree Tree, string? Name, TextSpan NameSpan, Scope FileScope, IReadOnlyList<Symbol> Exported,
        IReadOnlyList<Reexport> Reexports);

    /// <summary>
    /// Represents a name a <c>.use</c> brings in, together with the path it leads to. A module's
    /// re-exports, as in <c>.export .use hw::vic::border</c>, are the names its exported
    /// <c>.use</c> items bring in.
    /// </summary>
    /// <param name="Name">The name under which the symbol is brought in, and under which a re-exported symbol becomes part of the module.</param>
    /// <param name="Path">The path the symbol was brought in from, starting at the root of the modules.</param>
    public sealed record Reexport(string Name, IReadOnlyList<string> Path);
}
