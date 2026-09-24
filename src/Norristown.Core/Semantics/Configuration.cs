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
    /// Returns the value of <c>.target(cpu)</c> or <c>.has(mnemonic)</c>, whichever
    /// <paramref name="kind"/> is, for a build for <paramref name="cpu"/>. Problems with the
    /// argument are reported through <paramref name="report"/>.
    /// </summary>
    internal static Value AboutTheCpu(
        BuiltinKind kind, SyntaxToken function, IReadOnlyList<SyntaxNode> given, Cpu cpu,
        Action<TextSpan, DiagnosticMessage> report)
    {
        switch (kind)
        {
            case BuiltinKind.Target:
                if (given.Count != 1 || Alone(given[0]) is not { } cpuName || CpuNames.Parse(cpuName.Text) is not { } named)
                {
                    report(function.Span, Catalogue.TargetArgument.Message(CpuNames.Listed));
                    return Value.Unknown;
                }
                return Value.Of(named == cpu);

            // `.has` asks whether the build's CPU has an instruction, whichever CPU that is. A
            // program that runs on more than one CPU asks this rather than listing the CPUs
            // that have the instruction.
            case BuiltinKind.Has:
                if (given.Count != 1 || given[0] is not NameExpressionSyntax { SimpleName: { Kind: SyntaxKind.Mnemonic } mnemonic })
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
    /// settings. A file that declares a setting is analyzed with the whole program, so every other
    /// file's results remain valid.
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
            var chaining = false;
            var taken = false;
            foreach (var child in container.ChildNodes)
            {
                if (child is not BlockSyntax block)
                {
                    chaining = false;
                    continue;
                }

                // A condition inside one of these may name what the expansion binds, so it
                // has no value until there is an expansion to evaluate it in.
                if (block.BlockKind is BlockKind.Macro or BlockKind.Repeat or BlockKind.Each or BlockKind.MultiProc)
                {
                    chaining = false;
                    continue;
                }

                var opener = block.Opener.Statement;
                switch (opener)
                {
                    case IfDirectiveSyntax:
                        chaining = true;
                        answered.Add((tree, block.Position));
                        taken = Branch(block, opener, already: false);
                        continue;

                    case ElseIfDirectiveSyntax:
                    case ElseDirectiveSyntax:
                        if (!chaining)
                        {
                            Report(opener.Span, Catalogue.ElseWithoutIf.Message(Directive(opener)));
                            Leave(block);
                            continue;
                        }
                        answered.Add((tree, block.Position));
                        taken |= Branch(block, opener, taken);
                        continue;

                    default:
                        chaining = false;
                        Container(block);
                        continue;
                }
            }
        }

        public Value Evaluate(SyntaxNode node) => evaluator.Evaluate(node);

        public void Report(TextSpan span, DiagnosticMessage message) =>
            diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message));

        /// <summary>
        /// Determines whether the build takes one branch of a chain. <paramref name="already"/>
        /// indicates that an earlier branch was taken, in which case this one is left out
        /// regardless of its condition.
        /// </summary>
        private bool Branch(BlockSyntax block, StatementSyntax opener, bool already)
        {
            // The CPU is configuration, and a condition may test it with `.target`, so a
            // `.cpu` under an `.if` would change the very thing its condition may depend on. The
            // placement of `.cpu`, which the editor reads too, bars it there.
            foreach (var node in block.DescendantNodes())
            {
                if (node is CpuDirectiveSyntax && SyntaxFacts.PlacementOf(DirectiveKind.Cpu).IsBarredBy(DirectiveNesting.Condition))
                    Report(node.Span, Catalogue.CpuUnderACondition);
            }

            var take = !already && Holds(opener);
            if (take)
                Container(block);
            else
                Leave(block);
            return take;
        }

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
    /// </summary>
    private sealed class Settings
    {
        private readonly Dictionary<(string Module, string Name), Setting> byName;
        private readonly Dictionary<SyntaxTree, string> modules;
        private readonly Dictionary<Setting, long?> values;
        private readonly List<Setting> evaluating = [];
        private Func<SyntaxTree, Reader>? readerFor;

        public Settings()
            : this([], [], [])
        {
        }

        private Settings(
            Dictionary<(string Module, string Name), Setting> byName, Dictionary<SyntaxTree, string> modules,
            Dictionary<Setting, long?> values)
        {
            this.byName = byName;
            this.modules = modules;
            this.values = values;
        }

        /// <summary>
        /// Reads every setting that <paramref name="trees"/> declare, together with what the build
        /// sets. Each setting is evaluated once, so that a problem with it is reported whether or
        /// not anything reads it.
        /// </summary>
        public static Settings Read(
            IReadOnlyList<SyntaxTree> trees, Cpu cpu, Dictionary<string, long> defines, IEnumerable<Define> set,
            List<Diagnostic> diagnostics)
        {
            var settings = new Settings();
            foreach (var tree in trees)
            {
                var module = ModuleOf(tree);
                settings.modules[tree] = module;
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
                    settings.byName.TryAdd((module, name.Text), new Setting(tree, name, declaration.Value,
                        declaration.IsExported || exported.Contains(name.Text)));
                }
            }

            // The build names a setting with the module's path, as the setting is named from
            // outside the module.
            foreach (var define in set.Where(define => define.IsSetting))
            {
                var at = define.Name.LastIndexOf("::", StringComparison.Ordinal);
                var key = (define.Name[..at], define.Name[(at + 2)..]);
                if (!settings.byName.TryGetValue(key, out var setting))
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingUnknown.Message(define.Name)));
                }
                else if (!setting.IsExported)
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingNotExported.Message(define.Name, key.Item1)));
                }
                else
                {
                    setting.Given = define.Value;
                }
            }

            var evaluator = settings.EvaluatorFor(cpu, defines, diagnostics);
            settings.readerFor = tree => new Reader(tree, evaluator, diagnostics, [], []);
            foreach (var setting in settings.byName.Values)
                settings.Worth(setting);
            return settings;
        }

        /// <summary>
        /// Returns these settings after <paramref name="before"/> was replaced by
        /// <paramref name="after"/>. Neither version declares a setting, because otherwise the
        /// whole program would be read again, so every value is kept.
        /// </summary>
        public Settings Replacing(SyntaxTree before, SyntaxTree after)
        {
            var moved = new Dictionary<SyntaxTree, string>(modules);
            moved.Remove(before);
            moved[after] = ModuleOf(after);
            return new Settings(byName, moved, values) { readerFor = readerFor };
        }

        /// <summary>
        /// Creates an evaluator for the conditions of a build for <paramref name="cpu"/> with
        /// <paramref name="defines"/>. Its names refer to these settings, and it reports into
        /// <paramref name="diagnostics"/>.
        /// </summary>
        public Evaluator EvaluatorFor(Cpu cpu, Dictionary<string, long> defines, List<Diagnostic> diagnostics) =>
            Evaluator.ForConditions(new Conditions(cpu, defines, Lookup), diagnostics);

        /// <summary>
        /// Returns the value of a name in a condition as a setting. Returns null when the name
        /// refers to no setting and nothing was reported about it, which leaves the caller to
        /// report it as an unknown name.
        /// </summary>
        public Value? Lookup(NameExpressionSyntax name, Action<SyntaxNode, DiagnosticMessage> report)
        {
            var (setting, reported) = Find(name.Tree, name, report);
            if (setting is not null)
                return Worth(setting) is { } value ? Value.Of(value) : Value.Unknown;
            return reported ? Value.Unknown : null;
        }

        /// <summary>
        /// Returns the value of the setting <paramref name="name"/> in <paramref name="tree"/>, or
        /// null.
        /// </summary>
        public long? ValueOf(SyntaxTree tree, string name) =>
            modules.TryGetValue(tree, out var module) && byName.TryGetValue((module, name), out var setting)
                ? values.GetValueOrDefault(setting)
                : null;

        /// <summary>
        /// Finds the setting that a name in <paramref name="tree"/> refers to, and whether anything
        /// was reported about it, such as a setting that another module does not export. The
        /// setting may be one the file's own module declares, one a <c>.use</c> brought in, or one
        /// named with its module's path.
        /// </summary>
        public (Setting? Setting, bool Reported) Find(SyntaxTree tree, NameExpressionSyntax name, Action<SyntaxNode, DiagnosticMessage> report)
        {
            var parts = name.Names;
            if (parts.Length == 0 || !modules.TryGetValue(tree, out var own))
                return (null, false);

            Setting? found;
            if (parts.Length == 1)
            {
                if (byName.TryGetValue((own, parts[0].Text), out var local))
                    return (local, false);
                found = Used(tree, parts[0].Text);
            }
            else
            {
                var module = string.Join("::", parts.SkipLast(1).Select(part => part.Text));
                found = byName.GetValueOrDefault((module, parts[^1].Text));
            }
            if (found is null)
                return (null, false);
            if (!found.IsExported && found.Tree != tree)
            {
                report(name, Catalogue.NotExported.Message(name.GetText().Trim(), modules[found.Tree]));
                return (null, true);
            }
            return (found, false);
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

        private static string ModuleOf(SyntaxTree tree)
        {
            foreach (var child in tree.Root.Members)
            {
                if (child is LineSyntax { Statement: ModuleDirectiveSyntax module })
                    return string.Join("::", module.Name.Names.Select(part => part.Text));
            }
            return "";
        }

        /// <summary>Returns the names in a file's <c>.export</c> lists, which may include settings.</summary>
        private static HashSet<string> ExportedNames(SyntaxTree tree)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in tree.Root.Members)
            {
                if (child is not LineSyntax { Statement: ExportDirectiveSyntax export })
                    continue;
                foreach (var item in export.Items)
                {
                    if (item.Name.SimpleName is { Kind: SyntaxKind.Identifier } name)
                        names.Add(name.Text);
                }
            }
            return names;
        }

        /// <summary>
        /// Returns the setting that a <c>.use</c> in <paramref name="tree"/> brings in as
        /// <paramref name="name"/>. The <c>.use</c> may name the setting alone or in braces, under
        /// its own name or an alias, or bring in everything a module exports.
        /// </summary>
        private Setting? Used(SyntaxTree tree, string name)
        {
            foreach (var child in tree.Root.Members)
            {
                if (child is not LineSyntax { Statement: UseDirectiveSyntax use })
                    continue;
                var path = use.Path.Names.Select(part => part.Text).ToList();
                if (use.StarToken is not null)
                {
                    if (byName.GetValueOrDefault((string.Join("::", path), name)) is { IsExported: true } everything)
                        return everything;
                    continue;
                }
                if (use.OpenBraceToken is not null)
                {
                    foreach (var item in use.Items)
                    {
                        var alias = (item.Alias ?? item.Name).Text;
                        if (alias == name && byName.GetValueOrDefault((string.Join("::", path), item.Name.Text)) is { } listed)
                            return listed;
                    }
                    continue;
                }
                if (path.Count < 2)
                    continue;
                var brought = use.Alias?.Text ?? path[^1];
                if (brought == name && byName.GetValueOrDefault((string.Join("::", path.SkipLast(1)), path[^1])) is { } one)
                    return one;
            }
            return null;
        }
    }

    /// <summary>
    /// Represents one <c>.config</c>, with where it is declared, the value its file gives it, and
    /// the value the build sets.
    /// </summary>
    private sealed class Setting(SyntaxTree tree, SyntaxToken name, ExpressionSyntax? expression, bool isExported)
    {
        public SyntaxTree Tree { get; } = tree;

        public SyntaxToken Name { get; } = name;

        public ExpressionSyntax? Expression { get; } = expression;

        public bool IsExported { get; } = isExported;

        /// <summary>The value the build sets, which replaces the file's, or null when it sets none.</summary>
        public long? Given { get; set; }
    }
}
