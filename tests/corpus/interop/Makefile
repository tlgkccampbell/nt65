# Mixed cc65 project: nt65 -> ca65, hand-written ca65, and C compiled with cc65 against the
# header nt65 writes, linked with ld65. `make CONFIG=debug` builds the debug configuration.
ROOT   := ../../..
TOOLS  := $(ROOT)/.cache/cc65/bin
NT65   := $(ROOT)/src/Norristown.Cli/bin/Debug/net10.0/nt65.exe
CC65   := $(TOOLS)/cc65.exe
CA65   := $(TOOLS)/ca65.exe
LD65   := $(TOOLS)/ld65.exe

CONFIG ?= release
OBJ    := build/$(CONFIG)
# The configuration's `out` in nt65.json.
GEN    := $(OBJ)/gen

# An output is named after its module, wherever its source is.
MODULES  := apple2 apple2/hw cc65 mem reloc sound tables
NT65_S   := $(MODULES:%=$(GEN)/%.s)
NT65_O   := $(MODULES:%=$(OBJ)/nt65/%.o)
ASM_O    := $(patsubst asm/%.s,$(OBJ)/asm/%.o,$(wildcard asm/*.s))

all: $(OBJ)/app.bin $(OBJ)/c/main.o

# nt65 reads the whole program at once and writes every output that changed. What each output
# depends on is in the dependency file it writes, so an edit rebuilds only what it reaches.
$(NT65_S) $(GEN)/nt65.h: nt65.json
	$(NT65) build --config $(CONFIG) --depfile $(OBJ)/nt65.d --c-header $(GEN)/nt65.h

-include $(OBJ)/nt65.d

$(OBJ)/nt65/%.o: $(GEN)/%.s
	@mkdir -p $(@D)
	$(CA65) -g --create-dep $(@:.o=.d) -o $@ $<

$(OBJ)/asm/%.o: asm/%.s
	@mkdir -p $(@D)
	$(CA65) --cpu 65c02 -g -I asm --create-dep $(@:.o=.d) -o $@ $<

# The C is compiled against the generated header, and assembled; the cc65 runtime it would
# link with is not part of this test, so asm/main.s stands in for it in the binary.
$(OBJ)/c/main.o: c/main.c $(GEN)/nt65.h
	@mkdir -p $(@D)
	$(CC65) -t none -I $(GEN) -I $(ROOT)/.cache/cc65/include -o $(@:.o=.s) $<
	$(CA65) -I $(ROOT)/.cache/cc65/asminc -o $@ $(@:.o=.s)

# The nt65 output carries no debug directives, so the debug file is remapped from the line maps
# beside it afterwards. The hand-written and cc65 modules have no maps and are left alone.
$(OBJ)/app.bin: $(ASM_O) $(NT65_O) link.cfg
	$(LD65) -C link.cfg -o $@ -m $(OBJ)/app.map --dbgfile $(OBJ)/app.dbg $(ASM_O) $(NT65_O)
	$(NT65) remap-dbg $(OBJ)/app.dbg

-include $(NT65_O:.o=.d) $(ASM_O:.o=.d)

.PHONY: all
