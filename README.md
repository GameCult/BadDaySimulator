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

## Try it in a browser: the pain box page

`web/` is a static page (no server, no textures): a virtual human with senses, nerves and a goal, a live
readout of **pain**, **drive** and **will**, a counter of how long until the will breaks (or a scheduled
switch-off), and a button to open the box and switch it off. The sim is a TypeScript port of the C# core below,
stepped one 10 ms tick at a time, held to the C# output by parity tests. Entry points:
`web/src/painbox.ts` (the stepper), `web/src/view.ts` (counter and readout text), `web/src/main.ts` (page),
`web/test/` (parity fixtures are C# output; `e2e.mjs` drives built output in headless Chrome under its base path).

```
cd web && npm i
npm run typecheck && npm test
BASE=/void/baddaysim-pain-box/ npm run build      # BASE is the URL prefix it will be served under; BASE=./ is relocatable
PW_DIR=<dir with node_modules/playwright> node test/e2e.mjs
```

Live: pain box, nerve, reflex, drive, will, goal flip, the visitor's switch, forecast counter. Designed only:
everything in `docs/scenarios/` (the office, sensory load, coworkers), body and physics, impairment. The C# core
stays the reference; the port exists because a browser cannot run it without Blazor and a restore the build host
cannot do.

## What runs today: the pain box

A headless, deterministic simulation of one virtual human with a hand in a
pain box. Question it answers: **how long until the will breaks, or the box is
disabled first?** Entry points:

- `src/BadDay.Core/PainBoxSimulation.cs`: the model and the tick loop.
- `src/BadDay.Core/PainBoxParameters.cs`: every tunable, with defaults.
- `src/BadDay.Core/Sweep.cs`: consecutive seeds, break-time min/median/max.
- `src/BadDay.Cli`: `baddaysim`.
- `tests/BadDay.Tests`: determinism, break-time and monotonicity tests.

```
dotnet run --project src/BadDay.Cli -- run   --seed 1
dotnet run --project src/BadDay.Cli -- sweep --seed 1 --count 50 --pull 0.3
dotnet run --project src/BadDay.Cli -- run   --seed 1 --disable-at 5
dotnet run --project tests/BadDay.Tests          # exits non-zero on failure
```

`run` prints a timeline (samples every second, plus flinch, break and
withdrawal events) and a one-line summary. Options: `--intensity`, `--pull`,
`--disable-at`, `--max`, `--every`.

### Modeled

Fixed 10 ms tick, seeded SplitMix64 (the only randomness; integer arithmetic,
so a seed means the same stream anywhere). Following the layers in Lodur's
target, flattened to one loop:

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

No body, physics, balance, joints or ragdoll; no shock, adrenaline or blood
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
