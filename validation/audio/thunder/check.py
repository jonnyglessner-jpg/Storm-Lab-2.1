# Checks the game's thunder clips (Runtime/Audio/Resources/StormLab/Thunder) as Unity will import them: every tier has
# clips; each decodes as 48 kHz stereo, never touches full scale (Vorbis can overshoot the cut's -1 dBFS a little), has
# its loudest 2 s near the -20 dBFS StormAudio assumes, imports compressed in memory with Unity's normalising off, and
# comes from a recording listed in audio-sources/thunder/CREDITS.md. Then renders out/thunder_by_distance.wav: one close,
# one medium and one far clip at the level and low-pass StormAudio gives them at 0.5, 1, 2, 4, 8 and 16 km.
#   python -I validation/audio/thunder/check.py
import os, re, sys
import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfilt

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
CLIPS = os.path.join(ROOT, "src", "com.stormlab.unity", "Runtime", "Audio", "Resources", "StormLab", "Thunder")
CREDITS = os.path.join(ROOT, "audio-sources", "thunder", "CREDITS.md")
OUT = os.path.join(HERE, "out")
FS, FULL_LEVEL_M = 48000, 700.0   # StormAudio.thunderFullLevelM
fails = 0


def check(ok, what):
    global fails
    if not ok:
        fails += 1
        print("  FAIL " + what)


def loudest_rms_db(x, span=2.0):
    m = x.mean(axis=1) ** 2
    n = int(span * FS)
    c = np.concatenate([[0.0], np.cumsum(m)])
    w = (c[n:] - c[:-n]) / n if len(m) > n else np.array([c[-1] / max(len(m), 1)])
    return 10 * np.log10(w.max() + 1e-20)


def gain(d):          # StormAudio.PlayRecording
    return min(1.0, (FULL_LEVEL_M / max(d, 30.0)) ** 0.9)


def cutoff(d):
    return float(np.clip(22000.0 / (1.0 + d / 2000.0), 900.0, 22000.0))


def main():
    credits = open(CREDITS, encoding="utf-8").read()
    clips = sorted(f for f in os.listdir(CLIPS) if f.endswith(".ogg"))
    tiers = {}
    print(f"{len(clips)} clips in {CLIPS}")
    for f in clips:
        m = re.match(r"(close|medium|far)_(\d+)_\d+\.ogg$", f)
        check(m is not None, f"{f}: named <tier>_<freesound id>_<n>.ogg")
        if not m:
            continue
        tiers.setdefault(m.group(1), []).append(f)
        x, fs = sf.read(os.path.join(CLIPS, f), always_2d=True, dtype="float32")
        pk = 20 * np.log10(np.abs(x).max() + 1e-12)
        loud = loudest_rms_db(x)
        check(fs == FS and x.shape[1] == 2, f"{f}: 48 kHz stereo (got {fs} Hz, {x.shape[1]} ch)")
        check(pk < -0.3, f"{f}: peak {pk:.1f} dBFS stays clear of full scale")
        check(-27 < loud < -18.5, f"{f}: loudest 2 s {loud:.1f} dBFS near -20")
        meta = open(os.path.join(CLIPS, f + ".meta"), encoding="utf-8").read()
        check("loadType: 1" in meta and "normalize: 0" in meta, f"{f}: imports compressed in memory, not normalised")
        check(f"[{m.group(2)}__" in credits or f"| {m.group(2)}__" in credits or f"{m.group(2)}__" in credits, f"{f}: its recording is in CREDITS.md")
    for t in ("close", "medium", "far"):
        n = len(tiers.get(t, []))
        print(f"  {t:6s} {n:2d} clips")
        check(n >= 4, f"at least 4 {t} clips")

    # the distance law, and a preview
    print("distance  level   low-pass")
    for d in (300, 700, 1000, 2000, 4000, 8000, 16000, 25000):
        print(f"  {d / 1000:5.1f} km {20 * np.log10(gain(d)):6.1f} dB {cutoff(d):7.0f} Hz")
    check(gain(500) > gain(2000) > gain(8000) > gain(16000), "thunder falls off with distance")
    os.makedirs(OUT, exist_ok=True)
    pick = {"close": tiers["close"][0], "medium": tiers["medium"][0], "far": tiers["far"][0]}
    parts = []
    for d, t in ((500, "close"), (1000, "close"), (2000, "medium"), (4000, "medium"), (8000, "far"), (16000, "far")):
        x, _ = sf.read(os.path.join(CLIPS, pick[t]), always_2d=True, dtype="float32")
        x = x[: 12 * FS]
        sos = butter(2, cutoff(d), "lowpass", fs=FS, output="sos")
        x = sosfilt(sos, x, axis=0).astype(np.float32) * gain(d)
        fo = FS // 2
        x[-fo:] *= np.linspace(1, 0, fo, dtype=np.float32)[:, None]
        parts += [x, np.zeros((FS, 2), np.float32)]
        print(f"  preview {d / 1000:4.1f} km: {pick[t]}")
    sf.write(os.path.join(OUT, "thunder_by_distance.wav"), np.concatenate(parts), FS, subtype="PCM_16")
    print("preview:", os.path.join(OUT, "thunder_by_distance.wav"))
    print("thunder clips: all passed" if fails == 0 else f"thunder clips: {fails} FAILED")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
