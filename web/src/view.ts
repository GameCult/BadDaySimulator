import type { PainBoxResult, Readout } from "./painbox.js";

/** Seconds with one decimal, from milliseconds. */
export const seconds = (ms: number): string => (ms / 1000).toFixed(1) + " s";

export type Phase = "closed" | "open" | "ended";

export interface CounterView {
  /** Big number text. */
  value: string;
  /** What the number is counting down to, or what happened. */
  note: string;
}

/**
 * The counter: how long until the will breaks or the box is disabled, whichever
 * the deterministic forecast says comes first. `forecast` is the run to the end
 * from the current tick, so while the box stays on the number is exact.
 */
export function counterView(phase: Phase, now: Readout, forecast: PainBoxResult, final: PainBoxResult | null): CounterView {
  if (phase === "closed") return { value: "--", note: "The box is closed." };
  if (phase === "ended" && final) {
    switch (final.outcome) {
      case "WillBroke":
        return { value: seconds(final.timeToBreakMs ?? final.endMs), note: `The will broke here; the hand was out at ${seconds(final.endMs)}.` };
      case "BoxDisabled":
        return { value: seconds(final.endMs), note: "The box was switched off. The will held." };
      default:
        return { value: seconds(final.endMs), note: "Time ran out with the box on and the will intact." };
    }
  }
  switch (forecast.outcome) {
    case "WillBroke": {
      const left = Math.max(0, (forecast.timeToBreakMs ?? forecast.endMs) - now.timeMs);
      return { value: seconds(left), note: "until the will breaks, if the box stays on." };
    }
    case "BoxDisabled":
      return { value: seconds(Math.max(0, forecast.endMs - now.timeMs)), note: "until the box switches off. The will holds that long." };
    default:
      return { value: "never", note: `the will holds to the ${seconds(forecast.endMs)} limit with the box on.` };
  }
}

/** Drive is an afferent signal that can exceed 1 (sensitised); the bar saturates at DRIVE_FULL. */
export const DRIVE_FULL = 2.0;
export const barFraction = (v: number, full = 1.0): number => Math.min(1, Math.max(0, v / full));

export function sensesText(r: Readout, phase: Phase): string {
  if (phase === "closed") return "Sees: a closed box. Feels: nothing. Goal: put the hand in and keep it there.";
  const touch = r.stimulus <= 0 ? "nothing" : `box at ${r.stimulus.toFixed(2)}, nerve ${r.sensitisation.toFixed(2)}x sensitised`;
  const goal = r.goal === "HoldHand" ? "keep the hand in the box" : "get the hand out";
  return `Sees: an open box, ${r.boxOn ? "live" : "dead"}. Feels: ${touch}. Goal: ${goal}.`;
}
