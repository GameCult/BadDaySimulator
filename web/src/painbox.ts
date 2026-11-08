import { SplitMix64 } from "./splitmix64.js";
import type { PainBoxParameters } from "./params.js";

export type Goal = "HoldHand" | "Withdraw";
export type Outcome = "WillBroke" | "BoxDisabled" | "Endured";
export type EntryKind = "Sample" | "Flinch" | "WillBroke" | "Withdrew" | "BoxDisabled" | "Endured";

export interface TimelineEntry {
  timeMs: number;
  kind: EntryKind;
  stimulus: number;
  pain: number;
  strain: number;
  resolve: number;
  goal: Goal;
}

export interface PainBoxResult {
  seed: bigint;
  outcome: Outcome;
  endMs: number;
  /** Time resolve hit zero, ms; null unless outcome is WillBroke. */
  timeToBreakMs: number | null;
  flinches: number;
  timeline: TimelineEntry[];
}

/** What the nervous-system readout shows at one instant. */
export interface Readout {
  timeMs: number;
  stimulus: number;
  /** Afferent signal arriving at the centre this tick, after sensitisation and conduction delay. */
  drive: number;
  pain: number;
  strain: number;
  /** The will. */
  resolve: number;
  sensitisation: number;
  goal: Goal;
  flinches: number;
  boxOn: boolean;
}

// C# Math.Round is banker's rounding; Math.round here is half-up.
function roundEven(x: number): number {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}
const ticks = (seconds: number, tickSeconds: number) => roundEven(seconds / tickSeconds);
const ms = (tick: number, tickSeconds: number) => roundEven(tick * tickSeconds * 1000.0);

const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

/**
 * One virtual human with a hand in a pain box, ported from
 * src/BadDay.Core/PainBoxSimulation.cs and stepped one tick at a time so a page
 * can drive it. The arithmetic order is the C# loop's; the parity fixtures under
 * test/parity are C# output, and the tests hold this port to them.
 *
 * The only addition is the stepper itself: `disableBox()` is the visitor's
 * toggle, equivalent to a run whose boxDisableAtSeconds is the current time.
 */
export class PainBox {
  readonly seed: bigint;
  private readonly p: PainBoxParameters;
  private rng: SplitMix64;
  private readonly gainTrait: number;
  private readonly drainTrait: number;

  private readonly maxTick: number;
  private disableTick: number;
  private readonly sampleEvery: number;
  private readonly refractory: number;
  private readonly withdrawLatency: number;
  private delay: Float64Array;

  private tick = 0;
  private sens = 1.0;
  private pain = 0.0;
  private strain = 0.0;
  private resolve = 1.0;
  private stimulus = 0.0;
  private drive = 0.0;
  private goal: Goal = "HoldHand";
  private lastFlinch = Number.MIN_SAFE_INTEGER / 2;
  private breakTick = -1;
  private withdrawTick = -1;
  private flinches = 0;
  private boxOn = true;
  private outcome: Outcome = "Endured";
  private endTick: number;
  private finished = false;
  private timeline: TimelineEntry[] = [];

  constructor(p: PainBoxParameters, seed: bigint) {
    this.p = p;
    this.seed = seed;
    this.rng = new SplitMix64(seed);
    // Per-seed traits, drawn first and in fixed order so the stream never shifts.
    this.gainTrait = 1.0 + p.traitJitter * this.rng.nextSigned();
    this.drainTrait = 1.0 + p.traitJitter * this.rng.nextSigned();
    this.maxTick = ticks(p.maxSeconds, p.tickSeconds);
    this.disableTick = p.boxDisableAtSeconds !== null ? ticks(p.boxDisableAtSeconds, p.tickSeconds) : Infinity;
    this.sampleEvery = Math.max(1, ticks(p.sampleEverySeconds, p.tickSeconds));
    this.refractory = ticks(p.flinchRefractorySeconds, p.tickSeconds);
    this.withdrawLatency = Math.max(1, ticks(p.withdrawalLatencySeconds, p.tickSeconds));
    this.delay = new Float64Array(Math.max(1, ticks(p.conductionDelaySeconds, p.tickSeconds)));
    this.endTick = this.maxTick;
  }

  get done(): boolean {
    return this.finished;
  }

  get readout(): Readout {
    return {
      timeMs: ms(this.tick, this.p.tickSeconds),
      stimulus: this.stimulus,
      drive: this.drive,
      pain: this.pain,
      strain: this.strain,
      resolve: this.resolve,
      sensitisation: this.sens,
      goal: this.goal,
      flinches: this.flinches,
      boxOn: this.boxOn,
    };
  }

  get currentTick(): number {
    return this.tick;
  }

  /** The visitor switches the box off: it takes effect on the next tick. */
  disableBox(): void {
    if (!this.finished && this.disableTick > this.tick) this.disableTick = this.tick;
  }

  /** Runs to the end from here, on a copy. The sim is deterministic, so this is the exact future if nothing is toggled. */
  forecast(): PainBoxResult {
    const copy = this.clone();
    while (!copy.finished) copy.step();
    return copy.result();
  }

  clone(): PainBox {
    const c: PainBox = Object.create(PainBox.prototype);
    Object.assign(c, this);
    c.rng = this.rng.clone();
    c.delay = this.delay.slice();
    c.timeline = this.timeline.slice();
    return c;
  }

  result(): PainBoxResult {
    return {
      seed: this.seed,
      outcome: this.outcome,
      endMs: ms(this.endTick, this.p.tickSeconds),
      timeToBreakMs: this.breakTick >= 0 ? ms(this.breakTick, this.p.tickSeconds) : null,
      flinches: this.flinches,
      timeline: this.timeline,
    };
  }

  private log(kind: EntryKind): void {
    this.timeline.push({
      timeMs: ms(this.tick, this.p.tickSeconds),
      kind,
      stimulus: this.stimulus,
      pain: this.pain,
      strain: this.strain,
      resolve: this.resolve,
      goal: this.goal,
    });
  }

  /** Advances one tick: one iteration of the C# loop body. Returns the entries logged this tick. */
  step(): TimelineEntry[] {
    if (this.finished) return [];
    const p = this.p;
    const first = this.timeline.length;
    const tick = this.tick;
    const dt = p.tickSeconds;
    this.boxOn = tick < this.disableTick;

    if (tick === this.disableTick && this.goal === "HoldHand") {
      this.stimulus = 0.0;
      this.drive = 0.0;
      this.outcome = "BoxDisabled";
      this.endTick = tick;
      this.log("BoxDisabled");
      this.finished = true;
      return this.timeline.slice(first);
    }

    // Box -> stimulus (always draw jitter so the stream is independent of state).
    const jitter = this.rng.nextSigned();
    const ramp = Math.min(1.0, (tick * dt) / p.boxRampSeconds);
    this.stimulus = this.boxOn ? Math.max(0.0, p.boxIntensity * ramp * (1.0 + p.boxNoise * jitter)) : 0.0;

    // Nociceptor: threshold, sensitisation, then conduction delay.
    const above = Math.max(0.0, this.stimulus - p.nociceptorThreshold);
    this.sens += dt * (p.sensitisationGrowth * above - p.sensitisationRecovery * (this.sens - 1.0));
    this.sens = clamp(this.sens, 1.0, p.sensitisationCap);
    const slot = tick % this.delay.length;
    const arrived = this.delay[slot]; // written delayTicks ticks ago
    this.delay[slot] = above * this.sens * this.gainTrait;
    this.drive = arrived;

    // Pain: afferent drives it up toward 1, it decays on its own.
    this.pain += dt * (p.painRise * arrived * (1.0 - this.pain) - p.painDecay * this.pain);
    this.pain = clamp(this.pain, 0.0, 1.0);

    // Reflex: will holds down a fraction of it; the leak is a flinch, the effort is strain.
    const suppression = this.goal === "HoldHand" ? p.maxSuppression * this.resolve : 0.0;
    const leak = arrived * (1.0 - suppression);
    if (this.goal === "HoldHand" && leak > p.flinchLeakThreshold && tick - this.lastFlinch >= this.refractory) {
      this.lastFlinch = tick;
      this.flinches++;
      this.log("Flinch");
    }
    this.strain += dt * (arrived * suppression - p.strainRecover * this.strain);
    this.strain = Math.max(0.0, this.strain);

    // Will decays under load, scaled down by how hard the objective pulls.
    if (this.goal === "HoldHand") {
      const drain =
        (p.resolveDrainPain * this.pain + p.resolveDrainStrain * this.strain) * (1.0 - p.objectivePull) * this.drainTrait;
      const recovery = this.pain < p.resolveRecoversBelowPain ? p.resolveRecovery * (1.0 - this.resolve) : 0.0;
      this.resolve = clamp(this.resolve + dt * (recovery - drain), 0.0, 1.0);
      if (this.resolve <= 0.0) {
        this.goal = "Withdraw";
        this.breakTick = tick;
        this.withdrawTick = tick + this.withdrawLatency;
        this.log("WillBroke");
      }
    }

    if (tick % this.sampleEvery === 0) this.log("Sample");

    if (tick === this.withdrawTick) {
      this.outcome = "WillBroke";
      this.endTick = tick;
      this.log("Withdrew");
      this.finished = true;
    } else if (tick === this.maxTick) {
      this.log("Endured");
      this.finished = true;
    }
    this.tick++;
    return this.timeline.slice(first);
  }
}

export function run(p: PainBoxParameters, seed: bigint): PainBoxResult {
  const sim = new PainBox(p, seed);
  while (!sim.done) sim.step();
  return sim.result();
}

const f3 = (x: number) => x.toFixed(3);

/** Same text form as PainBoxResult.Render() in C#. */
export function render(r: PainBoxResult): string {
  let s = "time_ms kind stimulus pain strain resolve goal\n";
  for (const e of r.timeline) {
    s += `${e.timeMs} ${e.kind} ${f3(e.stimulus)} ${f3(e.pain)} ${f3(e.strain)} ${f3(e.resolve)} ${e.goal}\n`;
  }
  return s;
}

export function summary(r: PainBoxResult): string {
  switch (r.outcome) {
    case "WillBroke":
      return `seed ${r.seed}: will broke at ${r.timeToBreakMs} ms, hand out at ${r.endMs} ms, ${r.flinches} flinches`;
    case "BoxDisabled":
      return `seed ${r.seed}: box disabled at ${r.endMs} ms, will held, ${r.flinches} flinches`;
    default:
      return `seed ${r.seed}: endured to ${r.endMs} ms, will held, ${r.flinches} flinches`;
  }
}
