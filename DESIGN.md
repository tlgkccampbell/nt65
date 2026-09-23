# nt65 — Norristown Assembly Language

**Status:** version 1. This document defines nt65 version 1: the language, and what the
`nt65` command promises about its output, its project file and its command line (§17), with
the reasoning behind each. Where the implementation and this document disagree, one of them
is wrong, and the fix says which. For a first look at the language from ca65, see
[the guide](docs/GUIDE.md).

## 1. What nt65 is

nt65 is an assembly language for the 6502, its CMOS variants and the 65816 that **transpiles to ca65**.
Every nt65 program becomes an ordinary ca65 source file that assembles with ca65 and
links with ld65, using whatever linker configuration the project already has.
nt65 is to ca65 as TypeScript is to JavaScript: a stricter, more analyzable language
that targets an existing, widely deployed toolchain rather than replacing it.

### Goals

- **It is assembly.** The majority of lines are 6502-family mnemonics with familiar
  operands. Anyone who reads ca65 reads nt65.
- **Context-free syntax.** The syntactic category and structure of a line are fixed by
  the tokens on that line and the kind of block around it. There are no feature flags,
  no user-defined syntax and no textual substitution.
- **Order-independent meaning.** What a declaration means depends only on the set of
  declarations in the program, never on which file they are in or the order they appear.
  Forward references are unremarkable.
- **Tooling first.** The language is designed so that an editor can parse, resolve,
  rename, hover and diagnose without running an assembler.
- **ca65 interop at the object file.** nt65 output links against hand-written ca65
  objects and cc65 output, and ca65 code can use what nt65 exports. nt65 never reads
  ca65 source. The interoperability contract below states exactly what is promised.

### Non-goals

- Not a high-level language. No runtime types, no structured control flow, no register
  allocation. (Macros can build such things; the language does not.)
- Not an assembler. nt65 never produces object code and does not read linker configs.
- Not a ca65-compatible source dialect. Where ca65 syntax was harmless it was kept;
  where it was the cause of the problem it was changed or removed.

### Interoperability contract

The priority is working alongside existing ca65 and cc65 code in the same build, not
accepting ca65 source. nt65 promises a project that mixes them:

1. **Toolchain.** `nt65 build` writes one ca65 source file per module, named after it, and a
   line map beside each (§13); a module another places (§12) is written into that one's file
   instead of a file of its own. The project
   assembles those files with its existing ca65, built from the cc65 commit nt65 pins
   (§13), and links them with its existing ld65 configuration. nt65 never runs ca65 or ld65, and never reads,
   requires or changes a linker configuration. A build that wants source-level debugging runs
   `nt65 remap-dbg` on the debug file afterwards; one that does not can leave the maps alone.
2. **Command line.** The output assembles to the same bytes under any ca65 options the
   project uses (§13). Where an option genuinely conflicts with nt65's declarations,
   such as a `-mm` memory model against the segment table or a `-D` name against a
   declared name, ca65 reports an error.
3. **Segments.** Output goes only to segments named in the source or in `nt65.json`,
   each with its declared address size. Within a file, items keep their source order in
   each segment, and a placed module's items stand where its `.place` does; the order across
   files is the project's link order.
4. **Symbols.** Everything shared with ca65 is an ordinary linker symbol (§12). An
   export is spelled as its path with its module's in front, joined with `__`
   (`gfx__clear` for `clear` in module `gfx`, `gfx__clear__again` for an exported interior
   label), or as the name its `as` gives, and carries the address size nt65 uses or its
   export states: `.exportzp` for a zero-page label or a constant below `$100`, `far` for a
   far label or constant. Enum, struct and union members are exported as flat constants, a
   struct or union's size as its linker name and `__sizeof`, and a checked import is verified
   by ld65. A module imports only what it uses, so it pulls nothing else out of a library.
5. **Nothing extra.** nt65 adds no runtime, library, segment or startup code. Every
   label it generates is local to its file, except an `f__end` label that another file
   uses through `.endof` and the `__sizeof` of an exported struct or union.
6. **Deterministic output.** The same sources and configuration produce byte-identical
   output, and `nt65 build` rewrites only the output files whose contents change, so a
   build system reassembles only what an edit affected.
7. **Debugging.** With `ca65 -g` and `ld65 --dbgfile`, followed by `nt65 remap-dbg` on the
   debug file, debug information refers to `.nt65` files, by their paths from the project
   root, and lines (§13): the line that wrote the byte, or the repetition's own line for the
   bytes of a `.repeat` written back as one (§13). Generated names are derived from source names. The output
   itself carries no debug directives: each `foo.s` is written with a `foo.s.lines` beside
   it, which is what `remap-dbg` reads.
8. **C.** `nt65 build --c-header` writes a C header of what the program exports, in cc65's
   types, and make-style dependencies come from `--depfile` (§5.3, §13).

nt65 does not promise:

- to read ca65 source, include files, macros or object files;
- to check calls into nt65 routines from ca65: a ca65 caller must honour the routine's
  declared signature, and checking stops at the boundary;
- a calling convention: signatures describe processor state only, and cc65's C calling
  convention (the software stack, return values in A/X) is the programmer's job;
- cc65's start-up registration: nt65 has no `.constructor`, `.destructor` or `.interruptor`,
  so a routine cc65's runtime runs at start-up is registered by a ca65 stub that calls the
  nt65 routine;
- stable generated names across edits: only linker names and the fixed spellings
  `outer__inner` and `f__end` are stable;
- that a debugger shows scopes: debuggers see flat names such as `draw__loop`.

## 2. Why ca65 resists analysis

Almost every hard case in analyzing ca65 comes from *modal state* (something set on one
line that changes how later lines are read) or *user-defined syntax*. nt65 answers each
one structurally.

| ca65 feature | Why it is contextual | nt65 answer |
|---|---|---|
| bare macro invocation `foo arg` | an unknown word in mnemonic position may be a macro; cannot parse until all macros are known | invocations are spelled `foo!(arg)` (§11) |
| `.define` | textual substitution anywhere in the token stream | removed; constants and functions (§9) |
| `.feature` | changes the lexer and parser mid-file | removed; one fixed grammar |
| `.setcpu` | changes the mnemonic set mid-file | one CPU per program (§5.1); all mnemonics always lex |
| `.segment`, `.org`, `.pushseg`/`.popseg` | modal | segment regions and blocks, found from lines alone (§5.2) |
| `.a8` `.a16` `.i8` `.i16` `.smart` | modal register width | declared on `.proc`, flow-analyzed inside it, asserted with `.state` (§7.3) |
| zero page vs absolute guessed at the point of reference | depends on what has been *seen so far* | derived from declarations; always explicit in the output (§7.2) |
| `.set` (re-assignable symbols) | value depends on position | removed; symbols are single-assignment |
| `.include` | textual inclusion | project-wide modules (§12) |
| unnamed labels `:` `:+` `:-` | relative position | removed; `@local` labels cover the use |
| `.ident`, `.concat` used to build symbol names | computed identifiers | removed |
| `.local`, `.global` | modal scoping | structural scoping rules (§6) |
| `.charmap` | modal text encoding | named mappings applied explicitly (§8) |

## 3. Principles

### 3.1 Three levels of order

1. **Lexing and parsing** a file needs nothing outside that file. Brace nesting is the
   only cross-line state. No setting, declaration or other file can change how a line
   is read.
2. **Declarations** are a set. The meaning of any item depends only on which
   declarations exist in the program, not their order or file. Which declarations exist
   is fixed by the build configuration (the defines of §5.3) before anything in the
   program is evaluated, because `.if` conditions test defines and never program
   symbols (§10). The one thing the configuration does not fix by itself is a family's
   instances, which are one declaration per member of an enum (§10): the members are a list
   written out in the source, under conditions the configuration answers like any others, so
   reading the enum's headers is enough and nothing has to be evaluated to know which
   instances there are. Cycles among constant definitions are errors, not sequencing puzzles.
3. **Sequential state** exists only inside a `.proc` body and is limited to the
   instruction stream itself, the current address `*`, and 65816 processor state:
   register widths, the emulation flag, the direct page and the data bank. That state
   is recovered by a flow analysis that never leaves the proc: it consumes other
   routines only through their declared signatures, so it does not disturb rule 2.

Macro expansion (§11) sits between levels 2 and 3. An expansion may read constants,
through its arguments, but nothing at levels 1 and 2 reads an expansion: every line
parses, every name resolves, and every constant and shape has its value without
expanding a macro. The same holds for `.repeat` and `.each` bodies (§10).

### 3.2 The output is fully explicit ca65

The transpiler never lets ca65 guess. Every zero-page/absolute/far choice is written
with a `z:`/`a:`/`f:` prefix; every width-dependent immediate on the 65816 is preceded by
the width directive it needs; every expression whose meaning could depend on operator
precedence is parenthesized; names are flat, so ca65's scoping rules never apply;
character and string data is written as byte values, so no target character set can
change it; macros are expanded, `.if` is resolved and every repetition is unrolled before
output, each turn decided on its own. A counted `.repeat` whose turns then came out as the
same lines is written back as ca65's own `.repeat` around those lines, and one whose turns
differ only in the number the binding was worth as a `.repeat` with a counter (§13): ca65 is
still guessing nothing, because every choice was made before the comparison that allowed it —
it is repeating a line nt65 settled, and at most counting, not working one out.

The output assembles to the same bytes under any ca65 command line a project already
uses, with the pinned ca65 (§13). The header switches off everything a command-line option
can switch on (§13), and the output is written so that the remaining options (`-t`,
`-D`, `-mm`) cannot silently change it. If ca65 reports an error on nt65 output, that is an
nt65 bug, and so is a warning at any level ca65 has: the oracle assembles at `-W2`, which is
every warning ca65 knows, rather than at the default level, because a warning nobody is shown
at the default is still the output saying something a reader would have to answer.

### 3.3 Same spelling, same meaning

Where nt65 keeps a ca65 spelling, it keeps the ca65 meaning. Where nt65 changes the
meaning, it changes the spelling, so no construct silently means something different.
Expressions are the exception: they follow C's precedence, not ca65's (§9).

## 4. Lexical structure

- **Encoding:** UTF-8. **Comments:** `;` to end of line.
- **Every line lexes on its own.** There are no block comments, no line continuations
  and no multi-line strings, and nothing else may be added that spans lines at the
  lexical level. An edit re-lexes only the lines it touches.
- **One statement per line.** An opening `{` ends the line of its opener; a closing `}`
  starts a line and may be followed only by `.else {`, `.elseif expr {` or, after a
  macro call's block, `name {` for its next block (§11.4).
- **Leading whitespace is insignificant.** Labels may be indented. Because it means nothing
  there is nothing to argue about, so nt65 has one layout and writes it (§5.3): names at the
  margin of whatever holds them, what a block holds indented four columns further than the
  line that opens it, a routine's cheap locals at the routine's own margin, a run of named
  data lines with nothing between them lined up one column past the longest name in the run,
  and nothing after a line's last token. A `.segment NAME` region opens a block with no
  brace, so it indents nothing. It is the layout the generated ca65 is written in (§13), and
  the only whitespace inside a line it touches is the gap a run lines up on, so laying a file
  out cannot change what any line of it means.
- **Identifiers:** `[A-Za-z_][A-Za-z0-9_]*`, case-sensitive. Scoped names use `::`
  (`gfx::init`, `::hw::init`); `::` is one token, so `z::foo` walks into scope `z`
  and a prefix on a path from the root of the modules is written `z: ::hw::foo`. Cheap
  locals are `@name` (§6.2).
- **Reserved words:** the registers `a`, `x`, `y`, `s` (case-insensitive) and every
  `.directive` (also case-insensitive). **No mnemonic is reserved**, on any CPU. A line that
  starts with a mnemonic is an instruction unless the token after it is `:` or `=`, or `!`
  followed by `(`, which is what makes `rts:` a label, `lda = 5` a constant, `bne!(x)` a call
  to a macro named `bne` and `jmp rts` a jump to that label, while `lda !flag` stays an
  instruction; a
  mnemonic of a CPU the program is not built for is an error only where it is written as an
  instruction. The mnemonics are the canonical WDC names, with the bit number in
  `bbr0`–`bbr7`, `bbs0`–`bbs7`, `rmb0`–`rmb7` and `smb0`–`smb7` as ca65 spells them; ca65's
  alternative 65816 spellings (`tad`, `swa`, ...) are not nt65 mnemonics at all. A user
  symbol, a macro parameter or a member of an anonymous enum may not be named `X`, and may be
  named `lda`. Members of a named struct, union or enum are exempt even from that: they are
  always reached through `::`, where a register name is unambiguous (`Point::x`). What a name
  that is also an instruction costs is a reader's double take, so **nt65 warns** where one is
  declared — `mnemonic-name`, the same in every project, on a word any CPU nt65 knows has an
  instruction by, whichever CPU this program is built for. It is quiet where the spelling at
  the use cannot be taken for an instruction: a member reached through `::`, an `@local`
  label, and a macro, which is only ever called as `name!(...)`. That is what lets a library
  of macros spell another processor's instructions as that processor does, `bne!(@loop)`
  beside the 6502's own `bne`. The output has its own answer to the same problem (§13), which needs nothing of the
  programmer.
- **Numbers:** `$1F` hex, `%1010` binary, `255` decimal, `'c'` character. `6502x`, `65c02` and
  `65sc02` are CPU names, one token each, and `r65c02` a word; the CPU names are reserved where
  a CPU is named (§5.1) and mean nothing anywhere else. A `_` between two of a number's digits
  separates them and is worth nothing, in any base: `$7f_ff`, `%1010_1010`, `1_000`. It stands
  between two digits, so it may not begin or end a number or stand beside another `_`. The
  output writes every number without separators, and switches ca65's own
  `underline_in_numbers` off (§13).
- **Strings:** `"..."` with fixed escapes `\n \r \t \0 \\ \" \' \xHH`, which character
  literals share; `\0` is `\x00` written short. Outside a charmap
  (§8), character and string literals are ASCII: a non-ASCII character is an error, and
  `\xHH` writes any byte.
- **Address-size prefixes:** `z:`, `a:`, `f:` (as in ca65), and `d:` for a constant
  address reached through the direct page (§7.5). `d`, `f` and `z` are also
  ordinary identifiers, so the lexer emits `ident` `:` and the parser decides by
  position: a label at line start, a prefix in operand position.
- **Multi-character tokens:** `::`, `->` and `..` are single tokens, so `a->b` is not
  `a - >b` (the high byte of `b` subtracted from `a`); written with spaces, it is.

Every line is classified by its first one or two tokens and, for member and
bare-identifier lines, the kind of block that encloses it:

| first tokens | line kind |
|---|---|
| `.word` (any directive) | directive / block opener |
| `ident :`, `@name :` | label, optionally followed by a statement |
| `ident =`, `@name =` | constant; inside `.enum`, a member with an explicit value; inside a record initializer, a member's value (§6.3) |
| `ident !` | macro invocation |
| `ident` alone | bare identifier: an enum member inside `.enum`, a list item inside `.list` (§6.4), a `block` parameter splice inside a macro body (§11.4), an error elsewhere |
| mnemonic | instruction |
| any other expression | list items inside `.list` (§6.4), values inside a data body (§8), an error elsewhere |
| `}` | block close, optionally continuing with `.else {`, `.elseif expr {` or a macro's next block, `name {` |

**Block structure is a layer over lines, not part of parsing them.** A line whose last
token is `{` opens a block, unless that `{` is inside a parenthesis still open on the
line, so a half-typed `m!({` does not swallow the rest of the file; a line whose first
token is `}` closes one; a `} .else {` or `} name {` line does both and counts as 0; any
other brace (the `{operand}` grouping in macro arguments, §11, and values or a record
written in braces on one line, §8) must be balanced within its own line. The block layer is
therefore a per-line value of +1, −1 or 0, and the tree is recovered from a prefix sum without
looking inside any line. Every block opener is a keyword line, a macro call or a
continuation line, which gives error recovery an anchor when braces are unbalanced
mid-edit; there are no bare `{` blocks, `.scope {` serves that purpose.

A `.segment NAME` line with no brace and no size is a **region** line (§5.2). At file level
it opens a block with no brace, which holds every line up to the next region line or the end
of the file. It is found from its own tokens, as a brace is, so an edit to one moves only the
lines up to the next; anywhere else it is an ordinary line, which is an error.

Recovery uses that anchor only when it must. While the braces balance, the tree is exactly
the prefix sum, so a construct written where it may not appear, such as a `.proc` inside a
`.proc`, keeps the structure it was written with and is reported by the parser rather than
guessed at by the block layer. Where they do not balance, a `}` with no open block is
reported and treated as an ordinary line, a `.proc` or `.macro` opener inside a proc or
a macro closes the blocks back to outside it, and a region line closes every block, so the
items after a missing `}` are still found.

## 5. Program structure

A program is the set of `.nt65` files handed to the transpiler. Each file is a module, and
begins by saying which, `.module name` (§12). After that it is a sequence of **items**:
constants, `.config` settings, `.data` declarations, `.proc`, `.scope`, `.macro`, `.enum`, `.struct`, `.union`,
`.charmap`, `.list`, `.func`, `.signature`, `.export`, `.import`, `.use`, `.if`, `.repeat` and
`.each` at item level, unnamed `.res` and `.align` padding, segment declarations, segment regions,
segment blocks and `.place`.

Outside a proc there are no instructions and no labels. Code lives in a `.proc`, and every
byte outside one belongs to a `.data` declaration (§8), except unnamed `.res` and `.align`,
which pad between declarations. An instruction or a label outside a proc is an error, at file
level and inside `.scope`, `.if`, `.repeat`, `.each` and segment blocks alike. Inside a proc,
a label is a position in code: a branch target, inline data after a call, an interior entry
point. A label is only ever a location, and never has a size (§8).

### 5.1 CPU

```nt65
.cpu 65816
```

One CPU per program. It may be given on the command line or by a `.cpu` item; every
file that states it must agree. The CPUs are:

| CPU | instructions | ca65 |
|---|---|---|
| `6502` | the NMOS 6502 | `6502` |
| `6502x` | the NMOS 6502 and its undocumented opcodes, below | `6502X` |
| `65sc02` | the original CMOS set: `phx`, `stz`, `bra`, `(zp)` and the rest, without the bit instructions or `wai` and `stp` | `65SC02` |
| `r65c02` | Rockwell's: the 65SC02 and the bit instructions `bbr`, `bbs`, `rmb` and `smb` | `65C02` |
| `65c02` | WDC's: Rockwell's, and `wai` and `stp` | `W65C02` |
| `65816` | the 65C02 without the bit instructions, and the 65816's own | `65816` |

Each checks exactly its own instructions, and the output sets the matching ca65 CPU (§13).
The CPU does not affect parsing: all mnemonics and addressing modes always lex and parse,
and using one the target lacks is a semantic diagnostic that says which CPUs have it
("`stz` is not available on the 6502, and is on the 65sc02, r65c02, 65c02 and 65816").

Code that differs between CPUs tests what the CPU has, `.if .has(phx)`, which holds on every
CPU with the instruction; `.target(65c02)` names one CPU exactly (§9, §10). The CPU is
configuration, like a define.

**The undocumented opcodes.** The NMOS 6502 does something for every one of the 256 bytes it
can read as an opcode, and what it does for the 105 nobody documented is used by C64 and NES
programs that were counting cycles. They are the `6502x`, not the `6502`, because they are not
a processor's instruction set: no datasheet lists them, a CMOS part does something else
entirely with the same bytes, and a program that writes one has decided something about which
silicon it runs on. nt65 takes their spellings, their forms and their encodings from ca65's
`6502X` table, because what nt65 writes has to be what ca65 assembles, and there is no other
list with a claim to be the names. `slo`, `rla`, `sre`, `rra`, `dcp` and `isc` are a
read-modify-write and an arithmetic instruction at once; `lax` and `sax` move A and X together;
`alr`, `anc`, `ane`, `arr` and `axs` take an immediate; `sha`, `shx`, `shy`, `tas` and `las`
reach memory through the stack pointer or the high byte of their own address; `jam` stops the
processor. `nop` grows operands here and nowhere else, since its undocumented encodings read
one.

What each *costs* is a fact about the NMOS timing and is counted like any other instruction.
What each *leaves behind* is not always one answer: `ane`, `lax #`, and the stores that mix in
the high byte of their own address depend on the part and on what the bus was last driven with.
nt65 counts them and says on hover which are unstable, rather than refusing to count a line
whose timing is not in doubt. `jam` is the one with no count at all — there is no next cycle to
reach — and a routine that runs into one is shown as uncounted with that as the reason, the way
a block move is.

Writing one of them on a CPU that has not got it says so, and says what it is: `lax` on the
6502 is "not available on the 6502, and is an undocumented opcode of the NMOS 6502, which the
6502x has", rather than a word nobody declared.

### 5.2 Segments

```nt65
.segment ZP2: zp                ; declaration: this segment is zero page

.segment ZP2                    ; a region: what follows is in ZP2
.data ptr: .word
.data tmp: .byte

.segment CODE
.proc main: a8, i8 {
    ...
}
```

A **region** line, `.segment NAME` with no brace, places every item after it in the named
segment, up to the next region line or the end of the file: it is C#'s file-scoped namespace,
applied to segments. A region is structure, not a mode. It is found from lines alone, as a
brace is, so an edit to a region line affects only the lines up to the next one, and it may
be written only at file level: inside any block (`.scope`, `.if`, `.repeat`, `.each`, a proc)
it is an error, and there is no push or pop. Items that emit nothing, such as constants,
types, macros and imports, may stand anywhere, before the first region included. Anything
with an address outside every region and block is an error: there is no implicit `CODE`.

A segment **block**, `.segment NAME { }`, places what it holds and nothing after it. It may
appear anywhere an item may, as a one-off within a region or as a detour inside a proc.

**A segment's bytes are one run.** ca65 writes each segment's bytes in the order its input
writes them, whichever `.segment` line put them there, so nt65 lays a file out the same way:
per segment, with that segment's regions and blocks joined in the order the text writes them.
A routine at the end of one `CODE` region is followed by the first thing the next `CODE`
region writes, whatever `RODATA` regions stand between in the text, and a nested segment block
inside a proc is part of its own segment's run at its place in the text. Everything that reads
where bytes land reads this one layout: which routine a `.fallthrough` runs into (§7.4), branch
range and the form of a long branch (§7.6), and a distance between two data declarations (§8).
Only an `.align`, whose length depends on an address, and a `.place`, which puts another
module's bytes in between (§12), end a run of known distances. A translation unit of several
modules is laid out by the same rule (§12), so whether something is valid never depends on
whether something places its module.

A segment name is an identifier. ca65 quotes its segment names because they share a
namespace with symbols; nt65 declares segments in a table of their own, so a segment and a
symbol may share a name. The output still quotes them for ca65.

A segment's address size (`zp`, `abs`, `far`) is declared **exactly once** per program,
by a `.segment NAME: size` declaration item in any one file or in the project
configuration; a size after `:` is what makes the line a declaration rather than a region.
Regions and blocks only name the segment. The standard names are predeclared (`ZEROPAGE` as
`zp`, and `CODE`, `DATA`, `BSS` and `RODATA` as `abs`), and the predeclaration is what stands
when the program says nothing: a program may declare a standard segment once, as it declares
any other, to give it a direct page, a bank or mirrors, and at a size other than its own that
declaration is an error. A region or block that names a segment declared nowhere is an error,
so a misspelled name is caught before ld65 runs.
The address size is what nt65 uses to size references to symbols in that segment
(§7.2), so keeping it in one place means sizing depends on a small table rather than on
a fold over every file.

`far` needs the 65816. A far address is a bank and an offset, which no earlier processor
has, and ca65 rejects a far address size outright when its CPU setting is a 6502 or one of
its CMOS variants — for a segment declaration, an `.export` and an `.import` alike. A program built
for those processors that declares a far segment, or imports a far symbol, is an error
where it is written rather than output ca65 refuses (§3.2).

On the 65816 a segment declaration may also carry `dp = expr`, `bank = expr` and
`mirrors = [...]`, which §7.5 uses to check direct-page and data-bank assumptions. `dp` and
`bank` are constants, each given at most once; `dp` is for a `zp` segment, the only kind
reached through the direct page. `bank` is the segment's **home bank**, where it lives, and
`mirrors` lists the other banks the same memory is seen in, each a constant bank or a range
of them, and needs a `bank`:

```nt65
.segment LORAM: abs, bank = $7e, mirrors = [$00..$3f, $80..$bf]
```

On the other processors they are accepted and nothing reads them.

**Address spaces.** A 65xx program often carries another processor's code: the SNES sound
CPU's driver, a disk drive's half of a fast loader, a program for a second processor. Its
bytes are linked into the host's image and copied across at run time, so its segment loads
in the host's memory and runs in the other processor's. That other memory is an **address
space** of its own. The project declares each space beside the segments, with whether it
runs this program's processor, `"spaces": { "spc": "data", "drive": "code" }` (§5.3), and a
segment says which it is in with `space = name`, in the project file or in its declaration;
a segment that says none is in the host's space, which is every segment that does not. The
linker configuration is untouched: the segment still loads in ROM and runs at its own
address, which ld65 is told with `load` and `run`. An address has a space; a module does not,
and a module of constants and macros belongs to none, which is right for a macro pack both
sides use.

- **A space that holds data holds no instructions.** The program's processor stays one, so
  an instruction in a segment of a `data` space is an error, and what goes there is data
  declarations and macro calls, which is how another processor's instruction set is written
  (§11.2). A `code` space runs this program's processor, and its routines are checked as any
  are: the 1541 half of a C64 fast loader, or the second 65816 of an SA-1 cartridge.
- **A name in another space is a value.** A reference is checked by the space of the name's
  segment against the space of the segment of the code that writes it. Code may take a name
  from another space as an immediate and data may hold one, which is how the host tells the
  other processor where to start. A jump, a branch or a call to one is an error, and so is an
  operand that reaches memory through one: each would reach the same number in this
  processor's memory. An import says which segment it is in with `in`,
  `.import spc_entry: abs in SPCIMAGE`, and is checked as a name declared there (§12).
- **A segment's addresses are the linker's, and nt65 names them.** `.loadof(S)`, `.runof(S)`
  and `.spanof(S)` stand for the `__S_LOAD__`, `__S_RUN__` and `__S_SIZE__` that ld65 defines
  for a segment its configuration gives `define=yes`: where the image was loaded, where it
  runs and how many bytes it is. The output imports them, absolute as ld65 defines them, so a
  read of the image in another bank writes `f:`. `.runof(S)` is an address in `S`'s space and
  is checked as one; the other two are the host's. `.spanof` of a name that is a symbol
  measures the symbol (§7.6), and of a segment's name, the segment.

```nt65
.import spc_entry: abs in SPCIMAGE

.proc boot: a8, i16 {
    ldy #.runof(SPCIMAGE)
    ...
    lda f:.loadof(SPCIMAGE),x
    inx
    cpx #.spanof(SPCIMAGE)
    ...
    ldy #spc_entry
    ...
}
```

These declarations restate facts that live in the ld65 configuration, which nt65 does
not read or check. In particular a `zp` segment is emitted with `z:` operands, so ld65
must place it where its symbols are direct-page offsets (`$00`–`$FF`, relative to D);
`#<sym` and `.addr sym` on such a symbol then yield that offset, not an absolute
address, and on the 65816 an absolute operand on it is an error when its segment's `dp`
is not 0 (§7.2). Getting `dp =` and the linker config to agree is the programmer's job.

Inside a `.proc`, a segment block changes the segment of its contents, not their scope,
which is the structured form of the `.pushseg` / `.popseg` idiom:

```nt65
.proc draw: a8, i8 {
    ldx #0
@loop:
    lda table,x
    ...
    .segment RODATA {
        .data table: .byte 1, 2, 4, 8       ; this is draw::table
    }
}
```

A nested segment block is a separate flow region (§7.3): fall-through never enters it,
and the statement after the block follows the statement before it. If it contains
code, its first statement needs a `.next` edge or a declaration like any other entry
point, so it starts at a label: code at its start with no label is reached by nothing, and
is warned about as a label nothing reaches is. A nested segment block that names the segment it is already in is an error: its
contents would stay inline in the byte stream, where fall-through does reach them.

### 5.3 Project file

A project is described by `nt65.json` in the project root. `nt65 build` reads it;
`nt65 build main.nt65 --cpu 6502` works without one for single-file use.

```json
{
  "cpu": "65816",
  "files": ["src/**/*.nt65"],
  "out": "build",
  "defines": { "DEBUG": 1, "VERSION": "$0102" },
  "diagnostics": { "unused-symbol": "off", "mnemonic-name": "error" },
  "spaces": { "spc": "data" },
  "segments": {
    "SPCIMAGE": { "size": "abs", "space": "spc" },
    "ZP2":   { "size": "zp",  "dp": "$2100" },
    "WRAM":  { "size": "abs", "bank": "$7e" },
    "LORAM": { "size": "abs", "bank": "$7e", "mirrors": ["$00-$3f", "$80-$bf"] },
    "BANK1": { "size": "abs", "bank": 1 }
  },
  "ranges": {
    "$2100-$21ff": ["$00-$3f", "$80-$bf"],
    "$4200-$43ff": ["$00-$3f", "$80-$bf"]
  },
  "configurations": {
    "debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" },
    "pal":   { "defines": { "hw::PAL": 1 }, "out": "build/pal" }
  }
}
```

- `cpu`: `6502`, `6502x`, `65sc02`, `r65c02`, `65c02` or `65816`. A `.cpu` item in a file must agree.
- `files`: globs, from the project root. Order is not significant, and a glob may reach above
  the root, so a library shared between projects is part of each.
- `out`: where the output goes, the project root when there is none. A module's output is
  named after it, `.module gfx::sprite` in `out/gfx/sprite.s`, wherever its source is, so
  moving a source does not move its output. A module another places has none of its own: it
  is in the output of the module at the root of its translation unit (§12). nt65 records what it wrote in
  `out/.nt65-outputs`, and a later build deletes the output of a module that has gone from the
  program; it deletes nothing the record does not name.
- `defines`: the build configuration. Each define is a constant visible in every file,
  as if every module had brought it in, and defines are the only symbols an `.if` condition
  may test (§10). `-D NAME=value` on the command line adds a define or overrides one
  given here, and `-D NAME` on its own defines it as 1, for a define a condition only
  tests. A declaration in a file may not reuse a define's name. A name with a module's
  path, `"hw::SOUND_CHANNELS": 2` or `-D hw::SOUND_CHANNELS=2`, is no define: it sets the
  `.config` that module exports (§10), and setting one the module does not export, or one
  no module declares, is an error. The output always writes a define as its value,
  never by name, so a `-D` given to ca65 cannot collide with it.
- `diagnostics`: how much each diagnostic matters to this program, by name. The names are
  the catalogue's (§14), the same ones the command writes in brackets after a message, and the
  answers are `"off"`, `"warning"` and `"error"`. A diagnostic nt65 reports as an error is not
  a project's to turn down, and says so; a name nt65 has no entry for is an error, with the
  name it is nearly.
- `spaces`: the address spaces other than the host's (§5.2), each `"code"` when it runs this
  program's processor and `"data"` when it is another processor's memory. Only the project
  declares them: a program that links another processor's image has a project.
- `segments`: the segment table of §5.2 and §7.5. A segment declared here may not also
  be declared in a file, and a standard one keeps its size here too. Its `mirrors` are
  written as `ranges` writes banks, and its `space` names one of `spaces`.
- `ranges`: which banks an absolute *constant* address in each range may be accessed
  from (§7.5), for hardware registers that are mirrored in some banks only. A key is a
  range of addresses or a single address, each item a range of banks or a single bank,
  and no two keys may overlap.
- `configurations`: named builds of the program. Each gives `defines` over the project's,
  by name, `diagnostics` over the project's, by name, and an `out` in place of the project's,
  so that a release build can be stricter than the one being worked in; `--config name`
  chooses one, `-D` overrides on top of it, and with none chosen the project's own settings
  build. The editor's setting for the active configuration chooses the one a language server
  analyzes.
- `$schema`: accepted and ignored, so that a project file may name the schema an editor
  validates it against. Every other key nt65 does not know is an error.

Numbers are JSON numbers or strings in nt65 number syntax.

**The command line.** `nt65 build` finds `nt65.json` in the directory it runs in or the
nearest one above it, or takes `--project`. Every path it writes into output, and every
path in the dependency file, is from the project root, which is the directory a build
normally runs in; what it tells the person running it is from where they are.

| option | |
|---|---|
| `--project <file>` | the project file, or the directory that holds it |
| `--config <name>` | a named configuration |
| `--cpu <cpu>` | the processor, when the project does not say |
| `-D NAME[=value]` | a define, or a module's `.config` |
| `--out <dir>` | where output goes, in place of the configuration's `out` |
| `--depfile <file>` | make-style dependencies: each output depends on its source, the sources of the modules whose interfaces it uses and of those they use, the files that declare segments or settings, the `.incbin` files among them and `nt65.json`, and each of those has an empty rule so a deleted source does not stop make |
| `--c-header <file>` | a C header of what the program exports (§13) |
| `--check` | report and write nothing: no output, no header, no dependency file and no record of what was written |
| `--stdout` | write the named file's ca65 to standard output and no files. It takes one file, and answers for it whatever is wrong with the rest of the program: what could be written, under a first line saying that it is incomplete and why, which is what the editor shows beside the source (§14). For a module another places, it is that module's part of its translation unit's output (§12), from the comment that opens the part to the one that closes it |
| `--watch` | build again whenever the program changes, until interrupted |
| `--json` | one JSON object per diagnostic on standard output, for whatever is reading nt65 that is not an editor |
| `--help`, `--version` | |

A diagnostic is one line, `file:line:column: severity: message [name]`, on standard error.
The name is the catalogue's (§14), and goes last, where compilers put it: it is what a project
file switches and what CI matches on, and nobody reads it first. Where standard error is a
terminal and `NO_COLOR` is unset, `error:` and `warning:` are coloured and nothing else on the
line is, so the position stays selectable. `--json` puts them on standard output instead, one
object per line, with `file`, `line`, `column`, `endColumn`, `severity`, `id`, `message`, and
`related` where a diagnostic points at a second place, which the line form leaves to an editor.
What nt65 says about itself, the `nt65:` lines, stays on standard error either way, because it
is not about the program.

**Bringing a ca65 include over.** `nt65 import-inc <file.inc>` writes an nt65 module of
constants from a ca65 include file of them, to standard output or to the file `-o` names, with
`--module` naming the module. It is run once, by a person, and what it writes is the module's
own source from then on: a build reads no ca65, and a change to the include file is brought
over by running the command again. A `NAME = expr` or `NAME := expr` line whose expression nt65
reads becomes a constant and is exported, a comment is carried over, and every other line is
written out as a comment saying it was not converted and counted on standard error, so nothing
in the file is dropped where nobody sees it. What nt65 reads is nt65's own reader, so a ca65
operator nt65 does not have, and an expression whose order §9 asks to see in parentheses, are
both left for a person rather than written out as something that will not build.

**Explaining one.** `nt65 explain <name>` prints what the one line had no room for: what the
diagnostic is about, and the line a project file would write to switch it. Named nothing, it
lists every name there is; named something nt65 has no entry for, it says the name that one is
nearly. Given `--markdown` instead of a name, it writes every entry as one page, grouped under
the headings the catalogue is written in. The page is generated rather than kept by hand, so
no entry can be missing from it or say two things.

**Watching.** `nt65 build --watch` builds, then builds again whenever the program changes,
and says which directory it is watching after each one. What it waits for is what the last
build read — the project file, the sources, and the binaries an `.incbin` measured, which is
the set `--depfile` names — and any `.nt65` under the project root besides, since a file that
did not exist when the globs were matched is in no set worked out before it was written.
Nothing nt65 writes is either of those, so a build does not set off the next one. A command
line that is wrong comes straight back, because no file changing fixes it.

**Starting.** `nt65 init` writes an `nt65.json` and a `src/main.nt65` that builds, in the
directory it is given or the one it runs in, with `--cpu` choosing the processor. It refuses
to overwrite either, and writes neither when it would have to, so a directory that already
holds a program is left as it was.

**Formatting.** `nt65 fmt` writes the files it names in the one layout (§4), in place;
`--check` writes nothing, lists the files that are not in it already and exits 1, which is
what a gate runs. Named nothing, it formats every file the project's `files` name. It needs
no program: a file that belongs to no project, or that does not compile, is laid out from its
own lines and braces like any other.

**The editor's server.** `nt65 lsp` serves the language server (§14) on standard input and
output and takes nothing else, so that any editor speaking LSP starts it from the same command
every other use of nt65 already installs. What it says about itself goes to standard error,
because standard output carries the protocol.

A file named on the command line is built as part of its project, so a name another module
exports means what it means there, and only its output is written. Without a project the
named files are the program, and a name whose module is not among them says so. A program
that says nothing about its processor is built for the 6502, and `nt65 build` notes it.

An output whose text is unchanged is not rewritten, so a build tool sees it as unchanged; one
that is older than something it depends on is touched instead, so that make does not run nt65
for it again. One nt65 run writes every output that changed, and the others are up to date
when make reaches them, so a Makefile names each output as a target of the same rule, with no
marker file.

## 6. Symbols and scopes

### 6.1 Declarations

| form | declares |
|---|---|
| `name:` | a position in code, inside a proc: an address and nothing else. It may be followed by an instruction, data directive or macro call on the same line, and has no size whatever follows it. |
| `NAME = expr` | a constant if `expr` contains no address symbols, otherwise an **address alias**, sized, exported and imported like a label. Single assignment; forward references allowed; cycles are errors. A constant may hold text, and is then usable wherever a string literal is (§8). |
| `.config NAME = expr` | a **setting**: a constant a condition may test, whose value the build may set (§10). |
| `@name:`, `@name = expr` | a cheap local: a label or constant private to its proc or scope, or a position private to a `.data` block (§6.2). |
| `.proc name [: signature] { ... }` | a label **and** a scope, with a processor-state signature (§7.3). At file level or in a `.scope` outside any proc: procs do not nest. |
| `.multiproc E, b [: signature] { ... }` | a **family**: one routine per member of the named enum `E`, each named after its member, in the scope around the line. It stands where `.proc` stands (§10). |
| `.proc b [: signature] { ... }`, `.data b: element`, in an `.each E, b` body | the same family written out: a declaration in a repetition's body whose name is the name it binds declares one per member (§10). |
| `.proc name = expr [: signature]` | an **extern proc**: a routine with a signature and no body, at a constant address (a ROM or toolbox entry, §12) or naming another routine, which is how a routine is aliased. An alias that writes a signature must write the routine's, and one that writes none takes it. |
| `.scope [name] { ... }` | a scope. It is a namespace with no address of its own: its name is not an operand. |
| `.enum [name] { ... }` | constants (§6.3). |
| `.struct name { ... }`, `.union name { ... }` | member offsets and a size (§6.3). |
| `.data name: element`, `.data name { ... }` | data: an address with a size in bytes, a count of elements for an element type, and a scope of its members or of its type's fields (§8). |
| `.charmap name { ... }` | a text encoding (§8). |
| `.list name { ... }` | a named sequence of expressions (§6.4). |
| `.func name(...) = expr` | a pure expression function (§9). |
| `.signature name = items` | a **signature set**: signature items a routine names instead of writing them out (§7.3). |
| `.macro name(...) { ... }` | a macro (§11). |
| `.module path` | the module the file is, once, before its other items (§12). |
| `.use path`, `.use path::{a, b}`, `.use path::*`, `.use path as name` | names another module declares, or a module, brought in under their own names or `as` ones (§12). |

Every symbol carries what analysis needs: whether it is a constant or an address, its
address size (from its value, or from its segment), and for data its size in bytes
(`.sizeof(name)`) and its count of elements (`.countof(name)`).

### 6.2 Scoping rules

- Name lookup proceeds from the innermost scope outward to the module's top level, then to
  what its `.use` items bring in and to the defines, then to the modules themselves
  (§12). `a::b` walks into a named scope, or into a module; `::hw::name` starts at the
  root of the modules.
- **Cheap locals** `@name` are labels or constants private to the innermost enclosing
  `.proc` or `.scope`. A macro expansion and each `.repeat` or `.each` iteration also have their
  own, and so does a `.data name { }` block, whose `@` positions are private to it. `.if`
  bodies and segment blocks do not start a new set, so an `@loop` in a nested
  `.segment RODATA { }` block is visible to the proc's code.
  - A reference looks outward through enclosing scopes, so a nested `.scope` can branch
    to its proc's `@done`. Procs do not nest (§6.1), so this never reaches into another
    routine.
  - A cheap local cannot be reached with `::` or exported, and has its own namespace,
    so `@loop` never collides with a regular `loop`.
  - A name may be declared once in its scope. To reuse one, open an anonymous
    `.scope { }`, which is inline code, not a separate routine.
  - A cheap local outside any proc, scope, `.data` block, macro body, `.repeat` body or
  `.each` body is an error.

  Because nothing outside a proc can name the cheap locals declared inside it, every
  edge into such a label is visible to the proc's flow analysis (§7.3). The output gives
  each cheap local a generated name (§13).
- Nothing another module declares is visible without its path or a `.use`, and two
  modules may each export the same name: the linker sees each under its module's (§12).

```nt65
.proc init: a8, i8 {
    .scope {                ; clear RAM
        ldx #0
    @loop:
        sta $0200,x
        inx
        bne @loop
    }
    .scope {                ; clear the palette
        ldx #31
    @loop:
        stz palette,x
        dex
        bpl @loop
    }
    rts
}
```

### 6.3 Enumerations, structures and unions

These are the declarative part of ca65's type vocabulary, kept with brace syntax. They
declare constants and sizes and never generate code.

```nt65
.enum Color {
    red                 ; 0
    green = 5
    blue                ; 6
}

.struct Point {
    x: .word
    y: .word
}

.struct Player {
    pos:    .type Point
    hp:     .byte
    name:   .res 16, ' '
    colors: .word[4]
}

.union Value {
    b: .byte
    w: .word
}
```

**Enumerations.** Members are constants in the scope `Color`, referenced as
`Color::red`. A member without a value is the previous member plus one, starting at 0;
explicit values must be constant. An anonymous `.enum { }` declares its members into
the enclosing scope. A member may stand under an `.if` in the body (§10), so that which
members an enum has follows the configuration where the members are listed rather than by
writing out two whole enums under exclusive conditions; a member after one, with no value
of its own, is still the member before it plus one.

```nt65
.enum Cmd {
    move                ; 0
    fire                ; 1
    .if DEBUG {
        dump            ; 2
    }
    wait                ; 3 with DEBUG, 2 without
}
```

**Structures.** Members use the element types of data declarations (§8), but reserve
space instead of emitting it: `x: .byte` reserves one element, `colors: .word[16]` sixteen,
`pos: .type Point` one record and `pos: .type Point[4]` four. A member holds no value, so
`colors: .word 16` is an error, and a member's count is a number. A string member declares
its pad, `title: .res 21, ' '`: an initializer's text is padded with it, data of the type with
no values is filled with it, and `.res n` alone pads with zero. Each member is a constant
offset in the scope `Point` (`Point::x` is 0, `Point::y` is 2) with a byte size
(`.sizeof(Point::y)` is 2) and a count (`.countof(Player::colors)` is 4), and
`.sizeof(Point)` is the total. Members of a record member are reachable through it:
`Player::pos::y` is 2. An anonymous `.struct { }` inside a struct groups members without
introducing a scope. A **union** is a struct in which every member is at offset 0 and the
size is that of the largest member. Member names may be register names or mnemonics (§4).

**Data of a type.** `.data name: .type T` declares one record and `.type T[n]` an array of
n (§8):

```nt65
.data player: .type Player
.data actors: .type Player[MAX_ACTORS]
```

Data of a type is also a scope whose members are the fields of its first element, each an
address symbol in the declaration's segment with the member's size: `player::hp` is
`player + Player::hp`, and `player::pos::y` is `player + 2`. For an array,
`.countof(actors)` is `MAX_ACTORS`, `.sizeof(actors)` is `MAX_ACTORS * .sizeof(Player)`,
and `.sizeof(Player)` is the stride, so with X holding an element's offset
`lda actors::hp,x` reads that element's `hp`. An element the program does not have to work
out is written `actors[1]::hp` (§8), which is the same address arrived at before it runs.
Nothing else changes: these are ordinary
indexed operands that nt65 sizes from the segment of the declaration. `.type` is dotted
like the built-in element types, because a bare type name inside a proc would read like an
instruction.

**Initialized records.** `.type T { ... }` emits one record with values, which is what
record macros are written for in ca65, and `.type T[] { ... }` an array of them:

```nt65
.struct Actor {
    x:  .word
    y:  .word
    hp: .byte
    ai: .addr
}

.data boss: .type Actor { x = 100, y = 40, hp = 99, ai = chase }

.data hero: .type Actor {
    x = 16
    hp = 3
    ai = player_input
}

.data wave: .type Actor[] {
    { x = 32, ai = chase }
    { x = 64, ai = chase }
}
```

Each value names its member, so the struct decides the layout and reordering its members
cannot misplace a value. A member is named at most once, and a member not named is
zero. A value must fit its member as it would fit the matching data directive, and a
member of one element takes one value, so text longer than a byte is not one; a `.res n`
member takes a string of at most n bytes, padded with its pad; a record member takes a
nested one-line initializer, `pos = { x = 1, y = 2 }`; an array member always takes a
braced list of exactly its count, `colors = { $7fff, $001f, 0, 0 }`, in both forms; and a
union takes at most one member. The one-line form balances its braces on its line, and the
multi-line form is a block with one `member = value` per line. In an array of records, each
element is a braced initializer. The declaration is data like any other: `boss::hp` and
`.sizeof(boss)` need nothing more.

The output does not use ca65's `.enum`, `.struct`, `.union` or `.tag` (§13). Enum
members are emitted as constants, member offsets and type sizes as numbers with a
comment naming the path, a record with no values as `.res` of its size, and an
initialized record as one data directive per member.

### 6.4 Lists

A list is a named sequence of expressions, declared once and used wherever the same
items would otherwise be written out more than once, such as the low and high bytes of a
split pointer table:

```nt65
.list handlers {
    cmd_move
    cmd_fire
    cmd_quit
}

.segment RODATA
.data lo: .lobytes handlers
.data hi: .hibytes handlers
```

A line holds one or more comma-separated items, constants or addresses, whose names
resolve where the list is declared. A list name stands for its items in the operands of
`.byte`, `.word`, `.dword`, `.addr`, `.faraddr`, `.lobytes` and `.hibytes`, in `.each`
(§10), and as a `.next` target (§7.4). `.countof(handlers)` is the number of items, a
constant. A list emits nothing by itself, and is exported and used across modules like a
constant, by value.

## 7. Instructions

### 7.1 Operands

Standard forms, as in ca65:

```nt65
    inx                 ; implied
    asl a               ; accumulator (bare "asl" also accepted)
    lda #$10            ; immediate
    lda ptr             ; zero page / direct, or absolute — see 7.2
    lda d:$2105         ; a constant address through the direct page — see 7.5
    lda buf,x           ; indexed
    lda (ptr),y         ; indirect indexed
    lda (ptr,x)         ; indexed indirect
    bne @loop           ; relative
    jeq @far            ; long branch — see 7.6
    brk #0              ; signature byte required, see below
    jmp (vector)        ; indirect
```

An operand that begins with `(` is indirect only when the parentheses hold the whole
operand: `lda (ptr)` and `lda (ptr),y` are indirect, and `lda (hi + lo) * 2` is an
ordinary expression that happens to start with one, as it is in ca65.

The CMOS 6502s add `(zp)` and `(abs,x)`, and the R65C02 and WDC 65C02 the `bbr`/`bbs`/`rmb`/`smb` forms, of which `bbr` and
`bbs` take two operands, a zero-page address and a branch target: `bbr0 flags, @skip`.
65816 adds
`[dp]`, `[dp],y`, `sr,s`, `(sr,s),y`, `[abs]`, long forms, and `mvn #src, #dst` /
`mvp #src, #dst`, whose operands are bank bytes and are written as immediates (ca65's
bare form takes full addresses and silently keeps only their bank bytes).

`brk #sig` and `cop #sig` take a mandatory signature byte on every CPU. The processor
returns from the handler to the instruction two bytes after `brk`, so the one-byte
`brk` that ca65 accepts would make the next instruction's first byte the signature;
nt65 sizes both as two bytes.

`wdm #n`, on the 65816, is two bytes as well. The processor treats it as a no-op, and
emulators and debuggers use it as a hook.

### 7.2 Address size

The size of a data operand is determined without regard to where the reference
appears:

1. An explicit prefix `z:`, `a:` or `f:` wins; `d:` makes a direct operand from a
   constant address (§7.5).
2. If the expression is a constant, its value decides: below `$100` is zero page, below
   `$10000` absolute, otherwise far. The difference of two places in one data declaration
   is a constant (§8), though it names addresses.
3. Otherwise the expression contains address symbols, and its size is the widest
   address size among them, each taken from its segment (§5.2) or `.import` (§12).

nt65 then picks the narrowest addressing mode the instruction offers that is at least
that wide (`jmp zp_label` becomes absolute because `jmp` has no zero-page form) and
writes the choice into the output with an explicit prefix.

An instruction with only one form of an operand, such as `sta buf,y` on the 6502,
`jmp (vec)` or `pei (dp)`, leaves ca65 nothing to choose, so the output writes no prefix
there. nt65 checks instead that the operand is no wider than that one form reaches: an
absolute pointer in `lda (ptr),y` is an error, where ca65 would pass it to the linker.

On the 65816 a direct-page symbol keeps its meaning only as a direct operand: D plus its
offset. An absolute or long operand built on a symbol whose segment declares a `dp` other
than 0 is an error, whether the width comes from an `a:` or `f:` prefix, from an
instruction with no direct-page form (`lda ptr,y`, `jmp ptr`), or from an expression
that mixes it with a wider symbol, because such an operand reaches the offset in bank B
instead.

Control transfers are not sized by prefix. `jsr`, `jmp` and the branches take a near
target, `jsl` and `jml` a far one, and a mismatch is an error naming the other mnemonic
(§7.3). A far target is a routine declared `far` or a symbol in a `far` segment or
import. A routine is near or far by its signature alone, wherever its segment is; a
constant target is taken as written, so `jml $008000` is a long jump into bank 0. Only a
routine or a label is somewhere flow analysis can follow, so a constant target needs a
`.next` saying where it goes (§7.4).

### 7.3 65816 processor state

Register widths, the emulation flag and (§7.5) the direct page and data bank are the
sequential state nt65 keeps, because 65816 code cannot be written without them. nt65
tracks them with a **flow analysis confined to one `.proc`**. The analysis is small
because the language guarantees three things: every callable routine declares its
state at entry and exit, control flow stays inside the proc or goes to another
routine's entry, and every construct the analyzer cannot follow is recognized
syntactically and must be annotated (§7.4). The analysis, and every check in §7.3–§7.5
that depends on it, runs only on the 65816.

```nt65
.proc render: a16, i8 -> a8, i8 {
    lda #$1234          ; 16-bit immediate
    sep #$20            ; A is now 8-bit
    lda #$12
    rts                 ; checked: state here must be a8, i8
}
```

**Signatures.** Every routine carries one: a `.proc`, an extern proc, a `proc(...)` import and
each instance of a family (§10), whose signature is the family's read with the member that
instance is named after, so `dbr = Bank::b` is that instance's bank. A routine declares its
state at entry and, after `->`, at exit. Exit
defaults to entry, item by item: `a16, i8 -> a8` returns with `i8`. `emu` makes both widths
8, as it does in `.state`, and an exit that names a 16-bit width without naming the mode is
in native mode, the only one that width holds in: `emu -> a16, i16` returns native. The
items are:

| item | meaning | default |
|---|---|---|
| `a8` `a16` `a?` `a*` | accumulator width | `a*` |
| `i8` `i16` `i?` `i*` | index width | `i*` |
| `native` `emu` `e?` `e*` | emulation flag | `native` |
| `near` `far` | entered by `jsr`/`jmp` and left by `rts`, or by `jsl`/`jml` and `rtl` | `near` |
| `inline n`, `inline .strz` | the routine returns past data written after each call: n bytes, or one `.strz` (§7.4) | none |
| `args n` | the caller pushes n bytes before the call (below) | none |
| `interrupt` | an interrupt handler (below) | none |
| `noreturn` | the routine never returns (below) | none |
| `keeps a, x, y, c` | the registers it hands back as it was entered with them (§7.7) | none |
| `dp = e` `dp?` `dp*`, `dbr = e` `dbr?` `dbr*` | direct page and data bank (§7.5) | `dp*`, `dbr*` |
| `?` | every part above unknown (below) | none |
| a signature set's name | the items the set declares (below) | none |

**The widths default to `*`.** A proc that writes no width assumes nothing about one: it is
callable whatever the caller's widths are, and must hand them back as it found them. That is
what a routine which never touches a width-dependent immediate — a wait loop, zero-page
bookkeeping, a poll of a hardware register — actually promises, and it is now what such a
routine says by writing nothing. A body that does depend on a width gets `A's width is not
known here` and says which it means. The old default of `a8, i8` was a known value, so it
answered a question the author had not asked: `.proc f { lda #$12 }`, meant as 16-bit, assembled
silently as an 8-bit immediate, which is the ca65 failure §2 exists to remove. This is also why
a macro's items default to `*` (§11.5); procs now agree with them.

`?` means unknown, for entry points reached from outside nt65. Written on its own, as an item,
`?` is every one of those parts unknown at once — `a?, i?, e?, dp?, dbr?` — which is what a
routine reached from outside nt65 assumes and what an extern proc or an imported routine
usually declares: `.proc CHROUT = $FFD2: ?, far`. An item after it takes the place of what it
gives for that part, as a signature set's items are taken over, so `?, a8` is 8-bit A and
nothing else known.
`*` means unchanged: the routine assumes nothing about that part of the state and
returns it as it found it, so a caller keeps what it knew across the call. In the body a
`*` value counts as unknown wherever a known one is needed, and at every `rts` or `rtl`
it must still hold the entry value: nothing changed it, or a pull restored it from the
analysis stack (below). In an exit list, `*` is allowed only for an item that is `*` at
entry.

**Signature sets.** Most routines of a program run in the same state, so a signature may
name a set of items declared once, and write only what differs from it:

```nt65
.signature std = a8, i16, dp = 0, dbr = $80

.proc main: std {
    rts
}

.proc step: std, a16 -> std {
    sep #$20
    rts
}
```

A set's name comes first in its list, and the items after it take the place of the set's own
for the same part. A set may start from another set, but not reach itself. It is declared
where a constant may be, is exported and brought in with `.use` like one, and writes nothing.
After `->`, and in a macro's signature, a set gives only its state: `near`, `far`, `inline`,
`args`, `interrupt` and `noreturn` describe how a routine is called, entered or left, and are
left out there. There are no defaults per file or per segment: the built-in defaults above stay
what a signature that says nothing means.

**Routines that never return.** `noreturn` says a routine never returns: a reset handler, a
main loop, a routine that jumps away for good. It is written with `near`, `far` and
`interrupt`, before the arrow, because it describes how a routine relates to its caller rather
than what its state becomes, and a routine that never returns declares nothing after `->`. An
`rts` or `rtl` in it is an error, a call to it ends the path, so nothing after the call needs
`.next ?` and a proc that ends with one does not run off its end, and a jump from it checks
only the target's entry. The last three hold on every CPU. An interrupt handler leaves by
`rti` and never returns to a caller in any case, so `interrupt` does not take it.

**Interrupt handlers.** `interrupt` says the processor enters the routine from anywhere:
the widths, D and B are unknown at entry, and so is the mode unless `native` or `emu` is
written with it, which is all it may be written with. It leaves by `rti`, so it says nothing
after `->`, and an `rts` or `rtl` in it is an error. It is neither near nor far: a call to it,
`jsr`, `jsl` or a relative call, is an error, while its address in data, a vector, is what it
is for. A jump from it checks only the target's entry, and a jump to it is allowed only from
another interrupt handler or a routine that never returns. On the 6502 and its CMOS variants it is
accepted with the same `rti` and call checks.

**Arguments.** `args n` says the caller pushes n bytes before the call. Inside the routine
the analysis stack starts with those bytes and the return address above them, two bytes near
and three far, so a `.frame` can lay out both (below). At a call, where what the caller has
pushed is known, it must be at least n bytes. The call leaves the stack as it found it: the
caller removes the arguments.

Three kinds of routine carry a signature: a proc with a body, an extern proc
(`.proc CHROUT = $FFD2: a8, i8`, §6.1) and an imported routine
(`.import _printf: proc(a8, i16)`, §12). On the 65816 anything called must be one of
these.

**A routine with no body declares its state.** On the 65816 an extern proc at a constant
address and an imported routine must write at least one item, because the declaration is all
there is: no body will ever contradict it, so a default there is a guess nothing checks. `?` is
what to write where nothing is known, which is the usual answer for a ROM or toolbox entry:
`.proc CHROUT = $FFD2: ?, near`. An extern proc that names another routine and writes nothing
takes that routine's signature, which is a declaration too. A proc with a body is not held to
this: its body is checked against whatever it declares, defaults included.

**Transfer functions.** Every instruction has a fixed effect on the state:

| instruction | effect |
|---|---|
| `rep #const`, `sep #const`, in native mode | the named widths become known |
| `rep`, `sep`, in emulation mode | no change; widths are pinned at 8 |
| `sep #const` with E unknown | the named widths become 8, which they are in either mode |
| `rep #const` with E unknown | the named widths become unknown |
| `rep`, `sep` with a non-constant operand | both widths unknown |
| `.ensure a16, i8` | the named widths become known (below) |
| `clc` immediately before `xce`, in the same basic block | from emulation: native, both widths 8; from native: no change; with E unknown: native, both widths unknown |
| `sec` immediately before `xce`, in the same basic block | emulation mode, both widths 8 |
| any other `xce` | E unknown, both widths unknown |
| `php`, and every other push or pull | moves the analysis stack (below) |
| `plp` that pulls a P saved by `php` | the widths saved at the `php` |
| any other `plp` | both widths unknown; E unchanged |
| `jsr f`, `jsl f` | state must match f's entry; becomes f's exit, except that items f declares `*` keep their value. A call to a routine that says `noreturn` ends the path |
| `per L-1` directly followed by `brl f` or `bra f` to a routine, where `L` labels the statement after the branch | a relative call, as `jsr f`; with `phk` directly before the `per`, as `jsl f` |
| `jmp f`, `jml f`, a branch or `.next` edge to f, or a `.fallthrough f`, where f is a routine (a tail call) | state must match f's entry; f's exit, with its `*` items taken from the state here, must match this proc's exit; f must be `near` or `far` as this proc is. Where this proc never returns or is an interrupt handler, or f never returns, only f's entry is checked. An unconditional transfer ends the path |
| `jsr (t,x)` with `.next` naming routines | state must match every entry; becomes the merge of their exits |
| `rts`, `rtl` | state must match the proc's exit; path ends. An error in a proc that says `noreturn` or `interrupt` |
| `rti`, `stp` | path ends, nothing checked |
| `brk #s`, `cop #s`, `wdm #n`, `wai` | no change |
| indirect jumps | path ends; targets come from `.next` (§7.4) |
| anything else | no change |

**Merging.** The state at a label is the merge of every incoming edge (fall-through,
branches, jumps, `.next`). Where edges disagree, or any edge is unknown, the merged
value is unknown, and nothing is reported at the label. Errors arise where an unknown
value is *used*, which is what matters: two paths converging on a `rep #$30` before
`rtl` are fine, because in native mode a constant `rep`/`sep` makes the named widths
known whatever they were before.

**The analysis stack.** The state also records what the proc has pushed, one entry per
byte: part of a saved P, D or B, or a byte of unknown content. Every push and pull
moves it by its size (`pha` by A's width, `phx` and `phy` by the index width, `pea`,
`pei` and `per` by two), and a call leaves it as it was, since a routine returns with
the stack as it found it. A pull that finds the matching saved value restores it: `plp`
a P, `pld` a D and `plb` a B (§7.5). So `php` … `jsr` … `plp`, and a save and restore on
either side of a label, need no annotation.

The analysis stack is **a known top over a base**. At entry the base is where the routine
was entered. After `txs` or `tcs`, and after a pull of more than is known, the base is
unknown, but what is pushed from there on is still tracked on top of it, and a pull still
finds it: `txs` then `phk`, `plb` sets B, and `tcs` then `pea c`, `pld` sets D. A pull past
the known top gets a value nothing is known of. The whole stack becomes unknown after a push
or pull whose size is unknown, and at a merge where the incoming stacks differ in depth.

**Assertions.** `.state` takes the same items as a signature, except those that describe a
routine rather than a point in it: `near`, `far`, `inline`, `args`, `interrupt`, `noreturn`,
the `*` items, and a signature set, which stands for items of its own. `keeps` is the one item
that is about a routine and is written at a point too, because a routine's promise is its
point fact asked at every way out of it (§7.7):
`.state a16, i8, dbr = $7e`. Each item asserts and sets: if that part of the state is
known and differs, error; if it is unknown, this sets it. An item with `?` (`a?`, `e?`,
`dp?`) deliberately forgets. `emu` also makes both widths 8, which is what emulation mode
pins them at. Placed directly after a label, a `.state` is that label's declaration.

Where such a label can also be entered from outside its routine — it is exported, or a path
from another routine names it — the declaration is everything the label assumes: a part it does
not give is unknown there, whatever the routine's own paths leave, except a part the routine's
signature says `*`, which stays unchanged there as it does at a label nothing reaches. The two
sides meet in the middle, then: a jump in is checked for the parts the declaration gives, and
the code after the label assumes no more than those.

The analysis stack at such a label is **what entering the routine leaves**: nothing, or, for a
routine that takes `args n`, the arguments and the return address above them. Whoever jumps in
is taken to have arrived as a call would, which is the word a tail call to a routine is taken
at too, and it is the only thing either side can be held to: a `.state` says what the processor
state at the label is and has no way to say what is on the stack, so there is nothing for the
two of them to meet in the middle over. Where the path above the label has pushed something a
jump in has not, they disagree, and the stack after the label is one nothing is known of: a
`pla` there matches no `pha`, a `plp` finds no saved P, a `.frame` counts from a stack pointer
nobody knows, and `keeps` cannot be shown. So a save and its restore belong on one side of such
a label, and a second entry point that reads what its caller pushed says so with `args n`,
which is on the stack there exactly as it is at the routine's own entry. Nothing carries a push
across an entry point, a routine a `.fallthrough` runs into (§7.4) included: each of those is
entered with the stack of a call to it, which is what makes each of them callable.

**Setting widths.** `.ensure` takes width items, `a8`, `a16`, `i8` and `i16`, and makes
them hold, emitting only what the analysis says is needed: nothing where the widths
already hold, otherwise the `sep` or `rep` that sets them. Its effect on the state does
not depend on what it emits, so the choice is made once the analysis has converged and
the analysis never waits on its own output. A 16-bit width needs native mode known at
that point; in emulation mode the widths stay 8. An `.ensure` no path reaches writes
everything it names. `.ensure` is the checked form of the macro-time width stacks of ca65
code, such as libSFX's `RW` family, which follow the text of a file rather than its
control flow.

**Stack frames.** `.frame name: T` names the top `.sizeof(T)` bytes of the analysis stack
as a frame laid out as the struct `T`, usually directly after the instructions that make
room for it. If the analysis stack is unknown there, it becomes those bytes with nothing
known beneath them; over an unknown base, as after `tcs`, the frame may reach beneath what is
known. In a stack-relative operand, `name::member,s`
and `(name::member,s),y` are that member's offset from the current stack pointer,
computed from the pushes and pulls since the `.frame`, so a push between two reads cannot
silently shift them:

```nt65
.struct Locals {
    count: .word
    src:   .addr
}

.proc copy: a16, i16 {
    pea 0                   ; src
    pea 0                   ; count
    .frame locals: Locals
    lda #8
    sta locals::count,s     ; 1,s
    pha
    lda locals::src,s       ; 5,s: the pha is counted
    ...
    pla
    pla
    pla
    rts
}
```

A slot is an error where the stack depth is unknown, once the analysis stack no longer
contains the frame, and anywhere other than a stack-relative operand. A `.frame` larger than
what the proc has pushed since it was entered is an error. A frame ends with its proc.

A routine that takes `args n` starts with its arguments and return address on the
analysis stack, so a frame reaches them:

```nt65
.struct DivFrame {
    remainder: .word
    ret:       .res 2
    divisor:   .word
    dividend:  .word
}

.proc div16: a16, i16, args 4 {
    pea 0
    .frame f: DivFrame          ; the local, the return address, the caller's two words
    lda f::dividend,s
    plx
    rts
}
```

**Checks.** After the analysis converges:

- every immediate operand of `lda adc and bit cmp eor ora sbc` has a known A width and
  every immediate of `ldx ldy cpx cpy` a known index width, and no 16-bit immediate is
  emitted in emulation mode;
- every `rts`/`rtl` reaches the declared exit state and matches the proc's
  `near`/`far` attribute;
- every `.ensure` of a 16-bit width is reached in native mode, and every frame slot where
  the stack depth is known;
- every `jsr`/`jmp` targets a `near` routine and every `jsl`/`jml` a `far` one;
- every tail call matches the target's entry, and the target's exit and `near`/`far`
  match this proc's, because the target returns to this proc's caller, unless nothing
  returns: this proc never does or is an interrupt handler, or the target never returns;
- every call to a routine that takes `args n` has n bytes pushed, where that is known;
- no interrupt handler is called, and no routine that says `noreturn` or `interrupt` returns
  with `rts` or `rtl`, on every CPU;
- every call targets a routine with a signature (proc, extern proc or `proc(...)`
  import). A local subroutine is a separate proc, grouped with its callers in a
  `.scope` when a shared namespace helps; procs do not nest (§6.1). A routine with
  several entry points is written as adjacent procs, each ending in a `.fallthrough` into the
  one written after it (§7.4). On the
  6502 and its CMOS variants, where there is no state to contract, a call may target any address
  expression.

Outside any `.proc` there is no processor state: on the 65816 `rep`, `sep`, `xce`,
`plp`, `.state`, `.ensure`, `.frame` and any width-dependent immediate are errors, since code that touches processor
state belongs in a proc, and the checks of §7.5 do not apply.

**Code shared between CPUs.** A library reached by a glob above the project root is part of
each program that takes it (§5.3), compiled under that program's `.cpu`, so one source has to
mean something on both. On the 6502 and its CMOS variants the analysis does not run, and a
signature's items divide in two:

- `a16` and `i16` are an **error**: no code on those CPUs ever runs with a 16-bit register, so
  the item is false rather than merely idle. The same holds wherever a width is written — a
  signature, a set, a macro's signature, a `.state`, an `.ensure`.
- `native`, `emu`, `dp = e` and `dbr = e` are **accepted and inert**. They are about registers
  the CPU does not have rather than false of the ones it does, so a shared module may carry
  `dbr = $80` for the 65816's benefit and still build for a 6502. `near` and `far` need no rule
  of their own: `jsl` and `rtl` do not assemble there, so a far routine gives itself away.

`.state` and `.ensure` are likewise accepted, their names checked, and neither emits anything
nor checks anything, as the annotations of §7.4 are. That is what makes one routine callable
from either CPU:

```nt65
.proc putc: ?, near {
    .ensure a8, i8          ; `sep` on the 65816; nothing on the 6502
    sta $d000
    rts
}
```

`sep` means the same in either mode, so an `.ensure` of 8-bit widths holds even where the
emulation flag is unknown, and a caller in emulation mode is served too.

A signature cannot be varied by CPU in place, because it is part of the `.proc` line and `.if`
is a line-level block. The **signature set** is what varies instead, which is better style in
any case and the only form that scales past one routine:

```nt65
.if .target(65816) {
    .signature shared = a16, i16, dbr = $7e
} .else {
    .signature shared = a8, i8
}

.proc memcpy: shared {
    ...
}
```

Which declarations exist follows from the configuration alone (§10), so two declarations of one
name in exclusive branches are one declaration, not a clash. A module shared between CPUs should
name a set or write its widths, rather than lean on the defaults above, which are only correct
for it by coincidence.

**Implementation.** Split the body into basic blocks at labels and after transfers of
control; run a worklist over a lattice of `{unreached, known value, unknown}` per item.
Each item can change at most twice, so a block is walked at most once more than there are
items; in practice a routine without loops settles in one walk per block and a loop in
two. It is not "at most two passes per block": where a merge forgets one item, such as the
analysis stack, a later round can forget another because of it (a `plp` that no longer
finds its saved P), and the loop head learns that only on a third walk.

**Widths in the output.** ca65 uses its width setting only to size the immediate of a
width-dependent instruction (`lda adc and bit cmp eor ora sbc` for A, `ldx ldy cpx cpy`
for the index registers), and that setting is simply the last `.a8`/`.a16`/`.i8`/`.i16`
in the text: switching segments, `.pushseg`/`.popseg` and `.setcpu` do not reset it.
The output therefore does not try to follow control flow. Two rules place every width
directive:

1. A width directive appears only directly before a width-dependent immediate: never
   after `rep` or `sep`, at the start or end of a proc, or after a `.state`.
2. It is left out when the previous immediate for the same register, in output order,
   had the same width. The first such immediate in a file always gets one.

Rule 1 means ca65's setting before an immediate is exactly the width of the previous
immediate for that register, so rule 2 is exact. The size of every immediate comes from
the analysis, which errors wherever the width is unknown, so a label reached by a jump
from further down the file is sized by the width that arrives there, not by the code
above it:

```text
.proc fill: a16 {               fill:
    sep #$20                        sep #$20
    bra @b                          bra fill__b
@a:                             fill__a:
    lda #$1234                  .a16
    rts                             lda #$1234
@b:                                 rts
    rep #$20                    fill__b:
    bra @a                          rep #$20
}                                   bra fill__a
```

On the 6502 and its CMOS variants the output contains no width directives. nt65's own tests
assemble its output with `ca65 -l` and compare each line's length with the length nt65
computed (§7.6), which catches any disagreement about widths or addressing modes.

### 7.4 Unchecked constructs

Assembly's tricks (computed jumps, jumps to places that are not instruction boundaries,
data in the instruction stream, self-modifying code) are all allowed. Each has a
syntactic fingerprint, so nt65 finds it in one pass and requires an annotation that
tells the analysis what it cannot see. The annotations are claims: nt65 checks that the
claims and the code are consistent with each other, which is the same contract as the
proc's own entry declaration. On the 6502 and its CMOS variants nothing consumes processor
state, so the annotations are accepted and their names checked, but none is required; the
unreachable-label warning applies on every CPU, and so does falling off the end of a proc,
which is a warning there and an error on the 65816.

Two annotations carry most of it. Each applies to the statement immediately above it (for
a macro call, the last statement of its expansion, §11.3) and comes before any following
label:

- `.next @a, @b` names where flow goes after a statement whose successors nt65 cannot read
  for itself: an indirect jump or call, an `rts` or `rtl` used as a jump, a jump to a
  computed address, data flow runs into, the last statement of a nested segment block, and a
  macro call whose expansion ends in one of those. Each named target gets an edge carrying
  the current state; on a call, the targets are routines and the state after the call is the
  merge of their exits. Anywhere else a routine named is a jump to its first byte, checked
  like a tail call: it says where flow goes, and never that the routine is the one written
  next. Targets may be cheap locals, scoped paths (`gfx::init`) and, inside a macro body,
  `ident` parameters. A target naming a list (§6.4), or data declared as addresses
  (`.addr` or `.faraddr`, or such a member of a `.data` block) whose items are all code
  labels, each optionally minus 1 as in an RTS dispatch table, stands for every one of those
  labels, whether the items are written on the declaration's line or in its body. A target
  naming data of any other type is an error that asks for the address type. `.next ?` ends
  the path with nothing checked, and may stand after any statement. A `.next` that names
  targets after a statement whose successors nt65 already knows — an ordinary instruction, a
  direct `jsr` or `jmp` to a label or routine — is an error, since it could only contradict
  them; where it is the last line of a routine's body its message says `.fallthrough` is what
  was meant, and its fix writes that.
- **After a conditional branch**, `.next` names the branch's own target and nothing else, and
  says the branch is **always taken**: the flags are known where it stands, as in
  `bne L297E ; always` after a load of a nonzero value, or `bcs over` after a routine that
  always returns with carry set. The edge that runs on past the branch is removed and the one
  to its target stays, carrying the state after the branch, as a taken branch's edge does. What
  follows the branch is then reached only by what else names it, so text or a table there is
  not run into, and a label there that nothing else reaches is unreachable as usual. The short
  branches (`bcc`, `bcs`, `beq`, `bne`, `bmi`, `bpl`, `bvc`, `bvs`), the Rockwell `bbr` and
  `bbs`, and the long branches `jeq` and the rest all take it. Naming anything but the branch's
  own target, or more than it, is an error (next-not-the-branch-target); where code runs into
  data after a conditional branch, the fix writes this `.next`.
- `.patch @op` acknowledges that the store above it writes into the instruction at
  `@op`.

The third directive is about the end of a routine rather than a statement:

- `.fallthrough next_proc` says every path that reaches the end of the routine's body runs
  on into `next_proc`, whatever stands above it: an instruction, a call, an `.if` chain with
  or without an `.else`, a label. It is the last line of a `.proc` body **as the configuration
  resolves it**: the last line of the body, or the last line of a branch of an `.if` chain
  that is itself the last thing in the body, to any depth, since which branch is written is
  settled before analysis and in each configuration at most one `.fallthrough` remains, last.
  A branch the build does not take is not read, so what it names need not exist in this
  build. A `.fallthrough` stands nowhere else: not with anything after it in its branch, not
  in a branch whose chain has more of the body after it, not in a macro body, a block argument
  or a repetition. `next_proc` has to be the routine written directly after this one in its
  segment's run of bytes (§5.2) — the next thing written to that segment, whatever regions of
  other segments stand between in the text — in the same translation unit, and is checked
  like a tail call against its signature.

| quirk | how it is recognized | what is required |
|---|---|---|
| indirect jump: `jmp (t,x)`, `jml [t]` | addressing mode | `.next` listing the targets, or `.next ?` |
| indirect call: `jsr (t,x)` | addressing mode | `.next` listing the routines; the call returns with the merge of their exits |
| `rts` used as a jump | a block pushes a code label and then returns | `.next` on the `rts` |
| a routine that returns past inline data: `jsr print` then `.strz "hi"` | the routine's signature declares `inline` (§7.3) | the data after each call matches the declaration: one `.strz`, or a run of data directives directly after the call that comes to exactly n bytes; the analysis skips it with no `.next`, on every CPU |
| jump to a computed address: `jmp lbl+3` | direct branch or jump operand is not a bare label or routine name | `.next` listing the real targets, or `.next ?` when the target is not an instruction boundary |
| label used as data: `.addr @h`, `lda #<@h` | a code label used anywhere except as a direct branch, jump or call operand, the argument of `.sizeof`, `.endof` or `.spanof`, or the `per L-1` of a relative call | a declaration (a `.state` after the label), unless a `.next` in the same proc names the label |
| label nothing names | no fall-through, branch, or address-taken use; a label on data and a data declaration are exempt | reported as unreachable; a declaration acknowledges it |
| data reached by fall-through: the `.byte $2c` skip, opcodes ca65 lacks | data directive inside a proc with a fall-through predecessor | `.next` on the data. A routine it names is a jump to that routine's start, checked like a tail call, whether or not it is the one written next |
| a conditional branch that is always taken: `bne L ; always`, `bcs` over inline text | the flags are known where the branch stands, which nt65 does not work out | `.next` naming the branch's own target, which removes the edge past the branch; the fix of the data it would otherwise run into writes it |
| jump to a label on a data directive | target's statement is data | `.next` on the data, plus a declaration on the label |
| jump into another proc's interior, exported inner label | scoped path to an inner label used as a target, `.export` of an inner label | a declaration on the label, which a jump from any module is checked against for the parts it gives, and which is all the code after the label assumes (§7.3). The stack there is the one entering the routine leaves, so nothing the path above the label pushed is known after it. Such a label may be a jump target, never a call target. The jump is a way out of the routine making it, checked like a tail call: control never comes back, so that routine returns the way the routine the label is in returns and hands back what it hands back (§7.7) |
| falling off the end of a proc | last block does not end in a transfer of control | `.fallthrough next_proc` as the last line of the body as the configuration resolves it, which may be the last line of a branch of an `.if` chain that ends the body, checked like a tail call and checked to be the routine written directly after, in the same segment, or `.next ?`; a warning off the 65816, whose fix writes the `.fallthrough` where the routine written next is known. The next proc may be another module's where placement puts the two in one translation unit (§12): across a `.place` it is read in the segment the placing file is in at that line, and a routine in another segment is an error naming both |
| falling off the end of a segment block nested in a proc | its last block does not end in a transfer of control | `.next` saying where flow goes, or `.next ?`. A segment block is not a routine, so this is a claim about where flow goes and nothing about what is written next, and `.fallthrough` does not stand there; a jump into and out of the block is followed like any other in the proc |
| a `plp` that pulls no saved P, non-constant `rep`/`sep`, `xce` not immediately after `clc`/`sec` | opcode | a `.state` before the next dependent use |
| interrupt handler | proc header | `interrupt`, so the first immediate before `rep`/`sep` is an error, and a call to it is too (§7.3) |
| other external entry point | proc header | `a?, i?` entry, so the first immediate before `rep`/`sep` is an error |
| self-modifying code: `sta @op+1` | store or read-modify-write whose operand references a code label | `.patch @op`; widths of `@op` are analyzed as written |

Examples. A jump table inside a proc: the targets need no declarations because the
`.next` edges carry the state at the jump.

```nt65
.proc dispatch: a8, i16 {
    lda cmd
    asl a
    tax
    jmp (table,x)
    .next @move, @fire

.data table: .addr @move, @fire

@move:
    lda #1
    rts
@fire:
    lda #2
    rts
}
```

Every item of `table` is a code label, so `.next table` says the same. A table is read only
from data declared as addresses: a label on a line of `.addr` directives is a position, and
names no targets.

An interrupt handler, and a `plp` that restores a status byte saved elsewhere, so the
analysis stack holds no saved P for it:

```nt65
.proc nmi: interrupt, native {
    rep #$30
    pha
    sep #$20
    lda #1
    sta nmi_flag
    rep #$20
    pla
    rti
}

    lda saved_p
    pha
    plp
    .state a16          ; the analyzer cannot know what was in saved_p
```

The `bit` skip trick:

```nt65
@set_one:
    lda #1
    .byte $2c           ; bit abs: swallows the next instruction
    .next @store
@set_two:
    lda #2
@store:
    sta value
```

The same trick across routines, where the bytes skipped are the first instruction of the
routine written next and the skip lands on the one after it. The `.next` names a routine
that is not the next one written, which is a jump there, checked as a tail call; the routine
in between runs into it and says so with `.fallthrough`:

```nt65
.proc set_one: a8 {
    lda #1
    .byte $2c           ; bit abs: swallows the `lda #2` of `set_two`
    .next store
}

.proc set_two: a8 {
    lda #2
    .fallthrough store
}

.proc store: a8 {
    sta value
    rts
}
```

A routine whose body ends in an `.if` chain, taken or not, runs into the next routine from
every branch, and the `.fallthrough` after the chain says so for all of them:

```nt65
.proc prepare: a8 {
    lda #0
    .if FAST {
        asl a
    }
    .fallthrough draw
}

.proc draw: a8 {
    sta value
    rts
}
```

Where what a routine runs into depends on the configuration, the `.fallthrough` goes in the
branches instead, each naming the routine that follows in the builds that take it. A branch a
build does not take is not read, so it may name a routine only other builds declare:

```nt65
.proc FRM_VARIABLE {
    lda #1
    .if !CONFIG_CBM_ALL {
        jmp LOAD_FAC_FROM_YA
    } .else {
        .fallthrough LCE69              ; LCE69 exists only on Commodore machines
    }
}
```

The one honest limit: recognition is complete for what is written. A pointer built by
runtime arithmetic that lands on an unlabeled instruction is invisible here as in any
other tool. The unreachable-label check and the requirement that computed jumps list
their targets push the programmer to write the label down, and once it exists it is
covered.

### 7.5 Direct page and data bank (65816)

On the 65816 a direct operand reaches D plus its offset and an absolute operand
reaches bank B, so an instruction that assembles correctly can still address the wrong
memory. nt65 checks this the way it checks widths: the state is declared, tracked
through the proc, and compared against declarations on segments. Nothing here affects
the output, since ca65 does not need it, and the checks are opt-in by declaration: a
program that never leaves bank 0 with D at 0 need not say anything.

**Declarations.** A segment may state which direct page it is meant to be reached
through and which bank it lives in, in the segment table (§5.2, §5.3):

```nt65
.segment ZP2: zp, dp = $2100
.segment WRAM: abs, bank = $7e
.segment LORAM: abs, bank = $7e, mirrors = [$00..$3f, $80..$bf]
```

`bank` and `dbr` are different words for different things: `bank` is where a segment lives,
its home bank, and `dbr` is the value of the data bank register at a point in a routine. Code
is taken to run in the home bank of its segment, even where a mirror maps it elsewhere too.

A routine's signature may carry the D and B values it assumes at entry and, after
`->`, at exit: `.proc hud: a8, i16, dp = $2100, dbr = $7e {`. `dp?` and `dbr?` mean
unknown. A routine that does not state them is `dp*, dbr*`: it assumes nothing about D
and B and returns them as it found them. `.state dp = expr` and `.state dbr = expr`
assert and set them like any other item (§7.3), and are the only way back from unknown.
A label a `.state` declares starts from D and B unknown, except that in a routine that is
`dp*` or `dbr*` it starts from them unchanged, so only a routine that declares D or B needs
its declared labels to say what they are.

**A set of banks.** Most routines of a small program never set B; they need only that it be
one of the banks that can see what they reach. `dbr` may name that set, written as `mirrors`
writes banks:

```nt65
.proc draw_bg: far, dp = 0, dbr = [$00..$3f, $80..$bf] -> a8, i16 {
    ...
    sta $2100               ; checked against every bank of the set
    ...
}
```

At entry B lies in the set, and the routine hands it back as it found it unless its exit says
otherwise, so a set at entry is `dbr*` with a bound: a label a `.state` declares starts from
it, as it would from unchanged. After `->`, a set says only that B is one of those banks on
the way out. A `.state dbr = [...]` asserts that B lies in the set, which is an error where
what is known of it lies wholly outside, and narrows what is known to the banks both say.
`phk`, `plb` and the other idioms make B one value as they always do, and two different sets
meeting at a label merge to unknown. A set of one bank is that bank, and `dp` has no set: the
direct page is one address.

**Transfer functions.** nt65 does not track register values, so it recognizes the
idioms that load D and B from constants and treats everything else as unknown:

| sequence | effect |
|---|---|
| `lda #const` then `tcd`, with A 16-bit | D = const |
| `pea const` then `pld` | D = const |
| `lda #const`, `pha`, `plb`, with A 8-bit | B = const |
| `phk` then `plb` | B = the home bank of the enclosing segment |
| `pld`, `plb` that pull a D or B saved by `phd`, `phb` (the analysis stack, §7.3) | the saved value |
| `mvn #s, #d`, `mvp #s, #d` | B = d; for `#^sym`, the home bank of `sym`'s segment, when it declares one |
| calls, returns, merges, `xce` | as for widths (§7.3); `xce` leaves D and B alone |
| any other `tcd`, `pld`, `plb` | unknown |

The pushes in these idioms are values on the analysis stack, so a pull finds them wherever
the stack is known, not only directly after the push: `pea $2100`, `phx`, `plx`, `pld` sets
D, and so does `lda #$2100`, `pha`, `pld` with A 16-bit. Where two paths pushed different
values, the merged stack keeps its depth and forgets the values.

**Checks.** When the state is known and the other side is declared:

- a direct operand naming a symbol in a segment with a declared `dp` is an error if D
  differs;
- an absolute operand of an instruction that reads or writes data, naming a symbol in a
  segment with a declared `bank`, is an error if B is neither that home bank nor one of its
  `mirrors`, and where B is one of a set, if any bank of the set is neither;
- the same kind of operand, when it is a constant address covered by the project's
  `ranges` table (§5.3), is an error if B is not one of the permitted banks, or may be one
  that is not, which is how `sta $2100` with B at `$7e` is caught;
- operands that do not use B are exempt: long operands (`f:`), `jmp` and `jsr` (the
  program bank K), `jmp (abs)` and `jml [abs]` (a pointer in bank 0), `jmp (abs,x)` and
  `jsr (abs,x)` (K), and `pea` and `per` (no memory access);
- `jsr`, `jmp` and branches to a routine or a label whose segment declares a home bank
  different from the caller's segment's are an error, with `jsl`/`jml` as the fix;
- `jml` to a near routine is an error unless it lands in a bank other than the home bank of
  the code making it. A long jump into another bank is how a FastROM reset stub in bank `$00`
  reaches code in bank `$80`, and since the routine's `rts` then stays in its own bank, it is
  allowed only where nothing returns: from a routine that never returns or an interrupt
  handler, or to a routine that never returns;
- a long jump or call may reach a routine at its address in a mirror bank, written
  `(bank << 16) | .loword(f)`. It is checked as a transfer to `f`, and `bank` must be `f`'s
  home bank or one of its mirrors;
- immediates such as `#<sym` are never checked.

When either side is undeclared or unknown, nothing is reported. Signatures are the
exception, as they are for widths: a call, a tail call or a jump to a declared label is
checked against the D and B its target declares, and a return against the ones its
routine declares, and there an unknown value, `*` included, is an error. A set is met by
a B known to be one of its banks, or one of a set within it.

**Constant addresses through the direct page.** `d:` on a constant address reaches it
through the direct page: with D known to be `$2100`, `lda d:$2105` is emitted as
`lda z:$05`. D must be known at that point and the address must lie from D to D + `$FF`;
anything else is an error. This is the checked form of subtracting the current direct
page by hand (libSFX's `dpo()`), which silently goes wrong when D changes. `d:` is for
constants; symbols reach the direct page through `zp` segments (§5.2). Like every prefix it
stands before the whole operand, so it applies to the direct and direct-indexed forms; an
indirect operand has no place for it.

### 7.6 Sizes, branch range and cycle counts

Once widths and address sizes are known, every instruction's length is known (a long
branch's once its form is chosen, below), and so is every data directive's except `.align`, whose length depends on an address: string
and `.incbin` lengths are read at analysis time, and `.res` and `.repeat` counts are
constants. `brk #s`, `cop #s` and `wdm #n` are two bytes. nt65 uses these lengths for diagnostics:
branch range, cycle counts and the assertions it can check before ca65 runs.

**Sizes: shapes and layout.** `*` stays symbolic, because nt65 never knows absolute
addresses, and sizes come in two kinds with different names:

- `.sizeof(x)` and `.countof(x)` describe a **shape**: a struct, a union or a data
  declaration (§6.3, §8). They are nt65 constants and may appear wherever a constant
  may, including `.res` and `.repeat` counts. `.sizeof` of a proc is the one exception: a
  routine has no shape, so its size is its span, below, with what that allows.
- `.endof(x)` is the address just past a proc or a data declaration, and
  `.spanof(x)` is `(.endof(x) - x)`. They describe **layout** and are address
  expressions like any label difference: usable in operands, data and `.assert`, never
  where a constant is required (`.res`, `.repeat`, choosing an address size), and
  resolved by ca65 and ld65. The end of a proc is the end of its own bytes in its segment;
  nested segment blocks do not count.

All four work only on a named extent, and a label has none: it is only a position. `.sizeof`,
`.countof`, `.endof` and `.spanof` of a label are errors, and so are they of a scope, which
is only a namespace.

| declared as | `.sizeof` | `.countof` |
|---|---|---|
| an element type, any form | elements × element size | elements |
| `.incbin`, `.strz`, `.lobytes`, `.hibytes` | bytes | bytes |
| `.data name { }`, mixed | bytes | error |
| `.proc` | bytes in its body, its span | error |
| a struct or union | bytes, which is an array's stride | members |
| an enum | error | members |
| a label | error | error |
| `.scope` | error | error |
| an import with an element type | as the element type says | as the element type says |
| an import with none | error | error |

```nt65
    ldx #.spanof(reloc)             ; bytes to copy

.data header: .word .spanof(module) ; length in a header

.assert .spanof(irq) <= 64, "irq handler too big"
```

No nt65 constant is derived from code, so no size can depend on itself, and an edit
inside a proc never changes a constant another file uses. nt65 reports an `.assert` at edit
time when it can evaluate it; one it cannot, such as a span that contains `.align`, is passed
to ca65 as a link-time assertion.

**Branch range.** The distance from a relative branch to its target is known when both are
in one run of their segment's bytes (§5.2): the same segment of the same file, or of one
translation unit (§12), with no `.align` or `.place` between them, whatever regions of other
segments stand between in the text. A nested segment block's bytes are in its own segment's
run, so it adds nothing to the distance across it in the segment around it. nt65 reports an out-of-range
8-bit branch (beyond −128..+127 from the following instruction) at edit time; `brl`
and `per` have 16-bit range. When the distance is unknown, ca65's own check stands.

**Long branches.** `jeq`, `jne`, `jcs`, `jcc`, `jmi`, `jpl`, `jvs` and `jvc` branch like
their short forms but reach any near target. Each is emitted as the short branch where
nt65 knows the target is in range, and otherwise as the inverted branch over a `jmp` to
the target. Every long branch starts short, and those found out of range are lengthened
until none changes, which terminates because branches only grow; a target at an unknown
distance is always long. The distance is read as branch range reads it, so a `jeq` to a near
routine is short with a region of another segment between them in the text, as it is with
none. ca65's `longbranch` package can choose the short form only for
a target it has already seen, so its forward branches are always long; here they are not.
For the flow analysis a long branch is a conditional branch to its target, and a long
branch to a routine is a tail call (§7.3). Its cycle count is that of the form chosen.

**Cycle counts.** Each instruction has a cycle interval [min, max] from the CPU's table
for its addressing mode and, on the 65816, its widths. Where the count depends on
something nt65 cannot know, the interval widens: a possible page crossing on an
indexed or indirect-indexed read adds one to max (on the 65816 with a 16-bit index the
extra cycle is always paid and the count is exact); a branch costs 2 not taken and 3
taken, plus 1 when a taken branch crosses a page on the 6502, its CMOS variants and in
emulation mode. On the 65816 a direct operand costs one more when the low byte of D is
nonzero, which is known when D is known (§7.5). Tooling shows the interval per
instruction and per basic block on hover, and beside an interval what its top would be paid
for — a page crossed, a branch taken, a register 16 bits wide — since an interval a reader
cannot resolve tells them half of an answer, and above each routine, and each inline `.scope`
block of one, what one pass through it costs: the shortest and the longest path from where it
is entered to where its path ends. A routine no path leaves is shown as never returning,
rather than as one nothing could be worked out for.

A path that can come back on itself has no longest, and the count is a fewest with a `+`,
except where the loop counts itself: a register loaded with an immediate, brought down by one
or more `dex` or `dey` written in a row before the `bne` or `bpl` that takes the turn round
again, with one way into the loop, one way out of it, and nothing else in it touching that
register. The loop is found from the back edge and the blocks that dominate it, so a turn may
branch and may call; a loop inside one is counted first, and the turns multiply. `bne` needs
the stride to divide the count, and `bpl` a count with the sign bit clear, or it is not
counting down from that immediate at all. Every other loop keeps the `+`, because a loop
counted wrongly is worse than one not counted.

A routine holding an instruction the CPU does not have, or does not take that operand for,
gets no count: the line is reported and left out of the byte stream, so counting the rest
would say the routine is quicker than anything it could be built as.

A routine is also shown what it costs **with what it calls**: a call costs the call and then
whatever the routine it names costs, and a tail jump the same, since control comes back from it
to this routine's caller. That is worked out across the program, so an edit to one file moves
what another file's lenses say. A call to a routine with no body, one through a pointer, and a
routine that can reach itself leave no total to give, and the lens says the calls are not in
the count rather than quietly leaving them out.

Control does not come back from a routine that never returns, so a call to one ends the pass
where it is made and what that routine does is no part of this one's count. A routine every way
out of which hands off like that does not come back either, which is worked out over the whole
call graph and shown as what it takes to get there and then never returning: the shape of every
program's entry point, which sets up and hands over to a loop that runs for ever.

**A span's cycles, for an `.assert`.** `.mincycles(from, to)` and `.maxcycles(from, to)` are
what one pass from `from` to `to` costs: the fewest cycles and the most. Both ends are
positions in code — a label, or a routine's own name for its first position — in one routine
and one stream of bytes, and the span is the instructions from the first up to the second,
which is where the pass arrives rather than a line it runs. The two answers are the sum of
each instruction's own interval, so they are one number where every instruction is exact, and
a range where a page crossing, a taken branch or a width nobody knows widens one of them:

```nt65
.assert .mincycles(raster::top, raster::bottom) == 17, "the raster line moved"
.assert .maxcycles(raster::top, raster::bottom) == 17, "the raster line moved"
```

A sum along a run of instructions bounds one pass only where the run is one pass, so **the
span may hold no call and no loop**, and one that does is an error saying which it found: a
call takes as long as the routine it names, a loop takes its body as many times as it turns,
and a jump nt65 cannot follow could go anywhere, this span included. The ends being in two
routines, or the second coming before the first, are errors of the same kind.

Like `.endof` and `.spanof`, a cycle span describes layout rather than a shape: it is usable in
operands, data and `.assert`, and not where a constant is required (`.res`, `.repeat`, an
element count), because what it is worth depends on how the file was laid out and how much room
a declaration takes is what the file is laid out from. Unlike them, nt65 writes the number
itself: ca65 knows nothing about cycles.

### 7.7 What a routine keeps

The first question anyone asks about someone else's routine is which registers survive it.
nt65 answers every other question about a routine — what it costs, where control goes, what
state it wants — so it answers this one too, on every CPU, of A, X, Y and the carry. N and Z
are left out: nearly every instruction writes one of them, so a promise about either would say
nothing.

Each routine is followed the way the 65816's state is, over what each register may hold at each
point: the value some register was entered with, a value an instruction here wrote, or nothing
known. The entry value is named by the register it came from and not by the one holding it,
because a 6502 saves X through the accumulator, and `txa` then has to carry X's value into A
for the `pha` after it to save it. A save and its restore cancel through a stack of what each
push holds, so `pha` … `pla` around a call, or a `php` … `plp` across a label, needs nothing
written. On the 65816 a push is as wide as the register, so a pull gets the value back only
where the width is the same at both — which a routine that changes neither width is, whatever
those widths are. A save does not span a label anyone may jump into: the stack there is the one
entering the routine leaves (§7.3), so a pull below such a label finds no push made above it,
and a save and its restore belong on one side of it.

What a routine's calls do is worked out with it, across the program: a call hands back what the
routine it names hands back, and no more. Every routine starts out keeping everything and what
each one keeps is taken away until nothing moves, so two routines that call each other settle
rather than go round for ever. This is the one place the registers are easier than the cycle
counts, which leave no total at all for a routine that can reach itself. A call nt65 cannot
follow — through a pointer, or to a routine with no body that promises nothing — leaves the
registers it did not save unknown, and the answer says it is not the whole one.

A path that hands control to another routine is not a call but a way out: control lands in that
routine and that routine returns to this one's caller. What a routine hands back across such a
path is therefore what the routine handed to hands back. A `jmp` to a routine's own entry is
taken at that word, and so is a jump into another routine's interior (§7.4), since control comes
back from neither. A branch says the same on the path where it is taken, whether it names the
routine or a label inside it, a `.next` says it for the statement it stands under, which is how
a jump through a pointer says where control goes, and a `.fallthrough` says it for the end of a
routine that runs on into the next one. So a routine whose only way out hands control to another
promises no more than the routine it hands off to: where that one promises nothing, this one can
promise nothing, and what is reported names where control went, the routine it went to and the
`keeps` that belongs there.

**`keeps a, x` is the promise.** On a routine with a body it is checked at every `rts`, `rtl`
and `rti`: a register the routine cannot be shown to hand back is reported there, with what to
write. On an extern proc and an imported routine it is declared and trusted, exactly as the
rest of a signature is, which is the only way to know anything about a body that is not here:
`.proc CHROUT = $FFD2: keeps x, y` is a fact about the C64 KERNAL that had nowhere to live. A
routine that writes no `keeps` promises nothing, and one whose body is not here keeps nothing
as far as a caller may rely. `keeps` takes bare register names, which are items nowhere else,
so the list needs no brackets; a signature set may give one, and a signature that writes its
own takes the place of what the set gives.

This makes a signature mean something on every CPU, where until now only the 65816 read one.
`near`, `far`, `inline`, `args` and the state items stay what they were.

**`.state keeps a` is what a restore the analysis cannot see says.** A routine that saves a
register to memory and loads it back has handed it over, and no analysis that does not follow
memory can see that the byte came back unchanged. Seeing it would mean ruling out every store
that could have reached it, and nt65 never knows an address, so whether `sta table,x` reached
`save` cannot be answered wherever the two are not in one stream — which, for a zero-page slot
and a table in another segment, is always. The annotation is therefore the answer and not a
stand-in for a better analysis. Like `.next ?` it is a claim with nothing to check it against,
which is the contract every annotation of §7.4 has, and a program it is wrong about is wrong in
the same way. Written where the register was never destroyed it says nothing, which is a
warning. A `.state` carrying only `keeps` is not a label's declaration: it says what a register
holds, not what the processor state at that label is.

What is **not** here is a warning at a caller that holds a register across a call that destroys
it. It can be made to work: which registers a routine reads before writing them is as
computable as what it keeps, and a register the routine called reads is one the caller loaded
for it, so passing a value in a register and reading the result back out of the same one says
nothing. It is left out because a warning is the wrong shape for the answer. Every call takes
some register away, being told so is rarely news, and the reader's question is what the
registers are doing here rather than a list of the places they changed. That is something to
show, and §14 says where per-instruction facts are shown.

Tooling shows it in three places, and none of them is the only one: a lens is something an
editor can be told not to show.

- **A lens** above each routine, and above each inline `.scope` block of one, beside what a
  pass costs: `preserves A, X, Y, C`, `preserves X, Y`, `preserves none`. It is a list and not
  a sentence about one, because a lens is read at a glance. A block is asked the same question
  of itself that its routine is asked of its caller, from where the block is entered, so a
  `.scope` that saves a register and gives it back preserves it even where the routine around
  it does not. What nt65 works out is a floor, so where a call could not be followed the list
  ends with `?` — those registers and perhaps more, which is what `?` means everywhere else.
- **On hover over the line that declares a routine or opens a block**, the same list, because
  the lens above it may not be there.
- **On hover over an instruction**, beside what the line costs, what each register holds there,
  one to a line and always all four, and under them what the routine has pushed, top of the
  stack first:

  ```text
  A       X as entered
  X       as entered
  Y       new
  C       as entered, or new

  stack   X as entered
          status a8, i8
  ```

  What a register may hold is a set rather than one answer: the value it was entered with, the
  value another register was entered with — which is how a 6502 saves X, and naming the
  register the value came from is what makes the save readable — a value written here, or
  something nothing is known about. Where two paths leave different things the words are
  joined and not collapsed, because a save that is still good on one of them is worth seeing.
  A push is spelled the same way, and on the 65816 the state analysis names what the
  saved-register stack cannot: what a `php` saved, what a constant push holds, where a
  `.frame` is, and how wide the register a push moved was.

## 8. Data

Every data declaration starts with `.data`, as every routine starts with `.proc`. A label
is only a location, and a size can come only from a declaration, so a line break never
changes what a name means.

```nt65
.data player_x: .word                            ; one element, no value
.data buffer: .byte[64]                          ; 64 elements, no values
.data gradient: .byte 40, $e0, 0                 ; values on one line: three elements
.data row_lo: .byte[] {                          ; values in a body, which counts them
    .repeat 25, r {
        <(SCREEN + r * 40)
    }
}
.data handlers: .addr[4] { move, fire, jump, quit }   ; a body must hold exactly its count
.data header: .type RomHeader { title = "NT65" }       ; one record (§6.3)
.data sprites: .type Sprite[] {                        ; an array of records
    { x = 10, y = 20 }
    { x = 30, y = 40 }
}
.data tiles: .incbin "tiles.bin"                 ; bytes
.data msg: .strz "hi"
.data lo: .lobytes handlers
.data id: .bedword $4e543635                      ; high byte first
.data basic_stub {                               ; mixed data
    .word @next, 10
    .byte $9e, "2061", 0
@next:
    .word 0
}
```

The element types are the numbers `.byte`, `.word` (16 bits), `.long` (24) and `.dword` (32),
their big-endian partners `.beword`, `.belong` and `.bedword`, the addresses `.addr` and
`.faraddr`, and `.type T` for a struct or union `T`, which is dotted as they are. Every width
has a big-endian partner, so nobody wonders which widths have one: ca65 has only `.dbyt`,
which sits confusingly beside `.dword`, a little-endian 32-bit value. An element type may take a count: `[n]`,
or `[]` for as many elements as its values come to. `.word[16]` is sixteen words and
`.word 16` one word holding 16, and a value never starts with `[`, so the two cannot be
confused; a record is `.type T { … }` and an array of records `.type T[] { … }`.

- **Values.** Without a count, the values are written on the declaration's line, and an
  element type with no values is one element. With a count, they are written in braces, on
  the line (`.data lut: .byte[4] { 1, 2, 4, 8 }`) or in the body the line opens.
  `.data name: .byte 1, 2` and `.data name: .byte[] { 1, 2 }` are the same data; the second
  is for a count to check or values that span lines.
- **Bodies.** A body line is values separated by commas, one element each; for `.byte`, a
  string is one element per character, and for an array of records each element is a braced
  initializer. A body may hold `.repeat`, `.each` and `.if` blocks whose lines are values too.
- **Counts.** `[n]` with values checks the count exactly. A body shorter or longer than its
  count is an error that names both, rather than padding with zeros, because a short jump
  table is exactly the mistake a count is there to catch; padding is written with a
  `.repeat` in the body. `[]` with no values at all is an error. One text is the exception:
  a counted `.byte` array whose only value is one text (a string, a text constant, a call
  that returns text, or a charmap applied to one), `.data title: .byte[21] { "NT65" }`, is filled out with zero to its count,
  as `char title[21] = "..."` is in C. A text is not a table, so nothing else is padded: two
  texts, or one character, are a list again and a short one is the error it always was, and a
  pad other than zero is what a structure's `.res n, pad` member is for (§6.3).
- **Mixed data.** `.data name { }` holds unnamed data directives of any kind, nested `.data`
  declarations, `@` positions, unnamed `.align` and `.res`, `.repeat`, `.each` and `.if`, and
  macro calls that expand to those. It holds no instructions. A nested declaration is a
  member, reached as `name::sub` and emitted as `name__sub`, to any depth. The block is a
  scope, so inside it a member's siblings are named bare, as lookup runs outward (§6.2): a
  member `update` of `image` may write `.word play` for `image::play`. An `@` position is
  private to the block: it may be named inside it, has no size and cannot be exported. A
  plain label is an error there that suggests a member or a position.
- **Padding.** `.res n [, fill]` is only padding: unnamed between declarations, in a mixed
  body, or as a struct's string member (§6.3). `.data ptr: .res 2` is an error that asks for
  `.byte[2]`, since storage is declared with its type. `.align boundary [, fill]` is the other
  padding: it writes however many bytes it takes to reach the boundary, which is a constant
  power of two from 1 to $10000, and the fill is a byte as a `.res` fill is. How many bytes
  that comes to depends on where it lands, so an `.align` is the one directive with no length
  nt65 knows (§7.6).

Unnamed data directives are written as they are in ca65, inside a proc (inline data after a
call, the `.byte $2c` skip) or in mixed data:

```nt65
    .byte 1, 2, $ff, 'A', "text"
    .word $1234, label
    .long $123456                   ; 24-bit number
    .dword $12345678
    .beword $1234                   ; $12 then $34
    .addr label                     ; 16-bit address
    .faraddr label                  ; 24-bit address (65816)
    .res 16                         ; 16 bytes of zero
    .res 16, $ff                    ; 16 bytes of $ff
    .strz "hello"
    .align 256
    .incbin "sprites.bin"
    .incbin "sprites.bin", 64, 32   ; from offset 64, 32 bytes
    .lobytes first, second, third
    .hibytes first, second, third
    .bankbytes first, second, third
    .type Player                    ; .sizeof(Player) bytes (§6.3)
    .type Player { hp = 5 }         ; a record with values (§6.3)
    .addr handlers                  ; a list's items (§6.4)
```

A number slot holds a signed or an unsigned value of its width: a `.byte`, an 8-bit
immediate and a `.res` fill take -128 to 255, a `.word`, a `.beword` and a 16-bit immediate
-32768 to 65535, a `.long` and a `.belong` -8388608 to 16777215, and a `.dword` and a
`.bedword` the 32-bit equivalent. A negative constant is written as its two's complement,
with the source in a comment, and a value out of range is an nt65 error; a value that is not
a constant is written as it stands. An address slot holds an address of its width, which is
never negative: `.addr` takes 0 to $FFFF and `.faraddr` 0 to $FFFFFF. `.long` holds a 24-bit
number where `.faraddr` holds an address, which is the difference a C header generator tells. A far address in an `.addr` or a `.word` is an error rather than its low 16
bits, which ca65 would keep in an `.addr` without a word; `.loword(x)` says those are what
is meant. In the same way an absolute or far address in a `.byte` or a one-byte immediate,
and a far one in a two-byte immediate, is an error that ca65 would otherwise report as a
range error: `<x` and `.loword(x)` say which part is meant.

**Elements.** `name[i]` is the element at `i` of a declaration that has a count:
`buffer[3]`, `handlers[2]`, `actors[1]`. It is `name` plus `i` times the size of one element
— a record's being its type's size — and is as wide an address as `name` is. A member path
may follow, `actors[1]::hp`, and a member that is itself an array takes one of its own,
`player::colors[2]`.

An index is a constant, worked out before the program runs; one worked out as it runs is
what `actors::hp,x` is for. An index at or past the count, or below zero, is an error, and so
is an index on something with no elements: a routine, a constant, mixed data, or a name that
stands for what a call or a repetition gave it. A declaration's `[n]` and an expression's
`[i]` never meet — a count follows an element type, an index follows a name — and `.sizeof`
and the rest measure a declaration, not a place in one. The output writes the sum with the
path it came from in a comment, as `player::hp` is written.

```nt65
.data actors: .type Actor[8]

    lda actors[1]::hp
    .repeat 8, i {
        lda actors[i]::hp           ; a turn's name is a constant, so it indexes as one
    }
```

A data declaration's `.sizeof` is its bytes and its `.countof` its elements (§7.6):
`.byte[16]` gives 16 and 16, `.word a, b` gives 4 and 2, `.type Player[8]` gives
`8 * .sizeof(Player)` and 8, `.type Player { ... }` gives `.sizeof(Player)` and 1, a list
counts one element per item, and a string one element per byte. Mixed data has a size and
no count. One holding an `.align` has no size nt65 knows, and neither has one holding a macro
call, whose bytes exist only once it is expanded, after every constant has its value (§3.1):
`.sizeof` of either is an error naming the reason, and its `.spanof` measures it. Text a
`.func` returns is no macro call: a call is worked out with the constants (§9), so the bytes it
writes are known when they are, and so is the size of what holds them. `.endof` and `.spanof` work
on every data declaration.

**Distances inside a declaration.** nt65 never knows where a declaration lands, but it lays out
every byte of one it can size, so two places in the same declaration are a known distance apart
wherever it lands. The places are the declaration itself, its end (`.endof`), a member declared
in it at any depth, an `@` position in it where it can be named, an element `name[i]`, a member
reached through a record, and any of those a constant away. The difference of two of them is a
**constant** whenever nt65 knows every length between them: it is usable wherever a constant
is, sizes as its value does (§7.2), so a one-byte immediate takes it, hover shows it, and the
output writes it as its value. An `.align` between the two makes the length depend on where the
declaration lands, and a macro call between them writes bytes only once it is expanded, so
either leaves the difference an address expression, sized and written as one. A table whose
entries a macro would write is written with a function that returns text instead, and its
distances are constants (§9).

Two data declarations of one file in one segment are a known distance apart in the same way,
since a segment's bytes are one run (§5.2): their places' difference is a constant where
everything the file writes to that segment between them is data or padding whose length nt65
knows, whichever regions and nested segment blocks it is written in. A routine between them in
that segment is code, whose length is layout, and a `.place` puts another module's bytes
between, so either leaves the difference an address expression, as an `.align` or a macro call
does. Code has no such
distances: a routine's size is layout (§14).

```nt65
.data messages {
    .data NOFOR: .byte "NEXT WITHOUT FO", 'R' | $80
    .data SYNTAX {
        .byte "SYNTA", 'X' | $80
    }
}

ERR_NOFOR = messages::NOFOR - messages
ERR_SYNTAX = messages::SYNTAX - messages
```

`ldx #ERR_SYNTAX` loads 16, the offset of the message in the table, and the output writes
`ERR_SYNTAX = $10`.

**Binary files.** An `.incbin` path is relative to the `.nt65` file that names it. nt65
reads the file for its length (offset and length must be constants), treats it as a
dependency so that editing it re-analyzes the files that use it, and reports a missing
file as an error. The output names the file by a path relative to the output file,
which ca65 resolves from the directory of the file containing the `.incbin` wherever it
is run.

**Text encoding.** `.charmap` declares a named mapping from characters to bytes. It is
a declaration, not a mode, and is applied explicitly where text is emitted:

```nt65
.charmap screen {
    'A'..'Z' = $01          ; a range maps to consecutive values
    '@'      = $00
    ' '      = $20
}

.data greeting: .byte screen("HELLO WORLD")
.data letter: .byte screen('A')
```

A mapping may name any character, ASCII or not, and a character with no mapping is an
error when the mapping is applied. A mapping is an ordinary declaration,
exported and used across modules like a constant.

**Text.** A text is a string of bytes. A string literal writes one, ASCII outside a charmap and
`\xHH` for any byte. Text is usable in data, in a `.strz`, as what a charmap is applied to, in
`.strlen` and `.strat`, as a `.type` member's value and as a macro argument, and wherever one is
usable, each of these is:

- **a text constant**, `TITLE = "NT65"`, which crosses modules by value like any constant and
  never reaches ca65 as a symbol;
- **a call of a function whose body is text** (§9), `htasc("SYNTAX")`, which is text when its
  body is and may define a text constant, `GREETING = htasc("HI")`;
- **`.select(c, a, b)`** choosing between texts;
- **`.strsub(s, start, count)`**, the `count` bytes of `s` from `start`, counting from 0. A start
  or a count below zero, or one that reaches past the end, is an error rather than a shorter
  text;
- **`.strcat(part, ...)`**, its parts joined: a text part gives its bytes and a number part the
  one byte it is, which has to be 0 to 255, so `.strcat(13, "READY.", 13)` needs no escapes and
  `.strcat(.strsub(s, 0, n - 1), .strat(s, n - 1) | $80)` sets bit 7 on the last byte.

`.strlen(s)` is how many bytes `s` is, and `.strat(s, i)` the byte at `i`. There are no
operators on text: `+` and the rest are on numbers, and joining is `.strcat`. A define is never
text, and a literal a call builds text from is ASCII as any literal outside a charmap is.

Text never reaches ca65: the output writes its bytes, with the source expression in a comment,
whether it was written as a literal or built. Because a function is worked out with the
constants, the length of what one returns is known when they are, so a table of such texts has
a size and constant distances inside it (above), which a table a macro writes cannot have.

A charmap applied to built text maps each byte as the character of that code, exactly as it
maps a literal: a byte at `$80` or above, from `\xHH` or from `.strcat`, is the character
U+0080 to U+00FF of that number, which the charmap maps if it names that character, as in
`'\xc1'..'\xda' = $41`, and refuses otherwise. Bit 7 is not a flag the charmap looks past,
because which bytes a charmap gives is the charmap's to say and not a rule of nt65's.

**`.strz`** writes one text and the zero that ends it: a string literal, a text constant, a call
that returns text or a charmap applied to one. Numbers and further arguments are errors. A `$00` inside the text is
an error that names the character, as with `screen("A@B")` when `screen` maps `@` to `$00`:
the text would end early, and a routine declared `inline .strz` would return into the middle
of it. The name is the terminator's: DEC's `.ASCIZ`, by way of ca65's `.asciiz`, said "ASCII",
and nt65's text is ASCII only without a charmap.

`.countof` of an enum is how many members it has.

All character and string data, mapped or not, reaches the output as byte values (§13),
so a ca65 target (`-t`) cannot translate it.

## 9. Expressions

Operators and precedence, highest first:

| level | operators |
|---|---|
| 1 | built-in functions, parentheses |
| 2 | unary `+` `-` `~` `!` `<` (low byte) `>` (high byte) `^` (bank byte) |
| 3 | `*` `/` `.mod` |
| 4 | `+` `-` |
| 5 | `<<` `>>` |
| 6 | `<` `<=` `>` `>=` |
| 7 | `==` `!=` |
| 8 | `&` |
| 9 | `^` (xor) |
| 10 | `\|` |
| 11 | `&&` |
| 12 | `^^` |
| 13 | `\|\|` |

This is C's order, with `.mod` in place of `%` (which begins a binary number) and `^^`
for logical exclusive or. `=` only defines; equality is `==` and `!=`.

Where the order is easy to misread, parentheses are required:

- an operand of a shift, `&`, `^` or `|` may not be an unparenthesized binary expression
  with a different operator: `1 << i + 1` and `a & $0f == 0` are errors, and
  `(a & $0f) == 0` is not;
- `&&`, `^^` and `||` may not be mixed without parentheses: `a || b && c` is an error;
- a unary `<`, `>` or `^` may not be followed by a binary operator: `#<label+1` is an
  error, and `#<(label+1)` and `#(<label)+1` are not.

The first two follow the cases C compilers warn about under `-Wparentheses`; the third
covers a byte operator that reads as if it applied to the whole expression.

ca65's precedence differs, which does not matter: the output is parenthesized wherever
precedence could, so nothing depends on ca65's table.

A name may be followed by `[i]`, the element at `i` of a declaration with a count (§8).
It binds tighter than every operator, as a parenthesis does, and what is inside the brackets
is an expression like any other.

`*` is the current address. Built-in functions: `.lobyte(e)`, `.hibyte(e)`,
`.bankbyte(e)`, `.loword(e)`, `.hiword(e)`, `.sizeof(x)` and `.countof(x)` (§6.3, §8),
`.endof(x)` and `.spanof(x)` (§7.6), `.loadof(S)`, `.runof(S)` and `.spanof(S)` of a segment (§5.2), `.mincycles(from, to)` and `.maxcycles(from, to)` (§7.6),
`.strlen(s)`, `.strat(s, i)`, `.strsub(s, start, count)` and `.strcat(part, ...)` (§8), `.min(a, b)`,
`.max(a, b)`, `.sqrt(n)`, `.muldiv(a, b, c)`, `.sin(angle, turn, scale)` and
`.cos(angle, turn, scale)` (below), `.addrsize(x)`, the address size in bytes (1, 2 or 3) that
§7.2 gives a symbol or expression, `.target(cpu)`, true when the program's CPU is the one named
(§5.1), `.has(mnemonic)`, true when the program's CPU has that instruction,
`.select(c, a, b)`, and `.defined(NAME)`, which is true if NAME is a define (§5.3) and false
otherwise. Naming a symbol the program declares in `.defined` is an error, since
conditions never test the program (§10). Macro bodies add `.mode`, `.byteof`, `.exprof` and
`.empty` (§11).

**Numbers worked out at build time.** Four built-ins work a number out rather than ask about the
program, so that a table a routine reads is written where the routine is and not in a script in
another language. Each takes whole numbers and answers a whole number:

- **`.sqrt(n)`** is the largest whole number whose square is at most `n`. A negative `n` has no
  such number and is an error.
- **`.muldiv(a, b, c)`** is `a * b / c`. The product is worked out exactly, however large it is,
  so nothing overflows in the middle, and the quotient is rounded to the nearest whole number.
  A zero `c` is an error, and so is an answer that leaves 64 bits.
- **`.sin(angle, turn, scale)`** is `scale` times the sine of `angle`, where a whole turn is
  `turn` of the angle's own units: `.sin(i, 256, 127)` walks a circle in 256 steps and reaches
  127 at the quarter turn. **`.cos`** is the same a quarter turn on. The angle is taken round
  the circle first, so a table written with a running index needs no wrapping of its own and a
  negative angle is the same angle the other way. The turn is 1 to `$7fffffff` and the scale at
  most that either way; outside those it is an error.

**Rounding, and why it is written down.** What a declaration is worth is written into the
output, so an answer that differed in its last bit between two machines would assemble to
different bytes from one program. None of these is floating point and none of them may be: each
answer is **the whole number nearest the exact value, with a half going away from zero**, and
that is a definition every implementation can meet exactly. `.sqrt` and `.muldiv` are whole
numbers throughout. For `.sin` and `.cos` the exact value is a real number, and the only angles
at which it can sit exactly halfway between two whole numbers are the twelfth-turns where the
sine or the cosine is `±1/2` — a rational multiple of π has a rational sine only at 0, `±1/2`
and `±1` — so nt65 works those out from the fraction itself and everything else to a precision
at which the nearest whole number is not in doubt. `.sin(1, 12, 127)` is 64 and
`.sin(7, 12, 127)` is −64, both away from zero; `.sin(1, 12, 126)` is 63, with nothing to
decide.

```nt65
TURN  = 256
SCALE = 127

.segment RODATA
.data sine: .byte[TURN] {
    .repeat TURN, i {
        128 + .sin(i, TURN, SCALE)
    }
}
```

**`.select(c, a, b)`** is `a` when `c` holds and `b` when it does not. The condition must be
a constant, and only the chosen value is evaluated and has its names checked, so the other
may name what this build does not declare. It is usable in constants, conditions, `.func`
bodies and address expressions, where the output writes the chosen address. There is no
`?:` operator: `:` already means an address prefix, a signature, and an import's or
export's size.

```nt65
COLUMNS = .select(WIDE, 80, 40)
.func clamp(v) = .select(v > 255, 255, v)
```

**Functions.** `.func` declares a pure expression function, which is what a function-like
`.define` is used for in ca65:

```nt65
.func rgb15(r, g, b) = r | (g << 5) | (b << 10)

.data red: .word rgb15(31, 0, 0)
```

A call is written like a charmap application, `name(args)`. The body is one expression
whose names resolve where the function is declared. Arguments are values, not tokens: a
call means its body with each parameter replaced by its parenthesized argument, so
`rgb15(1 + 1, 0, 0)` passes 2. A call is constant when its arguments are, and may then
appear wherever a constant may, including `.res` counts, but not in an `.if` condition: a
function is a declaration of the program, and conditions are answered before any declaration
is read (§10). Functions may call functions, but not in a cycle, which is an error whether or
not anything calls them. A function
is exported and used across modules like a constant, and the output writes each call as
its parenthesized body, or as its value where nt65 has one.

**A function may return text.** A call is text when its body is: a string, a text constant, a
parameter given text, `.select` choosing text, `.strsub` or `.strcat` (§8). It is then usable
wherever a string is, and the output writes its bytes, as it writes a literal's. A call is
worked out with the constants, before anything is expanded (§3.1), so what it writes has a
length when they do, and a distance past it in a data declaration is a constant:

```nt65
; A text with bit 7 set on its last byte
.func htasc(text) = .strcat(.strsub(text, 0, .strlen(text) - 1), .strat(text, .strlen(text) - 1) | $80)

.data ERROR_MESSAGES {
    .data NOFOR: .byte htasc("NEXT WITHOUT FOR")
    .data SYNTAX: .byte htasc("SYNTAX")
}
ERR_SYNTAX = ERROR_MESSAGES::SYNTAX - ERROR_MESSAGES   ; 16, a constant
```

nt65 evaluates every expression it can (anything built only from constants) and uses
the value for sizing and diagnostics. The difference of two places in one data declaration is
one of them wherever every length between the two is known (§8): it names addresses, and it
is a constant, written as its value. Other expressions involving addresses are emitted
symbolically for ca65 and ld65 to resolve.

**Arithmetic is 64-bit and signed** while nt65 computes, so that what an expression passes
through on its way to a value has room. Within that:

- `/` truncates toward zero and `.mod` takes the sign of the dividend, so `-7 / 2` is `-3`
  and `-7 .mod 2` is `-1`. A division or a remainder by zero is an error.
- `>>` is arithmetic: the sign comes with it, so `-8 >> 1` is `-4`. A shift counts 0 to 63
  places, and a count outside that is an error rather than a value of its own.
- `+`, `-`, `*` and `<<` are errors where the result leaves 64 bits, as is negating the
  smallest number there is. Nothing wraps: a wrapped number is one nobody wrote.
- The comparisons and the logical operators are 1 or 0, and `&`, `|`, `^` and `~` are the
  bits of the 64-bit value.

**A value that reaches the output fits ca65's 32 bits.** ca65 computes in 32 bits, signed, and
reads a number up to `$ffffffff`, so a value the output carries is at least `-$80000000` and at
most `$ffffffff`. It is checked where it is declared, and so is every step of the expression it
is declared with: the output writes that expression as the source wrote it (§13), so ca65 works
the same steps out again and the two have to reach the same number. A condition and a count are
nt65's own, reach no output, and have all 64 bits to move in.

Together those give the invariant the output rests on: **an expression built only from
constants is never written out as text for ca65 to work out**. Either nt65 has its value, or
nt65 has said why it has none.

## 10. Conditional assembly and repetition

```nt65
.if DEBUG {
    jsr trace
} .elseif LEVEL > 2 {
    nop
} .else {
    ...
}

.data bits: .byte[] {
    .repeat 8, i {
        1 << i
    }
}

.assert .sizeof(table) == 32, "table must be 32 bytes"
.if !SOUND {
    .warning "built without sound"
}
.if PLATFORM > 2 {
    .error "unsupported configuration"
}
```

`.assert cond, "message"` takes no level. ca65's `error`, `warning`, `lderror` and
`ldwarning` choose when a check runs, which nt65 decides itself: at edit time when it can,
and otherwise at link time, which the output writes as ca65's `lderror`. A failed assertion
is always an error, and the message may be left out. `.error "text"` is a configuration the
file refuses to be built in, and `.warning "text"` one it builds in and has something to say
about.

`.if` and `.repeat` are allowed at item level, inside procs, and in `.data` bodies, where
their lines are values (§8). `.if` is allowed in an `.enum` body too, where its lines are
members (§6.3); a repetition is not, because a member's name is written, never computed.
`.multiproc` is allowed where `.proc` is, and nowhere else.

**Conditions test the configuration, not the program.** An `.if` condition may use
literals, operators, built-in functions, defines (§5.3) and settings. Inside a macro body it may
also use the macro's `const` and `one(...)` parameters, `.mode(p)` (§11.2) and
`.empty(p)` (§11.4), inside a `.repeat` body the repetition index, and inside an
`.each` body a binding whose value is a constant or a word. It may not otherwise name a constant, label or any other symbol
the program declares. nt65 therefore evaluates every condition outside those bodies
before it looks up any declaration, and which declarations exist follows from the
configuration alone. A check that depends on the program, such as
`.sizeof(Player) <= 16`, is an `.assert`, which is evaluated last.

The exceptions are safe because names declared in a macro body, a `.repeat` body or an
`.each` body are local to the expansion or the iteration, so which names the program
declares still follows from the configuration. They do read program constants, through
an argument such as `gen!(MAX_ACTORS)` or a list item, which is why expansion comes after
constants are evaluated and nothing that decides a constant contains a macro call (§3.1,
§11.1).

**Settings.** `.config NAME = value` is a define a file declares:

```nt65
.module hw
.export .config SOUND_CHANNELS = 3
.config PAL = 0

.if SOUND_CHANNELS > 2 {
    ...
}
```

A setting is written at file level, outside every block, so that which settings a program has
depends on no condition, and its value may use only literals, built-ins, defines and other
settings. Conditions anywhere may test it. It is private to its module and exported like a
constant, reached as `hw::SOUND_CHANNELS` or brought in with `.use`. The build may set an
exported one by its qualified name (§5.3), which makes the value in the file a default; a
setting the module keeps to itself is not part of its configuration, and setting it is an
error. The output writes a setting as its value, as it writes a define. The spelling is not
ca65's `.define`, which substitutes text.

In `&&` and `||` the right operand is evaluated, and its names checked, only when the
left operand does not already decide the result, so `.if .defined(TRACE) && TRACE`
works when `TRACE` is not defined.

**Declarations under an `.if` belong to the enclosing scope**, as if the `.if` were not
there, and a declaration in a branch that is not taken does not exist. The same name
may be declared under several `.if`s, in one chain or in separate ones:

```nt65
.if PLATFORM == 1 {
    LINES = 262
}
.if PLATFORM == 2 {
    LINES = 312
}
```

Two declarations of one name in taken branches are a duplicate, and a use of a name
with none is undefined. Both are reported for the configuration being built, as with
`#if` in C or `#[cfg]` in Rust. A member under an `.if` in an enum body belongs to the enum
the same way, and the members of the branches taken are the enum's, in the order they are
written.

`.repeat` counts may be any constant (no addresses).

`.each` repeats its body once per item of a list (§6.4) or a `list` parameter (§11.2), or
once per member of a named enum, in order:

```nt65
.data dispatch: .addr[] {
    .each handlers, h {
        h - 1                       ; an RTS dispatch table
    }
}

.data actions_table: .addr[] {
    .each Cmd, c {
        actions::c                  ; one entry per member of Cmd
    }
}
```

Over a list, the binding is each item's value. Over an enum it is the member as a name:
as an expression it is the member's value, and as the last component of a path it names
the member of that scope with the same name, so `actions::c` is `actions::move`, then
`actions::fire`. A table built this way stays in step with the enum it follows, which is
what ca65 code uses `.ident` for.

Over a list or a count the binding names no member, so a path ending in it is an error, and
so is a scope with no member of the name the binding stands for on some turn.

**A declaration named after the binding is a family**: one declaration per member of the enum,
under the member's name, in the scope around the `.each`. It is the rule that lets
`actions::c` *find* a member's declaration, run the other way to *make* one.

```nt65
.enum Channel {
    pulse1
    pulse2
    triangle
    noise
}

.scope level {
    .each Channel, ch {                     ; the form `.multiproc` stands for
        .data ch: .byte                     ; level::pulse1, level::pulse2, ...
    }
}

.scope play {
    .multiproc Channel, ch: a8, i8 {        ; play::pulse1, play::pulse2, ...
        lda level::ch
        .if ch == Channel::noise {          ; the enum's members may be named in a condition here
            inc a
        }
        sta level::ch
        rts
    }
}

.data dispatch: .addr[] {
    .each Channel, c {
        play::c                             ; play::pulse1, play::pulse2, ...
    }
}

    jsr play::triangle
```

`.multiproc E, b: signature { body }` is `.each E, b { .proc b: signature { body } }` with the
two blocks folded into one line, for the case that is nearly every family: one routine per
member and nothing else. It stands where `.proc` stands, and everything said here of a family
holds of it.

Which names a family declares comes from two headers — the repetition's line and the enum's
member list — so nothing is concatenated and the set of declarations still follows from the
configuration before anything is evaluated. Whether an instance exists never depends on a
value: a binding-named declaration stands directly in the body, not under an `.if` in it,
because a condition in a turn tests the member's *value*. Which instances there are varies by
configuration where the enum does, under an `.if` in its body or around it (§6.3).

The rules:

- A family declares routines and data: `.proc`, `.multiproc` and `.data name: element`. A
  `.scope` or a `.data` block named after the binding would declare everything inside it once
  per member, which is not what a family is; two roles for one member are two families,
  `note::pulse1` and `stop::pulse1`.
- A binding-named declaration stands directly in the body of an `.each` over a named enum, at
  item level: file level, a `.scope`, a segment region or block, or an `.if` around the
  `.each`. Anywhere else it is an error that says why: inside a proc it would nest, and over a
  list, a `list` parameter or a `.repeat` there are no names. `.multiproc` stands where `.proc`
  stands, and is elsewhere the error `.proc` is there.
- It is one declaration per member in the enclosing scope, so it collides with a hand-written
  declaration of a member's name there, and two families over the same enum in one scope
  collide too, as any duplicate does.
- `.export` before a binding-named declaration, or before `.multiproc`, exports every instance;
  the list form, `.export play::pulse1`, exports one. It is the one `.export` a repetition body
  may hold, since the names it exports are the enum's. `.export .scope play { }` around a
  family exports its instances by the ordinary rule.
- A condition in the body may name the enum's members, `.if ch == Channel::noise`: the
  binding's own value is one of them, so they are known where a turn's conditions are answered.
- What the body declares is the turn's, as a repetition's always is, and is named after the
  instance in the output (§13). An instance's name, kind and signature come from the headers,
  so a file's interface is still derived from headers alone (§14).
- An unused warning names a family only when nothing uses any instance, as an enum's members
  are one of a set. A problem the body has is reported naming the instance it was found on,
  and once, naming the binding, when every instance has it.


None of these decides anything in the output. nt65 resolves every `.if` and unrolls every
`.repeat` and `.each` itself, one turn at a time; names declared inside a `.repeat` or
`.each` body are distinct per iteration, as macro expansion labels are, and nothing outside
the body can name them. The one thing that does reach the output is the shape of a counted
`.repeat` whose turns all came out as the same lines, which is written back as a ca65
`.repeat` around one copy of them, with a counter in it where the turns differ only in the
number the binding was worth (§13) — a repetition of what nt65 decided, with nothing in it
left for ca65 to work out. A
body holds nothing that is one thing for the whole file: no `.import`, `.use`, `.module`,
`.cpu`, segment declaration, `.macro` or `.func`. A family is the one thing it does hold that
is not the turn's — a `.proc` or a `.data` whose name is the binding, and the `.export` before
it — because what such a declaration declares is one per member of the enum, which is a set
the configuration already fixed rather than something a turn works out.

## 11. Macros

Most of what ca65 code uses macros for is a language feature in nt65: constants and
functions instead of `.define` (§9), charmaps instead of screen-code macros (§8),
initialized records instead of record macros (§6.3), lists and `.each` instead of
variadic and name-building macros (§6.4, §10), long branches instead of `longbranch`
(§7.6), and flow-analyzed widths with `.ensure` instead of width macros (§7.3).
Appendix B lists the common patterns and what replaces each. What remains for macros is
instruction idioms, structured control built from blocks, computed data and debugging
wrappers, and a macro system that serves only those can stay restricted enough to
analyze.

```nt65
.macro set16(dest: operand, value) {
    lda #<value
    sta dest
    lda #>value
    sta dest+1
}

.macro note(pitch: const, frames: const = 1) {
    .byte pitch, frames
}

.proc clear: a8, i8 {
    set16!(ptr, SCREEN)
    set16!({buf,x}, $1234)
    rts
}

.segment RODATA
.data tune {
    note!(C4, frames = 8)
    note!(E4)
}
```

### 11.1 What expansion may depend on

Expansion comes after every constant and shape has its value and before the flow
analysis (§3.1). It may read constants, through its arguments; nothing earlier reads an
expansion.

- **Parsing.** Invocation is marked with `!`, so a macro call is never mistaken for an
  instruction and an unknown mnemonic is a typo rather than a possible macro. A body is
  ordinary nt65 and parses on its own. The syntax of an argument never depends on the
  kind of the parameter it binds to; kinds are checked when names are resolved.
- **Names.** A body sees the scope in which the macro is declared. A symbol that an
  exported macro uses without receiving it as a parameter must be exported by its module, or
  come from another, which nt65 checks at the macro's declaration (§12). Names in a `block` argument resolve in the caller, including the caller's
  `@labels`. A macro cannot declare names in its caller: labels, constants and types in
  a body are local to each expansion, and to name what a macro emits, a `.data` block holds
  the call, `.data player_sprite { sprite!(...) }`, or inside a proc a label goes on the
  call line. Go to definition, rename and find references therefore work in and through
  macros without expanding them.
- **Constants and shapes.** No macro call appears in a constant, an enum, a struct or a
  union. What a call emits is measured by the `.data` block that holds it (§8), and a
  label, which is only a position, measures nothing. A typed record is an initialized
  `.type T { }` (§6.3).

A macro may not call itself, directly or through other macros. nt65 checks this from
the resolved names in the bodies, without expanding anything, so every expansion is
bounded; an expansion of more than 65,536 statements is an error all the same.
Variable-length argument lists, the usual reason for recursion in ca65 macros, are
`list` parameters.

An expansion depends only on the macro's definition, the values of its arguments and the
defines, so expansions are cached on those, and an edit re-expands only the calls whose
inputs changed.

### 11.2 Parameters and arguments

| kind | argument | in the body |
|---|---|---|
| `expr` (the default) | an expression, constant or address | an expression |
| `const` | a constant expression, checked at the call | an expression that a condition may test |
| `ident` | a name: `draw`, `gfx::init`, `@done` | a name, usable in an operand and as a `.next` or `.patch` target |
| `operand` | a braced operand, `{buf,x}`, or an expression | a whole operand (below) |
| `one(w, ...)` | one of the listed words | a word, compared with `==` and `!=` |
| `list(kind)` | every remaining positional argument, each of that kind | a list, for `.each` and `.countof` |
| `block` | a trailing block (§11.4) | a line naming it splices it |
| `const(lo..hi)` | a constant from `lo` to `hi` | as `const` |
| `operand(m, ...)` | an operand in one of the listed modes | as `operand` |
| an enum's name | one of its members, by its bare name or its path | the member's value |

- **Arguments are values, not tokens.** A parameter stands for its argument as a
  parenthesized whole, so `value * 2` with the argument `1 + 2` is 6, where ca65's textual
  substitution gives 5, and `#<value` with `label+1` is `#<(label+1)`.
- **Defaults and named arguments.** A parameter may have a default, `(count = 1)`, whose
  names resolve where the macro is declared. After its positional arguments a call may
  name parameters, `actor!(40, hp = 5)`; `=` never appears in an expression, so this is
  unambiguous.
- **Operands.** An argument is an expression unless it is braced. `{buf,x}`,
  `{(ptr),y}`, `{(ptr)}` and `{#$1234}` are operands; an unbraced `(ptr)` is the
  expression `ptr`, and passing one to an `operand` parameter is an error, since it reads
  as indirect addressing. So any addressing mode other than a plain address is braced.
- **An operand value is a mode and an expression.** In the body an `operand` parameter
  stands as a whole operand. It may be followed by `+ const` or `- const`, which applies
  to its expression: `dest+1` with `dest` bound to `buf,x` is `buf+1,x`. That is an error
  for immediate, accumulator, indirect and stack-relative modes, where "the next byte"
  has no meaning. An operand parameter may not be followed by an index or wrapped in
  parentheses. `.mode(p)` is the argument's mode as a word (`imm`, `acc`, `abs`, `absx`,
  `absy`, `ind`, `indx`, `indy`, `sr`, `sry`, `long` or `longy`), which a condition may
  compare. `.byteof(p, n)` stands where the operand may and is byte n of its value: for
  an immediate `#e` it is `#((e >> (8 * n)) & $FF)`, and for a mode that accepts
  `+ const` it is `p + n`. One macro then serves constants and memory alike:

```nt65
.macro mov16(dest: operand, src: operand) {
    lda .byteof(src, 0)
    sta dest
    lda .byteof(src, 1)
    sta dest+1
}

    mov16!(ptr, {#SCREEN})
    mov16!(ptr, other_ptr)
```

- **An operand's expression.** `.exprof(p)` is the expression inside the operand the call
  passed as `p`: `5` for `{#5}`, `ptr` for `{(ptr),y}`, `buf` for `{buf,x}`. It is an
  expression, so it stands wherever one may, a data directive included, and
  `.addrsize(.exprof(p))` tells a direct-page argument from an absolute one, which `.mode`
  does not. An argument written with a prefix is as wide as the prefix says: `{a:ptr}` is
  absolute wherever `ptr` is declared. That is what a macro needs to emit another
  processor's instructions as data, one macro per instruction rather than one per addressing
  mode:

```nt65
.macro mov_a(src: operand(imm, zp, abs)) {
    .if .mode(src) == imm {
        .byte $e8, .exprof(src)
    } .elseif .addrsize(.exprof(src)) == 1 {
        .byte $e4, .exprof(src)
    } .else {
        .byte $e5
        .word .exprof(src)
    }
}
```

- **Words.** A `one(...)` argument is a bare word: an identifier, a register or a
  mnemonic. It is parsed like any other argument and never looked up as a symbol; the
  call is checked against the listed words. A word may be passed on to a `one` parameter
  whose list contains it.
- **Lists.** A `list` parameter follows every other parameter except blocks and takes the
  remaining positional arguments. `.countof(p)` is a constant.
- **Refined kinds.** Three kinds may say more, and what they say is checked at the call,
  where the error names the parameter and signature help shows the limits. `const(lo..hi)`
  takes a constant in the range, whose ends are constants where the macro is declared; a
  condition that is not a range stays an `.assert` in the body. `operand(m, ...)` takes an
  operand in one of the listed modes, the words `.mode` gives with `zp`, `zpx` and `zpy`
  for the direct-page addresses among `abs`, `absx` and `absy`: `dp: operand(zp)` says an
  argument must be reached through the direct page. And the name of an enum as a kind takes
  one of its members. The argument may be the member's bare name, which the enum answers
  rather than the caller, as a `one` takes words, or its path; the body sees the member's
  value. A value that is not a member, even one equal to a member's, is an error, and so is
  anything a parameter of the same kind passes on that is not one. An
  argument a refined kind refuses is reported once, at the call, and the body is not
  expanded for it.

```nt65
.enum Instrument {
    kick
    snare
    bassguitar
}

.macro play(what: Instrument, note: const(0..127)) {
    .byte what, note
}

.macro load(src: operand(imm, zp, abs, absx)) {
    lda src
}

    play!(bassguitar, 24)
    play!(Instrument::snare, 36)
```

```nt65
.macro push(regs: list(one(a, x, y))) {
    .each regs, r {
        .if r == a {
            pha
        } .elseif r == x {
            phx
        } .else {
            phy
        }
    }
}

    push!(a, x, y)
```

### 11.3 Bodies

A body may contain anything a proc body may, apart from the items forbidden below, and a
macro may be called wherever its expansion could be written: one that emits only data
wherever data may go, one that emits code wherever instructions may. Whether an
expansion is code, data or both is a property of the expansion, not a declared kind of
macro, because nothing that runs before expansion needs to know it.

A `.next` or `.patch` directly after a macro call applies to the last statement of its
expansion, as it would to any statement above it (§7.4), and a `.next` there names targets
only where that statement is one whose successors nt65 cannot read. That is how a caller
annotates a macro that leaves data in the instruction stream:

```nt65
.macro skip2() {
    .byte $2c           ; bit abs: swallows the next two bytes
}

@set_one:
    lda #1
    skip2!()
    .next @store
@set_two:
    lda #2
@store:
    sta value
```

**Local declarations.** A body may declare labels, constants, scopes, enums, structs,
unions, charmaps and lists. Each is local to its expansion and can be named only in the
body. A macro's header resolves names where the macro is declared and a body cannot
export, so no local type or list can reach the caller, and a type declared in a body
never gives the caller a shape.

**Forbidden in a body:**

| item | why |
|---|---|
| a label or constant named by an `ident` parameter | it would declare a name in the caller |
| `.export`, `.use`, `.module` | other modules resolve names through the modules' exports, and a module's interface (§14) would depend on expansion |
| a segment declaration, `.segment X: zp` | the segment table is program-wide and declared exactly once; it would depend on how many times the macro is called |
| `.cpu` | the CPU is program-wide |
| `.proc` | inside a proc it would nest (§6.1); at item level it would need a name from the caller, and its signature is part of the file's interface. A wrapper is a block macro called inside a proc the caller declares |
| `.import` | redundant: a body resolves names where the macro is declared, and the output imports what an expansion uses (§12) |
| `.macro`, `.func` | a definition in a body could capture the enclosing macro's parameters, which would make definitions into templates, for no common use |
| `.fallthrough` | it is the last line of a routine's own body (§7.4), which a macro body is not; the routine that calls the macro says what it runs into |

`.macro` appears only at file level or in a `.scope` outside any proc. A macro declared
in a proc would see that proc's `@locals`, and an expansion in another
proc would branch into them, out of sight of the first proc's flow analysis (§6.2).

### 11.4 Blocks

A trailing block binds to a `block` parameter, which is how structured constructs are
built as library code rather than language features:

```nt65
.macro times_x(count, body: block) {
    ldx #count
@loop:
    body
    dex
    bne @loop
}

    times_x!(8) {
        sta (ptr),y
        iny
    }
```

A block argument may declare only cheap locals, and those are local to each place the
block is spliced. A macro may take several blocks: after the first, each is a
continuation line naming its parameter, `} name {`, and a block parameter may default to
empty, `= {}`. `.empty(p)` is true when a block argument has no statements, and a
condition may test it:

```nt65
.macro branch_unless(c: one(eq, ne, cs, cc), target: ident) {
    .if c == eq {
        bne target
    } .elseif c == ne {
        beq target
    } .elseif c == cs {
        bcc target
    } .else {
        bcs target
    }
}

.macro if(c: one(eq, ne, cs, cc), then: block, else: block = {}) {
    branch_unless!(c, @skip)
    then
    .if !.empty(else) {
        jmp @done
    }
@skip:
    else
@done:
}

    cmp #10
    if!(cs) {
        lda #0
    } else {
        inx
    }
```

### 11.5 State signatures

On the 65816 a macro may declare the processor state it expects and leaves, with
the items of a proc signature other than `near`, `far`, `inline`, `args`, `interrupt` and
`noreturn` (§7.3), and it may name a signature set, whose state it takes. Unlike a
proc's, a macro's items default to `*`: a macro assumes and changes nothing it does not
declare.

```nt65
.macro add16(dest: operand, value: operand): a8 {
    clc
    lda dest
    adc .byteof(value, 0)
    sta dest
    lda dest+1
    adc .byteof(value, 1)
    sta dest+1
}
```

With a signature, the analysis treats a call the way it treats `jsr`: the state must
match the entry and becomes the exit. Each expansion is checked against the signature,
with errors reported at the body line and naming the call, so a caller in the wrong
state gets "`add16!` needs `a8`" at the call rather than an unknown width inside the
body. A block spliced into a macro with a signature must leave the state as it found it.
Without a signature, an expansion is analyzed inline as the code it contains. On the
6502 and its CMOS variants, signatures on macros are accepted and have no effect.

This is the checked replacement for macros that test ca65's `.asize` and `.isize`. A macro
cannot see the widths at its call, because the widths come from a flow analysis of
expanded code.

### 11.6 Errors

A diagnostic lands on the side of the call that can fix it. A definition is checked for
what holds under any arguments: parsing, names, the forbidden items above, and how its
parameters are used. A call is checked for each argument against its parameter's kind: a
`const` that is not constant, a word not in its `one` list, an unbraced `(ptr)` for an
`operand`. What depends on a particular binding, such as `stx dest` with `dest` bound to
`buf,x`, is reported at the call with a note naming the line in the body.

A word a condition compares with what a parameter stands for is never looked up, so a
misspelt one would quietly never match. A comparison of `.mode(p)` with a word that is not a
mode, or not one of the modes `p` lists, and of a `one` parameter, or a repetition's binding
over a `list(one(...))`, with a word it does not list, is a warning at the definition: it
holds for no argument at all. An `operand(zp)` is `abs` to `.mode`, so that is what such a
parameter is compared with.

### 11.7 Output

nt65 expands macros itself and emits flat code, with a comment naming the invocation.
ca65's `.macro` is not used in the output, so nt65's macro semantics never depend on
ca65's.

## 12. Modules

Every file is a module, and says which first:

```nt65
.module gfx::sprite
```

A file without one is an error, a single-file build included, and two files may not be the
same module. A module's name may be a path, and a path is only a name: `gfx::sprite` needs no
module `gfx`, and has no special view into it or into `gfx::tile`. **A module is one file**,
and unless something places it (below) it is one ca65 translation unit. That buys what no
module spread over files could have: a private name is private to one module, where no other
can see it, and nt65 never chooses the order of two files' bytes in a segment, which the
program's placements or the build's link order state. A large module is split into
submodules, `hw::vic` and `hw::sid`, that share names by exporting them.

**Placement.** A program brought over from ca65 is often one translation unit assembled from
`.include`s, and some of what it includes lands in the middle of another file: a platform's
routine that the including file's code runs into, or bytes that have to sit where the
original put them. nt65 has no text inclusion, and says the same with `.place`, which puts a
module's bytes where the line stands without making it any less a module:

```nt65
.module iscntc: placed

.segment CODE

.if KIM {
    .export .proc ISCNTC {
        ...
        cmp #$03
        .fallthrough flow1::STOP    ; runs into STOP, which the placement puts next
    }
}
```

```nt65
.module flow1: placed

.segment CODE
.proc RESTORE {
    ...
    rts
}

.place iscntc                       ; this platform's check for control-C

.export .proc STOP {
    ...
}
```

`.place m` emits module `m` where it stands, in the output of the module that places it:
every item of `m`, in each segment `m` writes to, at that point in that segment, in `m`'s own
order. A module, what it places, and what those place are one translation unit, written as
one `.s` named after the module at the root (§13). Placement moves bytes and nothing else: a
placed module keeps its file, its names, its privacy, its exports and its interface, is
analyzed as itself, and is named by its path like any other module.

A module's declaration says whether it may be placed:

- `.module m`, as every module is unless it says otherwise, stands alone. Its output is its
  own, and placing it is an error that offers to mark it.
- `.module m: placed` is placed exactly once and has no output of its own. One that nothing
  places is an error at its declaration. This is the form for code that depends on where it
  lands, which is true of it wherever it is used.
- `.module m: placeable` is placed at most once, and stands alone when nothing places it: a
  module one program places and another links as an object of its own.

`.place` stands at file level, in a `.segment` region or before any and in no other block,
never under an `.if`, so which modules
share a translation unit follows from the files and not from the configuration. Code that
belongs to one configuration is a module that is always placed and whose items stand under
an `.if` of their own; in any other configuration it places nothing. A module is placed at
most once, placement forms no cycle, and what a module places does not touch its regions: the
line after a `.place` is in the region the line before it was.

Placement is what lets a routine run into another module's. A `.fallthrough` into another
module's routine (§7.4) requires the two to be in one translation unit, where nt65 lays out
every byte and checks that the target starts where the routine ends, as it does within a file.
A unit is laid out by the rule every file is (§5.2), not by one of its own: per segment, the
root's regions and every placed module's contributions joined in the order the unit writes
them, so a branch, a long branch and a `.fallthrough` read the same distances whether or not
anything places the module they are in.
Across a `.place` it is read in the segment the placing file is in at that line, as ca65 lays
each segment's bytes down in the order one `.s` writes them: the routine before the `.place`
runs into the placed module's first routine in that segment, the placed module's last routine in
that segment runs into what the placing file writes next in it, and what the placed module
writes to other segments in between does not stand between them. A `.fallthrough` across a
`.place` whose two ends are not both in that segment is an error that names the segments. Across
translation units the order is the link's, which nt65 does not know, so there the `.fallthrough`
is an error that names placement as the way to say it; that includes a `placeable` module that
nothing places in this program. A root module that places the rest of a program in order is the
nt65 form of a ca65 program built as one file of `.include`s: the program is one object, and its
layout is written in the source, with nothing left for a build to put in order.

Most programs need none of this. A module that owns its routines and ends each one is laid
out correctly by any link order, and placement is for the code that is not.

A module's symbols are private unless exported. `.export` goes before a declaration, or
lists names:

```nt65
.export BORDER = $D020
.export .proc init {
    rts
}
.export .data vectors {
    .data native: .addr[8]
}
.export fill_page, clear::again, K: abs, init as "_init"
```

The list form is for what cannot carry `.export` itself: an interior label (`.export again`
inside `.proc clear`, or `clear::again` outside it), a member, an address size and a linker
name.

- **Exporting a named scope** exports what it declares, through the named scopes inside it.
  It stops at a routine's interior labels, which are exported one by one, and never exports
  cheap locals. **Exporting `.data`** exports its address and its named members, to any depth;
  its `@` positions stay its own. **Exporting a type** exports its members as flat constants.
- **An export's size** is written `.export K: abs`, as an import's is. It may widen what nt65
  knows, zero page to absolute or far, and another module's uses are sized by the export; it
  may not narrow it.

**Names from another module** are written with the module's path, or brought in with `.use`,
and are never visible any other way:

```nt65
.module main
.use hw::init                       ; one name
.use hw::{BORDER, set_border}       ; several
.use hw::sid::*                     ; everything the module exports
.use snd::init as snd_init          ; a name of this module's choosing
.use very::long::path as p          ; a module, named p::thing

.proc main: a8, i8 {
    jsr gfx::init                   ; qualified
    jsr snd_init
    lda p::thing
    rts
}
```

A `.use` path always starts at the root of the modules. `.use` belongs at a module's top
level, and a macro from another module is called through a name `.use` brings in. A name is
looked for in this order:

1. the scopes around it, out to the module's top level;
2. what a `.use` names, explicitly or with `as`;
3. the defines;
4. the first part of a module's path, for a qualified name;
5. what a `.use module::*` brings in.

A local declaration beats a name a `*` brings in, and a name two `*` imports bring in is an
error only where it is used. A name a `.use` brings in explicitly may not also be declared
in the module; `as` renames one of them. So another module adding an export never changes
what a name here already means. A leading `::` starts at the root of the modules,
`::hw::init`, past any scope with the same name as a module. A module `hw::vic` and a name
`vic` that module `hw` declares cannot both exist, because a path to one reaches the other.

**Linker names are qualified by module.** An export reaches ca65 as its whole path joined with
`__`: `init` in `.module gfx::sprite` is `gfx__sprite__init`, and an interior label, a scope
member or a `.data` member continues the path (`hw__vectors__native`). `as` sets the linker
name exactly, which is how a name meant for C or hand-written ca65 gets one: `.export memfill
as "_memfill"`, with cc65's underscore written out. A name that is not exported is private to
its `.s` and keeps its local spelling (§13). Two exports that still meet under one linker
name, through `as` or an identifier containing `__`, are an error.

What another module's output does with a name depends on its kind:

- **address symbols**, labels and address aliases alike, become `.import`/`.importzp`
  under their linker names in the referencing module's output, sized from the export;
  a module that measures another's routine or data with `.endof` or `.spanof` imports the
  `f__end` label beside it, and the module that declares `f` exports that label for it — it
  is the one label nt65 generates that another file can see (§1), and it is exported because
  something measured it rather than because the source asked;
- **constants** whose value nt65 knows are emitted by value (`gfx__SCREEN = $0400`) in every
  module that uses them, because ca65 cannot use an imported symbol where it needs a
  constant (`.res`, `.if`, `.repeat`, `.sizeof`);
- **enums, structs, unions, charmaps, lists, functions and signature sets** are used by value: an
  enum member becomes a constant, a member offset or type size a number, mapped text bytes, a list
  its items, a function call its body and a set the items it stands for; **macros** are expanded
  in the referencing module.

**Re-exports.** A module may make names it did not declare part of itself:

- **An import** may be exported, `.export .import sp: zp`. Other modules use it as if they had
  declared the import, with its size, signature and checked value; each writes its own
  `.import`, the re-exporting module writes no ca65 export, and the `lderror` assertion of a
  checked import is written by each module that uses it, since each was built against the
  value.
- **Another module's name** is re-exported with `.export .use hw::vic::border`, which makes
  `border` part of this module's interface: users reach it as `hw::border`, and it keeps the
  linker name of its definition, so a re-export emits nothing. A facade module presents the
  names its submodules define this way. A re-export names what it re-exports, explicitly or
  in braces; `.export .use hw::vic::*` is an error, because a glob would grow the interface
  silently.
- **A macro** that is exported may name only what its module exports, or what another module
  does. One whose body names something its module keeps private is an error at its
  declaration: the body resolves names where it is declared, and an expansion in another
  module could not link. The name is not exported for it.

**The object file is the only boundary with ca65.** nt65 never reads ca65 source and
ca65 never reads nt65 source; everything the two share is a linker symbol.

Symbols and routines that live outside nt65 (hand-written ca65, cc65 output, ROM entry
points) are declared explicitly:

```nt65
.import _printf: proc(a8, i16)      ; a routine, with its signature (§7.3)
.import zp_scratch: zp
.import far_table: far
.import sp: zp .byte[2]             ; storage, with what its bytes are
.import actors: .type Actor[8]
.import VIC_BORDER = $D020          ; a constant whose value nt65 needs; checked at link
.proc CHROUT = $FFD2: a8, i8        ; a routine at a fixed address; emitted as a constant
```

An imported address may say which segment it is in, `.import spc_entry: abs in SPCIMAGE`, and
is then checked as a name declared there: against the segment's bank, its direct page and its
address space (§5.2, §7.5).

On the 6502 and its CMOS variants the signature of a `proc(...)` import or an extern proc may be
empty, because there is no state for it to declare. On the 65816 it may not (§7.3).

**A typed import** says what the bytes another object defines are, in the element types a
`.data` declaration is written with (§8): `.import sp: .byte[2]`, `.import actors: .type Actor[8]`.
What it says is what nt65 works with, exactly as a `proc(...)` import's signature is: `.sizeof`
and `.countof` answer from the element type and the count, an element is reached with `name[i]`,
a record's fields are reached through it, `actors[2]::hp`, and each of those is the address the
type works out, in the import's own address size. An element type carries no values — the bytes
belong to whoever defines them — and it may be written after a size, `.import sp: zp .byte[2]`,
which is where an import of zero-page storage says both what it is and where it lives. The
output is the same plain `.import` with the same address size: what the import says is checked
nowhere, as a signature is checked nowhere, and getting it wrong is getting the other object's
declaration wrong.

- **ca65 modules** export symbols in the usual way. cc65's runtime library already
  exports its zero-page variables, so `.import sp: zp` needs nothing more. An import keeps
  its own name to the linker.
- **Constants defined only in a ca65 include file**, such as hardware registers and
  struct offsets, reach nt65 two ways. Where the value has to stay the ca65 file's, a small
  ca65 module includes the file and `.export`s the names needed, and nt65 declares each as a
  checked import, below. Where the include file is a list of constants and nothing else,
  `nt65 import-inc` writes it out once as an nt65 module of constants (§5.3), which is then the
  module's own source. An import whose element type an import does not state is opaque to
  nt65: it can be an operand, sized by its import, but it cannot appear where nt65 needs its
  value (`.res`, `.repeat`, the `ranges` check of §7.5), and has no size to measure.
- **A checked import**, `.import NAME = value`, gives nt65 the value. nt65 uses `value`
  wherever `NAME` appears, and the output of each module that uses it imports `NAME` and
  asserts `NAME = value` with `lderror`, so ld65 fails the link if the ca65 definition differs.
- **An import is written only where it is used.** A module's output imports what its code and
  data name, and what the macros it calls name; an import nothing uses is not written, and a
  path imports what it leads to rather than what it walks through, so `jmp outer::inner`
  imports `outer__inner` alone. An import pulls the module that defines it out of an `ar65`
  library, so writing an unused one would link code nothing calls.
- **nt65 exports** are ordinary symbols to ca65: addresses, routines and constants, and
  for an exported enum, struct or union its members as flat constants (`gfx__Color__red`,
  `game__Player__hp`), and for a struct or union its size (`game__Player__sizeof`). Each export
  carries the address size nt65 uses, so a zero-page label or a constant below `$100` is
  exported with `.exportzp`, unless its export gives a size.
- **Macros do not cross** from ca65, and there is no `.include`. Definitions several nt65
  modules share, such as a machine's hardware registers, live in an nt65 module that exports
  them.

## 13. Transpilation

One module produces one `.s`, named after it: `.module gfx::sprite` is `gfx/sprite.s` under
the project's `out` (§5.3), with its line map `gfx/sprite.s.lines` beside it. A translation
unit of several modules (§12) is one `.s`, named after its root, with each placed module's
items written where its `.place` stands and a comment naming the module and its source above
and below them. A reference between two modules of one unit needs no import: the name is
defined in the same file. An export is written as it would be anyway, since code outside
nt65 may name it. The output is
readable ca65 with a header comment and source spellings preserved where possible: it is the
program and nothing else, with everything a debugger needs in the map.

**Its segments are in the order the source writes them.** Each region and segment block is
written as a `.segment` line where the source has it, and ca65 appends what follows to that
segment's bytes, so each segment's bytes come out in the order the text writes them. That is
the layout nt65 reads distances from (§5.2): a `.fallthrough` it accepted, a branch it measured
and a long branch it made short are what ca65 assembles, across regions as within one.

**It is laid out as ca65 is written, not as the source was.** Names stand at the margin and
what they hold is indented once, which is the two levels hand-written ca65 has; the body of a
folded `.repeat` takes one more, because that is a block the output does hold. Keeping the
source's own indentation would step the output in past `.proc`, `.scope`, `.enum` and
`.struct` blocks that are no longer in it, leaving a run of constants indented under nothing.
A run of named data lines is lined up on its directives — `ptr:` and `frame:` become `ptr:`
and `main__frame:`, so what the source lined up no longer does — and a number is written in
lower case whatever case the source wrote it in, because everything nt65 works out for itself
is lower case and one file in two hands reads as two.

**What a block was is a comment, because ca65 cannot hold it.** A routine is a label and its
body: ca65's `.proc` is a scope as well as a label, and the output takes none (§13, flat
names), so the `.proc` line is written above the label as a comment, with the file and line it
came from. That is the only place a routine's signature appears at all — it emits nothing to
ca65 — and `; end of f` after the body says where the routine stopped. A macro expansion is
closed the same way: it opens with a comment naming the call and nothing else in the output
says where the caller's own code starts again. It is deterministic: the same sources and
configuration give byte-identical output, and `nt65 build` rewrites a file only when its
contents change.

**The header makes the output independent of ca65's command line.** It sets the CPU,
turns smart mode off, makes symbols case-sensitive, and switches off every ca65
`.feature` that changes syntax (`addrsize` is deprecated and always on, and resetting
it would itself warn). Options such as `--feature bracket_as_indirect` (which would
silently turn `lda [dp],y` into `lda (dp),y`), `--smart`, `-i` and `--cpu` then have no
effect. Each CPU is set as the ca65 CPU with exactly its instructions (§5.1): a `65c02`
program as `W65C02`, whose instruction set includes `wai` and `stp`, an `r65c02` one as
`65C02` and a `65sc02` one as `65SC02`:

```ca65
.setcpu "65816"
.smart -
.case +
.feature at_in_identifiers -, bracket_as_indirect -, c_comments -
.feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -
.feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -
.feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -
.feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -
```

ca65 rejects a `.feature` name it does not know, so this list ties the output to a
particular ca65. cc65 has not tagged a release since 2.19 but keeps changing on GitHub,
so a version number does not say what ca65 accepts, and nt65 pins a commit instead:
ca65 and ld65 built from cc65 commit `e11fb5c39371046ebe25485f984f644c5a0d65d3`
(2026-08-20). nt65's tests run against that build. The pin moves forward deliberately,
and this list is revisited when it does. The rest of
the output is written for the options the header cannot reach: character and string
data is bytes, so `-t` cannot translate it; defines are values, so `-D` cannot collide
with them; and every `.segment` carries its address size, so a memory model (`-mm`) that
disagrees with the segment table is a ca65 error naming the segment rather than a change
of addressing modes.

**Names in the output are flat.** The output contains no ca65 `.proc`, `.scope`,
`.enum`, `.struct` or cheap local labels, so it never depends on how ca65 resolves
names. An export is its linker name (§12), its path with its module's in front,
`gfx__clear`, or the name its `as` gives. A top-level name that is not exported keeps its
spelling and a scoped name `outer::inner` becomes `outer__inner`, except that a module
another places writes each name it does not export with its module in front, `iscntc__loop`
and `iscntc__outer__inner`, so that two modules' private names never meet in one file. The
end label behind
`.endof(f)` is `f__end` after whatever `f` is written as; these spellings are fixed, because
other modules and hand-written ca65 refer to them. Cheap
locals, labels from macro expansions and labels from `.repeat` iterations get names
derived from the source, such as `draw__loop` and, for a second `@loop` in the same
proc, `draw__loop_2`; the same source always yields the same names. An instance of a family is
an ordinary routine, `play__triangle`, written under a comment naming the line it came from and
which instance it is, and what its body declares is named after the instance,
`play__triangle__loop`. An import keeps its
linker name, so a local name that would collide with one is renamed in the output;
this happens when an exported macro expands in a module that has its own symbol of the
same name. A fixed spelling (`outer__inner`, `f__end`) that collides with another name
is an error. A label
named `z` or `f` is written `z := *`, because ca65 reads `z:` at the start of a line as
an address-size prefix; references to it need nothing special. An assignment takes the
whole line, so anything that followed such a label goes on the next one.

**A name ca65 would read as an instruction is written with its module in front.** No
mnemonic is reserved in nt65 (§4), and ca65 takes any word of its own instruction table at
the start of a line for an instruction, whatever else the file says that name is — including
the alternative spellings it keeps and nt65 does not (`swa`, `tad`, `dea`, `ina`). So a
top-level name that is not exported and that ca65 would misread under the `.setcpu` nt65
wrote is written `main__swa`, which is the spelling an export already has. Nothing else
moves: a scoped name and an export already hold a `__`, which no word of ca65's does. The
words come from ca65's own tables rather than from nt65's idea of the processor, so the two
cannot drift. The one name with no spelling to fall back on is an `as` name, which is
written into the output exactly as it is given: `as "lda"` is an error where the `as` is,
checked against every CPU nt65 writes a `.setcpu` for, because the name is what a module
built for another one links against.

**A prefix binds to the whole operand.** `z:ptr+1` sizes the expression, not just `ptr`.
Where an operand expression itself begins with `(`, as `lda (hi + lo) * 2` may (§7.1),
the output writes `z:+(hi + lo) * 2`: ca65 reads a `(` straight after a prefix as an
indirect operand, and a unary `+` keeps it an expression without changing what it is
worth.

**Debug information is beside the output, not in it.** ca65 has one way to say that a
generated line came from somewhere else, a `.dbg line` directive before the line itself, and
nt65 would need one before nearly every instruction it writes: a third of the output,
standing between every two lines of every routine. The point of writing ca65 rather than
object code is that a person can read it, so what the output would have said is written
beside it instead, and put into the debug file after the link.

Each `foo.s` is written with a `foo.s.lines` next to it, the module's *line map*. It holds
one record a line: `version`, the format's, which is checked; one `file` per source the `.s`
was written from — its root's, and each placed module's (§12) — naming it by its path from the
project root and its size in bytes, so that a debugger can tell the source has changed under
it; and one `line` per line of the `.s` that produces bytes, saying which
line of which source it came from. A line that produces no bytes gets no record: ld65
attaches a span to whichever line is in effect while bytes are generated, so a record for a
label or a constant would cover nothing, which nothing can step to or break on, and a label's
address is that of the bytes after it either way. The lines of a macro expansion map to the
line of the call, the way C debuggers treat preprocessor macros, and a comment naming the
call precedes the expansion. A module that produces no bytes at all — one of nothing but
constants — is written with no map.

The map is put to work after the link. `ca65 -g` records the lines of the `.s` it assembles
and ld65 copies them into the file `--dbgfile` names; `nt65 remap-dbg game.dbg` then reads
that file, finds the `foo.s.lines` beside each `.s` it names, and adds what they say: a
`file` record for each source, one `line` record per source line carrying the spans of every
generated line that came from it, recorded as an external source line as cc65 does for C, and
that line on each `sym` defined or used there. Only a module's `file` changes, to the source
it was written from, because it names one file and the one worth naming is the source; for a
translation unit of several modules, that is its root's, and the lines name the rest.
Everything that was there stays, so a debugger that was showing the generated ca65 still can.

It needs nothing but the debug file, which names every `.s` the program was built from, and
takes the paths as ca65 recorded them, which is from the directory a build runs in — where a
debugger reading the debug file also looks. A `.s` with no map beside it, which is anything
hand-written that the same program links, is left alone; that is also what makes running it
twice do nothing the second time. Skipping it entirely leaves a debug file that refers to the
generated ca65, which is what ld65 wrote and is still true.

| nt65 | ca65 |
|---|---|
| file header | `.setcpu`, `.smart -`, `.case +`, every `.feature` switched off |
| each generated line that produces bytes, and each `.assert` ca65 evaluates | nothing in the output; a `line` record in the map beside it, naming its `.nt65` file and line. ld65 reports imports, exports and link-time assertions at the `.s` line whatever the map says, so those get none. A folded `.repeat` counts the whole block against its own line, which is where ca65 counts it too, and that line names the repetition in the source; the lines of the body name where they came from and make no bytes of their own, and the `.endrepeat` names the brace that closed the body, because ld65 records a span for the whole block against it |
| `a == b`, `a != b`, `a ^^ b` | `a = b`, `a <> b`, `a .xor b`; nt65's other operators are ca65's |
| `.segment X: zp` declaration | nothing by itself |
| `.segment X` region, `.segment X { }` at file level | `.segment "X": zeropage`, `absolute` or `far`, from the segment table ... (next segment) |
| nested segment block | `.pushseg` / `.segment` ... `.popseg` |
| `.proc f: a16, i8 -> a8, i8 { }` | `; .proc f: a16, i8 -> a8, i8  file:line`, then `f:` and the body, then `; end of f`. The signature emits nothing to ca65, so the comment is the only place it appears |
| `.proc CHROUT = $FFD2: ...` | `CHROUT = $FFD2` |
| `.scope n { }` | its contents, with names flattened (`n__name`) |
| `@name` | a generated name, unique in the file |
| `lda ptr` | `lda z:ptr` (size made explicit) |
| `lda d:$2105` | `lda z:$05`, from the known D (§7.5) |
| `jeq t` | `beq t`, or `bne` over `jmp t` to a generated label (§7.6) |
| a width-dependent immediate (65816) | preceded by `.a8`/`.a16` or `.i8`/`.i16`, unless the previous immediate for that register had the same width (§7.3) |
| `.next`, `.fallthrough`, `.patch`, `dp =`, `bank =`, `inline` | nothing; they exist only for the analysis |
| `.state` | nothing; the widths it establishes size later immediates (§7.3) |
| `.ensure a16, i8` | the `rep` or `sep` the analysis requires there, or nothing |
| `.frame`, `locals::count,s` | nothing; the operand is its offset, with a comment naming the path |
| `brk #s`, `cop #s` | ca65's immediate form where the CPU setting accepts it, else `brk` and `.byte s` |
| `wdm #n` | `.byte $42, n` |
| `mvn #s, #d` | `mvn #s, #d` |
| `.if c { } .else { }` | resolved at transpile time; only the chosen branch is emitted |
| `.repeat n, i { }`, `.each l, v { }` | unrolled; what the turn is worth is written into each line, and the name the turn is bound to is not repeated down them as a comment. A counted `.repeat` of three turns or more whose body names nothing is then written back as one block, indented a level: `.repeat n` where the turns came out as the same lines, and `.repeat n, i` around the body once with a counter of the output's own where they differ only in the number the binding was worth, which is checked by putting every turn's number back and finding the line that turn was written as. Everything else stays unrolled |
| `m!(...)` | expanded inline, between `; m!(...)  file:line` and `; end of m!`; its lines map to the call's line (debug information) |
| `.enum Color { }` | a constant per member, `Color__red = 0` |
| `.struct`, `.union` | nothing by themselves |
| `.data name: .word[16]`, `.type T[n]`, any element type with no values | `name:` and `.res` of the total size; a record whose type pads with something other than zero is written a member at a time, and a member's padding as `.res n, fill` |
| `.data name: .byte 1, 2`, `.byte[] { … }` | `name:` and the element type's directive, a body a line at a time with its repetitions unrolled, and a run of one repeated byte gathered back into the `.res n, value` that says the same thing |
| `.type T { ... }`, `.type T[] { ... }` | a data directive per member of each record, each with a comment naming it |
| `.data name { }` | `name:` and its contents; a member `name::sub` is `name__sub`, and an `@` position gets a generated name |
| `.list` | nothing by itself; its items where it is used |
| a `.func` call | its value where nt65 has one, else its body, with each parameter replaced by its parenthesized argument |
| a `.func` call, `.strsub`, `.strcat` or `.select` that is text | its bytes, with the source expression in a comment, as a literal's are |
| `Player::pos::y`, `player::hp` | `2`, `player+4`, each with a comment naming the path |
| `'c'`, `"text"`, `screen("HELLO")` | byte values, with the source text in a comment |
| `.strz "s"`, `.strz TEXT` | `.byte` with those values and a terminating `$00`: the text is bytes by then |
| a text constant | its bytes where it is used, and nothing where it is declared |
| a negative constant in a number slot | its two's complement at the slot's width, with the source in a comment |
| `.long`, `.beword` | `.faraddr`, `.dbyt` |
| `.belong`, `.bedword` | `.byte` with the bytes high first: the values of a constant, or `.bankbyte(e)`, `.hibyte(e)`, `.lobyte(e)` |
| `.select(c, a, b)` | the chosen value |
| `.assert c, "m"` that nt65 cannot answer | `.assert c, lderror, "m"` |
| `.config`, `.warning` | nothing; a setting is written as its value where it is used |
| `.endof(f)`, `.spanof(f)` | `f__end`, `(f__end - f)`, with `f__end:` after the last byte of `f` |
| `.export s`, `.export .proc s {` | `.export m__s`, `.exportzp m__s`, `.export m__s: far` or `.export m__s: abs`, in module `m`, with nt65's address size or the export's |
| `.import N = v` | `.import N` and `.assert N = v, lderror, ...`; uses of `N` are emitted as `v` |
| `.incbin "f"` | `.incbin` with the path made relative to the output file |
| `.export .struct T {`, `.export .union T {` | its members as constants, and `m__T__sizeof`, its size |
| `.module`, `.use`, `.export .use` | nothing |
| `.place m` | `m`'s items where the line stands, in each segment it writes to, between `; .place m  file:line` and `; end of m`, and the placing file's segment written again after them where `m` left another |
| reference to another module's address | `.import m__s` or `.importzp m__s` in the referencing module, or an import's own name |
| reference to another module's constant, enum, struct, charmap, list, function or macro | emitted by value, or expanded in the referencing module |
| `NAME = expr` | `NAME = expr`, for a constant or an address alias, written where it stands and opening no segment; one using `*` is in its segment |
| a define | its value |

**The C header.** `nt65 build --c-header nt65.h` writes what the program exports as C for
cc65, so C and nt65 share one declaration of each type rather than two kept in step by hand:

- a struct or union as a C struct or union, member by member in cc65's types, with
  `_Static_assert` on the size nt65 gives it: `.byte` is `unsigned char`, `.word`
  `unsigned int`, `.addr` `void*`, `.dword` `unsigned long`, a record member its type, and a
  width C has no integer for (`.faraddr`, `.long` and the big-endian types) an array of bytes;
  an array member is an array;
- an enum as a C enum, and a constant as `#define`;
- a data declaration as `extern`, an array sized by its count, and bytes where it holds text,
  binary data or mixed data;
- a routine, or an exported interior label, as `extern void name(void);`. What it takes and
  returns is the programmer's to declare, and C does not allow a second prototype that
  differs, so each is skipped when `NT65_OWN_name` is defined before the header is included.

Every C name is the symbol's linker name, without the `_` cc65 puts before a C name. A routine
or data declaration exported without one, which C cannot name, is left out with a warning
saying to export it `as "_name"`, and data of a type that is not exported is declared as
bytes, with a warning. A linker name holds `__`, which C reserves to the implementation; cc65
does not mind, and the alternative is a second spelling of every name for one compiler's
opinion of a name it never sees.

### Example

`main.nt65`:

```nt65
; Fill four pages of screen memory with spaces, forever.
.module main

.cpu 6502

SCREEN       = $0400
SCREEN_PAGES = 4

.export fill_page

.segment ZEROPAGE
.data ptr:    .word             ; destination pointer
.data frame:  .byte

.segment CODE
; Fill 256 bytes at (ptr) with A.
.proc fill_page {
    ldy #0
@loop:
    sta (ptr),y
    iny
    bne @loop
    rts
}

.macro set16(dest: operand, value) {
    lda #<value
    sta dest
    lda #>value
    sta dest+1
}

.proc main {
    set16!(ptr, SCREEN)
    ldx #SCREEN_PAGES
@page:
    lda #' '
    jsr fill_page
    inc ptr+1
    dex
    bne @page
    inc frame
    jmp main
}
```

`main.s` (generated):

```ca65
; Generated by nt65 from main.nt65. Do not edit.
.setcpu "6502"
.smart -
.case +
.feature at_in_identifiers -, bracket_as_indirect -, c_comments -
.feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -
.feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -
.feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -
.feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -
.export main__fill_page
SCREEN       = $0400
SCREEN_PAGES = 4
.segment "ZEROPAGE": zeropage
ptr:   .res 2
frame: .res 1
.segment "CODE": absolute
; .proc fill_page  main.nt65:17
main__fill_page:
    ldy #0
fill_page__loop:
    sta (ptr),y
    iny
    bne fill_page__loop
    rts
; end of fill_page
; .proc main  main.nt65:33
main:
    ; set16!(ptr, SCREEN)  main.nt65:34
    lda #<SCREEN
    sta z:ptr
    lda #>SCREEN
    sta z:ptr+1
    ; end of set16!
    ldx #SCREEN_PAGES
main__page:
    lda #$20                        ; ' '
    jsr main__fill_page
    inc z:ptr+1
    dex
    bne main__page
    inc z:frame
    jmp main
; end of main
```

## 14. What tooling gets

Because declarations are a set and syntax is fixed, a language server can, from source
alone and without an assembler:

- parse any file in isolation and report syntax errors per line;
- resolve every reference (go to definition, find references, rename, including inside
  macro bodies and block arguments, without expanding a macro, and through qualified names,
  `.use`, `as` and re-exports: a rename across modules rewrites the `.use` items that name
  the symbol, and leaves a name `as` gave alone), the member names a record gives values
  included;
- colour every name by what it refers to, so `Joy::A` is an enum member and not a register. A
  long file is asked about a screenful at a time, and after an edit only what changed about it
  is sent: a file of thousands of lines is thousands of numbers, and a keystroke moves a
  handful of them;
- widen a selection through the tree, a step at a time: the operand under the caret, the
  instruction written around it, the block that holds the line, the routine that holds the
  block, and the file;
- show on hover the line that declares a symbol, as the language writes it, and under it its
  value, how wide an address it is, the segment it sits in and how many bytes it takes, what
  the output calls it where that is not what the source calls it, and with them the comment
  written above the declaration. There is no doc-comment syntax of its own: the
  `;` lines directly above a declaration, each on a line of its own, are what its author had
  to say about it, and a blank line or a line of code between ends them. Every instance of a
  family is declared on the family's line, so each of them shows the family's comment. A
  hover is read from the top down, and is ordered so that it can be: the declaring line, the
  comment, the one or two facts that kind of thing is pointed at for — a constant's value, a
  member's offset, what a call to a routine costs and which registers it hands back, which
  module a name came from — then a rule, and everything else the analysis worked out under
  it. Nothing is left out for standing far down: the first screenful is the answer and the
  rest is the working;
- say who calls a routine and what it calls, across modules, from the edges the cycle counts
  are already worked out over: a call, a tail jump, a `.next` under a call, and a `per` and
  branch pair (§7.4). A call nt65 cannot follow is in no list, because there is no routine to
  put in one;
- link the path an `.incbin` writes to the file it names, resolved beside the file that
  writes it, as the build resolves it;
- lay a file, or the lines a selection covers, out in the one layout (§4), from the same code
  `nt65 fmt` runs, so what the editor writes on save and what a gate checks are one thing. The
  whole file says where a line goes even when only part of it is asked about, because a run of
  data lines says together where its column is;
- diagnose wrong-CPU instructions, unavailable addressing modes, references to what another
  module does not export, unused symbols, and constant assertions;
- report every one of them under a name of its own. Every diagnostic nt65 has is in one
  catalogue, with the name it is reported under, how much it matters where a project says
  nothing, the sentence it says and a sentence about it the message has no room for. A name is
  kebab-case and says what is wrong rather than which pass found it — `unused-symbol`,
  `width-unknown`, `branch-out-of-reach` — and it is what the editor shows as the diagnostic's
  code, what `--json` writes as its `id`, what `nt65 explain` answers about, and what
  `nt65.json` switches (§5.3);
- on the 65816, diagnose width, mode and near/far mismatches at calls and returns,
  immediates reached with unknown width, every unannotated construct of §7.4, and
  direct-page and bank mismatches against declared segments and ranges (§7.5);
- report out-of-range branches and per-block cycle intervals before ca65 runs (§7.6), and
  show above each routine, and each inline `.scope` block of one, as a lens on the line that
  opens it, what one pass through it costs and what it costs with everything it calls, and
  which registers the routine hands back as it was entered with them (§7.7);
  an instruction's own cycles and why they are an interval, its block's, the name its
  datasheet gives it, the flags it writes, and on the 65816 the state reaching it, are on
  hover rather than in the line; what it costs and what state reaches it lead, and the flags,
  the registers and the stack stand under the rule;
- complete what may be written at the caret, and only that: the statements the place the
  caret is in accepts, so that a file's top level offers declarations and only code offers
  instructions, labels and what they say about the processor; the forms an instruction has
  on the program's CPU, and the registers that index them; after `::` the names a path leads
  to; in a `.use` the modules and what they export; in an operand or an expression the names
  in scope, what `.use` brought in, the built-in functions and how a number that is not plain
  digits is written; in a signature or a `.state`
  its items and the signature sets; in a macro call its parameters as named arguments; and
  nothing at all inside a comment or a text. It also shows, inside a macro call, a `.func`
  call or a `.select`, what it takes and which argument the caret is in. What is offered is
  ordered by how near it is: the labels of the routine the caret is in, then the file's names,
  then what `.use` brought in and the other modules, then the words the language spells, and
  the instructions last, alphabetical among themselves, because the thing meant is nearly
  always the nearest. A directive that opens a block is written as the block, with the name as
  the first stop and the body as the last, and on the 65816 a `.proc` stops on the signature
  the program's other routines mostly start with; nothing else is written with stops, because
  `lda ${1:operand}` fights the typing of someone who knows what they are writing, which in
  assembly is everyone. Nothing has a commit character: a completion that accepts itself on a
  `,` or a space, in a language where both follow a name on most lines, is wrong more often
  than it is right. The comment above a declaration is fetched for the one item the caret is
  on, so a list of hundreds is not mostly prose nobody is reading;
- find a declaration anywhere in the workspace by name;
- fix what a diagnostic names as its fix: a `.next ?` where the analysis cannot follow a
  transfer, a `.fallthrough` naming the routine written next where a routine runs off its end (a
  `.next ?` where nothing is known to be next), `.fallthrough` for a `.next` that ends a
  routine's body after a statement nt65 can follow, `jsl` for a `jsr` to a far routine and the
  other way, the long branch where a short one cannot reach, `rti` where an interrupt handler
  returns, the missing `.export` in the module that declares a name or the `.use` that brings it
  in, a `.state` after a label flow may reach unseen, saying what the analysis finds reaching
  it, a label outside a routine, with the data under it, as a `.data` declaration, a label in
  mixed data as a member of it or a position in it, the nt65 spelling of a ca65 directive, the
  declared name a misspelling is within a letter or two of, ca65's assertion level dropped, a
  `.res` as the `.byte[n]` that reserves the same room, an export widened to the address size it
  exports, the width item a routine assumes written into its signature, and the declaration or
  `.use` item nothing names, taken out or exported. Where the fix is a name nobody but the
  programmer can give — a name ca65 would read as an instruction (§4) — nothing is written: the
  caret goes on the name and a rename starts. Where a line has two readings — an expression that
  needs parentheses, a width the analysis cannot work out — each is offered and none is
  preferred, because which was meant is the programmer's to say;
- rewrite what is asked for at a selection, which nothing reported: a path written out in
  full brought in with a `.use` and one brought in written out in full, the `.use` items
  ordered with what nothing names gone, a declaration exported or no longer exported, what a
  routine leaves declared from what the analysis finds at its returns, a `rep` or `sep` that
  only changes the widths written as the `.ensure` that says what it is for and back, a
  number given a name, a label given a name of its own or made cheap, a declaration a routine
  owns put in a segment block, selected instructions lifted into a routine of their own with
  a call left where they were, the state they were written under declared and the label they
  start with as the name to be going on with, and ca65 in a selection read as nt65 as far as
  one line at a time can say it;
- show what a file became, beside the file: the ca65 a build writes for its module, as the
  program stands in the editor with whatever is not saved yet, and the caret is the link both
  ways — moving in the source shows the lines it became, and moving in the output shows the
  line that wrote them. Both come from the map the build already writes (§13), so nothing
  stands in either text to say it. It opens past the header, which is there for ca65 and not
  for the reader, and it follows the program on the same wait the rest of the squiggles come
  on, because it is about the program rather than about the caret. A file with errors shows
  what could be written, under a first line saying that it is incomplete and where the first
  thing wrong with it is; that is what a build would have written, and a build writes nothing.
  `nt65 build --stdout` (§5.3) writes the same text, so that what the editor shows and what a
  script reads are one thing;
- write a macro call out as what it expands to, in nt65 rather than in ca65: the body with the
  arguments in place, as the programmer would have written it by hand (§11). Hovering over a
  call says what it becomes in one line — how many lines, how many bytes and what it costs —
  and then the first eight lines of it, with a link to a view holding the rest. What the body
  decided from its arguments is decided in what is shown, because the text has no way to leave
  it open: an `.if` over a `one` parameter is the branch it takes, and an `.each` over a `list`
  parameter is its turns, since a call's arguments cannot be written as a list. A call inside
  the body stays a call, with a link of its own: one level at a time, because a fully written
  out nest of macros is unreadable and nobody wrote it. The same text replaces the call where
  the programmer asks for it, which is how one stops using a macro; it is refused, with the
  reason, where writing it there would change what the line means — where the macro is another
  file's, since a body's names are resolved where it is written, and where the call is itself
  in a body, since its arguments are not known until that body is expanded. A body that
  declares anything is written out inside an anonymous `.scope`, since each expansion has its
  own locals (§6.2) and two of them in one place would declare the same name twice, which is
  also what keeps *Expand all* valid where one body writes another out more than once;
- change what moving a file asks the program to change, before the move rather than after it.
  A module's name is written in its `.module` line and its output is named after that wherever
  the source is (§13), so a source that moves changes less than it looks: nothing in any other
  file names it. Two things do. A `files` entry that names it literally has to name it where it
  now is; one that is a glob either still matches, in which case there is nothing to do, or
  stops matching, and which glob was meant to cover it is the programmer's to say, so that is
  said and not rewritten. And an `.incbin` path is resolved beside the file that writes it, so
  it is rewritten when either end moves. Renaming a **module** is a rename of the name in its
  `.module` line, which already rewrites every `.use`; it does not move the file, and moving
  the file does not rename the module. The edits name the revision of each file they were
  worked out against, where the editor takes that;
- report on every file of every program, not only the ones that are open: a broken export is
  wrong in each module that named it, and none of them may be open. What is wrong with the
  project file is published for it too;
- run incrementally: editing one file re-parses one file; only resolution is global.

**What stands in a line, and what does not.** Everything the analysis works out about a line is
on hover, and five things are drawn in the line itself. Each of them marks a change rather than
a state, because the width of A is worth saying on the line where it becomes sixteen and noise
on the forty lines after: a line after which a width, the emulation flag, the direct page or
the data bank differs, and only the parts that differ; a branch layout had to write as the
opposite branch over a `jmp`; a value a declaration does not write, which is an enum member
given none, where a struct or union member lands, and what a constant written as a sum comes
to; the parameter a positional argument of a macro or `.func` call is for, where the argument
does not name it already; and what an instruction costs, with a block's total on the label that
opens it. A line carries at most one of them at its end, so where two would land the state
change wins and the other is under its sentence; twelve characters is as long as one gets; and
each says what it means in a sentence, with the declaration that decided it where there is one.

The counts are off until they are asked for and the rest are on, because a file opened for the
first time should look like the file: what is drawn by default is what is rare and surprising,
and what is on nearly every line is switched on for as long as the server runs by one command
rather than found in a settings page. Each kind has a switch of its own, named for what it
shows. What is deliberately not drawn is what would stand on nearly every line and says nothing
a reader could not have asked for: the address size chosen, which the declaration decides and
hover gives; how many bytes a line takes, which hover gives; what the registers hold, which
hover shows as a block a reader can run an eye down; and an address, which nt65 never knows,
because the linker decides one. Only the lines the editor is showing are worked out, and the
editor is asked to fetch them again when a setting changes or when an edit reaches past the
file it was made in.

**Projects in the editor.** Every `nt65.json` in the folders the editor opened, and in the
folders beneath them, is a project, and each is its own program. A file belongs to the
project whose `files` name it, whether or not it is saved yet; a library two projects share
belongs to both, and is shown as the nearest one analyzes it; a file no project names is part
of a program of the other such open files. A project file, a source nobody has open, or a
file an `.incbin` measured changing on disk is read again, and what is wrong is published
again. What is published is every file of every project, from the moment the editor connects,
and a file is sent again only where what is wrong with it changed, so that an edit in a
program of hundreds of files costs one message and not hundreds. The folders the editor has
open are the workspace: a folder added to it brings whatever projects are in it, and one
taken out takes its projects with it. A single `.nt65` file opened with no project anywhere is
served like any other: it is a program of the open files no project names, and everything the
editor asks about it is answered from that.

**What the editor watches.** The sources and the project files, which it can know about
without reading anything: `**/*.nt65` and `**/nt65.json`. The files an `.incbin` measures are
the program's to name and are known only once it has been read, so the server asks the editor
to watch those once it has read it, and asks again whenever the set changes; an editor that
cannot be asked watches the first two and hears about a binary when something else changes.

**What a keystroke publishes, and when.** A squiggle never flickers, and is never about text
that is gone. Four rules keep that:

- the edited file's own diagnostics go out at once, from the analysis that edit asked for.
  They are the whole answer about that file, program-wide problems with it included, so
  nothing about it is briefly missing;
- the rest of the program's go out after 200 ms with no further edit. An edit in one file can
  change what is wrong with another, and a squiggle in a file nobody is looking at is worth
  arriving a moment late rather than coming and going on every keystroke;
- a file's diagnostics stand until the ones that replace them arrive. A file that is still
  part of the program is never emptied and then filled in again; only a file that has left one
  is emptied;
- nothing is published about a revision of a file older than the newest the editor has sent.

The editor is asked to fetch the names' classes and the lenses again only where the edit
reached past the file it was made in, which is exactly when what a name in another file refers
to, or what a routine costs with its calls, can have moved. The edited file is never among
them: the editor asks about the document it is showing by itself.

**What the editor says it can take** is read once, when it connects, and everything that
depends on it is settled from that: whether the outline is a tree or the flat list the
protocol had first, whether an edit carries the revision each of its files was worked out
against — so that one worked out against a buffer that has since moved on is refused rather
than written into the wrong place — whether a completion may write a whole block with stops in
it, whether the editor will say when its folders change, and whether it can be asked to fetch
what it holds again. An editor that says nothing is given the plain answer, which every editor
understands. A position is a line and a count of UTF-16 code units into it, which the server
says so that an editor that would rather count differently knows not to.

**The server's life.** Nothing an editor sends ends the server. A frame whose body is not
JSON is answered as a parse error and the next frame is read; a request that arrives before
the editor has initialized the server, or after it has shut it down, is answered as that
rather than acted on, and a notification at either point is dropped. A request the person has
moved on from is cancelled, and a cancelled request stops at the next file of the program
rather than finishing. `exit` ends the process: cleanly where the editor shut the server down
first and as a failure where it did not. An editor that crashes never says goodbye, so the
server watches the process the editor named as its own when it connected, and leaves when that
process does.

**Unused symbols** are warnings: a label, constant, macro, struct, union, enum, routine or data
declaration that nothing names and the file does not export, since an export is what another
file uses. A member of a named enum is one of a set and is not reported on its own, a label a
`.state` declares an entry point is reached from outside, and a label flow analysis reports as
never reached is not reported twice. A routine nothing calls, jumps to or names — in data, in a
`.next` or a `.fallthrough`, anywhere — and that the file does not export is a routine nothing
can reach, which is what a finished port has left behind; a handler is the exception, because
the processor reaches it through a vector this program may not even hold, and an `interrupt`
signature is what says so. Data that holds values may be there for where it lands, as a header,
the vectors or a load address are, so only a declaration that reserves storage and holds no
values is reported. A name written in a branch the configuration leaves out counts as used,
because the other build uses it, and a file with errors gets none.

A `.use` item that brings in a name the file never writes is reported the same way, on the
name the item writes rather than on the whole line, so that one item of several in braces is
the one reported. A `.export .use` re-exports rather than uses, and what another module wants
of the name is not this file's business; a `.use module::*` brings in whatever that module
exports, which is no question about this file either, so neither is reported. Everything
reported as unused, a declaration or an item, is marked as unnecessary rather than only
listed, which is what fades it in the editor.

Analysis is of one configuration at a time, as with `#if` in C or `#[cfg]` in Rust:
lines in a branch that is not taken still parse, but are not resolved or analyzed, and the
editor shows them faded. The editor has a setting for which named configuration (§5.3) that
is; a project that has no configuration of that name builds its own settings, and the name is
a mistake only when no project has it.

**The incremental boundary is the file's interface**: its module's name and whether it is
placed, what it places and in what order among its own items, what it re-exports, and its
exported declarations, each
carrying everything a user of it needs (a constant's value, a label's address size, a
data declaration's `.sizeof` and `.countof`, a routine's signature, a list's items, a function's
body, a macro's kind and body and the exported symbols it uses). A family's instances are among
them, one per member: they are declarations of the file like any others, and which of them
there are is read from the enum's member list before any name is resolved. Where the enum is
another module's, its members are already part of that module's interface, so an edit there is
news to exactly the modules this rule already names. It also holds the names
of the declarations it does not export that a path can reach, because another module naming
one is told that it exists and is not exported, and what any of them means that another
module names anyway. A changed name is news only to the modules that looked that name up in
this one; a changed module name or re-export is news to all of them. It travels the other way
once: a module that starts or stops naming an unexported declaration of another is news to the
module that declares it, because a declaration something names, wrongly, is not also reported as
one nothing names. Positions are not part of it: what one file says about a place in
another moves with an edit there. The one exception is where a macro is written, because an
expansion's comment names the calls in it by file and line (§13) and a problem with a line of
its body is reported at the call with that line named beside it. Nothing in the interface is derived from a proc body or
depends on `*`: code sizes are layout, left to the linker (§7.6). If an edit leaves
the interface unchanged, no other file is re-analyzed, and the file it is in is laid out and
followed again whole: where control goes is read off the order layout wrote the bytes in, so
the unit is the file rather than the proc. Where the file is one of several in a translation
unit (§12), the unit is laid out again, since a `.fallthrough` from one of its modules into
another is checked against that layout. The only program-wide tables are the defines, the
module table, the segment and range tables (§5.2, §5.3) and the CPU, all small. Keeping
signatures declared rather than inferred is what protects this: inference would make
every caller depend on every callee's body.

## 15. Deliberately not in nt65

| ca65 feature | reason |
|---|---|
| `.define` | textual substitution; constants, `.func` and `.config` replace it (§9, §10) |
| `.constructor`, `.destructor`, `.interruptor` | cc65 start-up registration stays in a ca65 stub that calls the nt65 routine |
| `.feature`, `.setcpu` mid-file | changes the grammar or mnemonic set |
| `.set`, `.org` | positional state |
| `.include`, `.macpack` | textual inclusion; nt65 never reads ca65 source, and shares with ca65 through symbols (§12) |
| unnamed labels `:` `:+` `:-` | positional; use `@name` |
| `.ident`, `.concat`, `.sprintf` for names | computed identifiers; `.each` over an enum builds the tables they were used for, and declares the routines they were used to name (§10) |
| `.match`, `.xmatch`, `.tcount`, `.paramcount`, `.exitmacro` | token-stream macro programming; typed parameters (`const`, `one`, `list`, `operand` with `.mode`), named arguments and defaults replace the common uses (§11.2) |
| recursive macros | a depth limit does not bound an expansion; `list` parameters and `.each` replace walking argument lists (§11.1) |
| `.asize`, `.isize` in macros | expansion would depend on the flow analysis of its own output; macro state signatures and `.ensure` replace them (§7.3, §11.5) |
| modal `.charmap` | replaced by named mappings applied explicitly (§8) |
| `.local`, `.global`, `.pushseg`/`.popseg` | replaced by structural rules and blocks |
| `.smart` | replaced by the flow analysis; the output disables it |

## 16. Decisions

Recorded so the reasoning survives. None is open.

- **A diagnostic is named, not numbered.** A user meets the name in the Problems panel, in a
  project file and in CI output, and `"unused-symbol": "off"` can be read by someone who has
  never seen the catalogue, where `NT0203` cannot. A name also has to be chosen well, which is
  the point: it says what is wrong rather than which pass found it, so it survives the pass
  moving. The one number that stays a number is `NT1001`, which the source generator reports
  to whoever is building nt65 itself; that is a different audience.
- **How much a diagnostic matters is set in the project file, and nowhere in the source.**
  `"diagnostics": { "unused-symbol": "off" }` is the whole of it, and a named configuration
  says it again for a stricter build. Suppression in the source — an `.allow` above a
  declaration — is a language change, and is left out: the project file answers the case that
  matters, which is a team agreeing what it wants to be told, and `.allow` stays the honest
  spelling if one is ever wanted. Adding it later breaks nothing. An error is not a project's
  to turn down either way: a warning is a matter of taste, and an error is a program nt65
  refuses to translate.
- **Braces, not end-keywords.** Simpler to parse and, as much to the point, an nt65
  file is visually distinct from a ca65 file at a glance.
- **`name!(args)` for macro invocation.** A marker on the line is required by §3.1;
  the Rust spelling is the familiar one.
- **Explicit `.export`.** A file's interface is deliberate, which is what gives
  unused-symbol analysis and the incremental boundary of §14 their meaning.
- **Segment regions and blocks, not per-item attributes.** One placement mechanism, and
  the structured replacement for `.segment` and `.pushseg`/`.popseg`. A region, `.segment
  NAME` at file level, is C#'s file-scoped namespace applied to segments: it places what
  follows without indenting it, and it is structure rather than a mode, found from lines
  alone like a brace and allowed only at file level, with no push or pop. The braced block
  stays for a detour inside a proc or a one-off within a region. There is no default
  segment: a program that forgets to say where its bytes go is told so.
- **Segment names are identifiers.** ca65 quotes them because its segment names share a
  namespace with symbols; nt65's segments are a table of their own. The shortcuts
  `.zeropage`, `.code`, `.bss`, `.data` and `.rodata` are gone, which frees `.data` for
  declarations; `.segment RODATA` says the same in one more word.
- **A label is only a location.** Where a label also measured the data written on its line,
  a line break changed what a name meant: `tbl: .byte 1, 2` then `.byte 3` measured two
  bytes, and ca65's usual label over many data lines measured nothing. Formats built for
  analysis (HLA, WebAssembly text, LLVM IR) have no free-standing labels outside a routine:
  every name for data is a declaration with an extent. nt65 adopts that. Code lives in
  `.proc`, data in `.data`, and outside a proc there are no instructions and no labels.
- **Data declarations say their type.** `.data name: .word[16]` rather than `.res 32`, so a
  size, a count and an element type come from one place, `.next` can read targets from data
  declared as addresses, and `.res` is left as padding. `.type T` replaces ca65's `.tag T`
  and is dotted like the built-in element types, because a bare type name inside a proc
  would read like an instruction. `[n]` distinguishes an array of records from one record
  with values, and a value never starts with `[`, so `.word[16]` and `.word 16` cannot be
  confused.
- **A count is checked exactly.** A body shorter than its count is an error rather than
  padded with zeros, because a short jump table is exactly the mistake a count exists to
  catch; padding is written with a `.repeat` in the body.
- **JSON for the project file.** Every build tool, editor and scripting language reads it
  without a parser of nt65's, and a project file holds settings, not expressions.
- **Output is named by module**, not by source file. A module is one file, so the mapping is
  one to one, and a source can move or live outside the project without its output moving.
- **Named configurations in the project file**, over one set of defines. A debug build and a
  release build differ in a few defines and where their output goes, and a Makefile or an
  editor names one rather than repeating its `-D`s; each writing to its own `out` is what lets
  a build switch between them without marker files.
- **The C header comes from exports.** Declaring a struct in C and in nt65 is two layouts kept
  in step by hand; the linker name is already the C name. Routines are `void name(void)`, with
  `NT65_OWN_name` to replace it, because nt65 knows how a routine leaves the processor and not
  which C types its arguments are.
- **Only what is used is imported**, and a checked import is asserted by each module that uses
  it. An import pulls its module out of a library, so an unused one links code nothing calls.
- **Whitespace binds nothing.** `lda # 1` is `lda #1`, as it is in ca65, which assembles
  it without complaint. Maximal munch (§4) stays the one place spacing changes what a line
  means; making `#` bind to its expression would be a second such rule, and would raise the
  same question of `#< label`, `# (1+2)` and every other operand form.
- **Register names are reserved** wherever a bare name can appear, macro parameters
  included, but not for members of named structs, unions and enums, which are only
  reached through `::`. The output never names members, so ca65's restriction on member
  names does not apply.
- **Constants cross modules by value, addresses by import.** ca65 needs constants where
  it needs them, and an imported symbol is never constant to ca65.
- **Every file is a module that names itself, and a module is one file.** A name from another
  module is written with its path or brought in with `.use`, as in Rust, C# and Go, so what a
  name means never depends on which other files the build holds. One file per module keeps a
  private name private at the link, where ca65 has no namespace but the global one, and
  keeps nt65 from choosing the order of two files' bytes. A module's path is only a name,
  with no relative paths and no special view of its neighbours.
- **Layout across modules is placement, stated in the source.** A ca65 program built as one
  file of `.include`s puts one file's routine in the middle of another's and lets it run into
  what follows, and a module being one object could not say that. A fall-through into another
  module checked at link time, with the build's object list keeping the order, was considered
  and rejected: it hands the programmer ld65's object order to get right, catches a mistake
  only at the link, and still cannot put one module's bytes inside another's. `.place` states
  the layout where upstream stated it, keeps every module a module, and lets nt65 check a
  cross-module `.fallthrough` as it checks one within a file. What a translation unit holds is
  structure, so `.place` is never under an `.if`. Whether a module may be placed is said in
  its declaration, because a module that runs into code it does not hold cannot be read
  correctly from its own file otherwise, and so that forgetting to place it, or placing one
  that was not meant for it, is an error where the mistake is: `placed` must be, `placeable`
  may be, and a module that says neither is what every module was before, since most programs
  have no use for any of it.
- **A local name beats a `*`, and an explicit `.use` may not collide.** Another module adding
  an export can never change what a name here means, and a name brought in on purpose that a
  declaration would hide is a mistake to say so about.
- **`::` alone reaches the root of the modules.** It meant the file's top level, which the
  file's own module name now reaches; local scopes still come before modules for a first
  part, so a module never hides a scope written around the use.
- **Linker names are qualified by module**, as Rust, C# and Go keep their modules apart by
  mangling, so two modules may export `init`. `as` names a symbol for C or hand-written ca65
  exactly, as `#[no_mangle]` and `export_name` do. The interoperability contract changes with
  it: an export is no longer spelled as its nt65 name.
- **Export at the declaration**, with the list form kept for what cannot carry it. Writing
  every export twice was the most common complaint about `.export`.
- **Re-exports name what they re-export.** A glob re-export would grow a module's interface
  whenever another module grows, which is what `.export` exists to prevent.
- **No mnemonic is reserved.** Mnemonics were reserved by the program's CPU, and the review
  found ca65's alias spellings (`swa`, `tad`, `ina`, ...) accepted as names and refused by
  ca65. The first answer was to add them to the per-CPU reserved sets; the objection was to
  the shape of that rule rather than to its gap, since which names a program may declare
  should not depend on a project setting. Both single rules were weighed. Reserving every
  mnemonic everywhere takes ordinary words for CPUs a programmer has never heard of (`set`,
  `map`, `neg`, `tab`), still moves when the pinned ca65 gains an instruction, and is the
  path on which MASM and NASM each ended up adding an escape. Reserving none costs nothing
  in the grammar, which already reads `lda = 5`, `rts:` and `jmp rts` as what they are: a
  line that starts with a mnemonic is an instruction unless `:` or `=` follows it, and that
  is a question about the line rather than about the CPU. It leaves the backend's one limit
  to the emitter, which prefixes the few names ca65 would misread and takes its list from
  ca65's own tables (§13). And it never changes — not for a new CPU, not for a new ca65.
  A macro is exempt from the warning: its name only ever appears before a `!`, and a library
  of macros for another processor wants that processor's spellings, most of which the 6502
  shares.
  What a reader loses when a label is called `rts` is a warning's business, the same in
  every project. Registers stay reserved: `asl a` is a question about an operand, which
  position cannot answer.
- **Merge disagreement is not an error.** The lattice already has unknown; reporting at
  the use is precise, reporting at the label is not.
- **Signatures are declared, never inferred**, for procs, extern procs and imports
  alike. It is what keeps the analysis local and the interface stable.
- **`*` for unchanged state, and no project-wide `assume`.** A routine that does not
  touch part of the state should not erase what its caller knows, and the values of D
  and B belong on the routines that set them.
- **A stack of saved state, not push and pull pairing.** Tracking saved P, D and B as
  the analysis runs lets a save and restore span calls and labels.
- **A declared label anyone may jump into starts on the stack a call to the routine leaves.**
  Once such a label stopped assuming the widths its own routine's paths left, the analysis
  stack was the part still taken from them, and a `pla`, a `plp`, a `.frame` slot or a `keeps`
  below the label stood on a push a jump in never made. Three answers were weighed. Keeping the
  falling path's stack is what was already there, and is unsound. Checking the jumping side for
  the pushes the path above the label made puts a requirement on a jump into a label that a
  tail call to a whole routine does not carry, and there is nothing in the language for either
  side to write it down in. So the label assumes what a call to the routine assumes — nothing,
  or the `args n` the signature names — and where its own path has pushed more, the stack below
  it is unknown rather than either side's guess. What that costs is that a save may not span a
  second entry point, which is right: the two ways in genuinely arrive on different stacks, and
  a routine whose second entry point reads what its caller pushed has `args n` to say so. The
  message on whatever then fails names the label and says both.
- **A jump into another routine's interior is an exit, taken at that routine's word.** It looks
  like a jump within a body and behaves like a tail call: control lands in another routine, and
  that routine returns to this one's caller. So the jumping routine is held to what the routine
  the label is in declares and hands back — its exit state and its `keeps` — and not to what the
  code after the label happens to do, which is a body's business and no part of an interface.
  Reading the label's own `.state` for this instead was weighed and refused: a declaration says
  what the state at a point is, and there is nothing in it, nor anywhere for it, to say which
  registers survive from there to the return. Taking the routine's word keeps one rule for both
  spellings of a tail call and puts the promise where a caller can already read it.
- **What a routine keeps is worked out; what it promises is declared.** The set is a fact
  about a body, as what a pass costs is, so it is computed and shown rather than written;
  `keeps` is a promise a caller may lean on, so it is declared and checked. That is the same
  split the cycle counts and the signatures already have, and it is why inferring `keeps` into
  a routine's interface, which §16 rules out for signatures, is not what this does.
- **A caller is not warned about a register a call takes away.** Not because it cannot be
  worked out — which registers a routine reads before writing them settles an argument from a
  loss, and needs no calling convention — but because a warning is the wrong shape for it.
  Every call takes some register away; what a reader wants is to see what the registers are
  doing, not a list of the places they changed.
- **`.state keeps`, not a third annotation directive.** `keeps a` means the same at a point as
  at an exit, so `.state` carries it, and §7.4's two annotations stay two.
- **`.next` and `.fallthrough` are two directives.** `.next` once did two jobs: it named the
  successors of a statement nt65 cannot follow, and it said a routine runs into the one written
  after it, with an adjacency check. The two collided. After a direct `jsr X`, `.next Y` was
  read as the call's targets where the author meant "then run into `Y`"; a routine whose last
  statement is an `.if` chain had no statement for it to stand under; and a `.byte $2c` that
  lands on a routine other than the next one written could not be said, because naming a
  routine made the adjacency claim. Now `.next` is only about the statement above it, where
  that statement's successors are unreadable, and a routine it names is a jump checked as a
  tail call; `.fallthrough` is only about the end of a routine's body, whatever ends it. Go's
  `fallthrough` statement and C++17's `[[fallthrough]]` make the same choice: falling into
  what is written next is said with a word of its own, at the end of the thing that falls,
  rather than inferred or folded into a general jump. A `.next` that could only repeat or
  contradict what nt65 reads is an error, so the old spelling of a fall-through is caught
  where it stands, with a fix that writes the new one.
- **An always-taken branch is a `.next` naming its own target.** 6502 code often branches on
  flags it knows, `bne` after loading a nonzero value or `bcs` after a routine that always sets
  carry, as a two-byte jump or to hop over text. Once `.next` stopped naming the successors of
  statements nt65 can read, such a branch had to end in `.next ?`, which threw away the edge
  to the target, or warned that it ran into the data after it. A `.next` naming the branch's
  own target says exactly what is known and nothing more: the target edge was already right,
  and the edge past it is the one the flags rule out. A new directive was not wanted for what
  is an annotation on the statement above, and naming any other target stays an error, because
  a branch that goes somewhere its operand does not is a jump, and should be written as one.
- **The end of a body is the end the configuration resolves.** At first a `.fallthrough` was the
  literal last line of a body and never under an `.if`. msbasic has routines that run into
  different routines in different configurations, or jump in one and run on in another, and
  the literal rule left those unsayable: one `.fallthrough` after the chain names the same
  routine for every build. Conditions are settled before analysis (§10), so the body a build
  analyzes is the chosen branches, and in it a `.fallthrough` at the end of a branch that ends
  the body is its last line. Checking only the branches a build takes follows from that, and
  is what lets a branch name a routine only its own builds declare; each configuration is
  checked when it is built.
- **One layout, per segment, everywhere.** A file was once laid out a region at a time, each
  region a stream of its own, while a translation unit of placed modules joined a segment's
  regions as ca65 does. The same source was then accepted as a placed module and refused as an
  ordinary one: a routine followed in `CODE` by the next `CODE` region's routine was not
  adjacent to it with a `RODATA` region between them in the text, a `jeq` across one was
  always long, and a branch across one was left to ca65. What ca65 does is the only layout
  there is, and whether a program is valid cannot depend on whether something places its
  module, so every file and every unit is laid out per segment, its regions and blocks joined
  in text order, and everything that reads layout reads that. The flow analysis still treats a
  nested segment block as a detour (§5.2), since what fall-through reaches is a question about
  the routine's text and not about where the block's bytes land.
- **Processor-state analysis on the 65816 only.** On the other CPUs nothing consumes the
  state, so its annotations would be ceremony.
- **Procs do not nest.** A nested proc's bytes would sit inline in its parent's; a
  separate proc in a `.scope` gives the same privacy and namespace without that.
- **Cycle counts are two built-ins over a span, not one over a routine.** They were refused
  outright at first, because a sum along one path is no bound once a loop or a call is on it
  and a built-in that answered a number anyway would be read as a bound on the program. What
  that argument rules out is a count of a *routine*; it does not rule out a count of a span
  with neither in it, and §7.6 already works those out exactly — they are the raster lines and
  the interrupt prologues that anybody counting cycles is counting. So the refusal moves to
  where it belongs: the span says what it may not hold, and an error names the call or the loop
  it found rather than a number quietly standing for something it is not. There are two
  built-ins rather than one because the count is an interval wherever a page crossing, a taken
  branch or an unknown width makes it one, and a single `.cyclesof` would have had to choose
  which end of the interval to be. `.mincycles` and `.maxcycles` say which end is being asserted
  on, and where the count is exact they are the same number, which is what a raster routine
  wants to write down.
- **Sixty-four bits while nt65 computes, thirty-two where the output carries a value** (§9).
  One width for both was weighed and refused from either end. ca65's 32 everywhere costs the
  room an intermediate wants, and a language whose `.repeat` count and whose `.if` condition
  overflow where the arithmetic is fine is a language explaining its backend. Sixty-four
  everywhere is the bug the review found: `BIG = $7fffffffffffffff` built, and ca65 refused
  the output. So nt65 computes wide and checks narrow, at the declaration, where there is a
  name to say it about. Every step of a declaration's expression is checked with the result
  because the output writes that expression as the source wrote it, and ca65 works the steps
  out again — an intermediate the two disagree about would be a value that assembles as
  something nt65 never said. Overflow is an error rather than a wrap for the same reason:
  where an expression has no value nt65 can stand behind, the one thing that must not happen
  is that it is written out for ca65 to answer instead.
- **Compile-time arithmetic, integer only and rounded by a written rule.** A sine table, a
  circle and a scaled step are what a 6502 program spends its build time on, and without them
  each is a script in another language whose output is pasted in, so the table and the routine
  that reads it drift apart and nobody can see what the numbers were. The functions are integer
  in and integer out because everything they feed is: a byte table, a word table, a count. They
  are not floating point, and may not be, because what nt65 works out is written into the output
  and has to be the same on every machine that builds the program — a last bit that depended on
  a library or a processor's rounding would be a program that assembles to different bytes in
  two places. So the answer is defined rather than computed: the nearest whole number, halves
  away from zero. That definition is exact everywhere, because the only angles at which the
  value is a half are the twelfth-turns where the sine is `±1/2`, which nt65 works out from the
  fraction; everywhere else the nearest whole number is settled long before the precision runs
  out. `.muldiv` is there for the same reason: `a * b / c` written out overflows in the middle
  or loses the fraction at the end, and neither is what anybody meant.
- **Conditions test only the configuration.** `.if` sees defines and never program
  symbols, as `#if` in C and C#, `#[cfg]` in Rust and `#if` in Swift do. A conditional
  that can test program constants (ca65's `.if`, D's `static if`) makes which
  declarations exist depend on evaluating those declarations. Checks on program values
  are `.assert`.
- **In-file defines are `.config` settings.** An in-file define was left out at first; in C#
  it is mostly a temporary per-file toggle. A library needs somewhere to state its own
  configuration and its defaults, though, and a program somewhere to change them. A setting
  is a module's own, exported like a constant, written outside every block so that no
  condition decides which settings exist, and set by the build through its qualified name.
  It is spelled `.config` rather than ca65's `.define`, which means textual substitution.
- **The CMOS variants are CPUs of their own.** The 65SC02, the R65C02 and the WDC 65C02
  differ in whole instructions, and a program for one is wrong on another in exactly those,
  so each checks its own set and sets ca65's matching CPU. `.has` asks about an instruction,
  so code for several CPUs does not list them; `.target` stays exact.
- **The undocumented opcodes are a CPU of their own, and their names are ca65's.** Two other
  shapes were weighed. A project setting that turned them on for the 6502 would make which
  instructions a program may write depend on a setting, which is the shape the mnemonic
  decision already refused. Having them on the 6502 outright would mean a program that mistypes
  a name gets an instruction instead of a diagnostic on the one CPU where the greatest number
  of people are writing plain code. A sixth CPU says what it is: a program built for `6502x`
  has decided something about which silicon it runs on, and the flow analysis, the cycle counts
  and the lengths all follow from the one setting in the project file. The spellings are ca65's
  `6502X` table rather than a set chosen here, because no datasheet names them and the output
  has to be what ca65 assembles; taking the list from ca65 is what keeps it from drifting, as
  it does for the words the emitter prefixes (§13). Their counts are counted and their results
  are described, rather than the whole instruction being left uncounted: how long `sha` takes
  is not in doubt, and what it stores is, so the count stands and the hover says which. Only
  `jam` has no count, because it stops the processor.
- **Running off the end of a proc warns off the 65816.** Nothing consumes the state there,
  but a proc that runs into the next one is still usually a missing `rts`, and one that means
  to says so with `.fallthrough`.
- **Text is built by functions, and is text wherever a literal is.** A text constant crosses
  modules by value, and nothing in ca65 can hold one, so none reaches it. At first there was no
  arithmetic or concatenation on text, so a constant could not build text a literal could not
  have written, and text that had to be built, msbasic's `htasc` setting bit 7 on a keyword's
  last byte, was a macro. A macro's bytes exist only once it is expanded, after every constant
  has its value, so a distance into a table of such texts could never be a constant, and
  `ldx #ERR_SYNTAX` was refused. Letting constants wait for expansion would change §3.1's order
  for everything; a `.func` is already evaluated with the constants, so letting it return text
  keeps the order and gives the table its lengths. The text a function builds is still bytes
  nt65 writes out itself, never text for ca65, and `.strsub` and `.strcat` are the two
  built-ins that building it needs; there are still no operators on text, so `"A" + "B"` is not
  a second spelling of `.strcat` and `+` stays arithmetic.
- **Signed data.** A number slot takes a signed or an unsigned value of its width, as the
  bytes are the same, and the output writes the two's complement ca65 needs; an address is
  never negative.
- **`.strz`, not `.asciiz`.** The terminator is what the directive promises, so it checks it:
  one text, and no zero inside it.
- **`.assert` takes no level.** When a check can run is nt65's to decide, and a failure is an
  error whenever it is found.
- **`.select`, not `?:`.** `:` already means an address prefix, a signature and a size.
- **Macros do not declare names in their caller.** Otherwise the names a file declares
  would depend on expanding macros, whose conditions test their arguments.
- **One `.state` directive, with the signature's items.** A signature and an assertion
  both state the processor state at a point, so they share one grammar, and a label's
  declaration is one line. It replaces `.a8`, `.a16`, `.i8`, `.i16`, `.native`, `.emu`,
  `.dp` and `.dbr`, whose ca65 spellings mean an unchecked hint rather than a check.
- **Cheap locals are scoped by structure, not by position.** ca65's rule, the region
  between two regular labels, exists because a plain ca65 file need not use procs.
  nt65 has braces, and scoping cheap locals to the enclosing proc or scope makes them
  private, which keeps every edge into them inside one flow analysis.
- **C's operator precedence, with parentheses required at its traps.** Copying ca65
  expressions verbatim is not a goal, and C's order is the one readers already know.
- **Flat output.** With no ca65 scopes, structs or cheap locals in the output, nothing
  depends on ca65's name resolution, and nt65 chooses every name it emits.
- **Shapes are constants; layout is not.** `.sizeof` and `.countof` describe types and
  data and are nt65 constants. `.endof` and `.spanof` describe layout and are resolved by
  the linker. Keeping code sizes out of constants rules out sizes that depend on
  themselves and keeps proc bodies out of a file's interface. `.sizeof` of a proc is its
  span, with a span's limits, because a routine has no shape to measure instead.
- **A distance inside one data declaration is a constant.** A message's offset in a table,
  `ERR_NOFOR = messages::NOFOR - messages`, is an error number, and sizing it by its widest
  address made it an absolute address a one-byte immediate refused. The two places are part of
  one shape, so the distance is a shape too and changes with nothing the linker decides.
  Where an `.align` or a macro call stands between them it stays an address expression: the
  first depends on placement, and making the second a constant would have constants wait for
  expansion, which §3.1 rules out.
- **Segments are declared.** A misspelled segment name is an error, not a new segment.
- **A standard segment's predeclaration is a default.** It could not be declared again, so it
  could never carry a direct page, a bank or mirrors, and a 65816 program that wanted the checks
  on its standard segments renamed them. The rename leaked into the linker configuration, and
  since ca65 creates the standard segments in every object, the vacated names still existed
  while the new ones did not. Declared once at its own size, the segment says where it is, and
  "declared exactly once" keeps its meaning: the built-in table is what stands where a program
  says nothing. The size stays fixed because it is the one ca65 gives the segment in every
  object, whatever nt65 writes.
- **The output does not depend on ca65's command line.** A project passes one set of
  ca65 options to every `.s` file, so nt65 output resets or avoids everything those
  options can change, and is tested against ca65 built from one pinned cc65 commit,
  because cc65's version number no longer identifies what ca65 accepts.
- **Debug information beside the output, not in it.** ca65 can only carry it as a `.dbg line`
  before each generated line, which would be a third of the output and would stand between
  every two lines of every routine; the output is meant to be read. A line map beside each
  `.s`, put into ld65's debug file after the link, says the same thing and leaves the ca65
  alone. What lands in the debug file is what cc65 writes for C, as external source lines, so
  debuggers that read ld65 debug files need nothing new. A zero timestamp keeps it
  deterministic.
- **The object file is the only boundary with ca65.** Reading ca65 include files, even a
  declaration-only subset, would put nt65 in the business of parsing ca65 and require
  existing files to fit a layout. Symbols already cross at the link, and a checked
  import covers the values nt65 needs.
- **An import may say what its bytes are, and `nt65 import-inc` runs once.** Interop ran one
  way: `--c-header` sent types out and an import came back opaque, with no size, no members
  and no elements, so a project that shared a record with hand-written ca65 wrote its layout
  twice and kept the two in step by hand. A typed import is the same answer the language
  already gives for a routine: a `proc(...)` import declares a signature nothing checks, and a
  typed import declares a shape nothing checks. Both are trusted because the other side of the
  link is not nt65's to read, and both put the declaration where a reader of this module can
  see it. The alternative, reading the ca65 declaration, is the boundary above, and it stays
  refused. For a file that is only constants, which is what most hardware include files are,
  reading it once is not the same question: `nt65 import-inc` is a person's command, its output
  is an ordinary module to read and keep, and no build depends on it. That is why it writes a
  comment for every line it could not convert and counts them: a converter nobody checks is a
  silent half-translation, and this one is written to be checked.
- **Refined parameter kinds, not a type system.** A range on `const`, an enum as a kind and
  the modes of an `operand` map onto what macros did by hand with `.assert`, `.ident` and
  `.error` after a `.mode` test, and they matter most for a library of macros that is
  nothing but signatures. Structs as parameter types or a general type system over
  expressions would be a language inside the language, and are left out. A bare member name
  is a word, never looked up in the caller, for the reason a `one` word is not: what it
  means is the parameter's to say.
- **Language features before macros.** Where ca65 code reaches for a macro, nt65 first
  asks whether the pattern needs analysis, and if it does, makes it a language feature
  (Appendix B). Macros keep what does not, which is what lets them stay restricted.
- **Expansion comes after constants.** An expansion reads constants through its
  arguments, and nothing that decides syntax, names, declarations, constants or shapes
  reads an expansion, so none of those needs a macro expanded.
- **Macro arguments are values, not tokens.** Textual substitution is what lets a ca65
  macro change meaning with operator precedence and split an operand at its comma.
- **Macros do not type the labels on their calls.** A type returned by a macro would give
  the caller constants that exist only after expansion. Typed records are initialized
  `.type T { }` data. If a computed typed record is ever needed, the extension that fits is a result
  type in the macro's header, `-> T`, which limits the body to data so that each
  expansion can be checked against `.sizeof(T)` as soon as it exists.
- **One kind of macro.** Whether an expansion is code or data is visible in the
  expansion, and nothing before expansion needs it, so it is not declared. A separate
  data-macro kind was considered and left out: its only use would have been result
  types, which carry that restriction themselves.
- **Types declared in a macro body are local to the expansion.** They break no ordering,
  and scoping alone keeps them from escaping.
- **No recursive macros.** A depth limit does not bound the width of an expansion; a call
  graph without cycles, checked from names, does.
- **Long branches are built in.** Choosing between a short and a long branch needs
  distances, which are layout, so it cannot be a macro in nt65. nt65 knows distances
  within a region or segment block and can shorten forward branches, which ca65's `longbranch`
  cannot.
- **`.ensure`, not a width stack.** A macro-time stack follows the text; the flow analysis
  follows control flow, and `.ensure` emits from its result.
- **Stack frames are checked against the analysis stack.** Stack-relative offsets written
  by hand shift silently with every push, and the analysis already counts pushes.
- **Signature sets, not defaults per file or segment.** Most routines of a real 65816 program
  repeat one state, `a8, i16, dp = 0, dbr = $80`. A named set says it once and keeps each
  signature readable where it stands: a routine's signature is its set plus what differs,
  with nothing inherited from where the routine happens to be written. A set is a symbol like
  a constant, so it crosses modules the way constants do.
- **`noreturn` and `interrupt` are items, not conventions.** A routine that never returns used
  to invent an exit state, and a handler to spell out every unknown item and pick `near` or
  `far`, neither of which it is. Saying what they are lets the analysis check what matters
  for them — no `rts`, no call to a handler — and stop checking what does not.
- **A set of banks for B, not a wider unknown.** A small LoROM program's routines run in any
  bank that sees low RAM and the registers, so they said `dbr*`, and inside one every operand
  check was silent and no caller was checked for anything. A set is the `mirrors` model turned
  around: a segment says which banks see it, and a routine which banks it may run in, so every
  operand is checked against each of them. It is kept to `dbr`, since nothing else the
  analysis follows has a use for one, and a set at entry is handed back unchanged because the
  routines that want one are exactly those that never touch B.
- **An address space, not an instruction set per processor.** A 65xx sits beside many other
  processors, and nt65 adding their instruction sets one at a time would never finish; for a
  processor with a toolchain of its own, nt65 should not be its assembler anyway. What every
  such program gives the 65xx side is the same: an image of bytes, the space it runs in, a
  run address, and names in that space. That contract has nothing to do with the other
  processor's instructions, so it is what scales. A table of opcodes in the source was
  weighed and left out: operand shapes are syntax the analysis and the editor key off, what
  makes the analysis worth having is a few dozen lines of code per processor rather than
  rows, and a project's table is checked against nothing. The spaces are declared in the
  project file only, beside the segments and the linker configuration they restate.
- **A segment has one home bank and mirrors.** Low WRAM, hardware registers and FastROM code
  are each seen in several banks. Data is reached from any of them; code is taken to run in
  its home bank, which is what `phk` and the cross-bank checks use. `bank` stays the word for
  where a segment lives and `dbr` for the register's value at a point: two things, two words.
- **The analysis stack has an unknown base with a known top.** Forgetting the whole stack at
  `txs` lost `phk`, `plb` straight after it. Tracking pushes over a base nothing is known of
  keeps every idiom that pushes and pulls its own values, and is the model `.frame` already
  used after `tcs`.
- **`args n` for arguments the caller pushes.** A frame could not reach past the return
  address, so routines forgot their stack with `tsc`, `tcs` to reach their arguments. The
  item puts the arguments and the return address on the analysis stack at entry and checks
  callers push them. Arguments the callee removes are not in version 1.
- **A mirror address is an expression.** `(bank << 16) | .loword(f)` says which bank a long
  transfer lands in, and is checked as a transfer to `f`; a prefix or a rule of its own would
  hide that bank.
- **Leading whitespace means nothing.** A line's kind comes from its tokens, never from the
  column its first token starts in, so labels may be indented and so may everything else.
- **A slot too narrow for its address is an error, not a truncation.** ca65 keeps the low 16
  bits of a far address in an `.addr` without a word. nt65 asks for `.faraddr`, or `.loword(x)`
  where the low bits are meant, so a dropped bank is always written down.
- **An alias is checked against what it names.** An extern proc naming another routine writes
  that routine's signature or takes it, so a second name can never make a near routine far.
- **An operand with one form carries no prefix.** Where the instruction has only one form, ca65
  has nothing to choose. nt65 checks that the operand fits that form instead, which catches an
  absolute pointer in `lda (ptr),y` that ca65 would hand to the linker.
- **Constants open no segment.** A constant has no address, so it is written where it stands,
  and a module of constants writes no segment at all.
- **Imports, exports and link-time assertions are not mapped.** ld65 reports them at their
  `.s` line whatever the map says, so a record for them would only mislead.
- **A cause is reported once.** A reserved word as a parameter, a missing signature or a
  private function used many times gives one error where it can be fixed, not one at each use.
- **Unused-symbol warnings stop at what could be meant.** An export is used by definition, data
  that holds values may be there for where it lands, and a name in a branch this configuration
  leaves out is used by the other build. A routine is not an exception to that rule: an
  unexported one nothing names cannot be reached from anywhere, not from this module and not
  from ca65, so there is nothing it could have been meant for. A program's entry point is
  exported, because whatever hands control to it — a vector table, a linker configuration,
  hand-written ca65 — is outside the module and reaches it by its linker name. The one routine
  reached from outside without an export is an interrupt handler, which says `interrupt`.
- **`emu` in a signature is a state.** It pins both widths at 8, as in `.state`, and an exit
  that names a 16-bit width is in native mode without repeating `native`.
- **Mixed data is a named block.** A header, a vector table or a BASIC stub holds data of
  several kinds. `.data name { }` measures all of it, names its parts as members and keeps `@`
  positions private, so no label ever measures what follows it.
- **A string member declares its pad.** Text fields are padded with spaces as often as with
  zeros, and a pad written on the member keeps initializers and empty records consistent.
- **A scope is only a namespace.** A named block of code is a proc and a named block of data is
  `.data`; a scope with an address would have an extent nothing declares.
- **Exporting a scope or a `.data` block exports what it declares.** It stops at a routine's
  interior labels, which are entry points to declare one by one, and at `@` positions, which are
  private by their spelling.
- **An import may be re-exported.** One module then declares cc65's runtime symbols for the
  whole program. Each module that uses one writes its own `.import`, since the object that uses
  a symbol is the one that must import it.
- **An exported macro may name only what is exported.** Its body resolves names where it is
  declared, so an expansion in another module could not link to a private name. The error is
  at the declaration, and the name is not exported for it.
- **An export's size may widen, never narrow.** Other modules size their uses by the export,
  and a narrower size than nt65 knows would make them reach the wrong memory.
- **Every width has a big-endian partner, and `.long` is a number.** ca65's `.dbyt` beside
  `.dword` leaves readers guessing which widths have one. `.long` and `.faraddr` are the same
  bytes, but a C header needs to know which is an address.
- **No cc65 start-up registration.** `.constructor` and its partners are a convention of cc65's
  runtime, not of the language, and a ca65 stub that calls the nt65 routine keeps them where
  cc65 reads them.
- **Paths are from the project root.** ca65 records a path as written, so a path relative to
  the output file pointed outside the project in debug files and messages. The root is where a
  build runs, which is where a debugger looks.
- **A repetition folds back into a ca65 `.repeat`, and the output decides it, not the source.**
  Forty-three copies of one instruction is not what anyone reading the output wants to see, and
  ca65 has the directive that says it, so where writing `.repeat` would mean exactly what nt65
  means, the output writes it. What it may not do is guess: every turn is decided on its own —
  the address size an operand is reached at, the width an immediate is written at, the form a
  branch takes, the branch each `.if` in the body took, the name each turn declares — and none
  of that can be read off the source, because two turns that look alike there are often two
  different lines here. So the turns are written out first, as they always were, and compared
  afterwards; they fold only where they came out the same line for line, which is the same
  question, asked of the same lines, as the one that gathers a row of equal bytes back into a
  `.res`. A body that names something stays unrolled whatever it looks like, because ca65 would
  declare that name once a turn.
- **A counter where the turns differ only in what the binding was worth.** ca65's `.repeat`
  takes a counter and puts the turn's number wherever its name stands, which is exactly what
  the turns of `.repeat 256, i { i }` differ by, and two hundred and fifty-six lines of table
  are three lines of output instead. It is settled the same way as the rest: the body is the
  first turn with the counter where the number stood, and it is written only after putting
  every turn's number back has given the line that turn was actually written as. So the
  arithmetic ca65 does is the arithmetic nt65 checked, on the same expression, in the 32 bits
  the two already agree in (§9) — anything the binding decided rather than appeared in, such as
  how wide an address a turn reaches or how much room a line takes, makes a turn that the
  counter cannot reproduce, and the whole repetition is written out. The counter is a name of
  the output's, derived from the binding's and made unique against everything else in the file,
  since ca65 replaces the name wherever it stands.
- **nt65 deletes only what it wrote.** A record under `out` names each output: a removed
  module's output goes, and a hand-written file beside it never does.
- **Unchanged output is left alone, and stale output is touched.** make then reassembles only
  what an edit changed, and one nt65 run brings every output up to date, with no marker files.
- **A file named on the command line is built in its project.** Without the rest of the project
  another module's name would read like a typo.
- **A program that names no CPU is built for the 6502, and nt65 says so.** Requiring a CPU
  would burden the smallest builds; saying which one was assumed keeps it from being silent.
- **The editor's configuration is a name, not a project setting.** The projects of a workspace
  rarely share all their configurations. A project without the chosen one builds its own
  settings, and the name is a mistake only where no project has it.
- **The language version is the command's major version.** Neither a source file nor a project
  file names a version: nt65 1 is what every `nt65` 1.x builds, and anything that would stop a
  version 1 program building waits for nt65 2 (§17).
- **Fixes for diagnostics that name their fix, and rewrites for a selection.** A message that
  says what to write has a fix that writes it, and a fix is part of the diagnostic as what to
  change rather than as an edit, worked out against the files when it is asked for. Where the
  message names one answer, that fix is the one to apply without asking: `.next ?` rather than
  the labels a `.next` names, or a `.state` saying what the analysis finds. Where every answer
  needs a decision — which way `a & $0f == 0` was meant, which width an immediate is — each
  answer is offered and none is preferred, rather than nothing being offered: a list the
  programmer picks from is not a guess, and picking from one beats writing either out by hand.
  The other source is the selection, which reports nothing and is asked a question anyway:
  what is written at the caret has a way of being written that the analysis can work out, such
  as a path brought in with a `.use` or the routine a few lines would make. Those are offered
  only where they would change something, and each is worked out from the analysis, so that a
  rewrite means what the lines meant. Where a rewrite has to write a name, it writes one and
  asks the editor to rename it: a routine lifted out of another is called after the label the
  selection starts with, or `extracted` where it starts with none, and the change carries a
  command that puts the caret on that name and starts a rename, which is what every other
  language's extraction does. The name is the routine's own, so a placeholder there costs a
  keystroke; a name that would go into the file's interface, such as the linker name an `as`
  gives, is not written at all, because a placeholder another program links against is worse
  than writing the line by hand.
- **`_` between a number's digits, and `\0`.** Both are what every language a programmer comes
  from spells this way, and neither can mean anything else: `_` is not a digit in any base
  nt65 writes, so the only question is where one may stand, and it stands between two digits;
  `\0` was an unknown escape. ca65 has an `underline_in_numbers` feature, which the output
  switches off, because nt65 writes every number itself and so never needs it.
- **`$schema` in the project file.** A project file that names its schema gets completion,
  hover and validation in every editor that reads one, and the key is the convention for
  saying so. nt65 reads nothing from it. It is the one key nt65 accepts and ignores: every
  other unknown key stays an error, because a misspelt key that is quietly accepted is a
  setting that silently does nothing.
- **`.if` in an enum body, and no repetition there.** The alternative is two whole enums under
  exclusive conditions, which says twice what differs once and splits the type a program is
  written against. A member is a name written on its own line, so an `.if` around one changes
  only whether that line is read, which is what an `.if` does everywhere else; the values
  follow, because a member with no value of its own is the member before it plus one. A
  `.repeat` or an `.each` there would need a computed name, which §2 and §15 rule out, so
  neither is allowed.
- **A family is an `.each` over an enum that declares by its binding.** A program needs
  several routines that differ only in a constant — one per sound channel, per sprite slot, per
  bank — and the alternatives each break something the analysis rests on. Names are never
  computed (§2, §15), so `play_0` from a `.repeat` index is out. Which declarations exist
  follows from the configuration before anything is evaluated (§3.1), so a template
  instantiated by its calls, `jsr play(2)`, would make the routines that exist depend on every
  use in the program. A macro cannot declare a name in its caller (§11.1) and a file's
  interface comes from headers alone (§14), so ca65's `proc_macro name` idiom would put a
  routine's kind and signature in a macro body. Labels in a turn are private to it (§10), so
  one proc with a `.repeat` of entry points cannot export them.
  What is left is the enum, which nt65 already uses as a fixed set of names: `.each Cmd, c {
  actions::c }` *finds* a member's declaration, and a family is the same rule *making* one.
  Nothing is concatenated, the set of declarations still follows from the configuration, and
  the whole construct is `.each`, `.proc` and `.data` composing as they compose everywhere
  else. `.multiproc` is the two blocks folded into one line, because `.proc b` with `b` a
  binding reads oddly until one knows the rule and because a keyword line says what the block
  is from its opener: the parser knows the body is a routine's and the outline shows one block.
  The name is not `.procs`, which is `.proc` with one letter more at a glance.
- **A family declares routines and data, not scopes.** What a binding-named `.scope` or
  `.data` block held would be reached through it, `pulse1::stop`, which is one declaration per
  member of everything inside and so one identity per member for a body written once. Two
  roles for one member are two families instead, `note::pulse1` and `stop::pulse1`, which says
  the same thing with the names the program already has.
- **An element is `name[i]`, and one short text is padded out.** Reaching the second entry
  of a table meant writing `actors + .sizeof(Actor)`, which repeats what the declaration
  already says and quietly goes wrong when the type grows a member. `[i]` says the same thing
  in the words the declaration is written in, and is checked against the count, which is the
  whole reason the count is written down. It is deliberately a constant: names are never
  computed (§15), and an index worked out as the program runs is a register, `actors::hp,x`,
  which the language already has. The brackets cannot be confused with a declaration's `[n]`
  because a count follows an element type and an index follows a name. Padding is the other
  half of the same complaint: `char title[21] = "..."` is how a fixed field of text is
  declared in C, and writing the zeros out by hand hides what the count was for. Only one
  text is padded, never a list, because a short table is exactly the mistake a count catches.
- **Each project in a workspace is its own program.** A folder of several games, or a library
  with its test programs, holds projects that declare the same modules; one program of all
  of them would report every module twice.
- **Diagnostics are lines on standard error, and objects on standard output when asked for.**
  A person reads the line, and it is the line an editor's problem matcher already reads, so it
  stays where it is. Something reading nt65 that is not an editor wants structure, wants it on
  the stream it is capturing, and wants nothing else on that stream, so `--json` writes to
  standard output and what nt65 says about itself stays on standard error. Colour marks
  `error:` and `warning:` and nothing else: the position is what gets selected and copied, and
  a message wrapped in escapes is one nobody can grep.
- **One layout, and no setting for it.** Leading whitespace means nothing to the language, so
  nothing is lost by choosing a layout, and a setting would only give a project the chance to
  disagree with the next one. The layout is not invented either: it is the one the generated
  ca65 is already written in (§13), which is what a reader comparing a module with its output
  sees anyway. The formatter moves what stands before a line's first token, drops what stands
  after its last, and sets the one gap a run of data lines lines up on; everything else on a
  line is the programmer's, because a formatter that reflows an expression has to be told when
  not to, and that is the setting there is not.
- **Names are coloured by the language server; the grammar colours declarations the same way.**
  What a name refers to is the server's to say: a use, `jsr init` or `Joy::A`, names something
  only resolution can see, and a member of a named enum, struct or union may be spelled like a
  register or a mnemonic. So the server classifies every name as semantic tokens, drawn over the
  TextMate grammar's colours. The grammar is all there is while an editor starts and where no
  server runs, and a name that changed colour when the server answered would flicker, so the
  grammar gives each declaration the scope the editor maps the server's token to: the name after
  `.proc` or `.enum`, a label, a constant, a parameter, and the members of an enum, a struct, a
  union and a record. It knows these from the line and the block they are in; a use keeps the
  plain colour until the server answers, and so does a constant whose expression turns out to be
  an address.

## 17. Version 1

Version 1 is this document together with the `nt65` command and the language server released
as 1.0.0. It makes promises to five kinds of user, and each holds for every 1.x release.

- **To programmers: the language.** Lexing, syntax, blocks, name resolution, what each
  construct means and which programs are errors, as §4 to §12 and Appendix A state them, on each
  CPU with the instructions §5.1 gives it. A program that one 1.x release builds without errors,
  every later 1.x builds without errors.
- **To projects that link the output: the interoperability contract** of §1, all of it. The
  output of a program assembles and links with the pinned ca65 and ld65 to the same bytes, under
  the same linker names and address sizes, with the same `.nt65` file and line for each byte in
  the debug information, and the C header declares the same names with the same types.
- **To whoever writes `nt65.json`: the project file** of §5.3. Every key keeps its meaning, and
  a project file one release reads, every later one reads. That covers the names under
  `diagnostics`: a diagnostic's name is stable once released, so a project file that switches
  one keeps switching it. A release that stops reporting something keeps the name in the
  catalogue, reported by nothing, so that a project file naming it still reads.
- **To scripts and Makefiles: the command line** of §5.3. The options and what they do, where
  output goes and what it is named, the dependency file, the exit status (0 when the program
  built, 1 when it is wrong, 2 when the command is) and the form of each diagnostic,
  `file:line:column: severity: message [name]`, with the path as the person running it would
  write it, and the fields of the object `--json` writes in its place. `nt65 fmt` keeps its own exit
  status too: 0 when every file it was given is in the layout, 1 when `--check` found one that
  is not.
- **To packagers: the cc65 pin.** A release's output is tested against, and promised for, ca65
  and ld65 built from the cc65 commit that release names (§13). The pin moves in a minor
  release that says so, and only to a commit the same output assembles with, to the same bytes.

Version 1 does not promise:

- which warnings a program gets, or the wording and exact position of any diagnostic. The
  *name* a diagnostic is reported under is promised, and its wording is not: that is what lets
  a message be reworded without breaking a project file or a CI filter;
- the text of the output beyond what the contract of §1 names: its layout, its comments, which
  repetitions it says once and which it writes out, and the generated names of cheap locals,
  expansions, iterations and repeat counters;
- the layout `nt65 fmt` writes, character for character. It stays one layout, and laying out
  what is laid out changes nothing; a release that moves it says so, and formatting again is
  what answers it;
- the editor: which requests the language server answers, what it completes, hints or fixes,
  its own protocol extensions and the settings of the VS Code extension;
- the Norristown libraries as an API, the record nt65 keeps in `out`, and how fast anything is.

**Breaking changes.** A change is breaking when a project that one release builds, with the
same sources, project file and command line, is refused by a later one, or builds to different
bytes, linker names, export sizes or debug lines; when a project file or a command line one
release accepts is refused or means something else; or when output one release writes needs a
different ca65. Breaking changes wait for version 2. These are not breaking:

- a fix where the earlier behaviour broke the contract itself: output ca65 refuses or warns
  about, bytes that differ from what this document says they are, or a program accepted that
  this document says is an error;
- a new warning, a better message, and anything the editor does;
- a new directive, built-in function, project file key, command or command-line option, because
  every `.word` already lexes as a directive and an unknown key, command or option is already
  an error;
- a new CPU. It reserves nothing anywhere (§4) and widens the warning of §4 for every
  program, which a new warning is allowed to do.

A new state item, whose word a signature set may already be named, and a new contextual word
anywhere a name may stand, are part of the language version: each takes a spelling that names
something today. An instruction does not, since no mnemonic is reserved — a CPU that gains one
gains a warning, and a program that used the word keeps building.

**Version names.** The language is named by the major version of the command: nt65 1 is the
language every `nt65` 1.x builds. A minor release adds what is not breaking, and a patch release
only fixes. No source file or project file states a version; a project that needs a particular
release says so where it says which cc65 it builds with.

## Appendix A. Grammar

The grammar of what the parser reads. Expressions follow the precedence and the parenthesis
rules of §9, and what a construct means, and where it may stand beyond what is written here,
is in the sections above.

```text
; Quoted words are tokens, except that 'a?', 'dp*', 'z:' and the like are a name and the
; mark after it. Directives, mnemonics, registers and the contextual words (dp, bank,
; mirrors, as, proc, zp, abs, far, placed, placeable and the state items) match without
; regard to case.
; What the parser reads and the binder then rejects is noted in comments.
; No mnemonic is reserved (§4), so every `ident` that names a declaration below may also be
; a mnemonic: `rts:` is a label, `lda = 5` a constant and `bne!(x)` a macro call, because
; what follows the first word is what decides. A register may not, except as a `member-name`.
file        := module-decl item* (region item*)*
module-decl := '.module' module-path (':' ('placed' | 'placeable'))?   ; first
region      := '.segment' ident NL                    ; at file level only
item        := const | config | data-decl | padding | proc | multiproc | extern-proc | scope | macro
             | enum | struct | union | charmap | list | func | signature | export | import | use
             | cpu | segment-decl | segment | if-block | repeat-block | each-block | assert
             | warning | error | place
config      := '.config' ident '=' expr                ; at file level, outside every block
place       := '.place' module-path                    ; at file level, in no block but a region
assert      := '.assert' expr (',' string)?
warning     := '.warning' string
error       := '.error' string
cpu         := '.cpu' ('6502' | '6502x' | '65sc02' | 'r65c02' | '65c02' | '65816')
segment-decl := '.segment' ident ':' size (',' seg-attr)*
seg-attr    := 'dp' '=' expr | 'bank' '=' expr | 'mirrors' '=' '[' banks? ']'
             | 'space' '=' ident                     ; a space the project declares
banks       := expr ('..' expr)? (',' expr ('..' expr)?)*
padding     := '.res' expr (',' expr)? | '.align' expr (',' expr)?
local       := '@'ident                               ; one token
label-line  := (ident | local) ':' (instr | data | macro-call)?   ; inside a proc
const       := (ident | local) '=' expr
data-decl   := '.data' ident ':' data
             | '.data' ident '{' NL mixed* '}'
mixed       := data | data-decl | local ':' data? | macro-call
             | if-block | repeat-block | each-block  ; their contents mixed too
data        := element count? values?
             | element count? braced
             | element count '{' NL value-line* '}'
             | '.type' path '{' NL (init NL)* '}'
             | directive (expr (',' expr)*)?          ; `.res`, `.align`, `.incbin`,
                                                      ; `.lobytes`, `.hibytes`, `.bankbytes`
             | '.strz' expr                            ; one text
element     := '.byte' | '.word' | '.long' | '.dword' | '.beword' | '.belong' | '.bedword'
             | '.addr' | '.faraddr' | '.type' path
count       := '[' expr? ']'
values      := expr (',' expr)*                       ; with no count only
values-list := value (',' value)*
braced      := '{' (init (',' init)*)? '}' | '{' values-list '}'
value       := expr | braced
value-line  := values-list NL
             | if-block | repeat-block | each-block  ; their contents value lines too
init        := member-name '=' value
proc        := '.proc' ident (':' state ('->' state)?)? '{' NL body fallthrough? '}'
multiproc   := '.multiproc' path ',' ident (':' state ('->' state)?)? '{' NL body '}'
extern-proc := '.proc' ident '=' expr (':' state ('->' state)?)?
state       := state-item (',' state-item)*
state-item  := point-item | unchanged-item | '?' | 'near' | 'far' | 'inline' (expr | '.strz')
             | 'args' expr | 'interrupt' | 'noreturn' | keeps-item
             | path                                   ; a path names a signature set, first
keeps-item  := 'keeps' reg (',' reg)*                 ; reg is a, x, y or c (§7.7)
signature   := '.signature' ident '=' state
unchanged-item := 'a*' | 'i*' | 'e*' | 'dp*' | 'dbr*'     ; unchanged
point-item  := 'a8' | 'a16' | 'a?' | 'i8' | 'i16' | 'i?' | 'native' | 'emu' | 'e?'
             | 'dp' '=' expr | 'dp?' | 'dbr' '=' expr | 'dbr' '=' '[' banks ']' | 'dbr?'
enum        := '.enum' ident? '{' NL (enum-member | if-block)* '}'
enum-member := member-name ('=' expr)? NL
struct      := '.struct' ident? '{' NL member* '}'
union       := '.union' ident? '{' NL member* '}'
member      := member-name ':' (element ('[' expr ']')? | '.res' expr (',' expr)?) NL
             | struct | union                         ; anonymous: members without a scope
member-name := ident | register | mnemonic
charmap     := '.charmap' ident '{' NL (expr ('..' expr)? '=' expr NL)* '}'
                                                      ; characters, or a range of them
list        := '.list' ident '{' NL (expr (',' expr)* NL)* '}'
func        := '.func' ident '(' (ident (',' ident)*)? ')' '=' expr
scope       := '.scope' ident? '{' NL body '}'
segment     := '.segment' ident '{' NL (item* | body) '}'
body        := (item | label-line | instr | data | macro-call | assertion | ensure | frame
             | annotation | splice)*                  ; no proc inside a proc
splice      := ident                                  ; block parameter, in macros only
assertion   := '.state' state                         ; not near, far, inline, args,
                                                      ; interrupt, noreturn, a signature set
                                                      ; or the * items
ensure      := '.ensure' width (',' width)*           ; other state items parse, and are errors
width       := 'a8' | 'a16' | 'i8' | 'i16'
frame       := '.frame' ident ':' path
annotation  := '.next' (target (',' target)* | '?')     ; after a conditional branch, its
                                                      ; own target only: always taken
             | '.patch' target
fallthrough := '.fallthrough' path                    ; the last line of a proc's body, or of
                                                      ; a branch of an if-block ending it
target      := path                                   ; or an ident parameter, in macros;
                                                      ; a list, or data declared as addresses,
                                                      ; stands for its labels
path        := '::'? (ident | local) index? ('::' member-name index?)*
index       := '[' expr ']'                           ; an element of a counted declaration
module-path := ident ('::' member-name)*              ; always from the modules' root
instr       := mnemonic operand?                      ; mnemonics include jeq ... jvc (§7.6)
operand     := '#' expr
             | 'a'
             | prefix? expr (',' ('x' | 'y' | 's'))?
             | prefix? expr ',' expr                   ; bbr / bbs: zero page, branch target
             | '(' expr ')' (',' 'y')?                 ; indirect only when the parentheses
             | '(' expr ',' ('x' | 's') ')' (',' 'y')? ; hold the whole operand
             | '[' expr ']' (',' 'y')?
             | '#' expr ',' '#' expr                   ; mvn / mvp: source bank, destination bank
prefix      := 'z:' | 'a:' | 'f:' | 'd:'
macro       := '.macro' ident '(' (param (',' param)*)? ')'
               (':' state ('->' state)?)? '{' NL body '}'
param       := ident (':' kind)? ('=' (expr | '{' '}'))?
kind        := 'expr' | 'const' | 'ident' | 'operand' | 'block'
             | 'one' '(' word (',' word)* ')' | 'list' '(' kind ')'
             | 'const' '(' expr '..' expr ')'            ; the range it takes
             | 'operand' '(' word (',' word)* ')'        ; the modes it takes
             | path                                      ; an enum, whose members it takes
word        := ident | register | mnemonic
macro-call  := ident '!' '(' (arg (',' arg)*)? ')'
               ('{' NL body ('}' ident '{' NL body)* '}')?
arg         := (member-name '=')? (expr | '{' operand '}')   ; a word or a local is an expr
if-block    := '.if' expr '{' NL contents '}' ('.elseif' expr '{' NL contents '}')*
               ('.else' '{' NL contents '}')?
repeat-block := '.repeat' expr (',' ident)? '{' NL contents '}'
each-block  := '.each' expr (',' ident)? '{' NL contents '}'   ; a list, a named enum
                                                      ; or a list parameter
contents    := item*                                  ; at item level
             | body                                   ; inside a proc
             | value-line* | mixed*                   ; in a data body
             | enum-member*                           ; in an enum body
export      := '.export' export-item (',' export-item)*
             | '.export' (const | config | data-decl | proc | multiproc | extern-proc | scope | macro | enum
               | struct | union | charmap | list | func | signature | import | use)
                                                      ; a re-exported use names what it
                                                      ; re-exports: no '::' '*'
export-item := module-path (':' size)? ('as' string)?
use         := '.use' module-path (('::' '*') | ('::' '{' use-item (',' use-item)* '}')
               | ('as' ident))?                       ; at a module's top level
use-item    := ident ('as' ident)?
import      := '.import' import-item (',' import-item)*
import-item := ident (':' import-type ('in' ident)?)?    ; `in` a segment
             | ident '=' expr                        ; checked import
import-type := size | 'proc' '(' state? ('->' state)? ')'
             | size? element count?                  ; a typed import: no values
size        := 'zp' | 'abs' | 'far'
expr        := unary (binop unary)*                   ; precedence, and where parentheses
                                                      ; are required, in §9
unary       := ('+' | '-' | '~' | '!' | '<' | '>' | '^')* primary
primary     := number | char | string | cpu-name | '*' | '(' expr ')'
             | path ('(' (expr (',' expr)*)? ')')?    ; a charmap applied, or a .func call
             | builtin '(' (expr (',' expr)*)? ')'
binop       := '*' | '/' | '.mod' | '+' | '-' | '<<' | '>>' | '<' | '<=' | '>' | '>='
             | '==' | '!=' | '&' | '^' | '|' | '&&' | '^^' | '||'
builtin     := '.lobyte' | '.hibyte' | '.bankbyte' | '.loword' | '.hiword' | '.sizeof'
             | '.countof' | '.endof' | '.spanof' | '.strlen' | '.strat' | '.strsub' | '.strcat'
             | '.min' | '.max'
             | '.sqrt' | '.muldiv' | '.sin' | '.cos' | '.mincycles' | '.maxcycles'
             | '.addrsize' | '.target' | '.defined' | '.has' | '.select'
             | '.loadof' | '.runof'                     ; of a segment
             | '.mode' | '.byteof' | '.exprof' | '.empty' ; the last four in macro bodies
```

## Appendix B. ca65 macro patterns

These patterns are common in ca65 code, from cc65's own macro packages
(`.macpack generic`, `longbranch`, `cbm`, `apple2`, `atari` and `module`) to SNES
frameworks such as libSFX and structured-control macro libraries. The problem column
names what makes each hard to analyze: **text** (token substitution or splitting at
commas), **names** (declares or builds names in the caller), **order** (depends on what
has been seen so far, or on `.set`), **layout** (depends on addresses or distances),
**flow** (hides control flow or processor state), or **none**.

| ca65 pattern | problem | in nt65 |
|---|---|---|
| `.define SCREEN $0400` | text | a constant (§6.1) |
| function-like `.define`, such as cbm's `scrbyte(code)` | text | `.func` (§9) |
| `.define LIST a, b, c`, split with `.lobytes` and `.hibytes` | text | `.list` (§6.4) |
| include guards; `.global` in a shared include file | text | modules (§12) |
| `.set` counters that number states or IDs | order, names | `.enum` (§6.3) |
| `.ident` and `.concat` building `name_lo` or one label per entry | names | scopes; `.each` over an enum (§10) |
| `module_header`, which exports and declares a label in the caller | names | the caller writes the label and `.export`; `.if` on a define picks the segment |
| `add` and `sub` (generic), which count parameters because `add buf,x` splits at the comma | text | macros with an `operand` parameter (§11.2) |
| `bge`, `blt`, `bnz`, `bze` (generic) | none; bare invocation | macros |
| `bgt` (generic) as `beq *+4` then `bcs` | flow: a computed target | a macro with a local `@skip` |
| 16-bit `mov16`, `add16` and `cmp16`, choosing immediate or memory with `.match` | text | macros with `.mode` and `.byteof` (§11.2) |
| save sets, `push a,x,y`, with `.xmatch` | text | `list(one(a, x, y))` and `.each` (§11.2) |
| 6502 fallbacks for `phx` or `bra`, shadowing mnemonics under `.ifp02` | text, modal CPU | named macros under `.if .target(6502)` |
| skip bytes, `.byte $2c` | flow | `.next` (§7.4) |
| `jeq`, `jne` and the rest of `longbranch`, short only toward a label already seen | order, layout, text | built-in long branches (§7.6) |
| structured `if`, `else` and `while` with `.set` label stacks and parsed conditions | order, names, text | block macros with `one` parameters and continuation blocks (§11.4) |
| RTS dispatch tables, `.addr h-1` | flow | `.each` to build the table, `.next` naming it (§7.4) |
| `jsr print` followed by an inline string | flow | an `inline` signature (§7.3, §7.4) |
| relative calls, `per` then `brl`, such as libSFX's `bsr` | flow | recognized as a call (§7.3) |
| `php`, `sei` … `plp` critical sections | none | a block macro |
| `A8` and `A16`: `sep` or `rep` with `.a8` | flow: an unchecked hint | the flow analysis (§7.3) |
| width stacks such as libSFX's `RW`, `RW_push`, `RW_pull` and `RW_assume` | order: the stack follows the text | `.ensure` and `.state` (§7.3) |
| `proc` and `endproc` macros carrying width state | order | proc signatures (§7.3) |
| setting D and B, such as libSFX's `dpage` and `dbank` | flow | recognized idioms (§7.5); a macro is fine |
| an offset from the current direct page, such as libSFX's `dpo()` | order | `d:` (§7.5) |
| stack frames with `tsc` and `tcs` and `.struct` offsets on `,s` | flow | `.frame` (§7.3) |
| screen codes, `scrcode` (cbm, apple2, atari), recursing over nine parameters | text | `.charmap` (§8) |
| records: metasprites, actors, level objects | none, but the label has no fields | initialized `.type T { }` data (§6.3), or a macro when computed |
| terminated lists, computed tables, register-init pairs | none | macros, `.repeat`, `.each` and `.func` |
| file and cartridge headers: iNES, the C64 BASIC stub, Atari XEX | layout | `.endof` and `.spanof` (§7.6) with macros |
| `zp_var name, 2` through `.pushseg` | names, modal segment | a nested segment block (§5.2) |
| allocators that advance `.set RAM_PTR` | order, names | segments placed by ld65, or `.struct` offsets from a base |
| `.ifdef DEBUG` trace and break wrappers | none | `.if` on a define, with macros |
| emulator hooks | flow | `wdm #n` (§7.1) |
| checking that a symbol is zero page | order | `.assert .addrsize(sym) == 1` (§9) |
| CPU-conditional code, `.ifp816` | modal CPU | `.if .target(65816)` (§9) |
| page-crossing and timing checks | layout | `.assert`, checked at link time when it cannot be earlier (§7.6) |
