"""Decode PaRappa XA (CD-XA ADPCM) stage music from the raw disc image to WAV.

The extracted STAGE*.XA files only kept 2048 bytes per sector, which drops the
XA sub-headers and part of every audio sector, so this reads the raw 2352-byte
sectors from the .BIN using the LBA in game/assets/manifest.json.

Each interleaved XA channel is written as its own WAV.
"""

from __future__ import annotations

import argparse
import json
import struct
import wave
from pathlib import Path

SECTOR = 2352
K0 = (0, 60, 115, 98, 122)
K1 = (0, 0, -52, -55, -60)


def clamp16(value):
    return -32768 if value < -32768 else 32767 if value > 32767 else value


class Channel:
    def __init__(self):
        self.stereo = None
        self.rate = None
        self.left = []
        self.right = []
        self.history = [[0, 0], [0, 0]]


def decode_group(group: bytes, channel: Channel):
    """Decode one 128-byte 4-bit sound group (8 sound units x 28 samples)."""
    for unit in range(8):
        header = group[4 + unit]
        shift = header & 0x0F
        shift = 9 if shift > 12 else shift
        flt = min((header >> 4) & 0x03, 4)
        side = unit & 1 if channel.stereo else 0
        h = channel.history[side]
        out = channel.right if (channel.stereo and side) else channel.left
        for sample in range(28):
            byte = group[16 + sample * 4 + unit // 2]
            nibble = (byte >> 4) if unit & 1 else (byte & 0x0F)
            value = (nibble << 12) & 0xFFFF
            value = value - 0x10000 if value & 0x8000 else value
            value = (value >> shift) + ((h[0] * K0[flt] + h[1] * K1[flt] + 32) >> 6)
            value = clamp16(value)
            h[1], h[0] = h[0], value
            out.append(value)


def decode_file(bin_path: Path, lba: int, sectors: int):
    channels: dict[int, Channel] = {}
    with bin_path.open("rb") as disc:
        disc.seek(lba * SECTOR)
        for _ in range(sectors):
            raw = disc.read(SECTOR)
            if len(raw) < SECTOR:
                break
            file_no, channel_no, submode, coding = raw[16:20]
            if not submode & 0x04:  # not an audio sector
                continue
            if submode & 0x01 and not submode & 0x04:
                continue
            ch = channels.setdefault(channel_no, Channel())
            ch.stereo = bool(coding & 0x01)
            ch.rate = 18900 if coding & 0x04 else 37800
            if (coding >> 4) & 0x03:
                raise ValueError("8-bit XA is not supported")
            data = raw[24:24 + 2304]
            for group in range(18):
                decode_group(data[group * 128:(group + 1) * 128], ch)
    return channels


def write_wav(path: Path, channel: Channel):
    with wave.open(str(path), "wb") as out:
        out.setnchannels(2 if channel.stereo else 1)
        out.setsampwidth(2)
        out.setframerate(channel.rate)
        if channel.stereo:
            frames = [s for pair in zip(channel.left, channel.right) for s in pair]
        else:
            frames = channel.left
        out.writeframes(struct.pack(f"<{len(frames)}h", *frames))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bin", type=Path, help="raw disc image (PARAPPA.BIN)")
    parser.add_argument("manifest", type=Path, help="game/assets/manifest.json with file LBAs")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--only", nargs="*", default=[], help="file names to decode, e.g. STAGE1.XA")
    args = parser.parse_args()
    files = json.loads(args.manifest.read_text(encoding="utf-8"))["files"]
    args.out.mkdir(parents=True, exist_ok=True)
    summary = []
    for entry in files:
        name = Path(entry["path"]).name
        if not name.upper().endswith(".XA") or (args.only and name.upper() not in [n.upper() for n in args.only]):
            continue
        sectors = (entry["size"] + 2047) // 2048
        channels = decode_file(args.bin, entry["lba"], sectors)
        for number, channel in sorted(channels.items()):
            seconds = len(channel.left) / channel.rate
            out = args.out / f"{Path(entry['path']).stem}_ch{number:02d}.wav"
            write_wav(out, channel)
            summary.append({"file": entry["path"], "channel": number, "rate": channel.rate,
                            "stereo": channel.stereo, "seconds": round(seconds, 3), "wav": out.name})
            print(f"{entry['path']} ch{number}: {seconds:7.2f}s {'stereo' if channel.stereo else 'mono'} {channel.rate} Hz -> {out.name}")
    (args.out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
