# nt65 — Norristown Assembly Language

**Status:** draft design, non-normative. This document describes the shape of the
language and the reasoning behind it. It is not a specification; the implementation
decides details, and this document is updated when the implementation teaches us
something.

## 1. What nt65 is

nt65 is an assembly language for the 6502, 65C02 and 65816 that **transpiles to ca65**.
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

1. **Toolchain.** `nt65 build` writes one ca65 source file per `.nt65` file. The project
   assembles those files with its existing ca65, built from the cc65 commit nt65 pins
   (§13), and links them with its existing ld65 configuration. nt65 never runs ca65 or ld65, and never reads,
   requires or changes a linker configuration.
2. **Command line.** The output assembles to the same bytes under any ca65 options the
   project uses (§13). Where an option genuinely conflicts with nt65's declarations,
   such as a `-mm` memory model against the segment table or a `-D` name against a
   declared name, ca65 reports an error.
3. **Segments.** Output goes only to segments named in the source or in `nt65.json`,
   each with its declared address size. Within a file, items keep their source order in
   each segment; the order across files is the project's link order.
4. **Symbols.** Everything shared with ca65 is an ordinary linker symbol (§12). An
   export is spelled as its nt65 name, or `outer__inner` for an exported interior label,
   and carries the address size nt65 uses: `.exportzp` for a zero-page label or a
   constant below `$100`, `far` for a far label or constant. Enum, struct and union
   members are exported as flat constants, and a checked import is verified by ld65.
5. **Nothing extra.** nt65 adds no runtime, library, segment or startup code. Every
   label it generates is local to its file, except an `f__end` label that another file
   uses through `.endof`.
6. **Deterministic output.** The same sources and configuration produce byte-identical
   output, and `nt65 build` rewrites only the output files whose contents change, so a
   build system reassembles only what an edit affected.
7. **Debugging.** With `ca65 -g` and `ld65 --dbgfile`, debug information refers to
   `.nt65` files and lines (§13), and generated names are derived from source names.

nt65 does not promise:

- to read ca65 source, include files, macros or object files;
- to check calls into nt65 routines from ca65: a ca65 caller must honour the routine's
  declared signature, and checking stops at the boundary;
- a calling convention: signatures describe processor state only, and cc65's C calling
  convention (the software stack, return values in A/X) is the programmer's job;
- stable generated names across edits: only exported names and the fixed spellings
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
| `.segment`, `.org`, `.pushseg`/`.popseg` | modal | segment *blocks* (§5.2) |
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
   symbols (§10). Cycles among constant definitions are errors, not sequencing puzzles.
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
change it; macros are expanded, `.repeat` and `.each` are unrolled and `.if` is resolved
before output.

The output assembles to the same bytes under any ca65 command line a project already
uses, with the pinned ca65 (§13). The header switches off everything a command-line option
can switch on (§13), and the output is written so that the remaining options (`-t`,
`-D`, `-mm`) cannot silently change it. If ca65 reports an error or a warning on nt65
output, that is an nt65 bug.

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
- **Leading whitespace is insignificant.** Labels may be indented.
- **Identifiers:** `[A-Za-z_][A-Za-z0-9_]*`, case-sensitive. Scoped names use `::`
  (`gfx::init`, `::top_level`); `::` is one token, so `z::foo` walks into scope `z`
  and a prefix on a global reference is written `z: ::foo`. Cheap locals are `@name`
  (§6.2).
- **Reserved words:** all mnemonics of all three CPUs and the long branches of §7.6
  (case-insensitive), the
  registers `a`, `x`, `y`, `s` (case-insensitive), and every `.directive` (also
  case-insensitive). The mnemonics are the canonical WDC names, with the bit number in
  `bbr0`–`bbr7`, `bbs0`–`bbs7`, `rmb0`–`rmb7` and `smb0`–`smb7` as ca65 spells them;
  ca65's alternative 65816 spellings (`tad`, `swa`, ...) are ordinary identifiers. A user
  symbol, a macro parameter or a member of an anonymous enum cannot be named `lda` or
  `X`. Members of a named struct, union or enum are exempt: they are always reached
  through `::`, where a register name or mnemonic is unambiguous (`Point::x`). The
  mnemonic set is part of the language version: adding a CPU later reserves new words
  and is a breaking change.
- **Numbers:** `$1F` hex, `%1010` binary, `255` decimal, `'c'` character. `65c02` is a
  CPU name, one token, valid only where a CPU is named (§5.1).
- **Strings:** `"..."` with fixed escapes `\n \r \t \\ \" \' \xHH`, which character
  literals share. Outside a charmap
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
| `ident =`, `@name =` | constant; inside `.enum`, a member with an explicit value; inside a `.tag` initializer, a member's value (§6.3) |
| `ident !` | macro invocation |
| `ident` alone | bare identifier: an enum member inside `.enum`, a list item inside `.list` (§6.4), a `block` parameter splice inside a macro body (§11.4), an error elsewhere |
| mnemonic | instruction |
| any other expression | list items inside `.list` (§6.4), an error elsewhere |
| `}` | block close, optionally continuing with `.else {`, `.elseif expr {` or a macro's next block, `name {` |

**Block structure is a layer over lines, not part of parsing them.** A line whose last
token is `{` opens a block, unless that `{` is inside a parenthesis still open on the
line, so a half-typed `m!({` does not swallow the rest of the file; a line whose first
token is `}` closes one; a `} .else {` or `} name {` line does both and counts as 0; any
other brace (the `{operand}` grouping in macro arguments, §11, and a one-line `.tag`
initializer, §6.3) must be balanced within its own line. The block layer is therefore a
per-line value of +1, −1 or 0, and the tree is recovered from a prefix sum without
looking inside any line. Every block opener is a keyword line, a macro call or a
continuation line, which gives error recovery an anchor when braces are unbalanced
mid-edit; there are no bare `{` blocks, `.scope {` serves that purpose.

Recovery uses that anchor only when it must. While the braces balance, the tree is exactly
the prefix sum, so a construct written where it may not appear, such as a `.proc` inside a
`.proc`, keeps the structure it was written with and is reported by the parser rather than
guessed at by the block layer. Where they do not balance, a `}` with no open block is
reported and treated as an ordinary line, and a `.proc` or `.macro` opener inside a proc or
a macro closes the blocks back to outside it, so the items after a missing `}` are still
found.

## 5. Program structure

A program is the set of `.nt65` files handed to the transpiler. Each file is a sequence
of **items**: labels, constants, data, `.proc`, `.scope`, `.macro`, `.enum`, `.struct`,
`.union`, `.charmap`, `.list`, `.func`, `.export`, `.import`, `.if`, `.repeat` and
`.each` at item level, segment
declarations and segment blocks.

### 5.1 CPU

```nt65
.cpu 65816
```

One CPU per program. It may be given on the command line or by a `.cpu` item; every
file that states it must agree. The CPU does not affect parsing: all mnemonics and
addressing modes always lex and parse, and using one the target lacks is a semantic
diagnostic ("`stz` is not available on the 6502"). Code that differs between CPUs tests
`.target(65c02)` in an `.if` (§9, §10); the CPU is configuration, like a define.

### 5.2 Segments

```nt65
.segment "ZP2": zp              ; declaration: this segment is zero page

.segment "ZP2" {
ptr:    .res 2
tmp:    .res 1
}

.segment "CODE" {
...
}
```

A segment block places its items in the named segment. `.zeropage`, `.code`, `.bss`,
`.data` and `.rodata` are shortcuts for the standard names. Items outside any segment
block go to `CODE`.

A segment's address size (`zp`, `abs`, `far`) is declared **exactly once** per program,
by a `.segment "NAME": size` declaration item (no braces) in any one file or in the
project configuration. Blocks only name the segment. The standard names are
predeclared (`ZEROPAGE` as `zp`, the rest as `abs`). A segment block that names a
segment declared nowhere is an error, so a misspelled name is caught before ld65 runs.
The address size is what nt65 uses to size references to symbols in that segment
(§7.2), so keeping it in one place means sizing depends on a small table rather than on
a fold over every file.

`far` needs the 65816. A far address is a bank and an offset, which no earlier processor
has, and ca65 rejects a far address size outright when its CPU setting is a 6502 or a
65C02 — for a segment declaration, an `.export` and an `.import` alike. A program built
for those processors that declares a far segment, or imports a far symbol, is an error
where it is written rather than output ca65 refuses (§3.2).

On the 65816 a segment declaration may also carry `dp = expr` and `bank = expr`, which
§7.5 uses to check direct-page and data-bank assumptions. Both are constants, each given
at most once; `dp` is for a `zp` segment, the only kind reached through the direct page.
On the other processors they are accepted and nothing reads them.

These declarations restate facts that live in the ld65 configuration, which nt65 does
not read or check. In particular a `zp` segment is emitted with `z:` operands, so ld65
must place it where its symbols are direct-page offsets (`$00`–`$FF`, relative to D);
`#<sym` and `.addr sym` on such a symbol then yield that offset, not an absolute
address, and on the 65816 an absolute operand on it is an error when its segment's `dp`
is not 0 (§7.2). Getting `dp =` and the linker config to agree is the programmer's job.

A segment block may appear anywhere an item may appear, including inside a `.proc`.
It changes the segment of its contents, not their scope, which is the structured form
of the `.pushseg` / `.popseg` idiom:

```nt65
.proc draw {
    ldx #0
@loop:
    lda table,x
    ...
    .rodata {
    table: .byte 1, 2, 4, 8       ; this is draw::table
    }
}
```

A nested segment block is a separate flow region (§7.3): fall-through never enters it,
and the statement after the block follows the statement before it. If it contains
code, its first statement needs a `.next` edge or a declaration like any other entry
point, so it starts at a label: code at its start with no label is reached by nothing, and
is warned about as a label nothing reaches is. A nested segment block that names the segment it is already in is an error: its
contents would stay inline in the byte stream, where fall-through does reach them.

By convention the contents of a top-level segment block are not indented.

### 5.3 Project file

A project is described by `nt65.json` in the project root. `nt65 build` reads it;
`nt65 build main.nt65 --cpu 6502` works without one for single-file use.

```json
{
  "cpu": "65816",
  "files": ["src/**/*.nt65"],
  "out": "build",
  "defines": { "DEBUG": 1, "VERSION": "$0102" },
  "segments": {
    "ZP2":   { "size": "zp",  "dp": "$2100" },
    "WRAM":  { "size": "abs", "bank": "$7e" },
    "BANK1": { "size": "abs", "bank": 1 }
  },
  "ranges": {
    "$2100-$21ff": ["$00-$3f", "$80-$bf"],
    "$4200-$43ff": ["$00-$3f", "$80-$bf"]
  }
}
```

- `cpu`: `6502`, `65c02` or `65816`. A `.cpu` item in a file must agree.
- `files`: globs. Order is not significant.
- `out`: where each `foo.s` goes, mirroring the source tree.
- `defines`: the build configuration. Each define is a constant visible in every file,
  as if declared and exported once, and defines are the only symbols an `.if` condition
  may test (§10). `-D NAME=value` on the command line adds a define or overrides one
  given here, and `-D NAME` on its own defines it as 1, for a define a condition only
  tests. A declaration in a file may not reuse a define's name. There is no way
  to declare a define in a source file. The output always writes a define as its value,
  never by name, so a `-D` given to ca65 cannot collide with it.
- `segments`: the segment table of §5.2 and §7.5. A segment declared here may not also
  be declared in a file.
- `ranges`: which banks an absolute *constant* address in each range may be accessed
  from (§7.5), for hardware registers that are mirrored in some banks only. A key is a
  range of addresses or a single address, each item a range of banks or a single bank,
  and no two keys may overlap.

Numbers are JSON numbers or strings in nt65 number syntax.

## 6. Symbols and scopes

### 6.1 Declarations

| form | declares |
|---|---|
| `name:` | an address (label). May be followed by an instruction, data directive or macro call on the same line. |
| `NAME = expr` | a constant if `expr` contains no address symbols, otherwise an **address alias**, sized, exported and imported like a label. Single assignment; forward references allowed; cycles are errors. A constant holding text is used through `.strlen` and `.strat`, and cannot be written as data. |
| `@name:`, `@name = expr` | a cheap local: a label or constant private to its proc or scope (§6.2). |
| `.proc name [: signature] { ... }` | a label **and** a scope, with a processor-state signature (§7.3). At file level or in a `.scope` outside any proc: procs do not nest. |
| `.proc name = expr [: signature]` | an **extern proc**: a routine with a signature and no body, at a constant address (a ROM or toolbox entry, §12) or naming another routine, which is how a routine is aliased. An alias that writes a signature must write the routine's, and one that writes none takes it. |
| `.scope [name] { ... }` | a scope. It is a namespace with no address of its own: its name is not an operand. |
| `.enum [name] { ... }` | constants (§6.3). |
| `.struct name { ... }`, `.union name { ... }` | member offsets and a size (§6.3). |
| `name: .tag T [, n]`, `name: .tag T { ... }` | an instance, an array of instances or an initialized instance: a label that is also a scope of its fields (§6.3). |
| `.charmap name { ... }` | a text encoding (§8). |
| `.list name { ... }` | a named sequence of expressions (§6.4). |
| `.func name(...) = expr` | a pure expression function (§9). |
| `.macro name(...) { ... }` | a macro (§11). |

Every symbol carries what analysis needs: whether it is a constant or an address, its
address size (from its value, or from its segment), and for data its size in bytes
(`.sizeof(name)`).

### 6.2 Scoping rules

- Name lookup proceeds from the innermost scope outward to file scope, then to symbols
  exported by other files (§12). `::name` starts at file scope; `a::b` walks into a
  named scope.
- **Cheap locals** `@name` are labels or constants private to the innermost enclosing
  `.proc` or `.scope`. A macro expansion and each `.repeat` or `.each` iteration also have their
  own. `.if` bodies and segment blocks do not start a new set, so an `@table` in a
  nested `.rodata { }` block is visible to the proc's code.
  - A reference looks outward through enclosing scopes, so a nested `.scope` can branch
    to its proc's `@done`. Procs do not nest (§6.1), so this never reaches into another
    routine.
  - A cheap local cannot be reached with `::` or exported, and has its own namespace,
    so `@loop` never collides with a regular `loop`.
  - A name may be declared once in its scope. To reuse one, open an anonymous
    `.scope { }`, which is inline code, not a separate routine.
  - A cheap local outside any proc, scope, macro body, `.repeat` body or `.each` body is
  an error.

  Because nothing outside a proc can name the cheap locals declared inside it, every
  edge into such a label is visible to the proc's flow analysis (§7.3). The output gives
  each cheap local a generated name (§13).
- Two files may not export the same name.

```nt65
.proc init {
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
    pos:    .tag Point
    hp:     .byte
    name:   .res 16
}

.union Value {
    b: .byte
    w: .word
}
```

**Enumerations.** Members are constants in the scope `Color`, referenced as
`Color::red`. A member without a value is the previous member plus one, starting at 0;
explicit values must be constant. An anonymous `.enum { }` declares its members into
the enclosing scope.

**Structures.** Members are written like data declarations, but the directive reserves
space instead of emitting it: `.byte`, `.word`, `.dword`, `.addr` and `.faraddr`
reserve one element, `.res n` reserves n bytes, `.tag T` reserves `.sizeof(T)` bytes
and `.tag T, n` reserves n of them, and no operand values are allowed. Each member is a
constant offset in the scope `Point` (`Point::x` is 0, `Point::y` is 2) with a byte
size (`.sizeof(Point::y)` is 2), and `.sizeof(Point)` is the total. Members of a `.tag`
member are reachable through it: `Player::pos::y` is 2. An anonymous `.struct { }`
inside a struct groups members without introducing a scope. A **union** is a struct in
which every member is at offset 0 and the size is that of the largest member. Member
names may be register names or mnemonics (§4).

**Instances.** In data, `.tag T` allocates one instance and `.tag T, n` an array of n:

```nt65
player: .tag Player
actors: .tag Player, MAX_ACTORS
```

A label declared with `.tag` is also a scope whose members are the fields of its first
element, each an address symbol in the label's segment with the member's size:
`player::hp` is `player + Player::hp`, and `player::pos::y` is `player + 2`. For an
array, `.countof(actors)` is `MAX_ACTORS`, `.sizeof(actors)` is
`MAX_ACTORS * .sizeof(Player)`, and `.sizeof(Player)` is the stride, so with X holding
an element's offset `lda actors::hp,x` reads that element's `hp`. Nothing else changes:
these are ordinary indexed operands that nt65 sizes from the segment of the label.

**Initialized instances.** `.tag T { ... }` emits one instance with values, which is what
record macros are written for in ca65:

```nt65
.struct Actor {
    x:  .word
    y:  .word
    hp: .byte
    ai: .addr
}

boss:   .tag Actor { x = 100, y = 40, hp = 99, ai = chase }

hero:   .tag Actor {
    x = 16
    hp = 3
    ai = player_input
}
```

Each value names its member, so the struct decides the layout and reordering its members
cannot misplace a value. A member is named at most once, and a member not named is
zero. A value must fit its member as it would fit the matching data directive, and a
member of one element takes one value, so text longer than a byte is not one; a `.res n`
member takes a string of at most n bytes, padded with zeros; a `.tag` member takes a
nested one-line initializer, `pos = { x = 1, y = 2 }`; a union takes at most one member.
The one-line form balances its braces on its line, and the multi-line form is a block
with one `member = value` per line. The label is an instance like any other: `boss::hp`
and `.sizeof(boss)` need nothing more.

The output does not use ca65's `.enum`, `.struct`, `.union` or `.tag` (§13). Enum
members are emitted as constants, member offsets and type sizes as numbers with a
comment naming the path, an instance as `.res` of its size, and an initialized instance
as one data directive per member.

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

.rodata {
lo:     .lobytes handlers
hi:     .hibytes handlers
}
```

A line holds one or more comma-separated items, constants or addresses, whose names
resolve where the list is declared. A list name stands for its items in the operands of
`.byte`, `.word`, `.dword`, `.addr`, `.faraddr`, `.lobytes` and `.hibytes`, in `.each`
(§10), and as a `.next` target (§7.4). `.countof(handlers)` is the number of items, a
constant. A list emits nothing by itself, and is exported and used across files like a
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
    jmp (vector)        ; indirect
    bne @loop           ; relative
    jeq @far            ; long branch — see 7.6
    brk #0              ; signature byte required, see below
```

An operand that begins with `(` is indirect only when the parentheses hold the whole
operand: `lda (ptr)` and `lda (ptr),y` are indirect, and `lda (hi + lo) * 2` is an
ordinary expression that happens to start with one, as it is in ca65.

65C02 adds `(zp)`, `(abs,x)`, and the `bbr`/`bbs`/`rmb`/`smb` forms, of which `bbr` and
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
   `$10000` absolute, otherwise far.
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

**Signatures.** A routine declares its state at entry and, after `->`, at exit. Exit
defaults to entry, item by item: `a16, i8 -> a8` returns with `i8`. `emu` makes both widths
8, as it does in `.state`, and an exit that names a 16-bit width without naming the mode is
in native mode, the only one that width holds in: `emu -> a16, i16` returns native. The
items are:

| item | meaning | default |
|---|---|---|
| `a8` `a16` `a?` `a*` | accumulator width | `a8` |
| `i8` `i16` `i?` `i*` | index width | `i8` |
| `native` `emu` `e?` `e*` | emulation flag | `native` |
| `near` `far` | entered by `jsr`/`jmp` and left by `rts`, or by `jsl`/`jml` and `rtl` | `near` |
| `inline n`, `inline .asciiz` | the routine returns past data written after each call: n bytes, or one `.asciiz` (§7.4) | none |
| `dp = e` `dp?` `dp*`, `dbr = e` `dbr?` `dbr*` | direct page and data bank (§7.5) | `dp*`, `dbr*` |

`?` means unknown, for interrupt handlers and entry points reached from outside nt65.
`*` means unchanged: the routine assumes nothing about that part of the state and
returns it as it found it, so a caller keeps what it knew across the call. In the body a
`*` value counts as unknown wherever a known one is needed, and at every `rts` or `rtl`
it must still hold the entry value: nothing changed it, or a pull restored it from the
analysis stack (below). In an exit list, `*` is allowed only for an item that is `*` at
entry.

Three kinds of routine carry a signature: a proc with a body, an extern proc
(`.proc CHROUT = $FFD2: a8, i8`, §6.1) and an imported routine
(`.import _printf: proc(a8, i16)`, §12). On the 65816 anything called must be one of
these.

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
| `jsr f`, `jsl f` | state must match f's entry; becomes f's exit, except that items f declares `*` keep their value |
| `per L-1` directly followed by `brl f` or `bra f` to a routine, where `L` labels the statement after the branch | a relative call, as `jsr f`; with `phk` directly before the `per`, as `jsl f` |
| `jmp f`, `jml f`, or a branch or `.next` edge to f, where f is a routine (a tail call) | state must match f's entry; f's exit, with its `*` items taken from the state here, must match this proc's exit; f must be `near` or `far` as this proc is. An unconditional transfer ends the path |
| `jsr (t,x)` with `.next` naming routines | state must match every entry; becomes the merge of their exits |
| `rts`, `rtl` | state must match the proc's exit; path ends |
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
a P, `pld` a D and `plb` a B (§7.5). The stack becomes unknown after `txs` or `tcs`,
after a push or pull whose size is unknown, and at a merge where the incoming stacks
differ. So `php` … `jsr` … `plp`, and a save and restore on either side of a label, need
no annotation.

**Assertions.** `.state` takes the same items as a signature, except `near`, `far` and
the `*` items, which describe a routine rather than a point in it:
`.state a16, i8, dbr = $7e`. Each item asserts and sets: if that part of the state is
known and differs, error; if it is unknown, this sets it. An item with `?` (`a?`, `e?`,
`dp?`) deliberately forgets. `emu` also makes both widths 8, which is what emulation mode
pins them at. Placed directly after a label, a `.state` is that label's declaration.

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
room for it. If the analysis stack is unknown there, as it is after `tcs`, it becomes
those bytes with nothing known beneath them. In a stack-relative operand, `name::member,s`
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
what the proc has pushed is an error. A frame ends with its proc.

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
  match this proc's, because the target returns to this proc's caller;
- every call targets a routine with a signature (proc, extern proc or `proc(...)`
  import). A local subroutine is a separate proc, grouped with its callers in a
  `.scope` when a shared namespace helps; procs do not nest (§6.1). A routine with
  several entry points is written as adjacent procs joined by `.next` (§7.4). On the
  6502 and 65C02, where there is no state to contract, a call may target any address
  expression.

Outside any `.proc` there is no processor state: on the 65816 `rep`, `sep`, `xce`,
`plp`, `.state`, `.ensure`, `.frame` and any width-dependent immediate are errors, since code that touches processor
state belongs in a proc, and the checks of §7.5 do not apply.

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

On the 6502 and 65C02 the output contains no width directives. nt65's own tests
assemble its output with `ca65 -l` and compare each line's length with the length nt65
computed (§7.6), which catches any disagreement about widths or addressing modes.

### 7.4 Unchecked constructs

Assembly's tricks (computed jumps, jumps to places that are not instruction boundaries,
data in the instruction stream, self-modifying code) are all allowed. Each has a
syntactic fingerprint, so nt65 finds it in one pass and requires an annotation that
tells the analysis what it cannot see. The annotations are claims: nt65 checks that the
claims and the code are consistent with each other, which is the same contract as the
proc's own entry declaration. On the 6502 and 65C02 nothing consumes processor state, so
the annotations are accepted and their names checked, but none is required; the
unreachable-label warning applies on every CPU.

Two directives carry all of it. Each applies to the statement immediately above it (for
a macro call, the last statement of its expansion, §11.3) and comes before any following
label:

- `.next @a, @b` replaces the analyzer's reading of where flow goes after the
  statement. Each named target gets an edge carrying the current state; on a call, the
  targets are routines and the state after the call is the merge of their exits.
  Targets may be cheap locals, scoped paths (`gfx::init`) and, inside a macro body,
  `ident` parameters. A target naming a list (§6.4), or a data label whose items are all
  code labels, each optionally minus 1 as in an RTS dispatch table, stands for every one
  of those labels. `.next ?` ends the path with nothing checked.
- `.patch @op` acknowledges that the store above it writes into the instruction at
  `@op`.

| quirk | how it is recognized | what is required |
|---|---|---|
| indirect jump: `jmp (t,x)`, `jml [t]` | addressing mode | `.next` listing the targets, or `.next ?` |
| indirect call: `jsr (t,x)` | addressing mode | `.next` listing the routines; the call returns with the merge of their exits |
| `rts` used as a jump | a block pushes a code label and then returns | `.next` on the `rts` |
| a routine that returns past inline data: `jsr print` then `.asciiz "hi"` | the routine's signature declares `inline` (§7.3) | the data after each call matches the declaration: one `.asciiz`, or a run of data directives directly after the call that comes to exactly n bytes; the analysis skips it with no `.next`, on every CPU |
| jump to a computed address: `jmp lbl+3` | direct branch or jump operand is not a bare label or routine name | `.next` listing the real targets, or `.next ?` when the target is not an instruction boundary |
| label used as data: `.addr @h`, `lda #<@h` | a code label used anywhere except as a direct branch, jump or call operand, the argument of `.sizeof`, `.endof` or `.spanof`, or the `per L-1` of a relative call | a declaration (a `.state` after the label), unless a `.next` in the same proc names the label |
| label nothing names | no fall-through, branch, or address-taken use; a label on data is exempt | reported as unreachable; a declaration acknowledges it |
| data reached by fall-through: the `.byte $2c` skip, opcodes ca65 lacks | data directive inside a proc with a fall-through predecessor | `.next` on the data |
| jump to a label on a data directive | target's statement is data | `.next` on the data, plus a declaration on the label |
| jump into another proc's interior, exported inner label | scoped path to an inner label used as a target, `.export` of an inner label | a declaration on the label, which a jump from this file is checked against for the parts it gives. Such a label may be a jump target, never a call target |
| falling off the end of a proc | last block does not end in a transfer of control | `.next next_proc`, checked like a tail call and checked to be adjacent in the same segment |
| falling off the end of a segment block nested in a proc | its last block does not end in a transfer of control | `.next` saying where flow goes, or `.next ?`; a jump into and out of the block is followed like any other in the proc |
| a `plp` that pulls no saved P, non-constant `rep`/`sep`, `xce` not immediately after `clc`/`sec` | opcode | a `.state` before the next dependent use |
| handler or external entry point | proc header | `a?, i?` entry, so the first immediate before `rep`/`sep` is an error |
| self-modifying code: `sta @op+1` | store or read-modify-write whose operand references a code label | `.patch @op`; widths of `@op` are analyzed as written |

Examples. A jump table inside a proc: the targets need no declarations because the
`.next` edges carry the state at the jump.

```nt65
.proc dispatch: a8, i16 {
    lda cmd
    asl a
    tax
    jmp (@table,x)
    .next @move, @fire

@table: .addr @move, @fire

@move:
    lda #1
    rts
@fire:
    lda #2
    rts
}
```

Every item of `@table` is a code label, so `.next @table` says the same.

An interrupt handler, and a `plp` that restores a status byte saved elsewhere, so the
analysis stack holds no saved P for it:

```nt65
.proc nmi: a?, i? {
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
.segment "ZP2": zp, dp = $2100
.segment "WRAM": abs, bank = $7e
```

A routine's signature may carry the D and B values it assumes at entry and, after
`->`, at exit: `.proc hud: a8, i16, dp = $2100, dbr = $7e {`. `dp?` and `dbr?` mean
unknown. A routine that does not state them is `dp*, dbr*`: it assumes nothing about D
and B and returns them as it found them. `.state dp = expr` and `.state dbr = expr`
assert and set them like any other item (§7.3), and are the only way back from unknown.
A label a `.state` declares starts from D and B unknown, except that in a routine that is
`dp*` or `dbr*` it starts from them unchanged, so only a routine that declares D or B needs
its declared labels to say what they are.

**Transfer functions.** nt65 does not track register values, so it recognizes the
idioms that load D and B from constants and treats everything else as unknown:

| sequence | effect |
|---|---|
| `lda #const` then `tcd`, with A 16-bit | D = const |
| `pea const` then `pld` | D = const |
| `lda #const`, `pha`, `plb`, with A 8-bit | B = const |
| `phk` then `plb` | B = the declared bank of the enclosing segment |
| `pld`, `plb` that pull a D or B saved by `phd`, `phb` (the analysis stack, §7.3) | the saved value |
| `mvn #s, #d`, `mvp #s, #d` | B = d |
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
  segment with a declared `bank`, is an error if B differs;
- the same kind of operand, when it is a constant address covered by the project's
  `ranges` table (§5.3), is an error if B is not one of the permitted banks, which is
  how `sta $2100` with B at `$7e` is caught;
- operands that do not use B are exempt: long operands (`f:`), `jmp` and `jsr` (the
  program bank K), `jmp (abs)` and `jml [abs]` (a pointer in bank 0), `jmp (abs,x)` and
  `jsr (abs,x)` (K), and `pea` and `per` (no memory access);
- `jsr`, `jmp` and branches to a routine or a label whose segment declares a bank
  different from the caller's segment bank are an error, with `jsl`/`jml` as the fix;
- immediates such as `#<sym` are never checked.

When either side is undeclared or unknown, nothing is reported. Signatures are the
exception, as they are for widths: a call, a tail call or a jump to a declared label is
checked against the D and B its target declares, and a return against the ones its
routine declares, and there an unknown value, `*` included, is an error.

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
  may, including `.res` and `.repeat` counts.
- `.endof(x)` is the address just past a proc, a scope or a data declaration, and
  `.spanof(x)` is `(.endof(x) - x)`. They describe **layout** and are address
  expressions like any label difference: usable in operands, data and `.assert`, never
  where a constant is required (`.res`, `.repeat`, choosing an address size), and
  resolved by ca65 and ld65. The end of a proc or scope is the end of its own bytes in
  its segment; nested segment blocks do not count.

```nt65
    ldx #.spanof(reloc)             ; bytes to copy
    .word .spanof(module)           ; length in a header
.assert .spanof(irq) <= 64, error, "irq handler too big"
```

No nt65 constant is derived from code, so no size can depend on itself, and an edit
inside a proc never changes a constant another file uses. `.sizeof` of a proc or scope
is an error that points to `.spanof`. nt65 reports an `.assert` at edit time when it can
evaluate it; one it cannot, such as a span that contains `.align`, is passed to ca65 as
a link-time assertion.

**Branch range.** The distance from a relative branch to its target is known when both
sit in the same segment block with no `.align` between them; a nested segment block
contributes no bytes to the stream that encloses it. nt65 reports an out-of-range
8-bit branch (beyond −128..+127 from the following instruction) at edit time; `brl`
and `per` have 16-bit range. When the distance is unknown, ca65's own check stands.

**Long branches.** `jeq`, `jne`, `jcs`, `jcc`, `jmi`, `jpl`, `jvs` and `jvc` branch like
their short forms but reach any near target. Each is emitted as the short branch where
nt65 knows the target is in range, and otherwise as the inverted branch over a `jmp` to
the target. Every long branch starts short, and those found out of range are lengthened
until none changes, which terminates because branches only grow; a target at an unknown
distance is always long. ca65's `longbranch` package can choose the short form only for
a target it has already seen, so its forward branches are always long; here they are not.
For the flow analysis a long branch is a conditional branch to its target, and a long
branch to a routine is a tail call (§7.3). Its cycle count is that of the form chosen.

**Cycle counts.** Each instruction has a cycle interval [min, max] from the CPU's table
for its addressing mode and, on the 65816, its widths. Where the count depends on
something nt65 cannot know, the interval widens: a possible page crossing on an
indexed or indirect-indexed read adds one to max (on the 65816 with a 16-bit index the
extra cycle is always paid and the count is exact); a branch costs 2 not taken and 3
taken, plus 1 when a taken branch crosses a page on the 6502, the 65C02 and in
emulation mode. On the 65816 a direct operand costs one more when the low byte of D is
nonzero, which is known when D is known (§7.5). Tooling shows the interval per
instruction and per basic block. There are no cycle-count built-ins for `.assert`: a
sum along one path silently undercounts any loop or call on it, so it could not be a
true bound.

## 8. Data

```nt65
    .byte 1, 2, $ff, 'A', "text"
    .word $1234, label
    .dword $12345678
    .addr label                     ; 16-bit address
    .faraddr label                  ; 24-bit address (65816)
    .res 16                         ; 16 bytes of zero
    .res 16, $ff                    ; 16 bytes of $ff
    .asciiz "hello"
    .align 256
    .incbin "sprites.bin"
    .incbin "sprites.bin", 64, 32   ; from offset 64, 32 bytes
    .lobytes first, second, third
    .hibytes first, second, third
    .tag Player                     ; .sizeof(Player) bytes, fields as sub-symbols (§6.3)
    .tag Player, 8                  ; an array of 8 (§6.3)
    .tag Player { hp = 5 }          ; an initialized instance (§6.3)
    .addr handlers                  ; a list's items (§6.4)
```

An address slot holds an address of its width: `.addr` takes 0 to $FFFF and `.faraddr` 0
to $FFFFFF. A far address in an `.addr` or a `.word` is an error rather than its low 16
bits, which ca65 would keep in an `.addr` without a word; `.loword(x)` says those are what
is meant. In the same way an absolute or far address in a `.byte` or a one-byte immediate,
and a far one in a two-byte immediate, is an error that ca65 would otherwise report as a
range error: `<x` and `.loword(x)` say which part is meant.

A label on a data directive gets a `.sizeof` in bytes and a `.countof` in elements from
it: `.res 16` gives 16 and 16, `.word a, b` gives 4 and 2, `.tag Player, 8` gives
`8 * .sizeof(Player)` and 8, `.tag Player { ... }` gives `.sizeof(Player)` and 1, a list
counts one element per item, and a string one element per byte. `.align` has no
size, so `.sizeof` and `.countof` of it are errors; `.endof` and `.spanof` (§7.6) work on
any data label.

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

    .byte screen("HELLO WORLD")
    .byte screen('A')
```

A mapping may name any character, ASCII or not, and a character with no mapping is an
error when the mapping is applied. A mapping is an ordinary declaration,
exported and used across files like a constant. `.strlen(s)` and `.strat(s, i)` remain
for the cases a `.repeat` needs.

A value in a `.byte`, a `.word`, a `.res` fill or an immediate is not negative: ca65
refuses one, so nt65 reports it with its two's complement. A label with no data on its own
line measures nothing, and `.sizeof` or `.countof` of one is an error. `.countof` of an
enum is how many members it has.

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

`*` is the current address. Built-in functions: `.lobyte(e)`, `.hibyte(e)`,
`.bankbyte(e)`, `.loword(e)`, `.hiword(e)`, `.sizeof(x)` and `.countof(x)` (§6.3, §8),
`.endof(x)` and `.spanof(x)` (§7.6), `.strlen(s)`, `.strat(s, i)`, `.min(a, b)`,
`.max(a, b)`, `.addrsize(x)`, the address size in bytes (1, 2 or 3) that §7.2 gives a
symbol or expression, `.target(cpu)`, true when the program's CPU is the one named
(`6502`, `65c02` or `65816`), and `.defined(NAME)`, which is true if NAME is a define (§5.3) and false
otherwise. Naming a symbol the program declares is an error, since
conditions never test the program (§10). Macro bodies add `.mode`, `.byteof` and
`.empty` (§11).

**Functions.** `.func` declares a pure expression function, which is what a function-like
`.define` is used for in ca65:

```nt65
.func rgb15(r, g, b) = r | (g << 5) | (b << 10)

    .word rgb15(31, 0, 0)
```

A call is written like a charmap application, `name(args)`. The body is one expression
whose names resolve where the function is declared. Arguments are values, not tokens: a
call means its body with each parameter replaced by its parenthesized argument, so
`rgb15(1 + 1, 0, 0)` passes 2. A call is constant when its arguments are, and may then
appear wherever a constant may, including `.res` counts, but not in an `.if` condition: a
function is a declaration of the program, and conditions are answered before any declaration
is read (§10). Functions may call functions, but not in a cycle, which is an error whether or
not anything calls them. A function
is exported and used across files like a constant, and the output writes each call as
its parenthesized body.

nt65 evaluates every expression it can (anything built only from constants) and uses
the value for sizing and diagnostics. Expressions involving addresses are emitted
symbolically for ca65 and ld65 to resolve.

## 10. Conditional assembly and repetition

```nt65
.if DEBUG {
    jsr trace
} .elseif LEVEL > 2 {
    nop
} .else {
    ...
}

.repeat 8, i {
    .byte 1 << i
}

.assert .sizeof(table) == 32, error, "table must be 32 bytes"
.error "unsupported configuration"
```

`.if` and `.repeat` are allowed both at item level and inside procs.

**Conditions test the configuration, not the program.** An `.if` condition may use
literals, operators, built-in functions and defines (§5.3). Inside a macro body it may
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
`#if` in C or `#[cfg]` in Rust.

`.repeat` counts may be any constant (no addresses).

`.each` repeats its body once per item of a list (§6.4) or a `list` parameter (§11.2), or
once per member of a named enum, in order:

```nt65
.each handlers, h {
    .addr h - 1                     ; an RTS dispatch table
}

.each Cmd, c {
    .addr actions::c                ; one entry per member of Cmd
}
```

Over a list, the binding is each item's value. Over an enum it is the member as a name:
as an expression it is the member's value, and as the last component of a path it names
the member of that scope with the same name, so `actions::c` is `actions::move`, then
`actions::fire`. A table built this way stays in step with the enum it follows, which is
what ca65 code uses `.ident` for.

Over a list or a count the binding names no member, so a path ending in it is an error, and
so is a scope with no member of the name the binding stands for on some turn.

None of these reaches the output. nt65 resolves every `.if` and unrolls every `.repeat`
and `.each` itself; names declared inside a `.repeat` or `.each` body are distinct per
iteration, as macro expansion labels are, and nothing outside the body can name them. A
body holds nothing that is one thing for the whole file: no `.export`, `.import`, `.cpu`,
segment declaration, `.proc`, `.macro` or `.func`.

## 11. Macros

Most of what ca65 code uses macros for is a language feature in nt65: constants and
functions instead of `.define` (§9), charmaps instead of screen-code macros (§8),
initialized instances instead of record macros (§6.3), lists and `.each` instead of
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

    set16!(ptr, SCREEN)
    set16!({buf,x}, $1234)

.rodata {
tune:   note!(C4, frames = 8)
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
  exported macro uses without receiving it as a parameter must itself be exported, which
  nt65 checks. Names in a `block` argument resolve in the caller, including the caller's
  `@labels`. A macro cannot declare names in its caller: labels, constants and types in
  a body are local to each expansion, and to name what a macro emits, the label goes on
  the call line, `player_sprite: sprite!(...)`. Go to definition, rename and find
  references therefore work in and through macros without expanding them.
- **Constants and shapes.** No macro call appears in a constant, an enum, a struct or a
  union, and `.sizeof` and `.countof` of a label on a macro call are errors. `.endof`
  and `.spanof` work on it, because they are layout (§7.6). A typed record is an
  initialized `.tag` (§6.3).

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

- **Words.** A `one(...)` argument is a bare word: an identifier, a register or a
  mnemonic. It is parsed like any other argument and never looked up as a symbol; the
  call is checked against the listed words. A word may be passed on to a `one` parameter
  whose list contains it.
- **Lists.** A `list` parameter follows every other parameter except blocks and takes the
  remaining positional arguments. `.countof(p)` is a constant.

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
expansion, as it would to any statement above it (§7.4). That is how a caller annotates a
macro that leaves data in the instruction stream:

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
| `.export` | other files resolve names through the export map, and a file's interface (§14) would depend on expansion |
| a segment declaration, `.segment "X": zp` | the segment table is program-wide and declared exactly once; it would depend on how many times the macro is called |
| `.cpu` | the CPU is program-wide |
| `.proc` | inside a proc it would nest (§6.1); at item level it would need a name from the caller, and its signature is part of the file's interface. A wrapper is a block macro called inside a proc the caller declares |
| `.import` | redundant: a body resolves names where the macro is declared, and the output imports what an expansion uses (§12) |
| `.macro`, `.func` | a definition in a body could capture the enclosing macro's parameters, which would make definitions into templates, for no common use |

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
the items of a proc signature other than `near`, `far` and `inline` (§7.3). Unlike a
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
6502 and 65C02, signatures on macros are accepted and have no effect.

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

### 11.7 Output

nt65 expands macros itself and emits flat code, with a comment naming the invocation.
ca65's `.macro` is not used in the output, so nt65's macro semantics never depend on
ca65's.

## 12. Modules

Every file is a module. Its symbols are private unless exported:

```nt65
.export fill_page, SCREEN, Player, set16
```

References from another nt65 file simply name the symbol. What the output does depends
on the symbol's kind:

- **address symbols**, labels and address aliases alike, become `.import`/`.importzp`
  in the referencing file's output, sized from the declaration;
- **constants** whose value nt65 knows are emitted by value (`SCREEN = $0400`) in every
  file that uses them, because ca65 cannot use an imported symbol where it needs a
  constant (`.res`, `.if`, `.repeat`, `.sizeof`);
- **enums, structs, unions, charmaps, lists and functions** are used by value: an enum member becomes a
  constant, a member offset or type size a number, mapped text bytes, a list its items and a function
  call its body; **macros** are
  expanded in the referencing file;
- an **exported interior label** (`.export inner` inside `.proc outer`) is named
  `outer::inner` from other files and travels through the object file as
  `outer__inner` (§13).

`.export` may appear anywhere in the defining file.

**The object file is the only boundary with ca65.** nt65 never reads ca65 source and
ca65 never reads nt65 source; everything the two share is a linker symbol.

Symbols and routines that live outside nt65 (hand-written ca65, cc65 output, ROM entry
points) are declared explicitly:

```nt65
.import _printf: proc(a8, i16)      ; a routine, with its signature (§7.3)
.import zp_scratch: zp
.import far_table: far
.import VIC_BORDER = $D020          ; a constant whose value nt65 needs; checked at link
.proc CHROUT = $FFD2: a8, i8        ; a routine at a fixed address; emitted as a constant
```

On the 6502 and 65C02 the signature of a `proc(...)` import or an extern proc may be
empty.

- **ca65 modules** export symbols in the usual way. cc65's runtime library already
  exports its zero-page variables, so `.import sp: zp` needs nothing more.
- **Constants defined only in a ca65 include file**, such as hardware registers and
  struct offsets, reach nt65 through a small ca65 module that includes the file and
  `.export`s the names needed. An imported symbol is opaque to nt65: it can be an
  operand, sized by its import, but it cannot appear where nt65 needs its value (`.res`,
  `.repeat`, the `ranges` check of §7.5).
- **An import is not exported.** It is somebody else's symbol, and each nt65 file that
  uses it imports it.
- **A checked import**, `.import NAME = value`, gives nt65 the value. nt65 uses `value`
  wherever `NAME` appears, and the output imports `NAME` and asserts `NAME = value` with
  `lderror`, so ld65 fails the link if the ca65 definition differs.
- **nt65 exports** are ordinary symbols to ca65: addresses, routines and constants, and
  for an exported enum, struct or union its members as flat constants (`Color__red`,
  `Player__hp`). Each export carries the address size nt65 uses, so a zero-page label
  or a constant below `$100` is exported with `.exportzp`.
- **Macros do not cross** in either direction, and there is no `.include`. Definitions
  several nt65 files share, such as a machine's hardware registers, live in an nt65
  module that exports them.

## 13. Transpilation

One `foo.nt65` produces one `foo.s`. The output is readable ca65 with a header comment
and source spellings preserved where possible. It is deterministic: the same sources and
configuration give byte-identical output, and `nt65 build` rewrites a file only when its
contents change.

**The header makes the output independent of ca65's command line.** It sets the CPU,
turns smart mode off, makes symbols case-sensitive, and switches off every ca65
`.feature` that changes syntax (`addrsize` is deprecated and always on, and resetting
it would itself warn). Options such as `--feature bracket_as_indirect` (which would
silently turn `lda [dp],y` into `lda (dp),y`), `--smart`, `-i` and `--cpu` then have no
effect. A `65c02` program is set as ca65's `W65C02`, whose instruction set includes `wai`
and `stp`:

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
names. A top-level name keeps its spelling. A scoped name `outer::inner` becomes
`outer__inner`, and the end label behind `.endof(f)` is `f__end`; these spellings are
fixed, because other files and hand-written ca65 can refer to them once exported. Cheap
locals, labels from macro expansions and labels from `.repeat` iterations get names
derived from the source, such as `draw__loop` and, for a second `@loop` in the same
proc, `draw__loop_2`; the same source always yields the same names. An import keeps its
exported spelling, so a local name that would collide with one is renamed in the output;
this happens when an exported macro expands in a file that has its own symbol of the
same name. A fixed spelling (`outer__inner`, `f__end`) that collides with another name
is an error. A label
named `z` or `f` is written `z := *`, because ca65 reads `z:` at the start of a line as
an address-size prefix; references to it need nothing special. An assignment takes the
whole line, so anything that followed such a label goes on the next one.

**A prefix binds to the whole operand.** `z:ptr+1` sizes the expression, not just `ptr`.
Where an operand expression itself begins with `(`, as `lda (hi + lo) * 2` may (§7.1),
the output writes `z:+(hi + lo) * 2`: ca65 reads a `(` straight after a prefix as an
indirect operand, and a unary `+` keeps it an expression without changing what it is
worth.

**Debug information.** After the header, each output file names its source with
`.dbg file`: the path relative to the output file, the size, and a timestamp of zero,
so the output does not depend on file timestamps. A `.dbg line` directive precedes every
generated line that produces bytes, instruction or data. With `ca65 -g` and
`ld65 --dbgfile`, ld65's debug file maps each span of bytes to its `.nt65` file and line,
recorded as external source lines, as cc65 does for C; without `-g`, ca65 ignores the
directives. A line that produces no bytes gets none: ld65 attaches a span to whichever
line is in effect while bytes are generated, so a directive before a label or a constant
records a line covering nothing, which nothing can step to or break on, and a label's
address is that of the bytes after it either way. The lines of a
macro expansion map to the line of the call, the way C debuggers treat preprocessor
macros, and a comment naming the call precedes the expansion.

| nt65 | ca65 |
|---|---|
| file header | `.setcpu`, `.smart -`, `.case +`, every `.feature` switched off, then `.dbg file` |
| each generated line that produces bytes, and each `.assert` ca65 evaluates | preceded by `.dbg line` naming its `.nt65` file and line. ld65 reports imports, exports and link-time assertions at the `.s` line whatever the debug line says, so those get none |
| `a == b`, `a != b`, `a ^^ b` | `a = b`, `a <> b`, `a .xor b`; nt65's other operators are ca65's |
| `.segment "X": zp` declaration | nothing by itself |
| `.segment "X" { }` | `.segment "X": zeropage`, `absolute` or `far`, from the segment table ... (next segment) |
| nested segment block | `.pushseg` / `.segment` ... `.popseg` |
| `.proc f: a16, i8 -> a8, i8 { }` | `f:` and the body; the signature emits nothing by itself |
| `.proc CHROUT = $FFD2: ...` | `CHROUT = $FFD2` |
| `.scope n { }` | its contents, with names flattened (`n__name`) |
| `@name` | a generated name, unique in the file |
| `lda ptr` | `lda z:ptr` (size made explicit) |
| `lda d:$2105` | `lda z:$05`, from the known D (§7.5) |
| `jeq t` | `beq t`, or `bne` over `jmp t` to a generated label (§7.6) |
| a width-dependent immediate (65816) | preceded by `.a8`/`.a16` or `.i8`/`.i16`, unless the previous immediate for that register had the same width (§7.3) |
| `.next`, `.patch`, `dp =`, `bank =`, `inline` | nothing; they exist only for the analysis |
| `.state` | nothing; the widths it establishes size later immediates (§7.3) |
| `.ensure a16, i8` | the `rep` or `sep` the analysis requires there, or nothing |
| `.frame`, `locals::count,s` | nothing; the operand is its offset, with a comment naming the path |
| `brk #s`, `cop #s` | ca65's immediate form where the CPU setting accepts it, else `brk` and `.byte s` |
| `wdm #n` | `.byte $42, n` |
| `mvn #s, #d` | `mvn #s, #d` |
| `.if c { } .else { }` | resolved at transpile time; only the chosen branch is emitted |
| `.repeat n, i { }`, `.each l, v { }` | unrolled |
| `m!(...)` | expanded inline, preceded by `; m!(...)  file:line`; its lines map to the call's line (debug information) |
| `.enum Color { }` | a constant per member, `Color__red = 0` |
| `.struct`, `.union` | nothing by themselves |
| `.tag T, n` in data | `.res` of the total size |
| `.tag T { ... }` | a data directive per member, each with a comment naming it |
| `.list` | nothing by itself; its items where it is used |
| a `.func` call | its body, with each parameter replaced by its parenthesized argument |
| `Player::pos::y`, `player::hp` | `2`, `player+4`, each with a comment naming the path |
| `'c'`, `"text"`, `screen("HELLO")` | byte values, with the source text in a comment |
| `.asciiz "s"` | `.byte` with those values and a terminating `$00`: the text is bytes by then |
| `.endof(f)`, `.spanof(f)` | `f__end`, `(f__end - f)`, with `f__end:` after the last byte of `f` |
| `.export s` | `.export s`, `.exportzp s` or `.export s: far`, with nt65's address size |
| `.import N = v` | `.import N` and `.assert N = v, lderror, ...`; uses of `N` are emitted as `v` |
| `.incbin "f"` | `.incbin` with the path made relative to the output file |
| cross-file reference to an address | `.import s` or `.importzp s` in the referencing file |
| cross-file reference to a constant, enum, struct, charmap, list, function or macro | emitted by value, or expanded in the referencing file |
| `NAME = expr` | `NAME = expr`, for a constant or an address alias, written where it stands and opening no segment; one using `*` is in its segment |
| a define | its value |

### Example

`main.nt65`:

```nt65
; Fill four pages of screen memory with spaces, forever.
.cpu 6502

.zeropage {
ptr:        .res 2          ; destination pointer
frame:      .res 1
}

SCREEN       = $0400
SCREEN_PAGES = 4

.export fill_page

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

`main.s` (generated; the `.dbg line` directive before each generated line is omitted
here):

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
.dbg file, "main.nt65", 591, 0

.export fill_page

.segment "ZEROPAGE": zeropage
ptr:        .res 2
frame:      .res 1

SCREEN       = $0400
SCREEN_PAGES = 4

.segment "CODE": absolute
fill_page:
    ldy #0
fill_page__loop:
    sta (ptr),y
    iny
    bne fill_page__loop
    rts

main:
    ; set16!(ptr, SCREEN)  main.nt65:32
    lda #<SCREEN
    sta z:ptr
    lda #>SCREEN
    sta z:ptr+1
    ldx #SCREEN_PAGES
main__page:
    lda #$20                        ; ' '
    jsr fill_page
    inc z:ptr+1
    dex
    bne main__page
    inc z:frame
    jmp main
```

## 14. What tooling gets

Because declarations are a set and syntax is fixed, a language server can, from source
alone and without an assembler:

- parse any file in isolation and report syntax errors per line;
- resolve every reference (go to definition, find references, rename, including inside
  macro bodies and block arguments, without expanding a macro);
- show on hover a symbol's kind, value, address size, segment and byte size;
- diagnose wrong-CPU instructions, unavailable addressing modes, unexported cross-file
  references, unused symbols, and constant assertions;
- on the 65816, diagnose width, mode and near/far mismatches at calls and returns,
  immediates reached with unknown width, every unannotated construct of §7.4, and
  direct-page and bank mismatches against declared segments and ranges (§7.5);
- report out-of-range branches and per-block cycle intervals before ca65 runs (§7.6);
- run incrementally: editing one file re-parses one file; only resolution is global.

**Unused symbols** are warnings: a label, constant, macro, struct, union or enum that
nothing names and the file does not export, since an export is what another file uses. A
member of a named enum is one of a set and is not reported on its own, a label a `.state`
declares an entry point is reached from outside, and a label flow analysis reports as never
reached is not reported twice. A name written in a branch the configuration leaves out
counts as used, because the other build uses it, and a file with errors gets none.

Analysis is of one configuration at a time, as with `#if` in C or `#[cfg]` in Rust:
lines in a branch that is not taken still parse, but are not resolved or analyzed.

**The incremental boundary is the file's interface**: its exported declarations, each
carrying everything a user of it needs (a constant's value, a label's address size, a
data label's `.sizeof` and `.countof`, a routine's signature, a list's items, a function's
body, a macro's kind and body and the exported symbols it uses). It also holds the names
of the declarations it does not export that a path can reach, because another file naming
one is told that it exists and is not exported, and what any of them means that another
file names anyway. Positions are not part of it: what one file says about a place in
another moves with an edit there. The one exception is where a macro is written, because an
expansion's comment names the calls in it by file and line (§13) and a problem with a line of
its body is reported at the call with that line named beside it. Nothing in the interface is derived from a proc body or
depends on `*`: code sizes are layout, left to the linker (§7.6). If an edit leaves
the interface unchanged, no other file is re-analyzed, and within the file only the
edited proc's flow analysis reruns. The only program-wide tables are the defines, the
export map, the segment and range tables (§5.2, §5.3) and the CPU, all small. Keeping
signatures declared rather than inferred is what protects this: inference would make
every caller depend on every callee's body.

## 15. Deliberately not in nt65

| ca65 feature | reason |
|---|---|
| `.define` | textual substitution; constants and `.func` replace it (§9) |
| `.feature`, `.setcpu` mid-file | changes the grammar or mnemonic set |
| `.set`, `.org` | positional state |
| `.include`, `.macpack` | textual inclusion; nt65 never reads ca65 source, and shares with ca65 through symbols (§12) |
| unnamed labels `:` `:+` `:-` | positional; use `@name` |
| `.ident`, `.concat`, `.sprintf` for names | computed identifiers; `.each` over an enum builds the tables they were used for (§10) |
| `.match`, `.xmatch`, `.tcount`, `.paramcount`, `.exitmacro` | token-stream macro programming; typed parameters (`const`, `one`, `list`, `operand` with `.mode`), named arguments and defaults replace the common uses (§11.2) |
| recursive macros | a depth limit does not bound an expansion; `list` parameters and `.each` replace walking argument lists (§11.1) |
| `.asize`, `.isize` in macros | expansion would depend on the flow analysis of its own output; macro state signatures and `.ensure` replace them (§7.3, §11.5) |
| modal `.charmap` | replaced by named mappings applied explicitly (§8) |
| `.local`, `.global`, `.pushseg`/`.popseg` | replaced by structural rules and blocks |
| `.smart` | replaced by the flow analysis; the output disables it |

## 16. Decisions

Recorded so the reasoning survives. None is open.

- **Braces, not end-keywords.** Simpler to parse and, as much to the point, an nt65
  file is visually distinct from a ca65 file at a glance.
- **`name!(args)` for macro invocation.** A marker on the line is required by §3.1;
  the Rust spelling is the familiar one.
- **Explicit `.export`.** A file's interface is deliberate, which is what gives
  unused-symbol analysis and the incremental boundary of §14 their meaning.
- **Segment blocks, not per-item attributes.** One placement mechanism, nestable, and
  the structured replacement for `.pushseg`/`.popseg`.
- **JSON for the project file.**
- **Whitespace binds nothing.** `lda # 1` is `lda #1`, as it is in ca65, which assembles
  it without complaint. Maximal munch (§4) stays the one place spacing changes what a line
  means; making `#` bind to its expression would be a second such rule, and would raise the
  same question of `#< label`, `# (1+2)` and every other operand form.
- **Register names are reserved** wherever a bare name can appear, macro parameters
  included, but not for members of named structs, unions and enums, which are only
  reached through `::`. The output never names members, so ca65's restriction on member
  names does not apply.
- **Constants cross files by value, addresses by import.** ca65 needs constants where
  it needs them, and an imported symbol is never constant to ca65.
- **Merge disagreement is not an error.** The lattice already has unknown; reporting at
  the use is precise, reporting at the label is not.
- **Signatures are declared, never inferred**, for procs, extern procs and imports
  alike. It is what keeps the analysis local and the interface stable.
- **`*` for unchanged state, and no project-wide `assume`.** A routine that does not
  touch part of the state should not erase what its caller knows, and the values of D
  and B belong on the routines that set them.
- **A stack of saved state, not push and pull pairing.** Tracking saved P, D and B as
  the analysis runs lets a save and restore span calls and labels.
- **Processor-state analysis on the 65816 only.** On the other CPUs nothing consumes the
  state, so its annotations would be ceremony.
- **Procs do not nest.** A nested proc's bytes would sit inline in its parent's; a
  separate proc in a `.scope` gives the same privacy and namespace without that.
- **No cycle-count built-ins.** A sum along one path cannot be a true bound once a loop
  or a call is on it.
- **Conditions test only the configuration.** `.if` sees defines and never program
  symbols, as `#if` in C and C#, `#[cfg]` in Rust and `#if` in Swift do. A conditional
  that can test program constants (ca65's `.if`, D's `static if`) makes which
  declarations exist depend on evaluating those declarations. Checks on program values
  are `.assert`.
- **Defines come only from the project file and the command line.** An in-file define
  was considered and left out; in C# it is mostly a temporary per-file toggle.
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
  themselves and keeps proc bodies out of a file's interface.
- **Segments are declared.** A misspelled segment name is an error, not a new segment.
- **The output does not depend on ca65's command line.** A project passes one set of
  ca65 options to every `.s` file, so nt65 output resets or avoids everything those
  options can change, and is tested against ca65 built from one pinned cc65 commit,
  because cc65's version number no longer identifies what ca65 accepts.
- **Debug information through `.dbg`.** cc65 uses the same directives to map compiled
  code to C source, so debuggers that read ld65 debug files need nothing new. A zero
  timestamp keeps the output deterministic.
- **The object file is the only boundary with ca65.** Reading ca65 include files, even a
  declaration-only subset, would put nt65 in the business of parsing ca65 and require
  existing files to fit a layout. Symbols already cross at the link, and a checked
  import covers the values nt65 needs.
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
  `.tag`s. If a computed typed record is ever needed, the extension that fits is a result
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
  within a segment block and can shorten forward branches, which ca65's `longbranch`
  cannot.
- **`.ensure`, not a width stack.** A macro-time stack follows the text; the flow analysis
  follows control flow, and `.ensure` emits from its result.
- **Stack frames are checked against the analysis stack.** Stack-relative offsets written
  by hand shift silently with every push, and the analysis already counts pushes.

## Appendix A. Grammar sketch

```text
file        := item*
item        := label-line | const | data | proc | extern-proc | scope | macro
             | enum | struct | union | charmap | list | func | export | import | cpu
             | segment-decl | segment | if-block | repeat-block | each-block | assert
segment-decl := '.segment' string ':' size (',' seg-attr)*
seg-attr    := 'dp' '=' expr | 'bank' '=' expr
label-line  := (ident | '@' ident) ':' (instr | data | macro-call)?
const       := (ident | '@' ident) '=' expr
data        := directive (expr (',' expr)*)?          ; the directives of §8
             | '.tag' path (',' expr)?
             | '.tag' path '{' (init (',' init)*)? '}'
             | '.tag' path '{' NL (init NL)* '}'
init        := member-name '=' (expr | '{' (init (',' init)*)? '}')
proc        := '.proc' ident (':' state ('->' state)?)? '{' NL body '}'
extern-proc := '.proc' ident '=' expr (':' state ('->' state)?)?
state       := state-item (',' state-item)*
state-item  := point-item | keep-item | 'near' | 'far' | 'inline' (expr | '.asciiz')
keep-item   := 'a*' | 'i*' | 'e*' | 'dp*' | 'dbr*'        ; unchanged
point-item  := 'a8' | 'a16' | 'a?' | 'i8' | 'i16' | 'i?' | 'native' | 'emu' | 'e?'
             | 'dp' '=' expr | 'dp?' | 'dbr' '=' expr | 'dbr?'
enum        := '.enum' ident? '{' NL (ident ('=' expr)? NL)* '}'
struct      := '.struct' ident? '{' NL member* '}'
union       := '.union' ident '{' NL member* '}'
member      := member-name ':' ('.byte' | '.word' | '.dword' | '.addr' | '.faraddr'
             | '.res' expr | '.tag' ident (',' expr)?) NL
             | struct
member-name := ident | register | mnemonic
charmap     := '.charmap' ident '{' NL (char ('..' char)? '=' expr NL)* '}'
list        := '.list' ident '{' NL (expr (',' expr)* NL)* '}'
func        := '.func' ident '(' (ident (',' ident)*)? ')' '=' expr
scope       := '.scope' ident? '{' NL body '}'
segment     := ('.segment' string | '.zeropage' | '.code' | '.bss'
             | '.data' | '.rodata') '{' NL item* '}'
body        := (item | instr | macro-call | assertion | ensure | frame | annotation
             | splice)*                               ; no proc inside a proc
splice      := ident                                  ; block parameter, in macros only
assertion   := '.state' point-item (',' point-item)*
ensure      := '.ensure' width (',' width)*
width       := 'a8' | 'a16' | 'i8' | 'i16'
frame       := '.frame' ident ':' path
annotation  := '.next' (target (',' target)* | '?')
             | '.patch' target (',' target)*
target      := path | '@' ident                       ; or an ident parameter, in macros;
                                                      ; a list or table stands for its labels
path        := '::'? ident ('::' member-name)*
instr       := mnemonic operand?                      ; mnemonics include jeq ... jvc (§7.6)
operand     := '#' expr
             | 'a'
             | prefix? expr (',' ('x' | 'y' | 's'))?
             | prefix? expr ',' expr                   ; bbr / bbs: zero page, branch target
             | '(' expr ')' (',' 'y')?
             | '(' expr ',' ('x' | 's') ')' (',' 'y')?
             | '[' expr ']' (',' 'y')?
             | '#' expr ',' '#' expr                   ; mvn / mvp: source bank, destination bank
prefix      := 'z:' | 'a:' | 'f:' | 'd:'
macro       := '.macro' ident '(' (param (',' param)*)? ')'
               (':' state ('->' state)?)? '{' NL body '}'
param       := ident (':' kind)? ('=' (expr | '{' '}'))?
kind        := 'expr' | 'const' | 'ident' | 'operand' | 'block'
             | 'one' '(' word (',' word)* ')' | 'list' '(' kind ')'
word        := ident | register | mnemonic
macro-call  := ident '!' '(' (arg (',' arg)*)? ')'
               ('{' NL body ('}' ident '{' NL body)* '}')?
arg         := expr | '{' operand '}' | '@' ident | word | ident '=' arg
if-block    := '.if' expr '{' NL contents '}' ('.elseif' expr '{' NL contents '}')*
               ('.else' '{' NL contents '}')?
repeat-block := '.repeat' expr (',' ident)? '{' NL contents '}'
each-block  := '.each' path ',' ident '{' NL contents '}'
contents    := item*                                  ; at item level
             | body                                   ; inside a proc
export      := '.export' ident (',' ident)*
import      := '.import' import-item (',' import-item)*
import-item := ident (':' (size | 'proc' '(' state? ('->' state)? ')'))?
             | ident '=' expr                        ; checked import
size        := 'zp' | 'abs' | 'far'
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
| records: metasprites, actors, level objects | none, but the label has no fields | initialized `.tag` (§6.3), or a macro when computed |
| terminated lists, computed tables, register-init pairs | none | macros, `.repeat`, `.each` and `.func` |
| file and cartridge headers: iNES, the C64 BASIC stub, Atari XEX | layout | `.endof` and `.spanof` (§7.6) with macros |
| `zp_var name, 2` through `.pushseg` | names, modal segment | a nested segment block (§5.2) |
| allocators that advance `.set RAM_PTR` | order, names | segments placed by ld65, or `.struct` offsets from a base |
| `.ifdef DEBUG` trace and break wrappers | none | `.if` on a define, with macros |
| emulator hooks | flow | `wdm #n` (§7.1) |
| checking that a symbol is zero page | order | `.assert .addrsize(sym) == 1` (§9) |
| CPU-conditional code, `.ifp816` | modal CPU | `.if .target(65816)` (§9) |
| page-crossing and timing checks | layout | `.assert`, checked at link time when it cannot be earlier (§7.6) |
