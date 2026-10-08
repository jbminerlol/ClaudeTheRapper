"""Batch-convert extracted PaRappa VDF animations into Roblox FBX files.

Run with Blender (no UI needed):

    blender -b --factory-startup -P vdf_to_roblox_batch.py -- \
        --vdf-dir ../outputs/vdf-extracted --tmd-dir ../outputs/tmd-extracted --out ../outputs/roblox-fbx

For every base mesh in tmd-extracted/manifest.json that has VDF candidates,
this imports the mesh, adds each VDF as an animation, bakes them to bones,
saves a .blend for inspection, and writes one FBX per animation.
"""

import argparse
import importlib.util
import json
import sys
from pathlib import Path

import bpy

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("io_parappa_vdf", HERE / "io_parappa_vdf.py")
vdf = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = vdf
spec.loader.exec_module(vdf)


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--vdf-dir", type=Path, required=True)
    parser.add_argument("--tmd-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--only", nargs="*", default=[], help="limit to these base OBJ names (substring match)")
    parser.add_argument("--frames-per-key", type=int, default=4)
    parser.add_argument("--blend", choices=["LINEAR", "CONSTANT"], default="LINEAR")
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--scale", type=float, default=vdf.DEFAULT_SCALE)
    parser.add_argument("--no-blend-files", action="store_true")
    parser.add_argument("--animation-only", action="store_true", help="export bones + keyframes only, no mesh")
    return parser.parse_args(argv)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    return bpy.context


def main():
    args = parse_args()
    manifest = json.loads((args.tmd_dir / "manifest.json").read_text(encoding="utf-8"))
    entries = [entry for entry in manifest if entry.get("vdf_candidates_by_exact_vertex_span")]
    if args.only:
        entries = [e for e in entries if any(token.lower() in e["obj"].lower() for token in args.only)]

    summary = []
    for entry in entries:
        context = reset_scene()
        base_path = args.tmd_dir / entry["obj"]
        obj = vdf.import_base_obj(context, base_path, args.scale)
        context.view_layer.objects.active = obj
        imported = []
        for name in entry["vdf_candidates_by_exact_vertex_span"]:
            path = args.vdf_dir / name
            try:
                vdf.apply_vdf(context, obj, path, args.frames_per_key, args.blend, args.loop)
                imported.append(name)
            except (ValueError, OSError) as error:
                print(f"  skip {name}: {error}")
        if not imported:
            continue
        rig, _, baked, bones = vdf.bake_for_roblox(context, obj)
        obj.hide_set(True)
        out_dir = args.out / entry["bundle"] / base_path.stem
        written = vdf.export_roblox_fbx(context, rig, out_dir, animation_only=args.animation_only)
        if not args.no_blend_files:
            bpy.ops.wm.save_as_mainfile(filepath=str(out_dir / f"{base_path.stem}.blend"))
        print(f"{base_path.stem}: {len(imported)} VDF -> {bones} bones, {len(written)} FBX in {out_dir}")
        summary.append({"mesh": entry["obj"], "bones": bones + 1, "animations": [p.name for p in written],
                        "directory": str(out_dir)})

    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"Done: {len(summary)} mesh(es), {sum(len(s['animations']) for s in summary)} FBX file(s)")


main()
