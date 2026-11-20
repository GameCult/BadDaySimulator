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
- `src/BadDay.Core/PainBoxSession.cs`: the run's seed, traits, box and recorder; both runs drive it.
- `src/BadDay.Core/PainBox.cs`: the box schedule (ramp, jitter, disable time).
- `src/BadDay.Core/PainBoxParameters.cs`: the box and clock tunables, plus Lodur's parameter records as members.
- `src/BadDay.Core/Sweep.cs`: consecutive seeds, break-time min/median/max.
- `src/BadDay.Ymir/BodyPainBox.cs`: the same run on the Ymir ragdoll, with the hand-out time.
- `src/BadDay.Cli`: `baddaysim`.
- `tests/BadDay.Tests`: determinism, break-time, monotonicity and body tests.
- `tools/PainSweep`: the intensity x suppression table.

```
dotnet run --project src/BadDay.Cli -- run   --seed 1
dotnet run --project src/BadDay.Cli -- sweep --seed 1 --count 50 --pull 0.3
dotnet run --project src/BadDay.Cli -- run   --seed 1 --disable-at 5
dotnet run --project src/BadDay.Cli -- run   --seed 1 --body
dotnet run --project tools/PainSweep
dotnet run --project tests/BadDay.Tests          # exits non-zero on failure
```

`run` prints a timeline (samples every second, plus flinch, break and
withdrawal events) and a one-line summary. Options: `--intensity`, `--pull`,
`--disable-at`, `--max`, `--every`. `--body` runs it on the ragdoll (1/120 s
steps; the native Ymir library builds with cmake and a C compiler on first
build) and adds a line with the time the hand left the box. Nothing renders the puppet yet.

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
  and strain, scaled by `(1 - ObjectivePull)`, and recovers only when pain is low.
  Two seeded traits (nociceptor gain, resolve drain) make each seed a different person.
- **Goals:** `HoldHand` (the objective, with its pull) and `Withdraw`. When
  resolve reaches zero the goal flips and the hand is out after the withdrawal
  latency (100 ms).
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
tick's update, and when a Flinch and the will's break fall on the same tick the Flinch
row's goal column reads Withdraw.

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

## The office stress scenario (rung 3)

Sensory load alone: the office's stressors fill the stress bar until the will breaks, headless, one tick at a
time. The scenario is data in two parts. The **setting** names the emitters (printer, ceiling light, desk
phone, desktop notifications) and where they sit. The **cause** holds the stressors that drive them (each with
an onset, ramp, period, duration, intensity and jitter), the objective's pull (0.6) and the sense nerve the
character hears and sees with. Lodur owns what the stimulus does: sensing, load, arousal, and the arousal
drain on resolve. `StressSession` only writes the emitters' intensities each tick and logs the Actor's state;
the player's lever is `Silence(stressorId)`, from the current tick on. A run ends where the will breaks (rung 4
continues past it) or at 180 s.

The stress bar is `1 - resolve` and reads **lost composure**; the label is one constant
(`StressResult.StressBarLabel`) pending `lodur:question:stress-bar-meaning`.

- `src/BadDay.Core/Setting.cs`, `Cause.cs`: the office's defaults, in code, not persisted yet.
- `src/BadDay.Core/StressSession.cs`: one run, driven per tick by the headless run, the sweep, the CLI and a client.
- `src/BadDay.Core/StressSimulation.cs`: the headless run with scheduled silences, and the seed sweep.

```
dotnet run --project src/BadDay.Cli -- office --seed 1
dotnet run --project src/BadDay.Cli -- office --seed 1 --silence printer@20 --silence desk-phone@30
dotnet run --project src/BadDay.Cli -- office-sweep --seed 1 --count 50 --scale 1.5
```

Options: `--silence ID@S` (repeatable; `printer`, `ceiling-light`, `desk-phone`, `desktop-interrupt`), `--scale`
(every stressor's intensity), `--pull`, `--max`, `--every`. The rows are `time_ms kind audio visual interrupt
load arousal resolve stress goal`, with kinds Sample, Silenced, WillBroke and Endured. The sweep prints the
break-time min/median/max and the lowest resolve among runs that endured. The office breaks near 90 s at pull
0.6 only with the office's own sense nerve (threshold 0.05, no sensitisation, no conduction delay); Lodur's
`NerveParameters` defaults break it near 100 s. **Every number here is a placeholder.**
