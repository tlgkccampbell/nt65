#include "nt65lib.h"
#include <stdio.h>

int main(void)
{
    unsigned r;
    memfill((void*)0x2000, 0x55, 300);
    r = memsum((const unsigned char*)0x2000, 10);
    install_blob();
    call_blob();
    printf("%u %u %u %u\n", r, sprite_count, sprites[1].tile, (unsigned)&buffer_size);
    cmd_table[CMD_QUIT]();
    beep();
    return 0;
}
