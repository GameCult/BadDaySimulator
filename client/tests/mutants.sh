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
  timeout 400 "$GODOT" --headless --path client res://tests/Smoke.tscn > "$LOG/$name.log" 2>&1
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
mutant ball-inscribed       $F 's/return MathF.Abs(math.length(p) - _radius) <= Doll.BallError;/{ float r = math.length(p) - _radius; return r <= 0.0002f \&\& r >= -0.010f; }/' "from its ball"
# FacetError does not own the seat: at 40 mm the mesher check (an inward face) catches it, and the seat check must not.
mutant facet-error-40mm     $D 's/FacetError = 0.02f/FacetError = 0.04f/'       "face into their piece" "from its ball"
mutant ball-bone-child      $F 's/new PieceField(n + j, joint.A, o, radius)/new PieceField(n + j, joint.B, o, radius)/' "do not follow their joint's parent"
mutant ball-bone-root       $F 's/new PieceField(n + j, joint.A, o, radius)/new PieceField(n + j, 0, o, radius)/'       "do not follow their joint's parent"
mutant seam-0               $D 's/Seam = 0.002f/Seam = 0f/'                     "inside a joint ball's radius"
mutant thigh-078            $D 's/DollPrim.Cone(V(0, .2f, 0), .07f, V(0, -.22f, 0), .05f)/DollPrim.Cone(V(0, .2f, 0), .078f, V(0, -.22f, 0), .05f)/' "fold"

P=client/PuppetPieces.cs; M=client/PuppetModel.cs
mutant no-cache             $P 's/Cache.GetOrAdd(key, _ =>/Cache.GetOrAdd(new PieceKey(System.Guid.NewGuid().ToString()), _ =>/' "piece meshes"
mutant build-in-update      $M 's/if (_pendingSet is not { } set || !_pending.All(t => t.IsCompleted)) return false;/if (_pendingSet is not { } set) return false; Task.WaitAll(_pending);/' "pieces were not built yet"

echo "$ran mutants run, $failed not as expected (logs in $LOG)"
[ $failed -eq 0 ]
