; Hand-written ca65 that nt65 calls. str_upper in src/str.nt65 imports it.
.setcpu "6502"
.export upcase

.segment "CODE"
; Returns the character in A as a capital when it is a lowercase ASCII letter, and unchanged
; otherwise. X and Y are left as they were.
upcase:
        cmp #'a'
        bcc @done
        cmp #'z' + 1
        bcs @done
        and #$DF
@done:  rts
