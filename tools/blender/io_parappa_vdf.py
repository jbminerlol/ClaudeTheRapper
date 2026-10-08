"""PaRappa the Rapper VDF animation tools for Blender.

VDF is PSY-Q "vertex difference" MIME data: each record holds per-vertex
(x, y, z) deltas against a TMD base mesh (see ../VDF_FORMAT_NOTES.md).

What this add-on does:
  * File > Import > PaRappa base mesh (.obj): loads the OBJ files written by
    extract_tmd.py while keeping the original TMD vertex order (Blender's own
    OBJ importer may reorder or drop vertices, which breaks VDF indexing).
  * File > Import > PaRappa VDF animation (.vdf): adds one shape key per VDF
    record and an action that steps or blends through them. If no mesh is
    selected, the matching base mesh is found through tmd-extracted/manifest.json.
  * Object > PaRappa > Bake VDF to bones (Roblox): Roblox does not play
    shape-key animation, so this builds a rig with one bone per group of
    vertices that move together and bakes every VDF action into bone actions.
  * File > Export > PaRappa Roblox FBX: writes one .fbx per baked animation
    (Roblox reads a single animation per FBX file).

The parsing helpers at the top have no bpy dependency so they can be tested
outside Blender.
"""

from __future__ import annotations

bl_info = {
    "name": "PaRappa VDF Animation (import / Roblox FBX)",
    "author": "PaRappa PC port project",
    "version": (1, 0, 0),
    "blender": (4, 2, 0),
    "location": "File > Import / Export, Object > PaRappa",
    "description": "Import PlayStation VDF vertex-difference animation and bake it for Roblox FBX export",
    "category": "Import-Export",
}

import json
import os
import struct
from dataclasses import dataclass
from pathlib import Path

try:
    import bpy
    from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, StringProperty
    from bpy_extras.io_utils import ExportHelper, ImportHelper
    from mathutils import Vector
except ImportError:  # parsing helpers stay usable without Blender
    bpy = None


# One PlayStation unit is tiny; 1/256 makes the ~1400-unit-tall characters
# about 5.5 Blender units, close to a Roblox R15 character in studs.
DEFAULT_SCALE = 1.0 / 256.0
PS1_FPS = 30
PROP_VDF = "parappa_vdf"
PROP_SCALE = "parappa_scale"
PROP_BAKED = "parappa_roblox_bake"


# ---------------------------------------------------------------------------
# Format parsing (no bpy)
# ---------------------------------------------------------------------------

@dataclass
class VdfRecord:
    object_id: int
    first_vertex: int
    deltas: list  # [(dx, dy, dz)] in PlayStation coordinates


def parse_vdf(data: bytes) -> list[VdfRecord]:
    if len(data) < 4:
        raise ValueError("VDF is shorter than its record-count header")
    count = struct.unpack_from("<I", data, 0)[0]
    if not 1 <= count <= 100_000:
        raise ValueError(f"implausible VDF record count {count}")
    records = []
    cursor = 4
    for index in range(count):
        if cursor + 12 > len(data):
            raise ValueError(f"truncated VDF record header {index}")
        object_id, vertex_offset, vertex_count = struct.unpack_from("<III", data, cursor)
        cursor += 12
        if vertex_offset % 8:
            raise ValueError(f"VDF record {index} vertex offset is not 8-byte aligned")
        if not 1 <= vertex_count <= 100_000 or cursor + vertex_count * 8 > len(data):
            raise ValueError(f"VDF record {index} has invalid vertex data")
        deltas = [struct.unpack_from("<hhh", data, cursor + i * 8) for i in range(vertex_count)]
        cursor += vertex_count * 8
        records.append(VdfRecord(object_id, vertex_offset // 8, deltas))
    return records


def read_vdf(path) -> list[VdfRecord]:
    return parse_vdf(Path(path).read_bytes())


def read_vertex_order_obj(path):
    """Read an extract_tmd.py OBJ. Returns (psx_vertices, triangles)."""
    vertices, faces = [], []
    y_flipped = False
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("#") and "psx_y_down_to_obj_y_up=true" in line:
                y_flipped = True
            elif line.startswith("v "):
                x, y, z = (float(value) for value in line.split()[1:4])
                vertices.append((x, y, z))
            elif line.startswith("f "):
                faces.append(tuple(int(token.split("/")[0]) - 1 for token in line.split()[1:]))
    if y_flipped:
        vertices = [(x, -y, z) for x, y, z in vertices]
    return vertices, faces


def psx_to_blender(vector, scale):
    """PlayStation (x right, y down, z into screen) -> Blender Z-up. Proper rotation, no mirroring."""
    x, y, z = vector
    return (x * scale, z * scale, -y * scale)


def find_base_mesh(vdf_path, tmd_dir=None):
    """Look up the base OBJ for a VDF using tmd-extracted/manifest.json."""
    vdf_path = Path(vdf_path)
    candidates = [Path(tmd_dir)] if tmd_dir else []
    candidates += [vdf_path.parent.parent / "tmd-extracted", vdf_path.parent / "tmd-extracted"]
    for directory in candidates:
        manifest = directory / "manifest.json"
        if not manifest.is_file():
            continue
        for entry in json.loads(manifest.read_text(encoding="utf-8")):
            names = [name.upper() for name in entry.get("vdf_candidates_by_exact_vertex_span", [])]
            if vdf_path.name.upper() in names:
                return directory / entry["obj"]
    return None


def group_by_trajectory(offsets_per_frame, vertex_count, tolerance):
    """Group vertices whose offset from rest is identical in every frame.

    offsets_per_frame: list of frames, each a list of (x, y, z) offsets.
    Vertices in a group move rigidly by translation, so one bone per group
    reproduces the animation exactly while keeping the bone count down.
    """
    quantum = max(tolerance, 1e-9)
    groups = {}
    for vertex in range(vertex_count):
        key = tuple(
            round(component / quantum)
            for frame in offsets_per_frame
            for component in frame[vertex]
        )
        groups.setdefault(key, []).append(vertex)
    return list(groups.values())


# ---------------------------------------------------------------------------
# Blender helpers
# ---------------------------------------------------------------------------

def _action_fcurves(action, slot=None):
    """F-curves of an action on both legacy and layered (4.4+) action APIs."""
    if hasattr(action, "layers") and action.layers:
        curves = []
        for layer in action.layers:
            for strip in layer.strips:
                for channelbag in strip.channelbags:
                    if slot is None or channelbag.slot == slot:
                        curves.extend(channelbag.fcurves)
        return curves
    return list(getattr(action, "fcurves", []))


def _assign_action(id_data, action):
    anim = id_data.animation_data or id_data.animation_data_create()
    anim.action = action
    if action is not None and hasattr(anim, "action_slot") and anim.action_slot is None:
        slots = list(getattr(anim, "action_suitable_slots", [])) or list(action.slots)
        if slots:
            anim.action_slot = slots[0]
    return anim


def _set_frame_range(action, start, end):
    if hasattr(action, "use_frame_range"):
        action.use_frame_range = True
        action.frame_start = start
        action.frame_end = end


def build_base_mesh(context, name, psx_vertices, faces, scale, source=""):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([psx_to_blender(v, scale) for v in psx_vertices], [], faces)
    mesh.validate(clean_customdata=False)
    if len(mesh.vertices) != len(psx_vertices):
        raise RuntimeError("mesh validation changed the vertex count; VDF indices would no longer line up")
    obj = bpy.data.objects.new(name, mesh)
    obj[PROP_SCALE] = scale
    obj["parappa_source"] = source
    context.collection.objects.link(obj)
    return obj


def import_base_obj(context, path, scale):
    vertices, faces = read_vertex_order_obj(path)
    return build_base_mesh(context, Path(path).stem, vertices, faces, scale, str(path))


def parappa_actions(obj):
    key = obj.data.shape_keys if obj and obj.type == "MESH" else None
    names = list(obj.get(PROP_VDF, [])) if obj else []
    return [bpy.data.actions[name] for name in names if name in bpy.data.actions] if key else []


def activate_vdf_action(context, obj, action):
    """Zero every PaRappa shape key, then play `action` on the mesh."""
    key = obj.data.shape_keys
    for block in key.key_blocks[1:]:
        if block.name.startswith("VDF:"):
            block.value = 0.0
    _assign_action(key, action)
    if hasattr(action, "use_frame_range") and action.use_frame_range:
        context.scene.frame_start = int(action.frame_start)
        context.scene.frame_end = int(action.frame_end)


def apply_vdf(context, obj, path, frames_per_key=4, interpolation="LINEAR", loop=False,
              object_id=None, scale=None):
    """Add a VDF file to `obj` as shape keys plus an action. Returns the action."""
    records = read_vdf(path)
    if object_id is not None:
        records = [record for record in records if record.object_id == object_id]
    if not records:
        raise ValueError(f"{Path(path).name}: no records for object {object_id}")
    scale = scale if scale is not None else float(obj.get(PROP_SCALE, DEFAULT_SCALE))
    mesh = obj.data
    vertex_count = len(mesh.vertices)
    for index, record in enumerate(records):
        end = record.first_vertex + len(record.deltas)
        if end > vertex_count:
            raise ValueError(
                f"{Path(path).name} record {index} touches vertices {record.first_vertex}-{end - 1} "
                f"but '{obj.name}' has {vertex_count}; select the matching base mesh"
            )

    if mesh.shape_keys is None:
        obj.shape_key_add(name="Basis", from_mix=False)
    basis = mesh.shape_keys.key_blocks[0]
    stem = Path(path).stem
    blocks = []
    for index, record in enumerate(records):
        block = obj.shape_key_add(name=f"VDF:{stem}:{index:02d}", from_mix=False)
        block.slider_min = -1.0
        block.slider_max = 2.0
        for offset, delta in enumerate(record.deltas):
            vertex = record.first_vertex + offset
            block.data[vertex].co = basis.data[vertex].co + Vector(psx_to_blender(delta, scale))
        block.value = 0.0
        blocks.append(block)

    key = mesh.shape_keys
    action = bpy.data.actions.new(stem)
    action.use_fake_user = True
    _assign_action(key, action)
    start = 1
    times = [start + i * frames_per_key for i in range(len(blocks))]
    sequence = list(range(len(blocks)))
    if loop and len(blocks) > 1:
        times.append(start + len(blocks) * frames_per_key)
        sequence.append(0)
    # Every key gets a value at every key time: 1 when it is the current pose,
    # 0 otherwise. LINEAR then crossfades neighbours; CONSTANT steps.
    for block_index, block in enumerate(blocks):
        for time, active in zip(times, sequence):
            block.value = 1.0 if active == block_index else 0.0
            block.keyframe_insert("value", frame=time)
        block.value = 0.0
    for curve in _action_fcurves(action):
        for point in curve.keyframe_points:
            point.interpolation = interpolation
    _set_frame_range(action, times[0], times[-1])
    action["parappa_vdf_source"] = str(path)

    names = list(obj.get(PROP_VDF, []))
    names.append(action.name)
    obj[PROP_VDF] = names
    context.scene.render.fps = PS1_FPS
    activate_vdf_action(context, obj, action)
    return action


def _evaluated_coords(context, obj):
    depsgraph = context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    coords = [vertex.co.copy() for vertex in mesh.vertices]
    evaluated.to_mesh_clear()
    return coords


def bake_for_roblox(context, obj, actions=None, tolerance=1e-4):
    """Build <obj>_Roblox mesh + <obj>_Rig armature with one baked action per VDF action."""
    actions = actions or parappa_actions(obj)
    if not actions:
        raise ValueError(f"'{obj.name}' has no imported VDF animations")
    scene = context.scene
    saved = (scene.frame_current, scene.frame_start, scene.frame_end)
    rest = [block.co.copy() for block in obj.data.shape_keys.key_blocks[0].data]
    vertex_count = len(rest)

    sampled = {}  # action name -> (frame list, per-frame offsets)
    for action in actions:
        activate_vdf_action(context, obj, action)
        start, end = (int(v) for v in action.frame_range)
        frames = list(range(start, end + 1))
        offsets = []
        for frame in frames:
            scene.frame_set(frame)
            coords = _evaluated_coords(context, obj)
            offsets.append([tuple(coords[i] - rest[i]) for i in range(vertex_count)])
        sampled[action.name] = (frames, offsets)

    all_offsets = [frame for _, offsets in sampled.values() for frame in offsets]
    groups = group_by_trajectory(all_offsets, vertex_count, tolerance)

    # Static copy of the base mesh without shape keys.
    mesh = obj.data.copy()
    mesh.name = f"{obj.name}_Roblox"
    out_obj = bpy.data.objects.new(mesh.name, mesh)
    context.collection.objects.link(out_obj)
    out_obj.matrix_world = obj.matrix_world.copy()
    out_obj.shape_key_clear()
    out_obj.animation_data_clear()
    for prop in (PROP_VDF,):
        if prop in out_obj:
            del out_obj[prop]

    size = max((max(abs(c) for c in co) for co in rest), default=1.0)
    bone_length = max(size * 0.03, 1e-3)
    armature = bpy.data.armatures.new(f"{obj.name}_Rig")
    rig = bpy.data.objects.new(armature.name, armature)
    context.collection.objects.link(rig)
    rig[PROP_BAKED] = True

    # Roblox wants a root bone at the origin carrying no skin weights.
    previous_active = context.view_layer.objects.active
    context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    root = armature.edit_bones.new("Root")
    root.head = (0.0, 0.0, 0.0)
    root.tail = (0.0, 0.0, bone_length * 2)
    bone_names = []
    for group_index, vertices in enumerate(groups):
        centre = sum((rest[i] for i in vertices), Vector()) / len(vertices)
        bone = armature.edit_bones.new(f"V{group_index:03d}")
        bone.head = centre
        bone.tail = centre + Vector((0.0, 0.0, bone_length))
        bone.parent = root
        bone.use_connect = False
        bone_names.append(bone.name)
    bpy.ops.object.mode_set(mode="OBJECT")
    context.view_layer.objects.active = previous_active

    for name, vertices in zip(bone_names, groups):
        group = out_obj.vertex_groups.new(name=name)
        group.add(vertices, 1.0, "REPLACE")
    modifier = out_obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = rig
    out_obj.parent = rig

    rest_inverse = {name: armature.bones[name].matrix_local.to_3x3().inverted() for name in bone_names}
    baked = []
    for action in actions:
        frames, offsets = sampled[action.name]
        rig_action = bpy.data.actions.new(f"{action.name}")
        rig_action.name = f"{action.name}_Roblox"
        rig_action.use_fake_user = True
        rig_action["parappa_vdf_source"] = action.get("parappa_vdf_source", "")
        _assign_action(rig, rig_action)
        for frame, frame_offsets in zip(frames, offsets):
            for name, vertices in zip(bone_names, groups):
                pose_bone = rig.pose.bones[name]
                pose_bone.location = rest_inverse[name] @ Vector(frame_offsets[vertices[0]])
                pose_bone.keyframe_insert("location", frame=frame)
            root_bone = rig.pose.bones["Root"]
            root_bone.location = (0.0, 0.0, 0.0)
            root_bone.keyframe_insert("location", frame=frame)
        for curve in _action_fcurves(rig_action):
            for point in curve.keyframe_points:
                point.interpolation = "LINEAR"
        _set_frame_range(rig_action, frames[0], frames[-1])
        baked.append(rig_action)
    if baked:
        _assign_action(rig, baked[0])

    rig["parappa_actions"] = [action.name for action in baked]
    scene.frame_start, scene.frame_end = saved[1], saved[2]
    scene.frame_set(saved[0])
    return rig, out_obj, baked, len(groups)


def export_roblox_fbx(context, rig, directory, actions=None, animation_only=False):
    """Export one FBX per baked action. Returns written paths.

    animation_only writes just the bones and their keyframes, no mesh.
    """
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    names = list(rig.get("parappa_actions", []))
    actions = actions or [bpy.data.actions[name] for name in names if name in bpy.data.actions]
    meshes = [child for child in rig.children if child.type == "MESH"]
    if not actions or not meshes:
        raise ValueError(f"'{rig.name}' has no baked Roblox actions or skinned mesh")

    scene = context.scene
    saved_range = (scene.frame_start, scene.frame_end)
    for selected in context.selected_objects:
        selected.select_set(False)
    rig.select_set(True)
    if not animation_only:
        for mesh in meshes:
            mesh.select_set(True)
    context.view_layer.objects.active = rig

    written = []
    for action in actions:
        _assign_action(rig, action)
        start, end = (int(v) for v in action.frame_range)
        scene.frame_start, scene.frame_end = start, end
        path = directory / f"{bpy.path.clean_name(action.name.removesuffix('_Roblox'))}.fbx"
        bpy.ops.export_scene.fbx(
            filepath=str(path),
            use_selection=True,
            object_types={"ARMATURE"} if animation_only else {"ARMATURE", "MESH"},
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_NONE",
            axis_forward="-Z",
            axis_up="Y",
            mesh_smooth_type="FACE",
            add_leaf_bones=False,
            primary_bone_axis="Y",
            secondary_bone_axis="X",
            use_armature_deform_only=False,
            bake_anim=True,
            bake_anim_use_all_bones=True,
            bake_anim_use_nla_strips=False,
            bake_anim_use_all_actions=False,
            bake_anim_force_startend_keying=True,
            bake_anim_step=1.0,
            bake_anim_simplify_factor=0.0,
        )
        written.append(path)
    scene.frame_start, scene.frame_end = saved_range
    return written


# ---------------------------------------------------------------------------
# Operators and UI
# ---------------------------------------------------------------------------

if bpy is not None:

    class PARAPPA_OT_import_base(bpy.types.Operator, ImportHelper):
        """Import an extract_tmd.py OBJ keeping the TMD vertex order"""
        bl_idname = "import_mesh.parappa_base"
        bl_label = "Import PaRappa Base Mesh"
        bl_options = {"REGISTER", "UNDO"}
        filename_ext = ".obj"
        filter_glob: StringProperty(default="*.obj", options={"HIDDEN"})
        scale: FloatProperty(name="Scale", default=DEFAULT_SCALE, min=1e-6, precision=5)

        def execute(self, context):
            obj = import_base_obj(context, self.filepath, self.scale)
            context.view_layer.objects.active = obj
            obj.select_set(True)
            return {"FINISHED"}

    class PARAPPA_OT_import_vdf(bpy.types.Operator, ImportHelper):
        """Import PlayStation VDF vertex-difference animation as shape keys and actions"""
        bl_idname = "import_anim.parappa_vdf"
        bl_label = "Import PaRappa VDF"
        bl_options = {"REGISTER", "UNDO"}
        filename_ext = ".vdf"
        filter_glob: StringProperty(default="*.vdf;*.VDF", options={"HIDDEN"})
        files: CollectionProperty(type=bpy.types.OperatorFileListElement, options={"HIDDEN", "SKIP_SAVE"})
        directory: StringProperty(subtype="DIR_PATH", options={"HIDDEN", "SKIP_SAVE"})

        target: EnumProperty(
            name="Base mesh",
            items=[
                ("AUTO", "Auto (manifest)", "Use the active mesh if its vertex count fits, otherwise load the OBJ listed in tmd-extracted/manifest.json"),
                ("ACTIVE", "Active object", "Always apply to the active mesh"),
                ("FILE", "OBJ file", "Load the base mesh from the path below"),
            ],
            default="AUTO",
        )
        base_path: StringProperty(name="Base OBJ", subtype="FILE_PATH")
        scale: FloatProperty(name="Scale (new meshes)", default=DEFAULT_SCALE, min=1e-6, precision=5)
        frames_per_key: IntProperty(name="Frames per key", default=4, min=1, max=240,
                                    description="Timeline frames between VDF records (scene runs at 30 fps). The game's exact timing is not decoded yet")
        interpolation: EnumProperty(
            name="Blend",
            items=[("LINEAR", "Crossfade", "Blend smoothly between records"),
                   ("CONSTANT", "Step", "Snap from record to record")],
            default="LINEAR",
        )
        loop: BoolProperty(name="Loop back to first record", default=False)

        def _base_for(self, context, path):
            active = context.active_object
            if self.target == "ACTIVE":
                if not active or active.type != "MESH":
                    raise ValueError("select a mesh first")
                return active
            if self.target == "FILE":
                base = Path(bpy.path.abspath(self.base_path))
            else:
                records = read_vdf(path)
                needed = max(r.first_vertex + len(r.deltas) for r in records)
                if active and active.type == "MESH" and len(active.data.vertices) == needed:
                    return active
                base = find_base_mesh(path)
                if base is None:
                    raise ValueError(f"{Path(path).name}: no base mesh in tmd-extracted/manifest.json; pick one with 'OBJ file'")
            # Reuse a base already imported in this file.
            for obj in context.scene.objects:
                if obj.type == "MESH" and obj.get("parappa_source") == str(base):
                    return obj
            return import_base_obj(context, base, self.scale)

        def execute(self, context):
            paths = [os.path.join(self.directory, f.name) for f in self.files if f.name] or [self.filepath]
            done = 0
            for path in paths:
                try:
                    obj = self._base_for(context, path)
                    action = apply_vdf(context, obj, path, self.frames_per_key, self.interpolation, self.loop)
                except (ValueError, OSError, RuntimeError) as error:
                    self.report({"WARNING"}, str(error))
                    continue
                context.view_layer.objects.active = obj
                obj.select_set(True)
                done += 1
                self.report({"INFO"}, f"{Path(path).name} -> {obj.name} ({action.name})")
            return {"FINISHED"} if done else {"CANCELLED"}

    class PARAPPA_OT_play_vdf(bpy.types.Operator):
        """Switch the active mesh to one of its imported VDF animations"""
        bl_idname = "object.parappa_play_vdf"
        bl_label = "Play VDF Animation"
        bl_options = {"REGISTER", "UNDO"}
        action_name: StringProperty()

        def execute(self, context):
            obj = context.active_object
            action = bpy.data.actions.get(self.action_name)
            if obj is None or action is None or obj.data.shape_keys is None:
                return {"CANCELLED"}
            activate_vdf_action(context, obj, action)
            context.scene.frame_set(context.scene.frame_start)
            return {"FINISHED"}

    class PARAPPA_OT_bake_roblox(bpy.types.Operator):
        """Bake the active mesh's VDF animations into a bone rig Roblox can import"""
        bl_idname = "object.parappa_bake_roblox"
        bl_label = "Bake VDF to Bones (Roblox)"
        bl_options = {"REGISTER", "UNDO"}
        only_current: BoolProperty(name="Only the current animation", default=False)
        tolerance: FloatProperty(name="Merge tolerance", default=1e-4, min=0.0, precision=6,
                                 description="Vertices whose motion matches within this distance share one bone")

        @classmethod
        def poll(cls, context):
            obj = context.active_object
            return obj is not None and obj.type == "MESH" and bool(obj.get(PROP_VDF))

        def execute(self, context):
            obj = context.active_object
            actions = None
            if self.only_current:
                anim = obj.data.shape_keys.animation_data
                actions = [anim.action] if anim and anim.action else None
            try:
                rig, mesh, baked, bones = bake_for_roblox(context, obj, actions, self.tolerance)
            except ValueError as error:
                self.report({"ERROR"}, str(error))
                return {"CANCELLED"}
            obj.hide_set(True)
            self.report({"INFO"}, f"Baked {len(baked)} animation(s) onto {bones} bones + Root in '{rig.name}'")
            return {"FINISHED"}

    class PARAPPA_OT_export_roblox(bpy.types.Operator, ExportHelper):
        """Export each baked PaRappa animation as its own Roblox-ready FBX"""
        bl_idname = "export_scene.parappa_roblox_fbx"
        bl_label = "Export PaRappa Roblox FBX"
        filename_ext = ".fbx"
        filter_glob: StringProperty(default="*.fbx", options={"HIDDEN"})
        only_current: BoolProperty(name="Only the current animation", default=False)
        animation_only: BoolProperty(name="Animation only (no mesh)", default=True,
                                     description="Write only the bones and keyframes")

        @classmethod
        def poll(cls, context):
            return _find_rig(context) is not None

        def execute(self, context):
            rig = _find_rig(context)
            actions = None
            if self.only_current and rig.animation_data and rig.animation_data.action:
                actions = [rig.animation_data.action]
            try:
                written = export_roblox_fbx(context, rig, Path(self.filepath).parent, actions, self.animation_only)
            except ValueError as error:
                self.report({"ERROR"}, str(error))
                return {"CANCELLED"}
            self.report({"INFO"}, f"Wrote {len(written)} FBX file(s) to {Path(self.filepath).parent}")
            return {"FINISHED"}

    def _find_rig(context):
        obj = context.active_object
        if obj is None:
            return None
        if obj.type == "MESH" and obj.parent and obj.parent.get(PROP_BAKED):
            return obj.parent
        return obj if obj.type == "ARMATURE" and obj.get(PROP_BAKED) else None

    class PARAPPA_PT_panel(bpy.types.Panel):
        bl_label = "PaRappa VDF"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "PaRappa"

        def draw(self, context):
            layout = self.layout
            layout.operator(PARAPPA_OT_import_vdf.bl_idname, icon="IMPORT")
            obj = context.active_object
            actions = parappa_actions(obj) if obj and obj.type == "MESH" else []
            if actions:
                current = obj.data.shape_keys.animation_data
                current = current.action if current else None
                box = layout.box()
                box.label(text="Animations")
                for action in actions:
                    row = box.row()
                    op = row.operator(PARAPPA_OT_play_vdf.bl_idname, text=action.name,
                                      icon="PLAY" if action == current else "ACTION",
                                      depress=action == current)
                    op.action_name = action.name
                layout.operator(PARAPPA_OT_bake_roblox.bl_idname, icon="ARMATURE_DATA")
            if _find_rig(context):
                layout.operator(PARAPPA_OT_export_roblox.bl_idname, icon="EXPORT")

    def _menu_import(self, context):
        self.layout.operator(PARAPPA_OT_import_base.bl_idname, text="PaRappa base mesh (.obj)")
        self.layout.operator(PARAPPA_OT_import_vdf.bl_idname, text="PaRappa VDF animation (.vdf)")

    def _menu_export(self, context):
        self.layout.operator(PARAPPA_OT_export_roblox.bl_idname, text="PaRappa Roblox FBX (.fbx)")

    def _menu_object(self, context):
        self.layout.separator()
        self.layout.operator(PARAPPA_OT_bake_roblox.bl_idname)

    _classes = (
        PARAPPA_OT_import_base,
        PARAPPA_OT_import_vdf,
        PARAPPA_OT_play_vdf,
        PARAPPA_OT_bake_roblox,
        PARAPPA_OT_export_roblox,
        PARAPPA_PT_panel,
    )

    def register():
        for cls in _classes:
            bpy.utils.register_class(cls)
        bpy.types.TOPBAR_MT_file_import.append(_menu_import)
        bpy.types.TOPBAR_MT_file_export.append(_menu_export)
        bpy.types.VIEW3D_MT_object.append(_menu_object)

    def unregister():
        bpy.types.VIEW3D_MT_object.remove(_menu_object)
        bpy.types.TOPBAR_MT_file_export.remove(_menu_export)
        bpy.types.TOPBAR_MT_file_import.remove(_menu_import)
        for cls in reversed(_classes):
            bpy.utils.unregister_class(cls)
