"""Compares the web build's and the C# port's storm parameter blocks (parity.sh writes both)."""
import json, sys
web = json.load(open(sys.argv[1], encoding='utf-8')); cs = json.load(open(sys.argv[2], encoding='utf-8'))
worst = 0.0; where = None; bad = 0; n = 0
for k, w in web.items():
    c = cs.get(k)
    if c is None: print('missing in C#:', k); bad += 1; continue
    if w['kind'] != c['kind'] or w['cycles'] != c['cycles'] or abs(w['duration'] - c['duration']) > 1e-9:
        print('plan differs:', k, w['kind'], c['kind'], w['cycles'], c['cycles'], w['duration'], c['duration']); bad += 1
    if len(w['frames']) != len(c['frames']): print('frame count differs:', k); bad += 1; continue
    for fw, fc in zip(w['frames'], c['frames']):
        if fw['stage'] != fc['stage']: print('stage differs:', k, fw['t'], fw['stage'], '|', fc['stage']); bad += 1
        for i, (a, b) in enumerate(zip(fw['p'], fc['p'])):
            n += 1
            d = abs(a - b) / max(1.0, abs(a))
            if d > worst: worst, where = d, (k, fw['t'], i // 4, 'xyzw'[i % 4], a, b)
print(f'{len(web)} storms, {n} numbers; worst relative difference {worst:.2e} at {where}')
ok = bad == 0 and worst < 1e-5
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
