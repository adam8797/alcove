# Flip one byte in the middle of a zip entry's stored data, leaving zip headers intact.
import sys, zipfile, struct
path, entry = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(path) as z:
    info = z.getinfo(entry)
with open(path, 'r+b') as f:
    f.seek(info.header_offset)
    hdr = f.read(30)
    name_len, extra_len = struct.unpack('<HH', hdr[26:30])
    data_start = info.header_offset + 30 + name_len + extra_len
    pos = data_start + info.compress_size // 2
    f.seek(pos); b = f.read(1); f.seek(pos); f.write(bytes([b[0] ^ 0xFF]))
print(f"tampered {path}: flipped byte at offset {pos} inside '{entry}'")
