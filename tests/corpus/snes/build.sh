#!/bin/sh
set -e
cd "$(dirname "$0")"
ROOT=../../..
B=$ROOT/.cache/cc65/bin
# Tool names differ between systems only by the .exe suffix on Windows.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) X=.exe ;; *) X= ;; esac
$ROOT/src/Norristown.Cli/bin/Debug/net10.0/nt65$X build
# An output is named after its module, so `snes::pad` is build/snes/pad.s.
for f in $(find build -name '*.s'); do $B/ca65$X -g -l ${f%.s}.lst $f -o ${f%.s}.o; done
$B/ld65$X -C snes.cfg -o build/game.sfc -m build/game.map --dbgfile build/game.dbg $(find build -name '*.o')
ls -la build/game.sfc
