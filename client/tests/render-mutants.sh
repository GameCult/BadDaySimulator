#!/usr/bin/env bash
# Breaks the shipped look once per claim of RenderCheck and wants that claim's line to fail. Each mutant is one set of edits, each of which must
# apply exactly once; the file is restored whatever happens (the end, a failed edit or build, an interrupt: a trap restores it once the build or check in flight returns, and a leftover .orig
# from a run that died harder is refused at the start). Needs a window (RenderCheck draws). Run from anywhere:
#   GODOT=<godot console exe> bash client/tests/render-mutants.sh              # every claimed mutant must fail its own line
#   GODOT=<godot console exe> UNCLAIMED=1 bash client/tests/render-mutants.sh  # the unclaimed ones below must still pass (their readings are the record)
# A mutant's edits are old@@old|new@@new pairs in one file; the line it must fail is the RenderCheck check named in its last field.
#
# Readings (shipped / mutant) of the claims, from RenderCheck's bounds, which sit at the geometric midpoint of shipped worst and least mutant
# (lodur:ruling:deterministic-bound-margin); a zero reading is counted as lodur:ruling:deterministic-bound-zero-reading says (shipped 0 as half a unit, mutant 0 as one, then the geometric mean).
# The dither line reads three flat fields that hold every threshold of the 4x4 matrix, so the 2x2 matrix and the transposed one fail it; the fog line probes
# 6, 9 and 12 m against the literals 4 and 14 m, so a linear ramp and changed FogNear or FogFar fail it. Round-to-nearest in the post pass fails the dither line.
#
# UNCLAIMED, recorded as the ruling requires (no RenderCheck line separates them; the mutants are in UNCLAIMED below):
#   fog-per-pixel: fog from the interpolated view depth, in the fragment, instead of per vertex. The fog probe is a quad at one depth, where the
#     two agree to the bit; a surface spanning FogNear to FogFar would separate them (smoothstep of the depth against the blend of the corners'
#     smoothsteps, about 20 levels at the quarter point), but ps1.js's per-vertex fog is not among the claims the cut was ordered to guard.
set -u
cd "$(dirname "$0")/.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"

# name | file | old@@old | new@@new | the RenderCheck line it must fail
MUTANTS=(
'post-off|ps1_post.gdshader|COLOR = vec4(floor(c / 8.0) * 8.0 / 255.0, 1.0);|COLOR = vec4(c / 255.0, 1.0);|15-bit'
'twelve-bit|ps1_post.gdshader|COLOR = vec4(floor(c / 8.0) * 8.0 / 255.0, 1.0);|COLOR = vec4(floor(c / 16.0) * 16.0 / 255.0, 1.0);|15-bit'
'dither-off|ps1_post.gdshader|c = clamp(c + D[q.x][q.y], 0.0, 255.0);|c = clamp(c, 0.0, 255.0);|dither'
'dither-bayer2|ps1_post.gdshader|const mat4 D = mat4(vec4(-4., 0., -3., 1.), vec4(2., -2., 3., -1.), vec4(-3., 1., -4., 0.), vec4(3., -1., 2., -2.));|const mat4 D = mat4(vec4(-4., 0., -4., 0.), vec4(2., -2., 2., -2.), vec4(-4., 0., -4., 0.), vec4(2., -2., 2., -2.));|dither'
'dither-transposed|ps1_post.gdshader|c = clamp(c + D[q.x][q.y], 0.0, 255.0);|c = clamp(c + D[q.y][q.x], 0.0, 255.0);|dither'
'post-rounds|ps1_post.gdshader|COLOR = vec4(floor(c / 8.0) * 8.0 / 255.0, 1.0);|COLOR = vec4(floor(c / 8.0 + 0.5) * 8.0 / 255.0, 1.0);|dither'
'block-off|ps1_post.gdshader|textureLod(screen_tex, (size * 0.5 + (b + 0.5) * s) / size, 0.0)|textureLod(screen_tex, SCREEN_UV, 0.0)|blocks'
'lines-360|Look.cs|public const int Lines = 240;|public const int Lines = 360;|lines'
'lines-120|Look.cs|public const int Lines = 240;|public const int Lines = 120;|lines'
'snap-off|ps1.gdshader|if (clip.w > 0.05) {|if (false) {|snap'
'perspective-uv|ps1.gdshader|auv = vec3(uv * clip.w, clip.w);|auv = vec3(uv, 1.0);|affine'
'per-pixel-light|ps1.gdshader|varying vec3 light;@@light = emissive ? vec3(1.0) : ps1_ambient + ps1_key_color * max(dot(n, ps1_key_dir), 0.0) + glow;@@vec3 c = texture(albedo_tex, auv.xy / auv.z).rgb * tint * light;|varying vec3 light; varying vec3 nrm;@@nrm = n; light = vec3(1.0);@@vec3 c = texture(albedo_tex, auv.xy / auv.z).rgb * tint * (emissive ? vec3(1.0) : ps1_ambient + ps1_key_color * max(dot(normalize(nrm), ps1_key_dir), 0.0));|light'
'fog-off|ps1.gdshader|fog = smoothstep(ps1_fog_near, ps1_fog_far, -mv.z);|fog = 0.0;|fog'
'fog-linear|ps1.gdshader|fog = smoothstep(ps1_fog_near, ps1_fog_far, -mv.z);|fog = clamp((-mv.z - ps1_fog_near) / (ps1_fog_far - ps1_fog_near), 0.0, 1.0);|fog'
'fog-far-18|Look.cs|FogNear = 4f, FogFar = 14f;|FogNear = 4f, FogFar = 18f;|fog'
'fog-near-6|Look.cs|FogNear = 4f, FogFar = 14f;|FogNear = 6f, FogFar = 14f;|fog'
'fog-centred-wide|Look.cs|FogNear = 4f, FogFar = 14f;|FogNear = 2f, FogFar = 16f;|fog'
'mipmapped|ps1.gdshader|filter_nearest, repeat_enable|filter_nearest_mipmap, repeat_enable|mips'
'linear-filter|ps1.gdshader|filter_nearest, repeat_enable|filter_linear, repeat_enable|mips'
'glow-unset|Chamber.cs|Look.GlowLightEnergy * glow|0f|glow'
'carrier-unposed|BodyRenderer.cs|node.Transform = new Transform3D(new Basis(new Quaternion(rx[i]|_ = new Transform3D(new Basis(new Quaternion(rx[i]|figure at lanes'
)

UNCLAIMED_MUTANTS=(
'fog-per-pixel|ps1.gdshader|fog = smoothstep(ps1_fog_near, ps1_fog_far, -mv.z);@@c = mix(c, ps1_fog_color, fog);|fog = -mv.z;@@c = mix(c, ps1_fog_color, smoothstep(ps1_fog_near, ps1_fog_far, fog));|'
)

ACTIVE=""
restore() {
  if [ -n "$ACTIVE" ] && [ -f "$ACTIVE.orig" ]; then mv -f "$ACTIVE.orig" "$ACTIVE"; touch "$ACTIVE"; fi
  ACTIVE=""
}
trap restore EXIT
trap 'restore; exit 130' INT TERM

left="$(find . -maxdepth 1 -name '*.orig')"
if [ -n "$left" ]; then echo "an earlier run died with its edit in place: restore these from their .orig first: $left"; exit 2; fi

status=0
if [ "${UNCLAIMED:-}" = 1 ]; then LIST=("${UNCLAIMED_MUTANTS[@]}"); else LIST=("${MUTANTS[@]}"); fi
for m in "${LIST[@]}"; do
  IFS='|' read -r name file old new want <<<"$m"
  cp "$file" "$file.orig"
  ACTIVE="$file"
  if ! python - "$file" "$old" "$new" <<'PY'
import sys
path, old, new = sys.argv[1:4]
raw = open(path, newline='').read()
text = raw.replace('\r\n', '\n')
olds, news = old.split('@@'), new.split('@@')
if len(olds) != len(news):
    sys.exit("old and new have different numbers of edits")
for o, n in zip(olds, news):
    if text.count(o) != 1:
        sys.exit(f"{path}: the edit {o!r} applies {text.count(o)} times, not once")
    text = text.replace(o, n)
open(path, 'w', newline='').write(text.replace('\n', '\r\n') if '\r\n' in raw else text)
PY
  then restore; echo "MUTANT $name: edit did not apply once; aborting"; exit 2; fi
  build="$(dotnet build BadDay.Client.csproj 2>&1)"
  if ! grep -q "Build succeeded" <<<"$build"; then restore; echo "MUTANT $name: does not build; aborting"; exit 2; fi
  out="$("$GODOT" --path . res://tests/RenderCheck.tscn 2>&1)"
  restore
  readings="$(grep '^RENDER ' <<<"$out" | grep -v 'FAIL\|PASS\|FAILED' | sed 's/^RENDER //' | tr '\n' ';')"
  if [ "${UNCLAIMED:-}" = 1 ]; then
    if grep -q "^RENDER PASS" <<<"$out"; then echo "UNCLAIMED $name: RENDER PASS as recorded  [$readings]"
    else echo "UNCLAIMED $name: something fails now, so it is claimed by that line; move it  [$(grep '^RENDER FAIL' <<<"$out" | tr '\n' ';')]"; status=1; fi
  elif grep -q "^RENDER FAIL $want:" <<<"$out"; then echo "MUTANT $name: fails '$want' as wanted  [$readings]"
  else echo "MUTANT $name: DOES NOT FAIL '$want'  [$readings]"; status=1; fi
done
build="$(dotnet build BadDay.Client.csproj 2>&1)"
grep -q "Build succeeded" <<<"$build" || { echo "the shipped code does not build after the run"; status=1; }
exit $status
