"""Extract PaRappa rhythm charts from the stage overlays (COMOD*.BIN).

Each stage overlay runs a setup routine that stores pointers to its chart data
into a block of executable globals at 0x800943c0..0x80094434. This tool finds
those stores in the raw MIPS code, tracks the register values feeding them
(lui/addiu/ori/lw), and reads the tables they point at:

  0x800943c4  bar list: 24-byte entry per bar (0x180 ticks), indexed bar + 1
                +0x00 u8   section / verse
                +0x08 u32  buttons used in the bar (PSY-Q pad bits)
                +0x0c 6 bytes per half: sound-set id, timing-window row, ..., u16 flags
  0x800943c8  bar count
  0x800943e4  expected player presses: u16 position (16th notes), u16 button id
  0x800943e8  press count

The output JSON holds numbers only (timing and button ids), read from the
user's own disc files.
"""

from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path

OVERLAY_BASE = 0x801C3870
GLOBALS = {
    0x43C4: "bars_ptr", 0x43C8: "bar_count", 0x43D8: "sound_table_ptr", 0x43DC: "sound_set_count",
    0x43E0: "window_table_ptr", 0x43E4: "notes_ptr", 0x43E8: "note_count",
}
BUTTON_IDS = {1: "triangle", 2: "circle", 3: "cross", 4: "square", 5: "L", 6: "L", 7: "R", 8: "R"}
BUTTON_BITS = {"triangle": 0x10, "circle": 0x20, "cross": 0x40, "square": 0x80, "L": 0x04, "R": 0x08}
SIXTEENTHS_PER_BAR = 16
TICKS_PER_BAR = 0x180


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def read_setup_globals(code: bytes):
    """Return {global_name: value} from the overlay's chart setup stores."""
    stores = [
        i for i in range(0, len(code) - 4, 4)
        if (u32(code, i) >> 26) == 0x2B and (u32(code, i) & 0xFFFF) == 0x43E4
    ]
    if not stores:
        raise ValueError("chart setup store (0x800943e4) not found")
    anchor = stores[0]
    regs = {0: 0}
    found = {}
    # The setup is straight-line code; replay a window around the anchor.
    for i in range(max(0, anchor - 0x400), min(len(code) - 4, anchor + 0x400), 4):
        w = u32(code, i)
        op, rs, rt = w >> 26, (w >> 21) & 31, (w >> 16) & 31
        imm = w & 0xFFFF
        simm = imm - 0x10000 if imm & 0x8000 else imm
        if op == 0x0F:
            regs[rt] = (imm << 16) & 0xFFFFFFFF
        elif op == 0x09 and regs.get(rs) is not None:
            regs[rt] = (regs[rs] + simm) & 0xFFFFFFFF
        elif op == 0x0D and regs.get(rs) is not None:
            regs[rt] = regs[rs] | imm
        elif op == 0x23 and regs.get(rs) is not None:
            address = (regs[rs] + simm) & 0xFFFFFFFF
            offset = address - OVERLAY_BASE
            regs[rt] = u32(code, offset) if 0 <= offset < len(code) - 3 else None
        elif op == 0x2B and regs.get(rs) == 0x80090000 and imm in GLOBALS and regs.get(rt) is not None:
            found[GLOBALS[imm]] = regs[rt]
        elif op in (0x0F, 0x09, 0x0D, 0x23) and rt in regs and rt != 0:
            regs.pop(rt, None)
    return found


def read_chart(path: Path):
    code = path.read_bytes()
    g = read_setup_globals(code)

    def table(address, size):
        offset = address - OVERLAY_BASE
        if not 0 <= offset <= len(code) - size:
            raise ValueError(f"table at {address:#x} lies outside {path.name}")
        return code[offset:offset + size]

    bar_data = table(g["bars_ptr"], g["bar_count"] * 24)
    bars = []
    for index in range(g["bar_count"]):
        entry = bar_data[index * 24:(index + 1) * 24]
        halves = [entry[12:18], entry[18:24]]
        bars.append({
            "section": entry[0],
            "flags": u32(entry, 4),
            "button_mask": u32(entry, 8),
            "halves": [{"sound_set": h[0], "window_row": h[1], "flags": struct.unpack_from("<H", h, 4)[0]} for h in halves],
        })

    note_data = table(g["notes_ptr"], g["note_count"] * 4)
    notes = []
    for index in range(g["note_count"]):
        position, button = struct.unpack_from("<HH", note_data, index * 4)
        notes.append({"sixteenth": position, "button": BUTTON_IDS.get(button, f"id{button}"), "button_id": button})
    return g, bars, notes


def validate(bars, notes):
    """Each press should be listed in its bar's button mask (bar list is indexed bar + 1)."""
    problems = []
    for note in notes:
        bar = note["sixteenth"] // SIXTEENTHS_PER_BAR + 1
        bit = BUTTON_BITS.get(note["button"])
        if bar >= len(bars):
            problems.append(f"note at {note['sixteenth']} is past the last bar")
        elif bit is None or not bars[bar]["button_mask"] & bit:
            problems.append(f"{note['button']} at 16th {note['sixteenth']} not in bar {bar} mask {bars[bar]['button_mask']:#x}")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("assets", type=Path, help="extracted disc folder (contains S, S2, ...)")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for path in sorted(args.assets.rglob("COMOD*.BIN")):
        try:
            g, bars, notes = read_chart(path)
        except (ValueError, KeyError) as error:
            print(f"{path.relative_to(args.assets)}: {error}")
            continue
        problems = validate(bars, notes)
        chart = {
            "source": str(path.relative_to(args.assets)),
            "ticks_per_bar": TICKS_PER_BAR,
            "sixteenths_per_bar": SIXTEENTHS_PER_BAR,
            "globals": {k: (f"{v:#010x}" if k.endswith("_ptr") else v) for k, v in g.items()},
            "notes": notes,
            "bars": bars,
            "validation_problems": problems,
        }
        out = args.out / f"{path.stem}.chart.json"
        out.write_text(json.dumps(chart, indent=1), encoding="utf-8")
        print(f"{path.relative_to(args.assets)}: {len(bars)} bars, {len(notes)} presses, "
              f"{len(problems)} mismatch(es) -> {out.name}")
        for problem in problems[:5]:
            print("   ", problem)


if __name__ == "__main__":
    main()
