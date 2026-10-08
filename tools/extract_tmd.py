"""Scan PaRappa COMPO bundles for embedded PS1 TMD models and export OBJ meshes.

The game stores named TIM resources first, followed by unindexed binary data.
This tool only reads the user's extracted disc files and writes derived OBJ and
JSON files to a separate output directory. It keeps each TMD object's original
vertex order so a matching VDF stream can be applied without reindexing.
"""

from __future__ import annotations

import argparse
import json
import struct
from collections import Counter
from pathlib import Path


HEADER_BYTES = 0x10
ENTRY_BYTES = 20
DATA_OFFSET = 0x2000
MAX_OBJECTS = 16
MAX_ELEMENTS = 20_000
MAX_PACKET_WORDS = 64


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def resource_end(data: bytes) -> int:
    if len(data) < HEADER_BYTES or u32(data, 0) != 1:
        raise ValueError("not a recognized INT bundle")
    count = u32(data, 4)
    directory_end = HEADER_BYTES + count * ENTRY_BYTES
    if directory_end > len(data) or DATA_OFFSET < directory_end:
        raise ValueError(f"invalid INT directory with {count} entries")
    sizes = [u32(data, HEADER_BYTES + i * ENTRY_BYTES) for i in range(count)]
    end = DATA_OFFSET + sum(sizes)
    if end > len(data):
        raise ValueError("INT resource data exceeds the bundle")
    return end


def parse_tmd(data: bytes, offset: int):
    if offset + 12 > len(data) or u32(data, offset) != 0x41:
        return None
    flags, object_count = struct.unpack_from("<II", data, offset + 4)
    if flags != 0 or not 1 <= object_count <= MAX_OBJECTS:
        return None
    object_base = offset + 12
    if object_base + object_count * 28 > len(data):
        return None

    objects = []
    regions = [object_base + object_count * 28]
    total_primitives = 0
    total_supported_faces = 0
    for object_index in range(object_count):
        descriptor_at = object_base + object_index * 28
        vertex_top, vertex_count, normal_top, normal_count, primitive_top, primitive_count = struct.unpack_from(
            "<6I", data, descriptor_at
        )
        scale = struct.unpack_from("<i", data, descriptor_at + 24)[0]
        if (
            vertex_count > MAX_ELEMENTS
            or normal_count > MAX_ELEMENTS
            or primitive_count > MAX_ELEMENTS
            or abs(scale) > 32
        ):
            return None
        vertex_at = object_base + vertex_top
        normal_at = object_base + normal_top
        primitive_at = object_base + primitive_top
        if (
            vertex_at < object_base
            or normal_at < object_base
            or primitive_at < object_base
            or vertex_at + vertex_count * 8 > len(data)
            or normal_at + normal_count * 8 > len(data)
            or primitive_at > len(data)
        ):
            return None

        vertices = []
        for index in range(vertex_count):
            x, y, z = struct.unpack_from("<hhh", data, vertex_at + index * 8)
            vertices.append((x, y, z))

        cursor = primitive_at
        triangles = []
        mode_counts: Counter[str] = Counter()
        for _ in range(primitive_count):
            if cursor + 4 > len(data):
                return None
            output_words, input_words, primitive_flags, mode = data[cursor : cursor + 4]
            if not 1 <= input_words <= MAX_PACKET_WORDS or output_words > MAX_PACKET_WORDS:
                return None
            packet_end = cursor + 4 + input_words * 4
            primitive_code = (mode >> 5) & 7
            if packet_end > len(data) or primitive_code not in (1, 2, 3):
                return None
            total_primitives += 1
            mode_counts[f"0x{mode:02x}"] += 1

            # The VDF-linked PaRappa character meshes use unlit polygon packets.
            # Their vertex indices are the final aligned u16 values in the packet.
            if primitive_code == 1 and (primitive_flags & 1):
                corner_count = 4 if (mode & 8) else 3
                index_bytes = ((corner_count * 2 + 2 + 3) // 4) * 4
                index_at = packet_end - index_bytes
                indices = [u16(data, index_at + i * 2) for i in range(corner_count)]
                if all(index < vertex_count for index in indices):
                    for i in range(1, corner_count - 1):
                        triangles.append((indices[0], indices[i], indices[i + 1]))
                    total_supported_faces += corner_count - 2
            cursor = packet_end

        regions.extend(
            [
                vertex_at + vertex_count * 8,
                normal_at + normal_count * 8,
                cursor,
            ]
        )
        objects.append(
            {
                "object_index": object_index,
                "vertex_count": vertex_count,
                "primitive_count": primitive_count,
                "vertices": vertices,
                "triangles": triangles,
                "mode_counts": dict(mode_counts),
            }
        )

    if total_primitives == 0 or total_supported_faces == 0:
        return None
    return {
        "offset": offset,
        "end_offset": max(regions),
        "object_count": object_count,
        "objects": objects,
        "primitive_count": total_primitives,
        "supported_faces": total_supported_faces,
    }


def scan_bundle(data: bytes, start: int):
    candidates = []
    for offset in range((start + 3) & ~3, len(data) - 12, 4):
        candidate = parse_tmd(data, offset)
        if candidate is not None:
            candidates.append(candidate)

    selected = []
    for candidate in candidates:
        if any(
            previous["offset"] <= candidate["offset"] < previous["end_offset"]
            for previous in selected
        ):
            continue
        selected.append(candidate)
    return selected


def write_obj(path: Path, model: dict):
    with path.open("w", encoding="utf-8", newline="\n") as output:
        output.write("# Extracted from a local PlayStation TMD model; vertex order preserved.\n")
        output.write("# codex_psx_y_down_to_obj_y_up=true\n")
        for x, y, z in model["vertices"]:
            output.write(f"v {x} {-y} {z}\n")
        for a, b, c in model["triangles"]:
            output.write(f"f {a + 1} {b + 1} {c + 1}\n")


def vdf_vertex_spans(path: Path):
    if not path.exists():
        return {}
    try:
        vdfs = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    spans = {}
    for item in vdfs:
        source = Path(item["source"]).name.upper()
        by_object = {}
        for frame in item.get("frames", []):
            object_id = int(frame["object_id"])
            end = int(frame["skipped_vertices"]) + int(frame["vertex_count"])
            by_object[object_id] = max(by_object.get(object_id, 0), end)
        spans.setdefault(source, []).append(
            {
                "file": Path(item["output"]).name,
                "frame_count": int(item["frame_count"]),
                "object_vertex_spans": by_object,
            }
        )
    return spans


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="root folder with extracted disc files")
    parser.add_argument("--out", type=Path, required=True, help="output folder for OBJ models and manifest")
    parser.add_argument(
        "--bundles",
        nargs="+",
        default=["COMPO01", "COMPO02", "COMPO03", "COMPO05", "COMPO06", "COMPO07"],
        help="bundle names to scan (default: the six song stages)",
    )
    parser.add_argument("--vdf-manifest", type=Path, help="optional VDF extraction manifest for vertex-span links")
    args = parser.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    vdf_manifest = args.vdf_manifest or args.out.parent / "vdf-extracted" / "manifest.json"
    spans_by_bundle = vdf_vertex_spans(vdf_manifest)
    manifest = []
    for bundle_name in args.bundles:
        sources = sorted(args.assets.rglob(f"{bundle_name}.INT"))
        for source in sources:
            data = source.read_bytes()
            start = resource_end(data)
            models = scan_bundle(data, start)
            print(f"{source.relative_to(args.assets)}: {len(models)} candidate TMD meshes")
            bundle_vdfs = spans_by_bundle.get(source.name.upper(), [])
            for model_number, tmd in enumerate(models, start=1):
                for object_model in tmd["objects"]:
                    if object_model["vertex_count"] < 3 or not object_model["triangles"]:
                        continue
                    obj_name = (
                        f"{source.stem}_{model_number:03d}_off{tmd['offset']:08X}"
                        f"_obj{object_model['object_index']:02d}_v{object_model['vertex_count']}.obj"
                    )
                    output = args.out / obj_name
                    write_obj(output, object_model)
                    links = []
                    for vdf in bundle_vdfs:
                        span = vdf["object_vertex_spans"].get(object_model["object_index"])
                        if span == object_model["vertex_count"]:
                            links.append(vdf["file"])
                    manifest.append(
                        {
                            "bundle": source.stem,
                            "source": str(source.relative_to(args.assets)),
                            "tmd_offset": tmd["offset"],
                            "object_index": object_model["object_index"],
                            "vertex_count": object_model["vertex_count"],
                            "primitive_count": object_model["primitive_count"],
                            "triangle_count": len(object_model["triangles"]),
                            "mode_counts": object_model["mode_counts"],
                            "obj": obj_name,
                            "vdf_candidates_by_exact_vertex_span": links,
                            "association_basis": "matching object index and exact maximum VDF vertex span; runtime asset pointer not traced",
                        }
                    )

    manifest_path = args.out / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Exported {len(manifest)} mesh object(s) to {args.out}")
    print(f"Wrote {manifest_path}")


if __name__ == "__main__":
    main()
