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

## What runs today: the pain box

A deterministic simulation of one virtual human with a hand in a pain box,
run headless or on a Ymir ragdoll. Question it answers: **how long until the
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
are byte-identical (the loop uses only `+ - * /` and comparisons, no
transcendental functions).

Persistent content (puppet definitions, drive parameters, saved levels) will
be CultCache per Lodur's `content-is-cultcache`; this slice has none, so it
has no cache layer and no JSON.

### Build note

The core has no engine reference (`core-engine-free`). The slice has no
package dependencies, so the tests are a plain console program rather than
xunit. On the Yggdrasil mind host `dotnet restore` currently fails on a
NuGet named mutex (read-only `/dev/shm`); the slice was compiled there with
Roslyn's `csc` directly, which is why the `.csproj` files are the unverified part.
