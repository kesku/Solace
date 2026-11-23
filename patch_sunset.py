"""Stop Minecraft Earth iOS 0.33.0 from showing the "Time to Sunset" modal on every launch.

The modal is shown by 0x1015876e8, called from a screen controller's update. It asks
0x10161be8c whether to show: if a SunsetModalComponent entity exists, it runs a gate
(0x101b48d3c) and either returns 1 (show) or destroys the entity and returns 0 (skip).
NOP-ing the "gate said show" branch makes it always take the game's own skip path,
so the component is consumed and the modal is never built. The shutdown screen check
(patcher's DisableSunsetTimeCheck at 0x1129080) is unrelated and left alone.
"""
import struct, sys

SITE = 0x161BEC8                # cbz w0, 0x10161bf0c (return 1 -> show the modal)
ORIG = 0x34000220
NOP = 0xD503201F


def main(path):
    d = bytearray(open(path, "rb").read())
    cur, = struct.unpack_from("<I", d, SITE)
    if cur != NOP:
        assert cur == ORIG, (hex(SITE), hex(cur))
        struct.pack_into("<I", d, SITE, NOP)
        open(path, "wb").write(d)
    print("patched", path)


if __name__ == "__main__":
    main(sys.argv[1])
