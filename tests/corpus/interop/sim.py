# Minimal 65C02 simulator for this test (binary mode only). Loads build/app.bin.
import sys
m = bytearray(0x10000)
img = open('build/app.bin', 'rb').read()
m[0x0800:0x7800] = img[:0x7000]
m[0xF000:0x10000] = img[0x7000:]
A = X = Y = 0; S = 0xFF; C = Z = N = V = 0; I = 1; D = 0
pc = m[0xFFFC] | m[0xFFFD] << 8
def rd(a): return m[a & 0xFFFF]
def w16(a): return rd(a) | rd(a + 1) << 8
def nz(v):
    global Z, N
    v &= 0xFF; Z = int(v == 0); N = v >> 7; return v
def push(v):
    global S
    m[0x100 + S] = v & 0xFF; S = (S - 1) & 0xFF
def pull():
    global S
    S = (S + 1) & 0xFF; return m[0x100 + S]
def getP(): return N << 7 | V << 6 | 0x30 | D << 3 | I << 2 | Z << 1 | C
def setP(p):
    global N, V, D, I, Z, C
    N = p >> 7 & 1; V = p >> 6 & 1; D = p >> 3 & 1; I = p >> 2 & 1; Z = p >> 1 & 1; C = p & 1
def fetch():
    global pc
    v = rd(pc); pc += 1; return v
def fetch16():
    lo = fetch(); return lo | fetch() << 8
modes = {
    'imm': lambda: (pc - 0) if False else None,
}
def ea_of(mode):
    global pc
    if mode == 'imm': a = pc; pc += 1; return a
    if mode == 'zp': return fetch()
    if mode == 'zpx': return (fetch() + X) & 0xFF
    if mode == 'zpy': return (fetch() + Y) & 0xFF
    if mode == 'abs': return fetch16()
    if mode == 'abx': return (fetch16() + X) & 0xFFFF
    if mode == 'aby': return (fetch16() + Y) & 0xFFFF
    if mode == 'izx': z = (fetch() + X) & 0xFF; return rd(z) | rd((z + 1) & 0xFF) << 8
    if mode == 'izy': z = fetch(); return ((rd(z) | rd((z + 1) & 0xFF) << 8) + Y) & 0xFFFF
    if mode == 'izp': z = fetch(); return rd(z) | rd((z + 1) & 0xFF) << 8
    raise Exception(mode)
ops = {}
def grp(name, table):
    for mode, op in table.items(): ops[op] = (name, mode)
def std(b): return {'izx': b + 1, 'zp': b + 5, 'imm': b + 9, 'abs': b + 0xD, 'izy': b + 0x11, 'zpx': b + 0x15, 'aby': b + 0x19, 'abx': b + 0x1D, 'izp': b + 0x12}
for n, b in [('ORA', 0), ('AND', 0x20), ('EOR', 0x40), ('ADC', 0x60), ('LDA', 0xA0), ('CMP', 0xC0), ('SBC', 0xE0)]: grp(n, std(b))
t = std(0x80); del t['imm']; grp('STA', t)
grp('LDX', {'imm': 0xA2, 'zp': 0xA6, 'abs': 0xAE, 'zpy': 0xB6, 'aby': 0xBE})
grp('LDY', {'imm': 0xA0, 'zp': 0xA4, 'abs': 0xAC, 'zpx': 0xB4, 'abx': 0xBC})
grp('STX', {'zp': 0x86, 'abs': 0x8E, 'zpy': 0x96}); grp('STY', {'zp': 0x84, 'abs': 0x8C, 'zpx': 0x94})
grp('STZ', {'zp': 0x64, 'zpx': 0x74, 'abs': 0x9C, 'abx': 0x9E})
grp('CPX', {'imm': 0xE0, 'zp': 0xE4, 'abs': 0xEC}); grp('CPY', {'imm': 0xC0, 'zp': 0xC4, 'abs': 0xCC})
grp('BIT', {'zp': 0x24, 'abs': 0x2C, 'imm': 0x89, 'zpx': 0x34, 'abx': 0x3C})
grp('INC', {'zp': 0xE6, 'zpx': 0xF6, 'abs': 0xEE, 'abx': 0xFE}); grp('DEC', {'zp': 0xC6, 'zpx': 0xD6, 'abs': 0xCE, 'abx': 0xDE})
for n, b in [('ASL', 0), ('ROL', 0x20), ('LSR', 0x40), ('ROR', 0x60)]:
    grp(n, {'zp': b + 6, 'zpx': b + 0x16, 'abs': b + 0xE, 'abx': b + 0x1E}); ops[b + 0xA] = (n, 'acc')
imp = {0xE8: 'INX', 0xC8: 'INY', 0xCA: 'DEX', 0x88: 'DEY', 0xAA: 'TAX', 0x8A: 'TXA', 0xA8: 'TAY', 0x98: 'TYA', 0x9A: 'TXS', 0xBA: 'TSX',
       0x48: 'PHA', 0x68: 'PLA', 0xDA: 'PHX', 0xFA: 'PLX', 0x5A: 'PHY', 0x7A: 'PLY', 0x08: 'PHP', 0x28: 'PLP', 0x60: 'RTS', 0x18: 'CLC', 0x38: 'SEC',
       0x58: 'CLI', 0x78: 'SEI', 0xB8: 'CLV', 0xD8: 'CLD', 0xF8: 'SED', 0xEA: 'NOP', 0xDB: 'STP', 0x1A: 'INA', 0x3A: 'DEA'}
br = {0x10: ('N', 0), 0x30: ('N', 1), 0x50: ('V', 0), 0x70: ('V', 1), 0x90: ('C', 0), 0xB0: ('C', 1), 0xD0: ('Z', 0), 0xF0: ('Z', 1), 0x80: None}
steps = 0
while True:
    steps += 1
    if steps > 2_000_000: sys.exit('too many steps')
    at = pc; op = fetch()
    if op in br:
        off = fetch(); cond = br[op]
        if cond is None or globals()[cond[0]] == cond[1]: pc = (pc + (off - 256 if off > 127 else off)) & 0xFFFF
        continue
    if op == 0x20: t = fetch16(); r = pc - 1; push(r >> 8); push(r); pc = t; continue
    if op == 0x4C: pc = fetch16(); continue
    if op == 0x6C: pc = w16(fetch16()); continue
    if op == 0x7C: pc = w16((fetch16() + X) & 0xFFFF); continue
    if op in imp:
        n = imp[op]
        if n == 'STP': break
        if n == 'INX': X = nz(X + 1)
        elif n == 'INY': Y = nz(Y + 1)
        elif n == 'DEX': X = nz(X - 1)
        elif n == 'DEY': Y = nz(Y - 1)
        elif n == 'INA': A = nz(A + 1)
        elif n == 'DEA': A = nz(A - 1)
        elif n == 'TAX': X = nz(A)
        elif n == 'TXA': A = nz(X)
        elif n == 'TAY': Y = nz(A)
        elif n == 'TYA': A = nz(Y)
        elif n == 'TXS': S = X
        elif n == 'TSX': X = nz(S)
        elif n == 'PHA': push(A)
        elif n == 'PHX': push(X)
        elif n == 'PHY': push(Y)
        elif n == 'PHP': push(getP())
        elif n == 'PLA': A = nz(pull())
        elif n == 'PLX': X = nz(pull())
        elif n == 'PLY': Y = nz(pull())
        elif n == 'PLP': setP(pull())
        elif n == 'RTS': lo = pull(); hi = pull(); pc = ((hi << 8 | lo) + 1) & 0xFFFF
        elif n == 'CLC': C = 0
        elif n == 'SEC': C = 1
        elif n == 'CLI': I = 0
        elif n == 'SEI': I = 1
        elif n == 'CLV': V = 0
        elif n == 'CLD': D = 0
        elif n == 'SED': D = 1
        continue
    if op not in ops: sys.exit(f'unknown opcode ${op:02X} at ${at:04X}')
    n, mode = ops[op]
    if mode == 'acc':
        v = A
    else:
        ea = ea_of(mode); v = rd(ea)
    if n == 'LDA': A = nz(v)
    elif n == 'LDX': X = nz(v)
    elif n == 'LDY': Y = nz(v)
    elif n == 'STA': m[ea] = A
    elif n == 'STX': m[ea] = X
    elif n == 'STY': m[ea] = Y
    elif n == 'STZ': m[ea] = 0
    elif n == 'ORA': A = nz(A | v)
    elif n == 'AND': A = nz(A & v)
    elif n == 'EOR': A = nz(A ^ v)
    elif n == 'ADC': s = A + v + C; V = int(((A ^ s) & (v ^ s) & 0x80) != 0); C = s >> 8; A = nz(s)
    elif n == 'SBC': w = v ^ 0xFF; s = A + w + C; V = int(((A ^ s) & (w ^ s) & 0x80) != 0); C = s >> 8; A = nz(s)
    elif n in ('CMP', 'CPX', 'CPY'):
        r = {'CMP': A, 'CPX': X, 'CPY': Y}[n]; C = int(r >= v); nz(r - v)
    elif n == 'BIT':
        Z = int((A & v) == 0)
        if mode != 'imm': N = v >> 7; V = v >> 6 & 1
    elif n in ('INC', 'DEC'): m[ea] = nz(v + (1 if n == 'INC' else -1))
    else:
        if n == 'ASL': C = v >> 7; r = v << 1
        elif n == 'LSR': C = v & 1; r = v >> 1
        elif n == 'ROL': r = v << 1 | C; C = v >> 7
        elif n == 'ROR': r = v >> 1 | C << 7; C = v & 1
        r = nz(r)
        if mode == 'acc': A = r
        else: m[ea] = r
print(f'stopped at ${at:04X} after {steps} steps')
print('results $0200:', ' '.join(f'{b:02X}' for b in m[0x200:0x20D]))
print('fill: count of $55 in $2000-$21FF =', sum(1 for b in m[0x2000:0x2200] if b == 0x55), '; $212B =', hex(m[0x212B]), '$212C =', hex(m[0x212C]))
print('interior fill $2200:', ' '.join(f'{b:02X}' for b in m[0x2200:0x2205]))
print('blob at $0300:', ' '.join(f'{b:02X}' for b in m[0x300:0x308]), '; $0280 =', hex(m[0x280]))
print('WNDLFT $20 =', m[0x20], '; $0400 =', hex(m[0x400]))
