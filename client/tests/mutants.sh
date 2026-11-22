#!/usr/bin/env bash
# The puppet's mutants, one sed edit each: build the client, run the headless smoke (PuppetChecks inside), and require the failure line the mutant must print
# (or, for a mutant the checks must not mind, a pass). Refuses a dirty tree; restores every file it edits.
#   GODOT=<godot console exe> bash client/tests/mutants.sh (ONLY="name name" limits the run)
set -u
cd "$(dirname "$0")/../.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "refusing: the tree is dirty"; exit 2; }
LOG="${LOG:-$(mktemp -d)}"; failed=0; ran=0

# mutant <name> <file> <sed expression> <substring the smoke must print, or PASS> [substring it must not print] [a second substring it must print]
mutant() {
  local name="$1" file="$2" expr="$3" want="$4" absent="${5:-}" also="${6:-}"
  if [ -n "${ONLY:-}" ] && [[ " $ONLY " != *" $name "* ]]; then return; fi
  ran=$((ran + 1))
  sed -i "$expr" "$file"
  if git diff --quiet -- "$file"; then echo "FAIL  $name: the edit changed nothing"; failed=$((failed + 1)); return; fi
  if ! dotnet build client/BadDay.Client.csproj -v q > "$LOG/$name.build" 2>&1; then
    echo "FAIL  $name: does not build"; failed=$((failed + 1)); git checkout -q -- "$file"; return
  fi
  timeout 900 "$GODOT" --headless --path client res://tests/Smoke.tscn > "$LOG/$name.log" 2>&1
  local code=$?
  git checkout -q -- "$file"
  if [ "$want" = PASS ]; then
    if [ $code -eq 0 ]; then echo "ok    $name: passes, as it must"; else echo "FAIL  $name: must pass and exited $code"; failed=$((failed + 1)); fi
  elif [ -n "$absent" ] && grep -qF -- "$absent" "$LOG/$name.log"; then echo "FAIL  $name: printed '$absent', which it must not (log $LOG/$name.log)"; failed=$((failed + 1))
  elif [ -n "$also" ] && ! grep -qF -- "$also" "$LOG/$name.log"; then echo "FAIL  $name: did not print '$also' (log $LOG/$name.log)"; failed=$((failed + 1))
  elif [ $code -ne 0 ] && grep -qF -- "$want" "$LOG/$name.log"; then echo "ok    $name: caught ($want)"
  else echo "FAIL  $name: exit $code without '$want' (log $LOG/$name.log)"; failed=$((failed + 1)); fi
}

# A mutant is kept only if a semantic check fails on the line its want names. The pin (ExistingPiecesUnchanged) fails on any change, so a want only the pin satisfies proves the mesh changed and no more:
# a knob change that still yields a valid mesh is not a mutant. Only facet-error-19mm and thinbelow-8cm claim the pin, to show it sees a 1 mm knob change.
F=client/PuppetField.cs; D=client/PuppetDoll.cs
mutant seam-10mm            $D 's/Seam = 0.002f/Seam = 0.010f/'                 "from its ball"
mutant seat-band-0          $D 's/SeatBand = 0.015f/SeatBand = 0f/'             "more than 15 mm beyond the ball"
mutant seat-error-40mm      $D 's/SeatError = 0.005f/SeatError = 0.040f/'       "a ball joint shows slits"
mutant ball-bone-child      $F 's/new JointBall(n + j, joint.A, o, /new JointBall(n + j, joint.B, o, /' "do not follow their joint's parent"
mutant ball-bone-root       $F 's/new JointBall(n + j, joint.A, o, /new JointBall(n + j, 0, o, /'       "do not follow their joint's parent"
mutant seam-0               $D 's/Seam = 0.002f/Seam = 0f/'                     "less than half the seam inside a socket"
# The hands: sockets 10 mm off their balls, a palm with no entry, ThinError off (a thin body's free end departs from its silhouette), the thin rule moved
mutant hands-seam-10mm      $D 's/Seam = 0.002f/Seam = 0.010f/'                 "stop short of it"
mutant no-palm-entry        $D 's/new(Box3DSoaKind.Box, V(.014f, .045f, .04f), new\[\]/new(Box3DSoaKind.Box, V(.015f, .045f, .04f), new[]/' "of the palm bodies are drawn as their fallback"
mutant thin-error-off       $D 's/ThinError = 0.25f/ThinError = 100f/' "silhouette differs"
mutant facet-error-19mm     $D 's/FacetError = 0.02f/FacetError = 0.019f/'       "the hands must not change changed"
# The primitives (ruling doll-joints-built-primitives): the straddle off (the lattice inscribed: its face centres 7.5% inside), the frequency 2 (8 sides, 32 triangles), a roll with no length.
mutant ball-inscribed       $F 's#radius \* 2f / (1f + k)#radius#' "ball faces lie more than"
mutant ball-freq-2          $D 's/BallFrequency = 3/BallFrequency = 2/' "are not 38 vertices and 72 triangles"
mutant roll-sphere          $D 's/0.015f, V(0, 0, 1), 0.028f)/0.015f, V(0, 0, 1), 0f)/' "not its two knuckle rolls"
mutant body-seam-10mm       $D 's/Seam = 0.002f/Seam = 0.010f/'                             "a ball joint shows slits"
# Joints are built primitives, never meshed (ruling doll-joints-built-primitives): a ball routed through the decimator; the thin rule moved onto the bodies of 80 mm (ThinBelow); the seat limit without the thin error (Allowed's minimum).
mutant ball-decimated       $F 's#public (float3\[\] V, int\[\] Tris) Mesh() => PuppetField.Primitive(Radius, Axis, math.length(Roll), Doll.BallFrequency);#public (float3[] V, int[] Tris) Mesh() { var (v, t) = PuppetField.Primitive(Radius, Axis, math.length(Roll), Doll.BallFrequency); return PuppetDecimate.Run(v, t, Doll.FacetAlpha, Doll.FacetScale, new DecimateRules((p, tight) => true, p => false, p => 1f, System.Array.Empty<(float3 P, float Clear)>())); }#' "are not 38 vertices and 72 triangles"
mutant thinbelow-8cm        $D 's/ThinBelow = 0.04f/ThinBelow = 0.08f/' "the hands must not change changed"
mutant allowed-no-min       $F 's/(tight ? MathF.Min(Error, SeatLimit(p)) : Error)/(tight ? SeatLimit(p) : Error)/' "stray further from their piece's field"
# The decimator bounds the error both ways (ruling decimator-two-sided-error): the one-sided rule drops a lip thinner than the error; a palm 15 mm short leaves a knuckle roll's end bare.
mutant decimator-one-sided  client/PuppetDecimate.cs 's/ \&\& Hugs(x, fan, u, v))/)/' "of their error (worst"
mutant palm-short-z         $D 's/V(.014f, .0265f, .04f)/V(.014f, .0265f, .025f)/' "a roll's end is bare"
# The boolean's cutter and tidy: no countersink (a plain ball plus shadow: knife lips), no shadow ring, the cutter lattice turned 15 degrees about its axis, no tidy, B's inside-ness inverted (an open cut), a collapse onto the midpoint (fragments leave their planes).
mutant sink-off             $D 's/SinkAngle = 35f \* (MathF.PI \/ 180f)/SinkAngle = 0f/' "of socket rim meets at under"
mutant shadow-off           $F 's/points.Add(p0); points.Add(p0 + w \* far);/points.Add(p0);/' "lie further from its mesh than"
mutant cutter-turned        $F 's/Doll.BallFrequency, U);/Doll.BallFrequency, U * 0.9659f + math.cross(Axis, U) * 0.2588f);/' "less than half the seam inside a socket"
# A socket's line is the piece's own, authored (ruling doll-socket-away-authored): not the arm's line in the bind; a roll's across the roll
mutant shoulder-line-bind   $D 's/ParentLine: V(1, 0, 0)/ParentLine: V(0, -1, 0)/' "more than 15 mm beyond the ball"
mutant roll-line-tilted     $D '/across the roll/d;s/0.015f, V(0, 0, 1), 0.028f)/0.015f, V(0, 0, 1), 0.028f, Line: V(0, 1, 0.0524f))/' "the thinnest source triangle has an altitude of"
mutant tidy-off             $F 's/if (f.Cuts.Count > 0) solid = PuppetBoolean.Tidy/if (false) solid = PuppetBoolean.Tidy/' "wind against their source plane"
# Tidy's old rule instead of the vote: a sliver's own normal is noise (it does not vote) and a fan triangle may turn 5 degrees: two fragments are left inverted.
mutant tidy-sliver-exempt   client/PuppetBoolean.cs 's/if (after <= 0 \&\& after < before) return false;/var nb4 = N(t[i]); if (nb4.Len < 2 * sliverArea) continue; var na4 = N(t[i].Select(w => w == m ? x : w).ToArray()); if (na4.Len < 1e-14 || na4.Unit.Dot(nb4.Unit) < Math.Cos(5 * Math.PI \/ 180)) return false;/' "wind against their source plane"
mutant parity-flip          client/PuppetBoolean.cs 's/Winding(D3.Of(bv\[k\]), A, at) > 0.5/Winding(D3.Of(bv[k]), A, at) < 0.5/' "the boolean faulted"
mutant tidy-midpoint        client/PuppetBoolean.cs 's/alive\[ti\] = false; alive\[to\] = false;$/alive[ti] = false; alive[to] = false; v[x] = (v[x] + v[m]) * 0.5;/' "off their source plane"
# The bevel of a cut piece is its source's (PuppetShade): a fragment's normal from its own corners (the float corners of a sliver are off its plane), and a fragment's lines, creases and cap from its own edges (the bevel then
# changes from fragment to fragment of one source and bands the rim).
H=client/PuppetShade.cs
mutant facet-from-corners   $H 's/normal\[k\] = s.N;/normal[k] = Unit(math.cross(v[tris[3 * k + 1]] - v[tris[3 * k]], v[tris[3 * k + 2]] - v[tris[3 * k]]));/' "SourceNormal:"
mutant own-edges            $H 's/var s = sources\[tag\[k\]\];/var s = Sources(v, tris, false)[k];/' "FlatEdgesAgree:" "" "RimIsHard:"
P=client/PuppetPieces.cs; M=client/PuppetModel.cs
mutant no-cache             $P 's/Cache.GetOrAdd(key, _ =>/Cache.GetOrAdd(new PieceKey(System.Guid.NewGuid().ToString()), _ =>/' "piece meshes"
mutant build-in-update      $M 's/if (_pendingSet is not { } set || !_pending.All(t => t.IsCompleted)) return false;/if (_pendingSet is not { } set) return false; Task.WaitAll(_pending);/' "pieces were not built yet"
mutant joints-first-update-only $M 's/if (!seen.Contains(j.A) && !seen.Contains(j.B)) continue;/if (_signed) continue;/' "first appears with its joint"
mutant later-joint-kept     $M 's/if (!seen.Contains(j.A) && !seen.Contains(j.B)) continue;/if (_sight.Exists(s => s.A == j.A \&\& s.B == j.B)) continue;/' "already seen got a ball and socket at the next rebuild"

echo "$ran mutants run, $failed not as expected (logs in $LOG)"
[ $failed -eq 0 ]
