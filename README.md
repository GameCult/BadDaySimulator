# Bad Day Simulator

A 3D physics sandbox. You get a level, you place puppets and objects with
their own behaviours and interactions, and the puppets have a bad day. They
feel what happens to them, flinch, writhe, and sometimes crawl on toward
whatever they wanted, because they are driven by
[Lodur](https://eureka.gamecult.org/forge/GameCult/Lodur).

**Status: first slice only (the pain box). The 3D sandbox does not exist yet.** The design is Lodur's target,
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
`SwitchOff`, `DrainEvents`, dispose), that the Godot client drives and `run --body` steps to the end; `BadDay.Core` and
`BadDay.Ymir` target net8.0 for Godot, the executables and tests net10.0. Events queue per tick until drained, so a frame spanning several steps loses none. Stepping past the end keeps the body moving; that free motion is float arithmetic on the platform's math and is not bit-identical across operating systems (the run itself is).

### Godot client

`client/` is a Godot 4.7.2 (mono) project that renders the body run and takes the player's commands: the
Synaptic Induction Chamber, the dummy strapped to a stool with its right hand in the box. It owns presentation and
input only. Godot's physics engine is set to Dummy; each 120 Hz physics frame the client calls
`BodyPainBoxSession.Step()` once and draws Ymir's lanes (one node per stable id, shapes from the Kind and Size lanes).
Needs the .NET 8+ SDK, cmake and a C compiler (MinGW gcc on Windows), the sibling Lodur and Ymir checkouts
(`..\Lodur`, `..\Ymir`, or `LODUR_ROOT` and `YMIR_ROOT`), and Godot 4.7.2 mono.

```
dotnet build client/BadDay.Client.csproj     # Lodur, Ymir (cmake) and BDS, plus ymir_box3d.dll, into client/.godot/mono/temp/bin/Debug
<godot> --path client                        # play: right-drag orbits, the wheel zooms
<godot> --headless --path client res://tests/Smoke.tscn   # the smoke; exit code 0 passes
```

Or open `client/project.godot` in the Godot editor, Build, Play. The HUD has Reset with a seed, a live intensity
slider, Box off and Slow motion. Do not run `godot --import` on a fresh checkout; a plain run creates `.godot`.
Exports need export templates (not installed here yet), so the client runs from the editor and headless for now.

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
- **Goals:** the volunteer is paid to keep his hand in, and his Brain is data
  (`ChamberBrain`): `HoldHand` pulls with `Payment / (Payment + DollarsAtHalfPull)` (both
  100 by default, so $100 pulls 0.5; $0 pulls nothing and $400 pulls 0.8), and `Withdraw`
  is eligible once resolve has reached zero and outranks it. Lodur's Actor commits the
  switch after its deliberate latency (100 ms), so the break row still reads `HoldHand`;
  the hand is out when `Withdraw` takes over. The sessions never decide a goal: the
  goal column is read back from the Actor.
- **Measure:** `TimeToBreakMs` (resolve hits zero) and the outcome:
  `WillBroke`, `BoxDisabled` (disabled while the will held), or `Endured`.

### Not modeled

The headless run has no body, physics or balance (the body run has a ragdoll
held on a stool: it cannot balance); no shock, adrenaline or blood
loss; no impairment or crawling; no Unity binding; no puppets or objects other
than the box; no learning. The reflex never removes the hand by itself: only a
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

The office's stressors fill the stress bar until the will breaks, and then Office Fella goes into a rage:
headless, one tick at a time, or on the seated puppet. The scenario is data in two parts. The **setting** names the emitters (printer, ceiling light, desk
phone, desktop notifications) and where they sit. The **cause** holds the stressors that drive them (each with
an onset, ramp, period, duration, intensity and jitter), the job's pull (0.6, the keep-composure goal's) and the sense nerve the
character hears and sees with. Lodur owns what the stimulus does: sensing, load, arousal, and the arousal
drain on resolve. `StressSession` only writes the emitters' intensities each tick and logs the Actor's state;
the player's lever is `Silence(stressorId)`, from the current tick on. Office Fella's Brain is data too
(`OfficeBrain`): `KeepComposure` pulls with the job's pull; `Rage` (eligible once the will has broken, outranks it,
no pull) loops wind-up and strike with each arm at 0.25 s a key. The Actor commits the switch 100 ms after the break,
the session logs a `GoalChanged` row, and the run goes on `ReleaseSeconds` (6 s) after it, or to 180 s if the will
holds. Headless the rage moves nothing; `office --body` runs it on the puppet.

The stress bar is `1 - resolve` and reads **lost composure**; the label is one constant
(`StressResult.StressBarLabel`) pending `lodur:question:stress-bar-meaning`.

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
KeepComposure and Rage. The sweep prints the
break-time min/median/max and the lowest resolve among runs that endured. The office breaks near 90 s at pull
0.6 only with the office's own sense nerve (threshold 0.05, no sensitisation, no conduction delay); Lodur's
`NerveParameters` defaults break it near 100 s. **Every number here is a placeholder.**
