const MASK = (1n << 64n) - 1n;

/**
 * SplitMix64, ported from src/BadDay.Core/SplitMix64.cs. The only randomness in
 * a run. Integer arithmetic on BigInt, so a seed gives the same stream as the C#
 * reference.
 */
export class SplitMix64 {
  private state: bigint;

  constructor(seed: bigint) {
    this.state = BigInt.asUintN(64, seed);
  }

  nextUInt64(): bigint {
    this.state = (this.state + 0x9e3779b97f4a7c15n) & MASK;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK;
    z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & MASK;
    return z ^ (z >> 31n);
  }

  /** Uniform in [0, 1) with 53 bits. */
  nextDouble(): number {
    return Number(this.nextUInt64() >> 11n) * (1.0 / 9007199254740992.0);
  }

  /** Uniform in [-1, 1). */
  nextSigned(): number {
    return this.nextDouble() * 2.0 - 1.0;
  }

  clone(): SplitMix64 {
    return new SplitMix64(this.state);
  }
}
