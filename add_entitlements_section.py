"""Embed simulator entitlements as __TEXT,__entitlements (what Xcode does for Simulator builds).

The section header goes first in __TEXT and the plist goes in the header padding,
so no segment moves. Run before ad-hoc signing.
"""
import struct, sys

PAYLOAD_OFFSET = 0x2000


def main(binary, plist):
    d = bytearray(open(binary, "rb").read())
    ents = open(plist, "rb").read()
    ncmds, sizeofcmds = struct.unpack_from("<II", d, 16)
    o = 32
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from("<II", d, o)
        if cmd == 0x19 and d[o + 8:o + 24].rstrip(b"\0") == b"__TEXT":
            break
        o += cmdsize
    else:
        raise SystemExit("no __TEXT segment")
    vmaddr, _, fileoff, _ = struct.unpack_from("<QQQQ", d, o + 24)
    nsects, = struct.unpack_from("<I", d, o + 64)
    sects = o + 72
    names = [d[sects + i * 80:sects + i * 80 + 16].rstrip(b"\0") for i in range(nsects)]
    if b"__entitlements" in names:
        raise SystemExit("already has __entitlements")
    first_off, = struct.unpack_from("<I", d, sects + 48)
    end = 32 + sizeofcmds
    assert end + 80 <= PAYLOAD_OFFSET and PAYLOAD_OFFSET + len(ents) <= first_off
    assert not any(d[end:first_off]), "header padding is not empty"

    header = struct.pack("<16s16sQQIIIIIIII", b"__entitlements", b"__TEXT",
                         vmaddr + PAYLOAD_OFFSET - fileoff, len(ents), PAYLOAD_OFFSET,
                         0, 0, 0, 0, 0, 0, 0)
    d[sects:end + 80] = header + d[sects:end]
    struct.pack_into("<I", d, o + 4, cmdsize + 80)
    struct.pack_into("<I", d, o + 64, nsects + 1)
    struct.pack_into("<I", d, 20, sizeofcmds + 80)
    d[PAYLOAD_OFFSET:PAYLOAD_OFFSET + len(ents)] = ents
    open(binary, "wb").write(d)
    print(f"added __entitlements ({len(ents)} bytes) to {binary}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
