# An Atari XEX

A small program for the Atari 800XL and XE, written for nt65 from the start, whose subject is the
way an Atari executable loads. A XEX is a series of load segments, each with the addresses it
loads at, and DOS reads them one at a time. A segment can be followed by an INITAD, which DOS
calls as soon as that segment is in, before it reads the rest, and the file ends with a RUNAD,
which DOS jumps to once everything is in. So code can run before the program has finished
loading, and later segments can load over memory that code used.

This program has four load segments:

| Loads at | Segment | Then |
|---|---|---|
| `$0600` | `SYSCHK` | INITAD `syscheck::run` checks that nothing is in the way of the program |
| `$A000` | `LOADER` | INITAD `loader::show` puts up a loading screen |
| `$2000` | `CODE`, `RODATA` | INITAD `loader::hide` turns the loading screen off |
| `$A000` | `SCREEN` | the program's screen, loaded over the loader |

and then RUNAD `main::start`, which shows the program's screen. Color bars in two bands are
drawn on the blank lines above and below the text by display list interrupts, one color a line,
and a hook in the vertical blank moves them on a line each frame and counts the frames. The
screen says how many frames the load took and shows the frame count.

If a cartridge such as BASIC is in, or DOS leaves too little memory, the system check writes why
on DOS's screen, in ATASCII, and goes back to DOS, which stops the load before anything else is
read.

## Building

With `nt65`, `ca65` and `ld65` on the path, in PowerShell:

```text
./build.ps1
```

`build/demo.xex` is the program, with a debug file beside it that points at the `.nt65` sources
and a label file. From a build of this repository, with the pinned cc65 in `.cache/cc65`, run
from the repository's root:

```text
examples/atari-xex/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

## Running it

[Atari800](https://atari800.github.io) runs it with the operating system built into it,
AltirraOS, so it needs no ROM images:

```text
atari800 -xl -xl-rev altirra -nobasic -run build/demo.xex
```

`-nobasic` takes BASIC out, as holding OPTION does when the machine starts. Without it, the
system check refuses to load the program.

## Testing it

`./test.ps1` runs the program in Atari800 and checks what it left in memory. It takes about a
second and a half, most of it Atari800 starting. Atari800's monitor stops the program where RUNAD
enters `main::start` and saves memory, then sets the operating system's first countdown timer to
a hundred frames, pointed at an `rts` that it breaks on. When the timer runs out, in the vertical
blank, it saves memory again. A second run, at the same time, loads the program with BASIC in and
saves memory once the system check has written why it refused. The checks are:

- **segments**: the XEX's load segments, INITADs and RUNAD are in the order and at the addresses
  `tests/expected.txt` has. The test reads them from the file itself, and names each from the
  debug file.
- **loaded**: at RUNAD, every byte the XEX loads is in memory, the later of two where they load
  at the same addresses. That is the program's screen where the loading screen was.
- **state**: at RUNAD, the loader's two frame counts are set, the screen is off and the loading
  screen's color is still in its shadow, which says both of the loader's routines ran. After a
  hundred frames, the display list, the display list interrupt and the vertical blank point at
  the program's own; both bands were drawn in the last frame; and the frame count and the screen's
  last row are as `tests/expected.txt` has them. So is the message on the refused run's screen.

Atari800 tries to download Atari's own ROMs when it starts on the ones built into it. The test
gives it a proxy that refuses every connection, so the download fails at once and the run needs
no network. SDL's dummy video driver runs it with no window. `./test.ps1 -Update` writes
`tests/expected.txt` from the run instead of checking it, which is then read and checked by hand.
`-Atari800` names Atari800, or the folder it is in, when it is not on the path.

## Organization of the program

- `src/syscheck.nt65`: the system check, in page 6, and its two messages in ATASCII.
- `src/loader.nt65`: the loading screen, its display list, and the two routines DOS calls into.
- `src/main.nt65`: the start, which RUNAD jumps to, and the main loop, which shows the frame
  count.
- `src/bars.nt65`: the display list interrupt, the hook in the vertical blank, and the bars'
  colors.
- `src/display.nt65`: the program's display list and its text, in the segment loaded over the
  loader.
- `src/atari.nt65`: ANTIC, GTIA and the operating system's shadows, vectors and routines, the
  control block of an I/O channel as a struct, and a macro that waits for the vertical blank.
- `xex.cfg`: the linker configuration. Each memory area is a load segment, in the order the
  file lists them, and its `FORMATS` block names the INITADs and the RUNAD.

## What it shows of nt65

- **Overlays that load over each other.** `LOADER` and `SCREEN` are memory areas at the same
  addresses, and nt65 reads the linker configuration, so it knows they are never in memory
  together. Code in the loader that reads, writes or jumps to anything in the program's screen,
  or the other way round, is an error, `segment-not-visible`, as it is between two banks of a
  cartridge.
- **Charmaps for the machine.** The screen's text is `screen(...)` from `nt65::atari`, which
  writes the codes ANTIC shows, and the system check's messages are `atascii(...)`, which is
  what CIO writes to the screen editor.
- **Interrupts that chain to the operating system.** The display list interrupt and the hook in
  the deferred vertical blank are `interrupt` routines. The hook keeps where VVBLKD pointed and
  leaves through it with `jmp (old)` and `.next ?`.
- **Records for the operating system's tables.** The I/O channels' control blocks are
  `.data channels: .type Iocb[8] = $0340`, so `cio::channels[0]::buffer` is channel 0's buffer
  address.
- **Data laid out by the compiler.** Each row of text is a `.byte[40]` padded with zeros, which
  are spaces in screen codes. The number of rows comes from the size of the text, and the bars'
  colors are worked out with a `.func` and `.repeat`.

### What nt65 does not see

nt65 knows where each segment is, not when it is there. Two things about this program are true
only because of the order the XEX loads in, and nothing checks them:

- `main` may name the loader's routines, since `MAIN` and `LOADER` are at different addresses,
  but by the time anything in `main` runs from RUNAD the loader has been overwritten. Only
  `loader::hide`, which runs as `MAIN`'s INITAD, may use the loader at that point, and nt65 does
  not know which routines run when.
- The loader's routines run before `MAIN` is loaded, so they must not call anything in it. nt65
  would let them.

Checking either would need nt65 to read the `FORMATS` block, know the order the memory areas are
written in, and follow what each INITAD and the RUNAD reach.
