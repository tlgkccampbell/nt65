# A machine-language monitor

A small monitor in the manner of the ones the 8-bit machines shipped with, written for nt65
from the start rather than ported: it shows memory, changes it, disassembles it, and runs code
in it. The monitor itself knows nothing about the machine it runs on. Each machine is a
*platform* that supplies a handful of routines, and the Commodore 64 is the first.

```text
NT65 MONITOR
.:C000 A9 12 A5 34 B5 56 B6 78
.D C000 C004
,C000 A9 12    LDA #$12
,C002 A5 34    LDA $34
,C004 B5 56    LDA $56,X
.M C000 C007
:C000 A9 12 A5 34 B5 56 B6 78 ...4.V..
.X
```

## Commands

Numbers are hex, of one to four digits.

| Command | Does |
|---|---|
| `M [from [to]]` | shows memory, eight bytes to a line, from `from`, or from where the last `M` or `D` stopped; eight lines unless told where to stop |
| `:address byte...` | writes up to eight bytes from `address` |
| `F from to byte` | fills memory from `from` up to and including `to` |
| `D [from [to]]` | disassembles, from `from` or from where the last `M` or `D` stopped; sixteen instructions unless told where to stop |
| `G [address]` | calls `address`, or the PC shown by `R`, with the registers `R` shows; the code comes back to the monitor with `RTS` or `BRK` |
| `R` | shows the registers, as a `BRK` or the last `G` left them |
| `X` | leaves the monitor |

A line of `M` is itself a `:` command. On the C64, move the cursor up to one, change a byte and
press RETURN, and the byte is written; the characters beside the bytes are not read.

A command the monitor cannot read gets a `?`.

## Building

With `nt65`, `ca65` and `ld65` on the path, in PowerShell:

```text
./build.ps1
```

`build/c64/monitor.prg` is the C64 program, with a debug file beside it that points at the
`.nt65` sources and a label file for VICE's monitor. From a build of this repository, with the
pinned cc65 in `.cache/cc65`, run from the repository's root:

```text
examples/monitor/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

## Running and testing it in VICE

[VICE](https://vice-emu.sourceforge.io/) runs it; `x64sc` is its accurate C64:

```text
x64sc -autostart build/c64/monitor.prg -moncommands build/c64/monitor.lbl
```

The label file gives VICE's own monitor the program's names, so `d monitor__main` there
disassembles the monitor's entry.

`./test.ps1` runs the sessions in `tests/c64` in VICE, all at once, and checks the screen each
leaves; the four take about a second. A session is the screen as it should be. The lines that
start with the prompt, `.`, are what is typed; the rest is what the monitor answers. So a new
session is written by typing its commands, each on a line of its own after a `.`, and running
`./test.ps1 -Update -Session name` to fill in the answers, which are then read and checked by
hand. `-Vice` names VICE when `x64sc` is not on the path.

The script types a session with the `keybuf` command of VICE's monitor, as soon as the program
reaches `monitor::main`; saves screen memory when it reaches `platform::exit`; and leaves
through VICE's debug cartridge, with a `POKE 55295,0` typed after the `X` that ends every
session. A session that never reaches its `X` stops after 20 million cycles, and fails.

## Organization of the program

The monitor is a library in `lib/`, with no `nt65.json` of its own. Each platform is a project
of its own in a folder of its own, whose `nt65.json` names its files and the library's:

```json
"files": ["src/*.nt65", "../lib/*.nt65"]
```

So each platform chooses its own processor, segments and linker configuration, which is what a
platform differs by, and the library is analyzed as part of every program that uses it. In the
editor a library file shows as part of the first platform's program.

### The library

- `lib/monitor.nt65`: the command loop, the table of commands, the registers of the program
  being debugged, and what a platform must supply, in the comment at its top.
- `lib/parse.nt65`: the line the platform reads, and reading hex numbers from it.
- `lib/text.nt65`: writing text, hex and new lines, in terms of the platform's `putc`.
- `lib/memory.nt65`: `M`, `:` and `F`.
- `lib/disasm.nt65`: `D`.
- `lib/opcodes.nt65`: the 6502's instructions as data.
- `lib/run.nt65`: `G` and `R`.
- `lib/macros.nt65`: 16-bit moves and sums.

### The C64

- `c64/src/platform.nt65`: the `platform` module. The KERNAL's screen editor reads each line,
  which is why a line already on the screen can be entered again, and the KERNAL writes each
  character; `putc` is the KERNAL's `CHROUT` itself, declared with the registers it keeps. A
  `BRK` comes in through the KERNAL's BRK vector, below the registers the KERNAL pushed.
- `c64/src/stub.nt65`: the load address and the BASIC line `10 SYS2061`, whose digits are
  worked out from the address it names.
- `c64/c64.cfg`: the linker configuration, which puts the monitor where BASIC programs load
  and its zero page at `$FB` to `$FE`, which the KERNAL and BASIC leave free.

### Adding a platform

1. A folder with an `nt65.json` whose `files` are its own and `../lib/*.nt65`, giving its
   `cpu` and the segments its linker configuration places.
2. A module named `platform` exporting `LINE_LENGTH`, `putc`, `read_line` and `exit`, as the
   comment at the top of `lib/monitor.nt65` describes them, and starting the monitor: it gets
   the machine ready, sends a `BRK` to `monitor::broke` with the registers stored in
   `monitor::registers`, and jumps to `monitor::main`.
3. A linker configuration, an entry in `build.ps1`'s table of platforms, and sessions in
   `tests/<platform>` with an entry in `test.ps1`'s table, which says which emulator runs it and
   where its screen is. `test.ps1` reads a screen in the C64's screen codes; a platform whose
   screen holds something else needs its own reading.

A platform whose keyboard gives only one key at a time implements `read_line` as a loop over
it; the C64's screen editor already does that, and more.

## What it shows of nt65

- **A library shared by projects.** The platforms are projects whose files include the
  library's, and the library reaches the platform only through the names `platform` exports.
- **Data built as the program is compiled.** The opcode table is sixteen lines of text, as a
  data sheet lays it out; `.each` and `.repeat` walk it, and a `.func` packs each name into
  two bytes, so the 256-entry tables are written by nt65, not by a script.
- **A family of routines.** The disassembler has one routine for each addressing mode, all
  written once in a `.multiproc` over the modes' enum, and a table of them built with `.each`.
- **What a routine keeps, checked.** The platform's `putc` promises to keep X and Y, the
  library's routines that rely on that say so, and nt65 checks every promise through every
  call, including through `print`, which returns past the text written after its call.
- **Structure instead of convention.** Records for the registers, a list and an `.assert` that
  keep the commands and their letters in step, `noreturn` on the routines that never come back,
  and `.next` where a jump goes through a table.
