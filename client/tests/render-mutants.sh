#!/usr/bin/env bash
# The render check's mutants, literal edits: build the client, run the windowed RenderCheck (it needs a display; about 2.5 minutes a mutant) and require the
# failure line the mutant must print. The mutants are the ones the sample floors and bounds of RenderCheck.cs were derived against. A "survives" mutant is one the check
# cannot tell from the shipped doll; it must still pass, so that the list stays true if the check changes. Two do: a doubled kernel (the kernel is nonzero only in the 2 px
# bands, and doubling it moves the whole-interior statistics less than the shipped views differ among themselves), and a stale crease (PuppetSkin keeping the first pose's creases:
# every edge lies inside one rigid piece, so a crease never changes with the pose and the mutant draws the same puppet).
# Refuses a dirty tree; restores every file it edits. Every edit must apply exactly once, by itself (not "the file changed": one edit of a mutant applying does not vouch for its
# others), whatever the file's line endings (a newline in an old text matches LF or CRLF; a new text takes the file's): an edit that does not aborts the whole run, status 3,
# before anything is built, since a mutant built from a half-applied edit is some other program and its verdict means nothing.
#   GODOT=<godot console exe> bash client/tests/render-mutants.sh (ONLY="name name" limits the run, LOG=<dir> keeps the logs)
set -u
cd "$(dirname "$0")/../.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "refusing: the tree is dirty"; exit 2; }
LOG="${LOG:-$(mktemp -d)}"; failed=0; ran=0; EDITED=()

# edit <file> <old text> <new text>: one literal replacement, recorded for the mutant that follows; the old text must occur exactly once
edit() {
  EDITED+=("$1")
  if ! OLD="$2" NEW="$3" perl -0pe 'BEGIN { binmode STDIN; binmode STDOUT } $c = /\r\n/ ? "\r\n" : "\n"; ($o, $n) = ($ENV{OLD}, $ENV{NEW}); $n =~ s/\n/$c/g;
      $re = join(q{\r?\n}, map { quotemeta } split(/\n/, $o, -1)); $k = () = /$re/g; if ($k != 1) { print STDERR "$k matches\n"; exit 1 } s/$re/$n/' "$1" > "$1.edited"; then
    rm -f "$1.edited"; echo "ABORT: an edit of $1 applied to nothing or more than once: ${2:0:70}"; restore; exit 3
  fi
  mv "$1.edited" "$1"
}

# mutant <name> <substring the check must print, or SURVIVES (it must pass), or SHIPPED (no edit; it must pass)>, after the edits it is made of
mutant() {
  local name="$1" want="$2" f
  if [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $name "* ]]; then restore; return; fi
  ran=$((ran + 1))
  if ! dotnet build client/BadDay.Client.csproj -v q > "$LOG/$name.build" 2>&1; then
    echo "FAIL  $name: does not build"; failed=$((failed + 1)); restore; return
  fi
  (cd client && timeout 900 "$GODOT" --path . --rendering-driver opengl3 res://tests/RenderCheck.tscn > "$LOG/$name.log" 2>&1)
  local code=$?
  restore
  if [ "$want" = SHIPPED ] || [ "$want" = SURVIVES ]; then
    if [ $code -eq 0 ]; then echo "ok    $name: passes, as it must"; else echo "FAIL  $name: must pass and exited $code (log $LOG/$name.log)"; failed=$((failed + 1)); fi
  elif [ $code -ne 0 ] && grep -qF -- "$want" "$LOG/$name.log"; then echo "ok    $name: caught ($want)"
  else echo "FAIL  $name: exit $code without '$want' (log $LOG/$name.log)"; failed=$((failed + 1)); fi
}
restore() { local f; for f in "${EDITED[@]+"${EDITED[@]}"}"; do git checkout -q -- "$f"; done; EDITED=(); }

S=client/puppet.gdshader; L=client/Look.cs; P=client/project.godot; K=client/PuppetSkin.cs
mutant shipped SHIPPED
# the glaze's prefilter
edit $S 'dFdy(n), dFdy(n))), MAX_SPREAD);' 'dFdy(n), dFdy(n))), 0.0);';                     mutant kernel-0 "highlights flicker in the"
edit $S 'const float PIXEL_VARIANCE = 1.0 / 24.0;' 'const float PIXEL_VARIANCE = 1.0 / 12.0;'; mutant doubled-kernel SURVIVES
edit $S 'const float PIXEL_VARIANCE = 1.0 / 24.0;' 'const float PIXEL_VARIANCE = 0.15915494;'; mutant paper-sigma "view's highlights are"
edit $P 'msaa_3d=2' 'msaa_3d=0';                                                             mutant no-msaa "silhouette is"
edit $L 'GlazeStrength = 1f' 'GlazeStrength = 4f';                                           mutant glaze-x4 "view's highlights are"
edit $L 'GlazeStrength = 1f' 'GlazeStrength = 0f';                                           mutant glaze-off "there is no glaze"
# the bevel
edit $S 'n = normalize(n - (1.0 - corner) * tilt);' 'n = normalize(n);';                     mutant no-tilt "bevel is not where"
edit $S 'float s = 1.0 - smoothstep(0.0, width[k], edge[k]);' 'float s = 1.0 - smoothstep(0.0, 2.0 * width[k], edge[k]);'; mutant widened-smoothstep "drawn shading normal is"
edit $S 'float s = 1.0 - smoothstep(0.0, width[k], edge[k]);' 'float s = 1.0 - clamp(edge[k] / max(width[k], 1e-9), 0.0, 1.0);'; mutant linear-falloff "bevel is not where"
edit $S 'bevel_min), 0.5 * edge.w);' 'bevel_min), 1e9);';                                    mutant no-width-clamp "the cap is not where the shader puts it"
edit $S 'bevel_screen * VIEWPORT_SIZE.y * metres_per_pixel' 'bevel_screen * 720.0 * metres_per_pixel'; mutant width-720 "bevel is not where"
edit $S 'float reach = 1.5 * max(width[i], width[j]);' 'float reach = 1.0 * max(width[i], width[j]);'; mutant corner-reach-1 "corner blend"
edit $S 'tan(clamp(crease[k], -1.4, 1.4))' 'tan(clamp(crease[k], -1.0, 1.0))';               mutant crease-clamp-1 "the crease clamp is not where"
edit $S 'vertex_normal = (MODELVIEW_MATRIX * vec4(CUSTOM3.xyz, 0.0)).xyz;' 'vertex_normal = CUSTOM3.xyz;'; mutant vertex-normal-model "vertex normal is in the wrong space"
edit $K $'            Crease[slot] = Crease[other] = convexHere ? half : -half;\n        }\n    }' $'            if (!_fr) Crease[slot] = Crease[other] = convexHere ? half : -half;\n        }\n        _fr = true;\n    }'
edit $K '    private static float Altitude3' $'    private bool _fr;\n    private static float Altitude3';  mutant stale-crease SURVIVES
# the glaze and the lights
edit $S 'vec3 r = reflect(-VIEW, n);' 'vec3 r = reflect(-VIEW, n) * vec3(-1.0, 1.0, 1.0);'; mutant glaze-mirrored "not one studio"
edit $S 'vec3 r = reflect(-VIEW, n);' 'vec3 r = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, n), 0.0)).xyz;'; mutant glaze-world "not one studio"
edit $L 'GlazePool = 0.8f' 'GlazePool = 0f';                                                 mutant glaze-pool-0 "there is no pooling"
edit $L 'GlowLightEnergy = 0.1f' 'GlowLightEnergy = 3f';                                     mutant glow-3 "the facets cannot read"

echo "$ran mutants run, $failed not as expected (logs in $LOG)"
[ $failed -eq 0 ]
