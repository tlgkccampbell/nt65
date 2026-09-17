using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Writes one file's ca65. The output is readable, with the source's own spacing kept
/// and its comments dropped, and it is deterministic: the same source always gives the same
/// bytes.
/// <para>
/// Everything the output depends on is written into it. The header fixes the CPU and
/// switches off every ca65 option that changes syntax; every segment carries its address
/// size; every addressing mode that could be read two ways carries its prefix; and character
/// and string data is written as bytes, so no ca65 command line can change what it means.
/// </para>
/// </summary>
public sealed class Emitter
{
    /// <summary>Where a generated comment starts, so that a column of them lines up.</summary>
    private const int CommentColumn = 36;

    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly FlatNames names;
    private readonly List<Diagnostic> diagnostics;
    private readonly string source;
    private readonly string output;
    private readonly StringBuilder generated = new();
    private readonly List<int> lineBytes = [];
    private readonly List<string?> segmentStack = [];
    private readonly HashSet<Symbol> exported = [];

    // What the file measures with `.endof` or `.spanof`, and so what needs a label just past
    // its last byte. A use may come before the thing it measures, so they are found up front.
    private readonly HashSet<Symbol> ends = [];

    // What other files measure, whose ends this file exports and those files import.
    private readonly IReadOnlySet<Symbol> measuredElsewhere;

    // The width the previous 65816 immediate of each register was written at, in output
    // order, which is exactly what ca65's setting is when the next one is reached.
    private readonly Dictionary<WidthRegister, int> widths = [];
    // The segment being written, or null before any region or block names one.
    private string? segment;

    // The routine being written, which is what a generated label is named after.
    private Symbol? routine;
    private string? written;
    private bool pendingBlank;
    private int depth;

    // Which turn of which repetitions, and which expansion of which macros, is being
    // written. A body is written once per writing, with every name the level binds standing
    // for what it is worth there.
    private Expansion? expansion;

    // The line an expansion's output maps back to. The lines of an expansion map to the line
    // of the call, the way a C debugger treats a preprocessor macro, and only a line of this
    // file can be named: `.dbg file` declares one file, and a body may belong to another.
    private SyntaxNode? callLine;

    private Emitter(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string source, string output, IReadOnlySet<Symbol> measuredElsewhere)
    {
        this.measuredElsewhere = measuredElsewhere;
        this.model = model;
        this.layout = layout;
        this.names = names;
        this.diagnostics = diagnostics;
        this.source = source;
        this.output = output;
    }

    /// <summary>
    /// The ca65 for <paramref name="model"/>'s file. <paramref name="outRoot"/> is the
    /// project's output tree, or null for the project's root. <paramref name="measuredElsewhere"/>
    /// is what the program's other files measure with <c>.endof</c> and <c>.spanof</c>.
    /// </summary>
    public static OutputFile Emit(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string? outRoot = null, IReadOnlySet<Symbol>? measuredElsewhere = null)
    {
        var path = OutputPath(model, outRoot);
        var emitter = new Emitter(model, layout, names, diagnostics,
            model.Tree.Path, path, measuredElsewhere ?? new HashSet<Symbol>());
        emitter.Ends();
        emitter.Header();
        emitter.Exports();
        emitter.Imports();
        emitter.WalkContainer(model.Tree.Root);
        return new OutputFile(path, emitter.generated.ToString(), emitter.lineBytes) { Source = model.Tree.Path };
    }

    /// <summary>
    /// Where a file's output goes: <c>.module gfx::sprite</c> is written to <c>gfx/sprite.s</c>
    /// under the project's output tree, wherever the source is, so a source can move, or live
    /// outside the project, without its output moving. A file that says no module, which is
    /// already an error, is named after its source.
    /// </summary>
    public static string OutputPath(SemanticModel model, string? outRoot = null)
    {
        var path = model.FileScope.Module is { } module
            ? module.Replace("::", "/", StringComparison.Ordinal) + ".s"
            : Paths.Normalized(model.Tree.Path).Split('/')[^1] is var name
                && name.EndsWith(".nt65", StringComparison.OrdinalIgnoreCase) ? name[..^5] + ".s" : name + ".s";
        return string.IsNullOrEmpty(outRoot) || outRoot == "." ? path : $"{outRoot.TrimEnd('/')}/{path}";
    }

    /// <summary>A number as the output writes it: hexadecimal, at the width it is used at.</summary>
    private static string Hex(long value, int digits) =>
        "$" + value.ToString($"x{digits}", CultureInfo.InvariantCulture);

    /// <summary>
    /// A known value written in place of the name that stood for it, at the narrowest width
    /// that holds it. A negative number is written in decimal: hexadecimal for one would be
    /// sixteen digits of a width nt65 never meant.
    /// </summary>
    private static string Constant(long value) => value < 0
        ? value.ToString(CultureInfo.InvariantCulture)
        : Hex(value, value < 0x100 ? 2 : value < 0x10000 ? 4 : 8);

    /// <summary>The first and last tokens under a node, in source order.</summary>
    private static List<SyntaxToken> Tokens(SyntaxNode node)
    {
        var tokens = new List<SyntaxToken>();
        Collect(node, tokens);
        return tokens;

        static void Collect(SyntaxNode node, List<SyntaxToken> into)
        {
            var nodes = node.ChildNodes;
            var tokens = node.ChildTokens;
            int i = 0, j = 0;
            while (i < nodes.Length || j < tokens.Length)
            {
                if (j >= tokens.Length || (i < nodes.Length && nodes[i].Position < tokens[j].Position))
                    Collect(nodes[i++], into);
                else
                    into.Add(tokens[j++]);
            }
        }
    }

    /// <summary>
    /// The routines and data declarations this file measures. The end of <c>f</c> is
    /// written <c>f__end</c>, and that spelling is fixed, because other files and
    /// hand-written ca65 refer to it once it is exported — so a name that collides with it
    /// is an error rather than a rename.
    /// </summary>
    private void Ends()
    {
        var own = Extents.MeasuredIn(model).Concat(measuredElsewhere)
            .Where(symbol => symbol.Tree == model.Tree)
            .Distinct()
            .OrderBy(s => s.NameSpan.Start);
        foreach (var measured in own)
        {
            ends.Add(measured);
            var end = EndOf(measured);
            if (names.Claimed(end) is { } other)
            {
                diagnostics.Add(new Diagnostic(measured.DeclarationSpan, Severity.Error,
                    $"`{other.QualifiedName}` and the end of `{measured.QualifiedName}` both become "
                    + $"`{end}` in the output", [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(end);
        }

        foreach (var type in model.Symbols.Where(symbol => symbol.Tree == model.Tree && IsSized(symbol)))
        {
            var size = SizeOf(type);
            if (names.Claimed(size) is { } other)
            {
                diagnostics.Add(new Diagnostic(type.DeclarationSpan, Severity.Error,
                    $"`{other.QualifiedName}` and the size of `{type.QualifiedName}` both become `{size}` in the output",
                    [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(size);
        }
    }

    /// <summary>The label just past a symbol's last byte, which is what <c>.endof</c> stands for.</summary>
    private string EndOf(Symbol symbol) => Named(symbol) + "__end";

    /// <summary>
    /// The constant an exported struct or union's size is exported as, so that ca65 and C code
    /// can size what they allocate by it. Like an end, its spelling is fixed.
    /// </summary>
    private string SizeOf(Symbol type) => Named(type) + "__sizeof";

    /// <summary>Whether <paramref name="symbol"/> is a struct or union this file exports the size of.</summary>
    private static bool IsSized(Symbol symbol) => symbol is { IsExported: true, IsLayout: true, Size: not null };

    /// <summary>Writes the end label of whatever <paramref name="declaration"/> names, if it has one.</summary>
    private void End(SyntaxNode? declaration)
    {
        if (declaration is null)
            return;
        foreach (var token in declaration.ChildTokens)
        {
            if (token.Kind is not (SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic))
            {
                continue;
            }
            if (model.SymbolAt(token) is { } symbol && ends.Contains(symbol))
            {
                Segment();
                Flush();
                Line(LabelText(EndOf(symbol)));
            }
            return;
        }
    }

    /// <summary>
    /// The header: the CPU, smart mode off, case-sensitive symbols, and every
    /// <c>.feature</c> that changes syntax switched off, so that no ca65 command line can
    /// change what the file means.
    /// </summary>
    private void Header()
    {
        Line($"; Generated by nt65 from {source}. Do not edit.");
        Line($".setcpu \"{CpuNames.SpellForCa65(layout.Cpu)}\"");
        Line(".smart -");
        Line(".case +");
        Line(".feature at_in_identifiers -, bracket_as_indirect -, c_comments -");
        Line(".feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -");
        Line(".feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -");
        Line(".feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -");
        Line(".feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -");

        // The size lets ld65 check that the source has not changed under the debug file; the
        // timestamp is zero, so the output does not depend on when the file was written.
        Line($".dbg file, \"{source}\", {Encoding.UTF8.GetByteCount(model.Tree.Text)}, 0");
    }

    /// <summary>
    /// Every export, under its linker name and with the address size nt65 gives it, hoisted
    /// to the top of the file in the order the file declares them.
    /// <para>
    /// A macro is no symbol to the linker: what crosses is its expansion, written into whichever
    /// module called it. A charmap, a function and a list are used by value, and what crosses
    /// is the values, written where they are used. A scope and a type are only the way to
    /// their members, which are exported one by one, a type's as the flat constants they
    /// become. An import is somebody else's, and every module that uses one imports it.
    /// </para>
    /// </summary>
    private void Exports()
    {
        var any = false;
        foreach (var symbol in model.Symbols)
        {
            if (IsSized(symbol) && symbol.Tree == model.Tree)
            {
                if (!any)
                    Blank();
                any = true;
                Line(Linked(".export", Implicit(Value.Of(symbol.Size!.Value).ImpliedAddressSize()), SizeOf(symbol)));
            }
            if (!symbol.IsExported || !ProgramSymbols.IsLinked(symbol))
                continue;
            if (!any)
                Blank();
            any = true;
            exported.Add(symbol);

            // A constant is as wide as its value, which is how ca65 sizes one it is given.
            var size = symbol.ExportSize
                ?? Implicit(symbol.IsAddress ? symbol.AddressSize : symbol.Value.ImpliedAddressSize());
            Line(Linked(".export", size, Named(symbol)));

            // Another module that measures the declaration names its end, which goes with it.
            if (measuredElsewhere.Contains(symbol))
                Line(Linked(".export", size, EndOf(symbol)));
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>
    /// Everything the file gets from outside it: what another nt65 file exports and
    /// this one names, and what an <c>.import</c> item declares and the file uses. Each carries
    /// the address size nt65 gives it, so ca65 sizes an operand the way nt65 did. Nothing the
    /// file does not use is imported, because an import pulls the module that defines it out
    /// of a library.
    /// <para>
    /// A constant is not imported: ca65 cannot use an imported symbol where it needs a value,
    /// so the constant is written out here instead. A checked import is both — the value nt65
    /// uses, and an assertion that the definition it will be linked against agrees.
    /// </para>
    /// </summary>
    private void Imports()
    {
        var any = false;
        var used = model.Used.ToHashSet();
        foreach (var symbol in model.Symbols
            .Where(symbol => symbol.Kind is SymbolKind.ImportedAddress or SymbolKind.ImportedConstant && used.Contains(symbol))
            .Concat(model.ExternalSymbols))
        {
            if (Import(symbol) is not { } line)
                continue;
            if (!any)
                Blank();
            any = true;
            Line(line);

            // A checked import is checked by every module that uses it, since each was built
            // against the value.
            if (symbol is { Kind: SymbolKind.ImportedConstant } && symbol.Value.AsNumber() is { } checkedValue)
            {
                Line($".assert {Named(symbol)} = {Constant(checkedValue)}, lderror, "
                    + $"\"{Named(symbol)} is not {Constant(checkedValue)}, which is what "
                    + $"{source} was built against\"");
            }
        }

        // The end of what this file measures in another file comes from that file, which
        // exports it beside the declaration.
        foreach (var measured in Extents.MeasuredIn(model).Where(symbol => symbol.Tree != model.Tree)
            .OrderBy(symbol => Named(symbol), StringComparer.Ordinal))
        {
            if (!any)
                Blank();
            any = true;
            Line(Linked(".import", Implicit(measured.AddressSizeIn(model.Tree)), EndOf(measured)));
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>
    /// An <c>.export</c> or <c>.import</c> of <paramref name="name"/>, with the address size
    /// <paramref name="size"/>: <c>.exportzp</c>, <c>.export name: far</c> or plain.
    /// </summary>
    private static string Linked(string directive, AddressSize? size, string name) => size switch
    {
        AddressSize.ZeroPage => $"{directive}zp {name}",
        AddressSize.Absolute => $"{directive} {name}: abs",
        AddressSize.Far => $"{directive} {name}: far",
        _ => $"{directive} {name}",
    };

    /// <summary>
    /// A size as a line that states none has it: ca65 takes an import, and an export of an
    /// address, as absolute unless told otherwise.
    /// </summary>
    private static AddressSize? Implicit(AddressSize? size) => size == AddressSize.Absolute ? null : size;

    /// <summary>The line that brings one symbol in, or null for one that needs no line at all.</summary>
    private string? Import(Symbol symbol)
    {
        var name = Named(symbol);
        if (symbol.IsAddress || symbol.Kind == SymbolKind.ImportedConstant)
            return Linked(".import", Implicit(symbol.AddressSizeIn(model.Tree)), name);

        // A constant another file declares, written out by value. One whose value nt65
        // does not know has already been reported, and a string is only ever used through
        // `.strlen` and `.strat`, which are numbers before anything is written.
        return symbol.Value.AsNumber() is { } value ? $"{name} = {Constant(value)}" : null;
    }

    private void WalkContainer(SyntaxNode container) => Walk(container.ChildNodes, from: 0);

    /// <summary>
    /// A run of sibling lines and blocks. The <c>.if</c> chains among them are resolved here,
    /// because a chain is a run of siblings and only whoever walks them can see it.
    /// </summary>
    private void Walk(IReadOnlyList<SyntaxNode> children, int from)
    {
        var chain = new ConditionChain();
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Green is not GreenBlock block)
            {
                chain.Break();
                WalkLine(child);
                continue;
            }
            if (chain.Includes(model, child, expansion))
                WalkBlock(child, block.BlockKind);
        }
    }

    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        var lines = block.ChildNodes;
        var opener = lines.Length > 0 ? lines[0].Statement : null;

        // A macro body is written at every call that expands it, and nothing at all where it
        // stands.
        if (kind == BlockKind.Macro)
            return;

        // A block argument is the call's, not a block of its own: the line that opens it is
        // the call, which is written out here, and its lines are written wherever the body
        // splices them.
        if (kind == BlockKind.MacroBlock)
        {
            if (opener is not null && Macros.CallIn(opener) is not null)
                WalkLine(lines[0]);
            return;
        }

        // A branch the build takes is written out where it stands, with nothing said about
        // the `.if` around it; one it leaves out is not written at all. The block adds no
        // nesting of its own, so a segment block inside it is nested only if the `.if` was.
        if (kind == BlockKind.If)
        {
            Walk(lines, from: 1);
            return;
        }

        // A repetition is unrolled here: its body is written once per turn, and the `.repeat`
        // itself never reaches ca65. Nothing is reported from here, because layout walked the
        // same turns and has already said what is wrong with the count or the list.
        if (Constructs.Repeats(kind))
        {
            var outerTurn = expansion;
            foreach (var turn in Repetitions.Of(model, block, outerTurn, null))
            {
                expansion = turn;
                Walk(lines, from: 1);
            }
            expansion = outerTurn;
            return;
        }

        // A structure, a union, a list and a character mapping say what something means
        // without generating anything. An exported layout is the exception: its members
        // travel as flat constants, so the file that declares them has to define them.
        if (kind is BlockKind.List or BlockKind.Charmap)
            return;
        if (kind is BlockKind.Struct or BlockKind.Union)
        {
            Offsets(lines[0], opener);
            return;
        }

        // An enum writes its members out as the constants they are.
        if (kind == BlockKind.Enum)
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Statement is { Kind: SyntaxKind.EnumMember } member)
                    EnumMember(lines[i], member);
            }
            pendingBlank = true;
            return;
        }

        // One record written over several lines is written where its line is, as one
        // directive per member; the lines of values are the record's, and write nothing.
        if (kind == BlockKind.RecordInitializer)
        {
            WalkLine(lines[0]);
            return;
        }

        // A segment block anywhere but the file's own top level is a detour from the stream
        // around it, which is what `.pushseg` and `.popseg` say. A region is at file level.
        var nested = depth > 0;
        var pushed = false;
        var outerSegment = segment;
        var placing = kind is BlockKind.Segment or BlockKind.Region;
        if (placing && opener is not null)
        {
            var name = Constructs.SegmentOf(opener) ?? segment;
            if (nested)
            {
                Blank();
                Line(".pushseg");
                segmentStack.Add(segment);
                pushed = true;
                written = null;
            }
            segment = name;
        }
        else if (kind == BlockKind.Proc && opener is { Kind: SyntaxKind.ProcDeclaration })
        {
            routine = ProcLabel(lines[0], opener);
        }
        else if (opener is not null && kind != BlockKind.Scope)
        {
            WalkLine(lines[0]);
        }

        var outerRoutine = routine;
        depth++;
        Walk(lines, from: 1);
        depth--;
        if (kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody)
            End(opener);
        routine = outerRoutine;

        if (pushed)
        {
            Line(".popseg");
            segment = segmentStack[^1];
            segmentStack.RemoveAt(segmentStack.Count - 1);
            written = segment;
            pendingBlank = true;
        }
        else if (placing)
        {
            segment = outerSegment;
        }
    }

    private void WalkLine(SyntaxNode line)
    {
        if (line.Statement is not { } statement)
            return;

        switch (statement.Kind)
        {
            case SyntaxKind.BlankLine:
                pendingBlank = true;
                break;

            case SyntaxKind.LabeledLine:
                LabeledLine(line, statement);
                break;

            case SyntaxKind.ConstantDeclaration:
                Constant(line, statement);
                break;

            // Data that names itself stands where its first byte does, and ends where its last
            // byte does when anything measures it.
            case SyntaxKind.DataDeclaration:
                Declared(line, statement);
                if (line.Green is not GreenLine { OpensBlockKind: BlockKind.Data or BlockKind.DataBody })
                    End(statement);
                break;

            case SyntaxKind.DataDirective when DataSyntax.IsElementType(statement):
                Elements(line, statement, statement, symbol: null);
                break;

            case SyntaxKind.DataDirective when layout.Of(statement, expansion) is null:
                NotTranspiled(statement);
                break;

            case SyntaxKind.DataValues:
                Values(line, statement);
                break;

            case SyntaxKind.InstructionStatement
                when statement.ChildTokens.Length > 0
                    && SyntaxFacts.LongBranches.Contains(statement.ChildTokens[0].Text)
                    && layout.Of(statement, expansion) is { } laid:
                Branch(line, statement, laid);
                break;

            case SyntaxKind.InstructionStatement:
            case SyntaxKind.DataDirective:
                Source(line, statement, layout.Of(statement, expansion)?.Length ?? 0);
                break;

            case SyntaxKind.ExternProcDeclaration:
                ExternProc(line, statement);
                break;

            // An assertion nt65 answered has been answered; one it could not depends on where
            // things land, so it is written out for ld65 to check, with the level ca65 needs
            // for that. An `.error` the build reached has already been reported, and never
            // reaches ca65.
            case SyntaxKind.AssertDirective
                when Constructs.AssertionOf(statement).Condition is { } condition
                    && model.ValueOf(condition, expansion, layout.SpanOf).AsNumber() is null:
                Linked(line, statement, condition);
                break;

            case SyntaxKind.AssertDirective:
            case SyntaxKind.ErrorDirective:
                break;

            // A function, and the lines of the blocks above, exist for the analysis: a call is
            // written as its body, and a type as the constants it names.
            case SyntaxKind.FuncDeclaration:
            case SyntaxKind.SignatureDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.CharmapEntry:
            case SyntaxKind.ListItems:
            case SyntaxKind.MemberValue:
                break;

            case SyntaxKind.MacroCall:
                Expand(line, statement);
                break;

            case SyntaxKind.EnsureDirective:
                Ensure(line, statement);
                break;

            case SyntaxKind.BlockSplice:
                Splice(statement);
                break;

            // A `.cpu` item, a segment declaration, an `.export` or `.import` (both already
            // written) and a closing brace all say something about the file without
            // generating anything.
            default:
                break;
        }
    }

    /// <summary>
    /// A call, written out as the body it expands to, with a comment naming it. nt65 expands
    /// macros itself and emits flat code: ca65's own <c>.macro</c> is never used, so nt65's
    /// macro semantics never depend on ca65's.
    /// </summary>
    private void Expand(SyntaxNode line, SyntaxNode call)
    {
        if (model.MacroAt(call) is not { Definition: { } definition })
        {
            NotTranspiled(call);
            return;
        }

        // Expansions that went past the bound are an error already, and writing them out
        // would take as long as the laying out was spared.
        if (layout.ExpansionsExceeded)
            return;

        // A macro that reaches itself is an error already, and has nothing to write.
        if (Expansion.Expanding(expansion, definition))
            return;

        Segment();
        Flush();

        // The comment sits where the call was written. A call after a label starts at column
        // zero, where a comment would read as belonging to nothing, so it takes the body's
        // own indentation instead.
        var indent = Indent(line.Statement ?? call);

        // The `{` of a trailing block belongs to the block rather than to the call, and the
        // comment names the call.
        var written = call.GetText().Trim().TrimEnd('{').TrimEnd();
        Line($"{(indent.Length == 0 ? "    " : indent)}; {written}  {Where(call)}");

        var outerCall = callLine;
        var outer = expansion;

        // Every line of the expansion maps to the call, and a call inside a body maps to the
        // outermost call, which is the line of this file that asked for all of it.
        callLine ??= line;
        expansion = Expansion.Of(outer, call, definition);
        Walk(definition.ChildNodes, from: 1);
        expansion = outer;
        callLine = outerCall;
    }

    /// <summary>
    /// A line naming a <c>block</c> parameter, which writes out the lines the call gave it.
    /// Those are the caller's own code, so where they are this file's own lines they map back
    /// to themselves rather than to the call that spliced them.
    /// </summary>
    private void Splice(SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0
            || model.SymbolAt(statement.ChildTokens[0]) is not { Parameter: { } parameter }
            || model.ArgumentFor(parameter.Symbol, expansion) is not { Block: { } block })
        {
            return;
        }

        var outerCall = callLine;
        var outer = expansion;
        if (block.Tree == model.Tree)
            callLine = null;
        expansion = Expansion.Spliced(outer, statement, block);
        Walk(Macros.LinesOf(block), from: 0);
        expansion = outer;
        callLine = outerCall;
    }

    /// <summary>Where a call was written, as the comment before its expansion names it.</summary>
    private string Where(SyntaxNode call)
    {
        return $"{call.Tree.Path}:{call.LineIndex + 1}";
    }

    /// <summary>A <c>.proc</c> becomes its label; the signature says nothing to ca65.</summary>
    private Symbol? ProcLabel(SyntaxNode line, SyntaxNode opener)
    {
        foreach (var token in opener.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && model.SymbolAt(token) is { } reference)
            {
                Code(line, Indent(opener) + LabelText(Named(reference)), 0);
                return reference;
            }
        }
        return null;
    }

    /// <summary>
    /// A long branch, written as the form nt65 chose for it: the plain short branch where
    /// the target is in reach, and otherwise the opposite branch over a <c>jmp</c> to a
    /// generated label. ca65's own package can only ever write the long form forwards,
    /// because it chooses without knowing where the target lands.
    /// </summary>
    private void Branch(SyntaxNode line, SyntaxNode statement, LineLayout laid)
    {
        var mnemonic = statement.ChildTokens[0];
        var (taken, skipped) = Instructions.FormsOf(mnemonic.Text);
        var edits = new Edits();
        Substitute(statement, edits, nested: false);

        if (!laid.Inverted)
        {
            edits.Replace[mnemonic.Position] = taken;
            Code(line, Render(statement, edits), laid.Length);
            return;
        }

        var over = names.Generated((routine is null ? "" : Named(routine) + "__") + "over");
        edits.Replace[mnemonic.Position] = "jmp";
        var jump = Render(statement, edits);
        Code(line, $"{Indent(statement)}{skipped} {over}", Instructions.Length(AddressingMode.Relative));
        Line(jump, Instructions.Length(AddressingMode.Absolute));
        Line($"{over}:");
    }

    private void LabeledLine(SyntaxNode line, SyntaxNode statement)
    {
        var label = statement.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.Label);
        var rest = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.Label);

        // An element type after a label is written as it is anywhere, with the label in front.
        if (rest is { Kind: SyntaxKind.DataDirective } && DataSyntax.IsElementType(rest))
        {
            Elements(line, statement, rest, label is { ChildTokens.Length: > 0 } ? model.SymbolAt(label.ChildTokens[0]) : null);
            return;
        }

        // A label on a call names what the expansion emits, so the label comes first and the
        // expansion follows it.
        if (rest is { Kind: SyntaxKind.MacroCall })
        {
            LabelOnly(line, statement, label);
            Expand(line, rest);
            return;
        }
        if (rest is { Kind: SyntaxKind.DataDirective } && layout.Of(rest, expansion) is null)
        {
            NotTranspiled(rest);
            return;
        }
        var bytes = rest is null ? 0 : layout.Of(rest, expansion)?.Length ?? 0;
        if (rest is not null)
            Width(rest);
        var edits = new Edits();
        if (rest is not null)
        {
            Substitute(rest, edits, nested: false);
            Slot(rest, edits);
            Direct(rest, edits);
        }

        if (label is not { ChildTokens.Length: > 0 }
            || model.SymbolAt(label.ChildTokens[0]) is not { } reference)
        {
            Code(line, Render(statement, edits), bytes);
            return;
        }

        // `z := *` and `f := *`: ca65 reads `z:` at the start of a line as an address-size
        // prefix, so such a label is written as an assignment instead. An assignment
        // takes the whole line, so whatever followed the label goes on the next one.
        var text = LabelText(Named(reference));
        if (rest is not null && !text.EndsWith(':'))
        {
            Code(line, Indent(statement) + text, 0);
            Code(line, Indent(statement) + Render(rest, edits).TrimStart(), bytes);
            return;
        }

        edits.Replace[label.ChildTokens[0].Position] = text;
        if (label.ChildTokens.Length > 1)
            edits.Replace[label.ChildTokens[1].Position] = "";
        Code(line, Render(statement, edits), bytes);
    }

    /// <summary>
    /// A data declaration: its name, and what it holds where the line holds it. Mixed data,
    /// and an array whose values are in a body, write the name here and their bytes a line at
    /// a time below it.
    /// </summary>
    private void Declared(SyntaxNode line, SyntaxNode declaration)
    {
        if (DataSyntax.DeclaredName(declaration) is not { } name || model.SymbolAt(name) is not { } symbol)
            return;
        if (DataSyntax.ElementOf(declaration) is not { } element)
        {
            Code(line, Indent(declaration) + LabelText(Named(symbol)), 0);
            return;
        }
        if (DataSyntax.IsElementType(element))
        {
            Elements(line, declaration, element, symbol);
            return;
        }

        // Bytes such as an `.incbin`, written as the source wrote them.
        if (layout.Of(element, expansion) is not { } laid)
        {
            NotTranspiled(element);
            return;
        }
        var edits = new Edits();
        Substitute(element, edits, nested: false);
        WithName(line, declaration, element, symbol, Bare(element, edits, out var comment), laid.Length, comment);
    }

    /// <summary>
    /// An element type, named or not: its values as the directive of its type, or the room it
    /// takes as zeros. Values in a body are written a line at a time, below the name.
    /// </summary>
    private void Elements(SyntaxNode line, SyntaxNode statement, SyntaxNode directive, Symbol? symbol)
    {
        if (DataSyntax.BodyOf(directive) is { Green: GreenBlock { BlockKind: BlockKind.DataBody } })
        {
            if (symbol is not null)
                Code(line, Indent(statement) + LabelText(Named(symbol)), 0);
            return;
        }
        if (DataSyntax.TypeOf(directive) is { } named)
        {
            if (model.SymbolOf(named) is { IsLayout: true } type)
                Records(line, statement, directive, symbol, type);
            else
                NotTranspiled(directive);
            return;
        }
        if (layout.Of(directive, expansion) is not { } laid)
        {
            NotTranspiled(directive);
            return;
        }

        // A list is written without its braces, as the directive's own values are.
        var edits = new Edits();
        string text;
        string? comment = null;
        if (DataSyntax.BracedOf(directive) is { Kind: SyntaxKind.ValueList } list)
        {
            var (width, bigEndian) = Slot(directive);
            foreach (var value in list.ChildNodes)
                InPlace(value, width, bigEndian, edits);
            foreach (var brace in list.ChildTokens.Where(token => token.Kind is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace))
                edits.Replace[brace.Position] = "";
            text = $"{ForCa65(directive.ChildTokens[0].Text)} {Bare(list, edits, out comment)}";
        }
        else if (DataSyntax.ValuesOf(directive).Count > 0)
        {
            Substitute(directive, edits, nested: false);
            text = Bare(directive, edits, out comment);
        }
        else
        {
            text = $".res {laid.Length}";
        }
        WithName(line, statement, directive, symbol, text, laid.Length, comment);
    }

    /// <summary>
    /// <paramref name="node"/> rendered without the comment its edits collected, which is given
    /// back instead, to go after whatever the line puts in front of the text.
    /// </summary>
    private string Bare(SyntaxNode node, Edits edits, out string? comment)
    {
        comment = edits.Comments.Count == 0 ? null : string.Join(", ", edits.Comments);
        edits.Comments.Clear();
        return Render(node, edits).Trim();
    }

    /// <summary>One line of a body's values, written as the directive of the type the body is of.</summary>
    private void Values(SyntaxNode line, SyntaxNode values)
    {
        if (DataSyntax.DirectiveOfValues(values) is not { } directive)
            return;
        if (DataSyntax.TypeOf(directive) is { } named)
        {
            if (model.SymbolOf(named) is not { IsLayout: true } type)
                return;
            foreach (var record in values.ChildNodes)
                Fields(line, Indent(values), type, ValuesIn(record), path: "");
            return;
        }
        if (layout.Of(values, expansion) is not { } laid)
        {
            NotTranspiled(values);
            return;
        }
        var edits = new Edits();
        Substitute(values, edits, nested: false);
        var text = $"{Indent(values)}{ForCa65(directive.ChildTokens[0].Text)} {Bare(values, edits, out var comment)}";
        if (comment is not null)
            text += new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + comment;
        Code(line, text, laid.Length);
    }

    /// <summary>
    /// A line of data with its name in front, where it has one: on the same line, lined up
    /// where the source wrote the directive, or on a line of its own where ca65 would read
    /// the name as a prefix.
    /// </summary>
    private void WithName(
        SyntaxNode line, SyntaxNode statement, SyntaxNode directive, Symbol? symbol, string text, int bytes,
        string? comment = null)
    {
        var indent = Indent(statement);
        string written;
        if (symbol is null)
        {
            written = indent + text;
        }
        else if (LabelText(Named(symbol)) is var label && !label.EndsWith(':'))
        {
            Code(line, indent + label, 0);
            written = indent + "    " + text;
        }
        else
        {
            var column = directive.Span.Start - line.Tree.LineStarts[line.LineIndex]
                - (statement.Kind == SyntaxKind.DataDeclaration ? ".data ".Length : 0);
            var start = indent + label;
            written = start + new string(' ', Math.Max(column - start.Length, 1)) + text;
        }
        if (comment is not null)
            written += new string(' ', Math.Max(CommentColumn - written.Length, 2)) + "; " + comment;
        Code(line, written, bytes);
    }

    /// <summary>
    /// Records: one with values, on its line or in the block its line opens, or an array of
    /// them in braces, each written as one directive per member in the type's order, whatever
    /// order the values were written in. Room with no values is zeros, which one `.res` says,
    /// unless a member pads with something else.
    /// </summary>
    private void Records(SyntaxNode line, SyntaxNode statement, SyntaxNode directive, Symbol? symbol, Symbol type)
    {
        if (model.RoomFor(directive, expansion) is not { } room)
        {
            NotTranspiled(directive);
            return;
        }
        var indent = Indent(statement);
        IReadOnlyList<IReadOnlyDictionary<string, SyntaxNode>> records;
        if (DataSyntax.BracedOf(directive) is { } braced)
        {
            records = braced.Kind == SyntaxKind.RecordValues ? [ValuesIn(braced)] : [.. braced.ChildNodes.Select(ValuesIn)];
        }
        else if (DataSyntax.BodyOf(directive) is { } block)
        {
            records = [ValuesIn(block.ChildNodes.Skip(1).Select(child => child.Statement).OfType<SyntaxNode>())];
        }
        else if (!Pads(type))
        {
            WithName(line, statement, directive, symbol, $".res {room.Bytes}", (int)room.Bytes, type.QualifiedName);
            return;
        }
        else
        {
            records = [.. Enumerable.Repeat(ValuesIn([]), (int)room.Elements)];
        }

        if (symbol is not null)
            Code(line, indent + LabelText(Named(symbol)), 0);
        long bytes = 0;
        foreach (var record in records)
            bytes += Fields(line, indent, type, record, path: "");
        if (bytes != room.Bytes)
            NotTranspiled(directive);
    }

    /// <summary>Whether a type, or a record inside it, has a member that pads with something other than zero.</summary>
    private bool Pads(Symbol type) => (type.Body?.Symbols ?? []).Any(member =>
        member.Kind == SymbolKind.Member
        && (member.Type is { IsLayout: true } inner ? Pads(inner) : Fill(member) != 0));

    /// <summary>The byte a <c>.res n, fill</c> member pads with, which is zero when it names none.</summary>
    private long Fill(Symbol member) =>
        member.Data is { Kind: SyntaxKind.DataDirective } data && DataSyntax.NameOf(data) == ".res"
        && data.ChildNodes.Length > 1 && model.ValueOf(data.ChildNodes[1], expansion).AsNumber() is { } fill
            ? fill & 0xff
            : 0;

    /// <summary>The label of a line whose statement is written separately.</summary>
    private void LabelOnly(SyntaxNode line, SyntaxNode statement, SyntaxNode? label)
    {
        if (label is not { ChildTokens.Length: > 0 }
            || model.SymbolAt(label.ChildTokens[0]) is not { } reference)
        {
            return;
        }
        Code(line, Indent(statement) + LabelText(Named(reference)), 0);
    }

    /// <summary>
    /// The members of an exported layout, each written out as the constant offset it is, and its size.
    /// A layout nothing exports says nothing to ca65 and is left out entirely.
    /// </summary>
    private void Offsets(SyntaxNode line, SyntaxNode? opener)
    {
        if (opener is null || opener.ChildTokens.Length == 0)
            return;
        var named = opener.ChildTokens.FirstOrDefault(token =>
            token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic);
        if (named.Parent is null || model.SymbolAt(named) is not { } reference)
            return;

        foreach (var member in reference.Body?.Symbols ?? [])
        {
            if (exported.Contains(member) && member.Value.AsNumber() is { } offset)
                Definition($"{Indent(opener)}{Named(member)} = {Constant(offset)}");
        }
        if (IsSized(reference))
            Definition($"{Indent(opener)}{SizeOf(reference)} = {Constant(reference.Size!.Value)}");
    }

    /// <summary>One enum member, which is a constant like any other.</summary>
    private void EnumMember(SyntaxNode line, SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0
            || model.SymbolAt(statement.ChildTokens[0]) is not { } reference
            || reference.Value.AsNumber() is not { } value)
        {
            return;
        }
        Definition($"{Indent(statement)}{Named(reference)} = {Constant(value)}");
    }

    /// <summary>
    /// The members of a type, one directive each, in the order the type declares them. A
    /// member that is itself a record, or an array of them, is written out the same way, so a
    /// nested value reaches the fields inside it.
    /// </summary>
    private long Fields(
        SyntaxNode line, string indent, Symbol type, IReadOnlyDictionary<string, SyntaxNode> written, string path)
    {
        long bytes = 0;
        var members = (type.Body?.Symbols ?? []).Where(member => member.Kind == SymbolKind.Member).ToList();

        // A union is written as the one member it is given, or its first, and zeros to its size.
        if (type.Kind == SymbolKind.Union && members.Count > 0)
            members = [members.FirstOrDefault(member => written.ContainsKey(member.Name)) ?? members[0]];

        foreach (var member in members)
        {
            if (member.Size is not { } size)
                continue;
            var given = written.GetValueOrDefault(member.Name)?.ChildNodes.LastOrDefault();
            var named = path.Length == 0 ? member.Name : $"{path}::{member.Name}";
            var element = member.Data is { Kind: SyntaxKind.DataDirective } data ? data : null;

            // An array member takes a braced list, and one no value names is zeros.
            if (element is not null && DataSyntax.CountOf(element) is not null)
            {
                var items = given is { Kind: SyntaxKind.ValueList } list ? list.ChildNodes : [];
                if (member.Type is { IsLayout: true } records)
                {
                    for (var i = 0; i < member.Count; i++)
                        bytes += Fields(line, indent, records, ValuesIn(i < items.Length ? items[i] : null), $"{named}[{i}]");
                    continue;
                }
                var (width, bigEndian) = Slot(element);
                Field(line, indent, items.Length == 0
                    ? $".res {size}"
                    : $"{ForCa65(DataSyntax.NameOf(element))} {string.Join(", ", items.Select(item => Datum(item, width, bigEndian, []) ?? Rendered(item)))}",
                    named, size);
                bytes += size;
                continue;
            }

            // A nested record takes a braced list of its own members; anything else is a value.
            if (member.Type is { IsLayout: true } inner)
            {
                bytes += Fields(line, indent, inner, ValuesIn(given), named);
                continue;
            }
            Field(line, indent, Member(member, element, given), named, size);
            bytes += size;
        }

        if (type.Kind == SymbolKind.Union && type.Size is { } whole && whole > bytes)
        {
            Field(line, indent, $".res {whole - bytes}, $00", path.Length == 0 ? type.Name : path, whole - bytes);
            bytes = whole;
        }
        return bytes;
    }

    /// <summary>One member's directive, with the path it fills in a comment.</summary>
    private void Field(SyntaxNode line, string indent, string directive, string path, long size)
    {
        var text = $"{indent}    {directive}";
        Code(line, text + new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + path, (int)size);
    }

    /// <summary>The <c>member = value</c>s of a record, by member name: a braced record, or the lines of one.</summary>
    private static IReadOnlyDictionary<string, SyntaxNode> ValuesIn(SyntaxNode? record) =>
        ValuesIn(record is { Kind: SyntaxKind.RecordValues } ? record.ChildNodes : []);

    private static IReadOnlyDictionary<string, SyntaxNode> ValuesIn(IEnumerable<SyntaxNode> values)
    {
        var named = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.Kind == SyntaxKind.MemberValue && value.ChildTokens.Length > 0)
                named[value.ChildTokens[0].Text] = value;
        }
        return named;
    }

    /// <summary>
    /// One member of a record, written with the directive its type gave it. A member no value
    /// names is zero, and a member reserved by <c>.res</c> takes text, padded to the room it
    /// has with the byte it pads with.
    /// </summary>
    private string Member(Symbol member, SyntaxNode? element, SyntaxNode? given)
    {
        var size = member.Size ?? 0;
        var directive = element is null ? ".res" : DataSyntax.NameOf(element);
        if (directive == ".res")
            return $".byte {string.Join(", ", Padded(given, size, Fill(member)))}";
        var (width, bigEndian) = Slot(element!);
        var value = given is null ? Constant(0) : Datum(given, width, bigEndian, []) ?? Rendered(given);
        return $"{ForCa65(directive)} {(given is null && bigEndian && width > 2 ? string.Join(", ", Enumerable.Repeat(Hex(0, 2), width)) : value)}";
    }

    /// <summary>The bytes a reserved member takes: the text it was given, then the byte it pads with.</summary>
    private IEnumerable<string> Padded(SyntaxNode? value, long size, long fill)
    {
        var bytes = value is null ? [] : model.BytesOf(value, expansion)?.ToList() ?? [];
        for (var i = 0; i < size; i++)
            yield return Hex(i < bytes.Count ? bytes[i] & 0xff : fill, 2);
    }

    /// <summary>
    /// An expression written out rather than edited in place: a call becomes what it stands
    /// for, and every nested operation is parenthesized, so nothing depends on how ca65
    /// reads precedence. What it would say in a comment goes to <paramref name="comments"/>,
    /// the comments of the line it is written into, when there is one: text after it on that
    /// line would otherwise land in its comment.
    /// </summary>
    private string Rendered(SyntaxNode node, List<string>? comments = null)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return node.ChildNodes.Length > 0 ? "(" + Rendered(node.ChildNodes[0], comments) + ")" : "";
            case SyntaxKind.BinaryExpression when node.ChildNodes.Length == 2 && node.ChildTokens.Length > 0:
                return $"({Rendered(node.ChildNodes[0], comments)} {Operator(node.ChildTokens[0])} {Rendered(node.ChildNodes[1], comments)})";
            case SyntaxKind.UnaryExpression when node.ChildNodes.Length == 1 && node.ChildTokens.Length > 0:
                return $"({node.ChildTokens[0].Text}{Rendered(node.ChildNodes[0], comments)})";
            default:
                break;
        }

        // Anything with a value nt65 knows is written as that value; anything else keeps its
        // own spelling, with names flattened.
        if (model.ValueOf(node, expansion).AsNumber() is { } value)
            return Constant(value);
        var edits = new Edits();
        Substitute(node, edits, nested: false);
        return Inline(node, edits, comments);
    }

    /// <summary>
    /// <paramref name="node"/> rendered to go inside another line: its comments are that
    /// line's, given to <paramref name="comments"/>, or kept on the text when there is none.
    /// </summary>
    private string Inline(SyntaxNode node, Edits edits, List<string>? comments)
    {
        if (comments is null)
            return Render(node, edits).Trim();
        comments.AddRange(edits.Comments);
        edits.Comments.Clear();
        return Render(node, edits).Trim();
    }

    /// <summary>
    /// An operator as ca65 spells it. ca65 writes equality `=` and inequality `&lt;&gt;`; its
    /// `&amp;&amp;`, `||`, `^^` and `!` are nt65's.
    /// </summary>
    private static string Operator(SyntaxToken op) => op.Kind switch
    {
        SyntaxKind.EqualsEquals => "=",
        SyntaxKind.BangEquals => "<>",

        // ca65 has no `^^`: it reads `a ^^ b` as `a ^ ^b`, an xor with the bank byte.
        SyntaxKind.CaretCaret => ".xor",
        _ => op.Text,
    };

    /// <summary>
    /// What a symbol is called in the output, at the level being written. A name a macro body
    /// declares is a different name at every expansion.
    /// </summary>
    private string Named(Symbol symbol) => names.Of(symbol, expansion);

    /// <summary>
    /// An argument written where the body named its parameter. It goes in as a parenthesized
    /// whole, so `value * 2` with the argument `1 + 2` is `(1 + 2) * 2` — six, where ca65's
    /// textual substitution would give five — and `#&lt;value` with `label+1` is
    /// `#&lt;(label+1)`. It keeps its own spelling: an argument is an expression, not a number,
    /// and a name in it is written the way the same name would be written anywhere else.
    /// </summary>
    private string Substituted(SyntaxNode argument, List<string>? comments = null)
    {
        var edits = new Edits();
        Substitute(argument, edits, nested: false);
        var text = Inline(argument, edits, comments);
        return argument.Kind is SyntaxKind.BinaryExpression or SyntaxKind.UnaryExpression
            ? "(" + text + ")"
            : text;
    }

    /// <summary>A label, or the assignment that stands in for one ca65 would misread.</summary>
    private static string LabelText(string name) => name is "z" or "f" ? $"{name} := *" : $"{name}:";

    private void Constant(SyntaxNode line, SyntaxNode statement)
    {
        // A string has no ca65 spelling as a constant: it is used through `.strlen` and
        // `.strat`, which are numbers by the time anything is written.
        if (statement.ChildTokens.Length > 0
            && model.SymbolAt(statement.ChildTokens[0]) is { } reference)
        {
            if (reference.Value.IsString)
                return;
            var edits = new Edits();
            edits.Replace[statement.ChildTokens[0].Position] = Named(reference);
            foreach (var child in statement.ChildNodes)
                Substitute(child, edits, nested: false);
            var text = Render(statement, edits);
            if (statement.DescendantNodes().Any(node => node.Kind == SyntaxKind.CurrentAddressExpression))
                Code(line, text, 0);
            else
                Definition(text);
        }
    }

    /// <summary>An extern proc is a routine at a constant address, which is a constant.</summary>
    private void ExternProc(SyntaxNode line, SyntaxNode statement)
    {
        var name = statement.ChildTokens.FirstOrDefault(token =>
            token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic);
        var address = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ProcSignature);
        if (name.Parent is null || address is null
            || model.SymbolAt(name) is not { } reference)
        {
            return;
        }

        var edits = new Edits();
        Substitute(address, edits, nested: false);
        Definition($"{Indent(statement)}{Named(reference)} = {Render(address, edits).TrimStart()}");
    }

    private void Source(SyntaxNode line, SyntaxNode statement, int bytes, bool located = false)
    {
        Width(statement);
        var edits = new Edits();
        Substitute(statement, edits, nested: false);
        Immediate(statement, bytes, edits);
        Slot(statement, edits);
        Direct(statement, edits);
        Code(line, Render(statement, edits), bytes, located);
    }

    /// <summary>
    /// An immediate is a byte or a word slot, as wide as the instruction makes it, so a negative
    /// constant in one is written as its two's complement.
    /// </summary>
    private void Immediate(SyntaxNode statement, int bytes, Edits edits)
    {
        if (statement is { Kind: SyntaxKind.InstructionStatement, ChildNodes: [{ Kind: SyntaxKind.ImmediateOperand, ChildNodes: [var value] }] }
            && bytes is 2 or 3 && Datum(value, bytes - 1, bigEndian: false, edits.Comments) is { } text)
        {
            Replace(value, text, edits, around: false);
        }
    }

    /// <summary>An assertion the linker checks: ca65's <c>.assert</c> with the level that defers it to ld65.</summary>
    private void Linked(SyntaxNode line, SyntaxNode statement, SyntaxNode condition)
    {
        var edits = new Edits();
        Substitute(statement, edits, nested: false);
        var last = Tokens(condition)[^1].Position;
        edits.After[last] = edits.After.GetValueOrDefault(last, "") + ", lderror";
        Code(line, Render(statement, edits), 0, located: true);
    }

    /// <summary>
    /// An <c>.ensure</c>, written as the <c>rep</c> and <c>sep</c> the analysis found it needs,
    /// which is nothing where the widths already hold.
    /// </summary>
    private void Ensure(SyntaxNode line, SyntaxNode directive)
    {
        if (layout.Of(directive, expansion)?.Ensured is not { } ensured)
            return;
        var indent = Indent(directive);
        if (ensured.Reset != 0)
            Code(line, $"{indent}rep #{Hex(ensured.Reset, 2)}", 2);
        if (ensured.Set != 0)
            Code(line, $"{indent}sep #{Hex(ensured.Set, 2)}", 2);
    }

    /// <summary>
    /// A frame's member in a stack-relative operand, written as the offset from the stack
    /// pointer the analysis counted for it here: ca65 knows nothing of frames.
    /// </summary>
    private void Slot(SyntaxNode statement, Edits edits)
    {
        if (layout.Of(statement, expansion)?.Slot is not { } slot
            || statement.ChildNodes.FirstOrDefault() is not { } operand)
        {
            return;
        }
        foreach (var name in operand.DescendantNodes().Where(node => node.Kind == SyntaxKind.NameExpression))
        {
            var tokens = name.ChildTokens;
            if (tokens.Length == 0 || model.SymbolAt(tokens[0]) is not { Kind: SymbolKind.Frame })
                continue;
            edits.Replace[tokens[0].Position] = slot.ToString(CultureInfo.InvariantCulture);
            for (var i = 1; i < tokens.Length; i++)
                edits.Replace[tokens[i].Position] = "";
        }
    }

    /// <summary>
    /// A <c>d:</c> operand, written as the offset into the direct page the analysis found D
    /// makes it: with D at <c>$2100</c>, <c>lda d:$2105</c> is <c>lda z:$05</c>. ca65 has no
    /// <c>d:</c>, and knows nothing of D.
    /// </summary>
    private void Direct(SyntaxNode statement, Edits edits)
    {
        if (layout.Of(statement, expansion) is not { Direct: { } offset } laid
            || statement.ChildNodes.FirstOrDefault() is not { } operand)
        {
            return;
        }
        var written = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix);
        var expression = operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix);
        if (written is null || expression is null)
            return;
        foreach (var token in written.ChildTokens)
            edits.Replace[token.Position] = "";
        var tokens = Tokens(expression);
        edits.Before.Remove(tokens[0].Position);
        edits.Replace[tokens[0].Position] = (laid.Prefix ?? "") + Hex(offset, 2);
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
    }

    /// <summary>
    /// The <c>.a8</c>, <c>.a16</c>, <c>.i8</c> or <c>.i16</c> a 65816 immediate needs. ca65
    /// sizes an immediate from the last such directive in the text, whatever the control flow,
    /// so one goes directly before each immediate whose width differs from the previous
    /// immediate's for the same register, and nowhere else. The width itself is the
    /// analysis's, which follows control flow.
    /// </summary>
    private void Width(SyntaxNode statement)
    {
        if (layout.Of(statement, expansion) is not { Bits: { } bits }
            || statement.ChildTokens.Length == 0
            || Instructions.SizedBy(statement.ChildTokens[0].Text) is not { } register)
        {
            return;
        }
        if (widths.TryGetValue(register, out var previous) && previous == bits)
            return;
        widths[register] = bits;
        Segment();
        Flush();
        Line($"{Indent(statement)}.{(register == WidthRegister.A ? "a" : "i")}{bits}");
    }

    /// <summary>
    /// A line nothing can be written for, which analysis should already have refused. The file
    /// is not transpiled rather than transpiled wrongly.
    /// </summary>
    private void NotTranspiled(SyntaxNode statement)
    {
        var tokens = Tokens(statement);
        var first = tokens.FirstOrDefault(token => token.Kind != SyntaxKind.EndOfLine);
        if (first.Parent is null)
            return;

        // In a file that is already wrong, what cannot be written is almost always what the
        // mistake left behind, and saying so again only buries the mistake.
        if (diagnostics.Any(d => d.Severity == Severity.Error && d.Span.File == model.Tree.Path))
            return;
        diagnostics.Add(Expansion.Problem(model.Tree, first.Parent.Tree, first.Span, expansion, Severity.Error,
            $"`{first.Text}` cannot be written out, and nothing said why: this is a bug in nt65"));
    }

    /// <summary>
    /// Writes one line that came from the source, with the debug line that maps it back.
    /// A line that generates bytes gets one, because ld65 attaches a span of bytes to the line
    /// in effect while they were generated. So does one that is <paramref name="located"/>: an
    /// assertion ca65 evaluates, whose failure ca65 notes as generated from the line in effect.
    /// A label or a constant gets none, and neither do imports and exports: ld65 names the
    /// output's own line for what goes wrong with those, whatever the debug line says, and each
    /// would only record a line covering nothing.
    /// </summary>
    private void Code(SyntaxNode line, string text, int bytes, bool located = false)
    {
        Segment();
        Flush();
        if (bytes != 0 || located)
            Located((callLine ?? line).LineIndex + 1);
        Line(text, bytes);
    }

    /// <summary>The debug line that says what follows came from line <paramref name="number"/>.</summary>
    private void Located(int number) => Line($".dbg line, \"{source}\", {number}");

    /// <summary>
    /// Writes a definition that is in no segment: a constant, or a name for an address given
    /// by other names. It is written where it stands, before any segment or in whichever one is
    /// open, so a program of constants needs no segment in its linker configuration.
    /// </summary>
    private void Definition(string text)
    {
        Flush();
        Line(text);
    }

    /// <summary>The <c>.segment</c> directive, written when what follows lands somewhere new.</summary>
    private void Segment()
    {
        if (written == segment || segment is null)
            return;
        var size = model.Segments.Find(segment)?.Size ?? AddressSize.Absolute;
        if (written is not null)
            pendingBlank = true;
        Flush();
        Line($".segment \"{segment}\": {size switch
        {
            AddressSize.ZeroPage => "zeropage",
            AddressSize.Far => "far",
            _ => "absolute",
        }}");
        written = segment;
    }

    /// <summary>A blank line the source asked for, at most one however many it wrote.</summary>
    private void Blank()
    {
        pendingBlank = true;
        Flush();
    }

    private void Flush()
    {
        if (!pendingBlank)
            return;
        pendingBlank = false;
        if (generated.Length > 0)
            Line("");
    }

    private void Line(string text, int bytes = 0)
    {
        generated.Append(text.TrimEnd()).Append('\n');
        lineBytes.Add(bytes);
    }

    /// <summary>The whitespace a line starts with, which the output keeps.</summary>
    private static string Indent(SyntaxNode statement)
    {
        var tokens = Tokens(statement);
        if (tokens.Count == 0)
            return "";
        var indent = new StringBuilder();
        foreach (var trivia in tokens[0].Green.LeadingTrivia)
        {
            if (trivia.Kind == SyntaxKind.WhitespaceTrivia)
                indent.Append(trivia.Text);
        }
        return indent.ToString();
    }

    /// <summary>
    /// The statement's own text with the edits applied: the source's spacing, its comments
    /// dropped, and names, prefixes and byte values written where the source had something
    /// else.
    /// </summary>
    private string Render(SyntaxNode statement, Edits edits)
    {
        var text = new StringBuilder();
        var tokens = Tokens(statement);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.EndOfLine)
                continue;
            if (edits.Joined.Contains(token.Position))
            {
                if (!edits.Joined.Contains(tokens[i - 1].Position))
                    text.Length -= Width(tokens[i - 1].Green.TrailingTrivia);
                text.Append(edits.Before.GetValueOrDefault(token.Position, ""))
                    .Append(edits.Replace.GetValueOrDefault(token.Position, token.Text))
                    .Append(edits.After.GetValueOrDefault(token.Position, ""));
                if (i + 1 == tokens.Count || !edits.Joined.Contains(tokens[i + 1].Position))
                    Whitespace(text, token.Green.TrailingTrivia);
                continue;
            }
            Whitespace(text, token.Green.LeadingTrivia);
            if (edits.Before.TryGetValue(token.Position, out var before))
                text.Append(before);
            text.Append(edits.Replace.TryGetValue(token.Position, out var replacement) ? replacement : token.Text);
            if (edits.After.TryGetValue(token.Position, out var after))
                text.Append(after);
            Whitespace(text, token.Green.TrailingTrivia);
        }

        var line = text.ToString().TrimEnd();
        if (edits.Comments.Count == 0)
            return line;
        var padding = Math.Max(CommentColumn - line.Length, 2);
        return line + new string(' ', padding) + "; " + string.Join(", ", edits.Comments);
    }

    private static int Width(IEnumerable<GreenTrivia> trivia) =>
        trivia.Where(piece => piece.Kind == SyntaxKind.WhitespaceTrivia).Sum(piece => piece.Text.Length);

    private static void Whitespace(StringBuilder text, IEnumerable<GreenTrivia> trivia)
    {
        foreach (var piece in trivia)
        {
            if (piece.Kind == SyntaxKind.WhitespaceTrivia)
                text.Append(piece.Text);
        }
    }

    /// <summary>
    /// What the output writes instead of what the source did: flat names, the address-size
    /// prefix the chosen mode needs, byte values for text, and the parentheses that keep the
    /// output from depending on ca65's precedence.
    /// </summary>
    private void Substitute(SyntaxNode node, Edits edits, bool nested)
    {
        switch (node.Kind)
        {
            case SyntaxKind.NameExpression:
                Name(node, edits);
                return;

            case SyntaxKind.StringExpression:
            case SyntaxKind.CharacterExpression:
                Text(node, edits);
                return;

            case SyntaxKind.CallExpression:
                Applied(node, edits);
                return;

            // `wdm #n` is written as its bytes, which is what it is to every processor but the
            // emulator that hooks it.
            case SyntaxKind.InstructionStatement
                when node.ChildTokens.Length > 0
                    && node.ChildTokens[0].Text.Equals("wdm", StringComparison.OrdinalIgnoreCase)
                    && node.ChildNodes.FirstOrDefault() is { Kind: SyntaxKind.ImmediateOperand } hook
                    && hook.ChildTokens.Length > 0:
                edits.Replace[node.ChildTokens[0].Position] = ".byte";
                edits.Replace[hook.ChildTokens[0].Position] = "$42, ";
                break;

            case SyntaxKind.AbsoluteOperand:
                // In a macro body an `operand` parameter stands as a whole operand, so what
                // the call gave replaces what the body wrote, prefix, index and all. The
                // prefix goes on last, outside whatever parentheses the expression was given.
                if (Given(node, edits))
                    return;
                foreach (var child in node.ChildNodes)
                    Substitute(child, edits, nested: false);
                Prefix(node, edits);
                return;

            case SyntaxKind.DataDirective:
                Terminated(node, edits);

                // An `.incbin` names a file rather than holding data, so its path is left a
                // path — pointed at the file from wherever the output lands.
                if (Included(node, edits))
                    return;

                // An element type's values, and a `.res` fill, are slots of a width.
                if (DataSyntax.IsElementType(node) && DataSyntax.BracedOf(node) is null && node.ChildTokens.Length > 0)
                {
                    edits.Replace[node.ChildTokens[0].Position] = ForCa65(node.ChildTokens[0].Text);
                    var (width, bigEndian) = Slot(node);
                    foreach (var value in DataSyntax.ValuesOf(node))
                        InPlace(value, width, bigEndian, edits);
                    return;
                }
                if (DataSyntax.NameOf(node) == ".res" && node.ChildNodes is [var count, var fill])
                {
                    Substitute(count, edits, nested: false);
                    InPlace(fill, 1, bigEndian: false, edits);
                    return;
                }
                break;

            case SyntaxKind.DataValues when DataSyntax.DirectiveOfValues(node) is { } of && !DataSyntax.IsRecord(of):
                var (valueWidth, valuesBigEndian) = Slot(of);
                foreach (var value in node.ChildNodes)
                    InPlace(value, valueWidth, valuesBigEndian, edits);
                return;

            case SyntaxKind.BinaryExpression:
            case SyntaxKind.UnaryExpression:
                foreach (var op in node.ChildTokens)
                {
                    if (Operator(op) is var spelled && spelled != op.Text)
                        edits.Replace[op.Position] = spelled;
                }
                if (nested)
                {
                    var tokens = Tokens(node);
                    edits.Before[tokens[0].Position] = "(" + edits.Before.GetValueOrDefault(tokens[0].Position, "");
                    edits.After[tokens[^1].Position] = edits.After.GetValueOrDefault(tokens[^1].Position, "") + ")";
                }
                foreach (var child in node.ChildNodes)
                    Substitute(child, edits, nested: true);
                return;

            default:
                break;
        }

        foreach (var child in node.ChildNodes)
            Substitute(child, edits, nested: false);
    }

    /// <summary>
    /// The directive ca65 writes for one of nt65's element types. ca65's 24-bit directive is
    /// <c>.faraddr</c> and its one big-endian directive <c>.dbyt</c>; a wider big-endian value
    /// is written as its bytes.
    /// </summary>
    private static string ForCa65(string directive) => directive.ToLowerInvariant() switch
    {
        ".long" => ".faraddr",
        ".beword" => ".dbyt",
        ".belong" or ".bedword" => ".byte",
        _ => directive,
    };

    /// <summary>How wide one element of an element type is, and whether its bytes are written high first.</summary>
    private static (int Width, bool BigEndian) Slot(SyntaxNode directive)
    {
        var name = DataSyntax.NameOf(directive);
        return (SyntaxFacts.ElementSize(name) ?? 1, name is ".beword" or ".belong" or ".bedword");
    }

    /// <summary>One value of a slot, written as <see cref="Datum"/> says where it says anything, and as it stands otherwise.</summary>
    private void InPlace(SyntaxNode value, int width, bool bigEndian, Edits edits)
    {
        if (Datum(value, width, bigEndian, edits.Comments) is { } text)
            Replace(value, text, edits);
        else
            Substitute(value, edits, nested: false);
    }

    /// <summary>
    /// Writes <paramref name="text"/> in place of all of <paramref name="node"/>. What was written
    /// around it stays, and what was already made of the inside goes; with
    /// <paramref name="around"/> false, what was made of its ends goes too.
    /// </summary>
    private static void Replace(SyntaxNode node, string text, Edits edits, bool around = true)
    {
        var tokens = Tokens(node);
        for (var i = 0; i < tokens.Count; i++)
        {
            edits.Replace[tokens[i].Position] = "";
            if (i > 0 || !around)
                edits.Before.Remove(tokens[i].Position);
            if (i < tokens.Count - 1 || !around)
                edits.After.Remove(tokens[i].Position);
        }
        edits.Replace[tokens[0].Position] = text;

        // The spaces between the tokens it replaces go with them, and the ones around it stay.
        for (var i = 1; i < tokens.Count; i++)
            edits.Joined.Add(tokens[i].Position);
    }

    /// <summary>
    /// One value of a slot <paramref name="width"/> bytes wide where ca65 cannot take it as
    /// written: a negative constant is its two's complement, and a big-endian value wider than
    /// a word, which ca65 has no directive for, is its bytes, high first. Null for a value
    /// written as it stands. What the source said goes to <paramref name="comments"/>.
    /// </summary>
    private string? Datum(SyntaxNode value, int width, bool bigEndian, List<string> comments)
    {
        var known = model.ValueOf(value, expansion).AsNumber();
        if (bigEndian && width > 2)
        {
            if (model.BytesOf(value, expansion) is { Count: > 0 } text)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", text.SelectMany(b => HighFirst(b, width)));
            }
            if (known is { } number)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", HighFirst(number, width));
            }
            var written = Rendered(value, comments);
            var low = $".bankbyte({written}), .hibyte({written}), .lobyte({written})";
            return width == 3 ? low : $".lobyte(({written}) >> 24), {low}";
        }
        if (known is not (< 0 and var negative))
            return null;
        comments.Add(value.GetText().Trim());
        return Hex(negative & (long)(ulong.MaxValue >> (64 - (8 * width))), 2 * width);

        static IEnumerable<string> HighFirst(long number, int width) =>
            Enumerable.Range(0, width).Select(i => Hex((number >> (8 * (width - 1 - i))) & 0xff, 2));
    }

    /// <summary>
    /// Text reaches the output as bytes, so <c>.strz</c> becomes the bytes and
    /// the zero that ends them: ca65's own directive takes a string, and there is none left.
    /// </summary>
    private static void Terminated(SyntaxNode directive, Edits edits)
    {
        if (directive.ChildTokens.Length == 0
            || !directive.ChildTokens[0].Text.Equals(".strz", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        edits.Replace[directive.ChildTokens[0].Position] = ".byte";
        var last = directive.ChildNodes.LastOrDefault() is { } argument && Tokens(argument) is [.., var token]
            ? token.Position
            : directive.ChildTokens[0].Position;
        edits.After[last] = edits.After.GetValueOrDefault(last, "") + ", $00";
    }

    /// <summary>
    /// The path of an <c>.incbin</c>, rewritten so that ca65 finds the file from the output
    /// rather than from the source: ca65 looks beside the file it is assembling, so the output
    /// assembles from any directory. Returns whether the directive was one.
    /// </summary>
    private bool Included(SyntaxNode directive, Edits edits)
    {
        if (directive.ChildTokens.Length == 0
            || !directive.ChildTokens[0].Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (directive.ChildNodes.FirstOrDefault() is { } path
            && model.ValueOf(path, expansion) is { Kind: ValueKind.String, Text: { } named })
        {
            Replace(path, "\"" + Paths.Relative(Paths.Directory(output), Paths.Beside(source, named)) + "\"", edits);
        }
        foreach (var argument in directive.ChildNodes.Skip(1))
            Substitute(argument, edits, nested: false);
        return true;
    }

    private void Name(SyntaxNode name, Edits edits)
    {
        var tokens = name.ChildTokens;
        if (tokens.Length == 0)
            return;

        // A path names one symbol; the whole of it becomes that symbol's flat name. A body is
        // written out in every file that calls its macro, so what a name means is the
        // program's answer rather than this one file's.
        var last = tokens.LastOrDefault(token => token.Kind is not SyntaxKind.ColonColon);
        if (last.Parent is null || model.SymbolAt(last) is not { } named)
            return;
        var reference = named;

        // A path that ends in a repetition's name names a different member on every turn.
        if (named.Kind == SymbolKind.Binding && tokens.Length > 1)
        {
            if (model.SymbolOf(name, expansion) is not { } namesake)
                return;
            reference = namesake;
        }

        // A list stands for its own items wherever data takes them.
        if (model.ItemsOf(name) is { Count: > 0 } items)
        {
            edits.Replace[tokens[0].Position] = string.Join(", ", items.Select(item => Rendered(item, edits.Comments)));
            for (var i = 1; i < tokens.Length; i++)
                edits.Replace[tokens[i].Position] = "";
            edits.Comments.Add(name.GetText().Trim());
            return;
        }

        // A member is an offset: the offsets along the path added up, on the address the
        // path starts from when it starts at an instance rather than at a type.
        if (reference.Kind == SymbolKind.Member)
        {
            MemberPath(name, tokens, edits);
            return;
        }

        // A macro parameter stands for the argument the call gave it, as a parenthesized
        // whole, so `value * 2` with the argument `1 + 2` is 6 rather than 5.
        var symbol = reference;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            if (Parameter(symbol, edits.Comments) is not { } given)
                return;
            edits.Replace[tokens[0].Position] = given;
            for (var i = 1; i < tokens.Length; i++)
                edits.Replace[tokens[i].Position] = "";
            return;
        }

        // The name a repetition binds is worth something different on every turn, and this is
        // the turn being written.
        if (symbol.Kind == SymbolKind.Binding)
        {
            if (model.BindingsOf(expansion)?.TryGetValue(symbol, out var bound) is not true)
                return;

            // A list item is written as it stands, with its own names substituted; a number
            // is written as the number it is on this turn.
            var written = bound.Item is { } item ? Rendered(item, edits.Comments) : null;
            if (written is null && bound.Value.AsNumber() is { } turn)
                written = Constant(turn);
            if (written is null)
                return;

            edits.Replace[tokens[0].Position] = written;
            for (var i = 1; i < tokens.Length; i++)
                edits.Replace[tokens[i].Position] = "";
            edits.Comments.Add(symbol.Name);
            return;
        }

        // A string constant has no ca65 spelling, in this module or any other: it is written
        // as the bytes of its text, as a literal is.
        if (symbol.Value.IsString)
        {
            if (model.BytesOf(name, expansion) is { Count: > 0 } bytes)
            {
                Replace(name, string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2))), edits);
                edits.Comments.Add(name.GetText().Trim());
            }
            return;
        }

        // A define and a checked import are written as their value, never by name: a `-D` given to ca65 then cannot collide with a define, and a checked import
        // is a value nt65 has already used in its own arithmetic.
        var byValue = (symbol.IsDefine || symbol.IsConfig || symbol.Kind == SymbolKind.ImportedConstant)
            && symbol.Value.AsNumber() is not null;
        edits.Replace[tokens[0].Position] = byValue
            ? Constant(symbol.Value.Number)
            : Named(symbol);
        for (var i = 1; i < tokens.Length; i++)
            edits.Replace[tokens[i].Position] = "";
        if (byValue)
            edits.Comments.Add(symbol.QualifiedName);
    }

    /// <summary>
    /// What a macro parameter stands for here: the operand a call gave, the expression it
    /// gave, or the word or number it stands for.
    /// </summary>
    private string? Parameter(Symbol parameter, List<string> comments)
    {
        if (model.BindingsOf(expansion)?.TryGetValue(parameter, out var bound) is not true)
            return null;
        if (bound.Value.IsWord)
            return bound.Value.Text;
        if (bound.Argument is { Parameter.Kind: ParameterKind.Operand } given)
            return given.Operand is { } operand ? Substituted(operand, comments) : null;
        return bound.Item is { } item ? Substituted(item, comments) : null;
    }

    /// <summary>
    /// A path through a type or an instance. Through a type it is a number; through an
    /// instance it is that instance plus the offset, which is what ca65 and ld65 resolve.
    /// Either way the path it came from is kept in a comment.
    /// </summary>
    private void MemberPath(SyntaxNode name, IReadOnlyList<SyntaxToken> tokens, Edits edits)
    {
        Symbol? start = null;
        long offset = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxKind.ColonColon || model.SymbolAt(token) is not { } part)
                continue;
            if (part.Kind == SymbolKind.Member)
                offset += part.Value.AsNumber() ?? 0;
            else if (part.IsAddress)
                start ??= part;
        }

        var text = start is null
            ? Constant(offset)
            : offset == 0 ? Named(start) : $"{Named(start)}+{offset}";
        edits.Replace[tokens[0].Position] = text;
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
        edits.Comments.Add(name.GetText().Trim());
    }

    /// <summary>
    /// A call written out as what it stands for: a charmap applied to text becomes the bytes
    /// it maps them to, and a function call becomes its value. A call nt65 cannot work out
    /// is refused rather than passed to ca65, which knows neither.
    /// </summary>
    private void Applied(SyntaxNode call, Edits edits)
    {
        var tokens = Tokens(call);
        if (tokens.Count == 0)
            return;

        // `.endof(f)` and `.spanof(f)` describe layout rather than shape, so they are written
        // as the addresses they are and resolved by ca65 and ld65.
        if (Extents.Is(call, model, out var span) && Extents.MeasuredBy(call) is { } named
            && model.SymbolOf(named) is { } measured && (ends.Contains(measured) || measured.Tree != model.Tree))
        {
            Replace(call, span ? $"({EndOf(measured)} - {Named(measured)})" : EndOf(measured), edits);
            return;
        }

        // A built-in the analysis answers keeps the ordinary path.
        if (call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.NameExpression) is null)
        {
            // An address `.select` chooses is written as the value it chose, which is all ca65 sees.
            if (model.ValueOf(call, expansion).AsNumber() is null
                && Evaluator.SelectArguments(call) is [var condition, var ifHolds, var otherwise]
                && model.ValueOf(condition, expansion).AsNumber() is { } holds)
            {
                // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
                var chosen = Rendered(holds != 0 ? ifHolds : otherwise, edits.Comments);
                Replace(call, chosen.StartsWith('(') ? "+" + chosen : chosen, edits);
                return;
            }
            if (model.ValueOf(call, expansion).AsNumber() is { } builtin)
            {
                Replace(call, Constant(builtin), edits);
                return;
            }
            foreach (var child in call.ChildNodes)
                Substitute(child, edits, nested: false);
            return;
        }

        string? text = null;
        if (model.BytesOf(call, expansion) is { Count: > 0 } bytes)
            text = string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        else if (model.ValueOf(call, expansion).AsNumber() is { } value)
            text = Constant(value);

        if (text is null)
        {
            NotTranspiled(call);
            return;
        }
        Replace(call, text, edits);
        edits.Comments.Add(call.GetText().Trim());
    }

    /// <summary>Text becomes byte values, with the source spelling kept in a comment.</summary>
    private void Text(SyntaxNode literal, Edits edits)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes || literal.ChildTokens.Length == 0)
            return;
        edits.Replace[literal.ChildTokens[0].Position] =
            string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        edits.Comments.Add(literal.GetText());
    }

    /// <summary>
    /// An operand that names an <c>operand</c> parameter, written out as the one the call
    /// gave. Returns whether it was one.
    /// </summary>
    private bool Given(SyntaxNode operand, Edits edits)
    {
        if (Semantics.Operands.Substituted(model, operand, expansion) is not { } given)
            return false;

        var text = Argument(given, edits.Comments);
        if (text is null)
            return false;

        // ca65 reads a `(` at the head of an operand as indirect addressing, so an expression
        // that starts with one gets a unary `+`, which changes nothing about what it is worth.
        var prefix = operand.Parent is { } instruction ? layout.Of(instruction, expansion)?.Prefix ?? "" : "";
        if (text.StartsWith('(') && (prefix.Length > 0 || given.IsAddress))
            text = "+" + text;

        var tokens = Tokens(operand);
        edits.Replace[tokens[0].Position] = prefix + text;
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
        return true;
    }

    /// <summary>
    /// The text of the operand a call gave, with whatever the body asked of it: the operand
    /// as it stands, the byte after it, or one byte of an immediate value.
    /// </summary>
    private string? Argument(OperandSubstitution given, List<string> comments)
    {
        // `.byteof` on an immediate is a byte of the value, which is a number wherever nt65
        // knows it and a shift and a mask wherever only the linker will.
        if (given.ByteOf && given.Operand.Kind == SyntaxKind.ImmediateOperand)
        {
            if (given.Expression is not { } value)
                return null;
            if (model.ValueOf(value, expansion).AsNumber() is { } known)
                return "#" + Constant((known >> (int)(8 * given.Offset)) & 0xff);
            var shifted = Substituted(value, comments);
            return given.Offset == 0
                ? $"#({shifted} & $ff)"
                : $"#(({shifted} >> {8 * given.Offset}) & $ff)";
        }

        // Anything else is the argument's own operand: as it stands when the body named it
        // whole, and with `+ n` on its expression when the body asked for a later byte.
        var offset = given.Offset;
        if (offset == 0)
            return Substituted(given.Operand, comments);
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var written = Substituted(addressed, comments);
        return offset > 0 ? $"{written}+{offset}{index}" : $"{written}{offset}{index}";
    }

    /// <summary>The <c>z:</c> or <c>a:</c> that says which mode was chosen.</summary>
    private void Prefix(SyntaxNode operand, Edits edits)
    {
        var instruction = operand.Parent;
        if (instruction is null)
            return;
        var chosen = layout.Of(instruction, expansion)?.Prefix;
        var prefix = chosen ?? "";
        var written = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix);
        if (chosen is null && written is not null)
            return;
        var expression = operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix);
        var tokens = expression is null ? [] : Tokens(expression);

        // ca65 reads a `(` at the head of an operand, or straight after a prefix, as indirect
        // addressing, so an expression that starts with one — `lda (hi + lo) * 2`, which the
        // language allows, `jml (bank << 16) | .loword(f)`, or one written out with parentheses
        // around its first operation — gets a unary `+` in front of it. It changes nothing and
        // keeps the operand an expression.
        var opens = tokens is [{ Kind: SyntaxKind.OpenParen }, ..]
            || (tokens.Count > 0 && edits.Before.GetValueOrDefault(tokens[0].Position, "").StartsWith('('));
        var text = opens ? prefix + "+" : prefix;

        if (written is { ChildTokens.Length: > 0 })
        {
            // The source already said which; write the one that was chosen, in case an
            // expression made it wider.
            foreach (var token in written.ChildTokens)
                edits.Replace[token.Position] = "";
            edits.Replace[written.ChildTokens[0].Position] = text;
            return;
        }
        if (tokens.Count > 0)
            edits.Before[tokens[0].Position] = text + edits.Before.GetValueOrDefault(tokens[0].Position, "");
    }

    /// <summary>What the output writes in place of the source's own tokens.</summary>
    private sealed class Edits
    {
        /// <summary>Text to write before a token, by the token's position.</summary>
        public Dictionary<int, string> Before { get; } = [];

        /// <summary>Text to write after a token, by the token's position.</summary>
        public Dictionary<int, string> After { get; } = [];

        /// <summary>Text to write instead of a token, by the token's position; empty omits it.</summary>
        public Dictionary<int, string> Replace { get; } = [];

        /// <summary>Source spellings to keep in a comment at the end of the line.</summary>
        public List<string> Comments { get; } = [];

        /// <summary>Tokens written as part of the one before them, with no whitespace between the two.</summary>
        public HashSet<int> Joined { get; } = [];
    }
}
