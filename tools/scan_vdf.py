"""Find and extract PlayStation vertex-difference animation streams from raw files.

This implements the record layout used by PSXPrev's VDF parser. INT bundle
entries are scanned as raw bytes because the game labels many resources as
TIM even when VDF streams follow the TIM table payloads.
"""

from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path


MIN_FRAMES = 4
MAX_FRAMES = 2000
MAX_OBJECT_ID = 10000
MAX_VERTICES = 10000
MIN_TOTAL_VERTICES = 20


def parse_candidate(data: bytes, offset: int):
    if offset + 4 > len(data):
        return None
    frame_count = struct.unpack_from("<I", data, offset)[0]
    if not MIN_FRAMES <= frame_count <= MAX_FRAMES:
        return None

    cursor = offset + 4
    total_vertices = 0
    frames = []
    for _ in range(frame_count):
        if cursor + 12 > len(data):
            return None
        object_id, vertex_offset, vertex_count = struct.unpack_from("<III", data, cursor)
        cursor += 12
        skipped_vertices = vertex_offset // 8
        if (
            object_id > MAX_OBJECT_ID
            or vertex_offset % 8
            or vertex_count == 0
            or skipped_vertices + vertex_count > MAX_VERTICES
            or cursor + vertex_count * 8 > len(data)
        ):
            return None
        frames.append(
            {
                "object_id": object_id,
                "vertex_offset": vertex_offset,
                "skipped_vertices": skipped_vertices,
                "vertex_count": vertex_count,
            }
        )
        total_vertices += vertex_count
        cursor += vertex_count * 8

    if total_vertices < MIN_TOTAL_VERTICES:
        return None
    return {
        "offset": offset,
        "end_offset": cursor,
        "size": cursor - offset,
        "frame_count": frame_count,
        "total_vertices": total_vertices,
        "frames": frames,
    }


def scan_file(path: Path):
    data = path.read_bytes()
    matches = []
    for offset in range(0, max(0, len(data) - 16), 4):
        item = parse_candidate(data, offset)
        if item is not None:
            matches.append(item)

    # A byte pattern inside a valid stream can also look like a short stream.
    # Keep only the outermost, non-overlapping candidates.
    roots = []
    for item in matches:
        if not any(other["offset"] < item["offset"] < other["end_offset"] for other in matches):
            roots.append(item)
    selected = []
    for item in roots:
        if selected and item["offset"] < selected[-1]["end_offset"]:
            continue
        selected.append(item)
    return data, selected, len(matches)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", type=Path, help="raw files or directories to scan")
    parser.add_argument("--out", type=Path, required=True, help="directory for extracted .VDF streams")
    args = parser.parse_args()

    files = set()
    for input_path in args.inputs:
        if input_path.is_file():
            files.add(input_path.resolve())
        elif input_path.is_dir():
            files.update(p.resolve() for p in input_path.rglob("*") if p.is_file())
    args.out.mkdir(parents=True, exist_ok=True)

    manifest = []
    for source in sorted(files):
        try:
            data, streams, raw_match_count = scan_file(source)
        except OSError:
            continue
        if not streams:
            continue
        try:
            label = source.stem.upper()
        except Exception:
            label = "RAW"
        for index, stream in enumerate(streams, start=1):
            filename = f"{label}_{index:03d}.VDF"
            output = args.out / filename
            output.write_bytes(data[stream["offset"] : stream["end_offset"]])
            manifest.append(
                {
                    "source": str(source),
                    "source_offset": stream["offset"],
                    "output": str(output),
                    "size": stream["size"],
                    "frame_count": stream["frame_count"],
                    "total_vertices": stream["total_vertices"],
                    "frames": stream["frames"],
                    "raw_candidate_count_in_source": raw_match_count,
                }
            )
        print(f"{source}: extracted {len(streams)} VDF stream(s)")

    manifest_path = args.out / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Wrote {len(manifest)} VDF stream(s) to {manifest_path}")


if __name__ == "__main__":
    main()
