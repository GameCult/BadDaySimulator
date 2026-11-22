#!/usr/bin/env bash
# The puppet's mutants, one sed edit each: build the client, run the headless smoke (PuppetChecks inside), and require the failure line the mutant must print
# (or, for a mutant the checks must not mind, a pass). Refuses a dirty tree; restores every file it edits.
#   GODOT=<godot console exe> bash client/tests/mutants.sh (ONLY="name name" limits the run)
set -u
cd "$(dirname "$0")/../.."
GODOT="${GODOT:-F:/Applications/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "refusing: the tree is dirty"; exit 2; }
LOG="${LOG:-$(mktemp -d)}"; failed=0; ran=0

# mutant <name> <file> <sed expression> <substring the smoke must print, or PASS> [substring it must not print]
mutant() {
  local name="$1" file="$2" expr="$3" want="$4" absent="${5:-}"
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
  elif [ $code -ne 0 ] && grep -qF -- "$want" "$LOG/$name.log"; then echo "ok    $name: caught ($want)"
  else echo "FAIL  $name: exit $code without '$want' (log $LOG/$name.log)"; failed=$((failed + 1)); fi
}

F=client/PuppetField.cs; D=client/PuppetDoll.cs
mutant seam-10mm            $D 's/Seam = 0.002f/Seam = 0.010f/'                 "from its ball"
mutant seat-band-0          $D 's/SeatBand = 0.015f/SeatBand = 0f/'             "from its ball"
mutant seat-error-40mm      $D 's/SeatError = 0.005f/SeatError = 0.040f/'       "from its ball"
# FacetError does not own the seat: at 40 mm the mesher check (an inward face) catches it, and the seat check must not.
mutant facet-error-40mm     $D 's/FacetError = 0.02f/FacetError = 0.04f/'       "face into their piece" "from its ball"
mutant ball-bone-child      $F 's/new JointBall(n + j, joint.A, o, /new JointBall(n + j, joint.B, o, /' "do not follow their joint's parent"
mutant ball-bone-root       $F 's/new JointBall(n + j, joint.A, o, /new JointBall(n + j, 0, o, /'       "do not follow their joint's parent"
mutant seam-0               $D 's/Seam = 0.002f/Seam = 0f/'                     "less than half the seam inside a socket"
mutant thigh-078            $D 's/DollPrim.Cone(V(0, .2f, 0), .07f, V(0, -.22f, 0), .05f)/DollPrim.Cone(V(0, .2f, 0), .078f, V(0, -.22f, 0), .05f)/' "fold"
# The hands: sockets 10 mm off their balls (the hands are judged by the hug check, not the seat), a palm with no entry, ThinError off, a roll's clearance by point distance, and the thin error leaking into the other pieces
mutant hands-seam-10mm      $D 's/Seam = 0.002f/Seam = 0.010f/'                 "stop short of it"
mutant no-palm-entry        $D 's/new(Box3DSoaKind.Box, V(.014f, .045f, .04f), new\[\]/new(Box3DSoaKind.Box, V(.015f, .045f, .04f), new[]/' "of the palm bodies are drawn as their fallback"
mutant thin-error-off       $D 's/ThinError = 0.25f/ThinError = 100f/' "silhouette differs"
mutant facet-error-19mm     $D 's/FacetError = 0.02f/FacetError = 0.019f/'       "the hands must not change changed"
# The primitives (ruling doll-joints-built-primitives): the straddle off (the lattice inscribed: its face centres 7.5% inside), the frequency 2 (8 sides, 32 triangles), a roll with no length.
mutant ball-inscribed       $F 's#radius \* 2f / (1f + k)#radius#' "ball faces lie more than"
mutant ball-freq-2          $D 's/BallFrequency = 3/BallFrequency = 2/' "are not 38 vertices and 72 triangles"
mutant roll-sphere          $D 's/0.015f, V(0, 0, 1), 0.028f)/0.015f, V(0, 0, 1), 0f)/' "not its two knuckle rolls"
# The seat error scaled to the ball off (5 mm at every ball), and a limb wider than its ball (the palm's ellipsoid 34 mm across against the 26 mm wrist ball): the wedge between them is a slit. 
mutant seat-share-off       $D 's/SeatShare = 0.15f/SeatShare = 100f/' "a ball joint shows slits"
mutant limb-end-over-ball   $D 's/V(.0135f, .022f, .027f)/V(.0135f, .022f, .034f)/' "a ball joint shows slits"
# The fingers' root over the roll's trough rim (the box top at 14 mm over the axis, not 11): a lip of the root is lost.
mutant fingers-root-lip     $D 's/DollPrim.Box(V(0, -.00575f, 0), V(.0115f, .03625f, .037f), .0095f)/DollPrim.Box(V(0, -.00275f, 0), V(.0115f, .03925f, .037f), .0095f)/' "does not wrap its roll"
mutant thin-error-half      $D 's/ThinError = 0.25f/ThinError = 0.5f/' "silhouette differs"
mutant body-seam-10mm       $D 's/Seam = 0.002f/Seam = 0.010f/'                             "a ball joint shows slits"
# Joints are built primitives, never meshed (ruling doll-joints-built-primitives): a ball routed through the decimator; the thin rule moved onto the bodies of 80 mm (ThinBelow); the seat limit without the thin error (Allowed's minimum); a lower seat share.
mutant ball-decimated       $F 's#public (float3\[\] V, int\[\] Tris) Mesh() => PuppetField.Primitive(Radius, Axis, math.length(Roll), Doll.BallFrequency);#public (float3[] V, int[] Tris) Mesh() { var (v, t) = PuppetField.Primitive(Radius, Axis, math.length(Roll), Doll.BallFrequency); return PuppetDecimate.Run(v, t, Doll.FacetAlpha, Doll.FacetScale, new DecimateRules((p, tight) => true, p => false, p => 1f, System.Array.Empty<(float3 P, float Clear)>())); }#' "are not 38 vertices and 72 triangles"
mutant thinbelow-8cm        $D 's/ThinBelow = 0.04f/ThinBelow = 0.08f/' "the hands must not change changed"
mutant allowed-no-min       $F 's/(tight ? MathF.Min(Error, SeatLimit(p)) : Error)/(tight ? SeatLimit(p) : Error)/' "stray further from their piece's surface"
mutant seat-share-008       $D 's/SeatShare = 0.15f/SeatShare = 0.08f/' "shows the inside of a piece"
# The decimator bounds the error both ways (ruling decimator-two-sided-error): the one-sided rule drops a lip thinner than the error; a palm 15 mm short leaves a knuckle roll's end bare.
mutant decimator-one-sided  client/PuppetDecimate.cs 's/ \&\& Hugs(x, fan, u, v))/)/' "of their error (worst"
mutant palm-short-z         $D 's/V(.014f, .0265f, .04f)/V(.014f, .0265f, .025f)/' "a roll's end is bare"
# The boolean's cutter and tidy: no countersink (a plain ball plus shadow: knife lips), no shadow ring, the cutter lattice turned 15 degrees about its axis, no tidy, B's inside-ness inverted (an open cut), a collapse onto the midpoint (fragments leave their planes).
mutant sink-off             $D 's/SinkAngle = 35f \* (MathF.PI \/ 180f)/SinkAngle = 0f/' "of socket rim meets at under"
mutant shadow-off           $F 's/points.Add(p0); points.Add(p0 + w \* far);/points.Add(p0);/' "of socket rim meets at under"
mutant cutter-turned        $F 's/Doll.BallFrequency, U);/Doll.BallFrequency, U * 0.9659f + math.cross(Axis, U) * 0.2588f);/' "limbs at hand balls seat on under"
mutant tidy-off             $F 's/if (f.Cuts.Count > 0) solid = PuppetBoolean.Tidy/if (false) solid = PuppetBoolean.Tidy/' "wind against their source plane"
# Tidy's old rule instead of the vote: a sliver's own normal is noise (it does not vote) and a fan triangle may turn 5 degrees: two fragments are left inverted.
mutant tidy-sliver-exempt   client/PuppetBoolean.cs 's/if (after <= 0 \&\& after < before) return false;/var nb4 = N(t[i]); if (nb4.Len < 2 * sliverArea) continue; var na4 = N(t[i].Select(w => w == m ? x : w).ToArray()); if (na4.Len < 1e-14 || na4.Unit.Dot(nb4.Unit) < Math.Cos(5 * Math.PI \/ 180)) return false;/' "wind against their source plane"
mutant parity-flip          client/PuppetBoolean.cs 's/Winding(D3.Of(bv\[k\]), A, at) > 0.5/Winding(D3.Of(bv[k]), A, at) < 0.5/' "the boolean faulted"
mutant tidy-midpoint        client/PuppetBoolean.cs 's/alive\[ti\] = false; alive\[to\] = false;$/alive[ti] = false; alive[to] = false; v[x] = (v[x] + v[m]) * 0.5;/' "off their source plane"
P=client/PuppetPieces.cs; M=client/PuppetModel.cs
mutant no-cache             $P 's/Cache.GetOrAdd(key, _ =>/Cache.GetOrAdd(new PieceKey(System.Guid.NewGuid().ToString()), _ =>/' "piece meshes"
mutant build-in-update      $M 's/if (_pendingSet is not { } set || !_pending.All(t => t.IsCompleted)) return false;/if (_pendingSet is not { } set) return false; Task.WaitAll(_pending);/' "pieces were not built yet"
mutant joints-first-update-only $M 's/if (!seen.Contains(j.A) && !seen.Contains(j.B)) continue;/if (_signed) continue;/' "first appears with its joint"
mutant later-joint-kept     $M 's/if (!seen.Contains(j.A) && !seen.Contains(j.B)) continue;/if (_sight.Exists(s => s.A == j.A \&\& s.B == j.B)) continue;/' "already seen got a ball and socket at the next rebuild"

echo "$ran mutants run, $failed not as expected (logs in $LOG)"
[ $failed -eq 0 ]
