# Input sources at the caret

This document specifies an editor feature for nt65 and is written to be handed to the agent that
implements it. It covers the behaviour, the analysis, the protocol, the client and the order of
work. The decisions in it were made with the owner on 2026-10-04; the open questions at the end
are the only ones left.

Two mockups show the intended look. The first is a sketch, and the second applies the feature to
real code from `examples/monitor/lib/memory.nt65`:

- https://claude.ai/artifact/RrxYVcKfPhBm8WSM3doQD1
- https://claude.ai/artifact/PRAEkcRhUjoyZ8JcHr2DdW

## What it does

When the caret is on an instruction, the editor shows where each value that the instruction reads
was set. On a `jsr`, the values are the ones the callee reads, so the developer sees what is being
passed to the routine without reading back through it.

```
280    peek!(addr)              ▌A        ← source, tinted in A's colour
281    cmp #text(' ')
282    bcc @unprintable
283    cmp #text('Z') + 1
284    bcc @printable
285 @unprintable:
286    lda #text('.')           ▌A        ← source, tinted in A's colour
287 @printable:
288    jsr putc|      A ×2                ← caret line, one chip per input
```

The feature answers a question that DESIGN.md already raises and leaves to the editor. Its
section "What a routine keeps" declines to warn at a caller that holds a register across a call,
because "a warning is the wrong shape for the answer", and says "that is something to show". This
feature is that showing. **It never produces a diagnostic.** Nothing in it warns, errors or feeds
a check, and that is what lets the memory part be best effort.

### Terms

- An **input** is one thing the instruction at the caret reads. It is a register (A, X, Y), a
  flag (C, Z, N, V), on the 65816 a register width (M, X), or in pass 2 a memory location.
- A **source** is a place where an input's value was set, on a path that reaches the caret.
- A **through line** is a line the value passed through unchanged on its way to the caret. It is
  a call that keeps the register, or the `pla` that restores a value pushed earlier.
- The **scope** is the routine that holds the caret, which is its `FlowRegion`.

### Kinds of source

| Kind | Meaning | Where it is drawn |
|---|---|---|
| Instruction | An instruction in the scope wrote the value. Transfers (`tax`, `tay`) count as writes. | The instruction's line. |
| Macro | The writing instruction came from a macro expansion. | The outermost macro call in this file. |
| Call | A call wrote it, or the callee does not keep it. | The `jsr` line. |
| Entry | No write in the scope reaches the caret on this path, so the caller set it. | The routine's opening line. |
| Unknown | The analysis lost track. It records the line that caused that, called the **blocker**. | Nothing is drawn at the blocker; the chip shows `?` and the hover names the blocker. |

A call is a source for every register it does not keep, whether the callee returns a value there
or just destroys it. That covers `dump_line`, which returns the carry, without a `returns`
declaration, which nt65 does not have. A callee whose contract is incomplete (a ROM routine with no
`keeps`, or `jsr (ptr)` with no `.next`) gives an Unknown with the call as the blocker, not a Call
source.

The `pha` … `pla` pair passes a value through. The source is whatever set the register before the
`pha`, and the `pla` is a through line. A call that keeps the register is also a through line.

### What counts as an input

- On a `jsr`, or a `jmp` to a label with a signature (a tail call), the inputs are what the callee
  reads, from `FlowRegion.Reads` (`RoutineReads`). That covers A, X, Y and C. On the 65816 the
  callee's declared entry widths are inputs too.
- On any other instruction, the inputs are what the instruction itself reads: its register reads
  from `InstructionFacts`, plus the flags it reads. A `bcs` reads C, a `beq` reads Z, an `adc` reads
  A and C, and a `sta` reads A.
- On a macro call line, there are no inputs in pass 1. It is listed under later work.
- On a line inside a macro definition's body, there are no inputs. Each expansion would give a
  different answer.

## How it looks

Meaning is carried by colour, highlights and short glyphs. Text on the line is kept to a few
characters; anything longer is in the hover. The owner has run into overcrowded lines from inlays
before, so treat this as a firm rule.

Each input has a colour. Registers A, X and Y have one each. The flags share one colour and are
told apart by their letter. The two widths share one colour. Memory has one colour.

**Source lines** get:
- a tint across the line in the input's colour;
- a solid bar at the line's left edge, drawn as a whole-line left border, not in the glyph margin,
  where breakpoints live;
- a small tag after the code with the input's name, such as `A`;
- a mark in the overview ruler (the scrollbar) in the input's colour.

**Through lines** get a dotted left bar and a hollow tag. They get no tint.

**Best-effort sources** (memory, pass 2) get a fainter tint, a dashed bar, a dashed tag and a
dashed ruler mark.

**The caret line** gets one chip per input, after the code:

| Chip | Meaning |
|---|---|
| `A` | Every source of A is visible on screen. |
| `A↑12` | The nearest source is 12 lines above. |
| `A↓3` | The nearest source is below, reached around a loop. |
| `A ×2` | There are two sources. A count can follow a distance: `A↑12 ×2`. |
| `A↰` | A comes from the routine's caller on every path. |
| `A↰ ×2` | Some paths set A in the routine and some do not. |
| `A?` | At least one path loses track of A. |

The client works out the distance and visibility from the editor's visible ranges, so the chips
update on scroll without a new request.

**The routine's opening line** gets the same `↰` chip for each input that comes from the caller.
With VS Code's sticky scroll, that line is often pinned at the top of the editor, which helps in
long routines.

**The hover** on the caret line lists each input, the line number of each source, and the code on
that line. For Unknown sources it names the blocker and why it stopped the analysis, in a short
phrase: "`jsr (vector)` has no `.next`" or "`CHROUT` does not declare what it keeps". For memory
sources it says what might have changed the value. This is the only place where explanations are
written out.

**Navigation:** two commands move the caret through the sources of the inputs on the caret line
in order, and back. A third opens VS Code's peek view (`editor.action.peekLocations`) listing all
of them. Note that `Alt+↑`/`Alt+↓` move lines in VS Code, so the keybindings need to be chords
that are unbound by default. Choose them and check against the default keymap.

## The analysis

### Why a separate walk

The register walk (`RegisterWalk`, solved by `Dataflow<RegisterState>`) runs program-wide during
`RegisterKeeps.Compose`. Its values (`RegisterValue`) record the entry registers a value could hold
and whether it was written, but not where. It also tracks only A, A's high byte, X, Y and C.

Adding origin sets to `RegisterValue` would make the program-wide fixed point carry sets of steps
for every register in every routine on every analysis, for a feature that asks about one routine at
a time. Add a second walk instead:

- **`SourceWalk`** in `src/Norristown.Core/Flow/` solves one `FlowRegion` on demand, when the
  editor asks. It reuses the region's blocks (`ControlFlow`, `BasicBlock`), the solver
  (`Dataflow<TState>`), the instruction effects (`RegisterEffects`, `InstructionFacts`) and the
  callee contracts already composed (`RegisterKeeps.Of` and `ReadsOf`, or `FlowRegion.Registers`
  and `Reads` for each callee).
- Its value per input is a set of origins plus a set of through steps, both merged by union. Within
  one routine the sets stay small, and because they are drawn from a finite set of steps the fixed
  point terminates. The `Dataflow` change check compares states, so the sets need value equality.
  An immutable sorted set of `StepKey`s, or a small sorted array, works.

A second walk can drift from the first. Guard that with a test: for every statement in the example
corpus, the source walk's answer must agree with `RegisterStates.Before` about whether each register
holds an entry value, a written value or an unknown. This test is required, not optional.

### The state

Per input:

- **A, X, Y, C.** Follow `RegisterWalk.Step` closely. Transfers give the target the source's
  origins with the transfer as a new Instruction origin. The rule is that a transfer is where the
  register was set, so it is the source. Writes give a fresh Instruction origin. `brk` and `cop` make
  everything Unknown with that step as the blocker.
- **A's high byte on the 65816.** Track it as `RegisterWalk` does, and report it under A. When the
  caret instruction reads A at 16 bits, A's sources are the union of both halves.
- **N, Z, V.** These are new to the analysis; the existing `Registers` enum leaves N and Z out on
  purpose, so do not add them there. Add a structured table of the flags each instruction writes and
  reads, beside `InstructionFacts`. Today `Mnemonics.Flags` gives the written flags only as a display
  string. The table must cover `plp` and `rti`, which write every flag, and `bit`, which writes N and
  V from memory.
- **The stack.** Mirror `SavedStack`: a push saves the current origin set and a pull restores it,
  adding the pull to the through set. Where `SavedStack` gives up (a size or width mismatch, or a
  pull below the routine's entry), give Unknown with the pull as the blocker.
- **Calls.** Use `RegisterKeeps.Of(target)`. A register the callee keeps passes through, with the
  `jsr` added to its through set. A register it does not keep gets a Call origin at the `jsr`. When
  the contract is incomplete, or the block `CallsUnknown`, the register gets Unknown with the `jsr`
  as the blocker. N, Z and V after any call get a Call origin. Several `.next` targets merge, as in
  `RegisterWalk.Calls`.
- **Entry.** Seed every input with an Entry origin at the region's start. Labels that other routines
  enter (`RegisterWalk.Solve(region, of, start)`) are not a concern, because the walk always starts
  from the routine's own start.
- **`.state keeps` and `.state saves`.** Treat these as `RegisterWalk.Asserted` and `Saved` do:
  `.state keeps x` restores X's entry origin, with the directive as a through line.
- **Widths (65816).** Widths come from `StateAnalysis`, which keeps no origins either. Extend the
  source walk with a width origin per width register. `rep`, `sep`, `plp`, `xce` and `.ensure` are
  Instruction origins at their line. A call's exit state is a Call origin. The signature's entry
  state is an Entry origin. A macro with a state signature is a Macro origin at its call. Follow
  `StateAnalysis.Through`, `Flags`, `Ensured` and `Called` for the rules.

Branch conditions are not used to refine paths. The analysis is at block granularity, as the
existing walks are. A source counts if any path through the blocks reaches the caret.

### Spans

A step's `Statement` is the macro body's node when the step came from an expansion, possibly in
another file. Map every origin, through step and blocker to a span in the caret's file the way
`StateChecks.ReportAt` does: use the outermost macro call in this file when the statement is inside
a macro body, and the statement's own span otherwise. Block arguments, which are the caller's own
lines, report at their own span. Every range in a result is then in the caret's document.

The Entry span is the line that opens the routine. A caret in a macro definition body gets an empty
result.

## The protocol

Add a request on the template of `nt65/expansion`. It reuses `Server.AtAsync` to reach the analysis
at a position.

- **Method:** `nt65/sources`.
- **Params:** `TextDocumentPositionParams`.
- **Result:** `SourcesResult?`, null when the caret is not on an instruction or the analysis has no
  answer.

```
SourcesResult
  routine: Range                    the opening line of the routine, for Entry chips
  inputs:  SourcesInput[]

SourcesInput
  name:     string                  "A", "X", "Y", "C", "Z", "N", "V", "M", "X width"; or a symbol name in pass 2
  group:    string?                 pass 2: the root symbol for grouping, such as "banks" for banks::source
  category: "register" | "flag" | "width" | "memory"
  sources:  SourceSpan[]
  through:  Range[]

SourceSpan
  range:      Range                 the whole line to highlight
  kind:       "instruction" | "macro" | "call" | "entry" | "unknown"
  confidence: "proven" | "bestEffort"
  blocker:    Range?                for kind "unknown"
  reason:     string?               for kind "unknown" and for best-effort sources; a short phrase
```

Protocol types go in `src/Norristown.LanguageServer/Protocol/` as `internal sealed record`s, one
per file, serialized camelCase like the others. Pass 1 returns only `proven` sources, but the field
exists from the start so pass 2 does not change the protocol.

The hover rows need each source line's text; the client reads it from the document rather than the
server sending it.

## The client

The work goes in `editors/vscode/views.js`, beside the source-to-output follow that already uses
decorations there, or in a new `sources.js` if it grows large. Today the selection handler uses only
answers already fetched. This feature is the first to send a request when the caret moves, so:

- Debounce the request by about 100 ms after the caret stops.
- Skip the request when the caret stays on the same line in the same document version.
- Cancel or ignore a response that arrives after the caret has moved on.
- Clear every decoration when the result is null, the document changes, or the editor loses focus.

Decorations:

- Create one decoration type per input colour and per style (source, through, best-effort, chip,
  tag), once, at activation.
- Use `isWholeLine` with `backgroundColor` for the tint, and `borderWidth: '0 0 0 3px'` with
  `borderStyle` solid, dotted or dashed for the bar.
- Use `after` attachments for tags and chips. Rounded corners are not a supported attachment
  option; adding `border-radius` through the `textDecoration` string is the usual workaround.
- Use `overviewRulerColor` and `overviewRulerLane` for the ruler marks.
- Recompute chips, which depend on what is visible, in `onDidChangeTextEditorVisibleRanges` without
  a new request.

`package.json` additions:

- `contributes.colors`: one colour and one background colour per input group (A, X, Y, flags,
  widths, memory), with `dark`, `light` and `highContrast` defaults. Themes and users can then
  override them. Every colour appears with its input's letter, so colour is never the only signal.
- `contributes.commands`: next source, previous source, peek sources, and a toggle for the
  feature.
- `contributes.keybindings` for next and previous, with a `when` clause on a context key that the
  client sets while there is a result.
- `contributes.configuration`: `nt65.sources.enabled`, default true.

`tests/Norristown.Tests/Editors/ExtensionTests.cs` already checks that contributed commands match
`registerCommand` calls; the new commands must pass it.

## Documentation

- DESIGN.md, section "What tooling gets", says an instruction's per-instruction facts are "on
  hover rather than in the line". This feature puts short chips on the caret line and tints other
  lines. Amend that bullet to describe caret-driven source highlights, and keep the rule for
  everything else. Tell the owner in the summary that this sentence moved.
- DESIGN.md, section "What a routine keeps", ends with "that is something to show". Add a sentence
  that names this feature as where it is shown.
- When pass 2 lands, DESIGN.md must say that memory sources are best effort and feed no check. The
  same section says memory behaviour is "the programmer's word", and pass 2 must not read as going
  back on that.
- docs/GUIDE.md, section "In the editor": describe what the highlights mean, the chips, and the
  navigation commands.

## Order of work

Each step ends with the full test gate green and a commit to `main`.

1. **Source walk for A, X, Y and C** in Core, with flow tests built on `FlowFragment.Analyze` and a
   way to ask "sources at the line with this text", following `FlowFragment.StateAt`. Include the
   cross-check against `RegisterStates`.
2. **Flags N, Z and V:** the read and write table, and the walk changes.
3. **The `nt65/sources` request**, with language-server tests using `Caret.In` and
   `TestClient.RequestAsync`, on the pattern of `ViewRequestsTests`.
4. **The client:** decorations, chips, ruler marks, hover, commands, colours and the setting.
5. **65816 widths.**
6. **Documentation.**

That is pass 1. **The feature is not complete until pass 2 lands.** The owner said so because
passing arguments in memory is common.

### Pass 2: memory

Memory sources are best effort by design. Nothing that warns or errors may depend on them.

- **Which locations a callee reads:** infer it. A location is a callee input when the callee loads
  it, on some path, before storing to it, directly or through a callee of its own. Only direct
  addressing on a resolved symbol counts. Indexed and indirect reads are not inputs.
- **Sources:** the last direct store (`sta`, `stx`, `sty`, `stz`, and read-modify-write instructions
  such as `inc`) to the same symbol on each path to the caret.
- **Things that do not stop the search, but are named in the hover and lower the confidence:**
  - an indexed or indirect store that might hit the location;
  - a call that might write it, judged by the same inference applied to the callee's stores;
  - a store to a different symbol at the same address.
- **Things that stop the search with an Unknown:** a hardware register, meaning an address the
  platform declares as I/O, if nt65 knows of any; otherwise reaching the routine's entry gives an
  Entry source as usual.
- **Grouping:** fields of one struct, and offsets from one symbol (`ptr`, `ptr+1`), group under one
  chip, named by the root symbol.

## Later work, not in either pass

- A **flow rail**: a thin coloured bar in the left margin, running from a source to the caret, so a
  source off the top of the screen shows as a bar running off the top. The bar can be drawn as a
  whole-line border on the lines in between, but more than one register needs a CSS workaround
  through `textDecoration`. Prototype it with one input before deciding.
- Inputs at a **macro call line**: what the expansion reads before writing it.
- Showing the instruction inside the macro that set the value, in the hover for a Macro source.

## Open questions

1. **Keybindings** for next and previous source. Choose chords that are unbound by default and
   propose them to the owner.
2. **The hover's home.** The hover can come from the client, as a decoration's `hoverMessage`, or
   from the server's existing instruction hover. A decoration hover appears alongside the server's.
   Try the client hover first, and move it into `Hovers` if the two stacked hovers look cluttered.
3. **Pass 2 inference cost.** Inferring memory inputs program-wide, as `RegisterKeeps.Compose`
   does for registers, may cost too much to do on every keystroke. Doing it on demand for the
   callee at the caret, transitively, may be enough. Measure before choosing.

## Conventions

The repository's `CLAUDE.md` holds the C# style, member ordering, one type per file and the comment
voice; follow it. Files use LF line endings, and a CRLF `.cs` file fails the build. The test gate
should stay fast; add the new tests to the fast suite and keep each one small.
