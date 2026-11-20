#!/usr/bin/env bash
# The render check's mutants, one literal edit each: build the client, run the windowed RenderCheck (it needs a display; about 2.5 minutes a mutant) and require the
# failure line the mutant must print. The mutants are the ones the sample floors and bounds of RenderCheck.cs were derived against. A "survives" mutant is one the check
# cannot tell from the shipped doll (the cut report says why); it must still pass, so that the list stays true if the check changes. A stale crease (PuppetSkin keeping the first pose's creases) survives too and
# has no line here: every edge lies inside one rigid piece, so a crease never changes with the pose and the mutant draws the same puppet. Refuses a dirty tree; restores every file it edits.
#   GODOT=<godot console exe> bash client/tests/render-mutants.sh (ONLY="name name" limits the run, LOG=<dir> keeps the logs)
set -u
cd "$(dirname "$0")/../.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "refusing: the tree is dirty"; exit 2; }
LOG="${LOG:-$(mktemp -d)}"; failed=0; ran=0

# mutant <name> <file or -> <old text> <new text> <substring the check must print, or SURVIVES (it must pass), or SHIPPED (no edit; it must pass)>
mutant() {
  local name="$1" file="$2" old="$3" new="$4" want="$5"
  if [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $name "* ]]; then return; fi
  ran=$((ran + 1))
  if [ "$file" != - ]; then
    OLD="$old" NEW="$new" perl -0pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/' "$file"
    if git diff --quiet -- "$file"; then echo "FAIL  $name: the edit changed nothing"; failed=$((failed + 1)); return; fi
  fi
  if ! dotnet build client/BadDay.Client.csproj -v q > "$LOG/$name.build" 2>&1; then
    echo "FAIL  $name: does not build"; failed=$((failed + 1)); [ "$file" != - ] && git checkout -q -- "$file"; return
  fi
  (cd client && timeout 900 "$GODOT" --path . --rendering-driver opengl3 res://tests/RenderCheck.tscn > "$LOG/$name.log" 2>&1)
  local code=$?
  [ "$file" != - ] && git checkout -q -- "$file"
  if [ "$want" = SHIPPED ] || [ "$want" = SURVIVES ]; then
    if [ $code -eq 0 ]; then echo "ok    $name: passes, as it must"; else echo "FAIL  $name: must pass and exited $code (log $LOG/$name.log)"; failed=$((failed + 1)); fi
  elif [ $code -ne 0 ] && grep -qF -- "$want" "$LOG/$name.log"; then echo "ok    $name: caught ($want)"
  else echo "FAIL  $name: exit $code without '$want' (log $LOG/$name.log)"; failed=$((failed + 1)); fi
}

S=client/puppet.gdshader; L=client/Look.cs; P=client/project.godot
mutant shipped             -   ""  ""  SHIPPED
# the prefilter: the check cannot see these on the doll (flat facets, a 2 px bevel: the kernel is nonzero in the bands only)
mutant kernel-0            $S 'dFdy(n), dFdy(n)), MAX_SPREAD);' 'dFdy(n), dFdy(n)), 0.0);' SURVIVES
mutant doubled-kernel      $S 'const float PIXEL_VARIANCE = 1.0 / 24.0;' 'const float PIXEL_VARIANCE = 1.0 / 12.0;' SURVIVES
mutant paper-sigma         $S 'const float PIXEL_VARIANCE = 1.0 / 24.0;' 'const float PIXEL_VARIANCE = 0.15915494;' "view's highlights are"
mutant no-msaa             $P 'msaa_3d=2' 'msaa_3d=0' "silhouette is"
mutant glaze-x4            $L 'GlazeStrength = 1f' 'GlazeStrength = 4f' "view's highlights are"
# the bevel
mutant no-tilt             $S 'n = normalize(n - (1.0 - corner) * tilt);' 'n = normalize(n);' "bevel is not where"
mutant widened-smoothstep  $S 'float s = 1.0 - smoothstep(0.0, width[k], edge[k]);' 'float s = 1.0 - smoothstep(0.0, 2.0 * width[k], edge[k]);' "drawn shading normal is"
mutant linear-falloff      $S 'float s = 1.0 - smoothstep(0.0, width[k], edge[k]);' 'float s = 1.0 - clamp(edge[k] / max(width[k], 1e-9), 0.0, 1.0);' "bevel is not where"
mutant no-width-clamp      $S 'bevel_min), 0.5 * edge.w);' 'bevel_min), 1e9);' "the cap is not where the shader puts it"
mutant width-720           $S 'bevel_screen * VIEWPORT_SIZE.y * metres_per_pixel' 'bevel_screen * 720.0 * metres_per_pixel' "bevel is not where"
mutant corner-reach-1      $S 'float reach = 1.5 * max(width[i], width[j]);' 'float reach = 1.0 * max(width[i], width[j]);' "corner blend"
mutant crease-clamp-1      $S 'tan(clamp(crease[k], -1.4, 1.4))' 'tan(clamp(crease[k], -1.0, 1.0))' "the crease clamp is not where"
mutant vertex-normal-model $S 'vertex_normal = (MODELVIEW_MATRIX * vec4(CUSTOM3.xyz, 0.0)).xyz;' 'vertex_normal = CUSTOM3.xyz;' "vertex normal is in the wrong space"
# the glaze and the lights
mutant glaze-mirrored      $S 'vec3 r = reflect(-VIEW, n);' 'vec3 r = reflect(-VIEW, n) * vec3(-1.0, 1.0, 1.0);' "not one studio"
mutant glaze-world         $S 'vec3 r = reflect(-VIEW, n);' 'vec3 r = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, n), 0.0)).xyz;' "not one studio"
mutant glaze-pool-0        $L 'GlazePool = 0.8f' 'GlazePool = 0f' "there is no pooling"
mutant glow-3              $L 'GlowLightEnergy = 0.1f' 'GlowLightEnergy = 3f' "the facets cannot read"

echo "$ran mutants run, $failed not as expected (logs in $LOG)"
[ $failed -eq 0 ]
