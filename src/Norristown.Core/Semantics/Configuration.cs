using System.Runtime.CompilerServices;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Determines which <c>.if</c> branches a build takes, and therefore which parts of the program
/// exist at all.
/// <para>
/// A condition tests what the configuration alone decides, and never the program. It may use
/// literals, operators, the built-ins that measure nothing, settings, and the constants,
/// functions and enum members declared at file level, outside every block, from those. This
/// lets every condition be evaluated here, before any declaration has been collected. Which
/// declarations exist follows from the configuration alone, so the same name may be declared
/// under two conditions and only one of them is real. A check that depends on the program is an
/// <c>.assert</c>, which is evaluated last.
/// </para>
/// </summary>
public sealed class Configuration
{
    // A file's settings, wherever they appear, read once per tree.
    private static readonly ConditionalWeakTable<SyntaxTree, List<ConstantDeclarationSyntax>> settingsByTree = new();

    private readonly Dictionary<SyntaxTree, List<TextSpan>> omitted;
    private readonly HashSet<(SyntaxTree Tree, int Position)> answered;
    private readonly Table table;

    private Configuration(
        Dictionary<SyntaxTree, List<TextSpan>> omitted, HashSet<(SyntaxTree, int)> answered, Cpu cpu, Table table)
    {
        this.omitted = omitted;
        this.answered = answered;
        this.table = table;
        Cpu = cpu;
    }

    /// <summary>Gets a build that leaves nothing out, for a caller with no conditions to resolve.</summary>
    public static Configuration Everything { get; } = new([], [], ProgramCpu.Default, Table.Empty);

    /// <summary>Gets the CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</summary>
    public Cpu Cpu { get; }

    /// <summary>
    /// Determines which branches <paramref name="trees"/> take when built for
    /// <paramref name="cpu"/>, with the values the build gives its settings in
    /// <paramref name="given"/>.
    /// </summary>
    public static Configuration Resolve(
        IEnumerable<SyntaxTree> trees, Cpu cpu, IEnumerable<SettingValue> given, List<Diagnostic> diagnostics)
    {
        var all = trees.ToList();
        var table = Table.Read(all, cpu, given, diagnostics);
        var evaluator = table.ConditionEvaluator(diagnostics);
        var omitted = new Dictionary<SyntaxTree, List<TextSpan>>();
        var answered = new HashSet<(SyntaxTree, int)>();
        foreach (var tree in all)
        {
            var left = new List<TextSpan>();
            new Reader(tree, evaluator, diagnostics, left, answered).Container(tree.Root);
            if (left.Count > 0)
                omitted[tree] = left;
        }
        return new Configuration(omitted, answered, cpu, table);
    }

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
    /// Determines whether a setting is where its placement allows it, which is at file level,
    /// outside every block, whether or not it is exported. A cheap local is never a setting,
    /// because the build could not name it.
    /// </summary>
    internal static bool IsWellPlaced(ConstantDeclarationSyntax setting) =>
        setting.Name.Kind != SyntaxKind.CheapLocal && !SyntaxFacts.SettingPlacement.IsBarredBy(SyntaxFacts.NestingOf(setting));

    /// <summary>
    /// Determines whether this configuration answers the conditions of <paramref name="tree"/> as
    /// <paramref name="other"/> does. An edit to one file changes how another file's conditions
    /// are answered only through what the configuration decides, and when it does not, the other
    /// file's analysis still holds.
    /// </summary>
    internal bool AnswersAlike(Configuration other, SyntaxTree tree) =>
        Omitted(tree).SequenceEqual(other.Omitted(tree))
        && answered.Where(at => at.Tree == tree).ToHashSet().SetEquals(other.answered.Where(at => at.Tree == tree));

    /// <summary>
    /// Returns the value the build gives the setting <paramref name="name"/> that
    /// <paramref name="tree"/> declares, or null when it declares none by that name or its value
    /// is unknown.
    /// </summary>
    internal long? SettingOf(SyntaxTree tree, string name) => table.SettingOf(tree, name);

    /// <summary>
    /// Returns what the configuration alone makes of the constant, setting or enum member that
    /// <paramref name="tree"/> declares at file level as <paramref name="name"/>, or null when it
    /// declares none there that a condition could test.
    /// </summary>
    internal Decision? DecisionOf(SyntaxTree tree, string name) => table.DecisionOf(tree, name);

    /// <summary>
    /// Determines whether the build gives a value to the setting <paramref name="name"/> that
    /// <paramref name="tree"/> declares, rather than leaving it at its default.
    /// </summary>
    public bool Gives(SyntaxTree tree, string name) => table.Gives(tree, name);

    /// <summary>
    /// Returns whether the configuration alone decides the constant, setting or enum member that
    /// <paramref name="tree"/> declares at file level as <paramref name="name"/>, so that an
    /// <c>.if</c> may test it. Returns null when <paramref name="tree"/> declares no such value
    /// there, as for one declared under an <c>.if</c> or inside a block.
    /// </summary>
    public bool? DecidesValue(SyntaxTree tree, string name) => DecisionOf(tree, name) is { } decision ? decision.Why is null : null;

    /// <summary>
    /// Determines whether the configuration decided a value that <paramref name="tree"/>
    /// declares, such as a setting, or a constant a condition tested. Any file's conditions may
    /// read such a value.
    /// </summary>
    internal bool Decides(SyntaxTree tree) => table.Decides(tree);

    /// <summary>
    /// Returns the single token that makes up an argument, such as a CPU name, a number, or a
    /// name with a single component. Returns null when the argument is anything more.
    /// </summary>
    private static SyntaxToken? Alone(SyntaxNode argument) =>
        argument is NameExpressionSyntax name ? name.SimpleName
        : argument.ChildTokens is [var only] ? only
        : null;

    private static List<ConstantDeclarationSyntax> SettingsIn(SyntaxTree tree) =>
        settingsByTree.GetValue(tree, tree => [.. tree.Root.DescendantNodes().OfType<ConstantDeclarationSyntax>()
            .Where(constant => constant.IsSetting)]);

    /// <summary>Returns the names that the <c>.export .use</c> items of a file re-export.</summary>
    private static IEnumerable<ProgramSymbols.Reexport> Reexports(SyntaxTree tree) =>
        UsesIn(tree).Where(use => use.IsExported).SelectMany(ModuleSyntax.Brought);

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
        // How many branches of a chain the walk is inside. The outermost branch has already been
        // searched for `.cpu`, all the way down, so a branch inside it is not searched again.
        private int branches;

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
                if (!included)
                {
                    Leave(block);
                    continue;
                }
                var branch = block.Opener.Statement is IfDirectiveSyntax or ElseIfDirectiveSyntax or ElseDirectiveSyntax;
                if (branch)
                    branches++;
                Container(block);
                if (branch)
                    branches--;
            }
        }

        private static string Directive(StatementSyntax opener) =>
            opener is ElseDirectiveSyntax ? ".else" : ".elseif";

        private void Report(TextSpan span, DiagnosticMessage message) =>
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
            if (branches == 0)
            {
                foreach (var node in block.DescendantNodes())
                {
                    if (node is CpuDirectiveSyntax && SyntaxFacts.PlacementOf(DirectiveKind.Cpu).IsBarredBy(DirectiveNesting.Condition))
                        Report(node.Span, Catalogue.CpuUnderACondition);
                }
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

            var value = evaluator.Evaluate(condition);
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
    }

    /// <summary>
    /// Represents what the configuration alone decides in a program: its settings, and the
    /// constants, functions and enum members declared at file level, outside every block, from
    /// them. It holds a symbol for every name at file level, so that a name a condition uses is
    /// looked up as the binder would look it up, and one the configuration does not decide is
    /// reported with the reason.
    /// <para>
    /// A setting is declared with <c>?=</c> at file level, outside every block, so that which
    /// settings a program has depends on no condition. The build sets it by the path of the
    /// module that declares it, or by its name alone where no other module declares a setting of
    /// that name. A value is worked out when something first asks for it, and then kept.
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
    /// A name that is not a value, such as a routine or data, is recorded only so that a
    /// condition that uses it can say what it is.
    /// </description></item>
    /// </list>
    /// </summary>
    private sealed class Table
    {
        private readonly Cpu cpu;
        private readonly ProgramSymbols program;
        private readonly Dictionary<SyntaxTree, Scope> scopes;
        private readonly Dictionary<Symbol, Entry> entries;
        private readonly Dictionary<Symbol, long> given = [];
        private readonly Dictionary<Symbol, Decision> decided = [];
        private readonly HashSet<Symbol> evaluating = [];
        private readonly Dictionary<SyntaxTree, Reach> reached = [];

        // Receives what is wrong with a setting, which nothing reads before the binder does.
        private readonly List<Diagnostic> problems;

        private Table(
            Cpu cpu, ProgramSymbols program, Dictionary<SyntaxTree, Scope> scopes, Dictionary<Symbol, Entry> entries,
            List<Diagnostic> problems)
        {
            this.cpu = cpu;
            this.program = program;
            this.scopes = scopes;
            this.entries = entries;
            this.problems = problems;
        }

        /// <summary>Gets a table that declares nothing.</summary>
        public static Table Empty { get; } = new(ProgramCpu.Default, ProgramSymbols.Empty, [], [], []);

        /// <summary>
        /// Reads what <paramref name="trees"/> declare at file level, together with the values the
        /// build gives the settings. Each setting is worked out here, so that a problem with it is
        /// reported whether or not anything reads it.
        /// </summary>
        public static Table Read(IReadOnlyList<SyntaxTree> trees, Cpu cpu, IEnumerable<SettingValue> given, List<Diagnostic> diagnostics)
        {
            var scopes = new Dictionary<SyntaxTree, Scope>();
            var entries = new Dictionary<Symbol, Entry>();
            var modules = new List<ProgramSymbols.Module>();
            foreach (var tree in trees)
            {
                var scope = new Scope(ScopeKind.File, null, null, null) { Module = ModuleSyntax.ModuleOf(tree) };
                scopes[tree] = scope;
                foreach (var setting in SettingsIn(tree))
                {
                    if (!IsWellPlaced(setting))
                        diagnostics.Add(new Diagnostic(tree.GetSpan(setting.EqualsToken.Span), Catalogue.SettingMisplaced));
                }
                new Declarer(tree, ExportedNames(tree), entries).Container(tree.Root, scope, UndecidedCause.Measurement);
                modules.Add(new ProgramSymbols.Module(
                    tree, scope.Module, default, scope, [.. scope.Symbols.Where(symbol => symbol.IsExported)],
                    [.. Reexports(tree)]));
            }

            // Problems with the modules themselves are the binder's to report.
            var table = new Table(cpu, ProgramSymbols.Build(modules, []), scopes, entries, diagnostics);
            table.Give(given, diagnostics);
            foreach (var (symbol, entry) in entries)
            {
                if (entry is Entry.Setting)
                    table.Decide(symbol);
            }
            return table;
        }

        /// <summary>
        /// Creates an evaluator for the conditions of a build, whose names refer to this table and
        /// which reports into <paramref name="diagnostics"/>.
        /// </summary>
        public Evaluator ConditionEvaluator(List<Diagnostic> diagnostics) =>
            Evaluator.ForConditions(
                new Conditions(
                    cpu, name => Name(name, null, diagnostics), Call, (node, name, why) => diagnostics.Add(Undecided(node, name, why))),
                diagnostics);

        /// <summary>Determines whether the build gives the setting <paramref name="name"/> in <paramref name="tree"/> a value.</summary>
        public bool Gives(SyntaxTree tree, string name) =>
            scopes.TryGetValue(tree, out var scope) && scope.FindMember(name) is { } symbol && given.ContainsKey(symbol);

        /// <summary>Determines whether a value <paramref name="tree"/> declares has been decided.</summary>
        public bool Decides(SyntaxTree tree) => decided.Keys.Any(symbol => symbol.Tree == tree);

        /// <summary>Returns the value of the setting <paramref name="name"/> in <paramref name="tree"/>, or null.</summary>
        public long? SettingOf(SyntaxTree tree, string name) =>
            scopes.TryGetValue(tree, out var scope) && scope.FindMember(name) is { } symbol
                && entries.GetValueOrDefault(symbol) is Entry.Setting
                ? Decide(symbol).Value.AsNumber()
                : null;

        /// <summary>
        /// Returns what the configuration makes of the constant, setting or enum member
        /// <paramref name="name"/> that <paramref name="tree"/> declares at file level, or null.
        /// </summary>
        public Decision? DecisionOf(SyntaxTree tree, string name) =>
            scopes.TryGetValue(tree, out var scope) && scope.FindMember(name) is { } symbol
                && entries.GetValueOrDefault(symbol) is Entry.Setting or Entry.Constant or Entry.Member
                ? Decide(symbol)
                : null;

        /// <summary>
        /// Returns the diagnostic for a value a condition uses that the configuration does not
        /// decide. <paramref name="name"/> is the name the condition wrote, or null where it wrote a
        /// measurement.
        /// </summary>
        private static Diagnostic Undecided(SyntaxNode node, string? name, Undecided why)
        {
            var subject = $"`{name ?? node.GetText().Trim()}`";
            DiagnosticMessage message = why.Cause switch
            {
                UndecidedCause.Measurement => Catalogue.ConditionUsesAMeasurement.Message(subject),
                UndecidedCause.Conditional => Catalogue.ConditionUsesAConditionalDeclaration.Message(
                    subject, $"under another `.if`; declare `{why.Root}` once, with `.select`"),
                UndecidedCause.InBlock => Catalogue.ConditionUsesAConditionalDeclaration.Message(
                    subject, $"inside a block; declare `{why.Root}` at file level"),
                UndecidedCause.Unknown when why.Notes.IsEmpty => Catalogue.NotDeclared.Message(name ?? subject, ""),
                _ => Catalogue.ConditionNamesTheProgram.Message(subject),
            };
            return new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message, why.Notes);
        }

        /// <summary>
        /// Returns the note that says what <paramref name="measurement"/> measures, as the reason
        /// the configuration does not decide <paramref name="owner"/>.
        /// </summary>
        private static RelatedSpan Measures(Symbol owner, SyntaxNode measurement)
        {
            var (what, with) = measurement is CallExpressionSyntax { Function: { } function } call
                ? (string.Join(", ", call.Arguments.Arguments.Select(argument => argument.GetText().Trim())), function.Text)
                : (measurement.GetText().Trim(), "a measurement");
            return new RelatedSpan(owner.DeclarationSpan,
                $"`{owner.DisplayName}` measures `{what}` with `{with}`");
        }

        /// <summary>Returns the names in a file's <c>.export</c> lists.</summary>
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
        /// Records the values the build gives settings. A value named with a module's path sets that
        /// module's setting, and a value named alone sets the one setting of that name.
        /// </summary>
        private void Give(IEnumerable<SettingValue> values, List<Diagnostic> diagnostics)
        {
            var settings = entries.Where(pair => pair.Value is Entry.Setting).Select(pair => pair.Key).ToList();
            foreach (var value in values)
            {
                Symbol? symbol;
                if (value.HasPath)
                {
                    var at = value.Name.LastIndexOf("::", StringComparison.Ordinal);
                    symbol = program.ModuleNamed(value.Name[..at])?.FileScope.FindMember(value.Name[(at + 2)..]);
                    if (symbol is not null && entries.GetValueOrDefault(symbol) is not Entry.Setting)
                        symbol = null;
                }
                else
                {
                    var named = settings.Where(setting => setting.Name == value.Name).ToList();
                    if (named.Count > 1)
                    {
                        var paths = named.Select(setting => $"`{setting.Scope.Module}::{setting.Name}`").Order(StringComparer.Ordinal);
                        diagnostics.Add(new Diagnostic(value.Declaration,
                            Catalogue.SettingAmbiguous.Message(value.Name, string.Join(" or ", paths))));
                        continue;
                    }
                    symbol = named.SingleOrDefault();
                }
                if (symbol is null)
                    diagnostics.Add(new Diagnostic(value.Declaration, Catalogue.SettingUnknown.Message(value.Name)));
                else
                    given[symbol] = value.Value;
            }
        }

        /// <summary>
        /// Returns what the configuration makes of <paramref name="symbol"/>, working it out the
        /// first time it is asked for. A value on a cycle is unknown here, and the binder reports
        /// the cycle.
        /// </summary>
        private Decision Decide(Symbol symbol)
        {
            if (decided.TryGetValue(symbol, out var known))
                return known;
            if (!entries.TryGetValue(symbol, out var entry))
                return Decision.Not(new Undecided(UndecidedCause.Unknown, [], symbol.Name));
            if (!evaluating.Add(symbol))
            {
                // Nothing but this table evaluates a setting, so a setting on a cycle is reported
                // here. A constant on one is reported by the binder.
                if (entry is Entry.Setting)
                {
                    problems.Add(new Diagnostic(symbol.DeclarationSpan, Catalogue.DefinedInTermsOfItself.Message(symbol.Name)));
                    decided[symbol] = Decision.Of(Value.Unknown);
                }
                return Decision.Of(Value.Unknown);
            }
            var decision = entry switch
            {
                Entry.Setting setting => Setting(symbol, setting),
                Entry.Constant constant => Probe(constant.Value, symbol, null, null),
                Entry.Member member => Member(symbol, member),
                Entry.Marker marker => Decision.Not(new Undecided(
                    marker.Cause, [new RelatedSpan(symbol.DeclarationSpan, $"`{symbol.DisplayName}` {marker.Note}")], symbol.DisplayName)),
                _ => Decision.Not(new Undecided(
                    UndecidedCause.Place,
                    [new RelatedSpan(symbol.DeclarationSpan, $"`{symbol.DisplayName}` is a function")],
                    symbol.DisplayName)),
            };
            evaluating.Remove(symbol);
            decided[symbol] = decision;
            return decision;
        }

        /// <summary>
        /// Returns the value of a setting, which is the value the build gives it, or else its
        /// default. A default the configuration does not decide, or one that is text, is reported.
        /// </summary>
        private Decision Setting(Symbol symbol, Entry.Setting setting)
        {
            var declaration = setting.Declaration;
            var value = Probe(declaration.Value, symbol, null, problems);
            var at = declaration.Tree.GetSpan(declaration.Value.Span);
            if (value.Why is { } why)
                problems.Add(new Diagnostic(at, Severity.Error, Catalogue.SettingDefaultUndecided.Message(symbol.Name), why.Notes));
            else if (value.Value.IsString)
                problems.Add(new Diagnostic(at, Catalogue.SettingIsText));
            return given.TryGetValue(symbol, out var set) ? Decision.Of(Value.Of(set))
                : value.Value.AsNumber() is { } number ? Decision.Of(Value.Of(number))
                : Decision.Of(Value.Unknown);
        }

        /// <summary>
        /// Returns the value of an enum member, which is its own value, or one more than the member
        /// before it in the branches the build takes. One more than the largest value nt65 holds is
        /// unknown here, and the evaluator reports it as an overflow.
        /// </summary>
        private Decision Member(Symbol symbol, Entry.Member member)
        {
            Decision? previous = null;
            Symbol? before = null;
            Decision? found = null;
            void Walk(SyntaxNode container)
            {
                foreach (var (child, included) in ConditionChain.Walk(container.ChildNodes, 0, (block, already) => !already && Holds(block)))
                {
                    if (child is BlockSyntax { BlockKind: BlockKind.If } branch)
                    {
                        if (included)
                            Walk(branch);
                        continue;
                    }
                    if (child is not LineSyntax { Statement: EnumMemberSyntax { Name: { IsMissing: false } name } line })
                        continue;
                    var at = member.Body.FindMember(name.Text) ?? symbol;
                    var decision = line.Value is { } value ? Probe(value, at, null, null)
                        : previous is not { } last ? Decision.Of(Value.Of(0))
                        : last.Why is { } why ? Decision.Not(why.Through(at.DeclarationSpan, at.DisplayName, before!.DisplayName))
                        : last.Value.AsNumber() is { } number and < long.MaxValue ? Decision.Of(Value.Of(number + 1))
                        : Decision.Of(Value.Unknown);
                    if (name.Text == symbol.Name)
                        found ??= decision;
                    previous = decision;
                    before = at;
                }
            }
            Walk(member.Enum);
            return found ?? Decision.Of(Value.Unknown);
        }

        /// <summary>
        /// Determines whether the condition on a branch inside an enum holds. The pass over the
        /// file's conditions reports any problem with it, so nothing is reported here.
        /// </summary>
        private bool Holds(BlockSyntax block)
        {
            if (block.Opener.Statement is ElseDirectiveSyntax)
                return true;
            if (block.Opener.Statement is not ConditionalDirectiveSyntax { Condition: var condition })
                return false;
            var evaluator = Evaluator.ForConditions(new Conditions(cpu, name => Name(name, null, null), Call, (_, _, _) => { }), []);
            return evaluator.Evaluate(condition).AsNumber() is { } number && number != 0;
        }

        /// <summary>
        /// Evaluates <paramref name="expression"/>, the value of <paramref name="owner"/>, with the
        /// names in it referring to this table and to <paramref name="parameters"/>. The first thing
        /// the configuration does not decide becomes the reason <paramref name="owner"/> is not
        /// decided. Problems are reported into <paramref name="report"/>, or dropped when it is
        /// null, because the binder evaluates a constant again and reports them itself.
        /// </summary>
        private Decision Probe(
            ExpressionSyntax expression, Symbol owner, IReadOnlyDictionary<string, Value>? parameters, List<Diagnostic>? report)
        {
            Undecided? reason = null;
            void Undecided(SyntaxNode node, string? name, Undecided why) =>
                reason ??= name is not null ? why.Through(owner.DeclarationSpan, owner.DisplayName, name)
                    : why.Notes.IsEmpty ? why with { Notes = [Measures(owner, node)], Root = owner.DisplayName }
                    : why;
            var evaluator = Evaluator.ForConditions(
                new Conditions(cpu, name => Name(name, parameters, null), Call, Undecided), report ?? []);
            var value = evaluator.Evaluate(expression);
            return reason is { } because ? Decision.Not(because) : Decision.Of(value);
        }

        /// <summary>
        /// Returns what the configuration makes of a name, or null when a problem with it has been
        /// reported. A parameter of the function being evaluated has its argument's value.
        /// </summary>
        private Decision? Name(NameExpressionSyntax name, IReadOnlyDictionary<string, Value>? parameters, List<Diagnostic>? report)
        {
            if (parameters is not null && name.Names is [var only] && parameters.TryGetValue(only.Text, out var argument))
                return Decision.Of(argument);
            var (symbol, reported) = Find(name, report);
            if (reported)
                return null;
            return symbol is null
                ? Decision.Not(new Undecided(UndecidedCause.Unknown, [], name.GetText().Trim()))
                : Decide(symbol);
        }

        /// <summary>
        /// Returns what the configuration makes of a call to a function the program declares, with
        /// the values of its arguments. An argument the configuration does not decide has already
        /// been passed on, so the call is only unknown.
        /// </summary>
        private Decision? Call(CallExpressionSyntax call, IReadOnlyList<Value> arguments)
        {
            if (call.Callee is not { } callee)
                return Decision.Of(Value.Unknown);
            var (symbol, reported) = Find(callee, null);
            if (reported)
                return null;
            if (symbol is null)
                return Decision.Not(new Undecided(UndecidedCause.Unknown, [], callee.GetText().Trim()));
            if (entries.GetValueOrDefault(symbol) is not Entry.Function function)
                return Decide(symbol);
            var names = function.Declaration.Parameters?.Parameters.Select(parameter => parameter.Name.Text).ToList() ?? [];
            if (names.Count != arguments.Count || arguments.Any(value => value.Kind == ValueKind.Unknown) || !evaluating.Add(symbol))
                return Decision.Of(Value.Unknown);
            var bound = new Dictionary<string, Value>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
                bound[names[i]] = arguments[i];
            var decision = Probe(function.Declaration.Body, symbol, bound, null);
            evaluating.Remove(symbol);
            return decision;
        }

        /// <summary>
        /// Finds the symbol that a name in its file refers to, and whether anything was reported
        /// about it, such as a name that another module does not export. The name is looked for as
        /// the binder looks for it: declared in the file, brought in by a <c>.use</c>, or named with
        /// a module's path.
        /// </summary>
        private (Symbol? Symbol, bool Reported) Find(NameExpressionSyntax name, List<Diagnostic>? report)
        {
            var place = Place(name);

            // A name that two `.use module::*` items bring in is ambiguous. The binder reports
            // that, so the name has no value here and nothing more is said about it.
            if (place is { IsReported: true })
                return (null, true);
            if (place?.Symbol is not { } symbol)
                return (null, false);
            if (!symbol.IsExported && symbol.Tree != name.Tree)
            {
                report?.Add(new Diagnostic(name.Tree.GetSpan(name.Span),
                    Catalogue.NotExported.Message(name.GetText().Trim(), symbol.Module)));
                return (null, true);
            }
            return (symbol, false);
        }

        /// <summary>Returns what a name in its file leads to, looked for as the binder looks for a name, or null.</summary>
        private Resolution? Place(NameExpressionSyntax name)
        {
            var parts = name.Names;
            if (parts.Length == 0 || !scopes.TryGetValue(name.Tree, out var own))
                return null;
            var text = parts[0].Text;
            var start = name.GlobalToken is not null ? Semantics.Lookup.ModuleRoot(text, program)
                : First(name.Tree, own, text, last: parts.Length == 1);
            return Semantics.Lookup.Walk(start, [.. parts.Select(part => part.Text)], program);
        }

        /// <summary>
        /// Returns what the first part of a name means in <paramref name="tree"/>. The order is the
        /// binder's, which is the file's own declarations, then what a <c>.use</c> brought in, and
        /// then the modules and what a <c>.use module::*</c> brought in.
        /// </summary>
        private Resolution? First(SyntaxTree tree, Scope own, string text, bool last)
        {
            if (own.FindMember(text) is { } local)
                return new Resolution(local);
            var reach = ReachOf(tree);
            if (reach.Brought.TryGetValue(text, out var brought))
                return brought.IsReported ? null : brought;
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

                // A `.use` whose path leads nowhere here still brings the name in, so that the
                // name does not go on to mean what a `*` brought in.
                foreach (var item in ModuleSyntax.Brought(use))
                    brought.TryAdd(item.Name, Walk(item.Path) ?? Resolution.Reported);
            }
            return reached[tree] = new Reach(brought, globs);
        }

        /// <summary>Returns where a <c>.use</c> path leads from the root of the modules, or null when it leads nowhere.</summary>
        private Resolution? Walk(IReadOnlyList<string> path) =>
            path.Count == 0 ? null : Semantics.Lookup.Walk(Semantics.Lookup.ModuleRoot(path[0], program), path, program);
    }

    /// <summary>
    /// Declares, in the scopes of a <see cref="Table"/>, what one file declares at file level. A
    /// value declared under an <c>.if</c> or inside a block is recorded with the reason a
    /// condition cannot use it, and a declaration that is not a value with what it is.
    /// </summary>
    private sealed class Declarer(SyntaxTree tree, HashSet<string> exported, Dictionary<Symbol, Entry> entries)
    {
        /// <summary>
        /// Declares what <paramref name="container"/> holds in <paramref name="scope"/>.
        /// <paramref name="under"/> is <see cref="UndecidedCause.Conditional"/> inside an
        /// <c>.if</c>, <see cref="UndecidedCause.InBlock"/> inside another block, and
        /// <see cref="UndecidedCause.Measurement"/> at file level, where a value may be decided.
        /// </summary>
        public void Container(SyntaxNode container, Scope scope, UndecidedCause under)
        {
            var opener = (container as BlockSyntax)?.Opener;
            foreach (var child in container.ChildNodes)
            {
                if (child is LineSyntax { Statement: { } statement } line && line != opener)
                    Statement(statement, scope, under);
                else if (child is BlockSyntax block)
                    Block(block, scope, under);
            }
        }

        private static Entry Value(ExpressionSyntax value, UndecidedCause under) =>
            under == UndecidedCause.Measurement ? new Entry.Constant(value) : Where(under);

        private static Entry.Marker Where(UndecidedCause under) =>
            under == UndecidedCause.Conditional
                ? new Entry.Marker(
                    UndecidedCause.Conditional, "is declared under an `.if`")
                : new Entry.Marker(UndecidedCause.InBlock, "is declared inside a block");

        private static SyntaxToken? Named(StatementSyntax opener) => opener switch
        {
            ProcDeclarationSyntax { Name: { IsMissing: false } name } => name,
            MacroDeclarationSyntax { Name: { IsMissing: false } name } => name,
            DataDeclarationSyntax { Name: { IsMissing: false } name } => name,
            TypeDeclarationSyntax { Name: { IsMissing: false } name } => name,
            _ => null,
        };

        private static string? What(BlockKind kind) => kind switch
        {
            BlockKind.Proc => "is a routine",
            BlockKind.Macro => "is a macro",
            BlockKind.Data => "is data",
            BlockKind.Struct or BlockKind.Union => "is a type",
            BlockKind.Charmap => "is a charmap",
            BlockKind.List => "is a list",
            _ => null,
        };

        private void Statement(StatementSyntax statement, Scope scope, UndecidedCause under)
        {
            switch (statement)
            {
                case ConstantDeclarationSyntax { Name: { IsMissing: false, Kind: not SyntaxKind.CheapLocal } name } constant:
                    if (constant.IsSetting && under == UndecidedCause.Measurement && IsWellPlaced(constant))
                        Declare(name, scope, constant.IsExported, new Entry.Setting(constant));
                    else if (!constant.IsSetting)
                        Declare(name, scope, constant.IsExported, Value(constant.Value, under));
                    break;
                case FuncDeclarationSyntax { Name: { IsMissing: false } name } function:
                    Declare(name, scope, function.IsExported,
                        under == UndecidedCause.Measurement ? new Entry.Function(function) : Where(under));
                    break;
                case DataDeclarationSyntax { Name: { IsMissing: false } name } data:
                    Declare(name, scope, data.IsExported, new Entry.Marker(UndecidedCause.Place, "is data"));
                    break;
                case ExternProcDeclarationSyntax { Name: { IsMissing: false } name } proc:
                    Declare(name, scope, proc.IsExported, new Entry.Marker(UndecidedCause.Place, "is a routine"));
                    break;
                case SignatureDeclarationSyntax { Name: { IsMissing: false } name } signature:
                    Declare(name, scope, signature.IsExported, new Entry.Marker(UndecidedCause.Place, "is a signature set"));
                    break;
            }
        }

        private void Block(BlockSyntax block, Scope scope, UndecidedCause under)
        {
            var opener = block.Opener.Statement;
            switch (block.BlockKind)
            {
                case BlockKind.Region or BlockKind.Segment:
                    Container(block, scope, under);
                    break;
                case BlockKind.If:
                    Container(block, scope, under == UndecidedCause.InBlock ? under : UndecidedCause.Conditional);
                    break;
                case BlockKind.Enum when opener is EnumDeclarationSyntax @enum:
                    Enum(block, @enum, scope, under);
                    break;
                case BlockKind.Scope when opener is ScopeDeclarationSyntax { Name: { IsMissing: false } name } named:
                    var body = new Scope(ScopeKind.Scope, name.Text, scope, null);
                    if (Declare(name, scope, named.IsExported, new Entry.Marker(UndecidedCause.Place, "is a scope")) is { } owner)
                    {
                        owner.Body = body;
                        body.Owner = owner;
                    }
                    Container(block, body, UndecidedCause.InBlock);
                    break;
                default:
                    if (Named(opener) is { } declared && What(block.BlockKind) is { } what)
                        Declare(declared, scope, opener.IsExported, new Entry.Marker(UndecidedCause.Place, what));
                    break;
            }
        }

        /// <summary>
        /// Declares an enum and its members. A named enum is a scope of its own, and an anonymous
        /// one declares its members in the scope around it.
        /// </summary>
        private void Enum(BlockSyntax block, EnumDeclarationSyntax @enum, Scope scope, UndecidedCause under)
        {
            var body = scope;
            if (@enum.Name is { IsMissing: false } name)
            {
                body = new Scope(ScopeKind.Type, name.Text, scope, null);
                if (Declare(name, scope, @enum.IsExported, new Entry.Marker(UndecidedCause.Place, "is an enum")) is { } owner)
                {
                    owner.Body = body;
                    body.Owner = owner;
                }
            }
            foreach (var member in block.DescendantNodes().OfType<EnumMemberSyntax>())
            {
                if (member.Name is { IsMissing: false } memberName)
                {
                    Declare(memberName, body, @enum.IsExported,
                        under == UndecidedCause.Measurement ? new Entry.Member(block, body) : Where(under));
                }
            }
        }

        private Symbol? Declare(SyntaxToken name, Scope scope, bool isExported, Entry entry)
        {
            var symbol = new Symbol(name.Text, SymbolKind.Constant, scope, tree, name.Span)
            {
                IsExported = isExported || scope.Kind == ScopeKind.File && exported.Contains(name.Text),
            };
            if (scope.Declare(symbol) is not null)
                return null;
            entries[symbol] = entry;
            return symbol;
        }
    }

    /// <summary>
    /// Represents what the configuration may know of a name declared at file level: a setting,
    /// a constant, a function or an enum member it may decide, or a marker that says why it cannot.
    /// </summary>
    private abstract record Entry
    {
        /// <summary>Represents a setting, declared with <c>?=</c>.</summary>
        public sealed record Setting(ConstantDeclarationSyntax Declaration) : Entry;

        /// <summary>Represents a constant declared at file level, outside every block.</summary>
        public sealed record Constant(ExpressionSyntax Value) : Entry;

        /// <summary>Represents a function declared at file level, outside every block.</summary>
        public sealed record Function(FuncDeclarationSyntax Declaration) : Entry;

        /// <summary>Represents a member of the enum <paramref name="Enum"/>, whose members are declared in <paramref name="Body"/>.</summary>
        public sealed record Member(BlockSyntax Enum, Scope Body) : Entry;

        /// <summary>Represents a name the configuration does not decide, with the reason and what the note says of it.</summary>
        public sealed record Marker(UndecidedCause Cause, string Note) : Entry;
    }

    /// <summary>
    /// Represents what the <c>.use</c> items of one file bring in, which are names and the
    /// modules whose exports a <c>.use module::*</c> brings in.
    /// </summary>
    /// <param name="Brought">The names brought in one by one, and where each leads.</param>
    /// <param name="Globs">The modules whose exports a <c>.use module::*</c> brings in.</param>
    private sealed record Reach(Dictionary<string, Resolution> Brought, List<ProgramSymbols.Module> Globs);
}
