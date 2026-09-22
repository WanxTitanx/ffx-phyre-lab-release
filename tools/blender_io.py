# ── PHYRE-LAB Blender I/O script (P21) — run INSIDE Blender ──────────────────
# blender -b -P tools/blender_io.py -- import <in.gltf> <out.gltf>
#   [--edit-rx <deg>]        deterministic edit: rotate first bone
#   [--stats out.json]       dump scene stats (verts/bones/actions)
# Round-trip path: imports the tagged glTF, optionally applies a known edit,
# re-exports with export_extras so custom props survive the trip.
import json
import sys

import bpy


def clean():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def stats():
    me = bpy.context.scene.objects
    return {
        "objects": len(me),
        "meshes": sum(1 for o in me if o.type == "MESH"),
        "verts": sum(len(o.data.vertices) for o in me if o.type == "MESH"),
        "armatures": sum(1 for o in me if o.type == "ARMATURE"),
        "bones": sum(len(o.data.bones) for o in me if o.type == "ARMATURE"),
        "actions": len(bpy.data.actions),
    }


def main():
    av = sys.argv[sys.argv.index("--") + 1:]
    cmd = av[0]
    clean()
    if cmd == "import":
        src, dst = av[1], av[2]
        bpy.ops.import_scene.gltf(filepath=src)
        if "--edit-vx" in av:
            # deterministic authored edit: translate first vertex +X
            dx = float(av[av.index("--edit-vx") + 1])
            me = next(o for o in bpy.context.scene.objects
                      if o.type == "MESH")
            me.data.vertices[0].co.x += dx
        bpy.ops.export_scene.gltf(
            filepath=dst, export_format="GLTF_SEPARATE",
            export_extras=True, export_skins=True,
            export_yup=True, export_apply=False,
            export_animations=True, export_def_bones=False)
        print("BLENDER_RT_OK")
    if "--stats" in av:
        json.dump(stats(), open(av[av.index("--stats") + 1], "w"))
    return 0


main()
