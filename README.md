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
`..\Lodur`; Lodur finds `..\Ymir`, or `YMIR_ROOT`). Entry points:

- `src/BadDay.Core/PainBoxSimulation.cs`: the headless run over Lodur's Actor.
- `src/BadDay.Core/ChamberBrain.cs`: the volunteer's Brain, authored as data: his goals, and the payment's pull.
- `src/BadDay.Core/PainBoxSession.cs`: the run's seed, traits, box and recorder; both runs drive it.
- `src/BadDay.Core/PainBox.cs`: the box schedule (ramp, jitter, disable time).
- `src/BadDay.Core/PainBoxParameters.cs`: the box and clock tunables, plus Lodur's parameter records as members.
- `src/BadDay.Core/Sweep.cs`: consecutive seeds, break-time min/median/max.
- `src/BadDay.Ymir/BodyPainBox.cs`: the same run on the Ymir ragdoll, with the hand-out time.
- `src/BadDay.Ymir/SeatedPuppet.cs`: the stool, ground and ragdoll every body run is built on.
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
`BodyPainBoxSession.Step()` once and draws Ymir's lanes: one pose-carrying node per stable id, and the body drawn as a ball-jointed art doll
(lodur:ruling:puppet-joints-ball-jointed-doll): rigid pieces, one closed piece per shape body (its primitives smooth-min'd within the body only, `client/PuppetDoll.cs`) and a ball at every joint, with a socket in each limb (a limb may sink into its neighbour at the end of a bend: lodur:ruling:doll-pieces-hug-balls); a body is extracted fine (surface nets) from its body field alone and decimated by `client/PuppetDecimate.cs` to `Doll.FacetError` (`Doll.ThinError` of its thickness under `Doll.ThinBelow`) at alpha 0.5, the error bounded both ways (lodur:ruling:decimator-two-sided-error); each socket is then cut from the decimated mesh by `PuppetBoolean.Subtract`, a convex countersunk cutter (the joint's built ball grown by `Doll.Seam`, its rim sunk `Doll.SinkAngle` and carried past the ball: lodur:ruling:doll-socket-lip-countersink-35, lodur:ruling:doll-booleans-after-decimation), the result tidied onto its source planes and reduced to its largest solid, so every triangle of a cut piece lies on a source plane (a decimated triangle or a cutter's); a ball is built as a primitive (`PuppetField.Primitive`, `Doll.BallFrequency`: 72 triangles, a knuckle roll 96) and never meshed or decimated (lodur:ruling:doll-joints-built-primitives); every vertex follows one bone, so nothing blends across a joint, and the seam at each joint is the look. Each piece's mesh is built once per key (`client/PuppetPieces.cs`, a pure function of its shape and sockets) on the thread pool, never on the physics thread, and the shape is published when its pieces are built; rebuilt only when the bodies change (the joints, their sockets and balls are fixed at first sight: lodur:ruling:doll-severed-keeps-socket); skinned on the CPU every frame by `PuppetSkin`, which poses the normals that `PuppetShade` derived once, at assembly, from each triangle's source (a fragment of a cut draws the bevel of the decimated triangle or cutter triangle it was cut from: that source's edges, creases, least altitude and vertex normals, so the bevel does not change across the fragments of one source and a socket's rim is a hard edge); flat-shaded, each edge softened by a bevel 2 px wide (`Look.BevelScreen`, at least `Look.BevelMin`, question lodur:question:bds-puppet-bevel-look-width): the fragment shader tilts the normal to each edge's bisector and blends corners to the vertex normal). The puppet is glazed porcelain: a bisque lit by a studio (a shadowing key and two rims, `Look.Studio`) that the camera carries (lodur:ruling:porcelain-studio-follows-camera), plus an analytic glaze that reflects the same studio (`client/studio.gdshaderinc`, Fresnel times the studio in the reflected direction, added as emission so it draws the same under gl_compatibility), pooled darker in concave creases. The highlights are prefiltered where the shader decides them (geometric specular antialiasing, Tokuyoshi and Kaplanyan 2021): the spread of the shading normal over a pixel widens the studio the glaze reads, so a highlight does not crawl as the camera moves; the 3D draws with MSAA 4x (`rendering/anti_aliasing/quality/msaa_3d`) for silhouettes and triangle edges.
Needs the .NET 8+ SDK, cmake and a C compiler (MinGW gcc on Windows), the sibling Lodur, Ymir (with joint origin lanes, ABI 8) and CultLib
checkouts (`..\Lodur`, `..\Ymir`, `..\CultLib`, or `LODUR_ROOT`, `YMIR_ROOT` and `CULTLIB_ROOT`; CultLib must be a clean checkout of
42dce361, the revision Ymir admits, or the build refuses), and Godot 4.7.2 mono.

```
dotnet build client/BadDay.Client.csproj     # CultLib Geometry, Lodur, Ymir (cmake) and BDS, plus ymir_box3d.dll, into client/.godot/mono/temp/bin/Debug
<godot> --path client                        # play: Blender's navigation: middle-drag orbits the puppet, Shift+middle-drag pans, Ctrl+middle-drag and the wheel zoom; Alt+left-drag stands for the middle button (OrbitCamera.Emulate3ButtonMouse)
<godot> --headless --path client res://tests/Smoke.tscn   # the smoke; exit code 0 passes
<godot> --path client res://tests/RenderCheck.tscn  # the render check, in a window (not headless); exit code 0 passes
```

Or open `client/project.godot` in the Godot editor, Build, Play. The HUD has Reset with a seed, a live intensity
slider, Box off and Slow motion. Do not run `godot --import` on a fresh checkout; a plain run creates `.godot`.
Exports need export templates (not installed here yet), so the client runs from the editor and headless for now.

The smoke has no renderer. `RenderCheck.tscn` is the check that does: it poses the puppet in a bent pose under a moved parent, renders one frame
(gl_compatibility, the client's renderer), and compares the drawn silhouette with the mesh skinned on the CPU (intersection over union, at
least 0.999: a correct render measures 1.0000, and a forearm bone 10 degrees off, a bone 2 cm off and hard weights fail), and checks the bisque (the glaze off) is not clipped to white and that the chamber's light still lights it (a glow of 0 fails); with the glaze on at the close view, from the front and from behind, the glaze must change at least 1.5% of the puppet's pixels and, from the front, clip at most 12% (add `-- --glaze-png=<path>` or `--glaze-behind-png=`). It also draws the shading normal (the soft bevel) on the torso and compares it with a CPU reference of the whole formula, skinned by the check itself from the shape's own source shading (the lines, creases, caps and normals `PuppetShade` derived, posed by the carriers), in five views: close up and from 3.7 m at 1280x720, close up in a bend of 90 degrees, and at 1920x1080 from 2.2 m and from 3.7 m (so a width fixed at 720 lines fails). In each: 90th percentile error at most 2 degrees, 0.6 across the bevel band, 0.75 on triangles whose width the smallest altitude caps, at most 0.7 on the pixels the corner blend reaches (the smooth vertex normal, in view space, within 1.5 widths of a corner) and at most 1.5 on the band of edges turning by 115 to 137 degrees (a bevel that is off, a linear falloff, a missing width cap, the wrong width, a vertex normal left in model space, a shorter corner reach and a lower crease clamp fail). The drawn normals across triangle boundaries stay continuous: at most 0.9% of adjacent pixel pairs over 30 degrees at a bend of 60 degrees and 1% at 90, where some edges fold past 120 degrees and 2 px cannot soften them. The oracle frames above are drawn with MSAA off (they check the shader's own pixels). Last, the flicker check draws the glaze with the project's MSAA from 16 sub-pixel camera shifts in seven framings in the pose the player sees (five at 2.4 m, FOV 55, the first being where the orbit camera starts, and two at 0.7 m) and in three of them with the puppet bent 30 and 60 degrees, against the same shifts drawn 8 times the window's size, MSAA off, and box-filtered down in linear light. On the pixels the puppet covers fully, the 99th percentile of the per-pixel luma standard deviation over the shifts must stay within 1.60 times the reference's and the mean luma of the reference's highlights (245 or more) within 1.34 levels of the reference's (a glaze that crawls, or one blurred away, fails); on the silhouette's ring the mean distance from the reference must stay under 26.6 levels (the 3D without MSAA fails); and under a slow orbit (30 frames of 0.15 degrees at the shipped framing) the error against the reference, differenced twice in time over the halo of the highlights, must average at most 4.1 levels (the shipped glaze 3.2, an unfiltered one 5.3). The sample floors of the views and the bounds are derived from what the shipped shader and each mutant read (the bounds on the doll of 2258 triangles; the sample floors were derived on the earlier doll of 1294 and are not yet re-derived on this one); the derivation is in `RenderCheck.cs`, and `client/tests/render-mutants.sh` runs the mutants and names the ones the check cannot tell from the shipped doll (a doubled kernel; a stale crease, since every edge lies inside one rigid piece). The whole check takes about 2 minutes on a GTX 1070. Add `-- --png=<path>`, `-- --normals-png=<path>` or `-- --normals-far-png=<path>` (also `--normals-bend90-png=`, `--normals-mid-png=`, `--normals-far1080-png=`) to keep a frame. It needs a display.
Known gap: the culling box. `Puppet` sets the mesh's `CustomAabb` to the skinned bounds every frame; a 1 cm box at the origin or at the centre culls the puppet in the close view and fails, but a box of half the bounds around the centre passes, since it only drops limbs where the box leaves the screen, which no view samples.


### Who owns what (the map)

The client's puppet pipeline, one owner per decision (the paragraphs above say how it looks; this says who may write what):

| Owner | Inputs | Outputs | Derived state | Forbidden writers |
|---|---|---|---|---|
| Ymir (the session's world, ABI 8) | the session's commands and Lodur's torque lanes | pose, shape, stable-id and joint lanes, including each joint's origin in the world (the joint origin lanes) | none the client keeps | the client writes no lane; Ymir never reads the client |
| `PuppetField` (`Build`: the mesher) | shape bodies (id, kind, size, bind pose), qualifying joints (kind, body A, body B, origin in A's frame), the `Look` blend constants, the cell | `PuppetShape`: the welded vertices (each once, with its one bone), triangles as `Corners`, edge pairing as `Across`, and the bevel each triangle draws (`PuppetShade`'s) | nothing: a pure, engine-free function of its inputs | anything that reads lanes, Godot types, a call per frame |
| `PuppetModel` | `Box3DSoaView` (kind, size, stable id, pose, joint kind, bodies and origins), read by `Lanes` | the current `PuppetShape`, and whether this update rebuilt it | the bind (each body's pose the first time it is seen), each joint's origin kept in body A's frame from the origin lane at first sight, the topology signature (ordered id, kind and size of the shape bodies; ordered kind, A and B of the qualifying joints) | `Puppet` or the scene deciding when to rebuild; a pose or an origin alone causing a rebuild |
| `PuppetShade` (`Assemble`, once per piece) | a piece's sources: the decimated mesh, the cutters (a wall's source is its cutter triangle) and each fragment's tag; a ball or an uncut body as its own source | per triangle the source's normal, creases and least altitude; per corner the distances to the source's three lines and the interpolated vertex normal | none: pure | a normal, crease, distance, cap or vertex normal computed from a triangle's own corners anywhere else |
| `PuppetSkin` (one per shape) | the shape and one `Rigid` per bone, every frame | `Skinned`, `Normal`, `VertexNormal` (posed), `Lo` and `Hi` | the posed normals, every frame; buffers allocated once | the shader, `Puppet` or RenderCheck deriving a crease, vertex normal, normal or distance from a triangle's corners |
| `Puppet` | the lanes (a carrier per stable id, posed each physics frame), the model's shape, the skin's arrays | the Godot mesh (two streams uploaded in place each frame), `CustomAabb`, `MeshBuilds` | the build counter, the client's only one: it counts Godot mesh builds | geometry derived here; a second build counter; writing a lane |
| `puppet.gdshader` | `CUSTOM0..3` (distances to the source's lines and its cap, creases, normal, vertex normal), `Look.BevelScreen` and `BevelMin` | the shading normal (the bevel) and the colour | none | skinning, evaluating the SDF, owning a width or a crease |
| `tests/RenderCheck.cs` (the windowed oracle), `PuppetChecks.cs`, `Smoke.cs` | the same lanes through the real renderer, and an independent CPU reference of the whole bevel | an exit code, optional PNGs | none | writing the puppet's state; sharing `PuppetSkin`'s posing (the reference poses by the carriers itself) |

The scenarios, one owner per decision:

| Owner | Inputs | Outputs | Derived state | Forbidden writers |
|---|---|---|---|---|
| `ChamberBrain`, `OfficeBrain` | `PainBoxParameters` or `StressParameters`, the poses | a Lodur `Brain`: goals and their rules as data, and the release goal's id (`withdraw`, `calm`) | none; Lodur's Actor commits each switch | a session choosing a goal; a goal rule outside the Brain |
| `PainBoxSession`, `StressSession` | seed, traits, the box or the cause, the player's commands (`Silence`, box off) | the box's or the emitters' intensities each tick; rows read back from the Actor (the stress column is the drive's arousal, the goal column the Actor's goal); the end of the run | the release goal, found by id at construction (an unknown id refuses the build) | computing load, arousal, resolve or a goal; a goal index hard-coded for the release |
| Lodur (`Actor`, the drive, the Brain) | the sessions' stimuli, the body's region points | muscle strengths and targets; the events | resolve, strain, pain, arousal, the breaks, the sensory gain in force | BDS writing any of it |

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
the session logs a `GoalChanged` row for each, and the run goes on `ReleaseSeconds` (8 s, the settle: the seated body is under 0.025 m/s by then on every seed 1-100) after the Brain's release goal
(`ReleaseGoalId`, `calm`) takes over, or to 180 s if the will holds. `TimeToBreakMs` is the first break, the snap; the second `WillBroke` row is the rage running dry.
The chamber's release is declared the same way (`PainBoxParameters.ReleaseGoalId`, `withdraw`), and its withdrawal is one-way: a will that mends does not give the hand back. Who has the day is a `Character` (his senses; `Character.OfficeFella`),
apart from the setting and the cause. Each scenario builds its own Brain from its own parameters; one entry point keyed by character id
waits for Brains as content. Headless the rage moves nothing; `office --body` runs it on the puppet.

The stress bar is his arousal, how wound up he is right now, and reads **stress (arousal)** (`StressResult.StressBarLabel`; ruling `lodur:ruling:stress-bar-shows-arousal-calm-silences`).
It rises with each stressor and falls in quiet; will is not on it. Entering calm fades his arousal and his senses' gain to nothing over `CalmFadeSeconds` (3 s, the aftershock tail of
`lodur:ruling:flinch-tail-after-stimulus`; the calm goal's `EntryArousal`, `SensoryGain` and `EntrySeconds`), so the bar drains and stays empty for the rest of the run while the stressor columns
keep sounding. Arousal is exactly 0 from `CalmFadeSeconds` on; the load column tails off with the gain and reaches 0 one tick later (`Sense` reads the gain a tick after the Actor sets it).

- `src/BadDay.Core/Setting.cs`, `Cause.cs`: the office's defaults, in code, not persisted yet.
- `src/BadDay.Core/StressSession.cs`: one run, driven per tick by the headless run, the sweep, the CLI and a client.
- `src/BadDay.Core/OfficeBrain.cs`: Office Fella's goals, and the poses they write.
- `src/BadDay.Ymir/OfficeBody.cs`: the office on the seated puppet: both hands, the pelvis and the strikes.
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
