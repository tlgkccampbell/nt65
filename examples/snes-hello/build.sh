#!/bin/sh
# Builds hello.sfc with nt65, ca65 and ld65 from the path, or from NT65, CA65 and LD65.
set -e
cd "$(dirname "$0")"
NT65=${NT65:-nt65}
CA65=${CA65:-ca65}
LD65=${LD65:-ld65}
"$NT65" build
for f in build/*.s; do "$CA65" -g "$f" -o "${f%.s}.o"; done
"$LD65" -C snes.cfg -o build/hello.sfc --dbgfile build/hello.dbg build/*.o
