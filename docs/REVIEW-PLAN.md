# nt65 — Plan from the review of 2026-09-21

## Where this comes from

Seven reviews of the tree as it stood on 2026-09-21: the language design, the syntax layer, the
semantic layer, flow and layout, the emitter and the command line, the language server and the
VS Code client, and the architecture and tests across them. Every finding marked **confirmed**
below was reproduced against the built command and the pinned ca65; the rest are code traces
with a file and a line.

What the review found is not a design that needs rethinking. The line-at-a-time parser, the
interface-based incremental boundary, the fixture runner's determinism checks, hover, the
messages and `remap-dbg` all came back as things to leave alone. What it found is five kinds of
gap:

1. input that kills the process, and nothing that contains it;
2. three places the 65816 checker accepts a wrong program;
3. three places the output is not the ca65 the design promises, and an oracle that would not
   notice;
4. infrastructure the next features stand on and do not have: an identity for a diagnostic, a
   tree that can be rewritten, a semantic API a consumer can use, a server that negotiates;
5. features a user looks for and does not find, in the editor and in the language.

This plan orders them, says what each fix is, and says which can run beside which. It does not
replace `PLAN.md`: Stage 33 (release) is still the last thing, and Phase 0 here does part of
it early because the rest leans on it.

**How to read a finding.** *What* is what is wrong, *Fix* is what to write, *Check* is the test
that would have caught it. Small mechanical fixes are listed once, at the end, under the
workstream whose files they touch, and are done with that workstream rather than on their own.

## Status

Every workstream below is built and on `main` as of 2026-09-21. The text that follows is the
plan as it was written, kept as the record of why; this section says what became of it.

| | what landed | done differently, and why |
|---|---|---|
| 0 | the `gate` workflow; the licence on the package | — |
| A | depth guard, no value from a refused literal, the repetition bound, handlers of last resort (exit 70), the generated-program test; R4 | `'é'` is not refused by the lexer, which has no charmap context: a constant `K = 'é'` is reported at evaluation instead (D). The repetition bound lives in `Repetitions`, where the memory went |
| B | the three holes, each with its fixture; R6 for `StateAnalysis` (`StateChecks`); R2 (`InstructionFacts`) | at a label entered from outside, a part the routine's signature leaves `*` stays unchanged rather than unknown, as D and B already did; taken literally the plan's sentence made every `*` routine unwritable. The analysis stack there was a fourth hole, closed after: it is the one entering the routine leaves, since a declaration cannot say what is pushed (§7.3, §16) |
| C | the arithmetic in §9; no mnemonic reserved; `: abs` on imports; the hostile-options matrix; corpus builds in the gate; R1, R3 | a declared value *and every step of its expression* must fit ca65's 32 bits, since ca65 works the text out again. ca65 cannot be silent at `-W2` (it warns about its own symbols), so the matrix ignores that one message. `-mm far` is refused, as §13 says, rather than byte-identical |
| D | 357 names (`docs/DIAGNOSTICS.md`), severities in `nt65.json`, `nt65 explain`, `[name]` in the terminal, `id` in `--json`, `code` in the editor; fixtures match on name and line; `AnalysisLevel` on | — |
| E | own framing in front of StreamJsonRpc (`-32700`, null `params`, the lifecycle codes), `exit` and the parent watch, typed client capabilities, cancellation everywhere, the publishing rules, client-spelled URIs, `SyntaxTree.WithChanges` | — |
| F | `LookupNames`, `LookupSymbols`, `GetSymbolInfo`, `ReferencesTo`; the server's three copies gone; no read path writes; incremental matches clean for program-wide diagnostics; the semantic half of `ANALYSIS-API.md`, held by a reflection test | — |
| G | `Update`, `With…`, `SyntaxFactory`, `SyntaxRewriter`, `ReplaceNode` and its siblings, `NormalizeWhitespace` | a rewrite that reaches a line goes through one text change, because a green line holds tokens and not the statement they parse to |
| H | hover ordered; five kinds of hint; the output view and `nt65 build --stdout`; macro expansion and *Inline macro call*; snippets, nearness, resolve; files that move; selection ranges, token range and delta; the client items; R5 | an implied constant is written ` = $0300`, nt65's spelling, with the decimal in the tooltip. A routine's hover leads with cost and preserves, since its signature is already the declaring line. The rename filter is every file, since which extensions a program includes is not known at `initialize` |
| I | typed imports and `nt65 import-inc`; `6502x`; unused routines; `.sqrt .muldiv .sin .cos`; `.mincycles .maxcycles`; the document repairs | counted enums deferred (see *Decided*). The interop corpus keeps its checked imports, which ld65 verifies |
| J | `nt65 lsp`; portable scripts and a Linux leg; the version from the tag and a release workflow; coverage printed in CI | the VS Code extension keeps its bundled server |

**Left open**, none of it blocking:

- A routine that leaves by `jmp other::label` is not counted as leaving in `RegisterKeeps.Ends`:
  the target is a label rather than a routine, so nothing there ends the path, and the jumping
  routine's own `keeps` is credited with everything the code after the jump does.
- `Norristown.Project` stands outside the namespace order rather than in it: the syntax layer
  reads `CpuNames` out of it and it reads `SegmentTable` and `StateValue` back out of the
  semantic layer, so the two point at each other. `NamespaceOrderTests` says so where it holds
  the other six.
- The VS Code client's views (`views.js`) are checked by `node --check` and by the server tests
  on what they are sent, and have not been driven in a running editor by a test.

## Order, and what runs beside what

The workstreams are cut by which files they own, so that two running at once do not meet in a
file. Where two must touch one file, the plan says which goes first.

```
Phase 0   CI and licence                                  (an hour; everything after runs in it)

Phase 1   A  Containment      ∥  B  Flow soundness  ∥  C  The output contract
          Syntax, Literals,      Flow/*                   Emit/*, Operators, Evaluator
          Cli/Program, Server                             arithmetic, tests/Oracle

Phase 2   D  Diagnostic catalogue        (alone, short: it touches every reporting site)
          then
          E  Server foundation  ∥  F  Semantic API  ∥  G  Tree rewriting
             LanguageServer/*      Semantics/*          SyntaxGenerator, Syntax/*

Phase 3   H  Editor features    ∥  I  Language additions  ∥  J  Packaging and reach
             (needs E and F)       (needs D)                 (needs nothing)

Any time  R  Refactors, each done by the workstream that next opens the file
```

Why this order:

- **Phase 1 first** because every item in it is a program nt65 accepts and should not, or a
  crash. Nothing else in the plan is worth shipping on top of a checker with holes in it.
  A, B and C share no file but `Emitter.cs`, and there the one shared method
  (`Emitter.Pads`) is C's to fix.
- **D alone** because a catalogue changes every `new Diagnostic(` and every `Report`/`Warn` in
  Binder, Evaluator, Flow and Layout. Run beside anything else in Core it is a merge conflict
  per file. It is mechanical and short; do it in one sweep between phases, when no other
  workstream is mid-change.
- **E, F and G together** because they own three different projects. H waits for E (it needs
  client capabilities and cancellation) and for F (completion and rename move onto the API F
  adds, rather than growing a third copy of name resolution).
- **I after D** because every language addition reports new diagnostics, and they should be
  born with an identity rather than retrofitted.

**The test loop.** The fast suite is 1,279 tests in about 2.6 s, and this plan adds to it. The
budget is that it stays under 4 s. What keeps it there: the hostile-options matrix (C) is
oracle work and runs in the gate only, its option sets in parallel; the generated-program fuzz
(A) runs a fixed seed and a fixed count in the fast suite and a long run nowhere by default;
the one test that spawns the real server (E) is a single process for the whole lifecycle. At
the end of each phase, look at the longest class again. The one lever already known is that
`FixtureRunner` compiles each fixture four times (as written, again, reversed, shuffled); if
the suite passes 4 s, the reversed and shuffled runs move behind a flag the gate sets.

---

## Phase 0 — CI and a licence

**What.** There is no CI of any kind and no licence file. The gate is run by hand.

**Fix.** One workflow that runs `scripts/gate.ps1` on Windows with `.cache/cc65` cached on the
contents of `scripts/cc65.commit`. A `LICENSE` file; the CLI is `PackAsTool`, and a package
wants one. Linux comes in J, when the scripts are portable: the C# already is.

---

## Phase 1

### A. Containment

**What (confirmed).** Short, well-formed or half-typed programs kill the process, in the
command and in the server alike:

| input | result |
|---|---|
| `K = '\xZZ'` | `FormatException`, `Literals.cs:59`. The lexer reports the escape; evaluation runs on the token anyway |
| 600 nested `(`, or 800 unclosed | stack overflow in `Parser.ParseBinary`; about 14 frames a parenthesis and no depth count |
| `(0-$7fffffffffffffff-1) / (0-1)` | `OverflowException`, `Operators.cs:37` (and `:38` for `.mod`) |
| `.repeat $7fffffff, i { 0 }` in data | out of memory in `Evaluator.Turns` |

Neither `Cli/Program.cs` nor `Server.RunAsync` has a handler of last resort, and
`BrokenSourceTests` cannot reach any of these: it truncates sources that exist, and every one
of these is a program nobody wrote yet.

**Fix.**

- A depth count on `Parser`, raised in `ParseBinary`, `ParseParenthesized` and
  `ParseBracedValue`. Past 100 it reports that the expression nests too deeply and returns an
  `ErrorExpressionSyntax` over the rest of the line. `SyntaxNode.DescendantNodes` and its two
  siblings become iterative.
- **A token with a lexical diagnostic does not evaluate.** This is the general fix behind the
  `\x` crash: an expression that holds such a token has no value, the way an undeclared name
  has none. `Literals.Text` checks its two digits as well.
- `Operators.Binary` answers no value for `long.MinValue / -1` and `% -1`. What the arithmetic
  *is* belongs to C.
- A cap on the turns of a repetition in a data body, the same 65,536 that bounds macro
  expansion, reported as that one is.
- A handler of last resort in each `Program.cs`: the command prints `nt65: internal error` with
  the exception and the file being compiled and exits 70; the server logs, tells the client
  with `window/showMessage`, and exits non-zero so the client's restart runs. A stack overflow
  cannot be caught, which is why the guards above are not optional.

**Check.** A generated-program test beside the truncation one: a fixed seed builds programs
from a small grammar of the constructs above (deep nesting, extreme numbers, long repetitions,
bad escapes, self-reference) and asserts only that analysis and emission return.

### B. Flow soundness

**What.** Three programs the checker accepts and should not.

1. **(confirmed) A jump into a label that declares part of its state.** `.state a16` at
   `owner::into` names A only; the index width there stays whatever the fall-through path
   left. `jmp owner::into` from an `i8` routine builds clean, and lands on an `ldx #$1234`
   assembled three bytes wide. The caller's side checks "the parts it gives", as the design
   says; the callee's side never forgets the parts it does not.
2. **(confirmed) `rts` or `rtl` under `.next routine`.** `StateAnalysis.Through` pulls the
   return address and returns before `Transferred` runs, so the routine named is never checked
   against. The same mismatch written `jmp target` is four errors.
3. **`keeps a` across a save whose width is unknown at both ends.** `SavedStack` matches a
   pull to a push by the lattice value, and unknown equals unknown. A callee that widens A
   between them unbalances the stack, and `keeps a` is certified.

**Fix.**

1. At a block whose label is declared and can be entered from outside the routine (exported,
   or named by a path from another routine), meet the state that reaches it with the
   declaration: a part the `.state` does not give becomes unknown. The message for what then
   fails downstream should name the label and say which part to declare.
2. In the return case, run the `.next` targets through `CheckTailCall`, as `Transferred` does
   for `jmp`.
3. An unknown width never matches in `SavedStack.Pulled`.

**Check.** Three fixtures, each the probe with its expected diagnostic:
`unchecked-errors/partial-state`, `unchecked-errors/next-routine`, `registers/unknown-width`.

The instruction tables were assembled and compared byte for byte against ca65 for all five
CPUs and came back clean. Leave them.

### C. The output contract

**What.** "If ca65 reports an error or a warning on nt65 output, that is an nt65 bug" fails
three ways, and the oracle cannot see any of them.

1. **(confirmed) ca65's alias mnemonics are nt65 names.** `.proc swa` on the 65816 writes
   `swa:`, which ca65 reads as an instruction. The set is `tad tas tda tsa swa cpa dea ina`
   there and `dea ina` on the CMOS parts. §4 says they are reserved nowhere; under the
   `.setcpu` nt65 writes, they are.
2. **(confirmed) Numbers.** nt65 computes in 64 bits and ca65 in 32. `BIG = $7fffffffffffffff`
   builds and ca65 overflows; and an expression nt65 declines to evaluate (`1 << 70`) is
   written out as text for ca65 to compute, where it passes nt65's range check as one value
   and assembles as another.
3. **`.import` leaves out `: abs`**, so ld65 warns on every absolute import under `-mm far`.
4. **The oracle** runs ca65 with `-g -l` and nothing else, compares byte *counts* per line
   almost everywhere, has `-W2` off, and caches a success without keying on the ca65 binary.
   The corpus Makefiles, which are the only end-to-end proof of `--depfile`, the C header and
   `remap-dbg` together, are in no gate.
5. **(confirmed) A struct that contains itself**, with an instance, overflows the stack in
   `Emitter.Pads`. The analysis reports the cycle; emission walks it regardless.

**Fix.**

- **Decide the arithmetic, in §9, first.** Recommended: values are 64-bit signed while nt65
  computes, `/` truncates toward zero, `.mod` takes the dividend's sign, `>>` is arithmetic, a
  shift count outside 0–63 is an error, overflow of `+ - * <<` is an error, and **a value that
  reaches the output must fit ca65's 32 bits**, checked where it is declared. Then the
  invariant: *an expression built only from constants is never written as text.* Either nt65
  has its value or nt65 has reported why not.
- **Do the evaluator refactor here** (R1 below). `Configuration.cs:252-445` is a second
  evaluator, with the built-in switch copied whole. Two evaluators would each need the new
  arithmetic; one does not.
- **No mnemonic is reserved; the emitter keeps ca65 from reading a name as an instruction**
  (decided, see *Decided* at the end). The grammar never needed the reservation: a line that
  starts with a mnemonic is an instruction unless its second token is `:` or `=`, which is
  what the parser already does — `lda = 5`, `rts:` and `jmp rts` all parse today, and the only
  thing that refuses them is a check in the binder. That check goes. What replaces it on the
  way out: a top-level name that is not exported, and that ca65 would read as an instruction
  under the `.setcpu` nt65 wrote, is written with its module in front (`main__swa`), exactly
  as an exported name already is. Scoped names (`go__rts`) and exports already hold a `__` and
  cannot collide, so nothing else changes and no existing fixture's output moves. The set of
  words comes from ca65's own instruction tables in the pinned source, read by a test (below),
  so the list nt65 holds cannot drift from the one ca65 has — which is how the aliases were
  missed. The one case with no spelling to fall back on is `as "lda"` on an export: ca65 cannot
  define that symbol under any name, so it is an error where the `as` is written, checked
  against every ca65 CPU so that it does not depend on the program's. The warning that
  replaces the error for the reader's sake is D's (`mnemonic-name`); C lands a phase before D,
  so C turns today's error into that warning in the same commit — the cross-CPU warning that
  exists, widened to every CPU and given the new sentence — and D gives it its name.
- `Implicit()` comes off the two `.import` sites; exports keep it.
- Emission skips a symbol the analysis has already called cyclic. (`Compiler.cs:53` writes
  every file of a wrong program on purpose; that stays, but not for what cannot be laid out.)

**Check.**

- **The hostile-options matrix**: each corpus program assembled and linked under a set of
  option sets (`-t c64`, `-t none`, `-mm far`, `-mm near`, `-U`, `--smart`, `-D` of a declared
  name, a spread of `--feature`s), asserting the linked binary is byte-identical to the
  baseline and ca65's stderr is empty at `-W2`. Oracle trait, gate only, option sets in
  parallel. The review ran this by hand on `snes-hello` and it held; the point is that it keeps
  holding.
- Two tests with no assembler run. One reads the instruction tables out of the pinned
  `instr.c` and asserts that the words the emitter prefixes for each CPU are exactly ca65's
  table for the `.setcpu` string nt65 writes for it; it fails when `scripts/cc65.commit` moves
  to a ca65 that has a new one. The other asserts, over every fixture and corpus output, that
  no symbol is defined bare under a name in that table.
- A fixture, through the oracle, on each CPU: a proc, a constant, a data declaration and a
  label named for a mnemonic of the program's CPU, for an alias (`swa`, `ina`) and for another
  CPU's mnemonic, used from code and from another module; and `as "lda"` refused.
- The ca65 binary's hash in the oracle cache key; a `-Fixture` filter that matches nothing
  fails instead of passing.
- A gate step that runs the corpus builds against the pinned tools.

---

## Phase 2

### D. A catalogue of diagnostics

**What.** A diagnostic is a span, a severity and a sentence. There is no identity, so: no
warning can be switched off or made an error, by anyone, anywhere; the editor cannot group or
filter; `--json` cannot be allow-listed in CI; and the tests hold the prose, so rewording a
message is a test change. Three reviewers reached this independently, and the analyzer
framework cannot start without it.

**Fix.**

- **Identity is a name, not a number**: `unused-symbol`, `width-unknown`, `branch-out-of-reach`.
  A user meets this in the Problems panel, in a project file and in CI output, and
  `"unused-symbol": "off"` can be read by someone who has never seen the catalogue, where
  `NT0203` cannot. (`NT1001` stays what it is: a C# build diagnostic from the generator, which
  is a different audience.) Names are kebab-case, say what is wrong rather than which pass
  found it, and are stable once released — they join what §17 promises.
- One file in Core holds the catalogue: name, default severity, the message format, and one
  sentence of explanation that is not the message. `Diagnostic` gains `Id`. Reporting sites
  name a descriptor and pass arguments; they stop building sentences.
- **Severity in the project file**: `"diagnostics": { "unused-symbol": "off" }`, with `off`,
  `warning` and `error`; an error cannot be turned down. In a configuration as well as at the
  top, so a release build can be strict. The schema gets the names, and the existing schema
  test holds them to the catalogue.
- The name goes to LSP `code`, to `--json` as `"id"`, and in the terminal after the message in
  brackets, where compilers put it: `warning: `levels` is never used [unused-symbol]`.
- `nt65 explain <name>` prints the explanation. It is also what the editor's code link can
  open, once there is somewhere to link to; until then, no `codeDescription`.
- Fixtures match on `id` plus the span, and keep the message as a second, separate
  expectation, so that rewording touches one line.

- **`mnemonic-name`, the first warning born in the catalogue.** It is what is left of the
  reservation C removes, and it is a matter of reading, not of meaning: the program is right
  and so is the output.

  ```
  src/main.nt65:5:1: warning: `lda` is an instruction on the 6502; as a name it is legal and easy to misread [mnemonic-name]
  ```

  - It is **the same in every project**: a name that is an instruction on *any* CPU nt65 knows
    warns, whatever the program is built for. The message names the program's own CPU when the
    word is an instruction there and the first CPU that has it otherwise. It replaces both
    today's error and today's cross-CPU warning; the sentence about a shared module not
    building goes, because under the new rule it builds.
  - Once, on the declaration, never on a use: labels, constants, procs, data, macros, macro
    parameters and members of an anonymous enum — what the error covers today.
  - Quiet where the spelling at the use cannot be taken for an instruction, which is where
    nt65 is already quiet: a member of a named struct, union or enum (`Op::lda`) and an
    `@local` label (`@rts`). Both build today without a diagnostic, so nothing is loosened.
  - In the editor: a fix, *Rename `lda`…*, through the rename handshake the refactorings use,
    so the caret lands on the name and no name is guessed; hover adds "written `main__lda` in
    the output" on a name the emitter prefixes, and only there. Not marked unnecessary:
    nothing is unused.
  - `nt65 explain mnemonic-name` says what the line cannot: that a `:` or `=` after the first
    word is what makes it a declaration, that ca65 cannot define a bare symbol spelled like an
    instruction so nt65 writes a top-level one with its module in front, and the project-file
    line for a team that wants it an error.
  - On by default. It is a style warning and the likeliest in the catalogue to be switched
    off, but it is an error today, a port is where such names come from, and a default can be
    loosened after release and not tightened.

**Not in D:** suppression *in the source*. It is a language change, and the owner has left it
out for now (see *Decided*).

### E. Server foundation

Everything here is protocol, and none of it is visible when it works. It goes before the
features because three of them need it.

**What (all probed over stdio).**

- A request with `"params": null` ends the process: `RunAsync` catches connection loss and
  nothing else (`Server.cs:41`).
- After `shutdown` and `exit` the process stays until another byte arrives on stdin.
- `$/cancelRequest` is ignored; no handler takes a token. Nothing is debounced: every
  `didChange` re-analyses the program under the workspace lock and then asks the client to
  refresh semantic tokens and lenses for every visible document — two requests a keystroke, on
  top of the one the client makes anyway.
- Client capabilities are read for `refreshSupport` and nothing else. So there are no
  snippets, no `documentChanges` (hence no versioned edits and no file operations), and a
  folder added to the workspace is not seen.
- Only published diagnostics use the client's spelling of a URI; definition, references,
  rename, code actions, links, call hierarchy and workspace symbols build their own.
- `SyntaxTree.WithChange` takes one change, so a `didChange` of N changes is N passes over the
  file.

**Fix.**

- The handler of last resort from A; a malformed frame is answered `-32700` and the loop
  survives. `exit` ends the process itself, 0 after `shutdown` and 1 without; the server
  watches the parent process id it was given and leaves when the parent does.
- Requests before `initialize` get `-32002`; after `shutdown`, `-32600`.
- Keep the client's capabilities, and gate on them: snippets, `documentChanges`, hierarchical
  symbols, workspace folders. Declare `positionEncoding: utf-16`, which is already true.
- A `CancellationToken` through every request handler, checked between files of a program.
- **What a keystroke publishes, and when.** The rule the user should be able to rely on is
  that *a squiggle never flickers and is never about text that is gone*:
  - the edited file's own diagnostics are published at once, from the analysis of that file;
  - the rest of the program's are published after 200 ms without an edit;
  - a file's previous diagnostics stand until its new ones replace them — never cleared and
    then refilled;
  - nothing is published for a version older than the newest one received;
  - the edited document gets no refresh request (the client re-asks for it on its own), and
    the others get one only when the file's interface changed, which is exactly when their
    tokens or lenses can have.
- Every outgoing URI goes through `Workspace.UriOf`.
- `SyntaxTree.WithChanges(IReadOnlyList<TextChange>)`, splitting and rebuilding once.

**Check.** One test that spawns the real executable and drives a whole life: requests before
`initialize`, null params, a cancel, `shutdown`, `exit`, the exit code, the process gone. The
keystroke benchmark before and after the debounce. A test opened with `file:///c%3A/…` that
asserts every response spells it the same way.

### F. The semantic API

**What.** The API is half the reason the language exists, and its semantic half is private in
practice. `ANALYSIS-API.md` documents the syntax tree only; the `SemanticModel` members a
consumer would call have no test that names them. Three operations are missing, and the server
re-implements each:

| missing | the server's copy | what the copy gets wrong |
|---|---|---|
| names in scope at a position | `Completion.AddInScope` | — |
| what a path means at a position | `Completion.Walk` | tries candidates in a different order from `Binder.Resolve`, so completion and binding can disagree |
| references across the program | `Lsp.Everywhere` | `Edits.Rename` without it renames one file, silently |

And two things that are wrong rather than missing:

- **A read path writes.** `SemanticModel` is documented as safe to ask from any thread;
  `RoomFor` → `ElementWidth` → `LayOut` writes a struct's member sizes and offsets on first
  ask. `Symbol.Calls` and `Symbol.Uses` are public mutable lists.
- **Incremental analysis can disagree with a clean build.** A program-wide table diagnostic (an
  export-name collision) lands on the file that sorts later, which need not be the file that
  was edited; that file is not re-analysed, keeps its old model, and keeps unused-symbol
  warnings a clean build would have suppressed. The replay test cannot see it because its edit
  script never makes a collision.

**Fix.**

- `SemanticModel.LookupSymbols(position, name?)`, `SemanticModel.GetSymbolInfo(path)` answering
  what the binder would, `ProgramAnalysis.ReferencesTo(symbol)`. Completion, rename and
  find-references move onto them and the copies go.
- Struct and union layout is computed for every declared type during `EvaluateSymbols`, so no
  question asked later can trigger it. `Calls` and `Uses` become read-only views.
- The files named by table diagnostics that appeared or disappeared join `affected`; a file
  whose program-wide diagnostics changed gets a new model.
- A semantic page in `ANALYSIS-API.md`, and an `AnalysisApiTests` sibling that calls each
  public member by name.
- §14 says only the edited proc's flow analysis reruns; it is the whole file. Amend the
  sentence — at 45–60 ms a keystroke on a real program, the implementation is not what is
  wrong.

**Check.** The replay script gains an edit that makes, and one that removes, a cross-file
export collision. A test that asks a fresh model everything from several threads at once.

### G. A tree that can be rewritten

**What.** `SYNTAX-API.md` already lists this as the next work: no `With…` or `Update`, no
`SyntaxFactory`, no `SyntaxRewriter`, no annotations. An analyzer can be written today; a fix
cannot. Every fix and refactoring in the server is a text edit because of it.

**Fix.** As that document describes, from the rows the generator already has: `Update` and one
`With<Slot>` per slot, `SyntaxFactory` over the typed green constructors, `SyntaxRewriter` on
`Update`, `ReplaceNode` on the rewriter. Annotations when a refactoring first needs one, not
before. Harden the generator while it is open: a duplicate `Name` and a `Base` that names
itself both get past `NT1001` today, the second as a hang.

Nothing in H depends on G. It is here because it runs beside E and F for free, and because the
analyzer framework needs it and D, and then nothing else.

---

## Phase 3

### H. Editor features

#### What goes on the screen, and what does not

§14 already takes a position: what the analysis knows about a line is on hover, "so nothing
stands in the lines as they are written". That is right, and the features below are held to it.
The server computes far more than anyone wants to look at. The rules:

1. **Each surface answers one question.** A squiggle: *what is wrong?* A lens: *what should I
   know about this routine before I call it?* Hover: *what is this thing I am pointing at?* A
   hint: *what is true here that the line does not say and that I would read the line wrong
   without?* A side view: *what did this turn into?* A fact that answers none of these has no
   place, however cheaply it was computed.
2. **Nothing appears twice.** If the line says it, no hint says it. If a lens says it, hover
   does not lead with it.
3. **A hint marks a change, not a state.** The width of A is interesting on the line where it
   becomes 16, and noise on the forty lines after.
4. **The default is quiet.** A file opened for the first time should look like the file. What
   is on by default is what is rare and surprising; what is frequent is opt-in, and opting in
   is one command, not a settings hunt.
5. **Words before numbers, the answer before the working.** `long branch`, not `+3`. A value
   first; how it was reached, under it or in a tooltip.
6. **Everything shown can be switched off on its own**, by a setting named for what it shows.

**A hover audit comes with this work.** Hover was the best-reviewed thing in the server, and it
is also where everything computed has gone so far: declaration, value, size, address size,
segment, cost, preserves, cycles and why, flags, 65816 state, register contents, the stack,
the comment. Before adding to it, order it: the declaration line and its comment first, then
the one or two facts that kind of symbol is asked about most (a constant: its value; a routine:
what it wants and what it keeps; an instruction: its cycles and the state reaching it), a rule,
and the rest beneath. Nothing is removed; the first screenful becomes the answer.

#### H1. Inlay hints

The review called this the largest gap, and it is — and it is also the feature most able to
turn a clean listing into a dashboard. So it is five kinds of hint, each with a reason to
exist, not one per thing the layout knows.

| hint | looks like | shown | default |
|---|---|---|---|
| **A state change the line does not spell** | `rep #$30` ` a16 i16` · `jsr widen` ` → a16` · `plp` ` a? i?` | only on a line after which a width, the emulation flag, D or B differs from before it, and only the parts that changed. Not on `.ensure` or `.state`, which say it themselves | on, 65816 only |
| **A long branch** | `beq far_away` ` long` | where layout wrote the five-byte form. Tooltip: what it became and what it costs | on |
| **An implied value** | `pulse2` ` = 1` · `y: .word` ` +2` · `SIZE = W * H` ` = 768` | enum members with no value written; struct and union members' offsets; a constant whose right side is not a literal | on |
| **A parameter name** | `fill!(` `count:` `16, ` `with:` `0)` | positional macro and `.func` arguments. Not when the argument is a name that matches the parameter, not for one-parameter calls, not when named arguments are written | on |
| **Cycles** | `lda (ptr),y` ` 5–6` | every instruction, end of line; a label line carries its block's total | **off**; `nt65: Toggle Cycle Counts` and a status-bar item switch it for the session |

What is deliberately *not* a hint:

- **The addressing mode chosen** (`z:`/`a:`/`f:`). It is on nearly every line, the declaration
  decides it, and the programmer who cares is one hover away. A hint on every operand is the
  dashboard.
- **Bytes per line.** Same frequency, less use. Hover has it; the output view (H2) shows it in
  context.
- **Register contents.** Hover has them, as a block, where a block can be read.
- **Addresses.** nt65 never knows one — the linker does — and should not pretend. If this is
  wanted it comes from reading `ld65`'s debug file after a build, which is a different feature
  with a different freshness story, and is not planned here.

Mechanics: at most one hint at the end of a line, so where two would land (a state change on a
long branch) the state change wins and the other moves to its tooltip; twelve characters at
most; every hint has a tooltip that says in a sentence what it means and, where there is one,
which declaration decided it. Hints are computed for the range the client asks for, not the
file. Settings: `nt65.inlayHints.stateChanges`, `.longBranches`, `.impliedValues`,
`.parameterNames`, `.cycles`.

**Check.** A server test per kind, including each *absence* above (no hint on `.ensure`, none
for a matching argument name, none on a line whose state did not change). A snapshot of
`examples/snes-hello/src/main.nt65` with default settings, read by a person once: if it looks
busy, the defaults are wrong.

#### H2. What this became: the output beside the source

The transpiler's whole promise is that the output is readable ca65, and today seeing it means
building and opening `build/`.

- `nt65: Show Output Beside` opens a read-only document of the module's `.s`, as the program
  stands in the editor now, unsaved edits included. It follows edits on the debounce from E.
- **The caret is the link.** Moving in the source highlights the lines it became, and scrolls
  them into view; moving in the output highlights the source line. Both come from the line map
  the build already writes. No hover, no lens, no command per line — one view, always in step.
- It opens scrolled to the caret's lines, past the header; the twenty lines of `.feature` are
  there for ca65, not for the reader.
- A file with errors shows the output as far as it can be written, which is what `build`
  writes, with a first line saying that it is incomplete and why.
- The server side is one custom request, `nt65/output`, answering the text and the line
  pairs, so another editor's client can do the same with what it has. The command line gets
  the same thing as `nt65 build --stdout <file>`.

#### H3. Macro expansion, in nt65

A macro call is the one line whose meaning is somewhere else. The expansion shown is **nt65,
not ca65**: the body with the arguments in place, as the programmer would have written it by
hand.

- **On hover over a call**: the signature and doc comment as now, then one line — `expands to
  14 lines · 31 bytes · 58–64 cycles` — then the first eight lines of the expansion, and a
  link, *Show expansion*, when there are more. The summary line is the thing most often
  wanted; the listing is there for the short macros where it fits.
- ***Show expansion*** opens the same read-only side view as H2, holding the expansion. Calls
  inside it are left as calls, each with its own link: **one level at a time**, because a
  fully flattened expansion of nested macros is unreadable and nobody wrote it. *Expand all*
  is there for the person who wants it.
- A refactoring, *Inline macro call*, replaces the call with its expansion. It falls out of
  the same code, and is how one stops using a macro.

#### H4. Completion that writes structure

Today there are no snippets at all: the capability is never read, and `CompletionItem` has no
`insertTextFormat`.

- **Snippets for what opens a block, and only that**: `.proc`, `.macro`, `.func`, `.struct`,
  `.union`, `.enum`, `.scope`, `.segment … { }`, `.if`/`.else`, `.repeat`, `.each`,
  `.multiproc`. The name is the first stop, the body the last; on the 65816 `.proc` stops on a
  signature that starts as the project's most common one. A client without snippet support
  gets the plain word, as now.
- **No snippets for instructions.** `lda ${1:operand}` fights the typing of someone who knows
  what they are writing, which in assembly is everyone.
- **Order by nearness**, with `sortText`: labels of this routine, then the file's names, then
  what `.use` brought in, then other modules' exports; mnemonics alphabetical. The thing meant
  is nearly always the nearest.
- **No commit characters.** A completion that accepts itself on `,` or a space, in a language
  where both follow a name on most lines, is wrong more often than it is right.
- `completionItem/resolve` for documentation, so a long list does not carry every doc comment.

#### H5. Files that move

A module's name is written in its `.module` line and its output is named after that, wherever
the source is. So renaming a source file changes less than the review assumed, and the design
should say so rather than invent work:

- `workspace/willRenameFiles` answers edits to `nt65.json` where `files` names the file
  literally (a glob that still matches needs nothing, and one that stops matching is reported,
  not rewritten), and to `.incbin` paths in a file that moved to another folder.
- Renaming a **module** stays a rename of the name in `.module`, which already rewrites every
  `.use`. It does not rename the file: the two are independent by design.
- `WorkspaceEdit.documentChanges`, with versions, where the client has it, so an edit worked
  out against a buffer that has since changed is refused rather than applied.

#### H6. Smaller, and in this order

- `textDocument/selectionRange`, from the tree: operand, instruction, block, routine.
- Semantic tokens `range` and `delta`, which matter on long files.
- Diagnostic `code` from D, as soon as D is in.
- `nt65: Restart Server`.
- Activation on `onLanguage:nt65`, so a lone file gets a server; the file watcher narrowed
  from `**/*` to `**/*.nt65`, `**/nt65.json` and the `.incbin` files the program names.
- `nt65.json` validated as the JSON-with-comments nt65 actually reads.

**Not planned:** a debugger. Driving Mesen or VICE over DAP is a project the size of this
plan, and nothing here closes a door on it: `remap-dbg` already gives an emulator source lines.

### I. Language additions

Each is additive under §17, each changes `DESIGN.md` in the same commit with its reasoning in
§16, and each is born with a catalogue name from D. In the order a port would want them:

1. **Typed imports, and a converter for `.inc` files.** Interop flows one way: `--c-header`
   sends types out, and an `.import` is opaque — no type, no `.sizeof`, no members. The interop
   corpus writes every hardware constant twice. `.import name: .type T` and
   `.import name: .byte[n]`, declared and trusted exactly as a `proc(...)` import's signature
   already is; and `nt65 import-inc <file>`, run once by a person, which writes an nt65 module
   of constants from a ca65 include. nt65 still reads no ca65 at build time.
2. **Undocumented 6502 opcodes.** A sixth CPU, `6502x`, mapping to ca65's `6502X`. `lax` on a
   plain `6502` says that it is an undocumented instruction and which CPU has it, instead of
   a parse error.
3. **Unused routines.** An unexported `.proc` nothing calls, jumps to or names gets the
   warning §14 implies and the implementation does not give. It is what someone finishing a
   port most wants to see.
4. **Compile-time tables.** `.sin` and `.cos` over a turn scaled to an integer, `.sqrt`, and a
   rounding `.muldiv`, all integer in and integer out, so that a sine table is an `.each` and
   not a script in another language. If this is refused instead, §15 should say so and the
   guide should show the `.incbin` answer.
5. **`.cyclesof(from, to)`**, a constant, for a span with no call and no loop in it — an error
   otherwise, saying which it found. §16 refuses cycle built-ins because a sum is not a bound
   across a loop or a call; §7.6 already computes the exact cases, and those are the ones a
   raster routine needs an `.assert` on.
6. **A counted enum** — `.enum Bank[32]`, members spelled by the language — so that a family
   over thirty-two banks is not thirty-two names typed by hand. The least certain item here:
   worth a design conversation before code.

**The mnemonic rule in the document**, in the commit that makes C's change, not later: §4's
reserved-words paragraph loses the mnemonics and the long branches and keeps the registers and
the directives, and says instead that a line starting with a mnemonic is an instruction unless
`:` or `=` follows, that a name may be one, and that `mnemonic-name` warns. §16's "mnemonics
are reserved by the program's CPU" is replaced by the decision and its reasoning: the grammar
never needed it, the backend's limit is the emitter's to meet, and one rule that never moves —
across CPUs, new CPUs and a new ca65 — is worth more than an error for a name that is only hard
to read. §13 gains the prefix rule beside the other name spellings. §17's "a new CPU" bullet
gets shorter: a new CPU reserves nothing anywhere and widens a warning; and the paragraph after
it, that a CPU's instruction set is part of the language version because a new instruction
takes a name, no longer holds for mnemonics and is cut back to state items and contextual
words.

**Document repairs**, with whichever of these is first: §10's closing paragraph still says a
repetition body holds no `.proc` or `.export`, which families require; §3.1's "which
declarations exist is fixed by the build configuration" needs the enum-member clause; §3.2's
"or a warning" is true at ca65's default level and should say so. And `GUIDE.md`, when its
rewrite comes, needs a section the review found no trace of: what a 65816 port has to change
first (a signature on every routine, second entry points as adjacent procs joined by `.next`,
ROM addresses as extern procs).

### J. Packaging and reach

- **`nt65 lsp`** starts the server over stdio from the same dotnet tool as the command. Today
  the server ships only inside the `.vsix`, so nobody outside VS Code can have it. A paragraph
  each for Neovim, Helix and Zed in the README; it is plain LSP and costs nothing more.
- **Portable scripts.** The C# and the tests already branch on the OS and normalise paths; what
  is Windows-only is five `.ps1` files and two Makefiles that name `.exe`. PowerShell 7 runs on
  Linux, so this is mostly removing assumptions, and then CI (Phase 0) gains a Linux leg.
- **The version** from the tag in CI, rather than written in `Directory.Build.props`.
- A coverage run in CI, printed and not gated: the six largest types are reached only through
  `Compiler.Compile`, and nobody knows which of their branches no test enters.

---

## R. Refactors

None of these is a project of its own. Each is done by the workstream that next opens the
file, before that workstream's own change, so that the change lands in the better shape.

| | what | why now, and with what |
|---|---|---|
| R1 | **One evaluator.** `Configuration` drives `Evaluator` with a resolver that knows only defines; its copy of `Evaluate`/`Unary`/`Binary`/`Call` goes | **C**, before the arithmetic is decided, so it is decided once. The only refactor worth doing for its own sake: it is drift waiting to happen |
| R2 | **Instruction facts in one place.** What a mnemonic is — call, return, push, pull, store, which registers it writes — is re-derived from lowercase strings in eight places across Flow and Layout. One record per mnemonic beside the mode table, the helpers projecting from it | after **B**; before I.2, which adds a CPU's worth of mnemonics |
| R3 | **An emitted line is a record, not a string.** `Columns()` and `Filled()` each split the emitter's own text back into lines and re-derive meaning with `StartsWith(".byte ")`, with the line map kept in parallel arrays. A small record (label, text, comment, bytes, source line) makes the map's invariant a type | after **C**; before H2, which leans on the line map |
| R4 | **One row per directive.** `ParseDirectiveLine` and `ParseExportable` are two switches over the same kinds; adding a directive touches seven places, one of them checked. A `SyntaxFacts` row of kind, exportable and block kind; `Parser.cs` into partials by area | with **A**, which is in the parser anyway; before I, which adds directives |
| R5 | **The server's directive lists** (`Directives.cs`, six of them) are tied to nothing. A test that binds a minimal snippet for each directive offered at each place and asserts it is allowed there, and the converse | with **H4** |
| R6 | **Splitting the large files** along the seams the review found: name resolution out of `Binder` (it runs after the walk and touches none of its state); built-ins, then address sizing, then data sizing out of `Evaluator`; the fourteen checkers out of `StateAnalysis`, leaving a transfer function; mode selection apart from byte placement in `CodeLayout` | each when its file is next open: `Binder` in **F**, `StateAnalysis` in **B**, the others as they come |
| R7 | **Two back-edges in Core**: `Binder` uses Layout, `CodeLayout` uses Flow. One file each. A twenty-line test beside `ApiSurfaceTests` that holds the namespace order | with R6 |

## Small mechanical fixes

Done with the workstream named, not separately.

**A — syntax and containment**
- `Lexer.cs:244-262`: `error ??=` reports only the first problem in a literal.
- `Lexer.cs:232`: `'é'` is accepted in a character literal; §4 says it is an error, and the
  string case already reports it.
- `CpuName` is lexed for two of five CPU spellings and re-checked by the parser for all five;
  drop the kind or apply it to all.
- `SyntaxToken.Step` allocates the line's token list on every `GetNextToken`; `Parser.Own` is
  quadratic once a line has a diagnostic.
- The generator: no diagnostic when `Syntax.xml` is not among the additional files; an unknown
  `Base` surfaces as a C# error in generated code rather than `NT1001`.
- A CRLF file, a BOM file and one without a final newline in the corpus; a fidelity and
  formatter-idempotence property over the truncated variants (it holds today, unguarded).

**B — flow**
- `StateAnalysis.cs:839`: a non-constant `rep`/`sep` forgets the widths before the emulation
  check that would have kept them; swap the two.
- `StateAnalysis.cs:144`: a callee declaring emulation with `a*` yields A sixteen bits wide in
  emulation mode; force eight, as `Asserted` does.
- `Instructions.IsControlTransfer` omits `per`, and answers a CPU-independent question by
  hardcoding one CPU.
- `CheckNearBank` offers `jml` as the fix for a long conditional branch.
- `CodeLayout.sizeUnknown` is instance state standing in for a parameter.
- `Cycles.cs:174`: `mvn`/`mvp` void the whole routine's count without saying why.

**C — emit and command line**
- `BuildCommand.cs:71-78`: the early return for "no files" comes before the project file's own
  diagnostics are printed, so a malformed `nt65.json` is reported as having no `files`, above
  forty lines of usage.
- `Emitter.Repeated()` refuses any line with a `;`, so a commented run never folds to `.res`.
- `OutputManifest`: one set compares case-insensitively and the other does not.
- Output files written through a temporary file and a replace, so two builds into one `out`
  cannot interleave.
- Stale-output deletions go to stderr unprefixed on a successful build.
- `.data big: .byte[$7fffffff]` is accepted.
- A sentence in §13 that C header names contain `__`, which C reserves and cc65 does not mind.

**F — semantics**
- `Binder.InModule` gives a misspelled module member no near miss; `ReportUndeclared` does.
- A symbol another file names without its being exported is reported both as not exported and
  as never used.
- `lookedUp` is a set of strings with `"name:"` and `"member:"` prefixes; make it a record.
- `Scope.Enclosing(ScopeKind)`, for the four hand-written copies in `Binder`.
- Stacked `<summary>` blocks on `ProgramModel.CheckDeclaredSignatures`, `SemanticModel.ValueOf`
  and `Binder.Export`.
- Member-ordering slips: `SemanticModel.cs:71`, `ProgramSymbols.cs:121`, `Constructs.cs:29`,
  `Expansion.cs:190`.
- `WholeProgramReason`: five of its eight values are asserted by no test.

**I — design document**
- Appendix A: `.align` takes a fill; §8 should say a boundary is a power of two.
- §7.3's list of what `.state` does not take is shorter than the implementation's and the
  grammar's.
- `kept-item` and `keep-item` are two grammar names a letter apart for opposite things.
- §12 should say that a cross-module `.spanof` exports the end label.
- A half-written `.enum` or `.struct` on one line is nine errors; Stage 25 did this for
  `.data` and not for these.

**Anywhere**
- `TestResults/` in `.gitignore`. `AnalysisLevel` in `Directory.Build.props`, so the CA rules
  run under the warnings-are-errors the build already has.

## Decided

- **No mnemonic is reserved** (2026-09-21). The review found ca65's alias mnemonics (`swa`,
  `tad`, `ina`, …) accepted as names and refused by ca65, and the first answer was to add them
  to the per-CPU reserved sets §16 already had. The owner's objection was to the shape of that
  rule rather than to its gap: which names a program may declare should not depend on a
  project setting. The two single rules were weighed. Reserving every mnemonic everywhere
  takes ordinary words for CPUs a programmer has never heard of (`set`, `map`, `neg`, `tab`;
  with sweet16, `add`, `sub`, `pop`), still moves when the pinned ca65 gains an instruction,
  and is the path on which MASM and NASM each ended up adding an escape. Reserving none costs
  nothing in the grammar, which a probe showed already reads `lda = 5`, `rts:` and `jmp rts`
  as what they are; leaves the backend's one limit to the emitter, which prefixes the few
  names ca65 would misread and takes its list from ca65's own tables; and never changes, for a
  new CPU or a new ca65. What a reader loses when a label is called `rts` is a warning's
  business, the same in every project, and a project that wants the old strictness sets
  `"mnemonic-name": "error"`. Registers stay reserved: `asl a` is a question about an operand,
  which position cannot answer. The work is in C, the warning in D, the document in I.
- **Diagnostics are named, not numbered** (2026-09-21), as D describes.
- **No suppression in the source, for now** (2026-09-21). D ships severities in the project
  file and nothing in the language. `.allow` above a declaration stays the honest spelling if
  one is wanted later, and adding it then breaks nothing.
- **Compile-time math is added, integer only** (2026-09-21): I.4 as written, exactly specified
  so that the output stays deterministic.
- **Cycle counts become two constants, `.mincycles` and `.maxcycles`** (2026-09-21), over a
  span with no call and no loop in it, in place of I.5's single `.cyclesof`: exact where the
  count is exact, and honest where a page crossing makes it a range. §16's refusal of cycle
  built-ins is amended with that reasoning.
- **Counted enums are deferred** (2026-09-21). I.6 is not part of this plan.
- **E, F and G run together**, each in its own worktree, so the question of which pair comes
  first does not arise.
