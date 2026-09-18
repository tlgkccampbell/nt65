using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets while code is being written: completion, help with a call, the hints in
/// the lines, and a search for a declaration across the workspace.
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
    /// The file completion is asked in. Each <c>|name</c> marks a place a test may put its own
    /// line, and the name it asks for it by; a test that asks for none writes its line at the
    /// file's top level.
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
        .proc main {
        @loop:
            |body
            rts
        }
        """;

    public static TheoryData<string, string, string[], string[]> Completions => new()
    {
        // After `::`, what the path leads to exports; nothing it keeps private.
        { "body", "    jsr gfx::|", ["Sprite", "SCREEN", "clear"], ["helper", "main"] },
        { "body", "    lda gfx::Sprite::|", ["x", "y"], ["clear"] },
        { "body", "    lda vic::|", ["BORDER"], ["clear"] },

        // A `.use` starts at the modules' root, and in its braces names what the module exports.
        { "top", ".use |", ["gfx", "hw", "main"], ["clear"] },
        { "top", ".use hw::|", ["vic"], ["gfx"] },
        { "top", ".use gfx::{|", ["SCREEN", "Sprite", "clear"], ["hw", "helper"] },

        // An operand may name anything in scope, what `.use` brought in, and a module to walk into.
        { "body", "    lda |", ["@loop", "clear", "gfx", "main", "twice", "vic"], ["poke", "fast", "lda"] },
        { "macro", "    sta |", ["address", "value"], ["@loop"] },

        // A statement starts with an instruction or a macro call.
        { "body", "    |", ["lda", "poke"], ["clear"] },

        // A signature's items, and the signature sets; a `.state` asserts a point, and a macro is not called.
        { "top", ".proc other: |", ["a16", "a?", "a*", "dp", "far", "fast", "keeps", "near"], ["clear", "lda"] },
        { "body", "    .state |", ["a16", "dbr", "e?", "keeps", "native"], ["near", "a*", "fast"] },
        { "top", ".macro m(): |", ["a8", "e*"], ["near", "far", "keeps"] },
        { "top", ".proc other: a8, dp = |", ["twice", "vic"], ["a16"] },

        // A macro call's argument may name its parameter.
        { "body", "    poke!(|", ["address", "value", "clear"], ["lda"] },
        { "body", "    poke!(1, |", ["value"], [] },

        // A name being declared is not completed, and nothing stands between it and the `:`
        // or the `{` the declaration goes on with.
        { "top", ".proc |", [], ["clear"] },
        { "top", ".proc other |", [], ["clear", "lda", ".proc", "near"] },
        { "top", ".macro m(|", [], ["clear", "expr"] },
        { "top", ".macro m(a: |", ["expr", "const", "operand", "block", "one"], ["clear", "lda"] },

        // Past what finishes an expression an operator goes, and never a name.
        { "body", "    lda clear |", [], ["clear", "x", "#"] },
        { "body", "    lda clear + |", ["clear", "twice"], ["x", "#"] },

        // A statement is only what the place it is written in accepts: a file's top level
        // declares things, and only code holds instructions and what they need.
        { "top", "|", [".proc", ".data", ".export", ".cpu"], ["lda", "rts", "poke", ".state"] },
        { "body", "|", [".data", ".scope", ".state", ".ensure", ".byte", "lda", "poke"], [".proc", ".macro", ".cpu", ".config", ".module"] },
        { "macro", "|", ["lda", ".state", ".if"], [".proc", ".macro", ".export", ".import", ".segment"] },
        { "struct", "|", [".struct", ".union"], ["lda", ".proc", ".byte", "clear"] },
        { "values", "|", ["clear", "twice", ".sizeof"], ["lda", ".proc", ".byte"] },
        { "scope", "|", [".proc", ".macro", ".data"], ["lda", "rts", ".state"] },
        { "body", ".|", [".data", ".state", ".byte"], [".proc", ".config", "clear"] },

        // What a `:` asks for: what a member holds, and how wide an imported name is.
        { "struct", "    x: |", [".byte", ".word", ".res", ".type"], ["lda", "clear"] },
        { "top", ".data d: |", [".byte", ".addr", ".res", ".incbin"], ["lda", ".proc"] },
        { "top", ".import io: |", ["zp", "abs", "far", "proc"], ["clear"] },
        { "top", ".cpu |", ["6502", "65816", "65c02"], ["clear", "lda"] },

        // A `}` is followed by the next branch of a condition, and by nothing else.
        { "top", "} |", [".else", ".elseif"], [".proc", "lda", "clear"] },

        // An operand is the forms the instruction has, and the names an address is made of.
        { "body", "    lda |", ["#", "z:", "a:", "(", "clear", "@loop"], ["x", "y", "a", "f:", "["] },
        { "body", "    asl |", ["a", "z:", "clear"], ["#", "("] },
        { "body", "    jsr |", ["clear", "main"], ["#", "z:", "a:", "("] },
        { "body", "    inx |", [], ["#", "a", "clear", "lda"] },

        // What indexes the address, which is what the form the operand is in allows.
        { "body", "    lda table, |", ["x", "y"], ["s", "#", "clear"] },
        { "body", "    lda (table), |", ["y"], ["x", "s"] },
        { "body", "    lda (table, |", ["x"], ["y", "s"] },
        { "body", "    sta table, |", ["x", "y"], ["s"] },

        // A number is written in hex, in binary, as a character, or as the digits that need
        // no mark at all.
        { "body", "    lda #|", ["$", "%", "'"], ["x", "z:"] },
        { "body", "    lda |", ["$", "%", "'"], ["x"] },
        { "values", "|", ["$", "%", "'"], ["lda"] },
        { "body", "    inx |", [], ["$", "%", "'"] },

        // An expression may call a built-in function, and three of them only a macro body has.
        { "body", "    lda #|", [".sizeof", ".lobyte", "clear"], [".mode", ".byteof", "x"] },
        { "macro", "    lda #|", [".sizeof", ".mode", ".byteof", ".empty"], ["x"] },

        // Nothing is written inside a comment or a text literal.
        { "body", "    lda #1 ; load the |", [], ["lda", "clear", ".sizeof"] },
        { "top", ".error \"what went |", [], ["clear", ".proc"] },
    };

    [Fact]
    public async Task AnnouncesWhatTheEditingLayerCanDo()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        var timeout = TestContext.Current.CancellationToken;
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
        var timeout = TestContext.Current.CancellationToken;
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
    /// An instruction that takes an operand is written with the space before it and asks the
    /// client for what may go there; one that takes none is written on its own.
    /// </summary>
    [Fact]
    public async Task AnInstructionThatTakesAnOperandLeadsOnToIt()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Caret("""
            .module main
            .cpu 65816
            .segment CODE
            .proc main {
            @line
            }
            """.Replace("@line", line, StringComparison.Ordinal));
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, text);
        await client.NextDiagnosticsAsync(timeout);

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
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task SignatureHelpSaysWhatTheCallTakes(string where, string line, string signature, int active)
    {
        var timeout = TestContext.Current.CancellationToken;
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
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Place("body", "    lda (vic::BORDER),y|");
        await using var client = await OpenAsync(text, timeout);

        Assert.Null(await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout));
    }

    /// <summary>
    /// Above each routine, what one pass through it costs: an interval where its paths have a
    /// longest, the fewest and a <c>+</c> where it loops, and what the count does not follow.
    /// </summary>
    [Fact]
    public async Task ALensAboveEachRoutineSaysWhatOnePassThroughItCosts()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                (2, "11 cycles"),
                (7, "11-15 cycles"),
                (14, "11+ cycles, loops"),
                (20, "12 cycles, 23 cycles with calls"),

                // No path leaves it, so there is no pass through it to put a cost on.
                (24, "never returns"),

                // `stz` is not on the 6502, so the line is left out of the stream and what
                // is left of the routine is not what it would cost. It gets no lens at all.
            ],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// What a routine costs with what it calls, worked out through the call graph: a call
    /// costs the call and then the callee, a tail jump the same, and a routine that reaches
    /// itself, one with no body, or one through a pointer leaves no total to give.
    /// </summary>
    [Fact]
    public async Task ALensSaysWhatARoutineCostsWithWhatItCalls()
    {
        var timeout = TestContext.Current.CancellationToken;
        const string Source = """
            .module main
            .cpu 65c02
            .segment CODE
            .proc CHROUT = $ffd2
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
            .proc endless {
            @turn:
                jmp @turn
            }
            .proc external {
                jsr CHROUT
                rts
            }
            .data vector: .addr leaf
            """;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                "8 cycles",
                "18 cycles, 34 cycles with calls",
                "12 cycles, 46 cycles with calls",
                "3 cycles, 11 cycles with calls",
                "12 cycles, not counting calls",

                // It hands control to a routine that never comes back, so the count is what
                // it takes to get there and not a call it could not follow.
                "9 cycles, 17 cycles with calls, then never returns",

                // One way out of it returns, so it is not a routine that never comes back.
                "8-13 cycles",
                "never returns",
                "12 cycles, not counting calls",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// An inline <c>.scope</c> is a part of its routine and costs what a pass through it
    /// costs; one at file level holds declarations and no code, and has nothing to say.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeSaysWhatThatPartOfTheRoutineCosts()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [(2, "13 cycles"), (4, "5 cycles"), (11, "6 cycles")],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// What a routine hands back, beside what it costs. Most routines work in the accumulator
    /// and leave the rest alone, so it is said as what they do not keep; a routine whose calls
    /// nt65 cannot follow says its answer is not one, because silence would read as safety.
    /// </summary>
    [Fact]
    public async Task ALensSaysWhichRegistersARoutineHandsBack()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            ["preserves A, X, Y, C", "preserves Y, C", "preserves A, X, Y, C", "preserves ?"],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// A loop that counts a register down from an immediate says how many turns it takes, so
    /// what it costs is a bound and not a floor. A loop that is any other shape keeps the
    /// floor it had, because a loop counted wrongly is worse than one not counted.
    /// </summary>
    [Fact]
    public async Task ALoopCountingARegisterDownFromAnImmediateIsCounted()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // 16 turns of a 9-11 cycle block, the branch taken all but the last time.
                "167-182 cycles",

                // `bpl` runs one turn past zero, so `ldy #3` is four turns.
                "39-42 cycles",

                // The loop loads X itself, so where it has got to is not the immediate.
                "15+ cycles, loops",

                // The count does not start at an immediate.
                "18+ cycles, loops",

                // The call cuts the turn into two blocks, and the loop is read all the same;
                // the call is made once a turn, so it counts four times over.
                "51-54 cycles, 75-78 cycles with calls",
                "6 cycles",

                // Two `dex` a turn walk an array of words: `ldx #4` is three turns of `bpl`.
                "43-45 cycles",

                // A stride of two does not bring five down to zero, so `bne` never sees it.
                "19+ cycles, loops",

                // `bpl` reads the sign, and 200 has it set before the loop starts.
                "17+ cycles, loops",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>A search finds declarations in files no one has open, by the letters of their names in order.</summary>
    [Fact]
    public async Task WorkspaceSymbolsFindDeclarationsByTheirLetters()
    {
        var timeout = TestContext.Current.CancellationToken;
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
    /// An inline <c>.scope</c> is a part of its routine and is asked the same question of
    /// itself: a block that saves a register and gives it back keeps it, even where the routine
    /// around it does not.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeSaysWhatThatPartOfTheRoutinePreserves()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        // The routine loses A and X; the block gives A back, so all it costs the routine is X.
        Assert.Equal(
            [(2, "preserves Y, C"), (4, "preserves A, Y, C")],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// A lens is something an editor can be told not to show, so what a routine and an inline
    /// <c>.scope</c> block hand back is on hover as well as above the line.
    /// </summary>
    [Fact]
    public async Task HoverOnARoutineAndOnAScopeSaysWhatItPreserves()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var routine = await client.HoverAsync(MainUri, new Position(2, 7), timeout);
        var scope = await client.HoverAsync(MainUri, new Position(4, 6), timeout);

        Assert.Contains("preserves Y, C", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves A, Y, C", scope?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the registers hold at a line, on hover, beside what it costs. A register may hold
    /// what another was entered with, which is how a 6502 saves X, and saying which one is what
    /// makes the save readable.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhatTheRegistersHoldAtTheLine()
    {
        var timeout = TestContext.Current.CancellationToken;
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
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var entry = await client.HoverAsync(MainUri, new Position(3, 4), timeout);
        var after = await client.HoverAsync(MainUri, new Position(5, 4), timeout);

        Assert.Contains(
            "registers here:\n\n```text\nA  as entered\nX  as entered\nY  as entered\nC  as entered\n```",
            entry?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "registers here:\n\n```text\nA  as X entered\nX  as entered\nY  set\nC  as entered\n```",
            after?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The lenses that say what a pass costs, which is what these tests are about: what is kept
    /// is a lens of its own and has a test of its own.
    /// </summary>
    private static IEnumerable<CodeLens> Costs(IEnumerable<CodeLens> lenses) =>
        lenses.Where(lens => !lens.Command.Title.Contains("preserves", StringComparison.Ordinal));

    /// <summary>
    /// The main file with <paramref name="line"/> in the place <paramref name="where"/> marks,
    /// the other marked places left empty, and where the line's own <c>|</c> is. A place the
    /// file does not mark is its top level, past everything else.
    /// </summary>
    private static (string Text, Position Position) Place(string where, string line)
    {
        var text = Main.ReplaceLineEndings("\n");
        var lines = text.Split('\n').ToList();
        var at = lines.FindIndex(l => l.Trim() == "|" + where);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('|'))
                lines[i] = "";
        }
        if (at < 0)
        {
            at = lines.Count;
            lines.Add(line);
        }
        else
        {
            lines[at] = line;
        }
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Replace("|", "", StringComparison.Ordinal);
        return (string.Join('\n', lines), new Position(at, Math.Max(0, column)));
    }

    /// <summary>A source with its <c>|</c> taken out, and where that was.</summary>
    private static (string Text, Position Position) Caret(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
        var at = lines.FindIndex(l => l.Contains('|', StringComparison.Ordinal));
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Replace("|", "", StringComparison.Ordinal);
        return (string.Join('\n', lines), new Position(at, column));
    }

    private static async Task<TestClient> OpenAsync(string main, CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx.ReplaceLineEndings("\n"));
        await client.OpenAsync(VicUri, Vic);
        await client.OpenAsync(MainUri, main);
        await client.NextDiagnosticsAsync(MainUri, timeout);
        return client;
    }
}
