#!/usr/bin/env bash
# Breaks the shipped look once per claim of RenderCheck and wants that claim's line to fail. Each mutant is one edit that must apply
# exactly once; the file is restored whatever happens. Needs a window (RenderCheck draws). Run from anywhere:
#   GODOT=<godot console exe> bash client/tests/render-mutants.sh
set -u
cd "$(dirname "$0")/.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"

# name | file | old | new | the RenderCheck line it must fail
MUTANTS=(
'post-off|ps1_post.gdshader|COLOR = vec4(floor(c / 8.0) * 8.0 / 255.0, 1.0);|COLOR = vec4(c / 255.0, 1.0);|15-bit'
'block-off|ps1_post.gdshader|textureLod(screen_tex, (size * 0.5 + (b + 0.5) * s) / size, 0.0)|textureLod(screen_tex, SCREEN_UV, 0.0)|blocks'
'snap-off|ps1.gdshader|if (clip.w > 0.05) {|if (false) {|snap'
'perspective-uv|ps1.gdshader|auv = vec3(uv * clip.w, clip.w);|auv = vec3(uv, 1.0);|affine'
'glow-unset|Chamber.cs|Look.GlowLightEnergy * glow|0f|glow'
'carrier-unposed|BodyRenderer.cs|node.Transform = new Transform3D(new Basis(new Quaternion(rx[i]|_ = new Transform3D(new Basis(new Quaternion(rx[i]|figure at lanes'
)

status=0
for m in "${MUTANTS[@]}"; do
  IFS='|' read -r name file old new want <<<"$m"
  cp "$file" "$file.orig"
  restore() { mv -f "$file.orig" "$file"; }
  if ! python - "$file" "$old" "$new" <<'PY'
import sys
path, old, new = sys.argv[1:4]
raw = open(path, newline='').read()
text = raw.replace('\r\n', '\n')
if text.count(old) != 1:
    sys.exit(f"{path}: the edit applies {text.count(old)} times, not once")
text = text.replace(old, new)
open(path, 'w', newline='').write(text.replace('\n', '\r\n') if '\r\n' in raw else text)
PY
  then restore; echo "MUTANT $name: edit did not apply once; aborting"; exit 2; fi
  if ! dotnet build BadDay.Client.csproj 2>&1 | grep -q "Build succeeded"; then restore; echo "MUTANT $name: does not build; aborting"; exit 2; fi
  out="$("$GODOT" --path . res://tests/RenderCheck.tscn 2>&1)"
  restore
  readings="$(grep '^RENDER ' <<<"$out" | grep -v 'FAIL\|PASS\|FAILED' | sed 's/^RENDER //' | tr '\n' ';')"
  if grep -q "^RENDER FAIL $want:" <<<"$out"; then echo "MUTANT $name: fails '$want' as wanted  [$readings]"
  else echo "MUTANT $name: DOES NOT FAIL '$want'  [$readings]"; status=1; fi
done
dotnet build BadDay.Client.csproj 2>&1 | grep -q "Build succeeded" || { echo "the shipped code does not build after the run"; status=1; }
exit $status
