"""Point Minecraft Earth iOS 0.33.0's PlayFab calls at http://<titleid>.home.arpa:8080.

PlayFab URLs are built as "https://" + titleId + ".playfabapi.com" + path. The host
suffix literals are swapped in place (same length) and the three URL builders are
repointed from the shared "https://" literal to an existing "http://" literal.
"""
import struct, sys

BASE = 0x100000000
HTTP_LITERAL = 0x41774F6        # "http://\0"
HTTPS_LITERAL = 0x418472F       # "https://\0" (shared; left untouched)
LITERALS = [
    (0x41846EF, b".playfabapi.com\0", b".home.arpa:8080\0"),
    (0x4197819, b".playfabapi.com/\0", b".home.arpa:8080/\0"),
]
BUILDERS = [  # (adrp offset, add offset, register)
    (0x259E628, 0x259E62C, 1),
    (0x6D66A8, 0x6D66AC, 0),
    (0xB668A8, 0xB668AC, 0),
]


def adrp(pc, rd, target_page):
    imm = (target_page - (pc & ~0xFFF)) >> 12
    return 0x90000000 | ((imm & 3) << 29) | (((imm >> 2) & 0x7FFFF) << 5) | rd


def add_imm(rd, rn, imm12):
    return 0x91000000 | (imm12 << 10) | (rn << 5) | rd


def main(path):
    d = bytearray(open(path, "rb").read())
    assert d[HTTP_LITERAL - 1:HTTP_LITERAL + 8] == b"\0http://\0"
    assert d[HTTPS_LITERAL:HTTPS_LITERAL + 9] == b"https://\0"
    for off, old, new in LITERALS:
        cur = bytes(d[off:off + len(old)])
        if cur == new:
            continue
        assert cur == old, (hex(off), cur)
        d[off:off + len(new)] = new
    target = HTTP_LITERAL + BASE
    for a, b, rd in BUILDERS:
        want = (adrp(a + BASE, rd, target & ~0xFFF), add_imm(rd, rd, target & 0xFFF))
        orig = (adrp(a + BASE, rd, (HTTPS_LITERAL + BASE) & ~0xFFF), add_imm(rd, rd, (HTTPS_LITERAL + BASE) & 0xFFF))
        cur = struct.unpack_from("<II", d, a)
        if cur == want:
            continue
        assert cur == orig, (hex(a), [hex(x) for x in cur])
        struct.pack_into("<II", d, a, *want)
    open(path, "wb").write(d)
    print("patched", path)


if __name__ == "__main__":
    main(sys.argv[1])
