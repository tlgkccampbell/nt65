#!/bin/sh
set -e
cd "$(dirname "$0")"
ROOT=../../..
B=$ROOT/.cache/cc65/bin
# What the tools are called: the same build on either system, bar the suffix.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) X=.exe ;; *) X= ;; esac
$ROOT/src/Norristown.Cli/bin/Debug/net10.0/nt65$X build
for f in build/*.s; do $B/ca65$X -g -l ${f%.s}.lst $f -o ${f%.s}.o; done
$B/ld65$X -C c64.cfg -o build/game.prg -m build/game.map --dbgfile build/game.dbg build/*.o
ls -la build/game.prg
