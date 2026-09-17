# nt65 for ca65 programmers

nt65 is an assembly language for the 6502, its CMOS variants and the 65816 that transpiles
to ca65. You write `.nt65` files, `nt65 build` turns each into an ordinary `.s` file, and
your existing ca65, ld65, linker configuration and Makefile take it from there. Hand-written
ca65 and C compiled with cc65 link with it as they always have.

What you get for the change of syntax is an editor and a build that know your program: every
name resolves before anything is assembled, address sizes are never guessed, a branch out of
range is reported as you type, and on the 65816 the register widths, the direct page and the
data bank are checked through every routine. This guide is a tour for someone who already
writes ca65. [The design](../DESIGN.md) is the full definition of the language, with the
reasons behind it.

## Getting started

nt65 runs on .NET 10. With the packages built by `scripts/package.ps1` (see the
[README](../README.md)):

```text
dotnet tool install --global nt65 --configfile artifacts/nuget.config
code --install-extension artifacts/nt65-1.0.0.vsix
```

A project is a folder with an `nt65.json`:

```json
{
  "cpu": "6502",
  "files": ["src/**/*.nt65"],
  "out": "build"
}
```

`nt65 build` in that folder writes one `.s` per module into `build`. Assemble and link them
as you would any ca65 source:

```text
nt65 build
ca65 -g build/main.s -o build/main.o
ld65 -C c64.cfg -o game.prg --dbgfile game.dbg build/main.o
nt65 remap-dbg game.dbg
```

The `.s` is the program and nothing else: no debug directives between the instructions. What
a debugger needs is in the `build/main.s.lines` map written beside it, and the last step puts
it into the debug file, so that `game.dbg` points at your `.nt65` lines. Leave the step out
and the debug file still works — it just talks about the generated `.s`.

## A first program

```nt65
; Fill four pages of screen memory with spaces, forever.
.module main

.cpu 6502

SCREEN       = $0400
SCREEN_PAGES = 4

.segment ZEROPAGE
.data ptr:    .word             ; destination pointer

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

Most of it reads as ca65. What differs:

- **Every file is a module** and says so first, with `.module`.
- **`.segment CODE` has no quotes and no mode.** At file level it is a *region*: everything
  below it, up to the next one, is in `CODE`. There is no default segment, so bytes written
  before any region are an error rather than a surprise in `CODE`.
- **Data is declared with its type.** `.data ptr: .word` is a name, a size of two bytes and a
  count of one, where ca65 writes `ptr: .res 2`.
- **Code lives in `.proc`, with braces.** Outside a proc there are no instructions and no
  labels; inside one, `@loop` is a label private to the proc.
- **A macro call is marked with `!`**, and its parameters have kinds: `dest: operand` takes a
  whole operand, so `set16!({buf,x}, $1234)` works where a ca65 macro splits at the comma.

`nt65 build` writes `main.s`, in which `inc ptr+1` has become `inc z:ptr+1`: nt65 knows `ptr`
is in a zero-page segment and says so, rather than leaving ca65 to decide from what it has
seen so far.

## What stays the same

- Mnemonics, addressing modes and operands: `lda (ptr),y`, `sta buf,x`, `jmp (vector)`,
  `lda #<label`. `z:`, `a:` and `f:` prefixes mean what they mean in ca65.
- Numbers (`$1F`, `%1010`, `'c'`), comments after `;`, and one statement per line.
- `@name` for labels you do not want to invent a global name for.
- Data directives: `.byte`, `.word`, `.dword`, `.addr`, `.faraddr`, `.res`, `.align`,
  `.incbin`, `.lobytes`, `.hibytes`, `.bankbytes`.
- Constants, `NAME = expr`, and forward references to them.
- Directives, mnemonics and register names are case-insensitive; your names are not.

## Names and modules

A module's names are private until exported. Export at the declaration, or in a list:

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

Another module names them with the module's path, or brings them in with `.use`:

```nt65
.module game

.cpu 6502

.use gfx::clear

.segment CODE
.proc start {
    jsr clear
    jsr gfx::fill
    lda gfx::BORDER
    rts
}
```

There is no `.include`. Definitions several modules share, a machine's hardware registers for
instance, go in a module that exports them. The linker sees `clear` as `gfx__clear`, so two
modules may both export `init`; `as` gives a name exactly, for C or hand-written ca65, which
is why `fill` above is `_gfx_fill`, with cc65's underscore written out.

Symbols from outside nt65 are imported, with their size or, for a routine, its signature:

```nt65
.import _printf: proc()
.import sp: zp
.import VIC_BORDER = $D020          ; a value nt65 needs; ld65 checks it at link time
.proc CHROUT = $FFD2                ; a routine at a fixed address
```

`nt65 build --c-header nt65.h` writes the program's exports as C declarations: structs member
by member, enums, constants and `extern` data and routines.

## Segments

A segment's address size is declared once, in a file or in `nt65.json`, and never where it is
used. The standard ca65 segments are already declared:

```nt65
.segment ZP2: zp                    ; a declaration: a size after the colon

.segment ZP2                        ; a region
.data scratch: .byte
```

Inside a proc, a segment block puts something elsewhere without leaving the proc. It is the
structured form of `.pushseg` and `.popseg`:

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

There is no `.org`: where a segment goes is the linker configuration's business.

## Data and types

A label is only a position in code, never a size. Every name for data is a declaration:

```nt65
.struct Actor {
    x:  .word
    y:  .word
    hp: .byte
}

.segment BSS
.data buffer: .byte[64]             ; 64 bytes, no values
.data player: .type Actor           ; one record

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

`.sizeof(player)` is 5, and `player::hp` is the address `player + 4`, so `lda player::hp`
needs no offset table. A count in brackets is checked: `.byte[4] { 1, 2, 4 }` is an error,
not three bytes and a zero. `.res` is left for padding between declarations.

## Constants, functions and configuration

`.define` is gone, because it substitutes text. What it was used for has a replacement each:

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

A `.func` takes values, not tokens: `rgb15(1 + 1, 0, 0)` passes 2. An `.if` condition tests
only the configuration, meaning defines from `nt65.json` or `-D` and `.config` settings, never
a symbol of the program. A check on the program, such as a table's size, is an `.assert`,
which nt65 evaluates as you type when it can and leaves to ld65 when it cannot.

## Expressions

Expressions follow C's precedence, not ca65's, and nt65 writes whatever parentheses ca65 needs.

| ca65 | nt65 |
|---|---|
| `a = b`, `a <> b` in a condition | `a == b`, `a != b` |
| `a .mod b`, `a % b` | `a .mod b` (`%` starts a binary number) |
| `a .xor b` | `a ^^ b` |
| `.bitand`, `.bitor`, `.bitxor` | `&`, `\|`, `^` |

Where C's order is easy to misread, parentheses are required: `a & $0f == 0` is an error, and
so is `#<label+1`, which is written `#<(label+1)` or `#(<label)+1` depending on which you mean.

## Macros

A macro's parameters have kinds, a call is marked with `!`, and an argument is a value:

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

`{#$0400}` is an immediate operand and `other` an address, and `.byteof` picks the right byte
of either. A `block` parameter takes the braces after the call, which is how structured `if`
and loops are written as library code. A macro cannot call itself, cannot declare names in its
caller, and is checked where it is declared, so rename and go-to-definition work through it
without expanding anything.

## Long branches

`jeq`, `jne`, `jcs`, `jcc`, `jmi`, `jpl`, `jvs` and `jvc` are built in. Each is written as
the short branch where the target is in range, forward branches included, and as a branch over
a `jmp` where it is not. There is no `.macpack longbranch` to include.

## Tricks the analysis needs told about

nt65 follows control flow through each proc, and asks you to say what it cannot see:

```nt65
.proc dispatch: a8, i8 {
    lda command
    asl a
    tax
    jmp (handlers,x)
    .next cmd_move, cmd_fire            ; where the indirect jump goes

.data handlers: .addr cmd_move, cmd_fire

cmd_move:
    rts
cmd_fire:
    rts
}
```

The same goes for a `.byte $2c` skip (`.next` after it), self-modifying code (`.patch`), a
routine that returns past inline data (an `inline` signature) and a proc that runs into the
next (`.next next_proc`). Everything else is assembly as usual.

## The 65816

On the 65816 every routine declares the processor state it is entered and left in, and nt65
checks every immediate, call and return against it. There are no `.a8`, `.a16`, `.i8` or
`.i16` directives to keep in step by hand:

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

- A signature lists widths (`a8`, `i16`), the mode (`native`, `emu`), `near` or `far`, and
  the direct page and data bank (`dp = 0`, `dbr = $80`). Items left out default to `a8`,
  `i8`, native and near, and D and B to unchanged. A `.signature` set names the state most
  routines share.
- After `rep #$20` the analysis knows A is 16-bit, so `lda #0` is two bytes and the output
  carries the `.a16` ca65 needs. An immediate whose width is unknown is an error.
- `jsr` to a `far` routine is an error that names `jsl`, and so is `rts` in a routine that is `far`.
- `.ensure a16` writes the `rep` or `sep` only where the analysis says it is needed.
- `.state a16, dbr = $7e` after a label says what state flow the analysis cannot see arrives
  in. Code actions in the editor offer to write it.
- `.frame locals: T` names stack slots by struct member, `locals::count,s`, with the pushes
  since the frame counted for you.
- Segments may declare their home bank and direct page, `.segment WRAM: abs, bank = $7e`, and
  an absolute operand reached with the wrong data bank is an error.

## In the editor

The language server knows the program, so most of what it offers is the analysis rather than
the text. Every diagnostic that names a fix offers it: the long branch where a short one cannot
reach, `jsl` for a `jsr` to a far routine, the missing `.export` or `.use`, a `.state` saying
what the analysis finds reaching a label, `.byte[n]` for a `.res`, the nt65 spelling of a ca65
directive, and the declared name a typo is a letter or two from. Where a line has two readings —
`a & $0f == 0`, or an immediate whose width the analysis cannot work out — both are offered and
neither is applied for you.

Selecting code offers rewrites nothing reported:

| Ask for | And you get |
|---|---|
| a path written out in full | `.use path` at the top, and the name written short everywhere the file writes it |
| a name a `.use` brought in | the path written out in full, and the item that brought it gone |
| any `.use` line | the items in order, with what nothing names taken out |
| a declaration | it exported, or no longer exported |
| a routine on the 65816 | what it leaves declared, `-> a16`, from what the analysis finds at its returns |
| `rep #$20` | `.ensure a16`, and an `.ensure` written back out as the `rep` or `sep` it assembles to |
| a number in an operand | a name for it at the top of the file |
| a label | a name of its own for a `@cheap` one, or `@cheap` for a name only its routine writes |
| a `.data` in a routine | it moved into a `.segment NAME { }` block |
| a few lines of a routine | a `.proc` of their own, a `jsr` where they were, and the state they ran under declared; it is named after the label the lines start with, and the editor opens a rename on the name so you can type your own |
| pasted ca65 | as much of it as one line at a time can be read as nt65 |

A name brought in and never written is faded, as a declaration nothing names is, and both offer
to go.

## Migrating from ca65

### Directives

| ca65 | nt65 |
|---|---|
| `.setcpu "65816"`, `--cpu` | `"cpu"` in `nt65.json`, `--cpu`, or a `.cpu 65816` item |
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
| `.include "hw.inc"` | a module that exports what the file declared, and `.use` |
| `.export` with `.import` in another file | `.export` in one module, a path or `.use` in the other |
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
| `.feature`, `.macpack` | nothing: one grammar, and long branches are built in |
| `.constructor`, `.destructor`, `.interruptor` | a ca65 stub that calls the nt65 routine |

### Macros

| ca65 | nt65 |
|---|---|
| `.macro name arg1, arg2` … `.endmacro` | `.macro name(arg1, arg2) { … }` |
| `name arg1, arg2` | `name!(arg1, arg2)` |
| `.paramcount`, `.blank` for optional arguments | defaults, `(count = 1)`, and named arguments |
| `.match` to tell an immediate from memory | an `operand` parameter, `.mode(p)` and `.byteof(p, n)` |
| a parameter split at its comma, `add buf,x` | a braced operand, `add!({buf,x})` |
| `.xmatch` against register names | `one(a, x, y)` |
| variadic macros that recurse | a `list(...)` parameter and `.each` |
| `.exitmacro` | `.if` inside the body |
| structured `if`/`else` with `.set` label stacks | a macro with `block` parameters |
| `.asize`, `.isize` | a state signature on the macro, `.macro m(): a16 { }` |
| `.local` labels in a macro | labels in the body are local to each expansion |

A macro only replaces what needs no analysis. Where a ca65 macro hid control flow or processor
state, nt65 has a language feature instead; [Appendix B of the design](../DESIGN.md) goes
through the common macro packages pattern by pattern.

Most of the first two tables are a selection away: paste the ca65 in, select it and take *Read
the selection as nt65*, and the spellings, the block words, the segment directives and ca65's
operator words are written the nt65 way. What needs a decision rather than a spelling — an
unnamed label, a macro call, an `.include` — is left exactly as it was, for you and the
diagnostics to work through.

### Ten things that will catch you out

1. `%` is not modulo: it starts a binary number. Use `.mod`.
2. `=` defines; compare with `==`.
3. `#<label+1` is an error. Say which you mean with parentheses.
4. A label outside a `.proc` is an error: data is `.data name: …`.
5. `.byte[4] { 1, 2, 4 }` is an error, not padding.
6. `x`, `y`, `s` and `a` are reserved, and so are your CPU's mnemonics; call a variable `xpos`.
7. `.use` names start at the root of the modules, never at the current one.
8. A module's names are private until exported, even to the module next to it.
9. `.if` cannot test a program symbol, only defines and `.config` settings.
10. On the 65816, `jsr` to a far routine is an error, not a truncated address.
