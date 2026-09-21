using System.Runtime.CompilerServices;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which <c>.if</c> branches a build takes, and so which of the program is there at all.
/// <para>
/// A condition tests the build configuration and never the program: it may use literals,
/// operators, built-in functions, defines and <c>.config</c> settings, and nothing else a file
/// declares. That is what
/// lets every condition be answered here, before a single declaration has been collected —
/// which declarations exist follows from the configuration alone, so the same name may be
/// declared under two conditions and only one of them is real. A check that depends on the
/// program is an <c>.assert</c>, which is evaluated last.
/// </para>
/// </summary>
public sealed class Configuration
{
    // A file's `.config` items wherever they are written, read once per tree.
    private static readonly ConditionalWeakTable<SyntaxTree, List<ConfigDeclarationSyntax>> written = new();

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

    /// <summary>A build that leaves nothing out, for a caller with no conditions to resolve.</summary>
    public static Configuration Everything { get; } = new([], [], ProgramCpu.Default, new Settings());

    /// <summary>The CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</summary>
    public Cpu Cpu { get; }

    /// <summary>
    /// Works out which branches <paramref name="trees"/> take when built for
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
    /// Whether <paramref name="tree"/> writes a <c>.config</c> anywhere, whose value any file's
    /// conditions may read.
    /// </summary>
    public static bool DeclaresSettings(SyntaxTree tree) => SettingsIn(tree).Count > 0;

    /// <summary>
    /// Whether this pass answered the condition on <paramref name="block"/>. It answers every
    /// one it can reach before a declaration is looked up; the ones it cannot are inside a
    /// macro body, a <c>.repeat</c> or an <c>.each</c>, where a condition may name what the
    /// expansion binds, and those are answered once per expansion instead.
    /// </summary>
    public bool Answered(BlockSyntax block) => answered.Contains((block.Tree, block.Position));

    /// <summary>Whether the build includes what is written at <paramref name="node"/>.</summary>
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
    /// The branches of <paramref name="tree"/> this build leaves out, for an editor to dim.
    /// A branch inside one that is already left out is not listed again.
    /// </summary>
    public IReadOnlyList<TextSpan> Omitted(SyntaxTree tree) =>
        omitted.TryGetValue(tree, out var left) ? left : [];


    /// <summary>
    /// What <c>.target(cpu)</c> or <c>.has(mnemonic)</c> answers for a build for
    /// <paramref name="cpu"/>, or null when <paramref name="name"/> is neither. What is wrong
    /// with the argument goes to <paramref name="report"/>.
    /// </summary>
    internal static Value? AboutTheCpu(
        string name, SyntaxToken function, IReadOnlyList<SyntaxNode> given, Cpu cpu,
        Action<TextSpan, DiagnosticMessage> report)
    {
        switch (name)
        {
            case ".target":
                if (given.Count != 1 || Alone(given[0]) is not { } written || CpuNames.Parse(written.Text) is not { } named)
                {
                    report(function.Span, Catalogue.TargetArgument.Says(CpuNames.Listed));
                    return Value.Unknown;
                }
                return Value.Of(named == cpu);

            // Whether the CPU has an instruction, whichever it is: a program that runs on more
            // than one asks this rather than listing the CPUs that have it.
            case ".has":
                if (given.Count != 1 || given[0] is not NameExpressionSyntax { SimpleName: { Kind: SyntaxKind.Mnemonic } mnemonic })
                {
                    report(function.Span, Catalogue.HasArgument);
                    return Value.Unknown;
                }
                return Value.Of(Layout.Instructions.Writable(cpu, mnemonic.Text));

            default:
                return null;
        }
    }

    /// <summary>
    /// The one token an argument is written as — a CPU name, a number, or a name of a single
    /// component — or null where it is written as anything more.
    /// </summary>
    private static SyntaxToken? Alone(SyntaxNode argument) =>
        argument is NameExpressionSyntax name ? name.SimpleName
        : argument.ChildTokens is [var only] ? only
        : null;

    /// <summary>Whether a statement is written at file level, outside every block, exported or not.</summary>
    internal static bool AtFileLevel(StatementSyntax statement) =>
        statement.Parent?.FirstAncestorOrSelf<LineSyntax>()?.Parent?.Parent is null;

    /// <summary>
    /// What the build makes the <c>.config</c> <paramref name="name"/> that <paramref name="tree"/>
    /// declares, or null when it declares none by that name or its value is unknown.
    /// </summary>
    internal long? SettingOf(SyntaxTree tree, string name) => settings.ValueOf(tree, name);

    /// <summary>
    /// The configuration of a program in which <paramref name="before"/> became
    /// <paramref name="after"/>. A condition depends on nothing but the file it is in, the
    /// build and the settings, and a file that declares a setting is analyzed with the whole
    /// program, so every other file's answers stand.
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
    /// The defines a name in a file may be, by name. A define written with a module's path is a
    /// <c>.config</c> the build sets instead, and no name in a file.
    /// </summary>
    private static Dictionary<string, long> Plain(IEnumerable<Define> defines)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var define in defines.Where(define => !define.IsSetting))
            values[define.Name] = define.Value;
        return values;
    }

    private static List<ConfigDeclarationSyntax> SettingsIn(SyntaxTree tree) =>
        written.GetValue(tree, tree => [.. tree.Root.DescendantNodes().OfType<ConfigDeclarationSyntax>()]);

    /// <summary>
    /// Reads one file's conditions. A chain is a run of sibling blocks: the <c>.if</c> that
    /// starts it, then whichever <c>.elseif</c>s and <c>.else</c> continue it. The first
    /// branch whose condition holds is the one the build takes; the conditions after it are
    /// never evaluated, so nothing is reported about a branch that is not there.
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
                // has no answer until there is an expansion to answer it for.
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
                            Report(opener.Span, Catalogue.ElseWithoutIf.Says(Directive(opener)));
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
        /// One branch of a chain: whether the build takes it. <paramref name="already"/> says
        /// an earlier branch was taken, in which case this one is left out whatever it says.
        /// </summary>
        private bool Branch(BlockSyntax block, StatementSyntax opener, bool already)
        {
            // The CPU is configuration, and a condition may test it with `.target`, so a
            // `.cpu` under an `.if` would be deciding what decides it.
            foreach (var node in block.DescendantNodes())
            {
                if (node is CpuDirectiveSyntax)
                    Report(node.Span, Catalogue.CpuUnderACondition);
            }

            var take = !already && Holds(opener);
            if (take)
                Container(block);
            else
                Leave(block);
            return take;
        }

        /// <summary>Whether the condition an <c>.if</c> or <c>.elseif</c> writes holds.</summary>
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

        // From the block's full start, indentation included, to its closing brace: `Includes`
        // compares where a node starts, and an indented block starts at its line's leading
        // whitespace, before its first token.
        private void Leave(BlockSyntax block) =>
            omitted.Add(new TextSpan(block.Position, block.Span.End - block.Position));

        private static string Directive(StatementSyntax opener) =>
            opener is ElseDirectiveSyntax ? ".else" : ".elseif";
    }

    /// <summary>
    /// The <c>.config</c> items of a program: in-file defines, each its module's own. One is
    /// written at file level outside every block, so that which settings exist depends on no
    /// condition, and its value may use literals, built-ins, the build's defines and other
    /// settings. The build sets one a module exports by its qualified name, which makes what the
    /// file writes a default.
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
        /// Every setting <paramref name="trees"/> declare, with what the build sets, each
        /// evaluated once so that what is wrong with one is said whether or not anything reads it.
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
                    // A setting is known by its name, so a line that writes none declares nothing.
                    if (declaration.Name is not { IsMissing: false } name)
                        continue;
                    if (!Configuration.AtFileLevel(declaration))
                    {
                        diagnostics.Add(new Diagnostic(tree.GetSpan(declaration.Keyword.Span),
                            Catalogue.ConfigMisplaced));
                        continue;
                    }
                    settings.byName.TryAdd((module, name.Text), new Setting(tree, name, declaration.Value,
                        declaration.IsExported || exported.Contains(name.Text)));
                }
            }

            // What the build says about a setting is written with the module's path, as the
            // setting is named from outside the module.
            foreach (var define in set.Where(define => define.IsSetting))
            {
                var at = define.Name.LastIndexOf("::", StringComparison.Ordinal);
                var key = (define.Name[..at], define.Name[(at + 2)..]);
                if (!settings.byName.TryGetValue(key, out var setting))
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingUnknown.Says(define.Name)));
                }
                else if (!setting.IsExported)
                {
                    diagnostics.Add(new Diagnostic(define.Declaration,
                        Catalogue.SettingNotExported.Says(define.Name, key.Item1)));
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
        /// The same settings, read from a file that <paramref name="before"/> became. Neither
        /// declares a setting, or the whole program would be read again, so every value stands.
        /// </summary>
        public Settings Replacing(SyntaxTree before, SyntaxTree after)
        {
            var moved = new Dictionary<SyntaxTree, string>(modules);
            moved.Remove(before);
            moved[after] = ModuleOf(after);
            return new Settings(byName, moved, values) { readerFor = readerFor };
        }

        /// <summary>
        /// An evaluator for the conditions of a build for <paramref name="cpu"/> with
        /// <paramref name="defines"/>, whose names are these settings and which reports into
        /// <paramref name="diagnostics"/>.
        /// </summary>
        public Evaluator EvaluatorFor(Cpu cpu, Dictionary<string, long> defines, List<Diagnostic> diagnostics) =>
            Evaluator.ForConditions(new Conditions(cpu, defines, Lookup), diagnostics);

        /// <summary>
        /// What a name written in a condition is worth as a setting, or null when it names
        /// none and nothing was reported about it, which leaves it to be reported as naming
        /// nothing at all.
        /// </summary>
        public Value? Lookup(NameExpressionSyntax name, Action<SyntaxNode, DiagnosticMessage> report)
        {
            var (setting, reported) = Find(name.Tree, name, report);
            if (setting is not null)
                return Worth(setting) is { } value ? Value.Of(value) : Value.Unknown;
            return reported ? Value.Unknown : null;
        }

        /// <summary>What the setting <paramref name="name"/> in <paramref name="tree"/> is worth, or null.</summary>
        public long? ValueOf(SyntaxTree tree, string name) =>
            modules.TryGetValue(tree, out var module) && byName.TryGetValue((module, name), out var setting)
                ? values.GetValueOrDefault(setting)
                : null;

        /// <summary>
        /// The setting a name written in <paramref name="tree"/> stands for: one its own module
        /// declares, one a <c>.use</c> brought in, or one written with its module's path. Whether
        /// anything was reported about it, such as a setting another module keeps to itself.
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
                report(name, Catalogue.NotExported.Says(name.GetText().Trim(), modules[found.Tree]));
                return (null, true);
            }
            return (found, false);
        }

        /// <summary>What a setting is worth: what the build sets, or else what its file writes.</summary>
        public long? Worth(Setting setting)
        {
            if (values.TryGetValue(setting, out var known))
                return known;
            var reader = readerFor!(setting.Tree);
            if (evaluating.Contains(setting))
            {
                reader.Report(setting.Name.Span, Catalogue.DefinedInTermsOfItself.Says(setting.Name.Text));
                values[setting] = null;
                return null;
            }
            evaluating.Add(setting);
            var written = setting.Expression is { } expression ? reader.Evaluate(expression) : Value.Unknown;
            evaluating.Remove(setting);
            if (written.IsString && setting.Expression is { } text)
                reader.Report(text.Span, Catalogue.ConfigIsText);
            if (values.ContainsKey(setting))
                return values[setting];
            return values[setting] = setting.Given ?? written.AsNumber();
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

        /// <summary>The names a file's <c>.export</c> lists name, which a setting may be among.</summary>
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
        /// The setting a <c>.use</c> in <paramref name="tree"/> brings in as <paramref name="name"/>:
        /// named alone or in braces, under its own name or another, or with everything a module exports.
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

    /// <summary>One <c>.config</c>: where it is written, what its file gives it, and what the build sets.</summary>
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
