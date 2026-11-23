"""Adjust the UI canvas, inventory and vignette backdrop for the full-inner 0.33.0 build."""

import json
import struct
import sys
from pathlib import Path

# The game's UI resize routine calls a helper that returns 375 x 812 logical
# units before applying its safe-zone settings. Redirect only that call.
RESIZE_CALL = 0x2DD64
REFERENCE_SIZE = 0x1CE450
STUB_OFFSET = 0x43F7FC0  # Zero padding between __eh_frame and the end of __TEXT.
GENOA = Path("data/resource_packs/genoa")
BACKDROP = GENOA / "tappables/shared/backdrop/backdrop_vignette.geo.json"
INVENTORY = GENOA / "ui/genoa_inventory_common.json"
INVENTORY_ANIMATIONS = GENOA / "ui/animation/managers/inventory_screen.json"


def branch(source, target, opcode):
    return opcode | (((target - source) // 4) & 0x3FFFFFF)


def patch_canvas(data):
    call = branch(RESIZE_CALL, STUB_OFFSET, 0x94000000)
    original_call = branch(RESIZE_CALL, REFERENCE_SIZE, 0x94000000)
    # x20 is the resize routine's pointer to the physical width and height.
    # Use the original helper on narrow or portrait windows. On the 3x
    # inner display, width * 375 / (480 * 3) gives the logical width that
    # preserves the size of controls from a 480-point portrait window.
    stub = struct.pack(
        "<12I2f",
        0x2D400E82,  # ldp s2, s3, [x20]
        0x1E232040,  # fcmp s2, s3
        0x5400012D,  # b.le original
        0x52811308,  # mov w8, #2200
        0x1E220103,  # scvtf s3, w8
        0x1E232040,  # fcmp s2, s3
        0x540000AB,  # b.lt original
        0x1C0000A3,  # ldr s3, width_multiplier
        0x1C0000A1,  # ldr s1, reference_height
        0x1E230840,  # fmul s0, s2, s3
        0xD65F03C0,  # ret
        branch(STUB_OFFSET + 0x2C, REFERENCE_SIZE, 0x14000000),
        375 / (480 * 3),
        812.0,
    )
    (current_call,) = struct.unpack_from("<I", data, RESIZE_CALL)
    stub_range = slice(STUB_OFFSET, STUB_OFFSET + len(stub))
    if current_call == call and data[stub_range] == stub:
        return False
    if current_call != original_call or data[stub_range] != bytes(len(stub)):
        raise ValueError("unexpected 0.33.0 UI resize code or __TEXT padding")
    struct.pack_into("<I", data, RESIZE_CALL, call)
    data[stub_range] = stub
    return True


def patch_backdrop(source):
    data = json.loads(source)
    mesh = data["geometry.background.vignette"]["bones"][0]["poly_mesh"]
    # Keep the original center, UVs and winding. Mirror the outer half of
    # the texture on each side so the new quads meet the original edges.
    positions = mesh["positions"][:8]
    uvs = mesh["uvs"][:8]
    polys = mesh["polys"][:2]
    for left, right, left_u, right_u in [(-30, -15, 0.5, 0), (15, 30, 1, 0.5)]:
        corners = [(left, -20, left_u, 0), (right, -20, right_u, 0),
                   (right, 19.5, right_u, 1), (left, 19.5, left_u, 1)]
        for face, vertices in enumerate((corners, reversed(corners))):
            base = len(positions)
            for x, y, u, v in vertices:
                positions.append([x, y, 0])
                uvs.append([u, v])
            polys.append([[index, face, index] for index in range(base, base + 4)])

    fields = {"positions": positions, "uvs": uvs, "polys": polys}
    if all(mesh[key] == value for key, value in fields.items()):
        return source
    if [len(mesh[key]) for key in fields] != [8, 8, 2]:
        raise ValueError("unexpected 0.33.0 vignette mesh")
    mesh.update(fields)
    return json.dumps(data, indent=2) + "\n"


def replace_once(source, original, replacement):
    if source.count(original) == 1:
        return source.replace(original, replacement)
    if original in source or source.count(replacement) != 1:
        raise ValueError("unexpected 0.33.0 inventory layout")
    return source


def patch_inventory_counts(inventory, animations):
    # The inventory slot animation adds a translation to its Text layer
    # that can move counts past the slot after a wide launch. Let the
    # label's bottom-right anchor place it directly. Keep the hotbar mapping.
    layer_map = (
        '"animation_name": "inventory_slot",\n'
        '      "controller_name": "no_hover_button_group",\n\n'
        '      "control_layers": {\n'
    )
    animations = replace_once(animations, layer_map + '        "Text": "Stack Count",\n',
                              layer_map)
    inventory = replace_once(inventory, '"$stack_count_offset": [ -40, -34 ]',
                             '"$stack_count_offset": [ -4, 0 ]')
    return inventory, animations


def main(app):
    # build.sh has already checked the executable UUID in patch_duo.py.
    executable = app / "minecraftearthtf"
    files = [app / path for path in (BACKDROP, INVENTORY, INVENTORY_ANIMATIONS)]
    sources = [path.read_text() for path in files]
    backdrop, inventory, animations = sources
    binary = bytearray(executable.read_bytes())
    changed = patch_canvas(binary)
    updated = (patch_backdrop(backdrop), *patch_inventory_counts(inventory, animations))
    if changed:
        executable.write_bytes(binary)
    for path, source, contents in zip(files, sources, updated):
        if contents != source:
            path.write_text(contents)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
