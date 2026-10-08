"""Rip the SPU sound banks (VAB) from PaRappa's INT bundles to WAV.

Every COMPO/YCOMPO/ZCOMPO/COMMON bundle carries one VAB: header (programs, tone
attributes, VAG size table) followed directly by the VAG ADPCM bodies. The stage
overlays play rap voice entries with SsUtKeyOn(vab, prog, tone, note = tone + 24),
so each tone is written at the rate the game plays it:
    44100 * 2 ** ((tone + 24 - center + shift / 128) / 12)
Output: <out>/<folder>/<bundle>/p<prog>_t<tone>.wav plus index.json.
"""

from __future__ import annotations

import argparse
import json
import struct
import wave
from pathlib import Path

K0 = (0, 60, 115, 98, 122)
K1 = (0, 0, -52, -55, -60)


def decode_vag(data: bytes):
    out, h0, h1 = [], 0, 0
    for block in range(0, len(data) - 15, 16):
        header, flags = data[block], data[block + 1]
        shift, flt = header & 0x0F, min(header >> 4, 4)
        if shift > 12:
            shift = 9
        for i in range(28):
            byte = data[block + 2 + i // 2]
            nibble = (byte >> 4) if i & 1 else (byte & 0x0F)
            value = (nibble << 12) & 0xFFFF
            value = (value - 0x10000 if value & 0x8000 else value) >> shift
            value += (h0 * K0[flt] + h1 * K1[flt] + 32) >> 6
            value = max(-32768, min(32767, value))
            h1, h0 = h0, value
            out.append(value)
        if flags & 1 and block > 0:  # loop end / sample end
            break
    return out


def read_vab(data: bytes):
    o = data.find(b"pBAV")
    if o < 0:
        return None
    total, _, ps, ts, vs = struct.unpack_from("<IHHHH", data, o + 12)
    programs = [data[o + 32 + i * 16:o + 48 + i * 16] for i in range(128)]
    tone_base = o + 32 + 2048
    sizes = struct.unpack_from("<256H", data, tone_base + ps * 512)
    body = tone_base + ps * 512 + 512
    offsets, cursor = {}, body
    for v in range(1, vs + 1):
        offsets[v] = (cursor, sizes[v] << 3)
        cursor += sizes[v] << 3
    tones, slot = [], 0
    for prog, attr in enumerate(programs):
        if attr[0] == 0:
            continue
        for t in range(attr[0]):
            ta = data[tone_base + slot * 512 + t * 32:tone_base + slot * 512 + t * 32 + 32]
            vag = struct.unpack_from("<H", ta, 22)[0]
            if vag in offsets:
                tones.append({"prog": prog, "tone": t, "vol": ta[2], "center": ta[4], "shift": ta[5], "vag": vag})
        slot += 1
    return tones, offsets


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    index = []
    for bundle in sorted(args.assets.rglob("*.INT")):
        data = bundle.read_bytes()
        parsed = read_vab(data)
        if parsed is None:
            continue
        tones, offsets = parsed
        folder = args.out / bundle.parent.name / bundle.stem
        folder.mkdir(parents=True, exist_ok=True)
        for t in tones:
            start, size = offsets[t["vag"]]
            pcm = decode_vag(data[start:start + size])
            rate = int(round(44100 * 2 ** ((t["tone"] + 24 - t["center"] + t["shift"] / 128) / 12)))
            rate = max(4000, min(48000, rate))
            path = folder / f"p{t['prog']}_t{t['tone']:02d}.wav"
            with wave.open(str(path), "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(rate)
                w.writeframes(struct.pack(f"<{len(pcm)}h", *pcm))
            index.append({**t, "bundle": str(bundle.relative_to(args.assets)), "rate": rate,
                          "seconds": round(len(pcm) / rate, 3), "wav": str(path.relative_to(args.out))})
        print(f"{bundle.relative_to(args.assets)}: {len(tones)} tones")
    (args.out / "index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
