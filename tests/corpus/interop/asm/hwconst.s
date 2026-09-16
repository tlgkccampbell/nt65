; Re-export the include file's constants so nt65 can see them at link time.
.include "apple2.inc"
.export KBD, KBDSTRB, SPKR, TXTPAGE1
.exportzp WNDLFT
