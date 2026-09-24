using System.Runtime.CompilerServices;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Determines which <c>.if</c> branches a build takes, and therefore which parts of the program
/// exist at all.
/// <para>
/// A condition tests the build configuration and never the program. It may use literals,
/// operators, built-in functions, defines and <c>.config</c> settings, and nothing else that a
/// file declares. This lets every condition be evaluated here, before any declaration has been
/// collected. Which declarations exist follows from the configuration alone, so the same name
/// may be declared under two conditions and only one of them is real. A check that depends on
/// the program is an <c>.assert</c>, which is evaluated last.
/// </para>
/// </summary>
public sealed class Configuration
{
    // A file's `.config` items, wherever they appear, read once per tree.
    private static readonly ConditionalWeakTable<SyntaxTree, List<ConfigDeclarationSyntax>> settingsByTree = new();

    private readonly Dictionary<SyntaxTree, List<TextSpan>> omitted;
    private readonly HashSet<(SyntaxTree Tree, int Position)> answered;
    private readonly Settings settings;

    private Configuration(
        Dictionary<SyntaxTree, List<TextSpan>> omitted, HashSet<(SyntaxTree, int)> answered, Cpu cpu, Settings settings)
    {
        this.omitted = omitted;
        this.answered = answered;
        this.settings = settings;
        Cpu = cpu;
    }

    /// <summary>Gets a build that leaves nothing out, for a caller with no conditions to resolve.</summary>
    public static Configuration Everything { get; } = new([], [], ProgramCpu.Default, new Settings());

    /// <summary>Gets the CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</summary>
    public Cpu Cpu { get; }

    /// <summary>Gets a value indicating whether any file of the program declares a <c>.config</c>.</summary>
    internal bool HasSettings => settings.Any;

    /// <summary>
    /// Determines which branches <paramref name="trees"/> take when built for
    /// <paramref name="cpu"/> with <paramref name="defines"/>.
    /// </summary>
    public static Configuration Resolve(
        IEnumerable<SyntaxTree> trees, Cpu cpu, IEnumerable<Define> defines, List<Diagnostic> diagnostics)
    {
        var values = Plain(defines);
        var all = trees.ToList();
        var settings = Settings.Read(all, cpu, values, defines, diagnostics);

        var evaluator = settings.EvaluatorFor(cpu, values, diagnostics);
        var omitted = new Dictionary<SyntaxTree, List<TextSpan>>();
        var answered = new HashSet<(SyntaxTree, int)>();
        foreach (var tree in all)
        {
            var left = new List<TextSpan>();
            new Reader(tree, evaluator, diagnostics, left, answered).Container(tree.Root);
            if (left.Count > 0)
                omitted[tree] = left;
        }
        return new Configuration(omitted, answered, cpu, settings);
    }

    /// <summary>
    /// Determines whether <paramref name="tree"/> declares a <c>.config</c> anywhere. Any file's
    /// conditions may read the value of such a setting.
    /// </summary>
    public static bool DeclaresSettings(SyntaxTree tree) => SettingsIn(tree).Count > 0;

    /// <summary>
    /// Determines whether this pass evaluated the condition on <paramref name="block"/>. The pass
    /// evaluates every condition it can reach before a declaration is looked up. The ones it
    /// cannot reach are inside a macro body, a <c>.repeat</c> or an <c>.each</c>, where a condition
    /// may name what the expansion binds, and those are evaluated once per expansion instead.
    /// </summary>
    public bool Answered(BlockSyntax block) => answered.Contains((block.Tree, block.Position));

    /// <summary>Determines whether the build includes the source at <paramref name="node"/>.</summary>
    public bool Includes(SyntaxNode node)
    {
        if (!omitted.TryGetValue(node.Tree, out var left))
            return true;
        foreach (var span in left)
        {
            if (node.Position >= span.Start && node.Position < span.End)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the branches of <paramref name="tree"/> that this build leaves out, for an editor to dim.
    /// A branch inside one that is already left out is not listed again.
    /// </summary>
    public IReadOnlyList<TextSpan> Omitted(SyntaxTree tree) =>
        omitted.TryGetValue(tree, out var left) ? left : [];


    /// <summary>
    /// Determines whether <paramref name="before"/> and <paramref name="after"/>, two versions of
    /// one file, name the same module and re-export the same names. A condition in another file
    /// may reach a setting through the module's path or its re-exports, so a file that changes
    /// either changes what such a condition means.
    /// </summary>
    internal static bool LeadsAlike(SyntaxTree before, SyntaxTree after) =>
        ModuleSyntax.ModuleOf(before) == ModuleSyntax.ModuleOf(after)
        && Reexports(before).Select(Spelled).SequenceEqual(Reexports(after).Select(Spelled));

    /// <summary>
    /// Returns the value of <c>.target(cpu)</c> or <c>.has(mnemonic)</c>, whichever
    /// <paramref name="kind"/> is, for a build for <paramref name="cpu"/>. The caller has checked
    /// that <paramref name="given"/> holds one argument. Problems with the argument are reported
    /// through <paramref name="report"/>.
    /// </summary>
    internal static Value AboutTheCpu(
        BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> given, Cpu cpu,
        Action<TextSpan, DiagnosticMessage> report)
    {
        switch (kind)
        {
            case BuiltinKind.Target:
                if (Alone(given[0]) is not { } cpuName || CpuNames.Parse(cpuName.Text) is not { } named)
                {
                    report(function.Span, Catalogue.TargetArgument.Message(CpuNames.Listed));
                    return Value.Unknown;
                }
                return Value.Of(named == cpu);

            // `.has` asks whether the build's CPU has an instruction, whichever CPU that is. A
            // program that runs on more than one CPU asks this rather than listing the CPUs
            // that have the instruction.
            case BuiltinKind.Has:
                if (given[0] is not NameExpressionSyntax { SimpleName: { Kind: SyntaxKind.Mnemonic } mnemonic })
                {
                    report(function.Span, Catalogue.HasArgument);
                    return Value.Unknown;
                }
                return Value.Of(Processor.Instructions.Available(cpu, mnemonic.MnemonicKind));

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only `.target` and `.has` ask about the CPU.");
        }
    }

    /// <summary>
    /// Returns the single token that makes up an argument, such as a CPU name, a number, or a
    /// name with a single component. Returns null when the argument is anything more.
    /// </summary>
    private static SyntaxToken? Alone(SyntaxNode argument) =>
        argument is NameExpressionSyntax name ? name.SimpleName
        : argument.ChildTokens is [var only] ? only
        : null;

    /// <summary>
    /// Determines whether a setting is where its placement allows it, which is at file level,
    /// outside every block, whether or not it is exported.
    /// </summary>
    internal static bool IsWellPlaced(ConfigDeclarationSyntax setting) =>
        !SyntaxFacts.PlacementOf(DirectiveKind.Config).IsBarredBy(SyntaxFacts.NestingOf(setting));

    /// <summary>
    /// Returns the value the build gives the <c>.config</c> <paramref name="name"/> that
    /// <paramref name="tree"/> declares, or null when it declares none by that name or its value
    /// is unknown.
    /// </summary>
    internal long? SettingOf(SyntaxTree tree, string name) => settings.ValueOf(tree, name);

    /// <summary>
    /// Returns the configuration of a program in which <paramref name="before"/> was replaced by
    /// <paramref name="after"/>. A condition depends only on the file it is in, the build and the
    /// settings. A file that declares a setting is analyzed with the whole program, and so is one
    /// that does not lead to the settings as it did before (see <see cref="LeadsAlike"/>). So
    /// every other file's results remain valid.
    /// </summary>
    internal Configuration Replacing(
        SyntaxTree before, SyntaxTree after, Cpu cpu, IEnumerable<Define> defines, List<Diagnostic> diagnostics)
    {
        var replaced = new Dictionary<SyntaxTree, List<TextSpan>>(omitted);
        replaced.Remove(before);
        var reanswered = new HashSet<(SyntaxTree, int)>(answered.Where(at => at.Tree != before));
        var left = new List<TextSpan>();
        var moved = settings.Replacing(before, after);
        new Reader(after, moved.EvaluatorFor(cpu, Plain(defines), diagnostics), diagnostics, left, reanswered)
            .Container(after.Root);
        if (left.Count > 0)
            replaced[after] = left;
        return new Configuration(replaced, reanswered, cpu, moved);
    }

    /// <summary>
    /// Returns the build's defines that a plain name in a file can refer to, by name. A define
    /// given with a module's path sets a <c>.config</c> instead, and is not a name a file can use.
    /// </summary>
    private static Dictionary<string, long> Plain(IEnumerable<Define> defines)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var define in defines.Where(define => !define.IsSetting))
            values[define.Name] = define.Value;
        return values;
    }

    private static List<ConfigDeclarationSyntax> SettingsIn(SyntaxTree tree) =>
        settingsByTree.GetValue(tree, tree => [.. tree.Root.DescendantNodes().OfType<ConfigDeclarationSyntax>()]);

    /// <summary>Returns the names that the <c>.export .use</c> items of a file re-export.</summary>
    private static IEnumerable<ProgramSymbols.Reexport> Reexports(SyntaxTree tree) =>
        UsesIn(tree).Where(use => use.IsExported).SelectMany(ModuleSyntax.Brought);

    /// <summary>Returns a re-export as one string, so that two lists of them can be compared.</summary>
    private static string Spelled(ProgramSymbols.Reexport reexport) => $"{reexport.Name} {string.Join("::", reexport.Path)}";

    /// <summary>Returns the <c>.use</c> items of a file that this pass reads.</summary>
    private static IEnumerable<UseDirectiveSyntax> UsesIn(SyntaxTree tree) =>
        FileLevel(tree.Root).OfType<UseDirectiveSyntax>();

    /// <summary>
    /// Returns the statements that are at the file level of <paramref name="container"/> and
    /// under no condition. A statement in a <c>.segment</c> region or block is at file level,
    /// because a segment leaves names in the scope around it.
    /// </summary>
    private static IEnumerable<StatementSyntax> FileLevel(SyntaxNode container)
    {
        foreach (var child in container.ChildNodes)
        {
            if (child is LineSyntax { Statement: { } statement })
            {
                yield return statement;
            }
            else if (child is BlockSyntax { BlockKind: BlockKind.Region or BlockKind.Segment } segment)
            {
                foreach (var inner in FileLevel(segment))
                    yield return inner;
            }
        }
    }

    /// <summary>
    /// Reads one file's conditions. A chain is a run of sibling blocks, made up of the
    /// <c>.if</c> that starts it followed by any <c>.elseif</c> and <c>.else</c> blocks that
    /// continue it. The first branch whose condition holds is the one the build takes. The
    /// conditions after it are never evaluated, so nothing is reported about a branch that is
    /// not there.
    /// </summary>
    private sealed class Reader(
        SyntaxTree tree,
        Evaluator evaluator,
        List<Diagnostic> diagnostics,
        List<TextSpan> omitted,
        HashSet<(SyntaxTree, int)> answered)
    {
        public void Container(SyntaxNode container)
        {
            foreach (var (child, included) in ConditionChain.Walk(container.ChildNodes, 0, Branch, Orphaned))
            {
                // A condition inside one of these may name what the expansion binds, so it
                // has no value until there is an expansion to evaluate it in.
                if (child is not BlockSyntax block
                    || block.BlockKind is BlockKind.Macro or BlockKind.Repeat or BlockKind.Each or BlockKind.MultiProc)
                {
                    continue;
                }
                if (included)
                    Container(block);
                else
                    Leave(block);
            }
        }

        public Value Evaluate(SyntaxNode node) => evaluator.Evaluate(node);

        public void Report(TextSpan span, DiagnosticMessage message) =>
            diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message));

        /// <summary>
        /// Determines whether the build takes one branch of a chain, and records that the build
        /// answered it. <paramref name="already"/> indicates that an earlier branch was taken, in
        /// which case this one is left out regardless of its condition.
        /// </summary>
        private bool Branch(BlockSyntax block, bool already)
        {
            answered.Add((tree, block.Position));

            // The CPU is configuration, and a condition may test it with `.target`, so a
            // `.cpu` under an `.if` would change the very thing its condition may depend on. The
            // placement of `.cpu`, which the editor reads too, bars it there.
            foreach (var node in block.DescendantNodes())
            {
                if (node is CpuDirectiveSyntax && SyntaxFacts.PlacementOf(DirectiveKind.Cpu).IsBarredBy(DirectiveNesting.Condition))
                    Report(node.Span, Catalogue.CpuUnderACondition);
            }

            return !already && Holds(block.Opener.Statement);
        }

        /// <summary>Reports an <c>.elseif</c> or <c>.else</c> that continues no chain.</summary>
        private void Orphaned(BlockSyntax block) =>
            Report(block.Opener.Statement.Span, Catalogue.ElseWithoutIf.Message(Directive(block.Opener.Statement)));

        /// <summary>Determines whether the condition of an <c>.if</c> or <c>.elseif</c> holds.</summary>
        private bool Holds(StatementSyntax opener)
        {
            if (opener is ElseDirectiveSyntax)
                return true;
            if (opener is not ConditionalDirectiveSyntax { Condition: var condition })
                return false;

            var value = Evaluate(condition);
            if (value.IsString)
            {
                Report(condition.Span, Catalogue.ConditionIsText);
                return false;
            }
            return value.AsNumber() is { } number && number != 0;
        }

        // The span runs from the block's full start, including indentation, to its closing
        // brace. `Includes` compares where a node starts, and an indented block starts at its
        // line's leading whitespace, before its first token.
        private void Leave(BlockSyntax block) =>
            omitted.Add(new TextSpan(block.Position, block.Span.End - block.Position));

        private static string Directive(StatementSyntax opener) =>
            opener is ElseDirectiveSyntax ? ".else" : ".elseif";
    }

    /// <summary>
    /// Represents the <c>.config</c> items of a program, which are defines declared in source
    /// files, each belonging to its module. Each is declared at file level outside every block,
    /// so that which settings exist depends on no condition. Its value may use literals,
    /// built-ins, the build's defines and other settings. The build can set a setting that a
    /// module exports by its qualified name, which makes the value in the file only a default.
    /// <para>
    /// A condition names a setting as the rest of the program names any declaration. The settings
    /// are therefore kept in a <see cref="ProgramSymbols"/> of their own, and a name is resolved
    /// with <see cref="Semantics.Lookup"/>, the same way the binder resolves it. That covers a
    /// setting a module re-exports, one reached through a module a <c>.use</c> brought in, and
    /// the order in which a name is looked for.
    /// </para>
    /// <para>
    /// The conditions are evaluated before the binder collects the program, because they decide
    /// which of its declarations exist. So this class reads less than the binder does, and the
    /// difference is deliberate.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An <c>.export</c> or a <c>.use</c> under a condition is not read, because whether it exists
    /// depends on the conditions being evaluated. One in a <c>.segment</c> region or block is read.
    /// </description></item>
    /// <item><description>
    /// The table holds only settings, because the other declarations are not collected yet. A
    /// name that means another declaration is therefore not a setting, and the condition reports
    /// that it names the program.
    /// </description></item>
    /// <item><description>
    /// The build sets a setting by the path of the module that declares it. A module that
    /// re-exports the setting does not give it a second path for the build to use.
    /// </description></item>
    /// </list>
    /// </summary>
    private sealed class Settings
    {
        private readonly ProgramSymbols program;
        private readonly Dictionary<SyntaxTree, Scope> scopes;
        private readonly Dictionary<Symbol, Setting> bySymbol;
        private readonly Dictionary<Setting, long?> values;
        private readonly Dictionary<SyntaxTree, Reach> reached;
        private readonly List<Setting> evaluating = [];
        private Func<SyntaxTree, Reader>? readerFor;

        public Settings()
            : this(ProgramSymbols.Empty, [], [], [], [])
        {
        }

        private Settings(
            ProgramSymbols program, Dictionary<SyntaxTree, Scope> scopes, Dictionary<Symbol, Setting> bySymbol,
            Dictionary<Setting, long?> values, Dictionary<SyntaxTree, Reach> reached)
        {
            this.program = program;
            this.scopes = scopes;
            this.bySymbol = bySymbol;
            this.values = values;
            this.reached = reached;
        }

        /// <summary>Gets a value indicating whether the program declares any setting.</summary>
        public bool Any => bySymbol.Count > 0;

        /// <summary>
        /// Reads every setting that <paramref name="trees"/> declare, together with what the build
        /// sets. Each setting is evaluated once, so that a problem with it is reported whether or
        /// not anything reads it.
        /// </summary>
        public static Settings Read(
            IReadOnlyList<SyntaxTree> trees, Cpu cpu, Dictionary<string, long> defines, IEnumerable<Define> set,
            List<Diagnostic> diagnostics)
        {
            var scopes = new Dictionary<SyntaxTree, Scope>();
            var bySymbol = new Dictionary<Symbol, Setting>();
            var modules = new List<ProgramSymbols.Module>();
            foreach (var tree in trees)
            {
                var scope = FileScope(tree);
                scopes[tree] = scope;
                var exported = ExportedNames(tree);
                foreach (var declaration in SettingsIn(tree))
                {
                    // A setting is identified by its name, so a line without a name declares nothing.
                    if (declaration.Name is not { IsMissing: false } name)
                        continue;
                    if (!Configuration.IsWellPlaced(declaration))
                    {
                        diagnostics.Add(new Diagnostic(tree.GetSpan(declaration.Keyword.Span),
                            Catalogue.ConfigMisplaced));
                        continue;
                    }
                    var symbol = new Symbol(name.Text, SymbolKind.Constant, scope, tree, name.Span)
                    {
                        IsConfig = true,
                        IsExported = declaration.IsExported || exported.Contains(name.Text),
                    };
                    if (scope.Declare(symbol) is null)
                        bySymbol[symbol] = new Setting(symbol, name, declaration.Value);
                }
                modules.Add(new ProgramSymbols.Module(
                    tree, scope.Module, default, scope, [.. scope.Symbols.Where(symbol => symbol.IsExported)],
                    [.. Reexports(tree)]));
            }

            // Problems with the modules themselves are the binder's to report.
            var settings = new Settings(ProgramSymbols.Build(modules, [], []), scopes, bySymbol, [], []);

            // The build names a setting with the module's path, as the setting is named from
            // outside the module.
            foreach (var define in set.Where(define => define.IsSetting))
            {
                var at = define.Name.LastIndexOf("::", StringComparison.Ordinal);
                var module = define.Name[..at];
                if (settings.program.ModuleNamed(module)?.FileScope.FindMember(define.Name[(at + 2)..]) is not { } symbol)
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingUnknown.Message(define.Name)));
                }
                else if (!symbol.IsExported)
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingNotExported.Message(define.Name, module)));
                }
                else
                {
                    bySymbol[symbol].Given = define.Value;
                }
            }

            var evaluator = settings.EvaluatorFor(cpu, defines, diagnostics);
            settings.readerFor = tree => new Reader(tree, evaluator, diagnostics, [], []);
            foreach (var setting in bySymbol.Values)
                settings.Worth(setting);
            return settings;
        }

        /// <summary>
        /// Returns these settings after <paramref name="before"/> was replaced by
        /// <paramref name="after"/>. Neither version declares a setting, and both name the same
        /// module and re-export the same names, because otherwise the whole program would be read
        /// again. So every value is kept, and only what <paramref name="after"/> brings in is read
        /// afresh.
        /// </summary>
        public Settings Replacing(SyntaxTree before, SyntaxTree after)
        {
            var moved = new Dictionary<SyntaxTree, Scope>(scopes);
            moved.Remove(before);
            moved[after] = FileScope(after);
            var reach = new Dictionary<SyntaxTree, Reach>(reached);
            reach.Remove(before);
            return new Settings(program, moved, bySymbol, values, reach) { readerFor = readerFor };
        }

        /// <summary>
        /// Creates an evaluator for the conditions of a build for <paramref name="cpu"/> with
        /// <paramref name="defines"/>. Its names refer to these settings, and it reports into
        /// <paramref name="diagnostics"/>.
        /// </summary>
        public Evaluator EvaluatorFor(Cpu cpu, Dictionary<string, long> defines, List<Diagnostic> diagnostics) =>
            Evaluator.ForConditions(
                new Conditions(cpu, defines, (name, report) => Named(name, defines, report)), diagnostics);

        /// <summary>
        /// Returns the value of a name in a condition as a setting. Returns null when the name
        /// refers to no setting and nothing was reported about it, which leaves the caller to
        /// read it as a define or report it as an unknown name.
        /// </summary>
        public Value? Named(
            NameExpressionSyntax name, Dictionary<string, long> defines, Action<SyntaxNode, DiagnosticMessage> report)
        {
            var (setting, reported) = Find(name.Tree, name, defines, report);
            if (setting is not null)
                return Worth(setting) is { } value ? Value.Of(value) : Value.Unknown;
            return reported ? Value.Unknown : null;
        }

        /// <summary>
        /// Returns the value of the setting <paramref name="name"/> in <paramref name="tree"/>, or
        /// null.
        /// </summary>
        public long? ValueOf(SyntaxTree tree, string name) =>
            scopes.TryGetValue(tree, out var scope) && scope.FindMember(name) is { } symbol
                && bySymbol.TryGetValue(symbol, out var setting)
                ? values.GetValueOrDefault(setting)
                : null;

        /// <summary>
        /// Finds the setting that a name in <paramref name="tree"/> refers to, and whether anything
        /// was reported about it, such as a setting that another module does not export. The
        /// name is looked for as the binder looks for it. It may be a setting the file declares,
        /// one a <c>.use</c> brought in, or one named with a module's path.
        /// </summary>
        public (Setting? Setting, bool Reported) Find(
            SyntaxTree tree, NameExpressionSyntax name, Dictionary<string, long> defines,
            Action<SyntaxNode, DiagnosticMessage> report)
        {
            var parts = name.Names;
            if (parts.Length == 0 || !scopes.TryGetValue(tree, out var own))
                return (null, false);

            var text = parts[0].Text;
            var start = name.GlobalToken is not null ? Semantics.Lookup.ModuleRoot(text, program)
                : First(tree, own, text, last: parts.Length == 1, defines);
            var place = Semantics.Lookup.Walk(start, [.. parts.Select(part => part.Text)], program);

            // A name that two `.use module::*` items bring in is ambiguous. The binder reports
            // that, so the name has no value here and nothing more is said about it.
            if (place is { IsReported: true })
                return (null, true);
            if (place?.Symbol is not { } symbol || !bySymbol.TryGetValue(symbol, out var setting))
                return (null, false);
            if (!symbol.IsExported && symbol.Tree != tree)
            {
                report(name, Catalogue.NotExported.Message(name.GetText().Trim(), symbol.Module));
                return (null, true);
            }
            return (setting, false);
        }

        /// <summary>
        /// Returns the value of a setting, which is the value the build sets or otherwise the
        /// value its file gives it.
        /// </summary>
        public long? Worth(Setting setting)
        {
            if (values.TryGetValue(setting, out var known))
                return known;
            var reader = readerFor!(setting.Tree);
            if (evaluating.Contains(setting))
            {
                reader.Report(setting.Name.Span, Catalogue.DefinedInTermsOfItself.Message(setting.Name.Text));
                values[setting] = null;
                return null;
            }
            evaluating.Add(setting);
            var evaluated = setting.Expression is { } expression ? reader.Evaluate(expression) : Value.Unknown;
            evaluating.Remove(setting);
            if (evaluated.IsString && setting.Expression is { } text)
                reader.Report(text.Span, Catalogue.ConfigIsText);
            if (values.TryGetValue(setting, out var already))
                return already;
            return values[setting] = setting.Given ?? evaluated.AsNumber();
        }

        /// <summary>Creates the scope that holds the settings of <paramref name="tree"/>.</summary>
        private static Scope FileScope(SyntaxTree tree) =>
            new(ScopeKind.File, null, null, null) { Module = ModuleSyntax.ModuleOf(tree) };

        /// <summary>Returns the names in a file's <c>.export</c> lists, which may include settings.</summary>
        private static HashSet<string> ExportedNames(SyntaxTree tree)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var export in FileLevel(tree.Root).OfType<ExportDirectiveSyntax>())
            {
                foreach (var item in export.Items)
                {
                    if (item.Name.SimpleName is { Kind: SyntaxKind.Identifier } name)
                        names.Add(name.Text);
                }
            }
            return names;
        }

        /// <summary>
        /// Returns what the first part of a name means in <paramref name="tree"/>. The order is
        /// the binder's, which is the file's own settings, then what a <c>.use</c> brought in, then
        /// the defines, and then the modules and what a <c>.use module::*</c> brought in. Returns
        /// null for a define, which the caller reads, and for a <c>.use</c> that brought in
        /// something other than a setting.
        /// </summary>
        private Resolution? First(SyntaxTree tree, Scope own, string text, bool last, Dictionary<string, long> defines)
        {
            if (own.FindMember(text) is { } local)
                return new Resolution(local);
            var reach = ReachOf(tree);
            if (reach.Brought.TryGetValue(text, out var brought))
                return brought.IsReported ? null : brought;
            if (defines.ContainsKey(text))
                return null;
            return Semantics.Lookup.Outside(text, last, program, reach.Brought, reach.Globs);
        }

        /// <summary>
        /// Returns what the <c>.use</c> items of <paramref name="tree"/> bring in, reading them the
        /// first time they are asked about.
        /// </summary>
        private Reach ReachOf(SyntaxTree tree)
        {
            if (reached.TryGetValue(tree, out var known))
                return known;
            var brought = new Dictionary<string, Resolution>(StringComparer.Ordinal);
            var globs = new List<ProgramSymbols.Module>();
            foreach (var use in UsesIn(tree))
            {
                // The binder refuses an exported `*`, which brings in nothing.
                if (use.StarToken is not null)
                {
                    if (!use.IsExported && program.ModuleNamed(ModuleSyntax.PathOf(use.Path)) is { } module)
                        globs.Add(module);
                    continue;
                }

                // A `.use` whose path leads to no setting still brings the name in, so that the
                // name does not go on to mean a define or what a `*` brought in.
                foreach (var item in ModuleSyntax.Brought(use))
                    brought.TryAdd(item.Name, Walk(item.Path) ?? Resolution.Reported);
            }
            return reached[tree] = new Reach(brought, globs);
        }

        /// <summary>
        /// Returns where a <c>.use</c> path leads from the root of the modules, or null when it
        /// leads nowhere. A setting has no members, so a path that goes on past one leads nowhere.
        /// </summary>
        private Resolution? Walk(IReadOnlyList<string> path) =>
            path.Count == 0 ? null : Semantics.Lookup.Walk(Semantics.Lookup.ModuleRoot(path[0], program), path, program);
    }

    /// <summary>
    /// Represents one <c>.config</c>, with the symbol that stands for it while conditions are
    /// evaluated, the value its file gives it, and the value the build sets.
    /// </summary>
    private sealed class Setting(Symbol symbol, SyntaxToken name, ExpressionSyntax? expression)
    {
        public Symbol Symbol { get; } = symbol;

        public SyntaxTree Tree => Symbol.Tree;

        public SyntaxToken Name { get; } = name;

        public ExpressionSyntax? Expression { get; } = expression;

        /// <summary>The value the build sets, which replaces the file's, or null when it sets none.</summary>
        public long? Given { get; set; }
    }

    /// <summary>
    /// Represents what the <c>.use</c> items of one file bring in, which are names and the
    /// modules whose exports a <c>.use module::*</c> brings in.
    /// </summary>
    /// <param name="Brought">The names brought in one by one, and where each leads.</param>
    /// <param name="Globs">The modules whose exports a <c>.use module::*</c> brings in.</param>
    private sealed record Reach(Dictionary<string, Resolution> Brought, List<ProgramSymbols.Module> Globs);
}
