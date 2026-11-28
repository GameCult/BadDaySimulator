#!/usr/bin/env python3
"""Builds client/fella/fella_skin.glb: Fella's drawn body, MakeHuman's CC0 base mesh fitted to the Lodur plan's skeleton.

Pipeline (each step reads only the pinned inputs in tools/FellaMesh/input and the plan JSON from `baddaysim plan`):
  1. verify the pinned MakeHuman files by size and SHA-256;
  2. take the base mesh at the macro values (only MakeHuman's neutral ones: see MACROS), keep the body group, scale
     the whole body so its crown meets the plan's crown, soles on y = 0;
  3. per plan part, an affine map from the MakeHuman bones it owns to the plan's creation pose: the part's
     joint-to-joint segment is rotated and stretched along its axis onto the plan's segment (girth kept); every vertex
     is linearly blended over the parts by MakeHuman's own weights (the LBS pose into the creation pose);
  4. Blender's Decimate modifier (Collapse, symmetry on X, triangulate) on the whole body, run headless with private user folders;
  5. every face goes to the part with the most weight; a vertex on faces of two or more parts is a joint-ring vertex,
     emitted once per part with weight 0.5/0.5 on its two heaviest parts and one shared normal;
  6. one glTF primitive per part, JOINTS_0/WEIGHTS_0, one skin whose joints are the plan's parts, inverse binds from
     the plan's creation frames.

The skeleton is never typed here: part labels, creation frames and joint anchors come from the plan.
Usage: build_fella_mesh.py [--out client/fella/fella_skin.glb] [--check] [--plan plan.json] [macro options]
"""
import argparse
import hashlib
import os
import tempfile
import json
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
INPUT = HERE / "input"

# The pinned MakeHuman files (makehumancommunity/makehuman, CC0 1.0): name -> (bytes, sha256).
PINS = {
    "base.obj": (1749303, "8e761e6624b8f54536409135d1636da63b32486a90d4897f84e121d144f6fb4c"),
    "default.mhskel": (117790, "99f179bce0aa850b45d4191a1d0d234c5851f881c057439470ded3bddf729a24"),
    "default_weights.mhw": (897521, "0f3641d651ae3d00ad6b4ccee43142edb109d3bd909d27d9e4139ef1beed8625"),
}
# The Blender the glb is built with: its Decimate result is only reproducible on this version (--check refuses any other).
BLENDER_VERSION = "5.3.0"
BLENDER_DEFAULT = r"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe"  # or the BLENDER environment variable
MAKEHUMAN_COMMIT = "a8bc2d54ff0ac92e78ff71431b1023eda42bf482"

# MakeHuman's neutral macro values (its defaults: gender 0.5 is the androgynous base, lodur:ruling:default-fella-androgynous-base).
# The base mesh is the model with no macro target applied; a target set is not pinned yet, so any other value is refused
# until the follow-up fella-body-variety pins the macrodetails targets and ports their weighting.
MACROS = {"gender": 0.5, "age": 0.5, "muscle": 0.5, "weight": 0.5, "height": 0.5, "proportions": 0.5}

TARGET_VERTICES = 450  # about the whole body after decimation, before joint rings are copied per part
RING_PASSES = 8  # rounds of moving faces off a third part at a vertex; a vertex where three still meet is weighted to its two heaviest
RING = 0.5  # a joint-ring vertex's weight on each of its two parts

SIDE_SUFFIX = {"Left": ".L", "Right": ".R"}


def fail(message):
    sys.exit(f"build_fella_mesh: {message}")


# ---------------------------------------------------------------- inputs

def load_pinned():
    for name, (size, sha) in PINS.items():
        data = (INPUT / name).read_bytes()
        if len(data) != size or hashlib.sha256(data).hexdigest() != sha:
            fail(f"{INPUT / name} is not the pinned MakeHuman file (size or SHA-256 differs)")
    obj_v, obj_vt, faces, group = [], [], [], None
    for line in (INPUT / "base.obj").read_text().splitlines():
        t = line.split()
        if not t:
            continue
        if t[0] == "v":
            obj_v.append([float(x) for x in t[1:4]])
        elif t[0] == "vt":
            obj_vt.append([float(x) for x in t[1:3]])
        elif t[0] == "g":
            group = t[1]
        elif t[0] == "f" and group == "body":
            faces.append([tuple(int(i) - 1 for i in c.split("/")) for c in t[1:]])
    skeleton = json.loads((INPUT / "default.mhskel").read_text())
    weights = json.loads((INPUT / "default_weights.mhw").read_text())["weights"]
    return np.array(obj_v), np.array(obj_vt), faces, skeleton, weights


def load_plan(path):
    if path:
        text = Path(path).read_text()
    else:
        text = subprocess.run(
            ["dotnet", "run", "--project", str(ROOT / "src" / "BadDay.Cli"), "--", "plan"],
            check=True, capture_output=True, text=True, cwd=ROOT).stdout
    plan = json.loads(text)
    if len(plan["parts"]) != 19 or len(plan["joints"]) != 18:
        fail("the plan is not the 19-part, 18-joint humanoid")
    return plan


class Plan:
    def __init__(self, plan):
        self.parts = plan["parts"]
        self.index = {(p["label"], p["side"]): i for i, p in enumerate(self.parts)}
        self.joint = {(j["label"], j["side"]): np.array(j["anchor"]) for j in plan["joints"]}
        self.parent = {j["child"]: j["parent"] for j in plan["joints"]}

    def center(self, label, side):
        return np.array(self.parts[self.index[(label, side)]]["position"])

    def anchor(self, label, side):
        return self.joint[(label, side)]

    def name(self, i):
        p = self.parts[i]
        return p["label"] if p["side"] == "Center" else f"{p['label']}.{p['side']}"


# ---------------------------------------------------------------- fitting

def unit(v):
    return v / np.linalg.norm(v)


def frame(axis, across):
    d = unit(axis)
    a = unit(across - np.dot(across, d) * d)
    return np.column_stack([d, a, np.cross(d, a)])


def segment_map(p, q, across, p2, q2, across2, keep_length=False):
    """The affine map taking MakeHuman's segment p->q onto the plan's p2->q2: the axis rotated onto the axis with the
    across reference kept, stretched along the axis by the length ratio; perpendicular extent untouched."""
    d = unit(q - p)
    s = 1.0 if keep_length else np.linalg.norm(q2 - p2) / np.linalg.norm(q - p)
    r = frame(q2 - p2, across2) @ frame(d, across).T
    a = r @ (np.eye(3) + (s - 1.0) * np.outer(d, d))
    return a, p2 - a @ p


def part_table(plan, joint):
    """(part ordinal) -> (MakeHuman bone names, segment p, q, across, plan p2, q2, across2, keep_length).
    MakeHuman points are bone heads or tails; plan points are joint anchors, or the part's far end where the plan gives
    only a centre (a part's joint sits at its near end, so the far end is twice the centre minus the anchor)."""
    h = lambda bone: joint[bone + "____head"]
    t = lambda bone: joint[bone + "____tail"]
    x_axis = np.array([1.0, 0.0, 0.0])
    z_axis = np.array([0.0, 0.0, 1.0])
    rows = {}

    def far(label, side, anchor_label, anchor_side=None):
        return 2.0 * plan.center(label, side) - plan.anchor(anchor_label, anchor_side or side)

    hips = (plan.anchor("Hip", "Left") + plan.anchor("Hip", "Right")) / 2.0
    crown = 2.0 * plan.center("Head", "Center") - plan.anchor("Neck", "Center")  # the plan's head rests on its neck joint
    rows[plan.index[("Pelvis", "Center")]] = (
        ["root", "pelvis.L", "pelvis.R", "spine05", "spine04"],
        (h("upperleg01.L") + h("upperleg01.R")) / 2.0, h("spine03"), x_axis, hips, plan.anchor("Spine", "Center"), x_axis, False)
    rows[plan.index[("Torso", "Center")]] = (
        ["spine03", "spine02", "spine01", "neck01", "clavicle.L", "clavicle.R", "shoulder01.L", "shoulder01.R", "breast.L", "breast.R"],
        h("spine03"), h("neck02"), x_axis, plan.anchor("Spine", "Center"), plan.anchor("Neck", "Center"), x_axis, False)
    rows[plan.index[("Head", "Center")]] = (
        None,  # every bone not listed by another part: the neck's upper bones, the skull, jaw, face and tongue
        h("neck02"), np.array([t("head")[0], joint["__crown"][1], t("head")[2]]), x_axis, plan.anchor("Neck", "Center"), crown, x_axis, False)
    for side, sx in (("Left", ".L"), ("Right", ".R")):
        across = h("finger2-1" + sx) - h("finger5-1" + sx)  # pinky to index: toward the thumb, the plan's +z
        hand = lambda bone: bone + sx
        rows[plan.index[("UpperArm", side)]] = (
            [hand("upperarm01"), hand("upperarm02")], h(hand("upperarm01")), h(hand("lowerarm01")), across,
            plan.anchor("Shoulder", side), plan.anchor("Elbow", side), z_axis, False)
        rows[plan.index[("LowerArm", side)]] = (
            [hand("lowerarm01"), hand("lowerarm02")], h(hand("lowerarm01")), h(hand("wrist")), across,
            plan.anchor("Elbow", side), plan.anchor("Wrist", side), z_axis, False)
        rows[plan.index[("Palm", side)]] = (
            [hand("wrist")] + [f"metacarpal{i}{sx}" for i in range(1, 5)], h(hand("wrist")), h("finger3-1" + sx), across,
            plan.anchor("Wrist", side), far("Palm", side, "Wrist"), z_axis, False)
        rows[plan.index[("Thumb", side)]] = (
            [f"finger1-{i}{sx}" for i in (1, 2, 3)], h("finger1-1" + sx), t("finger1-3" + sx), across,
            plan.anchor("Thumb", side), far("Thumb", side, "Thumb"), z_axis, False)
        rows[plan.index[("Fingers", side)]] = (
            [f"finger{n}-{i}{sx}" for n in (2, 3, 4, 5) for i in (1, 2, 3)], h("finger3-1" + sx), t("finger3-3" + sx), across,
            plan.anchor("Fingers", side), far("Fingers", side, "Fingers"), z_axis, False)
        rows[plan.index[("Thigh", side)]] = (
            [hand("upperleg01"), hand("upperleg02")], h(hand("upperleg01")), h(hand("lowerleg01")), x_axis,
            plan.anchor("Hip", side), plan.anchor("Knee", side), x_axis, False)
        rows[plan.index[("Shin", side)]] = (
            [hand("lowerleg01"), hand("lowerleg02")], h(hand("lowerleg01")), t(hand("lowerleg02")), x_axis,
            plan.anchor("Knee", side), plan.anchor("Ankle", side), x_axis, False)
        toes = [f"toe{n}-{i}{sx}" for n in range(1, 6) for i in (1, 2, 3) if not (n == 1 and i == 3)]
        rows[plan.index[("Foot", side)]] = (
            [hand("foot")] + toes, h(hand("foot")), t(hand("foot")), x_axis,
            plan.anchor("Ankle", side), plan.anchor("Ankle", side) + t(hand("foot")) - h(hand("foot")), x_axis, True)  # the foot keeps MakeHuman's pitch and length
    return rows


def fit(plan, mh_v, mh_faces, skeleton, mh_weights):
    """Steps 2 and 3: the body-group mesh posed into the plan's creation pose. Returns positions (n, 3), triangles, part weights (n, 19), uv index."""
    body = sorted({c[0] for f in mh_faces for c in f})
    remap = {v: i for i, v in enumerate(body)}
    pos = mh_v[body]
    tris, tri_uv = [], []
    for f in mh_faces:  # MakeHuman's quads, split on the first diagonal
        corners = [(remap[c[0]], c[1]) for c in f]
        for k in range(1, len(corners) - 1):
            tris.append([corners[0][0], corners[k][0], corners[k + 1][0]])
            tri_uv.append([corners[0][1], corners[k][1], corners[k + 1][1]])

    joint = {name: mh_v[idx].mean(axis=0) for name, idx in skeleton["joints"].items()}
    ymin, ymax = pos[:, 1].min(), pos[:, 1].max()
    joint["__crown"] = np.array([0.0, ymax, 0.0])
    crown_plan = 2.0 * plan.center("Head", "Center") - plan.anchor("Neck", "Center")
    scale = crown_plan[1] / (ymax - ymin)
    shift = np.array([0.0, ymin, 0.0])
    pos = (pos - shift) * scale
    joint = {k: (v - shift) * scale for k, v in joint.items()}

    rows = part_table(plan, joint)
    owner = {}
    for part, row in rows.items():
        for bone in row[0] or []:
            if bone in owner:
                fail(f"bone {bone} is claimed by two parts")
            owner[bone] = part
    head = plan.index[("Head", "Center")]
    nparts = len(plan.parts)
    w = np.zeros((len(pos), nparts))
    for bone, entries in mh_weights.items():
        part = owner.get(bone, head)
        for v, x in entries:
            if v in remap:
                w[remap[v], part] += x
    total = w.sum(axis=1)
    if total.min() <= 0.0:
        fail("a body vertex has no skin weight")
    w /= total[:, None]

    maps = {part: segment_map(*row[1:4], *row[4:7], keep_length=row[7]) for part, row in rows.items()}
    posed = np.zeros_like(pos)
    for part, (a, t) in maps.items():
        posed += w[:, part, None] * (pos @ a.T + t)
    return posed, np.array(tris), w, tri_uv


# ---------------------------------------------------------------- decimation

def decimate(pos, tris, w, uv, target):
    """Blender's Decimate modifier (Collapse, symmetry on X, triangulate) reduces the posed body to about `target` vertices;
    each surviving vertex takes the part weights and UV of the nearest vertex of the full mesh. Returns the Blender version used."""
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        np.save(tmp / "pos.npy", pos)
        np.save(tmp / "tris.npy", tris)
        env = dict(os.environ)
        for name in ("CONFIG", "EXTENSIONS", "SCRIPTS"):  # Blender never reads or writes the operator's user folders
            (tmp / name).mkdir()
            env[f"BLENDER_USER_{name}"] = str(tmp / name)
        run = subprocess.run([os.environ.get("BLENDER", BLENDER_DEFAULT), "--background", "--factory-startup", "--python", str(HERE / "decimate_blender.py"), "--", str(tmp), str(target)],
                             env=env, capture_output=True, text=True)
        line = next((l for l in run.stdout.splitlines() if l.startswith("BLENDER ")), None)
        if run.returncode or line is None:
            fail("Blender did not decimate:\n" + run.stdout[-800:] + run.stderr[-800:])
        version = line.split()[1]
        out_pos, out_tris = np.load(tmp / "out_pos.npy"), np.load(tmp / "out_tris.npy")
    nearest = np.array([int(np.argmin(((pos - p) ** 2).sum(axis=1))) for p in out_pos])
    return out_pos, out_tris, w[nearest], uv[nearest], version


# ---------------------------------------------------------------- partition

def partition(pos, tris, w):
    """Step 5. Returns per-part (vertex ids, triangles) and the per-vertex joint weights."""
    nparts = w.shape[1]
    face_part = np.array([int(np.argmax(w[t].sum(axis=0))) for t in tris])
    for _ in range(RING_PASSES):  # a joint ring joins two parts: where three meet, the faces of the part with the least weight there go to the other two
        touching = [dict() for _ in range(len(pos))]
        for f, t in enumerate(tris):
            for v in t:
                touching[v].setdefault(int(face_part[f]), []).append(f)
        moved = False
        for v, parts in enumerate(touching):
            if len(parts) < 3:
                continue
            keep = sorted(parts, key=lambda p: (-w[v, p], p))[:2]
            for p, faces in parts.items():
                if p in keep:
                    continue
                for f in faces:
                    face_part[f] = max(keep, key=lambda q: (w[tris[f]][:, q].sum(), -q))
                    moved = True
        if not moved:
            break
    touching = [set() for _ in range(len(pos))]
    for f, t in enumerate(tris):
        for v in t:
            touching[v].add(int(face_part[f]))
    joints = np.zeros((len(pos), 2), dtype=np.int64)
    weights = np.zeros((len(pos), 2))
    for v, parts in enumerate(touching):
        if len(parts) == 1:
            joints[v, 0] = next(iter(parts))
            weights[v, 0] = 1.0
        else:
            two = sorted(parts, key=lambda p: (-w[v, p], p))[:2]
            joints[v] = two
            weights[v] = RING
    normals = np.zeros_like(pos)
    for t in tris:
        nrm = np.cross(pos[t[1]] - pos[t[0]], pos[t[2]] - pos[t[0]])  # area-weighted
        for v in t:
            normals[v] += nrm
    length = np.linalg.norm(normals, axis=1)
    normals /= np.where(length > 0, length, 1.0)[:, None]
    per_part = []
    for p in range(nparts):
        faces = tris[face_part == p]
        ids = sorted({int(v) for t in faces for v in t})
        per_part.append((ids, faces))
    return per_part, joints, weights, normals


# ---------------------------------------------------------------- glTF

def quat_mul(a, b):
    x1, y1, z1, w1 = a
    x2, y2, z2, w2 = b
    return (w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2, w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
            w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2, w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2)


def quat_conj(q):
    return (-q[0], -q[1], -q[2], q[3])


def quat_rotate(q, v):
    x, y, z, w = q
    u = np.array([x, y, z])
    return v + 2.0 * np.cross(u, np.cross(u, v) + w * v)


def quat_matrix(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def write_glb(plan, per_part, joints, weights, normals, pos, uv, provenance):
    blob = bytearray()
    views, accessors = [], []

    def add(array, ctype, ctype_name, target=None, bounds=False):
        while len(blob) % 4:
            blob.append(0)
        data = np.ascontiguousarray(array).tobytes()
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), **({"target": target} if target else {})})
        blob.extend(data)
        acc = {"bufferView": len(views) - 1, "componentType": ctype, "count": len(array), "type": ctype_name}
        if bounds:
            acc["min"] = [float(x) for x in array.min(axis=0)]
            acc["max"] = [float(x) for x in array.max(axis=0)]
        accessors.append(acc)
        return len(accessors) - 1

    FLOAT, USHORT, UINT = 5126, 5123, 5125
    primitives = []
    for p, (ids, faces) in enumerate(per_part):
        if not ids:
            fail(f"part {plan.name(p)} owns no vertices")
        local = {v: i for i, v in enumerate(ids)}
        j4 = np.zeros((len(ids), 4), dtype=np.uint16)
        w4 = np.zeros((len(ids), 4), dtype=np.float32)
        j4[:, :2] = joints[ids]
        w4[:, :2] = weights[ids]
        # a vertex with a single part has weight 0 in slot 1: its joint slot stays 0 with weight 0
        idx = np.array([[local[v] for v in t] for t in faces], dtype=np.uint32).reshape(-1)
        primitives.append({"attributes": {
            "POSITION": add(pos[ids].astype(np.float32), FLOAT, "VEC3", 34962, bounds=True),
            "NORMAL": add(normals[ids].astype(np.float32), FLOAT, "VEC3", 34962),
            "TEXCOORD_0": add(uv[ids].astype(np.float32), FLOAT, "VEC2", 34962),
            "JOINTS_0": add(j4, USHORT, "VEC4", 34962),
            "WEIGHTS_0": add(w4, FLOAT, "VEC4", 34962)},
            "indices": add(idx, UINT, "SCALAR", 34963), "mode": 4})

    # joint nodes: the plan's part tree, each node at its creation frame (local = parent^-1 * world)
    world_q = [tuple(p["rotation"]) for p in plan.parts]
    world_t = [np.array(p["position"]) for p in plan.parts]
    nodes, children = [], {}
    for p in range(len(plan.parts)):
        if p in plan.parent:
            parent = plan.parent[p]
            q = quat_mul(quat_conj(world_q[parent]), world_q[p])
            t = quat_rotate(quat_conj(world_q[parent]), world_t[p] - world_t[parent])
            children.setdefault(parent, []).append(p)
        else:
            q, t = world_q[p], world_t[p]
        nodes.append({"name": plan.name(p), "translation": [float(x) for x in np.float32(t)], "rotation": [float(x) for x in np.float32(q)]})
    for parent, kids in children.items():
        nodes[parent]["children"] = kids
    roots = [p for p in range(len(plan.parts)) if p not in plan.parent]
    inverse = np.zeros((len(plan.parts), 4, 4), dtype=np.float32)
    for p in range(len(plan.parts)):
        r = quat_matrix(world_q[p]).T  # inverse rotation
        m = np.eye(4)
        m[:3, :3] = r
        m[:3, 3] = -r @ world_t[p]
        inverse[p] = m.T  # glTF matrices are column-major
    ibm = add(inverse, FLOAT, "MAT4")
    nodes.append({"name": "Fella", "mesh": 0, "skin": 0})
    gltf = {
        "asset": {"version": "2.0", "generator": "tools/FellaMesh/build_fella_mesh.py", "extras": provenance},
        "scene": 0, "scenes": [{"nodes": roots + [len(nodes) - 1]}], "nodes": nodes,
        "meshes": [{"name": "Fella", "primitives": primitives}],
        "skins": [{"name": "Plan", "joints": list(range(len(plan.parts))), "inverseBindMatrices": ibm, "skeleton": roots[0]}],
        "buffers": [{"byteLength": len(blob)}], "bufferViews": views, "accessors": accessors,
    }
    js = json.dumps(gltf, sort_keys=True, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    while len(blob) % 4:
        blob.append(0)
    total = 12 + 8 + len(js) + 8 + len(blob)
    return b"glTF" + struct.pack("<II", 2, total) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(blob), b"BIN\0") + bytes(blob)


# ---------------------------------------------------------------- main

def build(args):
    for name, value in MACROS.items():
        if getattr(args, name) != value:
            fail(f"--{name} {getattr(args, name)}: only MakeHuman's neutral macro values ({name} {value}) are pinned; "
                 "other bodies wait for follow-up fella-body-variety, which pins the macrodetails targets")
    mh_v, mh_vt, faces, skeleton, mh_weights = load_pinned()
    plan = Plan(load_plan(args.plan))
    pos, tris, w, tri_uv = fit(plan, mh_v, faces, skeleton, mh_weights)
    uv = np.zeros((len(pos), 2))
    for t, tu in zip(tris, tri_uv):  # a vertex takes the UV of the first corner that names it (seams keep one island's)
        for v, c in zip(t, tu):
            if not uv[v].any():
                uv[v] = mh_vt[c]
    pos, tris, w, uv, blender = decimate(pos, tris, w, uv, TARGET_VERTICES)
    per_part, joints, weights, normals = partition(pos, tris, w)
    provenance = {"source": "makehumancommunity/makehuman", "commit": MAKEHUMAN_COMMIT, "license": "CC0 1.0",
                  "inputs": {k: v[1] for k, v in PINS.items()}, "macros": MACROS, "blender": blender}
    return write_glb(plan, per_part, joints, weights, normals, pos, uv, provenance), len(pos), blender


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=str(ROOT / "client" / "fella" / "fella_skin.glb"))
    ap.add_argument("--check", action="store_true", help="rebuild and compare bytes with the checked-in glb")
    ap.add_argument("--plan", help="a plan JSON file instead of running `baddaysim plan`")
    for name, value in MACROS.items():
        ap.add_argument(f"--{name}", type=float, default=value)
    args = ap.parse_args()
    data, vertices, blender = build(args)
    out = Path(args.out)
    if args.check:
        if blender != BLENDER_VERSION:
            fail(f"--check is only defined on Blender {BLENDER_VERSION}, the version the checked-in glb was built with; this run used Blender {blender}, so the bytes are not comparable (set BLENDER to a {BLENDER_VERSION} executable)")
        if not out.exists() or out.read_bytes() != data:
            fail(f"{out} differs from a rebuild")
        print(f"fella_skin.glb is the byte-identical rebuild ({len(data)} bytes, {vertices} decimated vertices)")
        return
    if blender != BLENDER_VERSION:
        print(f"warning: built with Blender {blender}, not the pinned {BLENDER_VERSION}: update BLENDER_VERSION and the input README, or --check will refuse", file=sys.stderr)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(data)
    print(f"wrote {out} ({len(data)} bytes, {vertices} decimated vertices)")


if __name__ == "__main__":
    main()
