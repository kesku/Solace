"""Opt the 0.33.0 executable into native Duo sizing before signing."""

import plistlib
import struct
import sys
from pathlib import Path

SEGMENT_64 = 0x19
UUID = 0x1B
BUILD_VERSION = 0x32
RPATH = 0x8000001C
LOAD_DYLIB = 0xC
OLD_SDK = 13 << 16
DUO_SDK = (27 << 16) | (1 << 8)
FRAMEWORKS_RPATH = b"@executable_path/Frameworks\0"
# The scene shim calls two virtual methods from this exact client build.
CLIENT_UUID = bytes.fromhex("34c962cf17e73d379c1d584d836c7315")
SCENE_LIBRARY = b"@executable_path/Frameworks/SolaceScene.dylib\0"
# The 0.33.0 map and HUD call through two pointers installed by SolaceScene.
# Reserve them immediately after __common, inside the writable segment.
HOOKS_OFFSET = 0x4ABF3C8
HOOK_STUB_OFFSET = 0x43F8000 - 0x100
HOOK_CALLS = (
    (0x18114B4, 0x27C7C38, 0xAA1403E1),  # MapCameraControlSystem: mov x1, x20 (projection)
    (0x2A9110, 0x13158C, 0xAA1303E1),    # ScreenView::resize: mov x1, x19 (screen)
)


def branch(source, target, opcode):
    distance = target - source
    if distance % 4 or not -(1 << 27) <= distance < (1 << 27):
        raise ValueError("unreachable ARM64 branch target")
    return opcode | ((distance // 4) & 0x3FFFFFF)


def patch_resize_calls(data):
    for index, (site, original, move_argument) in enumerate(HOOK_CALLS):
        stub = HOOK_STUB_OFFSET + 24 * index
        pointer = HOOKS_OFFSET + 8 * index
        pages = ((pointer >> 12) - (stub >> 12)) & 0x1FFFFF
        code = struct.pack(
            "<6I",
            0x90000010 | ((pages & 3) << 29) | ((pages >> 2) << 5),  # adrp x16, pointer
            0xF9400210 | (((pointer & 0xFFF) // 8) << 10),          # ldr x16, [x16, offset]
            0xB4000070,                                          # cbz x16, fallback
            move_argument,
            0xD61F0200,                                          # br x16
            branch(stub + 20, original, 0x14000000),              # fallback: b original
        )
        (current,) = struct.unpack_from("<I", data, site)
        target = branch(site, stub, 0x94000000)
        padding = data[stub:stub + len(code)]
        if current == target and padding == code:
            continue
        if current != branch(site, original, 0x94000000) or any(padding):
            raise ValueError("unexpected 0.33.0 resize call or __TEXT padding")
        struct.pack_into("<I", data, site, target)
        data[stub:stub + len(code)] = code


def patch_executable(path):
    data = bytearray(path.read_bytes())
    magic, cpu, _, kind, count, size = struct.unpack_from("<6I", data)
    assert (magic, cpu, kind) == (0xFEEDFACF, 0x100000C, 2), "expected an arm64 Mach-O executable"
    offset = 32
    end = offset + size
    commands = []
    rpaths = set()
    found_version = False
    found_uuid = False
    found_hook_storage = False
    first_section = None
    for _ in range(count):
        command, length = struct.unpack_from("<II", data, offset)
        assert length >= 8 and offset + length <= end, "invalid Mach-O load command"
        entry = data[offset:offset + length]
        offset += length
        if command == BUILD_VERSION:
            platform, minimum, sdk = struct.unpack_from("<III", entry, 8)
            assert not found_version and platform in (2, 7), "unexpected build platform"
            assert minimum == 12 << 16 and sdk in (OLD_SDK, DUO_SDK), "unexpected build version"
            struct.pack_into("<I", entry, 16, DUO_SDK)
            found_version = True
        if command == UUID:
            assert entry[8:] == CLIENT_UUID, "unexpected game executable UUID"
            found_uuid = True
        if command == RPATH:
            name_offset, = struct.unpack_from("<I", entry, 8)
            name = bytes(entry[name_offset:]).split(b"\0", 1)[0] + b"\0"
            if name in rpaths:
                # dyld rejects duplicate runpaths in executables linked with newer SDKs.
                assert name == FRAMEWORKS_RPATH, "unexpected duplicate runpath"
                continue
            rpaths.add(name)
        if command == SEGMENT_64 and entry[8:24].rstrip(b"\0") == b"__TEXT":
            first_section, = struct.unpack_from("<I", entry, 72 + 48)
            _, vmsize = struct.unpack_from("<2Q", entry, 24)
            assert HOOK_STUB_OFFSET + 24 * len(HOOK_CALLS) <= vmsize, "missing __TEXT padding"
        if command == SEGMENT_64 and entry[8:24].rstrip(b"\0") == b"__DATA":
            vmaddr, vmsize = struct.unpack_from("<2Q", entry, 24)
            nsects, = struct.unpack_from("<I", entry, 64)
            section = 72 + 80 * (nsects - 1)
            address, section_size = struct.unpack_from("<2Q", entry, section + 32)
            extra = 8 * len(HOOK_CALLS)
            end_address = 0x100000000 + HOOKS_OFFSET
            assert entry[section:section + 16].rstrip(b"\0") == b"__common", "unexpected __DATA layout"
            assert address + section_size in (end_address, end_address + extra), "unexpected __common size"
            assert end_address + extra <= vmaddr + vmsize, "missing writable hook storage"
            struct.pack_into("<Q", entry, section + 40, end_address + extra - address)
            found_hook_storage = True
        commands.append(entry)
    assert offset == end and found_version and found_uuid and found_hook_storage, "unexpected Mach-O header"
    length = (24 + len(SCENE_LIBRARY) + 7) & ~7
    load = struct.pack("<6I", LOAD_DYLIB, length, 24, 0, 0, 0)
    load = (load + SCENE_LIBRARY).ljust(length, b"\0")
    if load not in commands:
        commands.append(load)
    header = b"".join(commands)
    new_end = 32 + len(header)
    assert first_section is not None and max(end, new_end) <= first_section, "missing header padding"
    assert not any(data[end:new_end]), "header padding is not empty"
    data[32:max(end, new_end)] = header.ljust(max(end, new_end) - 32, b"\0")
    struct.pack_into("<II", data, 16, len(commands), len(header))
    patch_resize_calls(data)
    path.write_bytes(data)


def main(app, full_inner=False):
    path = app / "Info.plist"
    info = plistlib.loads(path.read_bytes())
    manifest = {
        "UIApplicationSupportsMultipleScenes": False,
        "UISceneConfigurations": {
            "UIWindowSceneSessionRoleApplication": [{
                "UISceneConfigurationName": "Solace",
                "UISceneDelegateClassName": "SolaceSceneDelegate",
            }],
        },
    }
    assert info.get("NSMainNibFile") == "MainWindow", "unexpected main nib"
    assert info.get("UIApplicationSceneManifest", manifest) == manifest, "unexpected scene manifest"
    info["UIApplicationSceneManifest"] = manifest
    # Without this, iOS 27 keeps the inner display at a portrait-width column. Filling it
    # works, but the game's HUD and menus scale to the width and come out oversized.
    if full_inner:
        info["UIRequiresFullScreenIgnoredStartingWithVersion"] = "27"
    else:
        info.pop("UIRequiresFullScreenIgnoredStartingWithVersion", None)

    patch_executable(app / "minecraftearthtf")
    path.write_bytes(plistlib.dumps(info, fmt=plistlib.FMT_BINARY))


if __name__ == "__main__":
    main(Path(sys.argv[1]), "--full-inner" in sys.argv[2:])
