// Dumps the web build's storm parameter block (src/storm3d/design.ts) for the parity check against the C# port.
// Bundled by parity.sh with esbuild from the website checkout; writes JSON to stdout.
import { environment } from '../../../StormLabWebsite/src/physics/environment';
import { storm } from '../../../StormLabWebsite/src/physics/storm';
import { PRESETS } from '../../../StormLabWebsite/src/presets';
import { toEnv } from '../../../StormLabWebsite/src/units';
import { planStorm, stormAt } from '../../../StormLabWebsite/src/storm3d/design';

const out: Record<string, unknown> = {};
for (const key of Object.keys(PRESETS)) {
  const E = environment(toEnv(PRESETS[key]));
  const S = storm(E);
  for (const seed of [undefined, 42, 7, 2026]) {
    const P = planStorm(E, S, seed);
    const frames: { t: number; p: number[]; stage: string }[] = [];
    for (let t = 0; t <= P.duration; t += 1.7) {
      const f = stormAt(P, t, 123.4);
      frames.push({ t, p: Array.from(f.params), stage: f.stage });
    }
    out[`${key}|${seed ?? 'teach'}`] = { duration: P.duration, kind: P.kind, cycles: P.cycles?.map(c => c.shape) ?? null, frames };
  }
}
process.stdout.write(JSON.stringify(out));
