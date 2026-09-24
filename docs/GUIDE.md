# nt65 for ca65 programmers

nt65 is an assembly language for the 6502 family and the 65816. It does not replace ca65: it
compiles to it. You write `.nt65` files, `nt65 build` turns each one into an ordinary ca65
source file, and ca65, ld65, your linker configuration and your Makefile take it from there.
Hand-written ca65 and C compiled with cc65 link with it as they always have.

The language is close to ca65. Most lines are the same mnemonics with the same operands. What
changes is everything that stops a tool from understanding a ca65 program before it is
assembled: textual macros, `.define`, modal directives such as `.a16` and `.segment`, and
address sizes that ca65 guesses from what it has seen so far. In their place nt65 has a small
set of structural rules, and in return it can check far more than an assembler can:

- Every name is resolved across the whole program before anything is assembled, so a typo, a
  missing export or a wrong module is reported as you type.
- Address sizes are never guessed. nt65 knows whether `ptr` is in zero page from where `ptr`
  is declared, and writes `z:`, `a:` or `f:` on every operand of the output.
- Branch range, cycle counts and which registers a routine preserves are worked out as you
  edit, not discovered after a build.
- On the 65816, register widths, emulation mode, the direct page and the data bank are
  tracked through every routine. `lda #$1234` with an 8-bit accumulator is an error in the
  editor, not a crash on hardware.
- A language server gives the editor go to definition, rename, hover with sizes and cycle
  counts, and fixes for most errors.

This guide assumes you know ca65 and the 65xx processors. It introduces every feature of
nt65, and spends the most time on the ones that differ most from ca65. The last section,
[Migrating from ca65](#migrating-from-ca65), is a reference table of ca65 constructs and their
nt65 equivalents.

## Contents

- [Getting started](#getting-started)
- [A first program](#a-first-program)
- [Modules instead of includes](#modules-instead-of-includes)
- [Segments](#segments)
- [Routines and labels](#routines-and-labels)
- [Data has a type](#data-has-a-type)
- [Text](#text)
- [Constants, functions and the build configuration](#constants-functions-and-the-build-configuration)
- [Expressions](#expressions)
- [Repetition and families](#repetition-and-families)
- [Macros](#macros)
- [What nt65 follows through your code](#what-nt65-follows-through-your-code)
- [Cycle counts and branch range](#cycle-counts-and-branch-range)
- [What a routine preserves](#what-a-routine-preserves)
- [The 65816](#the-65816)
- [Code for another processor](#code-for-another-processor)
- [Programs built from includes](#programs-built-from-includes)
- [Working with C and hand-written ca65](#working-with-c-and-hand-written-ca65)
- [Diagnostics](#diagnostics)
- [In the editor](#in-the-editor)
- [The command line](#the-command-line)
- [Migrating from ca65](#migrating-from-ca65)

## Getting started

nt65 runs on .NET 10. Build the packages as the [README](../README.md) describes, then
install the command and the VS Code extension:

```text
dotnet tool install --global nt65 --configfile artifacts/nuget.config
code --install-extension artifacts/nt65-<version>.vsix
```

`nt65 init` writes a project that builds: an `nt65.json` and a `src/main.nt65`. A project is
a folder with an `nt65.json` in it:

```json
{
  "cpu": "6502",
  "files": ["src/**/*.nt65"],
  "out": "build"
}
```

`nt65 build` reads the nearest `nt65.json` and writes one `.s` file per module into `out`.
You assemble and link them as you would any ca65 source:

```text
nt65 build
ca65 -g build/main.s -o build/main.o
ld65 -C c64.cfg -o game.prg --dbgfile game.dbg build/main.o
nt65 remap-dbg game.dbg
```

The last line is optional. Each `.s` is written with a `.s.lines` file beside it that maps
every line of output back to the `.nt65` line it came from. `nt65 remap-dbg` adds that
mapping to the debug file ld65 wrote, so a debugger such as Mesen or VICE shows your `.nt65`
source. Without it the debug file still works; it just refers to the generated `.s`.

A build that finds an error writes nothing, so a half-built program never reaches ca65.

## A first program

```nt65
; Fill four pages of screen memory with spaces, forever.
.module main

.cpu 6502

SCREEN       = $0400
SCREEN_PAGES = 4

.segment ZEROPAGE
.data ptr: .word                ; destination pointer

.segment CODE
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
    jmp main
}
```

Most of it reads as ca65. The differences are:

- **Every file is a module** and says which one on its first line, with `.module`.
- **`.segment CODE` has no quotes.** Written on its own at file level it opens a *region*:
  everything below it, up to the next `.segment` line, is in `CODE`. There is no default
  segment, so bytes written before any region are an error rather than a surprise in `CODE`.
- **Data is declared with its type.** `.data ptr: .word` declares two bytes of storage named
  `ptr`, where ca65 writes `ptr: .res 2`.
- **Code lives in `.proc` blocks, with braces.** There are no instructions and no labels
  outside a proc. Inside one, `@loop` is a label private to that proc.
- **A macro call is marked with `!`**, and a macro's parameters have kinds. `dest: operand`
  takes a whole operand, so `set16!({buf,x}, $1234)` works where a ca65 macro would split its
  argument at the comma.

This is what `nt65 build` writes for `main`:

```ca65
.segment "CODE": absolute
; .proc fill_page  main.nt65:13
fill_page:
    ldy #0
fill_page__loop:
    sta (ptr),y
    iny
    bne fill_page__loop
    rts
; end of fill_page
; .proc main  main.nt65:29
main:
    ; set16!(ptr, SCREEN)  main.nt65:30
    lda #<SCREEN
    sta z:ptr
    lda #>SCREEN
    sta z:ptr+1
    ; end of set16!
    ldx #SCREEN_PAGES
main__page:
    lda #$20                        ; ' '
    jsr fill_page
    inc z:ptr+1
    dex
    bne main__page
    jmp main
; end of main
```

The output is plain ca65 that anyone can read. Scoped names are flattened (`fill_page__loop`),
the macro is expanded with a comment naming the call, character literals are written as bytes,
and every operand that could be zero page or absolute carries its size: `sta z:ptr` and
`inc z:ptr+1`, because nt65 knows `ptr` is in a zero-page segment. The file also begins with a
header that sets the CPU and turns off every ca65 feature and option that could change what
the rest of it means, so the same output assembles to the same bytes whatever options your
build passes to ca65.

### What stays the same

- Mnemonics, addressing modes and operands: `lda (ptr),y`, `sta buf,x`, `jmp (vector)`,
  `lda #<label`. The `z:`, `a:` and `f:` prefixes mean what they mean in ca65.
- Numbers (`$1F`, `%1010`, `255`, `'c'`), comments after `;`, and one statement per line.
- Constants, `NAME = expr`, including forward references.
- The data directives `.byte`, `.word`, `.dword`, `.addr`, `.faraddr`, `.res`, `.align`,
  `.incbin`, `.lobytes`, `.hibytes` and `.bankbytes`.
- Directives, mnemonics and register names are case-insensitive; your own names are
  case-sensitive.

## Modules instead of includes

There is no `.include`. Every file is a module, and a module's names are private until it
exports them. Export a declaration by writing `.export` in front of it, or list names in an
`.export` line:

```nt65
.module gfx

.cpu 6502

.export BORDER = $D020

.segment CODE
.export .proc clear {
    lda #0
    sta BORDER
    rts
}

.proc fill {
    rts
}
.export fill as "_gfx_fill"
```

Another module refers to an exported name by its path, or brings it in with `.use`:

```nt65
.module game

.cpu 6502

.use gfx::clear

.segment CODE
.export .proc start {
    jsr clear
    jsr gfx::fill
    lda gfx::BORDER
    rts
}
```

Naming something a module does not export is an error, and so is naming a module that does
not exist. `.use` has several forms:

```nt65
.use hw::init                       ; one name
.use hw::{BORDER, set_border}       ; several names
.use hw::sid::*                     ; everything hw::sid exports
.use snd::init as snd_init          ; a name of your choosing
.use very::long::path as p          ; a module, then written p::thing
```

A `.use` path always starts at the root of the modules, never at the current one. A module's
name may itself be a path, `.module gfx::sprite`; the output for it is `gfx/sprite.s` under
`out`.

**Linker names.** The linker sees an export under its module's path joined with `__`, so
`clear` above is `gfx__clear`, and two modules may both export an `init` without clashing.
`as` gives the linker name exactly, which is how you name something for C or for hand-written
ca65: `fill` above links as `_gfx_fill`, with cc65's leading underscore written out. The
output imports only what a module actually uses, so linking against an `ar65` library pulls
in only the modules you need.

**Constants cross modules by value.** An exported constant is written into each module that
uses it as its value, so ca65 can use it where it needs a constant, in a `.res` count for
instance. The same goes for enums, structs, lists, functions and macros: they are compiled
into the module that uses them.

Definitions several modules share, such as a machine's hardware registers, go in a module
that exports them. A module that grows too large is split into submodules, such as `hw::vic`
and `hw::sid`, that export what they share. A module can present names its submodules
declare as its own with `.export .use hw::vic::border`, which is then reached as
`hw::border`. With `as`, it presents one under a name of its own:
`.export .use hw::sid::volume as sid_volume` is reached as `hw::sid_volume`. A library can
name what each program supplies this way. For example, each machine's `platform` module
re-exports its character map as `platform::text`, and the library writes `text("READY")`.

## Segments

A segment's address size is declared once, not at each use. The standard ca65 segments are
already declared: `ZEROPAGE` is zero page, and `CODE`, `RODATA`, `DATA` and `BSS` are
absolute. You declare others yourself, in a source file or in `nt65.json`:

```nt65
.segment ZP2: zp                    ; a declaration: a size after the colon

.segment ZP2                        ; a region
.data scratch: .byte
```

A size after the colon makes the line a declaration; without one it is a region. A region
that names a segment nobody declared is an error, so a misspelled segment name is caught
before ld65 runs. The sizes are `zp`, `abs` and `far`; `far` needs the 65816.

Inside a proc, a segment *block* puts something in another segment without leaving the proc.
It is the structured form of `.pushseg` and `.popseg`:

```nt65
.proc draw {
    ldx #3
@loop:
    lda table,x
    dex
    bpl @loop
    rts
    .segment RODATA {
        .data table: .byte 1, 2, 4, 8
    }
}
```

`table` is `draw::table`, private to `draw`'s scope, even though its bytes are in `RODATA`.
A segment block can also be used at file level, for one item in a different segment from the
region around it.

There is no `.org` and no `.reloc`. Where a segment lives in memory is the linker
configuration's business, and nt65 never reads the linker configuration.

**A segment's bytes are one run.** ca65 writes each segment's bytes in the order they appear
in the file, whichever `.segment` line put them there, and nt65 reads the file the same way.
A routine at the end of one `CODE` region is followed by whatever the next `CODE` region
writes, however many `RODATA` regions stand between them in the text. That is the layout nt65
uses to measure branches and to check which routine falls into which.

## Routines and labels

A routine is a `.proc` with a body in braces. Procs do not nest, and there are no
instructions or labels outside them: at file level, everything that takes up bytes is a
`.proc` or a `.data` declaration. Inside a proc a label is a position in the code, and it
never has a size.

Labels come in two kinds:

- `name:` is a label in the proc's scope. Another routine can jump to it as `proc::name`; on the
  65816 the label then needs a `.state` after it saying what state it is entered in.
- `@name:` is a *cheap local*, private to its proc. It cannot be reached from outside or
  exported, so `@loop` in two procs never collides.

A name may be declared only once in a scope. To reuse one inside a routine, open an anonymous
`.scope { }`, which is a namespace, not a separate routine:

```nt65
.proc init {
    .scope {                        ; clear RAM
        ldx #0
    @loop:
        sta $0200,x
        inx
        bne @loop
    }
    .scope {                        ; clear the palette
        ldx #31
    @loop:
        sta palette,x
        dex
        bpl @loop
    }
    rts
}
```

A named `.scope name { }` at file level groups declarations under a path, `name::thing`, and
has no address of its own.

An address outside nt65, such as a ROM entry point, is declared as an *extern proc*: a routine
with no body, at a constant address.

```nt65
.proc CHROUT = $FFD2                ; the C64 KERNAL's character output
```

`jsr CHROUT` then assembles as a call to `$FFD2`. An extern proc can carry a signature, like
any routine, which becomes important on the 65816 and for [what a routine
preserves](#what-a-routine-preserves).

## Data has a type

In ca65 a label on a `.res` line is just an address, and nothing but convention says how big
the thing at that address is. In nt65 every piece of data is a declaration with a type, so
nt65 knows its size, how many elements it has and, for records, where each field is.

```nt65
.struct Actor {
    x:  .word
    y:  .word
    hp: .byte
}

.segment BSS
.data buffer: .byte[64]             ; 64 bytes, no values
.data player: .type Actor           ; one record
.data actors: .type Actor[8]        ; eight records

.segment RODATA
.data sines: .byte 0, 49, 90, 117   ; four bytes, with values
.data title: .strz "SPACE TRAIN"
.data boss: .type Actor { x = 100, y = 40, hp = 99 }
.data exe_header {                  ; data of several kinds
    .word @end - exe_header, 0
    .byte "NT"
@end:
}
```

**Element types.** The types are `.byte`, `.word`, `.long` (24 bits), `.dword` (32 bits),
their big-endian forms `.beword`, `.belong` and `.bedword`, the addresses `.addr` and
`.faraddr`, and `.type T` for a struct or union `T`. A count in brackets makes an array:
`.word[16]` is sixteen words, and `.word 16` is one word holding 16.

**Values.** Values written on the declaration's line give one element each. With a count,
values go in braces, on the line or in a body below it, and the count is checked: `.byte[4] {
1, 2, 4 }` is an error, not three bytes and a zero, because a short table is exactly the
mistake a count is there to catch. `[]` counts the values for you. One text is the exception:
`.byte[21] { "NT65" }` is padded with zeros to 21 bytes, as `char title[21] = "..."` is in C.

**`.res` is only padding.** `.data ptr: .res 2` is an error that suggests `.byte[2]`. An
unnamed `.res` or `.align` may still stand between declarations, as padding.

**Records.** Data of a struct type is a scope of its fields: `player::hp` is the address
`player + 4`, so `lda player::hp` needs no offset constants. `lda actors::hp,x` reads the `hp`
of the element whose offset is in X, and `actors[2]::hp` names an element nt65 works out for
you. An index out of range is an error. An initialized record, `.type Actor { x = 100, hp = 99
}`, names each value's field, so reordering the struct cannot misplace a value, and a field not
named is zero. `.type Actor[] { ... }` is an array of initialized records, one braced record
per line.

**Sizes.** `.sizeof(x)` is a size in bytes and `.countof(x)` a number of elements:
`.sizeof(actors)` is 40, `.countof(actors)` is 8 and `.sizeof(Actor)` is 5. Both are
constants, usable anywhere a constant is, including a `.res` count. The size of a routine is
different, because it depends on how the code was laid out: `.endof(f)` is the address just
past `f` and `.spanof(f)` its length, and ca65 and ld65 work those out.

**Mixed data.** `.data name { ... }` holds data directives of any kind, nested `.data`
declarations, which become members such as `name::sub`, and `@` positions private to the
block. It is how you write a file header or any record whose layout is not a struct.

**Distances are constants.** nt65 never knows where a declaration will land, but it lays out
every byte inside one, so the distance between two places in the same declaration is a
constant. `@end - exe_header` above is 6, and nt65 writes it as 6. So is the offset of a
message in a table of messages, which a one-byte immediate can then load:

```nt65
.data messages {
    .data NOFOR: .byte "NEXT WITHOUT FO", 'R' | $80
    .data SYNTAX: .byte "SYNTA", 'X' | $80
}

ERR_SYNTAX = messages::SYNTAX - messages    ; 16
```

**Enums and unions.** `.enum` and `.union` are ca65's, with braces:

```nt65
.enum Color {
    red                             ; 0
    green = 5
    blue                            ; 6
}

.union Value {
    b: .byte
    w: .word
}
```

A named enum's members are written `Color::red`; an anonymous `.enum { }` puts its members in
the surrounding scope. A member can stand under an `.if`, so which members an enum has can
follow the build configuration.

**Lists.** A `.list` is a named sequence of values, written once and used wherever the same
items would otherwise be written twice. The classic use is a split table of addresses:

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

## Text

String and character literals are ASCII. A non-ASCII character in one is an error, and
`\xHH` writes any byte. The escapes are `\n`, `\r`, `\t`, `\0`, `\\`, `\"`, `\'` and `\xHH`.
Every character reaches the output as a byte value, so ca65's `-t` target option cannot
change it.

**Character maps are declarations, not a mode.** A `.charmap` has a name and is applied where
the text is written:

```nt65
.charmap screen {
    'A'..'Z' = $01                  ; a range maps to consecutive values
    '@'      = $00
    ' '      = $20
}

.data greeting: .byte screen("HELLO WORLD")
```

A character the map does not name is an error where the map is applied.

The Commodore, Atari and Apple II machines' own characters come with nt65, as charmaps in the
modules `nt65::cbm`, `nt65::atari` and `nt65::apple2`:

```nt65
.use nt65::cbm::{petscii, screen}

.data prompt: .byte petscii("READY."), $0D     ; for CHROUT
.data banner: .byte screen("SCORE")             ; for screen memory
```

Each character set has charmaps of its own, since the same byte shows a different character in
each: `petscii` and `screen` are for the uppercase and graphics set the C64 starts in, which has
no lower case, and `petscii_lower` and `screen_lower` for the other. Writing `petscii("Hi")` is
an error rather than a graphic on the screen. Atari's are `atascii` and `screen`, and the Apple
II's are `normal`, `inverse`, `flash`, and `normal_lower` for the IIe and later. Code shared
between machines names none of them itself; see the monitor example, whose `platform` module
re-exports its machine's charmap as `text`.

**Text is a value.** A constant may hold text, `TITLE = "NT65"`, and a `.func` may return it.
`.strlen(s)` is its length, `.strat(s, i)` the byte at `i`, `.strsub(s, start, count)` a part
of it and `.strcat(...)` joins texts and bytes. That covers what ca65 code uses `.sprintf`,
`.concat` and byte-at-a-time macros for. For example, Microsoft BASIC marks the last
character of each keyword by setting bit 7:

```nt65
; A text with bit 7 set on its last byte.
.func htasc(text) = .strcat(.strsub(text, 0, .strlen(text) - 1), .strat(text, .strlen(text) - 1) | $80)

.data keywords {
    .data END: .byte htasc("END")
    .data FOR: .byte htasc("FOR")
}
TOKEN_FOR = keywords::FOR - keywords        ; 3
```

A function is worked out along with the constants, before anything is assembled, so the text
it returns has a known length and `TOKEN_FOR` is a constant. The same table written by a
macro would have no size until the macro was expanded.

`.strz "text"` writes a text and a zero after it. A zero byte inside the text is an error,
because the string would end early.

## Constants, functions and the build configuration

`.define` is gone, because it substitutes text. Each of its uses has a replacement:

```nt65
LINES = 25                          ; a constant
.func rgb15(r, g, b) = r | (g << 5) | (b << 10)
.config VOICES = 3                  ; a setting the build may change

.if .defined(DEBUG) && DEBUG {
    TRACE_LEVEL = 2
} .else {
    TRACE_LEVEL = 0
}
```

- **A constant** is assigned once, may be used before it is defined, and may not depend on
  itself.
- **A `.func`** takes values, not tokens. `rgb15(1 + 1, 0, 0)` passes 2, where a ca65
  `.define` would substitute the text `1 + 1` and let precedence decide what it meant.
- **A define** comes from `nt65.json` or the command line, `-D DEBUG=1`, and is visible in
  every module.
- **A `.config` setting** is a define a module declares, with a default. The build overrides
  it by its path: `-D audio::VOICES=4`, or `"audio::VOICES": 4` under `defines`.

**Conditions test the configuration, never the program.** An `.if` condition may use numbers,
operators, built-in functions, defines and `.config` settings, and nothing else the program
declares. That is what lets nt65 know which declarations exist before it reads any of them.
A check that depends on the program, such as a table's size, is an `.assert`:

```nt65
.assert .sizeof(Actor) <= 8, "Actor must fit an 8-byte slot"
```

nt65 checks an `.assert` as you type when it can. When it depends on addresses only the
linker knows, nt65 writes it into the output for ld65 to check. `.assert` takes no level: a
failed assertion is always an error. `.error "text"` inside an `.if` refuses a configuration,
and `.warning "text"` builds it with a message.

In `&&` and `||`, the right side is evaluated only when the left does not decide the answer,
so `.defined(DEBUG) && DEBUG` works when `DEBUG` is not defined.

Declarations inside an `.if` belong to the surrounding scope. The same name may be declared
in several branches, and only the branch the configuration takes counts. The editor greys
out the branches the current configuration does not take.

**`.select(c, a, b)`** is `a` when `c` holds and `b` when it does not. Only the chosen side is
evaluated, so the other may name something this build does not declare:

```nt65
COLUMNS = .select(WIDE, 80, 40)
```

**Sets.** `v .in [a, b, c..d]` is 1 when the set holds `v` and 0 when it does not, and
`.switch(v, set, result, ..., otherwise)` is the result after the first set that holds `v`. A
set is values and ranges in brackets, or the name of a `.list`. `.switch` reads only the result
it chooses, as `.select` does, and a `.switch` with no `otherwise` is an error for a value no set
holds:

```nt65
.func operand_size(m) = .switch(m, [Mode::imp, Mode::acc], 0, [Mode::abs..Mode::ind], 2, 1)
.assert main .in [$8000..$ffff], "main is in ROM"
```

**CPU tests.** `.if .has(phx)` holds on every CPU that has the `phx` instruction, and
`.target(65c02)` names one CPU exactly. The CPU is part of the configuration.

**Named configurations.** `nt65.json` can describe several builds of the program:

```json
{
  "cpu": "6502",
  "files": ["src/**/*.nt65"],
  "out": "build",
  "defines": { "DEBUG": 0 },
  "configurations": {
    "debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" },
    "pal":   { "defines": { "hw::PAL": 1 }, "out": "build/pal" }
  }
}
```

`nt65 build --config debug` chooses one, and the editor has a setting for which one it
analyzes.

## Expressions

Expressions follow C's precedence, not ca65's. nt65 writes whatever parentheses ca65 needs,
so nothing in the output depends on ca65's own table.

| ca65 | nt65 |
|---|---|
| `a = b`, `a <> b` in a condition | `a == b`, `a != b` |
| `a .mod b` | `a .mod b` (`%` starts a binary number) |
| `a = 1 .or a = 2 .or a = 3` | `a .in [1..3]` |
| `a .xor b` | `a ^^ b` |
| `.bitand`, `.bitor`, `.bitxor`, `.bitnot` | `&`, `\|`, `^`, `~` |
| `.and`, `.or`, `.not` | `&&`, `\|\|`, `!` |

`=` only ever defines. Where C's order is easy to misread, nt65 requires parentheses:

- `a & $0f == 0` is an error, because it means `a & ($0f == 0)`. Write `(a & $0f) == 0`.
- `1 << n + 1` is an error. Write `1 << (n + 1)` or `(1 << n) + 1`.
- `a || b && c` is an error. Write the parentheses you mean.
- `#<label+1` is an error, because it is not obvious whether the `+ 1` applies before or after
  the low byte is taken. Write `#<(label+1)` or `#(<label)+1`.

nt65 works out every expression it can, in 64-bit signed arithmetic. `/` truncates toward
zero, `.mod` takes the sign of the dividend, and `>>` keeps the sign. Overflow, division by
zero and a shift of more than 63 places are errors rather than wrapped values. A value that
reaches the output must fit ca65's 32 bits.

A `_` may separate digits, `$7f_ff` or `%1010_1010`, and the output writes the number without
it.

**Numbers worked out at build time.** `.sqrt(n)`, `.muldiv(a, b, c)` (`a * b / c` without
overflow in the middle), `.sin(angle, turn, scale)` and `.cos(angle, turn, scale)` take whole
numbers and return whole numbers, rounded to the nearest with halves away from zero. None of
them uses floating point, so every machine builds the same bytes. A lookup table that used to
come from a script can sit beside the code that reads it:

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

`.sin(i, 256, 127)` treats a whole turn as 256 steps and reaches 127 at the quarter turn.
`.cos` is the same a quarter turn on.

The other built-in functions are `.lobyte`, `.hibyte`, `.bankbyte`, `.loword`, `.hiword`,
`.min`, `.max`, `.addrsize` (1, 2 or 3 for an address's size), `.defined`, `.has`,
`.target`, `.select`, `.switch`, the size functions of [Data has a type](#data-has-a-type), the text
functions of [Text](#text), and `.mincycles` and `.maxcycles`, which are in [Cycle counts and
branch range](#cycle-counts-and-branch-range).

## Repetition and families

`.repeat` and `.each` are unrolled by nt65. They work at file level, inside procs and in data
bodies, where each line of the body is a value:

```nt65
.data row_lo: .byte[] {
    .repeat 25, r {
        <(SCREEN + r * 40)
    }
}
```

`.each` repeats once per item of a list, or once per member of a named enum. Over an enum,
the name it binds is the member, so `actions::c` below is `actions::move`, then
`actions::fire`. That builds a dispatch table that stays in step with the enum, which is what
ca65 code uses `.ident` for:

```nt65
.enum Cmd {
    move
    fire
}

.scope actions {
    .proc move {
        rts
    }
    .proc fire {
        rts
    }
}

.data dispatch: .addr[] {
    .each Cmd, c {
        actions::c - 1              ; an RTS dispatch table
    }
}
```

**Families.** Run the same rule the other way and a declaration named after the binding
declares one routine or one data declaration per member of the enum. `.multiproc` is the
short form for routines:

```nt65
.enum Channel {
    pulse1
    pulse2
    noise
}

.scope level {
    .each Channel, ch {
        .data ch: .byte             ; level::pulse1, level::pulse2, level::noise
    }
}

.scope play {
    .multiproc Channel, ch {        ; play::pulse1, play::pulse2, play::noise
        lda level::ch
        .if ch == Channel::noise {
            ora #$80
        }
        sta level::ch
        rts
    }
}
```

Each instance is an ordinary routine: `jsr play::noise` calls one, and go to definition on it
lands on the `.multiproc` line. Nothing is built by pasting names together, so the editor
knows every instance: renaming `play::noise` renames the enum member `Channel::noise`, and with
it every instance named after it.

## Macros

Most of what ca65 code uses macros for is a language feature in nt65: constants and `.func`
instead of `.define`, charmaps instead of screen-code macros, initialized records instead of
record macros, lists and `.each` instead of variadic macros, and built-in long branches. What
is left for macros is instruction idioms, structured control flow and computed data, and for
those nt65's macros are safer and easier to read than ca65's.

A macro's parameters have kinds, a call is marked with `!`, and an argument is a value, not a
run of tokens:

```nt65
.macro mov16(dest: operand, src: operand) {
    lda .byteof(src, 0)
    sta dest
    lda .byteof(src, 1)
    sta dest+1
}

.macro times_x(count: const, body: block) {
    ldx #count
@loop:
    body
    dex
    bne @loop
}

.proc copy_twice {
    mov16!(ptr, {#$0400})
    times_x!(2) {
        mov16!(ptr, other)
    }
    rts
}
```

**Arguments are values.** A parameter stands for its whole argument, parenthesized, so a
body's `value * 2` with the argument `1 + 2` is 6, not ca65's 5, and `#<value` with `label+1`
is `#<(label+1)`.

**Operands.** An `operand` parameter takes a whole operand. Any addressing mode other than a
plain address is written in braces: `{#$0400}`, `{buf,x}`, `{(ptr),y}`. In the body,
`dest+1` adds to the operand's address and keeps its mode, so `{buf,x}` becomes `buf+1,x`.
`.byteof(src, n)` is byte n of either an immediate or an address, which is how `mov16` above
serves both. `.mode(p)` names the argument's mode (`imm`, `abs`, `absx`, `indy` and so on) for
an `.if` to test, and `.exprof(p)` is the expression inside it.

**Parameter kinds.**

| kind | takes |
|---|---|
| `expr` (the default) | an expression |
| `const` | a constant, checked at the call |
| `const(0..127)` | a constant in a range |
| `ident` | a name, such as a label for the body to branch to |
| `operand` | an operand, braced or not |
| `operand(imm, zp, abs)` | an operand in one of the listed modes |
| `one(a, x, y)` | one of the listed words |
| `list(kind)` | all remaining arguments, for `.each` |
| `block` | the braced block after the call |
| an enum's name | one of that enum's members |

A mistake in an argument is reported at the call, naming the parameter, rather than as an
error somewhere inside the expansion.

**Defaults and named arguments.** `.macro note(pitch: const, frames: const = 1)` gives
`frames` a default, and a call can name its arguments after the positional ones:
`note!(C4, frames = 8)`.

**Lists of words.** A `list` parameter with `.each` replaces ca65's recursive macros:

```nt65
.macro push(regs: list(one(a, x, y))) {
    .each regs, r {
        .if r == a {
            pha
        } .elseif r == x {
            txa
            pha
        } .else {
            tya
            pha
        }
    }
}
```

`push!(a, x, y)` then pushes all three.

**Blocks.** A `block` parameter takes the braces after the call, which is how structured
control flow is built as a library. A macro may take several blocks, each after the first
introduced by its parameter's name on the closing line:

```nt65
.macro if_eq(then: block, else: block = {}) {
    bne @skip
    then
    .if !.empty(else) {
        jmp @done
    }
@skip:
    else
@done:
}

.proc compare {
    cmp #10
    if_eq!() {
        lda #0
    } else {
        inx
    }
    rts
}
```

**Macros are hygienic.** Labels and other names declared in a body are local to each
expansion, so `@skip` above never collides between two calls. A macro cannot declare a name in
its caller, cannot call itself, and resolves the names in its body where the macro is declared.
Because of that, go to definition, rename and find references work in and through macros
without expanding them. To name data a macro writes, put the call in a `.data` block:
`.data player_sprite { sprite!(...) }`.

A macro is called with `name!(...)`, so a mnemonic is never mistaken for a macro and the other
way round. A macro may even be named after an instruction, `bne!(target)`, which is how a
library spells another processor's instructions.

The editor can show any call written out as the nt65 it expands to, and can replace the call
with that text when you want to stop using a macro.

## What nt65 follows through your code

nt65 follows control flow through every routine: where each instruction can go next, and on
the 65816 what state the processor is in when it gets there. Most code needs nothing from you
for this. The exceptions are the tricks assembly programmers use that no tool can read from
the text alone, and each of them has a short annotation that says what the code does.

On the 6502 and its CMOS variants the annotations are optional, and a trick left unannotated
is at most a warning. On the 65816 they are required, because the processor state after a
trick depends on them.

**`.next` says where an instruction goes** when nt65 cannot see it: an indirect jump, an `rts`
used as a jump, a jump to a computed address, or data that code runs into. It applies to the
statement directly above it:

```nt65
.proc dispatch {
    lda command
    asl a
    tax
    lda handlers+1,x
    pha
    lda handlers,x
    pha
    rts
    .next cmd_move, cmd_fire        ; the rts is a jump to one of these

    .data handlers: .addr cmd_move - 1, cmd_fire - 1

cmd_move:
    rts
cmd_fire:
    rts
}
```

`.next handlers` would say the same, since every item of `handlers` is a code label, optionally
minus one. `.next ?` says the path ends and nothing is checked.

The same annotation covers the `bit` skip trick, where one instruction's operand hides the
next instruction:

```nt65
.proc set_value {
    lda flag
    beq @two
    lda #1
    .byte $2c                       ; bit abs: swallows the lda #2
    .next @store
@two:
    lda #2
@store:
    sta value
    rts
}
```

After a conditional branch, `.next` naming the branch's own target says the branch is always
taken, which is common in 6502 code that knows its flags:

```nt65
.proc always_taken {
    lda #1
    bne @over                       ; A is not zero, so this is always taken
    .next @over
    .byte "INLINE TEXT", 0
@over:
    rts
}
```

`.next` is only for what nt65 cannot see. After an ordinary instruction or a direct `jsr`,
where nt65 already knows where flow goes, it is an error.

**`.fallthrough` says a routine runs into the next one.** A routine that ends without a
transfer of control runs into whatever is written after it. nt65 reports that, because it is
usually a mistake. When it is on purpose, the last line of the body says so:

```nt65
.proc clear_screen {
    lda #' '
    .fallthrough fill_screen
}

.proc fill_screen {
    ldx #0
@loop:
    sta $0400,x
    inx
    bne @loop
    rts
}
```

nt65 checks that `fill_screen` really is the routine written directly after `clear_screen`
in the same segment. This is also how nt65 writes a routine with a second entry point: as two
routines, the first falling into the second.

**Inline data after a call.** A routine that returns past data written after each call says
so in its signature, with `inline n` for n bytes or `inline .strz` for a zero-terminated
string. The data after every call is then checked, and flow continues after it:

```nt65
.proc print: inline .strz {
    ; pull the return address, print up to the zero, push the address past it
    rts
}

.proc greet {
    jsr print
    .strz "HELLO"
    rts
}
```

**Self-modifying code.** `.patch @op` after a store says that the store writes into the
instruction at `@op`.

**Routines that never return.** `noreturn` in a signature says the routine never returns: a
reset handler, a main loop, a routine that jumps away for good. An `rts` in it is an error,
and a call to it ends the path, so nothing after the call needs an annotation. `interrupt`
says the routine is an interrupt handler: it must end with `rti`, and calling it with `jsr`
is an error.

**Labels nothing reaches** are reported as unreachable, because they are usually a missing
branch or a leftover. A label whose address is taken as data, `.addr @handler`, is reached
from somewhere nt65 cannot see; on the 65816 it needs a `.state` saying what state it is
entered in, unless a `.next` in the same routine names it.

## Cycle counts and branch range

Once widths and address sizes are known, nt65 knows every instruction's length and its cycle
count, so several things that ca65 only finds out at assembly, or never, are checked as you
type.

**Branch range.** A branch out of range is reported in the editor, where both ends are in one
run of the same segment. The fix offers the long branch.

**Long branches are built in.** `jeq`, `jne`, `jcs`, `jcc`, `jmi`, `jpl`, `jvs` and `jvc` are
written as the short branch where the target is in range, forward branches included, and as
the opposite branch over a `jmp` where it is not. ca65's `longbranch` macros can only choose
the short form for a target they have already seen; nt65 lays the code out first.

**Cycle counts.** Every instruction has a cycle count, shown on hover as an interval where
it depends on something nt65 cannot know, such as a page crossing or whether a branch is
taken. Above each routine the editor shows what one pass through it costs, from the shortest
path to the longest, and what it costs including the routines it calls. A loop counted down
with `ldx #n` … `dex` … `bne` is multiplied out; other loops show a lower bound with a `+`.

**Timing assertions.** `.mincycles(from, to)` and `.maxcycles(from, to)` are the fewest and
the most cycles one pass takes from one label to another in the same routine. They are for
code whose timing matters, such as a raster effect:

```nt65
.proc raster {
top:
    lda #1
    sta $d020
    nop
bottom:
    rts
}

.assert .mincycles(raster::top, raster::bottom) == 8, "the raster line moved"
.assert .maxcycles(raster::top, raster::bottom) == 8, "the raster line moved"
```

A span with a call or a loop in it has no fixed count, so it is an error.

## What a routine preserves

nt65 works out which of A, X, Y and the carry each routine hands back unchanged, on every CPU.
It follows saves and restores through the stack, including the 6502's `txa`/`pha` … `pla`/`tax`
for X, and it follows calls across the whole program. The editor shows the answer above each
routine, as `preserves X, Y` for instance, and on hover shows what each register holds at
each instruction.

A routine can promise to preserve registers with `keeps`, and nt65 then checks the promise
at every return:

```nt65
.proc CHROUT = $FFD2: keeps x, y    ; a fact about the KERNAL, trusted

.proc print_digit: keeps x, y {
    clc
    adc #'0'
    jmp CHROUT
}
```

`print_digit` keeps X and Y because the routine it hands off to does. If it changed Y first,
the `keeps y` would be an error at the `jmp`, saying what to do. On an extern proc or an
import there is no body to check, so `keeps` is taken on trust, which gives the facts about a
ROM routine somewhere to live.

When a routine saves a register to memory and reloads it, nt65 cannot see that the value came
back unchanged. `.state keeps x` at the point where it has says so.

## The 65816

On the 65816 the width of A and of X and Y, emulation mode, the direct page register D and the
data bank register B change how instructions assemble and what memory they reach. ca65
tracks none of it: `.a16` and `.i8` say what the programmer believes, and nothing checks it.
nt65 tracks all of it, through every routine, and checks every instruction that depends on
it. There are no `.a8`, `.a16`, `.i8` or `.i16` directives; the output gets the ones ca65 needs.

### Signatures

Every routine declares the state it is entered in and, after `->`, the state it leaves in.
A routine that leaves in the state it was entered in writes no `->`.

```nt65
.proc render: a16, i8 -> a8, i8 {
    lda #$1234                      ; A is 16-bit: a 3-byte instruction
    sep #$20                        ; A is now 8-bit
    lda #$12
    rts                             ; checked: A must be 8-bit here
}
```

Inside the routine nt65 follows `rep`, `sep`, `php` … `plp`, `xce` after `clc` or `sec`, and
calls to other routines through their signatures. Every width-dependent immediate must have a
known width, and every `rts` must leave the state the signature promises.

The signature items are:

| item | meaning |
|---|---|
| `a8`, `a16` | the accumulator's width |
| `i8`, `i16` | the index registers' width |
| `native`, `emu` | the emulation flag; `native` is the default |
| `near`, `far` | called with `jsr` and left with `rts`, or with `jsl` and `rtl`; `near` is the default |
| `dp = e`, `dbr = e` | the direct page and data bank values |
| `a*`, `i*`, `dp*`, `dbr*` | unchanged: the routine assumes nothing and hands the value back as it found it |
| `a?`, `i?`, `e?`, `dp?`, `dbr?` | unknown |
| `?` | everything unknown, for code entered from outside nt65 |
| `keeps a, x, y, c` | registers handed back unchanged (see [What a routine preserves](#what-a-routine-preserves)) |
| `inline n`, `inline .strz` | returns past data after each call |
| `args n` | the caller pushes n bytes before the call |
| `interrupt`, `noreturn` | an interrupt handler; a routine that never returns |

**Widths default to "unchanged".** A routine that writes no width assumes nothing about it,
so it can be called in any state, and it must hand the widths back as it found them. That is
exactly right for code that never uses a width-dependent immediate. Code that does gets an
error, and you add `a8` or `a16` to the signature:

```text
main.nt65:4:5: error: `lda #` needs the width of A, and `f` says `a*`, which assumes nothing about it [width-unknown]
```

**Signature sets.** Most routines of a program share a state, so name it once:

```nt65
.signature std = a8, i16, dbr = $80

.proc clear_line: std {
    rep #$20
    lda #0
    ldx #0
@loop:
    sta f:$7e2000,x
    inx
    inx
    cpx #64
    bne @loop
    sep #$20
    rts
}

.proc long_work: std, far {
    .ensure a16
    lda #$1234
    .ensure a8
    rtl
}
```

Items after the set's name replace the set's for the same part. A set is exported and
brought in with `.use` like a constant.

**Calls are checked.** A `jsr` checks the caller's state against the callee's entry, and the
state after the call is the callee's exit. A `jsr` to a `far` routine is an error that names
`jsl`, and so is an `rts` in a `far` routine. Every routine you call needs a signature, so a
ROM entry or a routine from hand-written ca65 is declared with one:

```nt65
.proc COP_HANDLER = $00ff00: ?, far
.import _memset: proc(a16, i16)
```

A routine with no body must write at least one item on the 65816, because nothing else can
tell nt65 what it expects. `?` says nothing is known.

### Setting and asserting state

**`.ensure a16, i8`** makes widths hold, writing only the `rep` or `sep` the analysis says is
needed there, or nothing. It is the checked replacement for macros that switch widths and
track them in a stack that follows the text rather than the code.

**`.state a16, dbr = $7e`** asserts what the state is at a point, and sets it where nt65 does
not know. After a label that is entered from somewhere nt65 cannot see, a `.state` is that
label's declaration. It is also what follows a `plp` of a value nt65 did not see saved:

```nt65
.proc restore_flags: a8, i8 -> a16, i8 {
    lda saved_p
    pha
    plp
    .state a16, i8                  ; nt65 cannot know what was in saved_p
    rts
}
```

The editor's fixes write `.state` lines from what the analysis finds reaching a label.

### The stack

nt65 tracks what each routine pushes, so a save and its restore need no annotation, and it
names stack slots for you. `.frame name: T` lays the struct `T` over the top of the stack,
and `name::member,s` is that member's offset from the current stack pointer, counting every
push and pull since the frame:

```nt65
.struct Locals {
    count: .word
    src:   .addr
}

.proc copy: a16, i16 {
    pea 0                           ; src
    pea 0                           ; count
    .frame locals: Locals
    lda #8
    sta locals::count,s             ; 1,s
    pha
    lda locals::src,s               ; 5,s: the pha is counted
    pla
    pla
    pla
    rts
}
```

`args n` says the caller pushes n bytes before the call. A frame can then reach the
arguments above the return address, and every call is checked for having pushed enough.

### Direct page and data bank

A direct operand reaches D plus its offset, and an absolute operand reaches bank B, so an
instruction that assembles correctly can still reach the wrong memory. nt65 checks both,
against what segments declare. It is opt-in: a program that stays in bank 0 with D at 0
declares nothing and is checked for nothing.

```nt65
.segment HUD_DP: zp, dp = $2100
.segment WRAM: abs, bank = $7e
.segment LORAM: abs, bank = $7e, mirrors = [$00..$3f, $80..$bf]
```

With those declared, a direct operand naming a `HUD_DP` symbol is an error where D is known
to be something other than `$2100`, and an absolute operand naming a `WRAM` symbol is an error
where B is known to be a bank that cannot see it. Where D or B is unknown, nothing is
reported; a routine that declares them in its signature, `dp = 0, dbr = $7e`, makes them
known. nt65 recognizes the usual idioms that set D and B: `pea $2100` then `pld`,
`lda #$7e` / `pha` / `plb`, and `phk` / `plb`.

Hardware registers that only some banks can see are described in `nt65.json`:

```json
"ranges": {
  "$2100-$21ff": ["$00-$3f", "$80-$bf"],
  "$4200-$43ff": ["$00-$3f", "$80-$bf"]
}
```

`sta $2100` with B known to be `$7e` is then an error. A routine that only needs B to be
one of a set of banks says so: `dbr = [$00..$3f, $80..$bf]`.

**`d:` reaches a constant address through the direct page.** With D known to be `$2100`,
`lda d:$2105` is written as `lda z:$05`. It is the checked form of subtracting the direct page
by hand, which goes wrong without a word when D changes.

### Code shared with the 6502

A module shared between CPUs is compiled under each program's `.cpu`. On the 6502 family
there is no state to check: `native`, `emu`, `dp` and `dbr` are accepted and ignored, `.state`
and `.ensure` write nothing, and `a16` or `i16` are errors, because no 6502 register is ever
16 bits wide. So a routine like this builds for both:

```nt65
.proc putc: ?, near {
    .ensure a8, i8                  ; sep #$30 on the 65816, nothing on the 6502
    sta $d000
    rts
}
```

Where a signature has to differ by CPU, put a signature set under an `.if .target(65816)`
and give the routines the set.

### Porting a 65816 program

A 65816 program brought over from ca65 builds once three things are written down. They are
the three things nt65 cannot work out for itself, and until they are there most of the
errors it reports are consequences of not knowing them.

1. **A signature on every routine.** Write a `.signature` set for the state most of the
   program runs in, give it to every routine, then fix the few that differ.
2. **Second entry points as separate routines.** A ca65 routine often has a label part-way
   down that other code jumps to. Cut the routine in two at that label and end the first half
   with `.fallthrough` naming the second, which then takes a signature of its own.
3. **ROM and toolbox addresses as extern procs**, each with the state it is called in:
   `.proc TOOLBOX = $e10000: a16, i16, far`.

After those, the errors left are real ones: an immediate whose width was never set, a `jsr`
to a far routine, a label something jumps into that no `.state` describes. Each names its fix,
and the editor can write it.

## Code for another processor

A program often carries code for a second processor: the SNES sound CPU's driver, a disk
drive's half of a fast loader. Its bytes are linked into the host's image and copied across at
run time, so its segment loads in the host's memory but runs in the other processor's. nt65
calls that other memory an *address space*. The project declares it, and each segment says
which space it is in:

```json
"spaces": { "spc": "data" },
"segments": {
  "SPCIMAGE": { "size": "abs", "space": "spc" }
}
```

`"data"` means the space runs another processor, so its segments hold data and no
instructions. The other processor's code is written with macros that emit its instructions as
bytes. `"code"` means it runs this program's processor, as the second 65816 of an SA-1
cartridge does, and its routines are checked like any others.

A name in another space is only a number to this processor. Code may load it as an immediate
and data may hold it, which is how the host tells the other processor where to start, but a
jump, a call or a memory access through it is an error. `.loadof(S)`, `.runof(S)` and
`.spanof(S)` give a segment's load address, run address and size, as ld65 defines them for a
segment with `define = yes`:

```nt65
.import spc_entry: abs in SPCIMAGE

.proc upload_spc: a8, i16 {
    ldx #0
@loop:
    lda f:.loadof(SPCIMAGE),x
    sta $2140
    inx
    cpx #.spanof(SPCIMAGE)
    bne @loop
    ldy #spc_entry
    rts
}
```

## Programs built from includes

Some ca65 programs are a single translation unit: a top file that `.include`s the rest in
order, with code in one file running straight into code in the next. Separate modules cannot
express that, because ld65 puts object files in whatever order the build lists them.
*Placement* can. `.place m` writes module `m`'s bytes where the line stands, in every segment
`m` writes to:

```nt65
.module program

.cpu 6502

.place header                       ; each is declared `.module header: placed`
.place tokens
.place interpreter
```

```nt65
.module interpreter: placed

.cpu 6502

.segment CODE
.proc restore {
    lda #0
    rts
}

.place iscntc                       ; this platform's check for control-C, which runs into stop

.export .proc stop {
    rts
}
```

```nt65
.module iscntc: placed

.segment CODE
.export .proc ISCNTC {
    lda $c6
    .fallthrough interpreter::stop
}
```

A module declared `placed` is written where its `.place` stands and has no output of its own,
so the program above builds to a single `program.s` and there is no link order to get wrong. A
placed module is still a module, with its own names, exports and privacy, and `.fallthrough`
may run into another module's routine when both are in one translation unit. A module that
one program places and another links on its own is declared `placeable`.

`.place` never stands under an `.if`: which modules share a file does not depend on the
configuration. Platform code is a placed module whose contents are under an `.if` of their
own. New programs rarely need placement; it is for bringing over programs written this way.

## Working with C and hand-written ca65

nt65 never reads ca65 source, and ca65 never reads nt65. Everything they share is a linker
symbol, which is why they mix in one build without either knowing about the other.

**Importing.** A symbol from ca65 or cc65 is imported with what nt65 needs to know about it:

```nt65
.import _printf: proc()             ; a routine, with its signature
.import sp: zp                      ; an address in zero page
.import actors: .type Actor[8]      ; storage, with its type
.import VIC_BORDER = $D020          ; a value nt65 needs, checked by ld65 at link time
```

A typed import can be measured and indexed like local data: `.sizeof(actors)`,
`actors[2]::hp`. A checked import, `NAME = value`, gives nt65 a value to use now, and the
output asserts it with `lderror` so that ld65 fails the link if the ca65 side disagrees.

**Include files of constants.** `nt65 import-inc hw.inc` converts a ca65 include file of
constants into an nt65 module, once, for you to keep as source. Every line it cannot convert
is kept as a comment saying so.

**The C header.** `nt65 build --c-header nt65.h` writes what the program exports as C
declarations for cc65: structs member by member with their sizes checked, enums, constants,
and `extern` data and routines. C and nt65 then share one definition of each type. A routine
or data declaration appears in it only when its linker name starts with cc65's underscore,
`.export fill as "_fill"`, which C then calls `fill`; each one left out gets a warning saying
so.

**Start-up code.** nt65 has no `.constructor`, `.destructor` or `.interruptor`. A routine that
cc65's runtime should run at start-up is registered by a small ca65 file that calls it.

## Diagnostics

Every diagnostic has a stable kebab-case name, printed after the message:

```text
src/main.nt65:14:5: error: `far_routine` is far, so call it with `jsl` [call-distance-mismatch]
```

The name is what you use everywhere else:

- `nt65 explain call-distance-mismatch` prints a longer explanation of the rule and how to fix
  it. `nt65 explain` alone lists every name.
- `nt65.json` changes how much a warning matters: `"diagnostics": { "unused-symbol": "off",
  "mnemonic-name": "error" }`. A named configuration can do the same, so a release build can be
  stricter than the one you work in. An error cannot be turned down.
- `nt65 build --json` writes one JSON object per diagnostic, for CI and other tools.

`nt65 build` exits with 0 when the program built, 1 when it has errors and 2 when the command
line is wrong.

The warnings you will meet most are these. A declaration nothing uses and nothing exports is
`unused-symbol`, and the editor fades it. A `.use` item nothing uses is `unused-use-item`. A
label nothing reaches is `label-unreachable`. A name that is also an instruction, such as a
constant called `lda`, is legal but is `mnemonic-name`, because the next reader will take it
for an instruction.

## In the editor

The VS Code extension runs nt65's language server. Other editors that speak LSP can run it
with `nt65 lsp`; the [README](../README.md) shows the configuration for Neovim, Helix and Zed.
The server analyzes the whole program as you type, including files you do not have open, so
everything below works across modules.

- **Navigation:** go to definition, find references, highlight, rename (including through
  `.use`, macros and families), call hierarchy, workspace symbol search, the outline, folding
  and expand selection. An `.incbin` path is a link to its file.
- **Hover:** a symbol's declaration and the comment above it, its value or address size, its
  segment and size; an instruction's cycle count, the flags it writes and, on the 65816, the
  processor state reaching it; what a routine costs and which registers it preserves.
- **Lenses** above each routine: what one pass costs, what it costs with its calls, and which
  registers it preserves.
- **Inlay hints** at the end of a line, off by default for cycle counts: where a width or
  other state changes, where a long branch was written long, values a declaration implies,
  and parameter names in calls.
- **Completion** of what can be written where the cursor is, and signature help in macro and
  function calls.
- **Fixes** for most diagnostics: the long branch where a short one cannot reach, `jsl` for a
  `jsr` to a far routine, the missing `.export` or `.use`, a `.state` saying what reaches a
  label, `.fallthrough` for a routine that runs into the next, `.byte[n]` for a `.res`, the nt65
  spelling of a ca65 directive, and the declared name a misspelling is close to. Where a line
  could mean two things, such as an expression that needs parentheses, both readings are
  offered and neither is applied for you.
- **Refactorings** on a selection: bring a path in with `.use` or write it out in full; export
  or stop exporting a declaration; declare what a 65816 routine leaves; turn `rep #$20` into
  `.ensure a16` and back; give a number a name; turn a label into a cheap local or the other
  way round; move a routine's data into a segment block; extract lines into a routine of their
  own; and read pasted ca65 as nt65, as far as one line at a time can be converted.
- **Commands:** *Show Output Beside* shows the ca65 the current file becomes, and moving in
  either text highlights the matching lines in the other. *Show Macro Expansion* writes a macro
  call out. *Select Configuration* chooses which configuration the editor analyzes, and *Toggle
  Cycle Counts* switches the cycle hints on.
- **Formatting:** the same layout `nt65 fmt` writes.

## The command line

```text
nt65 build [options] [file.nt65...]
nt65 init [dir] [--cpu cpu]
nt65 fmt [--check] [file.nt65...]
nt65 remap-dbg file.dbg [--out file]
nt65 explain [name | --markdown]
nt65 import-inc file.inc [-o file.nt65] [--module name]
nt65 lsp
```

`nt65 build` builds the program the nearest `nt65.json` describes. Naming files builds the
whole program and writes only those files' output. Its options are:

| option | |
|---|---|
| `--project <file>` | the project file, or the folder that holds it |
| `--config <name>` | a named configuration |
| `--cpu <cpu>` | `6502`, `6502x`, `65sc02`, `r65c02`, `65c02` or `65816`, when the project does not say |
| `-D NAME[=value]` | a define, or a module's `.config` setting by its path |
| `--out <dir>` | where output goes |
| `--depfile <file>` | make-style dependencies of every output |
| `--c-header <file>` | a C header of what the program exports |
| `--check` | report problems and write nothing |
| `--stdout` | write one file's ca65 to standard output |
| `--watch` | build again whenever a source changes |
| `--json` | one JSON object per diagnostic on standard output |

An output whose contents did not change is not rewritten, so make and similar tools rebuild
only what an edit affected. `nt65 fmt` writes files in nt65's one layout, and `--check` lists
the files that are not in it and exits 1, for a CI gate.

The CPUs are the NMOS `6502`; `6502x`, the NMOS 6502 with its undocumented opcodes (`lax`,
`sax`, `slo` and the rest, spelled as ca65 spells them); `65sc02`, the original CMOS set;
`r65c02`, Rockwell's, with `bbr`, `bbs`, `rmb` and `smb`; `65c02`, WDC's, which adds `wai`
and `stp`; and the `65816`. Using an instruction the CPU lacks is an error that names the
CPUs that have it.

## Migrating from ca65

### Directives

| ca65 | nt65 |
|---|---|
| `.setcpu "65816"` | `"cpu"` in `nt65.json`, `--cpu`, or `.cpu 65816` |
| `.segment "CODE"` | `.segment CODE`, a region to the next one; `.segment CODE { }` for a block |
| `.zeropage`, `.code`, `.rodata`, `.bss`, `.data` | `.segment ZEROPAGE` and the rest |
| `.pushseg` / `.popseg` | a `.segment NAME { }` block |
| `.org`, `.reloc` | the linker configuration |
| `.proc name` … `.endproc` | `.proc name { … }` |
| `.scope name` … `.endscope` | `.scope name { … }` |
| `label: .res 2` | `.data label: .word`, or `.byte[2]` |
| `label: .byte 1, 2, 3` | `.data label: .byte 1, 2, 3` |
| a label over several data lines | `.data label { … }` |
| `.tag Player` | `.type Player` |
| `.struct` … `.endstruct`, `.enum`, `.union` | the same words, with braces |
| `.asciiz "text"` | `.strz "text"` |
| `.dbyt` | `.beword` |
| `.charmap $41, $01` | `.charmap screen { 'A'..'Z' = $01 }`, applied as `screen("TEXT")` |
| `.include "hw.inc"` | a module that exports what the file declared, and `.use`; `nt65 import-inc` converts a file of constants |
| `.include "part.s"` of code that must land where the line is | a module declared `placed`, and `.place` |
| `.export` and `.import` between files | `.export` in one module, a path or `.use` in the other |
| `.global`, `.local` | `.export`, and scoping by structure |
| `.define NAME 5` | `NAME = 5` |
| a function-like `.define` | `.func name(args) = expr` |
| `.define` of a list | `.list name { … }` |
| `.set` counters | `.enum`, or the index of a `.repeat` |
| `.ifdef NAME` | `.if .defined(NAME)` |
| `.if` on a program symbol | `.assert`, or a define |
| `.ifp02`, `.ifpc02`, `.ifp816` | `.if .target(6502)` and so on, or `.if .has(phx)` |
| `.assert expr, error, "m"` | `.assert expr, "m"` |
| `.a8`, `.a16`, `.i8`, `.i16`, `.smart` | a signature, `.state` and `.ensure` |
| unnamed labels `:`, `:+`, `:-` | `@name` |
| `.ident`, `.concat` to build names | a scope, or `.each` over an enum |
| `.sprintf`, `.concat` and `.left` on text | `.strcat`, `.strsub` and `.strat`, in a `.func` |
| `.feature`, `.macpack` | nothing: one grammar, long branches are built in, and `scrcode` is a charmap of `nt65::cbm`, `nt65::atari` or `nt65::apple2` |
| `.constructor`, `.destructor`, `.interruptor` | a ca65 stub that calls the nt65 routine |

### Macros

| ca65 | nt65 |
|---|---|
| `.macro name arg1, arg2` … `.endmacro` | `.macro name(arg1, arg2) { … }` |
| `name arg1, arg2` | `name!(arg1, arg2)` |
| `.paramcount`, `.blank` for optional arguments | defaults, `(count = 1)`, and named arguments |
| `.match` to tell an immediate from memory | an `operand` parameter, `.mode(p)` and `.byteof(p, n)` |
| an argument split at its comma, `add buf,x` | a braced operand, `add!({buf,x})` |
| `.xmatch` against register names | `one(a, x, y)` |
| variadic macros that recurse | a `list(...)` parameter and `.each` |
| `.exitmacro` | `.if` inside the body |
| structured `if`/`else` with `.set` label stacks | a macro with `block` parameters |
| `.asize`, `.isize` | a state signature on the macro, `.macro m(): a16 { }` |
| `.local` labels in a macro | nothing: every name in a body is local to its expansion |

In the editor, paste ca65 in, select it and choose *Read the selection as nt65*: spellings,
block words, segment directives and ca65's operator words are rewritten. What needs a decision
rather than a new spelling, such as an unnamed label, a macro call or an `.include`, is left
as it was for you and the diagnostics to work through.

### Things that will catch you out

1. `%` is not modulo; it starts a binary number. Use `.mod`.
2. `=` defines; compare with `==`.
3. `#<label+1` is an error. Say which you mean with parentheses.
4. A label outside a `.proc` is an error. Data is `.data name: ...`.
5. `.byte[4] { 1, 2, 4 }` is an error, not padding.
6. The register names `a`, `x`, `y` and `s` are reserved in any case, so `S` and `X` cannot be
   names either. Mnemonics are not reserved: `lda = 5` is a constant, with a warning.
7. `.use` paths start at the root of the modules.
8. A module's names are private until exported, even to the module next to it.
9. `.if` cannot test a program symbol, only defines and `.config` settings.
10. On the 65816, a routine with no widths in its signature assumes nothing about them, so
    `lda #$12` in it is an error until the signature says `a8` or `a16`.
11. On the 65816, a `jsr` to a far routine is an error, not a truncated address.
