# LoROM template

Damian Yerrick's [lorom-template](https://github.com/pinobatch/lorom-template), a minimal
working program for the Super Nintendo Entertainment System using the LoROM (mode $20) mapper,
ported to nt65. A character walks left and right across a static background while the sound
CPU plays a tune.

Concepts illustrated:

- internal header (with correct checksum) and init code
- setting up a static background
- loading data from multiple 32 KiB banks in a LoROM
- structure of a game loop
- automatic controller reading
- 8.8 fixed-point arithmetic
- acceleration-based character movement physics
- sprite drawing and animation, with horizontal flipping
- conversion of graphics to tile data in both 2-bit-per-pixel and 4-bit-per-pixel formats
- booting the S-SMP (SPC700 audio CPU)
- writing SPC700 code in 65C02 syntax, as nt65 macros after blargg's [SPC700 macro pack] for
  ca65
- use of SPC700 timers to control playback
- reading a sequence of pitches and converting them to frequencies
- compression of sampled sound to BRR format
- creating an SPC700 state file for SPC music players

Concepts not illustrated:

- a 512-byte header for obsolete floppy-based copiers
- S-CPU/S-SMP communication to play sound effects or change songs

[SPC700 macro pack]: http://forums.nesdev.com/viewtopic.php?p=121690#p121690

## Building

With `nt65`, `ca65` and `ld65` on the path, and Python 3 with Pillow as `py`, `python3` or
`python`, in PowerShell:

```text
./build.ps1
```

`build/lorom-template.sfc` runs in any SNES emulator, and `build/lorom-template.spc` in any
SPC player. `-Nt65`, `-Ca65`, `-Ld65` and `-Python` name the tools when they are not on the
path. From a build of this repository, with the pinned cc65 in `.cache/cc65`, run from the
repository's root:

```text
examples/lorom-template/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

Every build converts the pictures in `tilesets/` and the sounds in `audio/` into
`build/assets` with the Python tools upstream wrote, before nt65 runs, because nt65 measures
every file an `.incbin` names; and it writes the header's checksum into the linked image
last. Then it compares both images with `expected.sha256` and fails if either differs, since
every input is fixed; `-Update` accepts a change that is meant.

## Organization of the program

### The 65816 side, in nt65

- `src/snes.nt65`: register definitions for the S-CPU and S-PPU, the `io` signature set that
  says a routine runs in any bank that sees them, the joypad buttons as an
  enum, and the `NTXY` and `RGB` functions.
- `src/header.nt65`: the internal header as a struct, the vectors, and the interrupt stubs in
  bank $00.
- `src/init.nt65`: PPU and CPU I/O initialization code. It moves the direct page onto the
  register file twice, and reaches each register through it with `d:`.
- `src/main.nt65`: the NMI handler and the main loop.
- `src/bg.nt65`: background graphics setup.
- `src/player.nt65`: player sprite graphics setup and movement.
- `src/ppuclear.nt65`: useful subroutines for interacting with the S-PPU.
- `src/blarggapu.nt65`: sends the sound driver to the S-SMP.
- `nt65.json` links `lorom256k.cfg` and `spcfile/spc.cfg`, which declare the segments: each
  segment's bank is the bank it runs in there. The project adds what a linker configuration has
  no word for: the banks each memory area is mirrored in, the direct page, and the mirrored
  hardware ranges. That is what lets nt65 check every direct-page operand, every absolute
  operand and every call against D, B and the bank the code runs in.

### The SPC700 side, in nt65 data

nt65 has no SPC700 and needs none. The sound driver is data: each routine is a `.data` block of
calls to macros that emit SPC700 instructions, as upstream's was ca65 with no CPU and blargg's
macro pack. `nt65.json` puts the memory its segments run in, `SPCRAM` and `SPCZEROPAGE`, in the sound
CPU's address space, which holds data and no 65816 instructions. `spc_boot_apu` names where ld65
loaded the image, where it runs and how big it is with `.loadof`, `.runof` and `.spanof`, and
passes the driver's entry point on as a value, which nt65 refuses as a call target.

- `src/spc700.nt65`: the SPC700 instructions the driver uses, as macros in 65C02 spelling:
  `lda!({(ptr),y})` is `mov a, [ptr]+y`. Each parameter lists the addressing modes the SPC700
  has for it, and an address is reached through the direct page when its declaration puts it
  there.
- `src/spcimage.nt65`: the sound driver, S-Pently.
- `src/pently.nt65`: the music format: the conductor's commands, the notes and durations.
- `src/musicseq.nt65`: the music. The instruments, patterns and songs are enums, the macros
  that name them take their members, and the pattern and song tables are built from the enums
  in their order. `inst!` checks each instrument's volumes and envelope against their ranges.
- `spcfile/spcheader.s` and `spcfile/spc.cfg`: the header and linker configuration for the
  `.spc` state file, which the `.sfc` does not use.

### The tools

`tools/` holds the command-line programs upstream wrote, in Python, to convert asset data into
a form usable by the Super NES; `tools/convert.ps1` runs the converters, and `build.ps1` runs
it and then the checksum tool.

- `pilbmp2nes.py` converts bitmap images in PNG or BMP format into tile data usable by several
  classic video game consoles. It has several options to control the data format; use
  `pilbmp2nes.py --help` from the command prompt to see them all.
- `wav2brr.py` converts an uncompressed wave file to the BRR (bit rate reduction) format, a
  lossy audio codec based on ADPCM used by the S-DSP (the audio chip in the Super NES).
- `karplus.py` generates a plucked string sound, used for the bass sample.
- `makehat.py` generates a noise sample, from a fixed seed so that every build is the same.
- `fixchecksum.py` writes the internal header's checksum into the linked image.

## What the port changed

The linked image is byte for byte the upstream build, except where the following say so.

- Every routine declares the widths it is entered and left in, the direct page and the data
  bank, in place of `.smart`, `.a8`, `.i16` and the `seta8` family; nt65 checks the body
  against the declaration.
- `ppu_clear_nt` called into its own middle and fell through to the same place. The tail is
  its own routine, `doonedma`, that the first falls through into with `.fallthrough`, as
  `ppu_copy_oam` does into `ppu_copy`.
- `main` points the data bank at its own bank before the calls that load graphics rather than
  after them, so that every routine it calls runs with B known.
- `move_player` keeps its scratch byte in a declared direct-page variable rather than at
  address 0.
- `reset_fastrom` reads the header's map mode through its bank $80 address, which is the same
  byte.
- The unused `irqstub` is gone; the IRQ vector reaches `irq_handler` directly, as upstream.
- `USE_AUDIO`, `USE_PSEUDOHIRES` and `USE_INTERLACE` are settings, declared with `?=` and set
  with `nt65 build -D main::USE_AUDIO=0`.
- `makehat.py` seeds its random numbers, so the hi-hat is the same noise in every build rather
  than new noise each time.
- The sound driver reaches its direct-page variables through the direct page wherever it
  names them. Upstream's macro pack took a plain name as absolute unless `<` said otherwise,
  and the driver wrote `<` in some places and not others; the image is 80 bytes smaller for
  it. The two variables it declared and never used are gone.

## Greets

- [Super Nintendo Development Wiki] contributors
- Martin Korth (nocash) for [Fullsnes] doc and [NO$SNS] emulator
- Shay Green (blargg) for APU examples and SPC700 macro pack
- Jeremy Chadwick (koitsu) for more code organization tips

[Super Nintendo Development Wiki]: http://wiki.superfamicom.org/
[Fullsnes]: http://problemkaputt.de/fullsnes.htm
[NO$SNS]: http://problemkaputt.de/sns.htm

## Legal

The demo is distributed under the zlib License, reproduced below, and this port keeps it. It
is an altered version of the original, which is at the address above.

> Copyright 2017 Damian Yerrick
>
> This software is provided 'as-is', without any express or implied warranty. In no event
> will the authors be held liable for any damages arising from the use of this software.
>
> Permission is granted to anyone to use this software for any purpose, including commercial
> applications, and to alter it and redistribute it freely, subject to the following
> restrictions:
>
> 1. The origin of this software must not be misrepresented; you must not claim that you
>    wrote the original software. If you use this software in a product, an acknowledgment
>    in the product documentation would be appreciated but is not required.
>
> 2. Altered source versions must be plainly marked as such, and must not be misrepresented
>    as being the original software.
>
> 3. This notice may not be removed or altered from any source distribution.

A work's "source" form is the preferred form of a work for making modifications to it.
