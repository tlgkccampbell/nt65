# A machine-language monitor

A small monitor in the manner of the ones the 8-bit machines shipped with, written for nt65
from the start rather than ported: it shows memory, changes it, disassembles it, and runs code
in it. The monitor itself knows nothing about the machine it runs on. Each machine is a
*platform* that supplies a handful of routines. There are two: the Commodore 64, with a 6502,
and the Apple IIGS, with a 65816, on which the monitor reaches all sixteen megabytes, shows the
65816's registers and disassembles all of its instructions.

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

On the IIGS, which enters the code and gets its registers back at sixteen bits:

```text
NT65 MONITOR
.:4010 18 FB C2 30 A9 34 12 A2
.:4018 78 56 A0 BC 9A 6B
.D 4010 401D
,004010 18          CLC
,004011 FB          XCE
,004012 C2 30       REP #$30
,004014 A9 34 12    LDA #$1234
,004017 A2 78 56    LDX #$5678
,00401A A0 BC 9A    LDY #$9ABC
,00401D 6B          RTL
.G 4010
  PC     AC   XR   YR   SP   DP   DB NVMXDIZC E
  004010 1234 5678 9ABC 01FB 0000 00 10000001 0
```

## Commands

Numbers are hex, of one to four digits. On the 65816 they are of one to six, and an address's
first two digits are its bank.

| Command | Does |
|---|---|
| `M [from [to]]` | shows memory, eight bytes to a line on the C64 and sixteen on the IIGS, from `from`, or from where the last `M` or `D` stopped; eight lines unless told where to stop |
| `:address byte...` | writes up to as many bytes from `address` as a line of `M` shows |
| `F from to byte` | fills memory from `from` up to and including `to` |
| `D [from [to]]` | disassembles, from `from` or from where the last `M` or `D` stopped; sixteen instructions unless told where to stop |
| `G [address]` | calls `address`, or the PC shown by `R`, with the registers `R` shows; the code comes back to the monitor with `RTS` or `BRK`, or on the 65816 with `RTL` or `BRK` |
| `R` | shows the registers, as a `BRK` or the last `G` left them |
| `X` | leaves the monitor |
| `?` | lists the commands |

A line of `M` is itself a `:` command. On the C64, move the cursor up to one, change a byte and
press RETURN, and the byte is written; the characters beside the bytes are not read.

On the 65816, `G` enters the code as `JSL` does, in the mode and at the widths `R` shows, with
D and B set from it. `D` makes each immediate as wide as its register is in `R`, and follows
each `REP` and `SEP` in the listing. `R` shows the registers at their full sixteen bits, the
program bank as the PC's, and `E`, which is 1 in emulation mode. That line is 47 columns wide,
which the IIGS's 80-column screen has room for.

A command the monitor cannot read gets a `?`.

## Building

With `nt65`, `ca65` and `ld65` on the path, in PowerShell:

```text
./build.ps1
```

`build/c64/monitor.prg` is the C64 program and `build/apple2gs/monitor.bin` the IIGS's, each
with a debug file beside it that points at the `.nt65` sources and a label file for an
emulator's debugger. `-Platform` builds only the platforms it names. From a build of this
repository, with the pinned cc65 in `.cache/cc65`, run from the repository's root:

```text
examples/monitor/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

## Running it

`./run.ps1 -Platform c64` or `./run.ps1 -Platform apple2gs` starts a platform's program in its
emulator, to try by hand. `-Vice` and `-Mame` name the emulators when `x64sc` and `mame` are not
on the path.

[VICE](https://vice-emu.sourceforge.io/) runs the C64's, in `x64sc`, its accurate C64, with the
label file that gives VICE's own monitor the program's names, so `d monitor__main` there
disassembles the monitor's entry.

[MAME](https://www.mamedev.org/) runs the IIGS's, with the `apple2gs` ROM set in the `roms`
folder beside MAME. The IIGS boots with no disk, and once the firmware says it found none,
`mame-session.lua` loads the file into memory as BRUN would and enters it. `X` then goes to
BASIC.SYSTEM, which is not there, so close MAME instead.

On a real IIGS, or to have `X` return to BASIC, the file is a binary that loads at `$2000`. Put
it on a ProDOS disk as a `BIN` file whose load address is `$2000`, with a disk image tool such
as CiderPress II, and run it from BASIC.SYSTEM with `BRUN MONITOR`. The platform reads the
registers of a `BRK` where ROM 3's firmware saves them, and has been tried only on ROM 3.

## Testing it

`./test.ps1` runs the sessions in `tests/<platform>` in each platform's emulator, all of them
at once, and checks the screen each leaves; the eleven take about three seconds. A session is
the screen as it should be. The lines that start with the prompt, `.`, are what is typed; the
rest is what the monitor answers. So a new session is written by typing its commands, each on a
line of its own after a `.`, and running `./test.ps1 -Update -Session name` to fill in the
answers, which are then read and checked by hand. `-Platform` runs only the platforms it names,
and `-Vice` and `-Mame` name the emulators when `x64sc` and `mame` are not on the path.

For the C64, the script types a session with the `keybuf` command of VICE's monitor, as soon as
the program reaches `monitor::main`; saves screen memory when it reaches `platform::exit`; and
leaves through VICE's debug cartridge, with a `POKE 55295,0` typed after the `X` that ends
every session. A session that never reaches its `X` stops after 20 million cycles, and fails.

For the IIGS, MAME runs `mame-session.lua`, which loads the file as it does for `run.ps1`.
MAME's debugger, with no window, stops the machine at `monitor::main`, where the script types
the session through MAME's natural keyboard, and at `platform::exit`, where it reads the
80-column screen and quits. A session that never reaches its `X` stops after a minute of the
IIGS's time, and fails.

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

- `lib/monitor.nt65`: the command loop, the table of commands and `?`, the registers of the
  program being debugged, which are the 65816's on the 65816, and what a platform must supply,
  in the comment at its top.
- `lib/parse.nt65`: the line the platform reads, and reading hex numbers from it.
- `lib/text.nt65`: writing text, hex and new lines, in terms of the platform's `putc`.
- `lib/memory.nt65`: `M`, `:` and `F`.
- `lib/disasm.nt65`: `D`.
- `lib/opcodes.nt65`: the 6502's instructions or the 65816's, as data.
- `lib/run.nt65`: `G` and `R`.
- `lib/macros.nt65`: moves, sums and comparisons of addresses, which are two bytes or three,
  and reading and writing memory through one.

### The C64

- `c64/src/platform.nt65`: the `platform` module. The KERNAL's screen editor reads each line,
  which is why a line already on the screen can be entered again, and the KERNAL writes each
  character; `putc` is the KERNAL's `CHROUT` itself, declared with the registers it keeps. A
  `BRK` comes in through the KERNAL's BRK vector, below the registers the KERNAL pushed. It
  re-exports `nt65::cbm::petscii`, which comes with nt65, as `text`, and switches the machine to
  the uppercase and graphics characters that charmap is written for.
- `c64/src/stub.nt65`: the load address and the BASIC line `10 SYS2061`, whose digits are
  worked out from the address it names.
- `c64/c64.cfg`: the linker configuration, which puts the monitor where BASIC programs load
  and its zero page at `$FB` to `$FE`, which the KERNAL and BASIC leave free.

### The Apple IIGS

- `apple2gs/src/platform.nt65`: the `platform` module. The monitor runs in emulation mode, with
  D and B at 0, which is what `running` declares and nt65 checks through every routine. The
  firmware reads each line with `GETLN1`, writes each character with `COUT`, and shows them on
  the 80-column screen. A `BRK` in either mode reaches the Apple II's BRK vector at `$3F0` after
  the firmware has saved every register at its full width in bank `$E1`, and the platform copies
  them from there. It re-exports `nt65::apple2::normal`, which comes with nt65, as `text`.
- `apple2gs/apple2gs.cfg`: the linker configuration, which loads the file at `$2000`, below
  BASIC.SYSTEM, and puts its zero page at `$FA` to `$FF`, which the firmware, Applesoft and
  ProDOS leave free.

### Adding a platform

1. A folder with an `nt65.json` whose `files` are its own and `../lib/*.nt65`, giving its
   `cpu` and the segments its linker configuration places.
2. A module named `platform` that exports `running`, `text`, `NEWLINE`, `COLUMNS`,
   `LINE_LENGTH`, `putc`, `read_line` and `exit`, as the comment at the top of
   `lib/monitor.nt65` describes them. The module also starts the monitor. It gets the machine
   ready, sends a `BRK` to `monitor::broke` with the registers stored in `monitor::registers`,
   and jumps to `monitor::main`.
3. A linker configuration, an entry in `build.ps1`'s table of platforms, and sessions in
   `tests/<platform>` with an entry in `test.ps1`'s table, which says which emulator runs it.
   `test.ps1` knows how to drive VICE and MAME, and how to read a C64's screen; MAME's script
   reads the IIGS's. Another machine in either emulator needs its own reading of the screen.

A platform whose keyboard gives only one key at a time implements `read_line` as a loop over
it; the C64's screen editor and the Apple II's `GETLN` already do that, and more.

## What it shows of nt65

- **A library shared by projects.** The platforms are projects whose files include the
  library's, and the library reaches the platform only through the names `platform` exports.
- **One library for two processors.** The library is compiled as 6502 code for the C64 and as
  65816 code for the IIGS. What differs is chosen with `.if .target(65816)`: the registers'
  record, the enum members of the 65816's addressing modes, its opcode rows, and how `G` enters
  code. A `.func` that describes a mode names a 65816 member only inside
  `.select(.target(65816), ...)`, which reads only the value it chooses. Every routine declares
  the platform's signature set `running`, so on the 65816 nt65 checks the mode and the widths
  through every call, and on the 6502 the same text is accepted as it stands.
- **Text in the machine's own characters.** Every character the library writes or compares is
  written as `text('A')` or `text("NT65 MONITOR")`, where `text` is the charmap `platform`
  re-exports under that name. Each program gets its machine's bytes from one source. A
  character that machine lacks is an error in that program's build, and an `.assert` states
  the one thing the library needs of the order of a machine's characters.
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
