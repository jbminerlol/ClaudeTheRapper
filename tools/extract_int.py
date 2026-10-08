"""Extract named resources from PaRappa's PS1 INT bundles.

The bundle directory starts at byte 0x10. Each entry is a 20-byte record:
little-endian resource size followed by a 16-byte, NUL-padded filename. Payloads
start at byte 0x2000 and are concatenated in directory order.
"""

from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path


HEADER_SIZE = 0x10
ENTRY_SIZE = 20
DATA_OFFSET = 0x2000


def extract_bundle(source: Path, destination: Path) -> list[dict[str, object]]:
    data = source.read_bytes()
    if len(data) < HEADER_SIZE or struct.unpack_from("<I", data)[0] != 1:
        raise ValueError(f"{source}: not a recognized INT bundle")

    count = struct.unpack_from("<I", data, 4)[0]
    table_end = HEADER_SIZE + count * ENTRY_SIZE
    if table_end > len(data) or DATA_OFFSET < table_end:
        raise ValueError(f"{source}: invalid entry count {count}")

    records: list[tuple[str, int]] = []
    for index in range(count):
        offset = HEADER_SIZE + index * ENTRY_SIZE
        size = struct.unpack_from("<I", data, offset)[0]
        raw_name = data[offset + 4 : offset + ENTRY_SIZE].split(b"\0", 1)[0]
        name = raw_name.decode("ascii")
        if not name or Path(name).name != name:
            raise ValueError(f"{source}: invalid resource name at entry {index}")
        records.append((name, size))

    payload_end = DATA_OFFSET + sum(size for _, size in records)
    if payload_end > len(data):
        raise ValueError(f"{source}: resource payload exceeds bundle length")

    destination.mkdir(parents=True, exist_ok=True)
    manifest: list[dict[str, object]] = []
    cursor = DATA_OFFSET
    for name, size in records:
        payload = data[cursor : cursor + size]
        output = destination / name
        output.write_bytes(payload)
        manifest.append(
            {"name": name, "size": size, "bundle_offset": cursor, "path": str(output)}
        )
        cursor += size
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="root directory containing the disc's extracted files")
    parser.add_argument("output", type=Path, help="directory to write extracted resources")
    args = parser.parse_args()

    all_entries: list[dict[str, object]] = []
    for source in sorted(args.assets.rglob("*.INT")):
        relative = source.relative_to(args.assets)
        target = args.output / relative.parent / source.stem
        entries = extract_bundle(source, target)
        all_entries.extend(entries)
        print(f"{relative}: extracted {len(entries)} resources")

    manifest_path = args.output / "manifest.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(all_entries, indent=2), encoding="utf-8")
    print(f"Wrote {len(all_entries)} resources to {manifest_path}")


if __name__ == "__main__":
    main()
