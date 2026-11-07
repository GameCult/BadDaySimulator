# Scenario: Fella's bad day at the office

Status: **design, not built.** Every number below is a **placeholder** (marked `[P]`) chosen so the shape is
readable; none is measured physiology or tuned play. Nothing here is canon until the designer rules.

Concept: Meta, 2026-11-06. The protagonist's name, **Fella**, is by **Deru**.

Built on the first slice: the pain box (`PainBoxSimulation`, PR
[GameCult/BadDaySimulator#1](https://eureka.gamecult.org/forge/GameCult/BadDaySimulator/pulls/1), branch
`nibu/pain-box-first-slice`, not yet on `main`) and its sweep,
[`docs/pain-sweep.md`](https://eureka.gamecult.org/forge/GameCult/BadDaySimulator/src/branch/nibu/pain-box-first-slice/docs/pain-sweep.md).
Those links resolve on the PR branch until #1 merges. Layer and invariant names (`drive-is-state`,
`reflex-gain-from-drive`, `body-one-interface`, `content-is-cultcache`) are Lodur's
([`docs/lodur-target.md`](https://eureka.gamecult.org/forge/GameCult/Lodur/src/branch/main/docs/lodur-target.md)).

## The ladder

Meta's build order (2026-11-06): one system at a time, then connect, then full fat. Fella's whole bad day is the
top rung, not the first thing built. Each rung is demonstrable alone and measured before the next starts.

| Rung | What runs | Status |
|---|---|---|
| 1 | A Ymir body stands, falls and rests deterministically, read through the SoA view | built, draft PRs: Ymir `nibu/soa-joints` (stacked on Ymir#1), Lodur `nibu/first-slice` |
| 2 | One withdrawal reflex; time-to-break may be the main metric | built as the pain box (BDS PR #1, unmerged) |
| 3 | Sensory load drives the stress bar | not built |
| 4 | The goal flip, alone | not built |
| 5 | Fatigue and limb failure (jelly), alone | not built |
| 6 | One coworker, one personality axis | not built |
| 7 | Fella's whole bad day (this document) | design only |

A rung may use only what lower rungs built. Black Knight is an idea after rung 5, not a rung.

## Setting and cause

BDS is the whole space of bad days, not the office (Meta, 2026-11-06): the park, a factory, driving, resting at
home with a medical condition, and more. So a scenario is three things that vary independently, and neither the
engine nor the scenario schema may hard-code any of them:

- **Setting**: where and with what. Place and geometry, objects and fixtures, ambient conditions, who else is
  present, and the *emitters* in that place (a printer, a ceiling light, a phone) by stable id. A setting knows
  nothing about why the day is bad.
- **Cause**: why it is a bad day. The time-varying drivers that act on the actor (stressors, injuries, threats,
  symptoms, deadlines), the break condition, and the goal flip it triggers. A cause refers to the setting's
  emitters by id and to the actor's drive by name; it knows nothing about the room.
- **Fella** is a role filled by an actor document (traits, drive parameters, rig), not a hard-coded character:
  any actor can be Fella, and the engine never tests for the name.

| Setting | Cause | Reads as |
|---|---|---|
| office | accumulating sensory load | this document |
| factory floor | a machine fault and an injury | pain and impairment drive, no stress bar |
| car | a deadline and traffic | threat and time pressure |
| home, resting | a medical condition flaring | symptoms, fatigue; no coworkers needed |

The rest of this document is one pairing: the office setting with the sensory-load cause. Where a later section
says "stressor", "stress bar" or "Smash", that is this cause's content, authored as data in the cause document,
not engine behaviour.

## The story in two phases

1. **Composure.** Fella is at work, holding it together while the office keeps adding sensory load: noise,
   flicker, interruptions, crowding. The player (or a schedule) is the source of stress; Fella's job is to
   not break. The stress bar fills.
2. **Full ape mode.** When the bar breaks, Fella's goals do not drift, they **flip entirely**: composure no longer
   matters, nothing matters except smashing everything within reach with fists. The question phase 2 asks:
   **how long does the smashing hold while the limbs turn to jelly?** Coworkers react throughout, each by
   personality.

The pain box already has this shape in miniature: a will drains under a stimulus, and when resolve reaches zero
the goal flips (`HoldHand` to `Withdraw`). Fella reuses that mechanism instead of adding a second one. Her
composure **is** the drive's resolve; her sensory load **is** stimulus into the nerve layer, with the same
sensitisation (a loaded Fella notices more, which is what "overload" means mechanically).

## 1. The scenario as authored data

Scenario content is CultCache documents (`content-is-cultcache`). The tables below name the fields; they are
the shape of the documents, not a JSON format. A scenario document references setting, cause and actor
documents by stable id; it contains no behaviour.

**Scenario** (one document): `id`, `seed` (the only randomness, SplitMix64 as in the pain box), `setting`
(reference), `cause` (reference), `actor` (reference; the Fella role), `endConditions`, `testHooks`.

**Setting** (one document): `id`, `place` (geometry and fixtures), `emitters` (id, kind, position: the printer,
the ceiling light, the phone), `ambient`, `present` (coworker references). No schedule, no thresholds.

**Cause** (one document): `id`, `drivers` (here the stressor schedule below, each referring to a setting emitter
or a coworker by id), `breakCondition`, `flip` (the actor state change), `driveMapping` (which actor drive state
each driver feeds: for sensory load, `resolve`). Swap the cause and the same setting gives a different day; swap
the setting and the same cause plays somewhere else.

**Phases** are derived from actor state, not from a clock: phase 1 is "goal is KeepComposure", phase 2 is "goal
is Smash". There is no phase variable to desync.

| Field | Phase 1: Composure | Phase 2: Ape |
|---|---|---|
| Actor goal | `KeepComposure` (objective pull `[P] 0.6`) | `Smash` (pull 1.0) |
| Reflex gain | startle and flinch suppressed by resolve, strain accrues | suppression released; startle becomes strike |
| Muscle strength | 1.0 | `[P] 1.4` at the flip, then jelly decay (below) |
| Stress bar | fills from stressors | frozen; irrelevant |
| Ends when | break, or end of schedule (outcome `Endured`) | nothing smashable in reach, or strength below the effective floor |

### Stressor schedule

These rows are the cause document's `drivers`; the sources named are the setting's emitters.

A stressor is a document: `id`, `channel` (`audio`, `visual`, `interrupt`, `proximity`), `source` (an object or a
coworker id), `region` (ears, eyes, torso), `onsetSeconds`, `rampSeconds`, `intensity` (0..1), `durationSeconds`,
`repeat` (period, or none), `jitter` (seeded fraction). Channels are summed with a saturating curve so the
tenth beep matters less than the first but a second channel still adds (`[P]` shape: `1 - prod(1 - i_c)`).

| id | channel | source | onset | intensity | note (all `[P]`) |
|---|---|---|---|---|---|
| `printer-jam-beep` | audio | printer | 5 s, repeats 8 s | 0.25 | thin, regular, can be silenced by the player |
| `fluorescent-flicker` | visual | ceiling | 15 s, repeats 11 s | 0.2 | |
| `desk-phone` | audio | phone | 25 s, repeats 14 s | 0.35 | stops if answered (Fella can pick it up) |
| `chatty-neighbour` | interrupt | coworker `gossip` | 30 s | 0.3 | emitted by that coworker's behaviour, not a timer |
| `standup-crowd` | proximity | 4 coworkers | 60 s | 0.4 | scales with coworkers in range |
| `boss-email-ping` | interrupt | desktop | 75 s, repeats 20 s | 0.3 | |

The schedule is data: add, remove or retime a row and the scenario changes. Sources that are coworkers emit by
behaviour, so a stressor can start or stop because of what a coworker decides.

### Break condition

`resolve <= 0`, the drive's own value. Pain-box rule, unchanged: resolve drains with sensory load and strain
(holding startle down), scaled by `(1 - objectivePull)`, and recovers only when load is low. So quiet gaps
matter, and a player can silence stressors to extend phase 1. No separate "stress bar" state exists; the bar the
player sees is `1 - resolve`.

If the schedule ends with resolve above zero the outcome is `Endured` and phase 2 never runs. For testing
phase 2 alone, `testHooks.forceBreakAtSeconds` flips Fella at a fixed time (a test hook, not a game feature).

### The goal flip as an actor state change

The flip is one typed state transition, emitted as one event, `GoalFlipped { actor, from, to, tick }`. Nothing
else in the engine special-cases the break: everything downstream reads actor state.

| State | Before | After |
|---|---|---|
| `goal` | `KeepComposure` | `Smash` |
| goal weights | composure `[P] 1.0`, others per author | every weight 0 except `Smash`; a survival floor `[P] 0.1` keeps Fella from stepping off a ledge |
| objective pull | `[P] 0.6` | 1.0 |
| reflex gain from drive | suppression by resolve | suppression 0 (`reflex-gain-from-drive`: drive sets the gain) |
| adrenaline | baseline | spike `[P] 1.0`, decaying |
| target selector | off | on: nearest breakable object or fixture in reach, re-picked as things are destroyed |
| behaviour set | work, fidget, flinch | `Strike`, `Advance`, `Stagger` (limbs failing) |

Because the transition is drive state and not an authored cutscene, other scenarios can reuse it (a different
actor, a different trigger).

### Jelly limbs

The body layer takes per-muscle strength (`body-one-interface`), so jelly is strength, not damage. Each limb
group has `strength` in 0..1, scaled by adrenaline and drained by effort: `strength -= effortDrain * work(strike
impulse)` per strike, with slow recovery in lulls, plus an adrenaline-crash term that bites after the spike
decays (`[P]` rates). Fine control goes first: forearm and wrist strength fall before shoulders, so punches
land late, wide, then weakly. At `strength < [P] 0.25` a strike no longer breaks an object (the effective floor);
below `[P] 0.1` the arm will not lift. The scenario ends phase 2 at no smashable target left or all arms below
the floor, and records which one ended it.

## 2. Measurable outputs per phase

Deru's open question (`docs/pain-sweep.md`) is whether time-to-break is the right measure. Our answer: it is one
number from a run, and "never" is censored data, not a value. So report a **distribution and a margin**, plus
per-phase quantities that say what a player would feel. Recommendation, not a ruling, with low confidence until
a player has felt both candidate sets.

Outputs are computed from the timeline and the contact facts; none is a state the simulation reads.

**Phase 1 (composure)**
- `timeToFirstFlinch`, `flinchCount`, `flinchRate` over the last 30 s before the break.
- `sensoryLoadIntegral`: area under the combined load before the break or the end.
- `peakStrain` and `strainIntegral` (the cost of holding it together).
- `composureMargin`: minimum resolve reached when the run did not break; how close it came.
- `drainShare` per stressor channel and source: which stressor cost the most resolve.
- `silenceUsed`: stressors the player or Fella removed, and the resolve regained in lulls.
- `timeToBreak`, with the outcome (`WillBroke`, `Endured`), reported as a survival curve over a seed batch
  (fraction broken by 30, 60, 90 s), never a bare median.

**Phase 2 (ape)**
- `timeToFirstStrike` (flip to first impact) and `strikesLanded`.
- `objectsDestroyed` and `impulseDelivered` (sum of impact impulse, from contact facts).
- `effectivenessCurve`: fraction of strikes that break their target, by 5 s windows.
- `jellyOnsetSeconds`: time until the first limb group is under `[P] 0.5` strength.
- `holdingTimeSeconds`: flip to the last effective blow; the headline phase-2 number.
- `endReason`: `NoTargets`, `StrengthFloor`, or `Interrupted` (coworker restrained Fella). This one matters:
  holding time that ends for lack of targets says nothing about jelly.
- `collateral`: coworkers struck, and objects broken that were not the nearest.

**Both**: `coworkerOutcomes` per coworker (see section 3): reaction taken, first-reaction latency, distance
kept, whether it changed the phase-1 stress load or phase-2 end reason.

## 3. Coworker personality as data

A coworker is a document: `id`, `body` (same rig as any puppet), `axes` (below), `stressorsEmitted`, and
behaviour weights. As elsewhere in Lodur (`drive-is-state`), a coworker does not run a per-personality lookup of
"if X then Y": the axes set the weights of one shared behaviour selection over the coworker's own drive and
what it perceives of Fella.

Axes, each 0..1 (`[P]` set; add or drop as play teaches):

| Axis | Low | High |
|---|---|---|
| `courage` | freezes, flees | steps toward the threat |
| `empathy` | indifferent | tries to help or comfort |
| `assertiveness` | defers, silent | commands, de-escalates, restrains |
| `reactivity` | slow to register Fella | notices the first flinch and spreads it |
| `contagion` | own composure unaffected | own resolve drains with Fella's visible strain |

Reaction catalogue (behaviours, selected by weight; weights are per-document `[P]`): `ignore`, `freeze`, `flee`,
`hide`, `film` (a stressor, see below), `soothe`, `command`, `intervene`, `restrain`, `join_in` (contagion
break). Example weights for the five authored archetypes in this scenario (`[P]`):

| Coworker | courage | empathy | assertiveness | reactivity | contagion | likely reaction |
|---|---|---|---|---|---|---|
| `gossip` | 0.3 | 0.4 | 0.2 | 0.9 | 0.5 | films, spreads it, emits `chatty-neighbour` |
| `manager` | 0.6 | 0.3 | 0.9 | 0.5 | 0.2 | commands; stays near in phase 1 (extra load), restrains in 2 |
| `intern` | 0.1 | 0.6 | 0.1 | 0.6 | 0.8 | freezes, then flees; may break too |
| `friend` | 0.7 | 0.9 | 0.4 | 0.8 | 0.3 | soothes in phase 1; intervenes in phase 2 |
| `bystander` | 0.2 | 0.2 | 0.2 | 0.2 | 0.1 | ignores until a thing flies at them |

Coworkers feed the scenario through three authored paths only: stressors they emit, perceived-load changes
(proximity), and phase-2 `restrain` or `intervene`. Anything else they do is visible but must not alter Fella's
resolve, so the personality effect can be isolated in testing.

## 4. What Lodur and Ymir must expose

Lodur (all under Lodur's layer rules; none of it is built):
- Actor drive state readable and writable per tick: `resolve`, `adrenaline`, per-limb-group `strength`, `goal`,
  goal weights, objective pull. The goal as first-class state with a typed `GoalFlipped` event.
- Perception of non-contact stimuli by channel and region (audio, visual, interrupt, proximity), feeding the same
  nerve and sensitisation layer as the pain box's contact stimulus.
- Reflex gain set from drive, including a mapping from startle to strike when suppression is released.
- A strike behaviour with a target selector over breakables in reach, emitting body targets and per-muscle
  strength (`body-one-interface`), never joints or forces.
- A fatigue model for per-muscle strength (effort drain, recovery, adrenaline crash), deterministic and
  allocation-free per tick (`bounded-tick`).
- Coworker perception of another actor's visible state (strain, flinch, goal) and the shared behaviour
  selection weighted by axes.

Ymir (the pinned-Box3D boundary, `GameCult/Ymir`; the SoA world is PR
[GameCult/Ymir#1](https://eureka.gamecult.org/forge/GameCult/Ymir/pulls/1)):
- Contact facts per body pair with relative speed (the planar session has Begin/Hit/End; the 3D SoA world has
  none yet) to compute impact impulse for `impulseDelivered` and breaking.
- Breakable objects: a break threshold on a joint or weld, or removal of a body on a destroy call. Today the SoA
  world has a fixed capacity and no removal.
- Body layout beyond spheres: capsule limbs, boxes, joints with state lanes (angle, speed, motor target), and
  per-body force and torque write lanes. Follow-up `nibu-body:follow_up:ymir-soa-lodur-needs` lists these.
- Same-input same-bits stepping and an in-place state view, so metrics read lanes without copying and a seed
  reproduces a run.
- Stable ids for actors and objects so scenario documents refer to them.

## 5. Three tuning checks

Each can run headless in the style of the pain-sweep and fails loudly.

1. **Phase 1 is monotone and seeded.** For seeds 1..50, raising a stressor's intensity or adding a stressor never
   lengthens the median time-to-break, and removing all stressors never breaks. The default scenario breaks for
   at least 80% of seeds within the schedule (`[P]`: window 90 s) and the 50-seed survival curve is not a step
   (the 10th and 90th percentile of break time differ by more than `[P] 20%`), or the scenario cannot
   discriminate players. The same seed reproduces the timeline byte for byte.
2. **Phase 2 ends for the stated reason.** Force the break at fixed times over a grid of jelly drain rate and
   target supply (few, many). With plenty of targets, `holdingTimeSeconds` falls monotonically as drain rises
   and `endReason` is `StrengthFloor`; with few targets and slow drain, `endReason` is `NoTargets` and holding
   time does not move with drain. If holding time is insensitive to the drain at plentiful targets, the jelly
   model does nothing and the check fails.
3. **Personality separates and stays isolated.** With the schedule fixed, set each coworker axis to 0 and to 1
   in turn: each setting changes that coworker's reaction mix in a stated direction (courage up, fewer `flee`;
   assertiveness up, more `command` and `restrain`), and across the five archetypes at least four distinct
   first reactions occur. Separately, with coworker behaviour outside the three authored paths disabled, the
   phase 1 distribution from check 1 does not change; if it does, a coworker is leaking into the stress bar
   by an unauthored route.

## Not decided

- Whether phase 2 can be interrupted back into phase 1 (a restrained Fella recovering), or is one-way.
- Whether coworkers can also break, which makes this a cascade scenario.
- Rig, level geometry and what counts as "breakable"; those belong to the sandbox, not this document.
- Whether the player controls Fella in phase 1 or only the stressors.
