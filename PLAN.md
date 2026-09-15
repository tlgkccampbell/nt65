# nt65 — Implementation Plan

## Read this first: the design is provisional

`DESIGN.md` is a **draft**. It was written before any code existed, and it will contain
errors, contradictions (the prose and the grammar sketch in Appendix A do not always
agree), gaps and ideas that turn out to be bad once they are implemented. Implementing
it is the way we find those problems.

Treat the design as the best current statement of intent, not as a contract:

- **Expect to change it.** When the design is wrong, unclear or impractical, the
  implementation should not twist itself to comply, and it should not quietly diverge.
- **Trivial fixes**, such as a wrong line number in an example or a typo, a
  missing case with one obvious answer: fix the code and `DESIGN.md` and mention it at the
  end of the stage.
- **Anything that changes the language or the output**, such as a rule that doesn't work,
  two sections that disagree or a construct that turns out to be harder than it looks:
  stop, raise it with the user and propose a concrete change. Decide together, then
  update `DESIGN.md` and the code.
- **Where the design says nothing**, pick the simplest behaviour that fits it, leave a
  short comment in the code and mention it at the end of the stage.
- Record decisions by editing `DESIGN.md` or by a comment next to the code. No decision
  records, no ADRs and no spec documents. This is a proof of concept; process is not the
  deliverable.

Internal interfaces (syntax node shapes, module boundaries, the fixture format) are ours to
change whenever the work calls for it.

## Approach

The transpiler and the language server are brought up **together, in layers**. Each
stage adds one layer of the pipeline, makes it visible both from the command line and in
the editor, and ends at a point where we stop, run the checks listed for that stage and
look at the result before going further. Nothing in a stage depends on a later one.

The layers follow the design's own ordering (DESIGN §3.1): lines lex alone, blocks come
from a prefix sum over lines, declarations are a set, constants come before macro
expansion, flow analysis stays inside one proc, and emission comes last. If the code
starts to need a later layer to do an earlier one's job, that is a design problem worth
raising.

**Stopping points.** Stage 5 is the first complete proof of concept: a single-file 6502
program transpiles to ca65 that assembles, and an editor shows diagnostics, hover and
navigation. Stage 6 makes it usable on multi-file projects. Every stage after that adds
language surface and can be reordered, deferred or dropped at the user's call.

## Technology

**Names.** *nt65* is the language, and `nt65` is the command: the executable, the `.nt65`
file extension, `nt65.json` and the VS Code language id. The tool that implements it is
the **Norristown Assembler**, named after Norristown, Pennsylvania, where MOS Technology
had its headquarters. It stands to nt65 as Roslyn stands to C#. The solution, projects
and namespaces use `Norristown` (`Norristown.Core`, `Norristown.LanguageServer`, ...),
never `Nt65`.

**C# on .NET 10** (SDK 10.0.302 is installed). One solution holds the analysis core, the
CLI, the language server and the tests.

- **Language server.** `StreamJsonRpc` with hand-written types for the few requests we use
  (chosen in Stage 0 over `OmniSharp.Extensions.LanguageServer`, which is unmaintained
  since 2023 and heavy on dependencies). Add protocol types as stages need them.
- **Editor client.** A minimal VS Code extension that only launches the server and ships
  the TextMate grammar. It is the one piece not written in C#, and should stay a few dozen
  lines.
- **Tests.** xUnit v3. Its test projects are ordinary executables, so the edit loop can run
  a filtered subset directly rather than through `dotnet test`, if host startup turns out
  to dominate.
- **Determinism on Windows.** Format numbers with the invariant culture, never rely on
  `Dictionary` or `HashSet` enumeration order for output, and write output with `\n` line
  endings whatever the platform. Accept `\r\n` in source.

### ca65 is pinned to a commit

cc65 has not tagged a release since 2.19 but keeps changing on GitHub, so its version
number does not say what ca65 accepts. nt65 pins a commit instead: **cc65
`e11fb5c39371046ebe25485f984f644c5a0d65d3`** (2026-08-20, the head of `master` when this
plan was written). `DESIGN.md` §13 states the same pin.

- `scripts/cc65.commit` holds the SHA. A script clones cc65, checks out that commit and
  builds `ca65` and `ld65` into `.cache/cc65/` (ignored by git). `make` and MinGW `gcc` are
  installed.
- The oracle uses only that build, never whatever is on `PATH`. It checks that
  `ca65 --version` reports the pinned commit and refuses to run otherwise. The copy at
  `C:\Tools\cc65\bin` is `547d923` (2026-07-11), older than the pin, and must not be
  used by the tests.
- Moving the pin is a deliberate change: update the SHA, rebuild, run the gate, recheck
  the §13 header, and update `DESIGN.md`.

## Architecture

One analysis core, used by both the CLI and the server. The server never runs ca65.

```
Norristown.slnx
src/
  Norristown.Core/            one project; folders and namespaces per layer:
    Syntax/                   lexer (per line), line kinds, block layer, parser, red/green trees
    Project/                  nt65.json, file set, defines, segment and range tables, CPU
    Semantics/                scopes, declarations, resolution, constant and shape evaluation
    Expand/                   .if, .repeat, .each, macros
    Layout/                   instruction and data sizes, branch range, long branches
    Flow/                     basic blocks, .next/.patch, 65816 processor-state analysis
    Emit/                     ca65 output, names, header, .dbg
  Norristown.Cli/             the nt65 command (nt65 build)
  Norristown.LanguageServer/  language server
tests/
  Norristown.Tests/           unit, fixture, oracle and server tests
  fixtures/
editors/vscode/               thin client: launches the server, TextMate grammar
scripts/                      cc65.commit, cc65 build script, test and gate scripts
.cache/                       git-ignored and rebuildable: cc65 checkout and build, oracle cache
```

Keep the core in one project, split by folder, so incremental builds stay quick. Split it
only if a real dependency problem appears.

A few properties are cheap from the start and expensive to add later, so build them in
from Stage 1:

- **Lexing is per line with no cross-line state**, and the block tree is a separate pass
  over per-line +1/−1/0 values (§4). The parser never aborts a file: a bad line becomes
  an error node and the next line parses normally.
- **Syntax trees are built the way Roslyn builds them: red/green, and laid out for reuse
  when parsing incrementally.**
  - *Green nodes* are immutable and hold a kind, a width and children, with no parent and
    no absolute position. So after an edit, the node for an unchanged line is still the
    same object, wherever the edit moved it.
  - *Red nodes* are thin wrappers, created on demand, that add the parent and the absolute
    position. Everything above the parser works with red nodes.
  - Trees are full fidelity. Whitespace, comments and line breaks are trivia attached to
    tokens, and a tree's text is exactly its source, including error nodes and missing
    tokens.
  - The green tree is shaped for reuse: one node per line, under nodes for the block
    structure. A line's syntax depends only on its tokens and the kind of block around it
    (§3.1). So an edit re-lexes and re-parses only the lines it touches, plus any line
    whose enclosing block kind changed. It rebuilds only the path from those lines to the
    root, and reuses everything else by reference.
  - Tokens and small nodes that recur often (mnemonics, registers, punctuation with common
    trivia) are shared through a green-node cache, as in Roslyn.
  - Lexer tokens are green tokens from Stage 1, so nothing has to be converted later.
- **Everything carries a source span** (file, line, column range). In syntax, spans come
  from red nodes. Generated output lines carry the span they came from, including the
  call site for macro expansions, which is what `.dbg line` needs (§13).
- **Diagnostics are data**: a span, a severity, a message and optional related spans. No
  diagnostic code tables.
- **No order dependence.** A program is a set of files. The test harness shuffles file
  order and expects identical output and diagnostics.

## Testing and turnaround

Fast tests are a priority. At every stage boundary, check how long the loop takes and
whether it can be made faster.

- **Fixture tests** (the bulk). A case is a directory of `.nt65` files, an optional
  `nt65.json`, expected `.s` output and expected diagnostics. Diagnostics are written
  inline in the source as trailing comments such as `;! error: not available on the 6502`,
  so a case reads as one file. An update flag rewrites expected output. These run
  in-process, in parallel, and take seconds.
- **The ca65 oracle.** Output is only right if ca65 agrees. For each emitted `.s`, run
  `ca65 -l` and require **no errors and no warnings** (§3.2: a ca65 diagnostic on nt65
  output is an nt65 bug), then compare each listing line's byte count with the length
  nt65 computed (§7.3). Where the stage calls for it, link with `ld65` using a tiny linker
  config kept in the fixture. Oracle runs are parallel and cached by hash of the `.s`
  content, so unchanged output is not reassembled.
- **Server tests** drive the server in-process (open, change, hover, definition,
  rename) without an editor.
- **Commands**: `scripts/test.ps1` (fixtures and units; the edit loop),
  `scripts/test.ps1 -Ca65` (oracle only), `scripts/gate.ps1` (build, both suites; run once
  per stage or unit of work). Fail fast, and let a single fixture be selected by name.
- **DESIGN.md is a corpus.** As each construct comes online, turn the design's examples
  for it into fixtures. An example that doesn't work is a design finding. For example,
  the §13 output comment says `main.nt65:29` for a call on line 32.

## Stages

Each stage lists what to build, what the editor gains, what waits, and the checks to run
before moving on. Section numbers refer to `DESIGN.md`.

### Stage 0: Skeleton and harness

**Build.** The solution layout, build settings (nullable on, warnings as errors) and
test project. The cc65 pin: `scripts/cc65.commit` and the script that builds ca65 and ld65
at that commit. A `nt65 build` CLI that accepts files and does nothing yet. The fixture
harness with inline diagnostics and output snapshots. The ca65 oracle runner with caching
and the pinned-version check. The LSP library choice, and a VS Code extension shell that
starts an empty server.

**Check.**
- `scripts/test.ps1` runs an empty suite, and `scripts/gate.ps1` runs a trivial oracle case.
- The oracle refuses to run with a ca65 that is not the pinned build.
- The §13 output header, pasted into a `.s` by hand, assembles on the pinned ca65 with no
  warnings. If it doesn't, the header in the design is wrong. Raise it now,
  since every later stage emits it.
- The extension launches and the server logs a connection.

### Stage 1: Lexer, line kinds and blocks

**Build.** The token set of §4: identifiers, `::`, `->`, `..`, numbers in all bases,
characters, strings with the fixed escapes, directives, mnemonics of all three CPUs and
the long branches, registers, and the `ident :` / `ident !` ambiguities. Classify lines
by the table in §4. Build the block layer: per-line brace values, the rule that a `{`
inside an open parenthesis does not open a block, continuation lines (`} .else {`,
`} name {`), and recovery when braces are unbalanced.

**Editor.** A TextMate grammar for highlighting, generated from or checked against the
lexer's token classes.

**Not yet.** Parsing inside lines.

**Check.**
- Unit tests for each token class and each line kind.
- Every code block in `DESIGN.md` written in nt65 lexes and builds a balanced block tree
  (skip the ca65 output blocks).
- A file with `m!({` half typed in the middle recovers: blocks after it are still found.
- Re-lexing one edited line matches a full re-lex.

### Stage 2: Parser for the core language

**Build.** A parser from lines to a red/green syntax tree for the core items: labels, constants, cheap
locals, data directives (`.byte .word .dword .addr .faraddr .res .asciiz`), `.proc` with
its full signature syntax (parsed and kept, not yet used), extern procs, `.scope`,
segment declarations, segment blocks and shortcuts, `.export`, `.import` in all its
forms, and `.cpu`. Instructions with **every** operand form of every CPU, since syntax
does not depend on the CPU (§5.1, §7.1). Expressions with C precedence and the required
parentheses of §9.

**Editor.** Nothing new beyond Stage 1; the server is wired in the next stage.

**Not yet.** Types, `.if`/`.repeat`/`.each`, macros, `.next`/`.patch`/`.state`/`.ensure`/
`.frame`. Their lines parse as "not supported yet" nodes rather than syntax errors.

**Check.**
- Fixtures for each item and operand form, and for each parenthesis trap
  (`1 << i + 1`, `a || b && c`, `#<label+1`).
- The §6.2 and §13 input examples parse without errors.
- Full fidelity: every fixture's tree gives back its source text byte for byte.
- A tree printer renders fixtures in readable form, which is useful for every later stage.
- Incremental parsing: replaying a sequence of edits gives the same tree as parsing from
  scratch each time. Green nodes for untouched lines are reused by reference, checked by
  identity in the test.

### Stage 3: Language server online (syntax)

**Build.** A server that keeps an open-document cache, re-parses a document incrementally
on change (reusing green nodes from the previous tree) and publishes syntax diagnostics.
Document symbols (outline: procs, scopes, labels, constants, segment blocks). Folding
ranges from the block tree.

**Check.**
- In VS Code, typing a syntax error shows it immediately, and fixing it clears it.
- The outline reflects nesting, and folding works on every block.
- Server tests cover open, change and diagnostics.

### Stage 4: Declarations, scopes and constants (single file)

**Build.** Scope construction (§6.1, §6.2): procs, scopes, segment blocks that don't
start a scope, cheap-local rules, duplicate declarations. Name resolution with `::`
paths and lookup order. Classify symbols as constant, address or address alias. Constant
evaluation with forward references and cycle detection. Address size of each symbol from
its segment or value (§7.2), using the segment table with the standard names
predeclared. Undeclared segment names are an error. The reserved-word rules of §4.

**Editor.** Hover (kind, value, address size, segment), go to definition, find
references, document highlights, rename, and diagnostics for undefined, duplicate and
cyclic names, all within a file.

**Not yet.** Other files, defines, types.

**Check.**
- Fixtures for every scoping rule in §6.2, including the two `@loop`s in the §6.2
  example, cycles, and forward references.
- Rename a cheap local and a scoped label in the editor. Hover shows correct sizes.

### Stage 5: First transpile (single-file 6502 and 65C02)

**This is the first proof-of-concept stopping point.**

**Build.** CPU tables for the 6502 and 65C02: mnemonics, addressing modes and lengths.
Wrong-CPU mnemonic and mode diagnostics. Addressing mode selection (§7.2: narrowest mode
at least as wide as the operand, explicit prefix in output). The `brk #s` / `cop #s`
rule. Near/far checks on control transfers. Instruction and data lengths. Emission
(§13): header, `.dbg file` and `.dbg line`, segments with address sizes, nested segment
blocks as `.pushseg`/`.popseg`, flat names with the generated-name scheme, `z := *` for
labels named `z` or `f`, parenthesization of expressions, character and string data as
bytes with source text in a comment. Extern procs as constants. Deterministic output,
and `nt65 build` rewrites a file only when its content changes.

**Editor.** CPU diagnostics.

**Check.**
- The §13 example produces the §13 output, apart from any design errors found and fixed
  on the way.
- **Oracle:** every Stage 2–5 output fixture assembles with no warnings, and listing
  lengths match nt65's lengths.
- Probe the output spellings this stage depends on and record anything surprising in
  `DESIGN.md`: how far a `z:` prefix reaches in `z:ptr+1`, `z := *`, the `brk` form ca65
  accepts per CPU, and that `ca65 -g` plus `ld65 --dbgfile` map spans to `.nt65` lines.
- Output is identical across two runs and across shuffled input order.
- Stop here and review with the user before continuing.

### Stage 6: Projects and modules

**Build.** `nt65.json` (§5.3): CPU, file globs, output tree, defines and `-D`, segments
with sizes. Program-wide tables: the export map, the segment table (declared exactly
once), the CPU agreement check. Cross-file resolution (§12): addresses become
`.import`/`.importzp`, constants are emitted by value, defines are emitted as their
values. Exports with nt65's address size, `outer__inner` for exported interior labels,
and collision renaming against imports. `.import` of external symbols with sizes,
`proc(...)` signatures and checked imports with an `lderror` assertion.

**Editor.** The server loads the project, resolves across the workspace, and supports
cross-file definition, references and rename. Diagnostics for unexported references,
duplicate exports and conflicting segment declarations.

**Check.**
- A multi-file fixture assembles and **links with ld65** against a small hand-written
  ca65 module that calls into nt65 and is called from it. A checked import with a wrong
  value fails the link.
- Edits in one file update diagnostics in another in the editor.
- Probe `.export s: far` and `.assert N = v, lderror` on the pinned ca65.

### Stage 7: Types, data and text

**Build.** `.enum`, `.struct`, `.union` (§6.3); `.tag` instances, arrays and initialized
instances; fields of an instance as sub-symbols; `.sizeof` and `.countof`. `.list` (§6.4)
and its uses in data directives. `.charmap` and its application (§8). `.func` (§9). The
remaining data directives: `.lobytes`, `.hibytes`, `.align`, `.incbin` (read the file for
its length; relative path in the output). Members exported as flat constants. The
remaining built-in functions that don't need layout or configuration.

**Editor.** Hover shows member offsets, sizes and counts. Completion after `::` becomes
worthwhile here and is optional.

**Check.**
- The §6.3, §6.4, §8 and §9 examples as fixtures, through the oracle.
- An initialized instance emits one directive per member with the correct bytes. Compare
  the assembled bytes against a hand-written ca65 equivalent for one or two cases.

### Stage 8: Configuration, repetition and assertions

**Build.** `.if`/`.elseif`/`.else` with conditions restricted to literals, built-ins and
defines, evaluated **before** declarations are collected (§10). `.defined`, `.target`,
short-circuit evaluation, and declarations under `.if` in the enclosing scope. `.repeat`
and `.each` over lists and enums, with per-iteration names. `.assert` (evaluated where
possible, otherwise passed to ca65) and `.error`.

**Editor.** Inactive branches are dimmed and not resolved. Defines hover to their value.

**Check.**
- The §10 examples, including the same name declared under two `.if`s.
- A condition that names a program symbol is an error that says to use `.assert`.
- One fixture built under two sets of defines gives two correct outputs.

### Stage 9: Macros

**Build.** Macro definitions and calls (§11): parameter kinds, defaults and named
arguments, braced operands, `.mode`, `.byteof`, `.empty`, `list` and `one` parameters,
`block` parameters and continuation blocks. The recursion check from names. Hygiene:
locals per expansion, `ident` parameters cannot declare, caller-side names in blocks.
Forbidden items. Call-site checks with a note pointing at the body line. Expansion after
constants and before layout. Output with the call comment, and debug lines mapped to the
call. Exported macros expand in the importing file.

**Editor.** Definition, references and rename through macro bodies and block arguments
**without expanding** (§11.1). Diagnostics from an expansion land on the call with related
information in the body. Hover on a call can show the expansion.

**Not yet.** Macro state signatures (§11.5), which come with the 65816 analysis.

**Check.**
- The §11 examples (`set16`, `mov16`, `push`, `times_x`, `if`/`branch_unless`) through the
  oracle.
- An argument like `1 + 2` bound to `value * 2` gives 6.
- Renaming a label used inside a block argument works in the editor.

### Stage 10: Control flow and layout (all CPUs)

**Build.** Basic blocks and edges inside each proc. `.next` and `.patch` (§7.4), with
names checked on every CPU. The unreachable-label warning, and data reached by
fall-through. Tables and lists as `.next` targets. Branch range within a segment block
(§7.6). Long branches with the grow-until-stable loop. `.endof`/`.spanof` with `f__end`.
Cycle intervals for the 6502 and 65C02.

**Editor.** Out-of-range branch diagnostics. Cycle intervals as hover or inlay hints per
instruction and per block.

**Check.**
- A forward `jeq` within range emits `beq`; one out of range emits the inverted branch
  over `jmp`. The oracle agrees on lengths in both cases.
- The `bit` skip trick of §7.4 needs its `.next` and is accepted with it.

### Stage 11: 65816 processor state: widths and mode

**Build.** 65816 mnemonics, operand forms and lengths, including `mvn`/`mvp`, `wdm`,
`jsl`/`jml`/`rtl` and `per`/`brl`. Signatures (§7.3) with defaults and `*` items. The
state lattice and worklist over basic blocks for A and index widths and the emulation
flag. Transfer functions for `rep`, `sep`, `xce` after `clc`/`sec`, and `php`/`plp` with
the analysis stack. Calls, tail calls, returns and their checks. `.state`. Width
directive placement in output (§7.3, "Widths in the output"). The "outside any proc"
errors. Cycle intervals for the 65816.

**Editor.** Width, mode and near/far diagnostics at the use site. Hover on an instruction
shows the state reaching it.

**Check.**
- The `render` and `fill` examples of §7.3. The `fill` output has its `.a16` where the
  design shows it.
- **Oracle on 65816 output is the key check for this stage:** listing lengths expose any
  immediate sized wrongly.
- The claim that the analysis converges in at most two passes per block is measured, not
  assumed.

### Stage 12: 65816 unchecked constructs and stack

**Build.** The full requirement table of §7.4 on the 65816: indirect jumps and calls,
`rts` used as a jump, computed jumps, labels used as data, jumps into another proc,
falling off the end with an adjacency check, handlers with `a?, i?`. `inline` signatures.
Relative calls `per`/`brl` (with `phk`). `.ensure` with emission chosen after convergence.
`.frame` and stack-relative slots. Macro state signatures (§11.5).

**Editor.** Each unannotated construct is a diagnostic that says which annotation it
needs.

**Check.**
- The `dispatch`, `nmi`, `copy` and `add16` examples.
- `.ensure` emits nothing where widths already hold and the right `rep`/`sep` otherwise,
  confirmed by the oracle.

### Stage 13: 65816 direct page and data bank

**Build.** Segment `dp =` and `bank =` from files and `nt65.json`, and the `ranges` table.
D and B in the lattice, and the idioms of §7.5. The `d:` prefix. Direct-page and bank
checks with their exemptions. The absolute-operand-on-`dp≠0` error of §7.2. The D-dependent
cycle adjustment.

**Check.**
- `sta $2100` with B at `$7e` against a `ranges` entry is caught.
- `lda d:$2105` with D at `$2100` emits `lda z:$05`.

### Stage 14: Incremental analysis and editor polish

Only as far as measurements show a need. Up to here, re-analyzing the whole program on
each edit is acceptable if it stays fast.

**Build.** A file interface (§14) computed and compared per file, so an edit that leaves
it unchanged does not re-analyze other files. Per-proc flow reruns. Expansion caching on
definition, arguments and defines. `.incbin` files as dependencies.

**Check.**
- A generated project of a few hundred files: time a keystroke in a proc body before and
  after, and report both numbers.
- Incremental results equal a from-scratch analysis, checked by a test that replays edit
  sequences both ways.

## At the end of each stage

A few lines to the user, in the conversation or the commit message, not a new document:

- what now works, and how to see it (a command, a fixture, something to try in the editor);
- the gate result, and how long `scripts/test.ps1` and `scripts/gate.ps1` take;
- design problems found, what was fixed directly, and what needs a decision;
- anything deferred to a later stage.

Then wait for the go-ahead before starting the next stage.
