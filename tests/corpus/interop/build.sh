#!/bin/sh
# Builds the release configuration without make: see the Makefile for the incremental build.
set -e
cd "$(dirname "$0")"
ROOT=../../..
# What the tools are called: the same build on either system, bar the suffix.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) X=.exe ;; *) X= ;; esac
NT=$ROOT/src/Norristown.Cli/bin/Debug/net10.0/nt65$X
B=$ROOT/.cache/cc65/bin
$NT build --config release "$@"
mkdir -p build/release/nt65 build/release/asm
for f in $(find build/release/gen -name '*.s'); do o=build/release/nt65/${f#build/release/gen/}; mkdir -p $(dirname $o); $B/ca65$X -g -o ${o%.s}.o $f; done
for f in asm/*.s; do $B/ca65$X --cpu 65c02 -g -I asm -o build/release/asm/$(basename ${f%.s}).o $f; done
$B/ld65$X -C link.cfg -o build/release/app.bin -m build/release/app.map --dbgfile build/release/app.dbg build/release/asm/*.o $(find build/release/nt65 -name '*.o')
$NT remap-dbg build/release/app.dbg
