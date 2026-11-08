import { PainBox, type PainBoxResult, type Readout } from "./painbox.js";
import { defaultParameters, type PainBoxParameters } from "./params.js";
import { barFraction, counterView, DRIVE_FULL, sensesText, seconds, type Phase } from "./view.js";

const $ = <T extends Element>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`missing #${id}`);
  return el as unknown as T;
};

const query = new URLSearchParams(location.search);
const seedIn = $<HTMLInputElement>("seed");
const intensityIn = $<HTMLInputElement>("intensity");
const pullIn = $<HTMLInputElement>("pull");
const autoOffIn = $<HTMLInputElement>("autooff");
const speedIn = $<HTMLSelectElement>("speed");
if (query.has("seed")) seedIn.value = query.get("seed")!;
if (query.has("speed")) speedIn.value = query.get("speed")!;

const METERS = [
  { id: "pain", name: "Pain", color: "var(--pain)", get: (r: Readout) => barFraction(r.pain), minor: false },
  { id: "drive", name: "Drive", color: "var(--drive)", get: (r: Readout) => barFraction(r.drive, DRIVE_FULL), minor: false },
  { id: "will", name: "Will", color: "var(--will)", get: (r: Readout) => barFraction(r.resolve), minor: false },
  { id: "strain", name: "Strain", color: "var(--strain)", get: (r: Readout) => barFraction(r.strain, 4), minor: true },
] as const;
const meterEls = new Map<string, { fill: HTMLElement; val: HTMLElement }>();
for (const m of METERS) {
  const row = document.createElement("div");
  row.className = "meter" + (m.minor ? " minor" : "");
  row.innerHTML = `<span class="name">${m.name}</span><div class="track" role="meter" aria-label="${m.name}" aria-valuemin="0" aria-valuemax="100"><div class="fill" style="--c:${m.color}"></div></div><span class="val"></span>`;
  $("meters").appendChild(row);
  meterEls.set(m.id, { fill: row.querySelector(".fill")!, val: row.querySelector(".val")! });
}

function currentParameters(): PainBoxParameters {
  const off = Number(autoOffIn.value);
  return {
    ...defaultParameters,
    boxIntensity: Number(intensityIn.value),
    objectivePull: Number(pullIn.value),
    boxDisableAtSeconds: off > 0 ? off : null,
  };
}

// --- run state ---------------------------------------------------------------
let params = currentParameters();
let sim = new PainBox(params, BigInt(Math.max(0, Math.floor(Number(seedIn.value) || 0))));
let forecast: PainBoxResult = sim.forecast();
let phase: Phase = "closed";
let final: PainBoxResult | null = null;
let history: Readout[] = [];
let flinchTimes: number[] = [];
let lastFlinchMs = -1e9;
let breakMs: number | null = null;
let accumulator = 0;

function restart(open: boolean): void {
  params = currentParameters();
  sim = new PainBox(params, BigInt(Math.max(0, Math.floor(Number(seedIn.value) || 0))));
  forecast = sim.forecast();
  final = null;
  history = [];
  flinchTimes = [];
  lastFlinchMs = -1e9;
  breakMs = null;
  accumulator = 0;
  phase = open ? "open" : "closed";
  $("status").textContent = open ? "Box opened." : "Box closed.";
  render();
}

$("toggle").addEventListener("click", () => {
  if (phase === "closed") restart(true);
  else if (phase === "open") sim.disableBox();
  else restart(true);
});
$("reset").addEventListener("click", () => restart(false));
for (const el of [seedIn, intensityIn, pullIn, autoOffIn]) el.addEventListener("change", () => restart(false));
for (const el of [intensityIn, pullIn]) el.addEventListener("input", () => syncOutputs());
function syncOutputs(): void {
  $("intensity-v").textContent = Number(intensityIn.value).toFixed(2);
  $("pull-v").textContent = Number(pullIn.value).toFixed(2);
}

// --- time ----------------------------------------------------------------------
let lastFrame = performance.now();
function frame(now: number): void {
  const realDt = Math.min(0.1, (now - lastFrame) / 1000);
  lastFrame = now;
  if (phase === "open") {
    accumulator += realDt * Number(speedIn.value);
    let steps = 0;
    while (accumulator >= params.tickSeconds && !sim.done && steps < 1600) {
      accumulator -= params.tickSeconds;
      steps++;
      for (const e of sim.step()) {
        if (e.kind === "Flinch") { lastFlinchMs = e.timeMs; flinchTimes.push(e.timeMs); }
        if (e.kind === "WillBroke") { breakMs = e.timeMs; $("status").textContent = "The will broke."; }
      }
      if (sim.currentTick % 5 === 0 || sim.done) history.push(sim.readout);
    }
    if (sim.done) {
      final = sim.result();
      phase = "ended";
      $("status").textContent = final.outcome === "WillBroke" ? "The hand is out." : final.outcome === "BoxDisabled" ? "Box switched off. The will held." : "Time ran out. The will held.";
    }
  }
  render();
  requestAnimationFrame(frame);
}

// --- drawing -------------------------------------------------------------------
const lerp = (a: number, b: number, t: number) => a + (b - a) * t;
const PULSES = 6;
const pulseEls: SVGCircleElement[] = [];
for (let i = 0; i < PULSES; i++) {
  const c = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  c.setAttribute("fill", "#6d60ff");
  $("pulses").appendChild(c);
  pulseEls.push(c);
}
const NERVE: [number, number][] = [[505, 262], [400, 236], [300, 216], [215, 195], [190, 170], [190, 150]];
function nervePoint(t: number): [number, number] {
  const seg = Math.min(NERVE.length - 2, Math.floor(t * (NERVE.length - 1)));
  const u = t * (NERVE.length - 1) - seg;
  return [lerp(NERVE[seg][0], NERVE[seg + 1][0], u), lerp(NERVE[seg][1], NERVE[seg + 1][1], u)];
}

function render(): void {
  const r = sim.readout;
  for (const m of METERS) {
    const els = meterEls.get(m.id)!;
    const f = m.get(r);
    els.fill.style.width = `${(f * 100).toFixed(1)}%`;
    els.val.textContent = f.toFixed(2);
    els.fill.parentElement!.setAttribute("aria-valuenow", String(Math.round(f * 100)));
  }
  const c = counterView(phase, r, forecast, final);
  $("counter").textContent = c.value;
  $("counter-note").textContent = c.note;
  $("toggle").textContent = phase === "closed" ? "Open the pain box" : phase === "open" ? "Switch the box off" : "Open the box again";
  $("senses").textContent = sensesText(r, phase);
  syncOutputs();
  drawScene(r);
  drawChart();
}

function drawScene(r: Readout): void {
  const open = phase !== "closed";
  $("lid").setAttribute("transform", open ? "rotate(-70 456 222)" : "");
  $("box-glow").setAttribute("opacity", String(open && r.boxOn ? Math.min(1, r.stimulus) : 0));
  // Hand: hovering before the box opens, in the box while held, flinching, then out after the break.
  let hx = open ? 505 : 440, hy = open ? 262 : 196;
  if (open) {
    const since = r.timeMs - lastFlinchMs;
    if (since >= 0 && since < 300) { const k = 1 - since / 300; hx -= 16 * k; hy -= 10 * k; }
    if (breakMs !== null) { const k = Math.min(1, (r.timeMs - breakMs) / 100); hx = lerp(hx, 400, k); hy = lerp(hy, 140, k); }
  }
  $("hand").setAttribute("cx", String(hx));
  $("hand").setAttribute("cy", String(hy));
  $("arm-line").setAttribute("points", `190,190 ${lerp(190, hx, 0.55).toFixed(1)},${lerp(190, hy, 0.5).toFixed(1)} ${hx.toFixed(1)},${hy.toFixed(1)}`);
  // Face follows pain and will.
  const pain = open ? r.pain : 0;
  $("mouth").setAttribute("d", `M176 150 Q190 ${(156 - 22 * pain).toFixed(1)} 204 150`);
  $("brow-l").setAttribute("d", `M166 ${(114 - 2 * pain).toFixed(1)} L186 ${(114 + 7 * pain).toFixed(1)}`);
  $("brow-r").setAttribute("d", `M194 ${(114 + 7 * pain).toFixed(1)} L214 ${(114 - 2 * pain).toFixed(1)}`);
  $("head").setAttribute("stroke", r.goal === "Withdraw" ? "#ff8a2a" : "#b7c7d9");
  // Nerve pulses travel hand to head; their brightness is the drive.
  const strength = open ? Math.min(1, r.drive / DRIVE_FULL * 1.5) : 0;
  pulseEls.forEach((el, i) => {
    const [x, y] = nervePoint(((r.timeMs / 1000) * 0.9 + i / PULSES) % 1);
    el.setAttribute("cx", x.toFixed(1));
    el.setAttribute("cy", y.toFixed(1));
    el.setAttribute("r", (2 + 3 * strength).toFixed(1));
    el.setAttribute("opacity", (0.05 + 0.95 * strength).toFixed(2));
  });
}

const canvas = $<HTMLCanvasElement>("chart");
const ctx2d = canvas.getContext("2d")!;
let drawnSamples = -1;
function drawChart(): void {
  if (drawnSamples === history.length && phase !== "closed") return;
  drawnSamples = history.length;
  const W = canvas.width, H = canvas.height, pad = 6;
  ctx2d.clearRect(0, 0, W, H);
  ctx2d.fillStyle = "#07111a"; ctx2d.fillRect(0, 0, W, H);
  const span = Math.max(1000, forecast.endMs);
  const X = (ms: number) => pad + (ms / span) * (W - 2 * pad);
  const Y = (f: number) => H - pad - 14 - f * (H - 2 * pad - 14);
  ctx2d.strokeStyle = "#16212c"; ctx2d.lineWidth = 1;
  for (let f = 0; f <= 1; f += 0.5) { ctx2d.beginPath(); ctx2d.moveTo(pad, Y(f)); ctx2d.lineTo(W - pad, Y(f)); ctx2d.stroke(); }
  ctx2d.fillStyle = "#63758a"; ctx2d.font = "11px monospace";
  ctx2d.fillText("0 s", pad, H - 2);
  ctx2d.fillText(seconds(span), W - pad - 44, H - 2);
  const line = (color: string, get: (r: Readout) => number) => {
    ctx2d.strokeStyle = color; ctx2d.lineWidth = 2; ctx2d.beginPath();
    history.forEach((h, i) => (i === 0 ? ctx2d.moveTo(X(h.timeMs), Y(get(h))) : ctx2d.lineTo(X(h.timeMs), Y(get(h)))));
    ctx2d.stroke();
  };
  line("#ff8a2a", (h) => barFraction(h.pain));
  line("#6d60ff", (h) => barFraction(h.drive, DRIVE_FULL));
  line("#59b7ff", (h) => barFraction(h.resolve));
  ctx2d.fillStyle = "#eef5ff";
  for (const t of flinchTimes) ctx2d.fillRect(X(t) - 1, H - pad - 12, 2, 8);
}

restart(query.get("open") === "1");
requestAnimationFrame(frame);
