/* Hand-maintained: nt65 cannot generate this. Keep in step with nt65/tables.nt65. */
struct sprite { unsigned char x, y; unsigned tile; const char* name; };
enum cmd { CMD_MOVE, CMD_FIRE, CMD_JUMP, CMD_QUIT };
void __fastcall__ memfill(void* p, unsigned char v, unsigned n);
unsigned __fastcall__ memsum(const unsigned char* p, unsigned char n);
void install_blob(void);
void call_blob(void);
void beep(void);
unsigned char read_key(void);
void __fastcall__ set_left_margin(unsigned char col);
extern const struct sprite sprites[];
extern const unsigned char sprite_count;
extern void (* const cmd_table[])(void);
extern const unsigned char message[];
extern const unsigned char glyphs[];
extern const unsigned glyphs_size;
extern unsigned char buffer[];
extern void buffer_size;    /* a constant: use (unsigned)&buffer_size */
