"""Post-process an asc.jar-built skua.swf: SWF version 28, UseNetwork flag (so a local
file:// copy may still use sockets), black background, zlib compression (CWS)."""
import struct, sys, zlib

src, dst = sys.argv[1], sys.argv[2]
d = open(src, 'rb').read()
if d[:3] == b'CWS':
    d = b'FWS' + d[3:8] + zlib.decompress(d[8:])
assert d[:3] == b'FWS', 'not an SWF'
nbits = d[8] >> 3
p = 8 + (5 + 4 * nbits + 7) // 8 + 4  # after RECT, frame rate, frame count
head, tags = bytearray(d[:p]), bytearray()
has_bg = False
while p < len(d):
    code_len = struct.unpack('<H', d[p:p + 2])[0]
    code, ln, hl = code_len >> 6, code_len & 63, 2
    if ln == 63:
        ln, hl = struct.unpack('<I', d[p + 2:p + 6])[0], 6
    tag = bytearray(d[p:p + hl + ln])
    if code == 69:  # FileAttributes: set UseNetwork
        tag[hl] |= 0x01
        tags += tag
        if not has_bg:
            tags += struct.pack('<H', (9 << 6) | 3) + b'\x00\x00\x00'  # SetBackgroundColor #000000
            has_bg = True
        p += hl + ln
        continue
    if code == 9:
        has_bg = True
    tags += tag
    p += hl + ln
body = head[8:] + tags
out = b'CWS' + bytes([28]) + struct.pack('<I', 8 + len(body)) + zlib.compress(bytes(body), 9)
open(dst, 'wb').write(out)
print(f'{dst}: {len(out)} bytes (uncompressed {8 + len(body)})')
