/**
 * Every tunable of the pain box, mirroring PainBoxParameters.cs field for field
 * with the same placeholder defaults. Not measured physiology.
 */
export interface PainBoxParameters {
  tickSeconds: number;
  maxSeconds: number;
  boxIntensity: number;
  boxRampSeconds: number;
  boxNoise: number;
  /** When set, the box switches off at this time (the game disables it). */
  boxDisableAtSeconds: number | null;
  nociceptorThreshold: number;
  sensitisationGrowth: number;
  sensitisationRecovery: number;
  sensitisationCap: number;
  conductionDelaySeconds: number;
  painRise: number;
  painDecay: number;
  strainRecover: number;
  resolveDrainPain: number;
  resolveDrainStrain: number;
  resolveRecovery: number;
  resolveRecoversBelowPain: number;
  maxSuppression: number;
  flinchLeakThreshold: number;
  flinchRefractorySeconds: number;
  withdrawalLatencySeconds: number;
  objectivePull: number;
  traitJitter: number;
  sampleEverySeconds: number;
}

export const defaultParameters: PainBoxParameters = {
  tickSeconds: 0.01,
  maxSeconds: 120.0,
  boxIntensity: 0.8,
  boxRampSeconds: 2.0,
  boxNoise: 0.1,
  boxDisableAtSeconds: null,
  nociceptorThreshold: 0.1,
  sensitisationGrowth: 0.04,
  sensitisationRecovery: 0.02,
  sensitisationCap: 2.0,
  conductionDelaySeconds: 0.06,
  painRise: 1.0,
  painDecay: 0.05,
  strainRecover: 0.1,
  resolveDrainPain: 0.012,
  resolveDrainStrain: 0.05,
  resolveRecovery: 0.02,
  resolveRecoversBelowPain: 0.1,
  maxSuppression: 0.95,
  flinchLeakThreshold: 0.3,
  flinchRefractorySeconds: 0.5,
  withdrawalLatencySeconds: 0.1,
  objectivePull: 0.5,
  traitJitter: 0.2,
  sampleEverySeconds: 1.0,
};
