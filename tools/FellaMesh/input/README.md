# Pinned MakeHuman inputs

The three files `tools/FellaMesh/build_fella_mesh.py` builds `client/fella/fella_skin.glb` from. They are data: the build reads them, nothing runs them.

| File | Bytes | SHA-256 |
| --- | --- | --- |
| `base.obj` | 1,749,303 | `8e761e6624b8f54536409135d1636da63b32486a90d4897f84e121d144f6fb4c` |
| `default.mhskel` | 117,790 | `99f179bce0aa850b45d4191a1d0d234c5851f881c057439470ded3bddf729a24` |
| `default_weights.mhw` | 897,521 | `0f3641d651ae3d00ad6b4ccee43142edb109d3bd909d27d9e4139ef1beed8625` |

- Source: https://github.com/makehumancommunity/makehuman at commit `a8bc2d54ff0ac92e78ff71431b1023eda42bf482`, paths `makehuman/data/3dobjs/base.obj`, `makehuman/data/rigs/default.mhskel`, `makehuman/data/rigs/default_weights.mhw`.
- Licence: CC0 1.0 (the files state it; the repository's `LICENSE.ASSETS.md` is the grant). Copyright holders at release: Data Collection AB, Joel Palmius, Jonas Hauquier.
- Fetched 2026-11-28 into an empty directory, size-checked against the table, hashed, then copied here byte for byte (`.gitattributes` keeps git from touching their line endings). The build refuses any file whose size or SHA-256 differs.
- Build tool, pinned: **Blender 5.3.0** (the locally installed Steam build; `BLENDER` names another executable). The build runs it headless with private config, extension and script folders (never the user's), for one job: the Decimate modifier (Collapse, symmetry on X, triangulate) on the posed body, `decimate_blender.py`. Its result is only reproducible on that version, so `build_fella_mesh.py --check` refuses any other and says so; a build on another version warns, and the pin and this line move together.
- Not fetched: MakeHuman's macrodetails targets. The default Fella is the base mesh at MakeHuman's neutral macro values (gender 0.5, age 0.5, muscle 0.5, weight 0.5, height 0.5, proportions 0.5; lodur:ruling:default-fella-androgynous-base), which is the mesh with no target applied. Other bodies (lodur:follow_up:fella-body-variety) pin the targets they need here first.
