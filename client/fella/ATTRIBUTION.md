# Fella's skin: attribution

`fella_skin.glb` is built by `tools/FellaMesh/build_fella_mesh.py` from three MakeHuman files, never edited by hand.

- Source: MakeHuman (https://github.com/makehumancommunity/makehuman), commit `a8bc2d54ff0ac92e78ff71431b1023eda42bf482`: the base mesh (`base.obj`), the default skeleton (`default.mhskel`) and its skin weights (`default_weights.mhw`). Pinned copies and SHA-256 sums: `tools/FellaMesh/input/README.md`.
- Licence: CC0 1.0 Universal. MakeHuman released these assets to the public domain in September 2020; copyright holders at that point: Data Collection AB, Joel Palmius, Jonas Hauquier. No attribution is required; it is given here because the file's shape is theirs.
- What the build does to them: keeps the body group (drops the helper, joint, eye, teeth and tongue groups), scales it to the plan's height, poses it into Lodur's 19-part creation pose with MakeHuman's own weights, stretches each part's segment to the plan's length, decimates the whole body to about 450 vertices with Blender's Decimate modifier (Blender 5.3.0, GPL, used as a tool: no Blender code or data is in the file), splits it into one primitive per part with shared two-part joint rings, and writes glTF. Vertices, normals and weights are the build's; UVs are MakeHuman's, one per vertex.
