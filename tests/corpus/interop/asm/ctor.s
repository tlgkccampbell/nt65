; nt65 has no .constructor; ca65 needs the constructor defined in the same file, so this
; trampoline registers the nt65 routine.
.import init_lib
.constructor init_trampoline
.segment "ONCE"
init_trampoline:
        jmp init_lib
