"""Runs inside Blender (headless): reduces the posed body to about N vertices with the Decimate modifier (Collapse, symmetry on X, triangulate).
blender --background --factory-startup --python decimate_blender.py -- <dir> <target vertices>
Reads <dir>/pos.npy and <dir>/tris.npy; writes <dir>/out_pos.npy, <dir>/out_tris.npy and prints BLENDER <version>."""
import sys

import bpy
import numpy as np

directory, target = sys.argv[sys.argv.index("--") + 1], int(sys.argv[-1])
pos = np.load(directory + "/pos.npy")
tris = np.load(directory + "/tris.npy")
mesh = bpy.data.meshes.new("body")
mesh.from_pydata(pos.tolist(), [], tris.tolist())
obj = bpy.data.objects.new("body", mesh)
bpy.context.scene.collection.objects.link(obj)
mod = obj.modifiers.new("d", "DECIMATE")
mod.decimate_type = "COLLAPSE"
mod.use_symmetry = True
mod.symmetry_axis = "X"
mod.use_collapse_triangulate = True


def reduced(ratio):
    mod.ratio = ratio
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return evaluated.to_mesh()


low, high = 0.0, 1.0  # bisect the ratio: vertex count rises with it
for _ in range(24):
    mid = (low + high) / 2
    out = reduced(mid)
    if len(out.vertices) < target:
        low = mid
    else:
        high = mid
out = reduced(high)
out.calc_loop_triangles()
verts = np.array([v.co[:] for v in out.vertices])
faces = np.array([t.vertices[:] for t in out.loop_triangles])
np.save(directory + "/out_pos.npy", verts)
np.save(directory + "/out_tris.npy", faces)
print("BLENDER", bpy.app.version_string, len(verts), len(faces))
