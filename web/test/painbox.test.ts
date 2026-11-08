import { test } from "node:test";
import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
import { readdirSync, readFileSync } from "node:fs";
import { PainBox, render, run, summary } from "../src/painbox.js";
import { defaultParameters, type PainBoxParameters } from "../src/params.js";
import { SplitMix64 } from "../src/splitmix64.js";
import { barFraction, counterView, sensesText } from "../src/view.js";

const PARITY = pathToFileURL(process.cwd() + "/test/parity/"); // tests run from web/

// Same option names and defaults as src/BadDay.Cli.
function paramsFromArgs(args: string[]): { seed: bigint; p: PainBoxParameters } {
  const o = new Map<string, string>();
  for (let i = 0; i < args.length; i += 2) o.set(args[i].slice(2), args[i + 1]);
  return {
    seed: BigInt(o.get("seed")!),
    p: {
      ...defaultParameters,
      boxIntensity: Number(o.get("intensity") ?? 0.8),
      objectivePull: Number(o.get("pull") ?? 0.5),
      boxDisableAtSeconds: o.has("disable-at") ? Number(o.get("disable-at")) : null,
      maxSeconds: Number(o.get("max") ?? 120),
      sampleEverySeconds: Number(o.get("every") ?? 1),
    },
  };
}

test("SplitMix64 matches the published seed-0 vector", () => {
  const r = new SplitMix64(0n);
  assert.equal(r.nextUInt64(), 0xe220a8397b1dcdafn);
  assert.equal(r.nextUInt64(), 0x6e789e6aa1b965f4n);
});

// The fixtures are the C# reference's output (`baddaysim run ...`); see test/parity/README.md.
for (const file of readdirSync(PARITY).filter((f) => f.endsWith(".txt"))) {
  test(`parity with the C# reference: ${file}`, () => {
    const text = readFileSync(new URL(file, PARITY), "utf8");
    const [header, ...rest] = text.split("\n");
    const { seed, p } = paramsFromArgs(header.replace("# baddaysim run ", "").split(" "));
    const r = run(p, seed);
    assert.equal(render(r) + summary(r) + "\n", rest.join("\n"));
  });
}

test("same seed and parameters render byte-identically; another seed differs", () => {
  const a = render(run(defaultParameters, 5n));
  assert.equal(render(run(defaultParameters, 5n)), a);
  assert.notEqual(render(run(defaultParameters, 6n)), a);
});

test("the default box breaks the will, and a stronger box breaks it sooner", () => {
  const mild = run({ ...defaultParameters, boxIntensity: 0.6 }, 1n);
  const strong = run({ ...defaultParameters, boxIntensity: 1.0 }, 1n);
  assert.equal(strong.outcome, "WillBroke");
  assert.ok(strong.timeToBreakMs! < (mild.timeToBreakMs ?? Infinity));
});

test("the visitor's switch equals a scheduled disable at the same instant", () => {
  const scheduled = run({ ...defaultParameters, boxDisableAtSeconds: 4.0, sampleEverySeconds: 0.1 }, 3n);
  const sim = new PainBox({ ...defaultParameters, sampleEverySeconds: 0.1 }, 3n);
  while (sim.currentTick < 400) sim.step();
  sim.disableBox();
  while (!sim.done) sim.step();
  assert.equal(scheduled.outcome, "BoxDisabled");
  assert.equal(render(sim.result()), render(scheduled));
});

test("switching the box off before the break keeps the will; after the break it does not save the hand", () => {
  const early = new PainBox(defaultParameters, 1n);
  while (early.currentTick < 500) early.step();
  early.disableBox();
  while (!early.done) early.step();
  assert.equal(early.result().outcome, "BoxDisabled");
  assert.equal(early.result().timeToBreakMs, null);

  const late = new PainBox(defaultParameters, 1n);
  const breakMs = run(defaultParameters, 1n).timeToBreakMs!;
  while (late.currentTick < breakMs / 10 + 1) late.step();
  late.disableBox();
  while (!late.done) late.step();
  assert.equal(late.result().outcome, "WillBroke");
});

test("the forecast is the exact future when nothing is toggled, and does not disturb the run", () => {
  const sim = new PainBox(defaultParameters, 2n);
  while (sim.currentTick < 700) sim.step();
  const before = JSON.stringify(sim.readout);
  const f = sim.forecast();
  assert.equal(JSON.stringify(sim.readout), before);
  while (!sim.done) sim.step();
  const actual = sim.result();
  assert.equal(f.outcome, actual.outcome);
  assert.equal(f.timeToBreakMs, actual.timeToBreakMs);
  assert.equal(f.endMs, actual.endMs);
  assert.equal(render(f), render(actual));
});

test("a clone does not share state with the original", () => {
  const a = new PainBox(defaultParameters, 8n);
  while (a.currentTick < 300) a.step();
  const b = a.clone();
  for (let i = 0; i < 200; i++) b.step();
  assert.equal(a.currentTick, 300);
  const rest = () => { const s = a.clone(); while (!s.done) s.step(); return render(s.result()); };
  const fresh = new PainBox(defaultParameters, 8n);
  while (!fresh.done) fresh.step();
  assert.equal(rest(), render(fresh.result()));
});

test("pain, drive and will move the way the model says: pain up, will down while held", () => {
  const sim = new PainBox(defaultParameters, 1n);
  while (sim.currentTick < 100) sim.step();
  const early = sim.readout;
  while (sim.currentTick < 1000) sim.step();
  const later = sim.readout;
  assert.ok(later.pain > early.pain);
  assert.ok(later.resolve < early.resolve);
  assert.ok(later.drive > 0);
  assert.equal(later.goal, "HoldHand");
});

test("counter counts down to the break and then reports it", () => {
  const sim = new PainBox(defaultParameters, 1n);
  const f = sim.forecast();
  assert.equal(counterView("closed", sim.readout, f, null).value, "--");
  assert.equal(counterView("open", sim.readout, f, null).value, (f.timeToBreakMs! / 1000).toFixed(1) + " s");
  while (sim.currentTick < 500) sim.step();
  assert.equal(counterView("open", sim.readout, f, null).value, ((f.timeToBreakMs! - sim.readout.timeMs) / 1000).toFixed(1) + " s");
  while (!sim.done) sim.step();
  const end = counterView("ended", sim.readout, f, sim.result());
  assert.equal(end.value, (f.timeToBreakMs! / 1000).toFixed(1) + " s");
  assert.match(end.note, /will broke/);
});

test("a scheduled switch-off earlier than the break is what the counter counts to", () => {
  const sim = new PainBox({ ...defaultParameters, boxDisableAtSeconds: 5 }, 1n);
  const f = sim.forecast();
  assert.equal(f.outcome, "BoxDisabled");
  const c = counterView("open", sim.readout, f, null);
  assert.equal(c.value, "5.0 s");
  assert.match(c.note, /switches off/);
});

test("a gentle box that never breaks the will reads 'never' and says the limit", () => {
  const p = { ...defaultParameters, boxIntensity: 0.2, maxSeconds: 30 };
  const sim = new PainBox(p, 1n);
  const f = sim.forecast();
  assert.equal(f.outcome, "Endured");
  assert.equal(counterView("open", sim.readout, f, null).value, "never");
});

test("view helpers clamp and describe", () => {
  assert.equal(barFraction(3), 1);
  assert.equal(barFraction(-1), 0);
  assert.equal(barFraction(1, 2), 0.5);
  const sim = new PainBox(defaultParameters, 1n);
  assert.match(sensesText(sim.readout, "closed"), /closed box/);
  while (sim.currentTick < 300) sim.step();
  assert.match(sensesText(sim.readout, "open"), /keep the hand in the box/);
});
