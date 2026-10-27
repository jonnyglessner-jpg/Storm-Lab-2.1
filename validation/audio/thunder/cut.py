# Cuts the game's thunder clips out of the raw recordings (audio-sources/thunder, listed in its CREDITS.md) and writes
# them where StormAudio loads them: Runtime/Audio/Resources/StormLab/Thunder/<tier>_<freesound id>_<n>.ogg, with their
# .meta (compressed in memory, Unity's own normalising off). Tiers by how far away the strike sounds:
#   close   a crack or clap right overhead to ~2 km: sudden, bright, loud
#   medium  ~2-6 km: a sharp start that rolls
#   far     past ~6 km: slow swells of low rumble
# Each clip: 0.15 s before the event, a 20 ms fade in, the tail faded out over its last 1.5 s, DC removed, 48 kHz stereo,
# then matched in loudness (its loudest 2 s at -20 dBFS RMS) with its peak no higher than -1 dBFS. The game sets the
# level from the flash's distance, so every clip starts equal. Cut times come from analyse.py (its table and overview.png).
# Tiers were checked by brightness at each onset (the share above 1.5 kHz in the second after the level comes within
# 10 dB of its peak): close -16 to -25 dB, medium -18 to -36 dB, far -34 to -55 dB.
#   python -I validation/audio/thunder/cut.py
import os, uuid
import numpy as np
import soundfile as sf
from scipy.signal import resample_poly, butter, sosfilt

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
SRC = os.path.join(ROOT, "audio-sources", "thunder")
DST = os.path.join(ROOT, "src", "com.stormlab.unity", "Runtime", "Audio", "Resources", "StormLab", "Thunder")
FS = 48000
TARGET_RMS_DB, PEAK_DB, MAX_LEN = -20.0, -1.0, 28.0

# (freesound id, start s, end s, tier): starts are the event onsets from analyse.py less a little
CUTS = [
    # close: cracks and claps
    (574412, 0.60, 17.5, "close"),   # big explosive crack (rumble, then the crack at 2.5 s)
    (567945, 1.65, 15.5, "close"),   # pretty close crack
    (721072, 0.70, 16.9, "close"),   # sharp dry crack, a second clap at 4.7 s
    (446753, 0.55, 40.0, "medium"),  # heavy strike, nearly dry (sudden but dull: medium, not a near crack)
    (534023, 0.35, 13.4, "close"),   # thunderclap: loud cracking, quick fall
    (717907, 0.45, 33.0, "close"),   # close crack crash big
    (717909, 3.30, 28.0, "close"),   # close crack (the light rain before it left out)
    (840628, 0.45, 13.9, "close"),   # closeup strike
    # medium: a sharp start that rolls
    (574416, 1.50, 24.5, "medium"),  # medium-close crack and roll
    (574386, 0.50, 38.0, "medium"),  # rolling thunder, closer
    (194364, 11.0, 48.0, "medium"),  # a clap building over 3 s, Kentucky
    (616987, 61.3, 84.0, "far"),     # Toronto storm: a roll (dull at its onset: far)
    (616987, 122.2, 142.0, "medium"),  # its loudest crack
    # far: low rolling swells
    (177477, 6.5, 34.0, "far"),      # long low thunder, Warsaw
    (367222, 9.0, 60.0, "far"),      # slowed rolling thunder, very deep
    (568969, 10.5, 25.0, "far"),     # low rolls after a storm (roof drips under them)
    (568969, 48.8, 64.0, "far"),
    (568969, 82.6, 97.0, "far"),
    (572443, 1.0, 25.0, "far"),      # distant boomy rolls
    (572443, 37.5, 60.0, "far"),
    (717876, 2.4, 30.0, "far"),      # one distant boom like an explosion
    (717868, 7.6, 31.0, "far"),      # distant rolling, Stratford
    (717868, 41.7, 64.0, "far"),
    (717868, 89.0, 108.0, "far"),
    (616987, 3.6, 34.0, "far"),      # Toronto storm: distant rolls
    (616987, 152.8, 178.0, "far"),
    (616987, 208.4, 230.0, "far"),
    (616987, 236.4, 262.0, "far"),
    (616987, 276.8, 304.0, "far"),
    (616987, 347.8, 370.0, "far"),
]

META = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  externalObjects: {{}}
  serializedVersion: 6
  defaultSettings:
    loadType: 1
    sampleRateSetting: 0
    sampleRateOverride: 48000
    compressionFormat: 1
    quality: 0.7
    conversionMode: 0
  platformSettingOverrides: {{}}
  forceToMono: 0
  normalize: 0
  preloadAudioData: 1
  loadInBackground: 1
  ambisonic: 0
  3D: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def source(fid):
    for f in os.listdir(SRC):
        if f.startswith(f"{fid}__") and f.endswith(".ogg"):
            return os.path.join(SRC, f)
    raise FileNotFoundError(fid)


def to48k(x, fs):
    if fs == FS:
        return x
    from math import gcd
    g = gcd(FS, fs)
    return resample_poly(x, FS // g, fs // g, axis=0).astype(np.float32)


def loudest_rms_db(x, span=2.0):
    m = x.mean(axis=1) ** 2
    n = int(span * FS)
    c = np.concatenate([[0.0], np.cumsum(m)])
    if len(m) <= n:
        return 10 * np.log10(c[-1] / max(len(m), 1) + 1e-20)
    w = (c[n:] - c[:-n]) / n
    return 10 * np.log10(w.max() + 1e-20)


def brightness(x, span=1.0):
    """share of the energy above 1.5 kHz in the second after the level first comes within 10 dB of its peak, dB: a near
    crack is bright, distant rumble dull (measured at the onset, not the clip's start, which can be quiet background)"""
    m = x.mean(axis=1)
    n = int(0.02 * FS); k = len(m) // n
    e = 20 * np.log10(np.sqrt((m[: k * n].reshape(k, n) ** 2).mean(axis=1)) + 1e-12)
    t0 = int(np.argmax(e > e.max() - 10)) * n
    seg = m[t0: t0 + int(span * FS)]
    spec = np.abs(np.fft.rfft(seg * np.hanning(len(seg)))) ** 2
    f = np.fft.rfftfreq(len(seg), 1 / FS)
    return 10 * np.log10(spec[f > 1500].sum() / max(spec.sum(), 1e-20) + 1e-20)


def main():
    os.makedirs(DST, exist_ok=True)
    # a fresh cut replaces the old clips, keeping each clip's .meta (its guid) when the name is unchanged
    keep = {}
    for f in os.listdir(DST):
        if f.endswith(".ogg.meta"):
            keep[f[:-5]] = open(os.path.join(DST, f), encoding="utf-8").read()
        if f.endswith(".ogg") or f.endswith(".ogg.meta"):
            os.remove(os.path.join(DST, f))
    hp = butter(2, 18, "highpass", fs=FS, output="sos")
    count, total, by_tier = {}, 0.0, {}
    for fid, a, b, tier in CUTS:
        x, fs = sf.read(source(fid), always_2d=True, dtype="float32")
        if x.shape[1] == 1:
            x = np.repeat(x, 2, axis=1)
        x = to48k(x[:, :2], fs)
        b = min(b, a + MAX_LEN, len(x) / FS)
        seg = x[int(a * FS): int(b * FS)].copy()
        seg = sosfilt(hp, seg, axis=0).astype(np.float32)
        fi, fo = int(0.02 * FS), int(min(1.5, (b - a) / 3) * FS)
        seg[:fi] *= np.linspace(0, 1, fi, dtype=np.float32)[:, None]
        seg[-fo:] *= (np.linspace(1, 0, fo, dtype=np.float32) ** 2)[:, None]
        g = 10 ** ((TARGET_RMS_DB - loudest_rms_db(seg)) / 20)
        pk = np.abs(seg).max() * g
        if pk > 10 ** (PEAK_DB / 20):
            g *= 10 ** (PEAK_DB / 20) / pk
        seg *= g
        k = count.get((tier, fid), 0) + 1; count[(tier, fid)] = k
        name = f"{tier}_{fid}_{k}.ogg"
        path = os.path.join(DST, name)
        # (in blocks: libsndfile 1.2 crashes writing a long Ogg Vorbis buffer in one call)
        with sf.SoundFile(path, "w", FS, 2, format="OGG", subtype="VORBIS", compression_level=0.45) as out:
            for i in range(0, len(seg), 8192):
                out.write(seg[i: i + 8192])
        with open(path + ".meta", "w", encoding="utf-8", newline="\n") as m:
            m.write(keep.get(name, META.format(guid=uuid.uuid4().hex)))
        total += len(seg) / FS
        by_tier.setdefault(tier, []).append(brightness(seg))
        print(f"{name:22s} {len(seg) / FS:5.1f} s  gain {20 * np.log10(g):+5.1f} dB  peak {20 * np.log10(np.abs(seg).max()):5.1f} dBFS"
              f"  loudest 2 s {loudest_rms_db(seg):5.1f} dBFS  bright {brightness(seg):5.1f} dB")
    size = sum(os.path.getsize(os.path.join(DST, f)) for f in os.listdir(DST) if f.endswith(".ogg"))
    print(f"\n{len(CUTS)} clips, {total:.0f} s, {size / 1e6:.1f} MB")
    for t, v in by_tier.items():
        print(f"  {t:6s} {len(v):2d} clips, brightness median {np.median(v):5.1f} dB")


if __name__ == "__main__":
    main()
