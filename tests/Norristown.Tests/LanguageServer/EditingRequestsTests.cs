using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets while code is being written: completion, signature help for a call, the
/// code lenses and hovers that report cost and registers, and a search for a declaration across
/// the workspace.
/// </summary>
public sealed class EditingRequestsTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string VicUri = "file:///c:/work/hw/vic.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear, SCREEN
        .export .struct Sprite {
            x: .byte
            y: .byte
        }
        SCREEN = $0400
        .segment CODE
        .proc clear {
            rts
        }
        .proc helper {
            rts
        }
        """;

    private const string Vic = ".module hw::vic\n.export BORDER = $d020\n";

    /// <summary>
    /// The file completion is requested in. Each <c>|name</c> marks a place where a test may put
    /// a line of its own, and <c>name</c> is what the test calls that place; a test that names a
    /// place not marked here gets its line at the file's top level.
    /// </summary>
    private const string Main = """
        .module main
        .use gfx::{clear}
        .use hw::vic
        .signature fast = a8, i8
        .func twice(n) = n * 2
        .struct Point {
            |struct
        }
        .data table: .byte[] {
            |values
        }
        .scope loose {
            |scope
        }
        .segment CODE
        .macro poke(address: expr, value: const = 0) {
            |macro
        }
        .enum Pitch {
            low
            high
        }
        .macro tone(p: Pitch, w: one(up, down), s: list(Pitch)) {
        }
        .macro pick(src: operand(imm, zp), reg: one(x, y)) {
            |pick
        }
        .proc main {
        @loop:
            |body
            rts
        }
        """;

    public static TheoryData<string, string, string[], string[]> Completions => new()
    {
        // After `::`, what the module or type the path names exports, and nothing it keeps private.
        { "body", "    jsr gfx::|", ["Sprite", "SCREEN", "clear"], ["helper", "main"] },
        { "body", "    lda gfx::Sprite::|", ["x", "y"], ["clear"] },
        { "body", "    lda vic::|", ["BORDER"], ["clear"] },

        // A `.use` path starts from the root of the module names, and in its braces offers what
        // the module exports.
        { "top", ".use |", ["gfx", "hw", "main"], ["clear"] },
        { "top", ".use hw::|", ["vic"], ["gfx"] },
        { "top", ".use gfx::{|", ["SCREEN", "Sprite", "clear"], ["hw", "helper"] },

        // An operand may name anything in scope, anything a `.use` brought in, or a module to
        // write a path into.
        { "body", "    lda |", ["@loop", "clear", "gfx", "main", "twice", "vic"], ["poke", "fast", "lda"] },
        { "macro", "    sta |", ["address", "value"], ["@loop"] },

        // A statement starts with an instruction or a macro call.
        { "body", "    |", ["lda", "poke"], ["clear"] },

        // After a routine's `:`, the signature items and the named signatures. A `.state` states the
        // processor at one point, so it takes no calling convention or named signature; a macro is
        // not called, so it takes no calling convention either.
        { "top", ".proc other: |", ["a16", "a?", "a*", "dp", "far", "fast", "keeps", "near"], ["clear", "lda"] },
        { "body", "    .state |", ["a16", "dbr", "e?", "keeps", "native"], ["near", "a*", "fast"] },
        { "top", ".macro m(): |", ["a8", "e*"], ["near", "far", "keeps"] },
        { "top", ".proc other: a8, dp = |", ["twice", "vic"], ["a16"] },

        // A macro call's argument may name its parameter.
        { "body", "    poke!(|", ["address", "value", "clear"], ["lda"] },
        { "body", "    poke!(1, |", ["value"], [] },

        // A name being declared is not completed, and nothing is offered between the name and the
        // `:` or `{` that follows it.
        { "top", ".proc |", [], ["clear"] },
        { "top", ".proc other |", [], ["clear", "lda", ".proc", "near"] },
        { "top", ".macro m(|", [], ["clear", "expr"] },
        { "top", ".macro m(a: |", ["expr", "const", "operand", "block", "one", "Pitch"], ["clear", "lda"] },

        // Inside a parameter kind: the addressing modes an `operand` may list, the kinds a `list`'s
        // items may be, and nothing inside a `one`, whose words the macro's author chooses.
        { "top", ".macro m(a: operand(|", ["imm", "zpx", "longy"], ["expr", "clear", "Pitch"] },
        { "top", ".macro m(a: operand(imm, |", ["zp", "abs"], ["expr", "clear"] },
        { "top", ".macro m(a: list(|", ["const", "one", "Pitch"], ["imm", "clear"] },
        { "top", ".macro m(a: one(|", [], ["expr", "imm", "clear"] },

        // An argument is offered the values its parameter accepts: an enum's members by their bare
        // names, and a `one`'s words, whether the argument is positional or named.
        { "body", "    tone!(|", ["low", "high", "p", "w"], ["up"] },
        { "body", "    tone!(low, |", ["up", "down"], ["low"] },
        { "body", "    tone!(low, up, |", ["low", "high"], ["up"] },
        { "body", "    tone!(w = |", ["up", "down"], ["low"] },

        // In a condition, `.mode(p)` is compared with the modes the parameter allows, and a `one`
        // with its words.
        { "pick", "    .if .mode(src) == |", ["imm", "abs"], ["zp", "absx", "clear", "src"] },
        { "pick", "    .if reg != |", ["x", "y"], ["imm", "clear"] },

        // After a complete expression only an operator may follow, never a name.
        { "body", "    lda clear |", [], ["clear", "x", "#"] },
        { "body", "    lda clear + |", ["clear", "twice"], ["x", "#"] },

        // Only the statements the place accepts are offered: a file's top level holds declarations,
        // and only code holds instructions and the directives that go with them.
        { "top", "|", [".proc", ".data", ".export", ".cpu"], ["lda", "rts", "poke", ".state"] },
        { "body", "|", [".data", ".scope", ".state", ".ensure", ".byte", "lda", "poke"], [".proc", ".macro", ".cpu", ".config", ".module"] },
        { "macro", "|", ["lda", ".state", ".if"], [".proc", ".macro", ".export", ".import", ".segment"] },
        { "struct", "|", [".struct", ".union"], ["lda", ".proc", ".byte", "clear"] },
        { "values", "|", ["clear", "twice", ".sizeof"], ["lda", ".proc", ".byte"] },
        { "scope", "|", [".proc", ".macro", ".data"], ["lda", "rts", ".state"] },
        { "body", ".|", [".data", ".state", ".byte"], [".proc", ".config", "clear"] },

        // After a `:`, the storage a member or data declaration takes, and the address size of an
        // imported name; after `.cpu`, the processors.
        { "struct", "    x: |", [".byte", ".word", ".res", ".type"], ["lda", "clear"] },
        { "top", ".data d: |", [".byte", ".addr", ".res", ".incbin"], ["lda", ".proc"] },
        { "top", ".import io: |", ["zp", "abs", "far", "proc"], ["clear"] },
        { "top", ".cpu |", ["6502", "65816", "65c02"], ["clear", "lda"] },

        // A `}` is followed by the next branch of a condition, and by nothing else.
        { "top", "} |", [".else", ".elseif"], [".proc", "lda", "clear"] },

        // An operand is offered the instruction's addressing forms, and names to build an address from.
        { "body", "    lda |", ["#", "z:", "a:", "(", "clear", "@loop"], ["x", "y", "a", "f:", "["] },
        { "body", "    asl |", ["a", "z:", "clear"], ["#", "("] },
        { "body", "    jsr |", ["clear", "main"], ["#", "z:", "a:", "("] },
        { "body", "    inx |", [], ["#", "a", "clear", "lda"] },

        // After the comma, the index registers that the operand's addressing form allows.
        { "body", "    lda table, |", ["x", "y"], ["s", "#", "clear"] },
        { "body", "    lda (table), |", ["y"], ["x", "s"] },
        { "body", "    lda (table, |", ["x"], ["y", "s"] },
        { "body", "    sta table, |", ["x", "y"], ["s"] },

        // Where a number may go, the prefixes for hex (`$`), binary (`%`) and a character (`'`)
        // are offered; decimal digits need no prefix.
        { "body", "    lda #|", ["$", "%", "'"], ["x", "z:"] },
        { "body", "    lda |", ["$", "%", "'"], ["x"] },
        { "values", "|", ["$", "%", "'"], ["lda"] },
        { "body", "    inx |", [], ["$", "%", "'"] },

        // An expression may call a built-in function, and three of them exist only in a macro body.
        { "body", "    lda #|", [".sizeof", ".lobyte", "clear"], [".mode", ".byteof", "x"] },
        { "macro", "    lda #|", [".sizeof", ".mode", ".byteof", ".empty"], ["x"] },

        // Nothing is written inside a comment or a text literal.
        { "body", "    lda #1 ; load the |", [], ["lda", "clear", ".sizeof"] },
        { "top", ".error \"what went |", [], ["clear", ".proc"] },
    };

    [Fact]
    public async Task AnnouncesWhatTheEditingLayerCanDo()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.Equal(
            [" ", ".", ":", "@", "!", "#", "(", "[", ","], capabilities.CompletionProvider?.TriggerCharacters);
        Assert.Equal(["(", ",", "="], capabilities.SignatureHelpProvider?.TriggerCharacters);
        Assert.NotNull(capabilities.CodeLensProvider);
        Assert.True(capabilities.WorkspaceSymbolProvider);
        Assert.Equal(
            ["quickfix", "refactor.rewrite", "refactor.extract"], capabilities.CodeActionProvider?.CodeActionKinds);
    }

    [Theory]
    [MemberData(nameof(Completions))]
    public async Task CompletionOffersWhatMayBeWrittenThere(string where, string line, string[] offered, string[] notOffered)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Place(where, line);
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var labels = items.Select(item => item.Label).ToHashSet();
        Assert.All(offered, label => Assert.Contains(label, labels));
        Assert.All(notOffered, label => Assert.DoesNotContain(label, labels));
    }

    /// <summary>Completion replaces the part of a name already typed, and writes a named argument with its <c>=</c>.</summary>
    [Fact]
    public async Task ACompletionReplacesWhatIsTypedAndWritesWhatTheItemNeeds()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Place("body", "    poke!(val|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var value = Assert.Single(items, item => item.Label == "value");
        Assert.Equal(CompletionItemKind.Property, value.Kind);
        Assert.Equal("value = ", value.TextEdit.NewText);
        Assert.Equal(new Position(position.Line, position.Character - 3), value.TextEdit.Range.Start);
        Assert.Equal(position, value.TextEdit.Range.End);
    }

    /// <summary>
    /// An instruction that takes an operand is inserted with a trailing space and a command that
    /// asks the client to complete the operand; one that takes no operand is inserted alone.
    /// </summary>
    [Fact]
    public async Task AnInstructionThatTakesAnOperandLeadsOnToIt()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Place("body", "|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var lda = Assert.Single(items, item => item.Label == "lda");
        Assert.Equal("lda ", lda.TextEdit.NewText);
        Assert.Equal("editor.action.triggerSuggest", lda.Command?.Name);
        var inx = Assert.Single(items, item => item.Label == "inx");
        Assert.Equal("inx", inx.TextEdit.NewText);
        Assert.Null(inx.Command);
        var poke = Assert.Single(items, item => item.Label == "poke");
        Assert.Equal("poke!(", poke.TextEdit.NewText);
        Assert.Equal("editor.action.triggerSuggest", poke.Command?.Name);
    }

    /// <summary>An operand is offered the forms the CPU the program is built for actually has.</summary>
    [Theory]
    [InlineData("    lda |", new[] { "#", "(", "[", "z:", "a:", "f:", "d:" }, new[] { "x", "y", "s" })]
    [InlineData("    sta 3,|", new[] { "x", "y", "s" }, new[] { "#", "z:", "a:" })]
    [InlineData("    sta (3,|", new[] { "x", "s" }, new[] { "y" })]
    [InlineData("    sep |", new[] { "#" }, new[] { "main", "z:", "x" })]
    [InlineData("    mvn |", new[] { "#" }, new[] { "main", "z:", "a:" })]
    public async Task AnOperandOffersTheFormsTheCpuHas(string line, string[] offered, string[] notOffered)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .cpu 65816
            .segment CODE
            .proc main {
            @line
            }
            """.Replace("@line", line, StringComparison.Ordinal));
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, text));

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var labels = items.Select(item => item.Label).ToHashSet();
        Assert.All(offered, label => Assert.Contains(label, labels));
        Assert.All(notOffered, label => Assert.DoesNotContain(label, labels));
    }

    public static TheoryData<string, string, string, int> Calls => new()
    {
        { "body", "    poke!(|", "poke!(address: expr, value: const = 0)", 0 },
        { "body", "    poke!(SCREEN, |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(value = |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(twice(|", "twice(n)", 0 },
        { "body", "    poke!(twice(1), |", "poke!(address: expr, value: const = 0)", 1 },
        { "top", "X = .select(1, 2, |", ".select(condition, chosen, otherwise)", 2 },
        { "top", "X = .strsub(\"HELLO\", 1, |", ".strsub(text, start, count)", 2 },
        { "top", "X = .strcat(\"A\", 1, 2, |", ".strcat(part, ...)", 0 },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task SignatureHelpSaysWhatTheCallTakes(string where, string line, string signature, int active)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Place(where, line);
        await using var client = await OpenAsync(text, timeout);

        var help = await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        Assert.NotNull(help);
        var only = Assert.Single(help.Signatures);
        Assert.Equal(signature, only.Label);
        Assert.Equal(active, help.ActiveParameter);
    }

    [Fact]
    public async Task SignatureHelpIsNothingOutsideACall()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Place("body", "    lda (vic::BORDER),y|");
        await using var client = await OpenAsync(text, timeout);

        Assert.Null(await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout));
    }

    /// <summary>
    /// Above each routine, a lens with the cycles one pass through it costs: a range where its
    /// paths have a longest, the fewest followed by <c>+</c> where it loops, and a note of what
    /// the count leaves out.
    /// </summary>
    [Fact]
    public async Task ALensAboveEachRoutineSaysWhatOnePassThroughItCosts()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc straight {
                lda #0
                sta $10
                rts
            }
            .proc branching {
                lda $10
                beq @skip
                inx
            @skip:
                rts
            }
            .proc looping {
            @turn:
                lda $10
                bne @turn
                rts
            }
            .proc calling {
                jsr straight
                rts
            }
            .proc endless: noreturn {
            @turn:
                lda $10
                beq @turn
                jmp @turn
            }
            .proc unlaid {
                stz $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                (2, "11 cycles"),
                (7, "11-15 cycles"),
                (14, "11+ cycles, loops"),
                (20, "12 cycles, 23 with calls"),

                // No path leaves it, so there is no pass through it to put a cost on.
                (24, "never returns"),

                // `stz` is not a 6502 instruction, so the line is left out of the assembled code,
                // and a count of the rest would not be the routine's real cost. It gets no lens.
            ],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// What a routine costs including what it calls, worked out through the call graph: a call
    /// costs the call instruction plus the callee, as does a tail jump or a <c>.fallthrough</c> into
    /// another routine. What nt65 cannot count — a routine with no body, a call to an address
    /// no routine is declared at, a routine calling itself — is left out and named, and the rest is still counted,
    /// as a fewest with no most. The lens names two at most, then says how many more.
    /// </summary>
    [Fact]
    public async Task ALensSaysWhatARoutineCostsWithWhatItCalls()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65c02
            .segment CODE
            .proc CHROUT = $ffd2
            .proc RESET = $fffc: noreturn
            .proc into_leaf {
                inx
                .fallthrough leaf
            }
            .proc leaf {
                lda #0
                rts
            }
            .proc middle {
                jsr leaf
                jsr leaf
                rts
            }
            .proc onward {
                jsr middle
                rts
            }
            .proc tail {
                jmp leaf
            }
            .proc recurse {
                jsr recurse
                rts
            }
            .proc hands_off {
                jsr leaf
                jmp endless
            }
            .proc may_return {
                lda $10
                beq @die
                rts
            @die:
                jmp endless
            }
            .proc into_endless: noreturn {
                inx
                .fallthrough endless
            }
            .proc endless {
            @turn:
                jmp @turn
            }
            .proc external {
                jsr CHROUT
                rts
            }
            .proc through {
                jsr $1234
                rts
            }
            .proc two {
                jsr external
                jmp through
            }
            .proc three {
                jsr external
                jsr recurse
                jmp through
            }
            .proc quit: noreturn {
                jmp RESET
            }
            .proc bail: noreturn {
                jsr CHROUT
                jmp RESET
            }
            .data vector: .addr leaf
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                "2 cycles, 10 with calls",
                "8 cycles",
                "18 cycles, 34 with calls",
                "12 cycles, 46 with calls",
                "3 cycles, 11 with calls",
                "12 cycles, 12+ with calls, excluding recursion",

                // It jumps to a routine that never returns, so the count is the cost of getting
                // there, and the jump is not treated as a call the count could not follow.
                "9 cycles, 17 with calls, then never returns",

                // One of its paths returns, so it is not marked as never returning.
                "8-13 cycles",

                // It runs on into a routine that never returns, so it never returns either.
                "2 cycles, then never returns",
                "never returns",

                // What is left out is named however many calls away it is, and once however
                // many paths reach it.
                "12 cycles, 12+ with calls, excluding CHROUT",
                "12 cycles, 12+ with calls, excluding jsr $1234",
                "9 cycles, 33+ with calls, excluding CHROUT and jsr $1234",
                "15 cycles, 51+ with calls, excluding CHROUT, recursion and 1 more",

                // A routine with no body that is declared never to return ends the pass, as one
                // with a body does, and is not something the count leaves out.
                "3 cycles, then never returns",
                "9 cycles, 9+ with calls, excluding CHROUT, then never returns",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// The lens only has room to name what a cost with calls leaves out; the hover lists each
    /// thing with why nt65 cannot count it.
    /// </summary>
    [Fact]
    public async Task TheHoverSaysWhyEachThingACostWithCallsLeavesOutIsLeftOut()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc CHROUT = $ffd2
            .proc copy: a16, i16 {
                jsr move
                jsr CHROUT
                rts
            }
            .proc move: a16, i16 {
                mvn #$7e, #$7e
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);
        var hover = await client.HoverAsync(MainUri, new Position(4, 6), timeout);

        Assert.Equal(
            [
                "18 cycles, 18+ with calls, excluding move and CHROUT",
                "not counted: a block move takes 7 cycles a byte, and how many is in A",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
        Assert.Contains(
            """
            cost       18 cycles, 18+ with calls
            excluding  move: a block move takes 7 cycles a byte, and how many is in A
                       CHROUT: no code in the program
            """.ReplaceLineEndings("\n"),
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A block move takes seven cycles for every byte it moves, and the number of bytes is in A
    /// when it runs, so the routine containing it has no count. The lens says so rather than
    /// being left out, because a missing lens reads as though the analysis failed.
    /// </summary>
    [Fact]
    public async Task ALensSaysWhyARoutineWithABlockMoveHasNoCount()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc copy: a16, i16 {
                mvn #$7e, #$7e
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            ["not counted: a block move takes 7 cycles a byte, and how many is in A"],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// The text after a call to a routine that returns past it is never run, so it costs nothing
    /// and the routine still has a count, rather than none and no reason for it.
    /// </summary>
    [Fact]
    public async Task InlineDataAfterACallTakesNoTime()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 6502
            .import print: proc(inline .strz)
            .segment CODE
            .proc greet {
                jsr print
                .strz "hi"
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(["12 cycles, 12+ with calls, excluding print"], Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// An inline <c>.scope</c> is part of its routine, and its lens gives what one pass through
    /// the scope costs; a <c>.scope</c> at file level holds declarations and no code, and gets
    /// no lens.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeSaysWhatThatPartOfTheRoutineCosts()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc init {
                lda #0
                .scope {
                    ldx #4
                    stx $10
                }
                rts
            }
            .scope loose {
                .proc other {
                    rts
                }
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [(2, "13 cycles"), (4, "5 cycles"), (11, "6 cycles")],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// Which registers a routine preserves, in a lens beside its cost. A routine whose calls
    /// nt65 cannot follow shows <c>preserves ?</c> rather than no lens, because a missing lens
    /// would read as though the routine were safe to call.
    /// </summary>
    [Fact]
    public async Task ALensSaysWhichRegistersARoutineHandsBack()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc quiet {
                rts
            }
            .proc counts {
                lda #0
                ldx #1
                rts
            }
            .proc saves {
                pha
                lda #0
                pla
                rts
            }
            .proc rom = $FFD2
            .proc asks {
                jsr rom
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            ["preserves A, X, Y, C", "preserves Y, C", "preserves A, X, Y, C", "preserves ?"],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// A loop that counts a register down from an immediate value has a known number of turns,
    /// so its cost is a range with an upper bound rather than a minimum with <c>+</c>. A loop of
    /// any other shape still shows only the minimum, because a loop counted wrongly is worse
    /// than one not counted.
    /// </summary>
    [Fact]
    public async Task ALoopCountingARegisterDownFromAnImmediateIsCounted()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc counted {
                ldx #16
            @turn:
                sta $0200,x
                dex
                bne @turn
                rts
            }
            .proc past_zero {
                ldy #3
            @turn:
                sty $10
                dey
                bpl @turn
                rts
            }
            .proc touched {
                ldx #16
            @turn:
                ldx $10
                dex
                bne @turn
                rts
            }
            .proc from_memory {
                ldx $10
            @turn:
                sta $0200,x
                dex
                bne @turn
                rts
            }
            .proc calling {
                ldx #4
            @turn:
                jsr leaf
                dex
                bne @turn
                rts
            }
            .proc leaf {
                rts
            }
            .proc by_twos {
                ldx #4
            @turn:
                sta $0200,x
                dex
                dex
                bpl @turn
                rts
            }
            .proc uneven {
                ldx #5
            @turn:
                sta $0200,x
                dex
                dex
                bne @turn
                rts
            }
            .proc starts_negative {
                ldx #200
            @turn:
                sta $0200,x
                dex
                bpl @turn
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // 16 turns of a 9-11 cycle block, the branch taken all but the last time.
                "167-182 cycles",

                // `bpl` runs one turn past zero, so `ldy #3` is four turns.
                "39-42 cycles",

                // The loop reloads X from memory, so the count no longer follows from the immediate.
                "15+ cycles, loops",

                // The count does not start at an immediate.
                "18+ cycles, loops",

                // The call splits the loop body into two blocks, and the loop is still counted;
                // the call is made once per turn, so the callee's cost is counted four times.
                "51-54 cycles, 75-78 with calls",
                "6 cycles",

                // Two `dex` a turn step through an array of words: `ldx #4` is three turns of `bpl`.
                "43-45 cycles",

                // Stepping by two from five skips zero, so `bne` never sees it: not counted.
                "19+ cycles, loops",

                // `bpl` tests the sign bit, and 200 has it set before the loop starts: not counted.
                "17+ cycles, loops",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>A search finds declarations in files no one has open, matching names that contain the query's letters in order.</summary>
    [Fact]
    public async Task WorkspaceSymbolsFindDeclarationsByTheirLetters()
    {
        var timeout = TestTimeout.Token();
        var (text, _) = Place("body", "");
        await using var client = await OpenAsync(text, timeout);

        var found = await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("clr"), timeout);

        var clear = Assert.Single(found);
        Assert.Equal("clear", clear.Name);
        Assert.Equal("gfx", clear.ContainerName);
        Assert.Equal(GfxUri, clear.Location.Uri);
        Assert.Equal(new Position(8, 6), clear.Location.Range.Start);
        Assert.Equal([("Sprite", "gfx")], (await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("sprite"), timeout)).Select(s => (s.Name, s.ContainerName)));
    }

    /// <summary>
    /// An inline <c>.scope</c> gets its own lens of which registers it preserves, as a routine
    /// does: a block that saves a register and restores it preserves that register, even where
    /// the routine around it does not.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeSaysWhatThatPartOfTheRoutinePreserves()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                lda #1
                .scope {
                    pha
                    ldx #2
                    stx $10
                    pla
                }
                sta $11
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        // The routine clobbers A and X; the block restores A, so the only register it clobbers is X.
        Assert.Equal(
            [(2, "preserves Y, C"), (4, "preserves A, Y, C")],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// An editor can be set to hide lenses, so which registers a routine or an inline
    /// <c>.scope</c> block preserves is shown on hover as well as in the lens above the line.
    /// </summary>
    [Fact]
    public async Task HoverOnARoutineAndOnAScopeSaysWhatItPreserves()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                lda #1
                .scope {
                    pha
                    ldx #2
                    stx $10
                    pla
                }
                sta $11
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var routine = await client.HoverAsync(MainUri, new Position(2, 7), timeout);
        var scope = await client.HoverAsync(MainUri, new Position(4, 6), timeout);

        Assert.Contains("```nt65\n.proc main\n```", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves  Y, C", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("```nt65\n.scope\n```", scope?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves  A, Y, C", scope?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover on a line shows what each register holds there, beside what the line costs. A
    /// register may hold the value another register had on entry, which is how a 6502 saves X
    /// (by copying it to A), and naming that register makes the save readable.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhatTheRegistersHoldAtTheLine()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txa
                ldy #0
                sty $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var entry = await client.HoverAsync(MainUri, new Position(3, 4), timeout);
        var after = await client.HoverAsync(MainUri, new Position(5, 4), timeout);

        Assert.Contains(
            "A       as entered\nX       as entered\nY       as entered\nC       as entered\n```",
            entry?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "A       X as entered\nX       as entered\nY       new\nC       as entered\n```",
            after?.Contents.Value,
            StringComparison.Ordinal);

        // The routine pushes nothing, and an empty stack is shown by leaving the stack group out.
        Assert.DoesNotContain("stack", after?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a register may hold is a set: where two paths meet, it may hold its entry value on
    /// one path and a newly loaded value on the other. Reducing that to "unknown" would hide a
    /// save that is still valid on one path, so both descriptions are shown, joined by "or".
    /// </summary>
    [Fact]
    public async Task HoverSpellsOutWhatTwoPathsLeaveInARegister()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                ldx $10
                beq @skip
                lda #1
            @skip:
                sta $11
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(7, 4), timeout);

        // One path falls through the `lda` and the other branches over it.
        Assert.Contains(
            "A       as entered, or new\nX       new\nY       as entered\nC       as entered",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover lists what the routine has pushed, top of the stack first, with what each push
    /// saved: what a <c>pla</c> is about to get back is what a reader wants to know.
    /// </summary>
    [Fact]
    public async Task HoverListsWhatTheRoutineHasPushed()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txa
                pha
                php
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(6, 4), timeout);

        // The `php` is on top; under it is the accumulator, which `txa` filled with X.
        Assert.Contains(
            "C       as entered\n\nstack   C as entered\n        X as entered\n```",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the analysis has lost track of the stack, hover says so. Leaving the stack group out
    /// means the stack is empty, and a stack nothing is known about is not an empty one.
    /// </summary>
    [Fact]
    public async Task HoverSaysSoWhereTheStackIsNotKnown()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txs
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(4, 4), timeout);

        Assert.Contains("\nstack   unknown", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reader needs the top of the stack, which is what the routine is about to pull back, so
    /// a deep stack is cut off with a count of the entries not shown rather than listed in full.
    /// </summary>
    [Fact]
    public async Task HoverCountsThePushesItDoesNotList()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                pha
                pha
                pha
                pha
                pha
                pha
                pha
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(10, 4), timeout);

        Assert.Contains("        A as entered\n        and 1 more\n```", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.frame</c> declares the bytes it covers as one structure the routine has pushed, so
    /// hover shows them as one row however many pushes built them.
    /// </summary>
    [Fact]
    public async Task HoverReadsAFrameAsOnePush()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .struct Locals {
            a:      .word
            b:      .word
            }
            .segment CODE
            .proc p: a16, i8 {
                pea $0000
                pea $1234
                .frame vars: Locals
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(11, 4), timeout);

        Assert.Contains("\nstack   frame vars\n```", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// On the 65816 the processor-state analysis supplies what the stack tracking alone cannot
    /// know: which status a <c>php</c> saved, and how wide each pushed register was, which
    /// decides whether a later pull restores the value at all.
    /// </summary>
    [Fact]
    public async Task HoverNamesA65816PushAndSaysHowWideItWas()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc p: a8, i8 {
                pha
                php
                phx
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, new Position(7, 4), timeout);

        Assert.Contains(
            "stack   X as entered, 8-bit\n        status a8, i8\n        A as entered, 8-bit\n```",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The lenses that give what a pass costs, which is what these tests check. The lens that
    /// says which registers are preserved is left out; it has tests of its own.
    /// </summary>
    private static IEnumerable<CodeLens> Costs(IEnumerable<CodeLens> lenses) =>
        lenses.Where(lens => !lens.Command.Title.Contains("preserves", StringComparison.Ordinal));

    /// <summary>
    /// Returns the main file with <paramref name="line"/> at the place marked
    /// <paramref name="where"/>, the other marked places left empty, and the caret position
    /// given by the line's own <c>|</c>. The place <c>top</c> puts the line at top level, after
    /// everything else. Any other name the file does not mark is a mistake in the test and
    /// throws, so that a misspelled place cannot quietly test top level instead.
    /// </summary>
    private static (string Text, Position Position) Place(string where, string line)
    {
        var text = Main.ReplaceLineEndings("\n");
        var lines = text.Split('\n').ToList();
        var at = where == "top" ? lines.Count : lines.FindIndex(l => l.Trim() == "|" + where);
        if (at < 0)
            throw new ArgumentException($"the main file marks no place named \"{where}\"", nameof(where));
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('|'))
                lines[i] = "";
        }
        if (at == lines.Count)
            lines.Add(line);
        else
            lines[at] = line;
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Replace("|", "", StringComparison.Ordinal);
        return (string.Join('\n', lines), new Position(at, Math.Max(0, column)));
    }

    private static Task<TestClient> OpenAsync(string main, CancellationToken timeout) =>
        TestClient.OpenedAsync(timeout, (GfxUri, Gfx.ReplaceLineEndings("\n")), (VicUri, Vic), (MainUri, main));
}
