# Machine models

This document proposes a way for an nt65 program to describe the machine it runs on, so that the
analysis can check what depends on the machine as well as the CPU. It is written to be reviewed
by the owner and then handed to the agent that implements it. It covers the declarations, the
checks they enable, a sound analysis of memory built on them, what stays out, and the order of
work.

Two decisions were made with the owner on 2026-10-05, and the rest of the document follows from
them:

- A program opts in to machine knowledge by declaring it. A program that declares nothing is
  analyzed exactly as it is today.
- Users write their own models, including for homebrew machines. nt65 has no built-in machines.
  The models that come with nt65 are ordinary modules, written only with declarations any program
  may use.

Everything else here is a proposal. The open questions at the end are the decisions still needed.

## The problem

DESIGN.md, section "What a routine keeps", says why nt65 checks so little about memory:
"nothing about a location's behaviour is nt65's to know: it may be a mirror, a bank a mapper
switches, or a device register. The model covers what the CPU defines, and memory is defined by
the board." That is still right. It also means a whole class of common bugs goes unreported:

- a load from a write-only register, such as `lda PPUCTRL` on the NES, which reads open bus;
- a read-modify-write instruction on a write-only register, such as `inc` on a SID frequency;
- a store to ROM, or to an address where nothing is connected;
- a store to `$2008` that the reader does not recognize as `PPUCTRL`, because the PPU's
  registers repeat every 8 bytes;
- scratch or a `bss` segment placed where there is no RAM.

None of these needs the analysis to follow values through memory. Each needs one fact about the
board at one address. The programmer knows those facts, and nt65 has nowhere to put them except
`.mmio`, which records only that an address is a register.

## The idea

The board is the programmer's word, as memory already is. A machine model is a set of
declarations that state that word for the whole address space: what answers at each address,
whether it reads and writes like memory, and where it repeats. The checks are the same in every
program. They fire only at addresses the program has described, the same way the scratch check
fires only on `.scratch`.

This keeps the rule that nt65 has one rule everywhere. No check changes because a project names a
machine. A NES program and a homebrew program get different diagnostics for the same reason two
programs with different `.scratch` declarations do: they declare different things.

Because there are no built-in machines, the declarations have to describe hardware in general
terms. They never name a particular chip. A model for the NES and a model for a breadboard
computer use the same vocabulary, and the test corpus holds both to keep it that way.

### Why not the linker configs

The ld65 configs a project links already describe memory areas, and `MEMORY` takes
`type = ro | rw`. They are not enough. None of the example configs marks ROM, and the area a
config calls `ro` is often RAM: Applesoft's `BASROM` loads at `$0800`. A config also says nothing
about registers, mirrors or unconnected addresses. The configs stay where segments come from, and
the devices below say what the addresses are.

### Terms

- A **device** is whatever answers the CPU at a range of addresses. RAM, a ROM, a PPU and a 6522
  VIA are each a device. On a homebrew board a device is usually one chip select.
- A device's **kind** says how reads and writes behave there. It is given for reads and writes
  separately, because they differ on real boards.
- A device **repeats** when it answers at more addresses than it has. Incomplete address
  decoding does this, so a VIA with four register-select lines answers every 16 bytes across its
  whole range. Each repeat is a **mirror**.
- An address's **canonical address** is the first address in its device that answers the same.
  `$2008` on the NES is canonically `$2000`.

## Declaring devices

```nt65
.device RAM:  ram every $0800 = $0000..$1fff
.device PPU:  io every 8 = $2000..$3fff
.device APU:  io = $4000..$4017
.device TEST: none = $4018..$401f
.device PRG:  rom = $8000..$ffff
```

The form is `.device name: kind = range`, which reads like `.mmio name: element = address`. The
name appears in messages and hovers. Two devices may not overlap.

| Kind | Reads | Writes | Meaning |
|---|---|---|---|
| `ram` | memory | memory | A read returns what was last written. |
| `rom` | memory | ignored | A read returns a fixed byte, and a write changes nothing. |
| `io` | hardware | hardware | The hardware decides what a read returns and what a write does. |
| `none` | open bus | ignored | Nothing is connected. |

`, writes kind` gives writes a different kind from reads. It covers two common boards:

```nt65
.device BASIC: rom, writes ram = $a000..$bfff       ; C64: a store reaches the RAM underneath
.device BANKS: rom, writes io every 2 = $8000..$9fff ; MMC3: a store reaches the mapper
```

`every n` says the device repeats every n bytes across its range. It follows the kind it applies
to, so the MMC3 line repeats its writes every 2 bytes while its reads, which are ROM, do not
repeat. An address no device covers is unknown, as every address is today, and is never checked.
A model may therefore describe only part of a machine. An unconnected gap is declared with
`none`.

Devices describe the program's whole address space. Like a segment declaration, a device
declared in any module of the program applies to all of it, and it needs no `.export` or `.use`.
A shared library declares none, and each platform's project brings its own, which is the layout
the monitor example already has.

A device may stand under `.if`, so one model can follow a setting. A C64 program that banks out
BASIC can declare `BASIC` as `ram` when a setting says so. A program that switches the map while
it runs is described under later work.

On the 65816 a range is 24 bits wide, such as `$7e0000..$7fffff`. Whether devices should also
replace the project file's `ranges` is an open question.

## Register access

`.mmio` and struct fields may say which way a register goes:

```nt65
.mmio PPUCTRL:   write .byte = $2000
.mmio PPUSTATUS: read .byte = $2002
.mmio PPUDATA:   .byte = $2007          ; both ways

.struct Voice {
    freq:    write .word
    pulse:   write .word
    control: write .byte
    attack:  write .byte
    sustain: write .byte
}
```

A register with neither word goes both ways, so every `.mmio` written today keeps its meaning.
The access of a field applies wherever the struct is placed with `.mmio`. A field reached through
an index, such as `voices::freq,x`, is still that field.

A device's mirrors resolve to its registers. `sta $2008` is a store to `PPUCTRL`, and the hover
and the checks treat it so. The `constant-used-as-address` suggestion offers `PPUCTRL` for
`$2008` too.

## Chips as types

Homebrew machines are built from a few standard chips, such as the 6522 VIA, the 6551 ACIA and
the SID. A chip is a struct whose fields are its registers, with their access. A machine places
the chips it has:

```nt65
.use nt65::chip::w65c22 as via

.device VIA1: io every $10 = $6000..$7fff
.mmio   VIA1_REGS: .type via::W65c22 = $6000
```

nt65 ships a small library of chip types under `nt65::chip`, beside the charmaps and the macro
modules. A user can write a chip type for anything else in the same way.

## Example models

These are the three models the corpus holds. The homebrew one is the test that nothing in the
vocabulary is specific to a famous machine.

A breadboard 65C02 in the style of Ben Eater's kit has 16K of RAM, a VIA decoded across `$6000`
to `$7fff` and a 32K ROM:

```nt65
.device RAM:  ram = $0000..$3fff
.device GAP:  none = $4000..$5fff
.device VIA1: io every $10 = $6000..$7fff
.device ROM:  rom = $8000..$ffff
```

The NES with an MMC3 cartridge combines the console's fixed map with the cartridge's:

```nt65
.device RAM:    ram every $0800 = $0000..$1fff
.device PPU:    io every 8 = $2000..$3fff
.device APU:    io = $4000..$4017
.device TEST:   none = $4018..$401f
.device CART:   none = $4020..$5fff
.device PRGRAM: ram = $6000..$7fff
.device BANKS:  rom, writes io every 2 = $8000..$9fff   ; the bank select and bank data
.device MIRROR: rom, writes io every 2 = $a000..$bfff   ; the mirroring and RAM protection
.device IRQ:    rom, writes io every 2 = $c000..$dfff   ; the IRQ latch and reload
.device ACK:    rom, writes io every 2 = $e000..$ffff   ; the IRQ disable and enable
```

The base console and the mapper are two modules, `nt65::machine::nes` and
`nt65::machine::mmc3`, so a program on another mapper uses the first with a mapper module of its
own.

The C64's default map has ROM over RAM, and I/O chips that repeat:

```nt65
.device PORT:   io = $0000..$0001
.device RAM:    ram = $0002..$9fff
.device BASIC:  rom, writes ram = $a000..$bfff
.device HIRAM:  ram = $c000..$cfff
.device VIC:    io every $40 = $d000..$d3ff
.device SID:    io every $20 = $d400..$d7ff
.device COLOR:  io = $d800..$dbff                 ; only the low four bits read back
.device CIA1:   io every $10 = $dc00..$dcff
.device CIA2:   io every $10 = $dd00..$ddff
.device EXPAN:  io = $de00..$dfff
.device KERNAL: rom, writes ram = $e000..$ffff
```

## Checks

Each check is a single access at a single instruction, judged by the device and register at the
canonical address. None of them follows values through memory.

| Name | Severity | Reported when |
|---|---|---|
| `store-to-rom` | warning | An instruction writes where writes are ignored, meaning a `rom` device with no other kind for writes. |
| `access-to-nothing` | warning | An instruction reads or writes a `none` device. |
| `read-of-write-only` | warning | An instruction reads a `write` register. |
| `write-to-read-only` | warning | An instruction writes a `read` register. |
| `scratch-outside-ram` | error | `.scratch` is at an address, or in a segment, whose writes are not `ram`. |
| `segment-not-writable` | warning | A linked config runs a `bss`, `zp` or `rw` segment where writes are not `ram`. |

A read-modify-write instruction, such as `inc`, `asl` or `trb`, is both a read and a write, so
`inc` on a SID register is `read-of-write-only`. `bit` is a read.

An instruction's address is known when its operand is a constant, or a symbol with a fixed
address such as data found elsewhere, `.mmio` and placed scratch. A symbol in a segment is known
when every memory area the segment runs in lies in one device. An indexed operand is judged by
its base address. A store through a pointer is not seen. These gaps only miss reports, as the
scratch check's gaps do, and no gap can cause one.

## What it feeds that is not a check

- **Input sources.** The editor's highlight of where an instruction's inputs come from stops at a
  `.mmio` register. With devices, a load from anywhere in an `io` device stops it too, and a store
  and a load at two mirrors of one RAM address are grouped as one location.
- **Hover.** A constant address names its device, and its register when it has one, including at
  a mirror. `$2008` shows as `PPUCTRL`, mirrored from `$2000`.
- **The scratch check.** Scratch compares canonical addresses, so a `.data` declared at a mirror
  of scratch is the same bytes.

## Memory analysis

This is the second phase, and it builds on devices. It lets nt65 prove that a byte of RAM keeps
its value between a store and a later load. DESIGN.md does not follow values through memory
because nt65 cannot know what the board does or what else writes there. Devices answer the first
question. This phase answers the second.

### What it proves, and what it never claims

The analysis is sound. It over-approximates every write that could reach a byte, so when it says
a byte is unchanged, the byte is unchanged. Imprecision makes it prove less, and never makes it
claim something false.

That only works in one direction. An unresolved pointer store makes every byte of RAM possibly
written, so a warning on "may have changed" would fire everywhere. The checks in this phase
therefore take one of two shapes:

- A **proof** is used silently. A routine that saves a register in RAM and loads it back keeps
  that register, so its callers can rely on it.
- A **report** names a write that nt65 can see, such as a direct store to the same address on
  some path. A write nt65 cannot pin down, such as a store through a pointer, gives no report. The
  hover says why nothing was proved.

### The three facts

A sound answer needs three facts. Only one of them is ever declared, and only for code nt65
cannot see.

1. **Which addresses are RAM.** These come from devices. An address in a `ram` device is memory,
   and a mirror of it is the same byte. An address in any other device, or in none, is never
   memory to this analysis. A program that declares no devices therefore gets no proofs, which
   is the sound default.
2. **What code nt65 cannot see writes.** A routine with no body may declare `writes` in its
   signature, beside `reads` and `keeps`. This covers an extern proc, such as a ROM entry, an
   imported routine and the target of a `.next ?`. `writes none` says it writes no RAM. A routine
   with no body that has no `writes` may write any RAM, so leaving it out is sound and only costs
   precision.

   ```nt65
   .proc print_char = $ff00: reads a, keeps x, y, writes $00..$0f
   .proc delay = $ff10: reads a, writes none
   ```

   A machine model declares its ROM's routines once, so user code declares nothing. `writes` on a
   routine with a body is an error, because nt65 works the answer out from the body.
3. **What everything else writes.** This is inferred, program-wide, as a may-write set for each
   routine:
   - A direct store, or a read-modify-write instruction, writes its canonical address.
   - An indexed store writes the window its index can reach, which is at most 256 bytes from the
     base. A known range for the index narrows it.
   - A store through a pointer writes any RAM.
   - A call writes whatever its callee writes. A cycle of calls writes the union of what its
     routines write.
   - A store into a code segment is a write like any other, so self-modifying code needs nothing
     special.

### Interrupts

An interrupt handler can run between any two instructions. The program's handlers are already
known, since `scratch-in-handler` walks them, so the union of what they may write is added to
every instruction. That ignores `sei`, which is still sound, and it matches the NES, whose NMI
cannot be masked. Taking `sei` into account is a later refinement.

### Pointers

A pointer store that writes any RAM is common, because copy loops use `(ptr),y`. The first
version accepts that loss and measures it on the examples. If it costs too much, the next step is
to track the easy pattern, a pointer whose bytes the routine set from an address, as in
`lda #<buf` and `sta ptr`. Its stores then write the window from `buf`. A full value-set
analysis, which tracks the possible addresses of every pointer, is the fallback if the easy
pattern is not enough.

### What uses it

- **`.state saves` is checked.** Today nt65 trusts that the saved byte stays put until the
  restore. With this phase it proves that, and reports a direct store to the same byte on some
  path between them. A save it cannot prove is still trusted, and the hover says why.
- **Saves are inferred.** A routine that stores X to RAM and loads it back keeps X, without
  `.state saves` or `.state keeps`, when nt65 can prove it. That is the inference DESIGN.md does
  not make today, and the proof is what makes it safe.
- **Input sources in RAM become exact.** A memory source that is proved is drawn like a register
  source, and only an unproved one keeps the dashed look.

The scratch check does not change. It already follows only stores that name scratch, and its gaps
only miss warnings.

## What it does not do

- **Devices alone do not make RAM checkable.** A `ram` device says that a byte holds what was
  last stored, not that nothing else stores there. An interrupt handler, DMA or another processor
  can. Memory that hardware or another processor writes is declared `io`. Values are followed
  through `ram` only by the memory analysis, which proves what it claims.
- **It builds no machine into nt65.** No check names a chip or a machine. The shipped models can
  be read, copied and edited like any other module.
- **It changes nothing for a program without devices.** Every check above needs a declaration at
  the address it judges.

## Later work

- **Register protocols.** The NES's `PPUADDR` and `PPUSCROLL` share a write toggle that reading
  `PPUSTATUS` resets, and an odd number of writes between resets is a classic bug. Checking it
  needs path-sensitive register state like the flags, and a user-writable way to declare the
  toggle. That notation is real design work, and it should not be built as a NES special case.
- **Read side effects.** Some reads change the hardware: reading `PPUDATA` advances the address,
  and reading a 6522's port clears an interrupt flag. With that declared, nt65 could report the
  CPU's dummy reads, such as an indexed `lda` or `sta` that touches the wrong register when it
  crosses a page. The dummy reads are CPU-defined, and the side effect is the board's fact.
- **Bank windows.** A mapper changes what a `rom` window holds. Knowing which bank a call reaches
  is the same kind of tracking as the 65816 data bank, but it depends on each mapper's registers.
- **Maps that change while the program runs.** The C64's processor port and the Apple II's soft
  switches change what is mapped, and on the Apple II even a read does it. A model for those
  would track the map as state.
- **Timing.** Whether PPU writes fall within vblank needs cycle counts and a model of the frame.
  This is the furthest from the rest of the feature and may belong in another tool.
- **Other address spaces.** Devices describe the host CPU's space. An SPC700 image's space is
  left alone.

## Documentation

- DESIGN.md, section "Data": add `.device` beside `.mmio` and `.scratch`, the access words on
  `.mmio` and struct fields, and the grammar lines.
- DESIGN.md, section "What a routine keeps": the sentence "nothing about a location's behaviour
  is nt65's to know" must say that a program may now declare it for each address. When the memory
  analysis lands, the section must also say that `.state saves` is checked and that a save nt65
  can prove needs no `.state` at all.
- DESIGN.md, section "Signatures": `writes` in the table of signature items.
- DESIGN.md, section "What tooling gets": devices in hover and in the input sources.
- docs/GUIDE.md: a section on describing a machine, with the homebrew model as its example.
- The diagnostics catalogue: the six new names.

## Order of work

Each step ends with the full test gate green and a commit to `main`.

1. **`.device`** in the syntax, the binder and a program-wide device table. Report overlaps and
   bad ranges, and show devices in hover.
2. **Canonical addresses.** Resolve mirrors when looking up a register at a constant address, in
   hover and in `constant-used-as-address`.
3. **Register access** on `.mmio` and struct fields, with `read-of-write-only` and
   `write-to-read-only`.
4. **`store-to-rom` and `access-to-nothing`.**
5. **`scratch-outside-ram` and `segment-not-writable`**, and canonical addresses in the scratch
   check.
6. **The models.** Write `nt65::chip` types for the chips the examples use, and
   `nt65::machine::nes`, `mmc3` and `c64`. Add the homebrew corpus program. Move the mmc3,
   c64-demo and monitor examples' register modules onto them. The ported examples (msbasic,
   lorom-template and snrom-template) are left as they are.
7. **Input sources:** stop at `io` devices and group mirrors.
8. **Documentation.**

Steps 1 to 5 are tested with small fixtures. Each check has one fixture that reports and one that
does not.

The memory analysis follows as a second phase, with the same rule for each step:

9. **`writes` on routines with no body**, in the syntax and the signature model, with the error on
   a routine with a body.
10. **May-write sets** for each routine, with the handlers' union, and a hover row that shows
    them. Test them with flow fragments, as the scratch check is tested.
11. **The `.state saves` check**, proved or reported, with the reason in the hover.
12. **Measure** how many saves in the examples go unproved because of pointer stores. Decide from
    that whether to track pointers.
13. **Inferred saves**, then **exact memory sources** in the input-sources highlight.
14. **`writes` on the shipped models' ROM routines**, checked against each machine's
    documentation before it is written.

## Open questions

1. **The keyword.** `.region` would read well, but "region" already names a segment region line
   and `FlowRegion`. `.device` matches how a homebrew builder thinks of chip selects. Other
   candidates are `.map` and `.bus`.
2. **Repeats.** `every n` covers every board above. A decode mask, such as `decode $e00f`, would
   also cover decoding that skips address lines, at the cost of being harder to read. Start with
   `every` unless a real board needs the mask.
3. **A device's registers.** A device could carry its register type in one line, as
   `.device VIA1: io every $10 = $6000..$7fff, regs: .type W65c22`, in place of a separate
   `.mmio`. That is shorter, but it puts two declarations into one.
4. **The access words.** `read` and `write` before the element type are short. A field such as
   `freq: write .word` might read as an instruction to a newcomer. `in` and `out` are an
   alternative.
5. **Severity.** All the new warnings could be errors, since each is a fault on real hardware.
   Warnings let a project turn one off in `diagnostics` while a model is being written.
6. **The project file's `ranges`.** On the 65816, 24-bit devices can say which banks see the
   hardware registers. If they do, `ranges` could go, or stay as a shorthand.
7. **Module names.** `nt65::machine::nes` and `nt65::chip::w65c22` are proposals.
8. **What `writes` lists.** Address ranges are enough for ROM routines. Imported ca65 routines
   might want to name the symbols they write instead, which needs those symbols to resolve.
9. **An inferred save in the editor.** An inferred save changes what a routine keeps, which its
   callers see. Whether the `keeps` lens should mark a register kept through memory is open.

## Conventions

The repository's `CLAUDE.md` holds the C# style, member ordering, one type per file and the
comment voice; follow it. Files use LF line endings, and a CRLF `.cs` file fails the build. Add
the new tests to the fast suite and keep each one small.
