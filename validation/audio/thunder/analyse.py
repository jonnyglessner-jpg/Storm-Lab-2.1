# Finds the thunder events in the raw recordings (audio-sources/thunder) and measures each: when it starts, how loud it
# peaks over the recording's background (rain, wind, city), how sharp its attack is and how much of its first moment is
# high frequency (a near crack is sharp and bright; distant thunder is a slow, dull swell). Prints a table and draws
# every recording's level over time to out/overview.png, for choosing the cuts in cut.py.
#   python -I validation/audio/thunder/analyse.py
import glob, os, sys
import numpy as np
import soundfile as sf
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "..", "..", "audio-sources", "thunder")
OUT = os.path.join(HERE, "out")
HOP = 0.02   # envelope step, s


def load(path):
    x, fs = sf.read(path, always_2d=True, dtype="float32")
    return x, fs


def envelope_db(mono, fs):
    n = int(HOP * fs)
    frames = len(mono) // n
    e = np.sqrt(np.mean(mono[: frames * n].reshape(frames, n) ** 2, axis=1) + 1e-12)
    return 20 * np.log10(e)


def hf_share(mono, fs, t0, span=0.3):
    seg = mono[int(t0 * fs): int((t0 + span) * fs)]
    if len(seg) < 256:
        return 0.0
    spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg)))) ** 2
    f = np.fft.rfftfreq(len(seg), 1 / fs)
    return float(spec[f > 2000].sum() / max(spec.sum(), 1e-20))


def events(env, floor):
    """runs of the envelope 15 dB over the background, merged when closer than 1.5 s"""
    above = env > floor + 15
    runs, start = [], None
    for i, a in enumerate(above):
        if a and start is None:
            start = i
        if not a and start is not None:
            runs.append([start, i]); start = None
    if start is not None:
        runs.append([start, len(env)])
    merged = []
    for r in runs:
        if merged and (r[0] - merged[-1][1]) * HOP < 1.5:
            merged[-1][1] = r[1]
        else:
            merged.append(r)
    return [r for r in merged if (r[1] - r[0]) * HOP > 0.3]


def main():
    os.makedirs(OUT, exist_ok=True)
    files = sorted(glob.glob(os.path.join(SRC, "*.ogg")))
    fig, axes = plt.subplots(len(files), 1, figsize=(16, 1.6 * len(files)))
    for ax, path in zip(axes, files):
        x, fs = load(path)
        mono = x.mean(axis=1)
        env = envelope_db(mono, fs)
        floor = float(np.percentile(env, 10))
        peak = float(env.max())
        name = os.path.basename(path)
        print(f"\n{name}  {len(mono) / fs:.1f} s  {fs} Hz  background {floor:.1f} dB  peak {peak:.1f} dB  (envelope dBFS)")
        for a, b in events(env, floor):
            i = a + int(np.argmax(env[a:b]))
            pk = env[i]
            # attack: from 20 dB under the event's peak to the peak
            j = i
            while j > a and env[j] > pk - 20:
                j -= 1
            attack = (i - j) * HOP
            # tail: until it falls to 6 dB over the background (or the next event)
            k = i
            while k < len(env) - 1 and env[k] > floor + 6:
                k += 1
            print(f"  event {a * HOP:7.2f}-{k * HOP:7.2f} s  peak {pk:6.1f} dB at {i * HOP:7.2f}  over background {pk - floor:5.1f} dB"
                  f"  attack {attack:5.2f} s  high share {hf_share(mono, fs, j * HOP):.3f}")
        t = np.arange(len(env)) * HOP
        ax.plot(t, env, lw=0.6)
        ax.axhline(floor, color="grey", lw=0.5)
        ax.set_ylim(floor - 10, max(peak + 3, floor + 30))
        ax.set_xlim(0, t[-1] if len(t) else 1)
        ax.set_title(name, fontsize=8, loc="left")
        ax.tick_params(labelsize=7)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, "overview.png"), dpi=70)
    print("\noverview:", os.path.join(OUT, "overview.png"))


if __name__ == "__main__":
    main()
