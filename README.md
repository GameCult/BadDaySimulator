# Bad Day Simulator

A 3D physics sandbox. You get a level, you place puppets and objects with
their own behaviours and interactions, and the puppets have a bad day. They
feel what happens to them, flinch, writhe, and sometimes crawl on toward
whatever they wanted, because they are driven by
[Lodur](https://eureka.gamecult.org/forge/GameCult/Lodur).

**Status: two scenarios (the pain box and the office stress arc), headless, on the Ymir ragdoll and in a Godot client. The 3D sandbox does not exist yet.** The design is Lodur's target,
[`docs/lodur-target.md`](https://eureka.gamecult.org/forge/GameCult/Lodur/src/branch/main/docs/lodur-target.md)
in the Lodur repo; this game is its first consumer and its proving ground.

## Why it exists

It shows and tests everything Lodur does (stimuli, reflexes, pain and shock,
impairment) in one place where a player can poke at all of it.

## Where it lives

Canonical: `https://eureka.gamecult.org/forge/GameCult/BadDaySimulator`.
GitHub `GameCult/BadDaySimulator` is a push mirror.

## What runs today: the Synaptic Induction Chamber (the pain box)

A deterministic simulation of one virtual human with a hand in the Synaptic
Induction Chamber (the pain box), run headless or on a Ymir ragdoll. Question it answers: **how long until the
will breaks, or the box is disabled first?** The nervous model is Lodur's
(`Lodur.Actor`); this game holds the box, the seed, the restraint and the
recorder. It needs sibling checkouts of Lodur and Ymir (`LODUR_ROOT`, else
`..\Lodur`, built against Lodur `c324570` (`nibu/lodur-dead-flows`: a dead physiology's flows read zero); Lodur finds `..\Ymir`, or `YMIR_ROOT`). Entry points:

- `src/BadDay.Core/PainBoxSimulation.cs`: the headless run over Lodur's Actor.
- `src/BadDay.Core/ChamberBrain.cs`: the volunteer's Brain, authored as data: his goals, and the payment's pull.
- `src/BadDay.Core/PainBoxSession.cs`: the run's seed, traits, box and recorder; both runs drive it.
- `src/BadDay.Core/PainBox.cs`: the box schedule (ramp, jitter, disable time).
- `src/BadDay.Core/PainBoxParameters.cs`: the box and clock tunables, plus Lodur's parameter records as members.
- `src/BadDay.Core/Sweep.cs`: consecutive seeds, break-time min/median/max.
- `src/BadDay.Ymir/BodyPainBox.cs`: the same run on the Ymir ragdoll, with the hand-out time.
- `src/BadDay.Ymir/SeatedPuppet.cs`: the stool, ground and ragdoll every body is built from (`SeatedBody` holds the world).
- `src/BadDay.Cli`: `baddaysim`.
- `tests/BadDay.Tests`: determinism, break-time, monotonicity and body tests.
- `tools/PainSweep`: the intensity x suppression table.

```
dotnet run --project src/BadDay.Cli -- run   --seed 1
dotnet run --project src/BadDay.Cli -- sweep --seed 1 --count 50 --pay 40
dotnet run --project src/BadDay.Cli -- run   --seed 1 --disable-at 5
dotnet run --project src/BadDay.Cli -- run   --seed 1 --body
dotnet run --project tools/PainSweep
dotnet run --project tests/BadDay.Tests          # exits non-zero on failure
```

`run` prints a timeline (samples every second, plus flinch, break and
withdrawal events) and a one-line summary. Options: `--intensity`, `--pay` (what the volunteer is paid, dollars; default 100),
`--disable-at`, `--max`, `--every`. `--body` runs it on the ragdoll (1/120 s
steps; the native Ymir library builds with cmake and a C compiler on first
build) and adds a line with the time the hand left the box. The body run is also a stepping
session, `BodyPainBoxSession` (create, `Step`, read `Hand` and the world's lanes, `SetIntensity`,
`SwitchOff`, `BoxOn`, `DrainEvents`, dispose), that the Godot client drives and `run --body` steps to the end; `BadDay.Core` and
`BadDay.Ymir` target net8.0 for Godot, the executables and tests net10.0. Events queue per tick until drained, so a frame spanning several steps loses none. Stepping past the end keeps the body moving; that free motion is float arithmetic on the platform's math and is not bit-identical across operating systems (the run itself is).

### Godot client

`client/` is a Godot 4.7.2 (mono) project that renders the body run and takes the player's commands: the
Synaptic Induction Chamber, the dummy strapped to a stool with its right hand in the box. It owns presentation and
input only. Godot's physics engine is set to Dummy; each 120 Hz physics frame the client calls
`BodyPainBoxSession.Step()` once and draws Ymir's lanes: one pose-carrying node per stable id (`BodyRenderer`), each holding its body's own
primitive (a capsule, box or sphere of the lane's size; the figure is a placeholder of primitives until the anatomy meshes come). The look is
PlayStation-1-era 3D (lodur:ruling:bds-look-ps1-anatomy): every surface is `ps1.gdshader` (vertices snapped to the line grid, affine texture
coordinates, unfiltered texels, light, glow and fog per vertex, in raw sRGB arithmetic), and `ps1_post.gdshader` on a full-screen rect under the
HUD quantises the frame to one colour per block of a 240-line grid (`Look.Lines`, blocks of round(H / 240) pixels anchored at the screen centre)
in 15-bit colour with the 4x4 ordered dither. No `Light3D` exists: the shader lights the scene from the globals `ps1_*` (`project.godot`
`[shader_globals]`), which `Look` (constants), `Main` (`ps1_res`) and `Chamber` (the glow light, 0.9 m range) write.
Needs the .NET 8+ SDK, cmake and a C compiler (MinGW gcc on Windows), the sibling Lodur and Ymir (with joint origin lanes, ABI 8) checkouts
(`..\Lodur`, `..\Ymir`, or `LODUR_ROOT` and `YMIR_ROOT`), and Godot 4.7.2 mono. Nothing in this repository pins or checks the siblings' revisions
(BDS's graph references no CultLib project, so Ymir's own revision check never runs here); the client was last verified against Lodur 2359142 (the gravity subtree gate) and
Ymir 1d27114 (its `extern/box3d` submodule initialised).

```
dotnet build client/BadDay.Client.csproj     # Lodur, Ymir (cmake) and BDS, plus ymir_box3d.dll, into client/.godot/mono/temp/bin/Debug
<godot> --path client                        # play: Blender's navigation: middle-drag orbits the puppet, Shift+middle-drag pans, Ctrl+middle-drag and the wheel zoom; Alt+left-drag stands for the middle button (OrbitCamera.Emulate3ButtonMouse)
<godot> --headless --path client res://tests/Smoke.tscn   # the smoke; exit code 0 passes
<godot> --path client res://tests/RenderCheck.tscn  # the render check, in a window (not headless); exit code 0 passes; -- --frames=<dir> keeps its frames
bash client/tests/render-mutants.sh                # breaks the look once per claim; each break must fail its line
```

Or open `client/project.godot` in the Godot editor, Build, Play. The HUD has Reset with a seed, a live intensity
slider, Box off and Slow motion. Do not run `godot --import` on a fresh checkout; a plain run creates `.godot`.
Exports need export templates (not installed here yet), so the client runs from the editor and headless for now.

The smoke has no renderer. `RenderCheck.tscn` is the check that does: it renders real frames (gl_compatibility, the client's renderer at 1280x720)
of seed 1 stopped at a pinned tick and reads them back, so every reading is the same run to run. It checks that every channel is a multiple of 8 and the odd multiples of 8 appear (15-bit, not 12-bit colour), that the
grid is 240 lines (a literal: the blocks are 3 px at 720, whatever `Look.Lines` says), that every block of the grid is one colour, that a flat field's
blocks follow ps1.js's 4x4 ordered dither, that a wall at 9 m is fogged to the depth's smoothstep, that a triangle with different vertex normals is lit by
the blend of its corners' light (per vertex, not per pixel), that a minified checker that carries mipmaps still draws only black and white (nearest, no
mips), that a probe box's drawn blocks are those of its hull with the corners snapped to a coarse grid (the unsnapped hull differs in 8 blocks), that the
texel runs of a checker triangle seen at 60 degrees keep equal widths along a scanline (affine), that the chamber at full glow lights the figure by at least
1.2 levels over dark, and that each dynamic body's lane origin projects within a block of a figure pixel. The check holds at 1280x720 only: at any other
window size it fails "window" before reading a frame (the grid is round(H / 240) px, 216 lines at 1080, and `Camera3D.UnprojectPosition` answers in the
stretch base's coordinates). Bounds sit at the midpoint of the shipped worst and the least claimed mutant (`lodur:ruling:deterministic-bound-margin`),
each stated in its comment; `client/tests/render-mutants.sh` applies one set of edits per claim (post off, 12-bit post, dither off, per-pixel blocks, 360 and
120 lines, snap off, perspective-correct UVs, per-pixel light, fog off, mipmapped and linear textures, glow unwritten, carriers unposed) and wants that
claim's line to fail; with `UNCLAIMED=1` it runs the edits no line separates (per-pixel fog, so far), whose readings it records. It needs a display.


### Who owns what (the map)

The client's drawing pipeline, one owner per decision (the paragraphs above say how it looks; this says who may write what):

| Owner | Inputs | Outputs | Derived state | Forbidden writers |
|---|---|---|---|---|
| Ymir (the session's world, ABI 8) | the session's commands and Lodur's torque lanes | pose, shape, stable-id and joint lanes, including each joint's origin in the world (the joint origin lanes) | none the client keeps | the client writes no lane; Ymir never reads the client |
| `BodyRenderer` | the lanes (kind, size, stable id, pose), each physics frame | one carrier per stable id with one primitive `MeshInstance3D`; `PoseOf`, `InterpolatedPoseOf`, `PuppetCentre` (the union of the dynamic bodies' carrier-placed primitive boxes), `MeshBuilds` | `MeshBuilds`, the smoke's counter: a Reset with the same ids makes no mesh | geometry derived from anything but the lane's size; writing a lane |
| `ps1.gdshader` | `Look`'s numbers and the `ps1_*` globals, the mesh's vertices, normals and UVs | every surface's colour: snapped vertices, affine texels, per-vertex light, glow and fog | none | skinning; a second light model; a `Light3D` |
| `ps1_post.gdshader` | the frame and `ps1_res` | the framebuffer at the line grid in 15-bit colour with the dither | none | a second resolution owner (viewport scaling, a SubViewport; the project's `canvas_items` stretch, base 1280x720, only scales canvas items and `Camera3D.UnprojectPosition`'s answers, and the pass reads the real pixels, so it owns no resolution) |
| `Look`, `Main`, `Chamber` | `Look`: constants and textures; `Main`: the window size; `Chamber`: the box's intensity | `Look`: the constant globals and `Look.Ps1` materials; `Main`: `ps1_res` and the post layer; `Chamber`: `ps1_glow_*` | `ps1_res` is the window over `Look.BlockSize`, never set elsewhere | any other writer of a `ps1_*` global |
| `tests/RenderCheck.cs` (the windowed check), `render-mutants.sh`, `Smoke.cs` | the same lanes through the real renderer | an exit code, optional PNGs | none | writing the scene's state beyond pinning a tick and a probe |

The scenarios, one owner per decision:

| Owner | Inputs | Outputs | Derived state | Forbidden writers |
|---|---|---|---|---|
| `ChamberBrain`, `OfficeBrain` | `PainBoxParameters` or `StressParameters`, the poses | a Lodur `Brain`: goals and their rules as data, and, for the chamber, the release goal's id (`withdraw`) | none; Lodur's Actor commits each switch | a session choosing a goal; a goal rule outside the Brain |
| `PainBoxSession`, `StressSession` | seed, traits, the box or the cause, the player's commands (`Silence`, box off) | the box's or the emitters' intensities each tick; rows read back from the Actor (the stress column is the drive's arousal, the goal column the Actor's goal); the end of the run | the release goal, found by id at construction (an unknown id refuses the build) | computing load, arousal, resolve or a goal; a goal index hard-coded for the release |
| Lodur (`Actor`, the drive, the Brain) | the sessions' stimuli, the body's region points | muscle strengths and targets; the events | resolve, strain, pain, arousal, the breaks, the sensory gain in force | BDS writing any of it |
| Lodur's `Physiology` | `Afflict` calls (only from `StressSession.Wound`) and the Actor's step input | blood loss, consciousness, air hunger, death and its cause (`Died` once; a dead body is limp) | tissue integrity, bleed, the motor ceiling | a session or client deciding death, bleed or consciousness; any `Afflict` outside `StressSession.Wound` |
| `SeatedBody` | the scenario's strengths and targets each tick | the world's view, the sense points, the `BodyEvent` queue | the ragdoll and its world, built through `SeatedPuppet.Build` | any `Box3DSoaWorld` or world step in BDS outside it |
| `BodyPainBoxSession`, `OfficeBodySession` | their scenario's commands (`SetIntensity`, `SwitchOff`; `Silence`, `Wound`) | one `SeatedBody` stepped per `Step`; the chamber's hand and result, the office's read-outs (will, arousal, resolve, consciousness, blood loss, air hunger, death) | none: neither owns a world | creating or stepping a world; deciding death |

Found stale by Modeling, 2026-11-20, not changed: `docs/scenarios/fella-bad-day-at-the-office.md` is a design document (Status: design, not built) whose ladder rows 3 and 4 are now built; its text outside the ladder is not a map of the code.

### Modeled

Fixed 10 ms tick headless (1/120 s on the body), seeded SplitMix64 (the only
randomness; integer arithmetic, so a seed means the same stream anywhere). The
layers are Lodur's, run by its Actor (see Lodur's target); each timeline row is the
Actor's state at the end of its tick:

- **Perception / nerve:** the box emits an intensity (ramped in, with seeded
  jitter). A nociceptor applies a threshold and sensitises under sustained
  stimulus; the signal reaches the centre after a conduction delay.
- **Reflex:** the arriving signal drives a withdrawal reflex. Will suppresses
  it in proportion to resolve; what leaks through is a flinch, and holding it
  down accumulates strain (suppression is never free, per
  `reflex-gain-from-drive`).
- **Drive:** pain rises with the signal and decays; resolve drains with pain
  and strain, scaled by `(1 - pull)` of the goal in force, and recovers only when pain is low.
  Two seeded traits (nociceptor gain, resolve drain) make each seed a different person.
- **Goals:** the volunteer is paid to keep his hand in, and his Brain is data, built in code for now
  (`ChamberBrain`; it becomes CultCache content with Lodur's Brain content, follow-up `npc-brains-as-content`): `HoldHand` pulls with `Payment / (Payment + DollarsAtHalfPull)` (both
  100 by default, so $100 pulls 0.5; $0 pulls nothing and $400 pulls 0.8), and `Withdraw`
  is eligible once the will has broken (`Breaks >= 1`, which never falls, so the withdrawal is one-way: a will that mends does not give the hand back), outranks it and is involuntary for good. Lodur's Actor commits the
  switch after its deliberate latency (100 ms), so the break row still reads `HoldHand`;
  the hand is out when `Withdraw` takes over. The sessions never decide a goal: the
  goal column is read back from the Actor.
- **Measure:** `TimeToBreakMs` (resolve hits zero) and the outcome:
  `WillBroke`, `BoxDisabled` (disabled while the will held), or `Endured`.

### Not modeled

The headless run has no body, physics or balance (the body run has a ragdoll
held on a stool: it cannot balance); no shock, adrenaline or blood
loss; no impairment or crawling; no Unity binding; no puppets, and no objects other
than the box and the office's emitters; no learning. The reflex never removes the hand by itself: only a
broken will does. All parameter values are **placeholders**, not physiology;
the figures in Lodur's target are marked unverified and so are these. Break
times in the output are properties of this toy model, not predictions about people.
Bit-exact output across CPU architectures is not claimed; same-machine runs
are byte-identical. The headless arithmetic is Lodur's and uses only `+ - * /`
and comparisons; the hold behaviour's shake uses `Math.Sin`, and the body run
adds native physics, so neither is claimed bit-exact across machines (Windows
and Linux x64 agreed on seeds 1-5, the sweep and `run --body` when compared).

The timeline of a run differs from the pre-Lodur slice only in that each row is the
Actor's state at the end of its tick: a Flinch row shows strain and resolve after the
tick's update. The goal column is the goal in force, so rows from the break to the
withdrawal 100 ms later (the break row, and any sample falling in between) read `HoldHand`.

Persistent content (puppet definitions, drive parameters, saved levels) will
be CultCache per Lodur's `content-is-cultcache`; this slice has none, so it
has no cache layer and no JSON.

### Build note

The core has no engine reference (`core-engine-free`). It references the sibling Lodur checkout
(`LODUR_ROOT`, else `..\Lodur`) and nothing else, so `dotnet build` and `dotnet run` work with the
.NET SDK alone; the tests are a plain console program rather than xunit. Only `BadDay.Ymir` (and what
references it: the CLI and the tests) builds Ymir's native library, which needs cmake and a C compiler
(on Windows MinGW: `CMAKE_GENERATOR="Ninja Multi-Config" CC=gcc`). The projects were built and the tests
run on Windows and on Linux.

## The office stress scenario (rungs 3 and 4)

The office's stressors raise the stress bar until the will breaks, and then Office Fella snaps, rages, and settles:
headless, one tick at a time, or on the seated puppet. The scenario is data in three parts. The **setting** names the emitters (printer, ceiling light, desk
phone, desktop notifications) and where they sit. The **cause** holds the stressors that drive them (each with
an onset, ramp, period, duration, intensity and jitter), and the job's pull (0.6, the keep-composure goal's). The **character** holds the sense nerve he
hears and sees with. Lodur owns what the stimulus does: sensing, load, arousal, and the arousal
drain on resolve. `StressSession` only writes the emitters' intensities each tick and logs the Actor's state;
the player's lever is `Silence(stressorId)`, from the current tick on. Office Fella's Brain is data too, built in code for now (`OfficeBrain`; content later, as above): `KeepComposure` pulls with the job's pull; `Rage` (eligible once the will has broken, `Breaks >= 1`, outranks it,
no pull) loops wind-up and strike with each arm at 0.25 s a key. It refills his will on entry, is involuntary for its first second
(`RageOnsetSeconds`) and then costs `RageEffort` (0.05) a second, so the rage drains the will it was given. When that will runs dry
and breaks a second time (5-20 s after the snap) `Calm` takes over: his composed pose at normal strength, involuntary since his will is spent,
terminal as data (it gates on `Breaks >= 2`, which never falls, and nothing outranks it): he throws no further fit
(ruling `lodur:ruling:fella-calm-after-rage`). The Actor commits each switch 100 ms after its break,
the session logs a `GoalChanged` row for each. The office has no end of its own (ruling `lodur:ruling:bds-office-is-a-sandbox-preset`): the break is an event in the scene, the body goes on, and a wound may kill it in its own time; whoever steps the session stops when the player ends it (the headless run, the sweep and `OfficeBody.Run` take a horizon in seconds, the CLI's `--max`, 180 by default).
`TimeToBreakMs` is the first break, the snap; the second `WillBroke` row is the rage running dry.
The chamber's release is declared the same way (`PainBoxParameters.ReleaseGoalId`, `withdraw`), and its withdrawal is one-way: a will that mends does not give the hand back. Who has the day is a `Character` (his senses; `Character.OfficeFella`),
apart from the setting and the cause. Each scenario builds its own Brain from its own parameters; one entry point keyed by character id
waits for Brains as content. Headless the rage moves nothing; `office --body` runs it on the puppet.

On the puppet the office is `OfficeBodySession`, stepped by its caller one 1/120 s step at a time like the chamber's session (`OfficeBody.Run` records one for a horizon). It builds a `SeatedBody`, the one owner of every BDS body world, and attaches a Lodur `Physiology` to the Actor (`StressParameters.SlowScale`, default 20 [P]: ruling `lodur:ruling:bds-physiology-clock`; Acute afflictions such as a cut do not move with it). The player's harm is `Wound(part, tool)`: a `WoundTool` is a depth and a severity; the hit point in the part's frame and Lodur's `PartTissuesDef.Reach` pick the layers, and `StressSession.Wound` binds one Cut affliction on each, the only path harm takes into the body. The knife (`WoundTool.Knife`, 0.02 m at 60 [P]: skin, fat and muscle on limbs and trunk, the larynx at the front of the throat) is the click-cut's content until contacts become afflictions. Lodur decides the rest: blood loss, consciousness, air hunger and death (`lodur:ruling:bds-death`). A cut on the left lower arm bleeds the body out in about 40 s; the Actor reports `Died` once, the body goes limp (it writes no strength from then on), the session logs a `Died` row, ends the run `ReleaseSeconds` later with `Outcome.Died`, and `StressResult` carries `DiedAtMs` and `DeathCause`. An unwounded run is unchanged: nothing in it can die.

The stress bar is his arousal, how wound up he is right now, and reads **stress (arousal)** (`StressResult.StressBarLabel`; ruling `lodur:ruling:stress-bar-shows-arousal-calm-silences`).
It rises with each stressor and falls in quiet; will is not on it. Entering calm fades his arousal and his senses' gain to nothing over `CalmFadeSeconds` (3 s, the aftershock tail of
`lodur:ruling:flinch-tail-after-stimulus`; the calm goal's `EntryArousal`, `SensoryGain` and `EntrySeconds`), so the bar drains and stays empty for the rest of the run while the stressor columns
keep sounding. Arousal is exactly 0 from `CalmFadeSeconds` on; the load column tails off with the gain and reaches 0 one tick later (`Sense` reads the gain a tick after the Actor sets it).

- `src/BadDay.Core/Setting.cs`, `Cause.cs`: the office's defaults, in code, not persisted yet.
- `src/BadDay.Core/StressSession.cs`: one run, driven per tick by the headless run, the sweep, the CLI and a client.
- `src/BadDay.Core/OfficeBrain.cs`: Office Fella's goals, and the poses they write.
- `src/BadDay.Ymir/OfficeBody.cs`: the office on the seated puppet, recorded to its end: both hands, the pelvis and the strikes.
- `src/BadDay.Ymir/OfficeBodySession.cs`: the office's stepping session on a `SeatedBody` with a Lodur `Physiology`; `Wound`, the read-outs, death.
- `src/BadDay.Ymir/SeatedBody.cs`: the one owner of a BDS body world and its step order.
- `src/BadDay.Core/WoundTool.cs`: what a wound is made of: a depth and a severity; `Knife`.
- `src/BadDay.Core/StressSimulation.cs`: the headless run with scheduled silences, and the seed sweep.

```
dotnet run --project src/BadDay.Cli -- office --seed 1
dotnet run --project src/BadDay.Cli -- office --seed 1 --body
dotnet run --project src/BadDay.Cli -- office --seed 1 --silence printer@20 --silence desk-phone@30
dotnet run --project src/BadDay.Cli -- office-sweep --seed 1 --count 50 --scale 1.5
```

Options: `--silence ID@S` (repeatable; `printer`, `ceiling-light`, `desk-phone`, `desktop-interrupt`), `--scale`
(every stressor's intensity), `--pull`, `--max`, `--every`, and `--body` (run the puppet; the summary adds the rage's
length and the strikes counted as forward hand-speed peaks). The rows are `time_ms kind audio visual interrupt
load arousal resolve stress goal`, with kinds Sample, Silenced, WillBroke, GoalChanged and Endured and goals
KeepComposure, Rage and Calm. The sweep prints the
break-time min/median/max and the lowest resolve among runs that endured. The office breaks near 90 s at pull
0.6 only with the office's own sense nerve (threshold 0.05, no sensitisation, no conduction delay); Lodur's
`NerveParameters` defaults break it near 100 s. **Every number here is a placeholder.**
