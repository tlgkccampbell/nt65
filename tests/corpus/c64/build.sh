#!/bin/sh
set -e
cd "$(dirname "$0")"
ROOT=../../..
B=$ROOT/.cache/cc65/bin
$ROOT/src/Norristown.Cli/bin/Debug/net10.0/nt65.exe build
for f in build/src/*.s; do $B/ca65.exe -g -l ${f%.s}.lst $f -o ${f%.s}.o; done
$B/ld65.exe -C c64.cfg -o build/game.prg -m build/game.map --dbgfile build/game.dbg build/src/*.o
ls -la build/game.prg
