# Mixed cc65 project: nt65 -> ca65, hand-written ca65, (C via cc65 when available) -> ld65.
ROOT   := ../../..
TOOLS  := $(ROOT)/.cache/cc65/bin
NT65   := $(ROOT)/src/Norristown.Cli/bin/Debug/net10.0/nt65.exe
CA65   := $(TOOLS)/ca65.exe
LD65   := $(TOOLS)/ld65.exe

CFG    ?= release
DEFS_release :=
DEFS_debug   := -D DEBUG=1

GEN    := build/gen/nt65          # nt65.json "out" + the source dir it mirrors; must match nt65.json
GEN    := $(strip $(GEN))
OBJ    := build/$(CFG)

NT65_SRC := $(wildcard nt65/*.nt65)
NT65_S   := $(NT65_SRC:nt65/%.nt65=$(GEN)/%.s)
NT65_O   := $(NT65_SRC:nt65/%.nt65=$(OBJ)/nt65/%.o)
ASM_O    := $(patsubst asm/%.s,$(OBJ)/asm/%.o,$(wildcard asm/*.s))

# Dependencies nt65 cannot report: .incbin files (by hand), and the configuration.
NT65_EXTRA := data/glyphs.bin

all: $(OBJ)/app.bin

# nt65 is a whole-program step; it rewrites only .s files whose text changed, so a stamp
# carries the "ran since the inputs changed" timestamp. The .s outputs are shared by all
# configurations (no --out), so switching CFG forces a run through the config marker.
$(OBJ)/nt65.stamp: $(NT65_SRC) $(NT65_EXTRA) nt65.json build/.cfg-$(CFG) | $(OBJ)
	$(NT65) build $(DEFS_$(CFG))
	touch $@

build/.cfg-$(CFG):
	mkdir -p build && rm -f build/.cfg-* && touch $@

$(NT65_S): $(OBJ)/nt65.stamp ;

$(OBJ)/nt65/%.o: $(GEN)/%.s
	@mkdir -p $(@D)
	$(CA65) --cpu 65c02 -g -o $@ $<

# ca65 .incbin in tables.s is read at assemble time: same-size edits don't change tables.s
$(OBJ)/nt65/tables.o: data/glyphs.bin

$(OBJ)/asm/%.o: asm/%.s
	@mkdir -p $(@D)
	$(CA65) --cpu 65c02 -g -I asm --create-dep $(@:.o=.d) -o $@ $<

$(OBJ)/app.bin: $(ASM_O) $(NT65_O) link.cfg
	$(LD65) -C link.cfg -o $@ -m $(OBJ)/app.map --dbgfile $(OBJ)/app.dbg $(ASM_O) $(NT65_O)

$(OBJ):
	mkdir -p $@

-include $(ASM_O:.o=.d)

.PHONY: all
