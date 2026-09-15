; The DESIGN.md §13 output header, pasted by hand, with a few lines after it. It must
; assemble with no errors and no warnings on the pinned ca65. ";= N" is the byte count.
.setcpu "65816"
.smart -
.case +
.feature at_in_identifiers -, bracket_as_indirect -, c_comments -
.feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -
.feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -
.feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -
.feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -
.dbg file, "main.nt65", 591, 0

.segment "ZEROPAGE": zeropage
.dbg line, "main.nt65", 5
ptr:        .res 2                  ;= 2

.segment "CODE": absolute
.dbg line, "main.nt65", 18
main:
.dbg line, "main.nt65", 19
    lda #$20                        ;= 2
.dbg line, "main.nt65", 20
    sta (ptr),y                     ;= 2
.dbg line, "main.nt65", 21
    inc z:ptr+1                     ;= 2
.dbg line, "main.nt65", 22
    .byte $48, $45, $4C, $4C, $4F, $00  ;= 6
.dbg line, "main.nt65", 23
    jmp main                        ;= 3
