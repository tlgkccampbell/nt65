# Hello, SNES

A 32 KiB LoROM whose backdrop colour you mix with the joypad: right and left brighten and
darken red, up and down green, A and B blue.

- `src/snes.nt65`: the registers, the joypad buttons as an enum, the `std` state signature and
  an `rgb15` function.
- `src/main.nt65`: reset, the main loop, the NMI, and the handshake that keeps the NMI from
  uploading a colour the main loop has only half mixed.
- `src/header.nt65`: the ROM header, a typed `.data` of a struct, and the vectors.
- `nt65.json` gives the segments' banks and the hardware ranges, which is what lets nt65 check
  every `jsr` and register access against D and B. `snes.cfg` places them for ld65.

With `nt65`, `ca65` and `ld65` on the path:

```text
sh build.sh
```

`build/hello.sfc` runs in any SNES emulator. `NT65`, `CA65` and `LD65` name the tools when they
are not on the path. From a build of this repository, with the pinned cc65 in `.cache/cc65`:

```text
NT65=../../src/Norristown.Cli/bin/Debug/net10.0/nt65.exe CA65=../../.cache/cc65/bin/ca65.exe LD65=../../.cache/cc65/bin/ld65.exe sh build.sh
```

The header's checksum is left unset, which emulators report and run anyway.
