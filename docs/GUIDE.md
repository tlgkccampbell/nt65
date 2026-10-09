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
- Branch range, cycle counts, and which registers a routine reads and preserves are worked out
  as you edit, not discovered after a build.
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

`nt65 init` writes a project that builds: an `nt65.json` and a `src/main.nt65`. Run it in a
folder that already has your ld65 configs, and the project links them, so they declare its
segments: one config directly, and several as one named configuration each. A project is a
folder with an `nt65.json` in it:

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
nt65 remap-dbg game.dbg --labels game.lbl
```

The last line is optional. Each `.s` is written with a `.s.lines` file beside it that maps
every line of output back to the `.nt65` line it came from. `nt65 remap-dbg` adds that
mapping to the debug file ld65 wrote, so a debugger such as Mesen or VICE shows your `.nt65`
source. Without it the debug file still works; it just refers to the generated `.s`.

`--labels` also writes a label file that names every address by its path in the source, such
as `wave::shown`, in the format ld65's `-Ln` writes, which VICE's monitor reads with
`-moncommands`. ld65's own label file names a name a module does not export as the `.s` spells
it, `shown`, so two modules' private names can be the same in it; in nt65's they cannot. A
cheap local, and a name a macro or a repetition declares, has no path and is left out.

A build that finds an error writes nothing, so a half-built program never reaches ca65.

## A first program

```nt65
; Fill four pages of screen memory with spaces, forever.
.module main

.cpu 6502

.const SCREEN       = $0400
.const SCREEN_PAGES = 4

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

.export .const BORDER = $D020

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
absolute. You declare others yourself, in a source file, in `nt65.json`, or by linking your
linker configuration, below:

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
configuration's business. nt65 never writes one, but it can read yours, so that the segments
are written down once:

```json
"links": { "prg": { "config": "c64.cfg" } }
```

With `links` in `nt65.json`, each segment in the config's `SEGMENTS` block is declared: `zp` if
its `type` is `zp` or it runs in page zero, and `abs` otherwise, in the bank it runs in. A
segment the config does not place is an error where you name it, before ld65 would stop the
link, and so is `.loadof` of a segment the config does not give `define = yes`. `segments` in
`nt65.json` then adds only what a config cannot say, such as `"far"` or a `dp`. A program
linked twice, such as a cartridge and a music file, names both configs, and a segment they
both place must be the same in each.

**Values ld65 never writes are an error.** ld65 writes no bytes for a `bss` or `zp` segment,
and none for a segment that loads into a memory area with `file = ""`. Values given there,
such as `.data count: .byte 3` in `ZEROPAGE`, never reach memory, and neither does code. Leave
the values out and set them in code, or load them with the program and copy them where they
run, as a `DATA` segment with `load = ROM, run = RAM` does.

**Banks and overlays cannot see each other.** When a linked config runs two segments in
different memory areas that cover the same addresses, such as the switchable banks of a
cartridge mapper or two disk overlays with one load address, only one of them is ever mapped.
Code in one that jumps to, calls, branches to, reads or writes anything in the other is an
error, on every processor. Go through code both can see, such as a trampoline in the fixed
bank. Taking the address as a value, `#<name` or `.addr name`, is fine, because that is how
the trampoline is told where to go. nt65 tracks no mapper state: code in the fixed bank may
name anything, and nothing is reported where the config does not say where a segment runs.

A config can give each such memory area a `bank` attribute, the number the program writes to
its mapper to show it. `.bankof(name)` is that number for the area `name` runs in, which ld65
works out, so code in the fixed bank maps a bank by naming what it is about to call or read:

```nt65
lda #.bankof(draw::box)         ; the bank the config puts draw::box in
sta RAM_BANK
jsr draw::box
```

Only ld65 knows the number, so nt65 writes it for ld65 to work out, as a byte, and an `.if`
cannot test it; check it with an `.assert`.

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

**Data found elsewhere.** `.data name: element = address` names data that something else put at
an address: another declaration, another program or the hardware. It has no bytes of its own,
but has the type, size and fields its element gives it. A hardware register is declared with
`.mmio` in place of `.data`, which says that the hardware owns what it holds, so the editor does
not ask a routine's callers where a register's value came from. A register that reads back what
was last written to it, such as a bank latch, behaves as memory and stays `.data`.

```nt65
.mmio VIC_BORDER: .byte = $D020
.mmio CIA1:       .byte[16] = $DC00
```

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

.const ERR_SYNTAX = messages::SYNTAX - messages ; 16
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
.const TOKEN_FOR = keywords::FOR - keywords  ; 3
```

A function is worked out along with the constants, before anything is assembled, so the text
it returns has a known length and `TOKEN_FOR` is a constant. The same table written by a
macro would have no size until the macro was expanded.

`.strz "text"` writes a text and a zero after it. A zero byte inside the text is an error,
because the string would end early.

## Constants, functions and the build configuration

`.define` is gone, because it substitutes text. Each of its uses has a replacement:

```nt65
.const LINES = 25                   ; a constant
.func rgb15(r, g, b) = r | (g << 5) | (b << 10)
.const VOICES ?= 3                  ; a setting: 3 unless the build says otherwise
.const DEBUG  ?= 0

.const TRACE_LEVEL = .select(DEBUG, 2, 0)
```

- **A constant** is assigned once, may be used before it is defined, and may not depend on
  itself. It is a number or a text, never an address: an address is declared as what is there,
  `.data TXTPTR: .addr = CHRGOT + 1` or `.proc CHROUT = $ffd2`.
- **A `.func`** takes values, not tokens. `rgb15(1 + 1, 0, 0)` passes 2, where a ca65
  `.define` would substitute the text `1 + 1` and let precedence decide what it meant. A
  parameter may have a default, and a call may name arguments after its positional ones, as a
  macro call does: with `.func scaled(value, factor = 2) = value * factor`, `scaled(3)` is 6
  and `scaled(3, factor = 4)` is 12.
- **A setting** is a constant declared with `?=`, whose value is a default the build may
  change. The build sets it by its path, `-D audio::VOICES=4` or `"audio::VOICES": 4` under
  `settings` in `nt65.json`, or by its name alone, `-D DEBUG=1`, when no other module has a
  setting of that name. A setting is declared at file level, outside every block.

**Conditions test the configuration, never the program.** An `.if` condition may use numbers,
operators, the built-in functions that measure nothing, settings, and the constants and
functions built only from those at file level, outside every block. That is what lets nt65
know which declarations exist before it reads any of them. Nothing marks such a constant:
nt65 works it out, and the editor says so on hover. A constant declared under an `.if`, or
built from a size or an offset, is known only once the declarations are read, and a
condition that uses one is an error that says why. A check that depends on the program, such
as a table's size, is an `.assert`:

```nt65
.assert .sizeof(Actor) <= 8, "Actor must fit an 8-byte slot"
```

nt65 checks an `.assert` as you type when it can. When it depends on addresses only the
linker knows, nt65 writes it into the output for ld65 to check. `.assert` takes no level: a
failed assertion is always an error. `.error "text"` inside an `.if` refuses a configuration,
and `.warning "text"` builds it with a message.

In `&&` and `||`, the right side is evaluated only when the left does not decide the answer.

Declarations inside an `.if` belong to the surrounding scope. The same name may be declared
in several branches, and only the branch the configuration takes counts. The editor greys
out the branches the current configuration does not take. Another `.if` cannot test a
constant declared in a branch, so a value that differs between builds is one declaration with
`.select`, as `TRACE_LEVEL` is above.

**`.select(c, a, b)`** is `a` when `c` holds and `b` when it does not. Only the chosen side is
evaluated, so the other may name something this build does not declare:

```nt65
.const COLUMNS = .select(WIDE, 80, 40)
```

**Sets.** `v .in [a, b, c..d]` is 1 when the set holds `v` and 0 when it does not, and
`.switch(v, set, result, ..., otherwise)` is the result after the first set that holds `v`. A
set is values and ranges in brackets, or the name of a `.list`. `.switch` reads only the result
it chooses, as `.select` does, and a `.switch` with no `otherwise` is an error for a value no set
holds:

```nt65
.func operand_size(m) = .switch(m,
    [Mode::imp, Mode::acc], 0,
    [Mode::abs..Mode::ind], 2,
    1)
.assert main .in [$8000..$ffff], "main is in ROM"
```

**Long expressions.** A line whose `(` or `[` is still open at its end continues onto the next,
as the `.switch` above does, and each line may have a comment. A macro call's arguments and a
`.func` call's continue the same way, `name = value` arguments included, and so do the
parameters of a `.macro` or a `.func`, `name = default` included, and a macro's `name: kind`:

```nt65
.func scaled(
    value,                          ; the value
    factor) = value * factor        ; and what it is multiplied by
```

Only those brackets carry a line on: the parentheses of `(ptr),y` stay on one line, in an
instruction and in a braced argument alike. A line that starts a statement of its own, such as
an instruction or a directive, is never joined to the one before it, so a bracket left open by
mistake is reported where it is. That holds inside a macro call and a list of parameters too, so
an argument or a parameter named like an instruction, such as `inx`, does not start a line
there.

**CPU tests.** `.if .has(phx)` holds on every CPU that has the `phx` instruction, and
`.target(65c02)` names one CPU exactly. The CPU is part of the configuration.

**Named configurations.** `nt65.json` can describe several builds of the program:

```json
{
  "cpu": "6502",
  "files": ["src/**/*.nt65"],
  "out": "build",
  "configurations": {
    "debug": { "settings": { "DEBUG": 1 }, "out": "build/debug" },
    "pal":   { "settings": { "hw::PAL": 1 }, "out": "build/pal" }
  }
}
```

`nt65 build --config debug` chooses one, and the editor has a setting for which one it
analyzes. With none chosen, the project's own settings build. When they cannot, because each
configuration is a different target, `"default": "name"` says which configuration builds instead.

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

`=` never compares. Where C's order is easy to misread, nt65 requires parentheses:

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
.const TURN  = 256
.const SCALE = 127

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
`.min`, `.max`, `.addrsize` (1, 2 or 3 for an address's size), `.bankof` (in
[Segments](#segments)), `.has`, `.opcode` (in [What nt65 follows through your
code](#what-nt65-follows-through-your-code)), `.target`, `.select`, `.switch`, the size functions of [Data has a type](#data-has-a-type), the text
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
`note!(C4, frames = 8)`. A `.func` takes defaults and named arguments by the same rules.

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

**Macros that come with nt65.** Three modules hold the macros most programs would otherwise
write for themselves:

- `nt65::wide` works on values of 2 to 4 bytes: `mov!`, `add!`, `sub!`, `inc!`, `dec!`, `cmp!`,
  `neg!`, `asl!`, `lsr!`, `add8!` and `adds8!`, which adds a signed byte. `ldax!` and `stax!`
  move a 2-byte value through A and X, as cc65 passes one.
- `nt65::cmos` spells the 65C02's `stz`, `phx`, `plx`, `phy`, `ply`, `bra`, `inc a` and `dec a`
  so that one source serves the 6502 as well.
- `nt65::regs` holds `save!`, which pushes registers around a block, and `asr_a!`, `neg_a!`,
  `abs_a!` and `sxt_a!` for signed values in A.

```nt65
.use nt65::wide::{add, inc, cmp}

.proc advance {
    add!(score, {#100}, bytes = 3)      ; a 3-byte score
    inc!(ptr)
    cmp!(ptr, {#SCREEN_END})            ; C and Z as cmp leaves them
    rts
}
```

Each macro's comment, which hover shows, says what it destroys. On the 65816 they need 8-bit
registers, and a call with a 16-bit one is an error at the call.

## What nt65 follows through your code

nt65 follows control flow through every routine: where each instruction can go next, and on
the 65816 what state the processor is in when it gets there. Most code needs nothing from you
for this. The exceptions are the tricks assembly programmers use that no tool can read from
the text alone, and each of them has a short annotation that says what the code does.

The annotations are required on every CPU, and a trick left unannotated is an error. What
each routine reads and keeps depends on where its paths go, so a path nt65 cannot see would
make those answers wrong. The one difference is a label that another routine jumps into. On
the 65816 it needs a `.state` saying what the processor state is there. On the 6502 and its
CMOS variants nt65 treats such a label as an entry where nothing about the registers is
known, and it needs nothing.

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
minus one. A table of records works the same way: where each entry is a struct with an `.addr`
member, such as an address and the bank it is in, `.next` names the table and follows the
address members.

`.next ?` says control goes somewhere you are not naming. The path ends there, and nt65
assumes the code it reaches may do anything: no register survives it, nothing is known about
the state after it, and its cost is unknown. A routine that promises `keeps` cannot keep that
promise across a `.next ?`, so where a promise matters, name the places control goes.

Most targets can be named. A jump to a ROM entry point names an extern proc declared at its
address, and a jump through a vector that the program itself, or the system at startup, fills
with one routine names that routine. `.next ?` is for code the program cannot know, such as a
user's machine code that a monitor runs.

Some routines pull their own return address, adjust the stack, and jump back through it.
`.next .return` says that such a jump goes back to the caller, as the routine's return would:

```nt65
.proc push_sign {
    pla                             ; the return address, less one
    sta ret
    pla
    sta ret + 1
    inc ret                         ; bug: assumes not on a page boundary
    lda sign
    pha                             ; left on the caller's stack
    jmp (ret)
    .next .return
}
```

The jump is checked as a return: a `keeps` promise must hold there, and on the 65816 so must
the exit state. nt65 counts what the routine leaves on its caller's stack, here one byte, and
the caller's analysis goes on from there. `.next .return 1` writes the count, which is then a
promise nt65 checks, and `.next .return ?` says it cannot be known. Labels may follow, as in
`.next .return, step`, for a jump that goes back to the caller on some paths and to `step` on
others.

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

nt65 follows the N, Z, C and V flags through each routine, from what the instructions
themselves set: a load of a constant, `clc` and `sec`, and the branches already taken on the
way. Where the flag a branch tests is known on every path to it, the branch goes one way only,
so `lda #1` then `bne @over` needs nothing more. A call returns with the flags its routine
returns with: for a routine with a body, what nt65 finds its body returns with on every path,
and for one without, what its signature gives after `->` and keeps with `keeps` (see
[Flags in signatures](#flags-in-signatures)). Every other flag is unknown after it. So the way a
ROM routine returns is written once, on the routine:

```nt65
.proc CHROUT = $FFD2: reads a -> c = 0  ; the ROM routine returns with carry clear

.proc always_taken {
    jsr CHROUT
    bcc @over                       ; so this is always taken
    .byte "INLINE TEXT", 0
@over:
    rts
}
```

Where the flags are known from something nt65 cannot see and no signature can say, `.next`
naming the branch's own target says the branch is always taken, and `.state c = 0` before the
branch says why.

`.next` is only for what nt65 cannot see. After an ordinary instruction or a direct `jsr`,
where nt65 already knows where flow goes, it is an error. After a branch nt65 proves is always
taken, the editor offers to remove it, and after one the flags show is never taken, it is an
error. So is `.next ?` after a conditional
branch, because the branch goes either to its operand or on, and neither is unknown. Where the
operand is an expression, such as `NULL-1`, write the label at that address instead.

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
instruction at `@op`. `.opcode(dex)` is the byte `dex` is written as on the program's CPU, so a
store that turns one instruction into another can say which it writes:

```nt65
    ldy #.opcode(dex)
    sty @step
    .patch @step as dex
```

`as dex` lists what the store can turn the instruction into, and nt65 then follows both: after
`@step`, X has been written either way, and the carry, which neither `inx` nor `dex` touches, is
still known. A variant keeps the instruction's addressing mode and operand, and may not touch
the stack or the widths; a branch may become another branch.

**Code inside an instruction.** Size-coded programs branch into the middle of an instruction, so
its operand runs as other instructions. `.label` names such a position, a label at an
instruction plus a number of bytes into it:

```nt65
.proc hidden_entry {
    lda $E8                         ; $A5 $E8, and $E8 runs as `inx`
    .label hidden_inx = hidden_entry + 1
    lsr a
    bcc hidden_inx
    rts
}
```

From that position nt65 decodes the bytes as the CPU runs them, until they reach the start of an
instruction as written, here the `lsr`, and follows those instructions like any others. Hover
shows them. A branch, a `.next` and an `.assert` may name the position. Every byte has to be one
nt65 knows, so an operand only the linker knows is an error, and so is a byte the CPU has no
instruction for, or an instruction that jumps, returns or touches the stack.

An instruction with several forms names one with the words `.mode` uses, such as
`.opcode(lda, absx)`, with `zp`, `zpx` and `zpy` for the direct page and `far` and `farx` for an
`f:` address. A form the CPU lacks is an error.

**Routines that never return.** A routine that no path returns from never returns, and a call
to it ends the path, so nothing after the call needs an annotation. nt65 works this out on
every processor. `noreturn` in a signature makes it a contract: an `rts` in such a routine is
an error. `interrupt`
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

The pass follows the code as it runs, so a branch counts whichever way it goes and a jump skips
what it jumps over. A path that returns before it reaches the second label is not counted. A
span whose way through makes a call or loops has no fixed count, so it is an error.

## What a routine preserves

nt65 works out which of A, X, Y and the flags C, Z, N and V each routine hands back unchanged,
on every CPU. It follows saves and restores through the stack, including the 6502's
`txa`/`pha` … `pla`/`tax` for X and `php` … `plp` for the flags, and it follows calls across the
whole program. Most instructions set Z and N, so few routines keep those, but many keep V, and a
caller that branches on a flag it set before a call relies on the routine keeping it. The editor shows the answer above each
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

A routine that writes no `keeps` promises whatever its body keeps, and its callers may rely on
all of it. Once it writes `keeps`, the list is the whole promise. nt65 still uses what the body
keeps, but a caller that relies on a register the list leaves out gets `unpromised-keep` at the
call:

```text
main.nt65:12:5: warning: this call relies on `print_digit` keeping Y, which it does but does not promise (it declares `keeps x`) [unpromised-keep]
```

A caller relies on a register when it uses, after the call, the value the register held before
it, or when its own `keeps` hands that value back. One fix adds the register to the routine's
`keeps`. The other saves the register around the call, with `pha` and `pla`, or with `phx` and
`plx` or `phy` and `ply` where the CPU has them. It is offered only where that is safe: not for
the carry, since `plp` would restore every flag; not where a label on the call's line would let
a branch skip the save; and not where the code after the call reads N or Z before setting them,
since the pull sets both. Where the reliance is deliberate, `.allow "unpromised-keep"` before
the call says so.

A routine that declares no `keeps` is inferred to keep what its body keeps, and its callers may
rely on what its own code keeps and on what the routines it calls promise. What those routines
keep without promising stays unpromised through it. If `b` declares no `keeps` and keeps Y only
because it calls `c`, which keeps Y but declares `keeps x`, then a caller of `b` that relies on Y
gets the same warning, naming `c`:

```text
main.nt65:14:5: warning: this call relies on `b` keeping Y, which it does only because `c` keeps it without promising to (it declares `keeps x`) [unpromised-keep]
```

The fix adds `y` to `c`'s `keeps`, where the promise was declined.

When a routine saves a register to memory and reloads it, nt65 cannot see that the value came
back unchanged. `.state keeps x` at the point where it has says so.

nt65 also works out which registers a routine reads: those whose values, as its caller left
them, it uses on some path, directly or through a routine it calls. The lens above each routine
shows it, as `reads A, C` for instance. A value moved to another register or pushed and pulled
back is followed, so `txa` … `sta` reads X, and a save and its restore read nothing. Anything
nt65 cannot follow counts as read. A pushed value reached by `tsx` is read, and a call to a
routine whose body is not in the program ends the list with `?`, since that routine may read
anything. It does so only where a register or the stack still holds something the caller left,
because that is all such a routine can see. A register stored to memory counts as read, even
where it is only being saved, because nt65 does not follow values through memory.

A call or a jump to a label inside another routine is answered from that label. It reads and
keeps what the path from the label reads and keeps, which may differ from what the routine does
from its top. A routine that sets Y and then runs into a shared tail reads nothing, while a call
to the tail uses the caller's Y.

A routine can declare what it reads with `reads`, and nt65 then checks its body against the
declaration. A register the body uses without its being listed is an error where it is used,
which is how a missing `clc` shows up. Callers go by the declaration. On an extern proc or an
import it is trusted, which gives a ROM routine's inputs somewhere to live, and `reads none`
says the routine reads nothing, which leaving `reads` out does not:

```nt65
.proc CHROUT = $FFD2: reads a, keeps x, y

.proc add8: reads a, c {        ; the carry comes in from the caller
    adc operand
    rts
}
```

Where a register is saved to memory, `.state saves x` directly under the store says the store
only saves it, so it is not counted as a use. It pairs with the `.state keeps x` where the value
is loaded back. Both are your word about memory, which nt65 does not check:

```nt65
.proc putc: reads a, keeps x {
    stx saved
    .state saves x
    ldx column
    sta line,x
    inx
    stx column
    ldx saved
    .state keeps x
    rts
}
```

On the 65816 the accumulator's two bytes are followed apart, because an 8-bit instruction leaves
the high byte alone. A routine that writes an 8-bit A and then uses it at 16 bits, or swaps the
halves with `xba`, reads what its caller left in the high byte.

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
| `c = 0`, `c = 1`, and the same for `z`, `n`, `v`, `d` and `i`; `cz = 0` for several at once | before `->`, the value each call must give the flag; after it, the value the routine returns with (see [Flags in signatures](#flags-in-signatures)) |
| `c`, `z`, `n` or `v` alone, after `->` | a flag the routine sets for its caller on every path, such as a found-or-not carry |
| `keeps a, x, y, c, z, n, v` | registers and flags handed back unchanged (see [What a routine preserves](#what-a-routine-preserves)) |
| `reads a, x, y, c, z, n, v`, `reads none` | registers and flags whose values from the caller it uses (see [What a routine preserves](#what-a-routine-preserves)) |
| `inline n`, `inline .strz` | returns past data after each call |
| `args n` | the caller pushes n bytes before the call |
| `interrupt`, `noreturn` | an interrupt handler; a routine that never returns |

**What a routine leaves out is inferred.** The state has five parts: A's width, the index
width, the mode, `dp` and `dbr`. A routine that writes an item for a part is held to it, at its
calls and at its returns. A part it writes nothing for is inferred:

- **The exit comes from the body.** A part every return hands back as it was entered with is
  unchanged. A part every return gives one value has that value. A part the returns disagree
  on is unknown after a call, which is an error only where a caller then needs it.
- **The entry comes from the callers.** Where every call, tail call and `.fallthrough` into a
  routine agrees on a part, the routine is entered with it, so its immediates are sized from
  it. Callers in other modules count too.

```nt65
.proc draw {                ; declares nothing
    lda #$1234              ; 16-bit, because every caller is in a16
    sep #$20
    rts                     ; inferred exit: a8
}

.export .proc main: a16 -> a8 {
    jsr draw
    rts
}
```

Only callers in the program count. C code or a ca65 object that calls an exported routine is
not checked, as nothing outside nt65 is, so a routine such code calls declares the entry it
expects. A routine whose address is taken, as `.addr`, `pea` and `#<` take it, may be called
through it from anywhere, and keeps the default entry, `a*, i*, native, dp*, dbr*`. So does a
routine nothing calls, such as a reset handler. A width-dependent immediate in such a routine
needs a width the signature writes:

```text
main.nt65:4:5: error: `lda #` needs the width of A, and `f` declares `a*`, which assumes nothing about it [width-unknown]
```

**Callers that disagree.** The widths and the mode decide how the routine's bytes are read.
Callers that enter a routine with two widths, where its body has an immediate that depends on
the width, are an error at the routine, `callers-disagree`. It lists each caller, and its fixes
declare either width. Callers then get the usual error where they call in the other state.
Where the body does not depend on the width, the callers may disagree, and the routine hands
the width back as it found it.

The direct page and the data bank decide only which memory an operand reaches, and a routine
may be meant to run with several. Callers that disagree combine as two paths do where they
meet. `dbr` becomes one of the callers' banks, and every check of a bank is made for each.
`dp` becomes unknown, which is an error only where an operand needs it, such as a `d:` operand
or a symbol in a segment that declares `dp`. The error names the callers.

Hover over a routine's name to see what is inferred for it, on the `inferred` row. The
refactoring "Declare the state … is inferred with" writes it into the signature, which makes it
a contract.

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

A routine with no body has nothing to infer from. One that writes nothing about its state is
called in any state, and returns with every part unknown, so the code after a call to it has to
set what it needs. `?` says the same in the signature.

### Flags in signatures

A signature can say what a routine needs of the flags and what it returns with. A flag takes a
value with `=`, as `dp = e` does. Before `->` it is the value every call must give it; after
`->` it is the value the routine returns with. A flag named alone after `->` is a result the
routine sets on every path, such as a carry that says whether a search found anything. Several
flags that take one value are written as one run of letters, in any order: `cz = 0, n = 1` is
`c = 0, z = 0, n = 1`, and nt65 shows it as `zc = 0, n = 1`, zeros first, each run in the order
the status register holds the flags (N V D I Z C). The entry may be empty:

```nt65
.proc SCROLL_UP = $E8EA: -> z = 1   ; the KERNAL's scroll returns with Z set

; A carry status. A path that returns without setting C is an error.
.proc find_slot: reads x -> c {
    lda table,x
    cmp #EMPTY
    rts                             ; C comes from the cmp
}

; An entry requirement, checked at each call
.proc next_row: d = 0, c = 0 -> c = 0 {
    lda ptr
    adc #40
    sta ptr
    bcc @done                       ; C is 1 on the fall-through path
    inc ptr+1
    clc
@done:
    rts                             ; checked: C is 0 on both paths
}
```

nt65 checks these where it can:

- **At each call**, the flags the callee needs on entry must be proved, and the error's fix
  inserts `.ensure c = 0` before the call. A routine that asks for nothing is unaffected.
- **At each return**, every flag the routine promises a value for must be proved, as a width
  must. A tail call hands on the flags of the routine it jumps to, so those must keep the
  promise. A result named alone must be set on every path after entry; a path that leaves the
  caller's flag in place is an error.
- **A routine with no body** is trusted, as its `keeps` is.

A routine with a body that names no flag after `->` returns with what its body is found to: the
flags it leaves as its caller set them, and the values it gives others on every path, through
the routines it calls. Its callers use that, so a helper that ends `clc` then `rts` decides a
`bcc` after a call to it. Once a routine names any flag after `->`, it promises the flags it
names and only those. nt65 still uses what the body returns, but relying on a flag it does not
name is a warning, as relying on an unpromised `keeps` is, and the fix adds the flag to its exit:

```text
main.nt65:12:5: warning: this call relies on `next_row` returning with `d = 0`, which it does but does not promise (it declares `-> c = 0`) [unpromised-flag]
```

A routine that names no flag passes on what the routines it calls return without promising, and
does not promote it, so the warning names the routine that declined, at any depth.

`keeps c` together with `-> c = 0` is an error, since the carry cannot both come back unchanged
and come back 0. The 65816's m, x and e flags are not written this way: they are the widths and
the mode, `a8`, `i16` and `native`.

### Setting and asserting state

**`.ensure a16, i8`** makes widths hold, writing only the `rep` or `sep` the analysis says is
needed there, or nothing. It is the checked replacement for macros that switch widths and
track them in a stack that follows the text rather than the code.

**`.ensure c = 0`** does the same for a flag on every CPU: it writes `clc` only where the flags
do not already prove the carry clear. It takes `c`, `d` and `i` with either value, and `v = 0`,
which are the flags one instruction sets without changing a register. On the 65816 a flag rides
along with a `rep` or `sep` the widths need anyway, so `.ensure a16, c = 0` writes `rep #$21`.
Writing `.ensure c = 0` before an `adc` says once what the add needs, and makes a hint about a
redundant `clc` unnecessary.

**`.state c = 1`** declares a flag where nt65 cannot know it, such as after a call to a ROM
routine with no signature yet, and is checked where the flags prove the other value.

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
names stack slots for you.

It also works out how many bytes each routine leaves on its caller's stack when it returns.
Almost every routine leaves none. One that pulls its own return address, as msbasic's error
paths do, can take some of its caller's bytes, and after a call to it the caller's pushes are
counted from what is left. Where the ways a routine returns leave different amounts, or one
of them goes somewhere nt65 cannot follow, the caller's stack is unknown after the call, and
a pull after it restores nothing known.

To name stack slots, `.frame name: T` lays the struct `T` over the top of the stack,
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

With those declared, a direct operand naming a `HUD_DP` symbol is an error unless D is known
to be `$2100`, and an absolute operand naming a `WRAM` symbol is an error where B is known to
be a bank that cannot see it. Where B is unknown, nothing is reported. A routine that declares
them in its signature, `dp = 0, dbr = $7e`, makes them known. An interrupt handler starts with
D unknown, because it runs with whatever D the code it interrupted held, so it sets D before
it names a `HUD_DP` symbol. nt65 recognizes the usual idioms that set D and B: `pea $2100` then `pld`,
`lda #$7e` / `pha` / `plb`, and `phk` / `plb`.

A `jsr`, `jmp` or branch to a segment whose home bank is not the caller's is an error too: it
is the same question as a switchable bank (see Segments), what the code can see, and here the
answer is `jsl` or `jml`.

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

A project that links its config gives the space to the memory the segment runs in, and every
segment that runs there is in it: `"memory": { "SPCRAM": { "space": "spc" } }` under the link.

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
.import c_sp: zp                    ; an address in zero page
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
`.export fill as "_fill"`, which C then calls `fill`. A comment at the end of the header lists
each one left out, so a name C reports as undeclared can be found there.

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

Where one place relies on what a warning reports, `.allow` says so there, rather than turning
the warning off for the whole project. The reason is optional, and the editor shows it when you
hover over the name:

```nt65
.allow "unused-symbol", "the monitor calls it by address"
.proc dump_registers {
    rts
}
```

`.allow` applies to the statement below it. Before a line that opens a block, such as a
`.proc`, it covers the whole block. It hides only a warning: an error cannot be allowed, and
neither can a diagnostic whose fix is an annotation such as `.next` or `.patch`, because the
analysis would go on following paths that are not there. An `.allow` that hides nothing is a
warning of its own, `allow-unused`, so it goes when the code it was written for changes. The
editor offers `.allow` as a fix on any warning it may hide.

## In the editor

The VS Code extension runs nt65's language server. Other editors that speak LSP can run it
with `nt65 lsp`; the [README](../README.md) shows the configuration for Neovim, Helix and Zed.
The server analyzes the whole program as you type, including files you do not have open, so
everything below works across modules.

- **Navigation:** go to definition, find references, highlight, rename (including through
  `.use`, macros and families), call hierarchy, workspace symbol search, the outline, folding
  and expand selection. An `.incbin` path is a link to its file. Go to definition on a segment
  name opens the line of each linked config that places it.
- **Hover:** a symbol's declaration and the comment above it, its value or address size, its
  segment and size; an instruction's cycle count, the flags it writes and, on the 65816, the
  processor state reaching it; what a routine costs and which registers it reads and preserves.
- **Lenses** above each routine: what one pass costs, what it costs with its calls, and which
  registers it reads and preserves.
- **Where an instruction's inputs come from.** Rest the caret on an instruction, and every line
  that set a value it reads is highlighted. On a `jsr`, the values are the ones the routine
  called reads, so you see what is passed to it without reading back through the caller.
  Each value has a colour: A, X and Y one each, the flags one between them, and on the 65816
  the two widths one. A width is named as a signature spells the one the routine called needs,
  so `a8` or `a16` for the accumulator and `i8` or `i16` for the index registers. A line that set a value is tinted in its colour, with a solid bar at its
  left edge, a tag naming the value after the code, and a mark in the scrollbar. A line the
  value only passed through unchanged, such as a call that keeps the register or the `pla`
  that restored a value pushed earlier, gets a dotted bar and a hollow tag. A value a macro set
  is shown at the macro call.

  Memory is followed as well, as a best guess. A location the instruction reads by name, or
  that the routine called reads before writing it, is a value like a register: the last store
  to it is its source, drawn with a dashed bar and a fainter tint. The bytes of a pointer such
  as `ptr` and `ptr+1`, and the members of one struct, share a chip named after the symbol.
  A hardware register declared with `.mmio` is not followed, since the hardware sets it.
  A line that might also have changed the value since, such as a store through a pointer or a
  call that may write the location, is drawn as a doubt rather than a source: a thin dashed bar
  with no tint and no scrollbar mark, and a faded tag such as `ptr?`. The hover names those
  lines too.

  The caret line gets one chip per value. `A` means every line that set it is on screen;
  `A↑12` and `A↓3` give the distance to the nearest one, above or round a loop below; `A ×2`
  counts them where there is more than one. `A↰` means the routine's caller set it, and the
  line that opens the routine gets the same chip. `A↰ ×2` means some paths set it in the
  routine and some do not. `A?` means the analysis lost track of it on some path, at a call it
  cannot follow or to a routine that does not say what it keeps. A call that reads many values
  would fill the line, so the chips are held to 60 characters, registers first, and a `+N` box
  counts the rest; `nt65.sources.chipLength` changes the limit. Hover the caret line for each
  source with its line and code, and for why the analysis lost track.

  The same caret shows where the instruction's own values go. Each line that reads a value the
  instruction writes, before anything writes it again, is tinted and barred in the value's
  colour like a source, and its tag starts with an arrow, as `→Y`. A call is such a line where
  the routine it calls reads the value, and a store to a named location is read by the lines that
  load it, as a best guess drawn dashed. Where the value leaves the routine, by a return, a tail
  call or a run into the next routine, the line gets a dotted bar and a hollow tag such as `Y↱`,
  because the caller may read it. So nothing is ever shown as dead. The caret line's chip counts
  the readers, as `Y→3`, or is `Y↱` where the value only leaves. A value nothing reads gets no
  chip. The hover lists every reader.

  Self-modifying code is linked the same way, in the memory color. On a store with a `.patch`,
  or on the `.patch` itself, the instruction it writes into is highlighted with a tag that says
  what it can run as, such as `runs as dex or inx`, or `rewritten` where the `.patch` lists
  nothing. The caret line's chip is `patches @step`. On the patched instruction, every store that
  writes into it is highlighted with a `patches @step` tag, and the chip is `patched`, or
  `patched ×2` for two stores. Every link is declared, so none is a guess.

  **Shift+Alt+PageDown** and **Shift+Alt+PageUp** move the caret through the sources, readers and patch links and back,
  leaving the highlights in place, and *Peek Input Sources* lists them all. The setting
  `nt65.sources.enabled`, or *Toggle Input Sources*, turns the feature off. It only shows what
  the analysis found and never reports a problem.
- **The margin** in front of a routine's lines draws its loops and, if asked, where control
  goes. The setting `nt65.margin`, or *Choose What the Margin Shows*, picks how much: `off`
  draws nothing, `loops` draws the loops of every routine, and `flow` also draws the branches
  and jumps of the routine at the caret. It is `off` by default. Every line of a routine
  gets the same width, so the code does not jog.

  Each loop is a bracket from its first line to its last, nested where loops nest. A trip
  count follows the last line: `×16` for a loop that counts a register down from 16, which is
  the count the cycle hints multiply by, and `×?` for one whose count the program does not
  say. The bracket of the innermost loop that holds the caret is brighter. A branch back that
  the flags prove never taken makes no loop.

  With `flow`, each branch, jump and edge a `.next` declares in the caret's routine is an arrow
  from the line that transfers to the label it goes to. Only the caret's routine has arrows, so
  a long file stays quiet. Brackets take the columns furthest from the code and arrows the
  columns nearest it, and a shorter arrow goes nearer the code than a longer one. An exit
  branch therefore crosses the brackets it leaves. Where a loop starts at the label its
  branches go back to, the bracket is the drawing of those branches. Its top ends in an
  arrowhead, an earlier branch back joins it as a tee, and none of them is drawn as an arrow as
  well. A loop entered by a jump to its test at the bottom gets a bracket from its body to its
  test, and its branch back stays an arrow.

  The arrow that starts or ends on the caret's line is drawn thicker, in a color of its own.
  One a `.next` declares has another color. A branch the flags prove always or never taken is
  faded, and so is an arrow from a line nothing reaches. Only transfers that land in the
  routine are drawn: a call returns to the line after it, and a tail call or a branch to
  another routine leaves it. Brackets and arrows together take at most four columns, and
  hovering the line of an arrow that had no room says where it goes.
- **Width stripes** beside the line numbers show, on the 65816, the width of A and of X and Y
  on every line. The left stripe is A and the right one is X and Y, each bright for 16 bits
  and dim for 8. Both are purple in emulation mode. A line shows the widths its own instruction
  runs with, which are the widths that size its immediate, so a `rep` or `sep` shows the widths
  before it and the inlay hint at its end says what they become. A comment or a blank line
  takes the widths of the code below it. A width that is not known, a line nothing reaches and
  a line after a routine's last instruction have no stripe. A macro's call shows the widths its
  body is entered with. VS Code shows no hover on the icons, so the status bar, beside the
  caret's position, shows the caret line's stripes next to the registers they stand for, such
  as `A 8  XY 16` or `emulation`. Its tooltip is the key to the colors. The setting
  `nt65.widths.enabled`, or *Toggle Width Stripes*, turns them off.
- **The Data view**, in the nt65 view of the activity bar, shows how routines share the
  program's data, on the zero page or each 65816 direct page and in every other segment. Each
  page is a value of D, with the segments
  reached through it, the locations on it and, under each location, the calls that lead to the
  routines that use it. Colour says how a location is shared: by one routine, by several, by an
  interrupt handler and the code it interrupts, or by a routine that relies on it across a call
  to another that uses it as a temporary of its own, which is marked `⚠`. A glyph says what each
  routine does with it: `↓` reads it first, `↑` only writes it, `↕` both, `◦` uses it as a
  temporary. `⧉` marks pages that overlap, and locations that take the same bytes. On one page,
  two addresses the source fixes are an alias, and a byte the layout also gives to another
  location is a collision, which the page notes and the grid stripes. Two segments that the
  linked config places over the same bytes, by pinning both with `start` or `offset` or by
  running them in memory areas that overlap, share them *by config*, which is marked like an
  alias. A location that no
  instruction reaches but whose address the program takes, as `ldx #tmp`, `lda #<ptr` or
  `.addr tmp` does, is marked `◎` *address taken* rather than unused. Hardware registers reached through D, such as the
  SNES's at $2100, are a page of their own. The tree lists the registers an instruction reaches
  there, and the grid also shows, dimmed, every other `.mmio` register in the page's 256 bytes.
  Code that reaches memory while D is not known,
  such as a handler after it gives D back, is listed under `D = ?`, with where each access
  lands on every page the interrupted code holds D at. A handler that uses a location as a
  temporary, or only writes one that the code it interrupts writes and reads back, is marked
  `⚠`; one that counts or flags something for that code is not. A constant address that an
  instruction reaches through the page, such as `lda $FB`, is shown as a location named by its
  address, `$00FB`; a constant with an index register added, such as `lda 1,x`, is an offset from
  wherever the register points and is not. Selecting a row marks the
  lines it stands for. *Show Grid* draws a page as 16 rows of 16 bytes, with each location's
  bytes outlined, what is free, and what the page shares with other pages. A location is
  brighter, and its bar longer, the more often its instructions run in one pass: a loop that
  counts a register down from a constant multiplies them, and so does a call inside one. A loop
  whose count nt65 cannot know counts once, and the bar's dashed end says that it may be more.
  With the caret in a routine, the locations it uses are marked in both views, and the one under
  the caret more strongly.

  ld65 decides where data lands, and nt65 does not run it, so each location says where its
  address comes from. It is *fixed* by the source, *built* from the last build (the newest
  `.dbg` under `out`, when the build runs ld65 with `--dbgfile`; the grid says when a source
  has changed since), *predicted* from the linked config (a segment starts at its `start`, at
  its `offset` into its memory area, or after the segment before it, rounded up to its `align`), or *guessed* from the
  page's base without one. Files' bytes
  follow in the order the project lists them, which a page notes when several files share a
  segment. Overlaps and shared bytes between pages are reported only for addresses the map
  trusts, never for guessed ones. Like the input sources, the map only shows what the analysis
  found and never reports a problem.

  The data in every other segment is listed after the pages, one row for each segment. The data
  aliases at fixed addresses that an instruction reaches, such as a C64 program's screen at
  $0400, are under *fixed addresses*, and the `.mmio` registers it reaches are under *hardware*.
  These locations are shared, colored and marked `⚠` just as a page's are, so a variable in BSS
  that an interrupt handler writes is flagged like one on the zero page. An indexed access, such
  as `sta buffer,x`, counts for the location it starts from. An access through a pointer, such
  as `lda (ptr),y`, names no location and is never counted. Instead, the routines that take a
  location's address are listed under it with `◎`, which shows where each pointer to it is
  made. nt65 predicts addresses only on a page, so a location in another segment has an address
  only once a build gives it one.
- **Interrupt context.** A routine that an interrupt handler reaches says so after its
  signature in the outline, as `under nmi`, or `under nmi and main` where the rest of the
  program reaches it too. Its hover has the same as a `context` row, and its name carries the
  `interrupt` semantic token modifier, which colors it wherever it appears. A theme or
  `editor.semanticTokenColorCustomizations` can recolor `function.interrupt`. A routine is
  reached by the calls, tail calls, branches, `.next` targets and `.fallthrough` that lead out
  of a handler, but not into another handler. A transfer nt65 cannot follow, such as one under
  `.next ?`, stops the walk, and the hover of a routine under an interrupt lists the lines where
  it does so in a `not followed` row. The Data view uses the same walk.
- **The Processor view**, in the nt65 view of the activity bar, shows what the instruction
  hover shows below its rule for the caret's line, and follows the caret. On the 65816 it gives
  the mode and the widths, D and B. On every processor it gives what A, X and Y hold, with the
  constant where the instructions give one and the line that set the value, the flags as `0`,
  `1` or `?`, and the stack top first. On the 65816 each push is keyed by the stack-relative
  offset that reads it, such as `3,s`. A line with nothing that runs, such as the one that
  opens a routine, shows the next line that does. A fact that is not known says `unknown`
  rather than being left out. The stack is known only from the routine's entry, so it ends at
  the stack the routine was entered with. *Choose the Caller the Processor View Shows*, or the
  view's last row, picks one `jsr` or `jsl` to the routine, and the stack then goes on through
  the return address into what that caller had pushed. That is true only on the path through
  that call, so it is a choice the reader makes, never the default.
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
  offered and neither is applied for you. A decimal number used as an address, as in `lda 10`,
  is `immediate-missing`, fixed with the `#` it most likely lost or by writing the address in
  hex.
- **Suggestions** where code could be smaller, faster or say more, shown as hints and never
  reported by a build: a `jsr` followed by `rts` that can be a `jmp` (and `jsl` with `rtl` a
  `jml`), a `rep` or `sep` that sets a width the register already has, and a `.const` that an
  instruction uses as an address, with fixes that declare it as data with `.data`, or as a
  hardware register with `.mmio`. The flags bring more: a `.next` the flags prove, a branch
  that is never taken, a `jmp` that can be a branch a byte shorter (`bra` on the 65C02 and
  the 65816), a branch over a `jmp` that can be the opposite branch, a `clc` or `sec` that
  sets C to what it already is, and a `clc` before `adc #n` where C is 1, which can be
  `adc #n-1`. The flag analysis also follows the constants A, X and Y hold, which brings a
  `cmp #0` straight after a `lda` or `dex` that already set N and Z, an `ldx #0` where X is
  already 0, as after a `dex` and `bne` loop, and an `ldx #0` where A is 0, which can be `tax`, a
  byte shorter. A tail call is not suggested to a
  routine that depends on how deep the stack is, such as one that pops its caller's return
  address, and a routine with a branch this configuration leaves out gets no suggestions.
- **Refactorings** on a selection: bring a path in with `.use` or write it out in full; export
  or stop exporting a declaration; declare what a 65816 routine leaves; turn `rep #$20` into
  `.ensure a16` and back; give a number a name; turn a label into a cheap local or the other
  way round; move a routine's data into a segment block; extract lines into a routine of their
  own; and read pasted ca65 as nt65, as far as one line at a time can be converted.
- **Commands:** *Show Output Beside* shows the ca65 the current file becomes, and moving in
  either text highlights the matching lines in the other. *Show Macro Expansion* writes a macro
  call out. *Select Configuration* chooses which configuration the editor analyzes, *Toggle
  Cycle Counts* switches the cycle hints on, *Toggle Input Sources* switches the
  highlights of where an instruction's inputs come from, *Toggle Width Stripes* switches the
  65816 width stripes, *Choose What the
  Margin Shows* picks between nothing, loops, and loops with the caret routine's flow arrows,
  and *Choose the Caller the Processor View Shows* picks the call the Processor view's stack
  goes on through.
- **Formatting:** the same layout `nt65 fmt` writes.

## The command line

```text
nt65 build [options] [file.nt65...]
nt65 init [dir] [--cpu cpu]
nt65 fmt [--check] [file.nt65...]
nt65 remap-dbg file.dbg [--out file] [--labels file]
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
| `-D NAME[=value]` | a setting's value, by its path or its name alone |
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

**Choosing an encoding.** The NMOS 6502 runs several bytes as some of its undocumented forms,
six of them as `nop zp,x`, and ca65 writes only one. `.encoded` before an instruction gives the
byte it is written as:

```nt65
    .encoded $34
    nop $39,x                       ; ca65 writes $14
```

The instruction is still code that nt65 follows and shows on hover. The byte has to run as that
instruction in a form its operand allows, and it picks between the direct-page and the absolute
form.

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
| `.define NAME 5` | `.const NAME = 5` |
| a function-like `.define` | `.func name(args) = expr` |
| `.define` of a list | `.list name { … }` |
| `.set` counters | `.enum`, or the index of a `.repeat` |
| `.ifdef NAME` | a setting, `.const NAME ?= 0`, and `.if NAME` |
| `.if` on a program symbol | `.assert`, or a setting |
| `NAME = label + 1`, an address under a name | `.data NAME = label + 1`, with an element type such as `: .byte` where its size is needed, or `.proc NAME = label` for a routine |
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
2. A constant starts with `.const`, and `=` never compares; compare with `==`.
3. `#<label+1` is an error. Say which you mean with parentheses.
4. A label outside a `.proc` is an error. Data is `.data name: ...`.
5. `.byte[4] { 1, 2, 4 }` is an error, not padding.
6. The register names `a`, `x`, `y` and `s` are reserved in any case, so `S` and `X` cannot be
   names either. Mnemonics are not reserved: `.const lda = 5` is a constant, with a warning.
7. `.use` paths start at the root of the modules.
8. A module's names are private until exported, even to the module next to it.
9. `.if` cannot test a program symbol, only settings and the constants built from them.
10. On the 65816, a routine with no widths in its signature assumes nothing about them, so
    `lda #$12` in it is an error until the signature says `a8` or `a16`.
11. On the 65816, a `jsr` to a far routine is an error, not a truncated address.
