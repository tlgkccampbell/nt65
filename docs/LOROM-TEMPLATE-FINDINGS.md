# What porting the LoROM template says about the language

`examples/lorom-template` is pinobatch's LoROM template for the SNES ported to nt65, done in
September 2026 to replace the earlier hello-world example. The 65816 side ported completely,
and the linked image is byte for byte the upstream build except where the port chose to
differ. The sound driver did not port at all, because it is SPC700 code. This document is
what the port and the conversation around it recommend changing in the language, in the order
worth doing them, followed by what was considered and left alone.

The recommendations keep to the language's own shape: a declaration that is checked rather
than a hint, one grammar whatever the configuration, the object file as the only boundary
with ca65, and rules that are the same on every CPU and under every setting.

## Summary

1. **A foreign address space** in the program model: a segment declares the space it is
   in beside its size, bank and mirrors, cross-space references are allowed as values and
   refused as transfers, and `.loadof`, `.runof` and `.spanof` on a segment stand for the
   addresses and size ld65 defines. A foreign segment holds data and macro calls and no
   instructions, so this is the general mechanism for any coprocessor, whatever assembled
   its code, and it also serves two 65xx processors in one program.
2. **Three edits at the macro edge of data blocks**, so a foreign processor's instruction set
   can be a library module of macros: an operand parameter's expression usable in a data
   directive, macros exempt from the mnemonic-name warning, and sibling members of a data
   block reachable by bare name.
3. **Refinements of the macro parameter kinds**: a range on `const`, an enum as a kind, and
   accepted modes and address sizes on `operand`.
4. **A set of banks as a `dbr` item**, for routines that run in any bank that can see the
   registers.
5. **Declaring a predeclared segment once**, so `ZEROPAGE`, `CODE`, `BSS` and `RODATA` can
   carry a direct page, a bank and mirrors.
Items 4 and 5 are small and independent of the rest. Items 1 to 3 are one line of work:
the SPC700 driver written as nt65 data and macros needs all three, and the first is the only
substantial one. No new instruction set is part of it.

## 1. A foreign address space

### What the port met

The driver is a segment whose load address is in ROM7 and whose run address is the sound
CPU's RAM, linked in the same ld65 run as the 65816 code. That one link with two address
spaces is what gives the 65816 side the load address, the run address and the size as
linker symbols, and the driver's entry point as a RAM-space value. The port keeps the driver
as upstream's ca65, assembled with `--cpu none` through blargg's macro pack, and
`blarggapu.nt65` imports the four names as opaque symbols. nt65 checks none of them: the
immediates are sized by their imports, a wrong `.cfg` name is ld65's error rather than
nt65's, and nothing stops a 65816 module from writing `jsr spc_entry`, which links and
crashes.

### Why not per-processor support first

A 65xx can sit beside any processor, and in hobby projects it does: an AVR or a Pico the
65xx bootstraps over a link, a Z80 in a C128, a soft core on an FPGA, a homebrew CPU with no
toolchain of its own. Adding instruction sets to nt65 one at a time would never finish, and
for a processor with a real toolchain nt65 should not be the assembler anyway. What every
one of those programs gives the 65xx side is the same four things: an image of bytes, the
space it runs in, a run address, and named values in that space. That contract has nothing
to do with the coprocessor's instruction set, so the mechanism that scales is the contract.

### The change

- **A segment declares its space.** Beside its size, bank and mirrors, in the project file
  or in a file's `.segment` declaration, `space = spc` says which address space a segment
  belongs to. A segment that names none is in the host's space, which is every segment
  today, and the linker configuration is untouched: a foreign segment still loads into ROM
  and runs at its own address, as the template's `SPCIMAGE` does. An address has a space;
  a module does not, and nothing is said per module. Modules of constants and macros
  belong to no space, which is right for a macro pack used by both sides.
- **A foreign segment holds no instructions.** The program's CPU stays one, as today. An
  instruction, so a `.proc`, in a region whose segment is in a foreign space is an error;
  what goes there is data blocks and macro calls, the way the template's driver is written
  under `.setcpu "none"`. The foreign processor's instruction set is a library module of
  macros over data (below), or, for the few processors worth it, a built-in table later;
  the space does not care which.
- **A segment's addresses are nt65's to name.** `.loadof(SEGMENT)`, `.runof(SEGMENT)` and
  `.spanof(SEGMENT)` stand for the `__SEGMENT_LOAD__`, `__SEGMENT_RUN__` and
  `__SEGMENT_SIZE__` that ld65 defines for a segment with `define=yes`, so the host reads
  the ROM copy of an image and tells the coprocessor where it runs without importing
  three opaque names.
- **Cross-space references are values.** A reference is checked by the segment of the
  name against the segment of the code that writes it. 65816 code may take a name from
  another space as an immediate, put it in data, or read the ROM copy through a far
  operand. Using one as a jump, branch or call target, or as an absolute operand, is an
  error that says which space the name is in.

This is the same work the C64 fast loader that runs half its code on the 1541, the BBC Micro
program that runs on the Tube second processor, and the SA-1 game with two 65816s all need.
Each pairs two processors of one family, so a 65xx space other than the host's holds
instructions for the program's own CPU and needs nothing else, and the C64 case is a good
test of the rules before any foreign processor arrives.

### What was rejected

Letting a project define its own opcodes from a table in source. It would emit correct
bytes; the macro pack proves that is the easy part. It fails on three counts: operand shapes
are syntax-tree node kinds that every analyzer and editor feature keys off, so a table would
have to change the parse, which the design refuses for `.feature` and mid-file `.setcpu` for
the same reason; the facts that make the analysis worth having, what `sep` and `plp` and
`tcd` do, are a few dozen lines of code per processor and not rows; and a project's table is
checked by nobody, where every table nt65 ships is checked against the pinned ca65 in the
gate. The general case is served by the address space above and by macros over data below.
Built-in tables stay for processors where nt65 would be the best assembler available, which
is a short list.

## 2. Macros over data blocks

### What the port met

The template's driver already *is* a data block full of macro calls: `.setcpu "none"`
switches ca65's instruction set off and every SPC700 mnemonic is a macro emitting `.byte`.
nt65 has the same idiom with better machinery: nested `.data` members for routines, `@`
positions for branch targets, `*` for the current address, `.incbin` and `.align`, and
macros whose arguments are checked values rather than tokens. An SPC700 pack could be a
library module today, `.use spc700::*`, with none of the token-stream directives the design
removed. Three things stand in the way of making it read well.

### The changes

- **An operand parameter's expression in a data directive.** A pack dispatches on shape,
  `mov!(a, {#5})` against `mov!(a, {(ptr),y})`, the 6502 spelling of the SPC700's `[dp]+y`,
  with `.mode(src)` and a `one(a, x, y, ya, sp, psw)` parameter. But in the body an operand parameter stands only as a whole operand,
  and `.byteof` stands only where an operand may, so nothing lets the body write
  `.byte $e8, <the immediate's value>`. Without it a pack is one macro per addressing mode,
  which is blargg's lower layer with the readable layer lost. An `.exprof(p)`, or `.byteof(p,
  n)` accepted in a data directive, closes the gap, and `.addrsize` on the expression then
  answers direct page against absolute, which `.mode` alone does not.
- **Macros exempt from `mnemonic-name`.** The SPC700 shares `beq`, `bne`, `bra`, `inc`,
  `dec`, `and`, `eor`, `cmp`, `jmp`, `nop`, `push` and `pop` with the 6502 vocabulary. A
  macro call is always `name!(...)`, so no reader mistakes one for an instruction, but a
  pack in a shared module would warn at its declaration in every program that uses it, or
  mangle its names. The warning is right for `lda = 5` and noise for a macro.
- **Sibling members by bare name.** A foreign routine is a nested `.data` block, and a call
  from one to another spells the full path, `image::pently_update`, where the source it
  replaces wrote `pently_update`. Resolving a member among its siblings inside the enclosing
  block is the convenience a proc's cheap locals already have.

Two things to check rather than add: a branch macro writes `.byte target - (* + 1)` and
should `.assert` that the offset fits, which nt65 can evaluate as you type wherever no
`.align` lies between, since it knows every other length in a block; and `.sizeof` of a
block holding `.align` is unknown by design, so an uploader measures the image with
`.spanof`.

## 3. Typed parameter refinements

The parameter kinds, `expr`, `const`, `ident`, `operand`, `one`, `list` and `block`, are
already a type system. Three refinements map onto what the template does by hand, and they
matter most for a pack, which is nothing but macro signatures.

- **A range on `const`.** The template's `INST` macro takes eight numbers and follows them
  with seven `.assert` lines. As `lvol: const(0..15)` the signature says it, the error lands
  at the call naming the parameter, and signature help shows the limits. A condition that
  is not a range, attack must be odd, stays an `.assert`.
- **An enum as a kind.** `playPat` takes an instrument that upstream names with
  `.ident(.concat("PD_", ...))`; here it is an `.enum Instrument`, and `inst: Instrument`
  accepts only its members. If the argument may be the member's bare name, as `one(...)`
  takes words, the music reads `playPat!(0, 0, 24, bassguitar)`. Where the template ors a
  note and a duration together, an enum-typed parameter is the wrong shape and two
  parameters are the right one, which is itself useful for the kind to say.
- **Modes and sizes on `operand`.** `src: operand(imm, dp, dpx, abs, absx, indx, indy)`
  rejects an unsupported mode at the call with the accepted list, where today the body tests
  `.mode` and ends in `.error`; `dp: operand(zp)` says an operand must be a direct-page
  address. Both are checked where the macro is declared without expanding anything, which
  is the direction the design already leans.

Nothing further: structs as parameter types or a general expression type system would be a
language inside the language, and these three cover the template and the pack.

## 4. A set of banks as a `dbr` item

Upstream's own comment in `main` states the premise of a small LoROM: every program bank can
reach low RAM, the PPU ports and the DMA ports, so most routines never set B and need only
that it be one of those banks. nt65 can say B is one value, unknown, or unchanged. Ten
routines in the port, every `ppu_*` and `spc_*` routine and `draw_bg`, therefore declare
`dbr*`, inside which every operand check is silent and against which callers are checked
for nothing. The `ranges` table knows exactly which banks may reach `$2100`; nothing lets a
routine claim to run in one of them.

The item spells the set the way `mirrors` already does:

```nt65
.proc draw_bg: far, dp = 0, dbr = [$00..$3f, $80..$bf] -> a8, i16 {
```

At entry B lies in the set. An absolute operand passes when every bank in the set may reach
its segment or range, so `sta PPUADDR` is checked and a store into bank `$7e` WRAM is
caught. A call is checked when the caller's B is known and outside the set. `phk`, `plb`
narrows the set to one value as today, and two different sets meeting at a label merge to
unknown. This reuses the home-bank-and-mirrors model rather than adding to it.

## 5. Declaring a predeclared segment once

`ZEROPAGE`, `CODE`, `BSS` and `RODATA` are predeclared and cannot be declared again, so they
can never carry `dp`, `bank` or `mirrors`. A 65816 program that wants the direct-page and
bank checks on its standard segments must rename them, and the rename leaks: the linker
configuration changes, and because ca65 creates the standard segments in every object, the
vacated `RODATA` still exists while the new `RODATA0` does not, so ld65 warns until the
configuration says `optional=yes`. The port renamed four segments for no reason a reader
will guess.

The fix is to treat the predeclaration as a default. `.segment ZEROPAGE: zp, dp = 0` in a
file, or `"ZEROPAGE": { "size": "zp", "dp": 0 }` in the project file, is the program's one
declaration of it, allowed once, with a different size still an error. "Declared exactly
once" keeps its meaning: the built-in table is what stands when a program says nothing.

## Considered and not recommended

- **A call into a routine's interior.** `ppu_clear_nt` called into its own middle and fell
  through to the same place. Splitting it at `doonedma` with `.fallthrough` made the second entry a
  routine with its own signature. The design forbids the call on purpose, and the split is
  clearer than what it replaced.
- **`.incbin` measured before the build.** It forces asset conversion ahead of nt65, but
  `.sizeof` of the data is an nt65 constant, and the design puts shapes in constants
  deliberately. The build script pays a line for it.
- **The unused-constant warning.** It turned the header's loose constants into four exported
  enums, which is the better interface.
- **Scratch at address `$0000`.** `d:$0000` reaches it with D checked; nothing is missing.
- **`near` and `far` on routines that never return.** `reset_fastrom` and `main` are entered
  by `jml` and nt65 accepts either word once home banks are declared. Redundant, not wrong.
- **Project-defined opcode tables.** See the first section.

## Two notes that are not language changes

- **Checked imports in the example.** The driver's run address and entry point are constants
  by construction, `$0200` from the linker configuration and `$0300` from a page-aligned
  sample directory. `.import __SPCIMAGE_RUN__ = $0200` and `.import spc_entry = $0300` would
  give nt65 the values and make ld65 assert them at link time, with the existing mechanism.
  The load address and size stay opaque, correctly, since they are ROM layout.
- **A hint at a merge.** Where two widths meet at a loop head, the `a?` inlay hint lands on
  the line before the label, which reads as if the store forgot the width. Placing it on the
  label would say what happened.
