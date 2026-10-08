"""Convert PaRappa VDF vertex animation into Roblox R15 KeyframeSequences.

VDF animates individual vertices, but an R15 avatar only rotates its 15 body
parts. This tool approximates the motion:

  1. Splits each character mesh into groups of vertices that move together
     (spectral clustering on how constant the distances between them stay
     across every VDF frame of that character).
  2. Fits a rotation for each group per VDF record (Kabsch), relative to the
     character's most typical pose (used as the R15 standing pose).
  3. Maps groups to R15 parts by position (head on top, legs at the bottom,
     arms at the sides, torso in the middle) and writes the mapping to an
     editable JSON file next to the output. Re-running reuses that file, so
     you can correct a wrong guess and convert again.
  4. Writes one .rbxmx KeyframeSequence per VDF. In Studio: drag the file in,
     right-click the KeyframeSequence > Save to Roblox, then use the
     animation ID on any R15 character.

Squash-and-stretch cannot be represented by rotating rigid parts, so the
result is an approximation. Needs numpy; no Blender required.

    python vdf_to_r15.py --vdf-dir ../outputs/vdf-extracted --tmd-dir ../outputs/tmd-extracted --out ../outputs/roblox-r15
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from xml.sax.saxutils import escape

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "blender"))
from io_parappa_vdf import read_vdf, read_vertex_order_obj  # noqa: E402

PS1_FPS = 30
R15_HEIGHT_STUDS = 5.0

# R15 pose hierarchy (parent -> children), as KeyframeSequence Poses nest it.
R15_TREE = {
    "HumanoidRootPart": ["LowerTorso"],
    "LowerTorso": ["UpperTorso", "LeftUpperLeg", "RightUpperLeg"],
    "UpperTorso": ["Head", "LeftUpperArm", "RightUpperArm"],
    "LeftUpperArm": ["LeftLowerArm"], "LeftLowerArm": ["LeftHand"],
    "RightUpperArm": ["RightLowerArm"], "RightLowerArm": ["RightHand"],
    "LeftUpperLeg": ["LeftLowerLeg"], "LeftLowerLeg": ["LeftFoot"],
    "RightUpperLeg": ["RightLowerLeg"], "RightLowerLeg": ["RightFoot"],
}
R15_PARENT = {child: parent for parent, children in R15_TREE.items() for child in children}
R15_ORDER = ["HumanoidRootPart"]
for _part in R15_ORDER:
    R15_ORDER.extend(R15_TREE.get(_part, []))


def psx_to_roblox(points):
    """PlayStation (x right, y down, camera looks +z) -> Roblox (y up, character faces -Z)."""
    return np.asarray(points, float) * np.array([-1.0, -1.0, 1.0])


# ---------------------------------------------------------------------------
# Geometry
# ---------------------------------------------------------------------------

def kabsch(source, target):
    """Best rotation R with target ~ R @ source (both centred inside). Returns (R, ok)."""
    a = source - source.mean(0)
    b = target - target.mean(0)
    u, s, vt = np.linalg.svd(a.T @ b)
    if s[1] < 1e-6 * max(s[0], 1e-9):  # collinear points: rotation undefined
        return np.eye(3), False
    d = np.sign(np.linalg.det(vt.T @ u.T)) or 1.0
    return vt.T @ np.diag([1.0, 1.0, d]) @ u.T, True


def cluster_vertices(rest, frames, count, seed=0):
    """Spectral clustering of vertices that stay rigid relative to each other."""
    n = len(rest)
    count = max(1, min(count, n))
    height = np.ptp(rest[:, 1]) or 1.0
    distances = np.linalg.norm(frames[:, :, None] - frames[:, None], axis=-1)
    rigidity = distances.std(0) / height
    spatial = np.linalg.norm(rest[:, None] - rest[None], axis=-1) / height
    affinity = np.exp(-(rigidity / 0.03) ** 2) * np.exp(-(spatial / 0.25) ** 2)
    degree = affinity.sum(1)
    laplacian = np.eye(n) - affinity / np.sqrt(degree[:, None] * degree[None])
    _, vectors = np.linalg.eigh(laplacian)
    embed = vectors[:, :count]
    embed /= np.linalg.norm(embed, axis=1, keepdims=True) + 1e-12
    rng = np.random.default_rng(seed)
    best = None
    for _ in range(20):
        centres = embed[rng.choice(n, count, replace=False)]
        for _ in range(60):
            labels = ((embed[:, None] - centres[None]) ** 2).sum(-1).argmin(1)
            centres = np.array([embed[labels == j].mean(0) if (labels == j).any() else centres[j] for j in range(count)])
        score = ((embed - centres[labels]) ** 2).sum()
        if best is None or score < best[0]:
            best = (score, labels)
    labels = best[1]
    # Renumber clusters top-to-bottom so the mapping file reads naturally.
    order = sorted(set(labels.tolist()), key=lambda j: -rest[labels == j, 1].mean())
    remap = {old: new for new, old in enumerate(order)}
    return np.array([remap[j] for j in labels])


def guess_mapping(rest, labels):
    """Assign cluster ids to R15 parts from their position in the reference pose."""
    centre = rest.mean(0)
    height = np.ptp(rest[:, 1]) or 1.0
    info = {}
    for j in sorted(set(labels.tolist())):
        c = (rest[labels == j].mean(0) - centre) / height
        info[j] = c
    mapping = {}
    remaining = set(info)

    head = max(remaining, key=lambda j: info[j][1])
    mapping["Head"] = [head]
    remaining.discard(head)

    low = sorted((j for j in remaining if info[j][1] < -0.2), key=lambda j: info[j][1])
    for side, sign in (("Left", -1), ("Right", 1)):
        leg = [j for j in low if np.sign(info[j][0]) == sign]
        leg.sort(key=lambda j: -info[j][1])  # upper first
        if leg:
            mapping[f"{side}UpperLeg"] = [leg[0]]
            if len(leg) > 1:
                mapping[f"{side}LowerLeg"] = leg[1:]
        remaining -= set(leg)

    for side, sign in (("Left", -1), ("Right", 1)):
        arm = [j for j in remaining if np.sign(info[j][0]) == sign and abs(info[j][0]) > 0.08 and info[j][1] > -0.2]
        arm.sort(key=lambda j: abs(info[j][0]))  # nearest the shoulder first
        if arm:
            mapping[f"{side}UpperArm"] = [arm[0]]
            if len(arm) > 1:
                mapping[f"{side}LowerArm"] = arm[1:]
        remaining -= set(arm)

    # Anything near the top that is left belongs with the head (hair, hat, face).
    for j in [j for j in remaining if info[j][1] > 0.25]:
        mapping["Head"].append(j)
        remaining.discard(j)
    torso = sorted(remaining, key=lambda j: -info[j][1])
    if torso:
        mapping["UpperTorso"] = torso[: max(1, len(torso) // 2)]
        if len(torso) > 1:
            mapping["LowerTorso"] = torso[max(1, len(torso) // 2):]
    return mapping, {j: [round(float(x), 3) for x in c] for j, c in info.items()}


# ---------------------------------------------------------------------------
# Animation
# ---------------------------------------------------------------------------

def world_rotations(reference, pose, labels, mapping):
    """World rotation (relative to the reference pose) for every R15 part."""
    world = {"HumanoidRootPart": np.eye(3)}
    for part in R15_ORDER[1:]:
        clusters = mapping.get(part, [])
        indices = np.where(np.isin(labels, clusters))[0] if clusters else []
        rotation, ok = (kabsch(reference[indices], pose[indices]) if len(indices) >= 3 else (None, False))
        if part == "LowerTorso" and not ok:
            rotation, ok = kabsch(reference, pose)  # whole body
        world[part] = rotation if ok else world[R15_PARENT[part]]
    return world


def joint_transforms(world):
    """Motor6D.Transform rotations: each part relative to its parent.

    Assumes the standard R15 rig, whose joint C0/C1 carry no rotation.
    """
    return {part: world[R15_PARENT[part]].T @ world[part] for part in R15_ORDER[1:]}


def cframe_xml(rotation, translation=(0.0, 0.0, 0.0)):
    x, y, z = (float(v) for v in translation)
    r = [float(v) for v in np.asarray(rotation).reshape(-1)]
    cells = "".join(f"<R{i}{j}>{r[i * 3 + j]:.6f}</R{i}{j}>" for i in range(3) for j in range(3))
    return f'<CoordinateFrame name="CFrame"><X>{x:.6f}</X><Y>{y:.6f}</Y><Z>{z:.6f}</Z>{cells}</CoordinateFrame>'


class Referents:
    def __init__(self):
        self.count = 0

    def __call__(self):
        self.count += 1
        return f"RBX{self.count:08X}"


def pose_xml(part, transforms, root_offset, easing, ref, indent="    "):
    rotation = np.eye(3) if part == "HumanoidRootPart" else transforms[part]
    translation = root_offset if part == "LowerTorso" else (0.0, 0.0, 0.0)
    children = "".join(pose_xml(child, transforms, root_offset, easing, ref, indent + "  ") for child in R15_TREE.get(part, []))
    return (
        f'{indent}<Item class="Pose" referent="{ref()}"><Properties>'
        f"{cframe_xml(rotation, translation)}"
        f'<token name="EasingDirection">0</token><token name="EasingStyle">{easing}</token>'
        f'<string name="Name">{part}</string><float name="Weight">1</float>'
        f"</Properties>\n{children}{indent}</Item>\n"
    )


def keyframe_sequence_xml(name, keyframes, loop, easing):
    """keyframes: list of (time_seconds, transforms, root_offset)."""
    ref = Referents()
    body = []
    for time, transforms, root_offset in keyframes:
        body.append(
            f'  <Item class="Keyframe" referent="{ref()}"><Properties>'
            f'<string name="Name">Keyframe</string><float name="Time">{time:.6f}</float></Properties>\n'
            f"{pose_xml('HumanoidRootPart', transforms, root_offset, easing, ref)}  </Item>\n"
        )
    return (
        '<roblox xmlns:xmime="http://www.w3.org/2005/05/xmlmime" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xsi:noNamespaceSchemaLocation="http://www.roblox.com/roblox.xsd" version="4">\n'
        f'<Item class="KeyframeSequence" referent="{ref()}"><Properties>'
        f'<bool name="Loop">{"true" if loop else "false"}</bool>'
        f'<string name="Name">{escape(name)}</string>'
        '<token name="Priority">2</token></Properties>\n'
        + "".join(body)
        + "</Item>\n</roblox>\n"
    )


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------

def load_character(entry, tmd_dir, vdf_dir):
    vertices, _ = read_vertex_order_obj(tmd_dir / entry["obj"])
    base = psx_to_roblox(vertices)
    animations = []
    for name in entry["vdf_candidates_by_exact_vertex_span"]:
        poses = []
        for record in read_vdf(vdf_dir / name):
            pose = base.copy()
            end = record.first_vertex + len(record.deltas)
            pose[record.first_vertex:end] += psx_to_roblox(record.deltas)
            poses.append(pose)
        animations.append((Path(name).stem, np.stack(poses)))
    return base, animations


def convert_character(entry, args):
    base, animations = load_character(entry, args.tmd_dir, args.vdf_dir)
    frames = np.concatenate([poses for _, poses in animations])
    # The base mesh is not a natural pose; use the most typical frame instead.
    flat = frames.reshape(len(frames), -1)
    reference = frames[np.linalg.norm(flat[:, None] - flat[None], axis=-1).sum(1).argmin()]

    out_dir = args.out / entry["bundle"] / Path(entry["obj"]).stem
    out_dir.mkdir(parents=True, exist_ok=True)
    mapping_path = out_dir / "mapping.json"
    if mapping_path.exists() and not args.remap:
        saved = json.loads(mapping_path.read_text(encoding="utf-8"))
        labels = np.array(saved["vertex_clusters"])
        mapping = {part: list(ids) for part, ids in saved["parts"].items()}
    else:
        labels = cluster_vertices(reference, frames, args.clusters)
        mapping, centroids = guess_mapping(reference, labels)
        mapping_path.write_text(json.dumps({
            "note": "Edit 'parts' to move cluster ids between R15 parts, then re-run. "
                    "cluster_centroids are (x right, y up, z back) / character height in the reference pose. "
                    "Use --remap to recluster from scratch.",
            "parts": mapping,
            "cluster_centroids": {str(j): c for j, c in centroids.items()},
            "vertex_clusters": labels.tolist(),
        }, indent=2), encoding="utf-8")

    height = np.ptp(reference[:, 1]) or 1.0
    studs = R15_HEIGHT_STUDS / height
    easing = 1 if args.blend == "constant" else 0
    written = []
    for name, poses in animations:
        keyframes = []
        sequence = list(range(len(poses))) + ([0] if args.loop and len(poses) > 1 else [])
        for step, index in enumerate(sequence):
            pose = poses[index]
            transforms = joint_transforms(world_rotations(reference, pose, labels, mapping))
            offset = (pose.mean(0) - reference.mean(0)) * studs if args.root_motion else np.zeros(3)
            keyframes.append((step * args.frames_per_key / PS1_FPS, transforms, offset))
        path = out_dir / f"{name}.rbxmx"
        path.write_text(keyframe_sequence_xml(name, keyframes, args.loop, easing), encoding="utf-8")
        written.append(path)
    return out_dir, mapping, written


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--vdf-dir", type=Path, required=True)
    parser.add_argument("--tmd-dir", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--only", nargs="*", default=[], help="limit to base meshes whose name contains one of these")
    parser.add_argument("--clusters", type=int, default=8, help="number of rigid vertex groups per character")
    parser.add_argument("--frames-per-key", type=float, default=4, help="30 fps frames between VDF records")
    parser.add_argument("--blend", choices=["linear", "constant"], default="linear")
    parser.add_argument("--loop", action="store_true")
    parser.add_argument("--root-motion", action="store_true", help="also move the whole body (bounce/sway)")
    parser.add_argument("--remap", action="store_true", help="ignore existing mapping.json files")
    args = parser.parse_args()

    manifest = json.loads((args.tmd_dir / "manifest.json").read_text(encoding="utf-8"))
    entries = [e for e in manifest if e.get("vdf_candidates_by_exact_vertex_span")]
    if args.only:
        entries = [e for e in entries if any(token.lower() in e["obj"].lower() for token in args.only)]
    total = 0
    for entry in entries:
        out_dir, mapping, written = convert_character(entry, args)
        total += len(written)
        parts = ", ".join(f"{part}={ids}" for part, ids in mapping.items())
        print(f"{Path(entry['obj']).stem}: {len(written)} animation(s) -> {out_dir}\n    {parts}")
    print(f"Done: {total} KeyframeSequence file(s)")


if __name__ == "__main__":
    main()
