// The storms' sound at the player: tornado wind (StormRenderer.TornadoWindAt), rain and hail (the storm volume's
// precipitation where the player is, read back from the GPU), and thunder for every flash: a real recording (CC0 field
// recordings, Resources/StormLab/Thunder, cut by validation/audio/thunder/cut.py) chosen by how far the flash is, or
// ThunderSynth's sound made from the flash's channel. Either replaces the lightning core's built-in thunder through
// LightningSystem.ThunderOverride. Add it once to a scene; it finds the storms and their lightning itself unless a game
// hands it its storms (Storms).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using StormLab.Lightning;
using UnityEngine;
using UnityEngine.Rendering;
using NVector3 = System.Numerics.Vector3;

namespace StormLab.Audio
{
    [DefaultExecutionOrder(120)]
    public sealed class StormAudio : MonoBehaviour
    {
        [Tooltip("where the wind and rain are taken (the player's truck); empty: the listener")]
        public Transform player;
        [Tooltip("the ears (thunder's distance and pan); empty: the scene's AudioListener")]
        public Transform listener;
        [Range(0f, 1f)] public float windVolume = 1f;
        [Range(0f, 1f)] public float rainVolume = 0.8f;
        [Tooltip("thunder on (when off, the lightning core's built-in thunder stays silent too)")]
        public bool thunder = true;
        [Range(0f, 1f)] public float thunderVolume = 1f;
        [Tooltip("Recordings: real thunder, close / medium / far by the flash's distance. Synthesized: made from the flash's channel")]
        public ThunderSound thunderSound = ThunderSound.Recordings;
        [Tooltip("a recording plays at full level this near, m, and falls off beyond as (distance)^-0.9")]
        public float thunderFullLevelM = 700f;
        [Tooltip("wind speeds (m/s) at which the wind sound starts and reaches full intensity")]
        public Vector2 windRange = new Vector2(3f, 80f);
        [Tooltip("air temperature for the speed of sound, C")]
        public float temperatureC = 22f;
        [Tooltip("thunder voices playing at once at most")]
        [Range(2, 24)] public int maxThunderVoices = 16;
        [Tooltip("how often the rain at the player is read back from the storm volume, s")]
        public float rainEvery = 0.2f;

        /// <summary>The storms to hear; null: every active StormRenderer in the scene.</summary>
        public Func<IEnumerable<StormRenderer>> Storms;

        /// <summary>the tornado wind at the player, m/s</summary>
        public float WindSpeed { get; private set; }
        /// <summary>how loud the tornado wind sound is now, 0..1 (0 silent): the ambience's birds stop when it starts</summary>
        public float TornadoAudio => wind != null ? wind.Amount : 0f;
        /// <summary>precipitation at the player, 0..1, and hail, 0..1</summary>
        public float Rain { get; private set; }
        public float Hail { get; private set; }
        public int ThunderPlaying => voices.Count;
        /// <summary>thunder started since enabled, and the last one (its distance, delay and character)</summary>
        public int ThunderPlayed { get; private set; }
        public ThunderHeard LastThunder { get; private set; }

        public enum ThunderSound { Recordings, Synthesized }
        /// <summary>a thunder started: how far, how long after the flash, how loud (dBFS, the loudest 2 s) and what</summary>
        public sealed class ThunderHeard { public float nearestM, delaySeconds, lengthSeconds, levelDb; public string what; }

        WindSynth wind; RainSynth rain;
        ProceduralAudio windOut, rainOut;
        readonly List<StormRenderer> storms = new List<StormRenderer>();
        readonly Dictionary<StormRenderer, float> rainBy = new Dictionary<StormRenderer, float>();
        readonly HashSet<LightningSystem> hooked = new HashSet<LightningSystem>();
        readonly ConcurrentQueue<Pending> done = new ConcurrentQueue<Pending>();
        readonly List<Voice> voices = new List<Voice>();
        float rainTimer, hookTimer;
        Action<LightningFlash, double> onThunder;
        // the recordings by tier (close, medium, far), each drawn from a shuffled bag so none repeats soon
        readonly List<AudioClip>[] recordings = { new List<AudioClip>(), new List<AudioClip>(), new List<AudioClip>() };
        readonly List<AudioClip>[] bags = { new List<AudioClip>(), new List<AudioClip>(), new List<AudioClip>() };
        readonly System.Random pick = new System.Random(17);
        static readonly string[] Tiers = { "close", "medium", "far" };
        const float RecordingDb = -20f;   // the clips' loudest 2 s, dBFS (cut.py)

        sealed class Pending { public Thunder sound; public double flashDsp; public Vector3 from; }
        // (clip: made for this voice and destroyed with it; a recording is shared and only played)
        sealed class Voice { public AudioSource source; public AudioClip clip; public double startsAt, endsAt; public float nearestM; }

        void OnEnable()
        {
            int fs = ProceduralAudio.SampleRate;
            if (wind == null) { wind = new WindSynth(fs); rain = new RainSynth(fs); }
            if (!windOut) windOut = ProceduralAudio.Create(transform, "Tornado wind", wind, false);
            if (!rainOut) rainOut = ProceduralAudio.Create(transform, "Rain", rain, false);
            onThunder = OnThunder;
            hookTimer = 0f;
            if (recordings[0].Count + recordings[1].Count + recordings[2].Count == 0)
                foreach (var c in Resources.LoadAll<AudioClip>("StormLab/Thunder"))
                {
                    int t = Array.FindIndex(Tiers, x => c.name.StartsWith(x + "_"));
                    if (t >= 0) recordings[t].Add(c);
                }
        }

        void OnDisable()
        {
            foreach (var l in hooked) if (l && l.ThunderOverride == onThunder) l.ThunderOverride = null;
            hooked.Clear();
            foreach (var v in voices) Release(v);
            voices.Clear();
        }

        Transform Ears()
        {
            if (listener) return listener;
            var l = FindAnyObjectByType<AudioListener>();
            return l ? l.transform : (Camera.main ? Camera.main.transform : transform);
        }

        void Update()
        {
            var ears = Ears();
            // the final mix gets a limiter, so loud moments that pile up dip instead of clipping
            if (ears.TryGetComponent<AudioListener>(out _) && !ears.TryGetComponent<MasterLimiter>(out _)) ears.gameObject.AddComponent<MasterLimiter>();
            var at = player ? player.position : ears.position;
            storms.Clear();
            if (Storms != null) storms.AddRange(Storms());
            else foreach (var s in FindObjectsByType<StormRenderer>()) if (s.isActiveAndEnabled) storms.Add(s);

            // ---- wind: every storm's tornadoes at the player
            var w = Vector3.zero;
            foreach (var s in storms) if (s.Plan != null) w += s.TornadoWindAt(at);
            WindSpeed = w.magnitude;
            wind.speed = WindSpeed; wind.startMs = windRange.x; wind.fullMs = Mathf.Max(windRange.y, windRange.x + 1f);
            // the side the wind comes from (it blows along w, so it arrives from -w)
            wind.pan = WindSpeed > 0.5f ? Vector3.Dot(ears.right, -w / WindSpeed) : 0f;
            windOut.Source.volume = windVolume;

            // ---- rain: the heaviest of the storms' precipitation at the player
            if ((rainTimer -= Time.unscaledDeltaTime) <= 0f)
            {
                rainTimer = rainEvery;
                foreach (var s in storms) RequestRain(s, at);
            }
            float r = 0f, h = 0f;
            foreach (var s in storms)
            {
                if (rainBy.TryGetValue(s, out var v)) r = Mathf.Max(r, v);
                h = Mathf.Max(h, HailAt(s, at));
            }
            if (rainBy.Count > storms.Count * 2 + 4) rainBy.Clear();
            Rain = r; Hail = Mathf.Min(h, r * 1.5f);
            rain.rain = Rain; rain.hail = Hail;
            rainOut.Source.volume = rainVolume;

            // ---- thunder: hook every lightning system, play what the worker threads have made
            if ((hookTimer -= Time.unscaledDeltaTime) <= 0f)
            {
                hookTimer = 1f;
                foreach (var l in FindObjectsByType<LightningSystem>())
                    if (l.ThunderOverride == null) { l.ThunderOverride = onThunder; hooked.Add(l); }
            }
            while (done.TryDequeue(out var p)) Play(p, ears);
            for (int i = voices.Count - 1; i >= 0; i--)
                if (AudioSettings.dspTime >= voices[i].endsAt) { Release(voices[i]); voices.RemoveAt(i); }
        }

        // ------------------------------------------------------------------ rain
        /// <summary>The storm volume's precipitation (green channel, stored as its square root) a little above the
        /// ground under `at`, read back from the GPU; it lands in rainBy a frame or two later.</summary>
        void RequestRain(StormRenderer s, Vector3 at)
        {
            var vol = s.Volume;
            if (vol == null || !s.ReadyToRender || !SystemInfo.supportsAsyncGPUReadback) { rainBy[s] = 0f; return; }
            var p = s.WorldToStorm(at);     // km: x east, y north, z up from the storm's ground
            float L = StormVolume.L;
            if (Mathf.Abs(p.x) > L - 1f || Mathf.Abs(p.y) > L - 1f) { rainBy[s] = 0f; return; }
            float fx = (p.x + L) / (2f * L) * vol.NV - 0.5f, fy = (p.y + L) / (2f * L) * vol.NV - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, vol.NV - 2), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, vol.NV - 2);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            // the shafts near the ground (at least 200 m up: the lowest cells are cut by the scene depth anyway)
            float z = Mathf.Clamp(p.z, 0.2f, StormVolume.ZT - 0.5f);
            int iz = Mathf.Clamp(Mathf.FloorToInt(z / StormVolume.ZT * vol.NZ), 0, vol.NZ - 1);
            var storm = s;
            AsyncGPUReadback.Request(vol.Dens, 0, x0, 2, y0, 2, iz, 1, TextureFormat.RGBA32, req =>
            {
                if (this == null || req.hasError) return;
                var d = req.GetData<byte>();
                if (d.Length < 16) return;
                float G(int i) { float g = d[i * 4 + 1] / 255f; return g * g; }
                float v = Mathf.Lerp(Mathf.Lerp(G(0), G(1), tx), Mathf.Lerp(G(2), G(3), tx), ty);
                // (the forward-flank core of a mature storm reads about 0.3-0.65)
                rainBy[storm] = Mathf.Clamp01(v * 1.5f);
            });
        }

        /// <summary>The hail core's share at `at` (the volume does not keep hail apart from rain): the BUILD pass's hail
        /// core, a Gaussian round S_hail that reaches the ground once its amount passes 0.625.</summary>
        static float HailAt(StormRenderer s, Vector3 at)
        {
            if (s.Plan == null) return 0f;
            var a = s.Frame.param;
            float amt = a[10 * 4 + 3];
            if (amt <= 0f) return 0f;
            var p = s.WorldToStorm(at);
            float q = new Vector2(p.x - a[40], p.y - a[41]).magnitude / Mathf.Max(a[42], 0.1f);
            float reach = a[4] * (1f - Mathf.Clamp01(amt * 1.6f));
            return amt * Mathf.Exp(-q * q) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(reach - 0.2f, reach + 0.3f, 0.25f));
        }

        // ------------------------------------------------------------------ thunder
        void OnThunder(LightningFlash flash, double lateSeconds)
        {
            // (still hooked when off, so LightningSystem's own thunder does not play instead)
            if (!thunder || thunderVolume <= 0f) return;
            var ears = Ears();
            var e = new NVector3(ears.position.x, ears.position.y, ears.position.z);
            // cheap range test before copying the channel
            float near = float.MaxValue;
            foreach (var pt in flash.Channels[0].points) near = Mathf.Min(near, NVector3.Distance(pt, e));
            if (near > ThunderSynth.AudibleRangeM || (voices.Count + done.Count >= maxThunderVoices && Farthest(near) < 0)) return;
            var from = new Vector3(flash.LightPosition.X, flash.LightPosition.Y, flash.LightPosition.Z);
            double flashDsp = AudioSettings.dspTime - lateSeconds + ThunderFlash.StrokeOffset(flash);
            if (thunderSound == ThunderSound.Recordings && recordings[0].Count + recordings[1].Count + recordings[2].Count > 0)
            {
                PlayRecording(near, flashDsp, from, ears);
                return;
            }
            var input = ThunderFlash.From(flash, e, temperatureC);
            var pending = new Pending { flashDsp = flashDsp, from = from };
            Task.Run(() =>
            {
                try { pending.sound = ThunderSynth.Synthesize(input); if (pending.sound != null) done.Enqueue(pending); }
                catch (Exception ex) { Debug.LogException(ex); }
            });
        }

        /// <summary>the voice not yet heard (still on its way) whose flash is farthest, and farther than nearestM; -1: none</summary>
        int Farthest(float nearestM)
        {
            int best = -1; double now = AudioSettings.dspTime;
            for (int i = 0; i < voices.Count; i++)
                if (voices[i].startsAt > now + 0.05 && voices[i].nearestM > nearestM && (best < 0 || voices[i].nearestM > voices[best].nearestM)) best = i;
            return best;
        }

        void Play(Pending p, Transform ears)
        {
            var th = p.sound;
            // all voices busy: a nearer flash takes the place of the farthest one still on its way
            if (voices.Count >= maxThunderVoices)
            {
                int f = Farthest(th.nearestM);
                if (f < 0) return;
                Release(voices[f]); voices.RemoveAt(f);
            }
            double start = p.flashDsp + th.delaySeconds, now = AudioSettings.dspTime;
            // the sound should already have begun (the synthesis ran late): start that far in, faded in over 20 ms, as
            // jumping into the middle of a waveform clicks
            var samples = th.samples;
            if (start < now)
            {
                int skip = (int)((now - start) * th.sampleRate);
                if (skip >= samples.Length - th.sampleRate / 10) return;
                samples = new float[th.samples.Length - skip];
                Array.Copy(th.samples, skip, samples, 0, samples.Length);
                int fade = Math.Min(samples.Length, th.sampleRate / 50);
                for (int i = 0; i < fade; i++) samples[i] *= i / (float)fade;
                start = now;
            }
            var clip = AudioClip.Create("Thunder", samples.Length, 1, th.sampleRate, false);
            clip.hideFlags = HideFlags.DontSave;
            clip.SetData(samples, 0);
            var go = new GameObject("Thunder"); go.transform.SetParent(transform, false); go.hideFlags = HideFlags.DontSave;
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false; src.clip = clip; src.volume = thunderVolume;
            // distance and air absorption are in the samples; only pan by where the flash is
            src.spatialBlend = 0f;
            src.panStereo = Vector3.Dot(ears.right, (p.from - ears.position).normalized) * 0.7f;
            if (start > now) src.PlayScheduled(start); else src.Play();
            voices.Add(new Voice { source = src, clip = clip, startsAt = start, endsAt = Math.Max(start, now) + clip.length + 0.2, nearestM = th.nearestM });
            float e = 0f; foreach (var v in samples) e += v * v;
            ThunderPlayed++;
            LastThunder = new ThunderHeard { nearestM = th.nearestM, delaySeconds = th.delaySeconds, lengthSeconds = clip.length,
                                             levelDb = 10f * Mathf.Log10(e / samples.Length + 1e-12f) + 20f * Mathf.Log10(thunderVolume + 1e-6f), what = th.character };
        }

        /// <summary>A recording for a flash `nearM` away: close within ~2 km, medium to ~6 km, far beyond (mixed across
        /// the edges), started when the sound of the channel's nearest point arrives, fainter and duller with distance.</summary>
        void PlayRecording(float nearM, double flashDsp, Vector3 from, Transform ears)
        {
            if (voices.Count >= maxThunderVoices)
            {
                int f = Farthest(nearM);
                if (f < 0) return;
                Release(voices[f]); voices.RemoveAt(f);
            }
            float u = (float)pick.NextDouble();
            int tier = nearM < Mathf.Lerp(1500f, 2600f, u) ? 0 : nearM < Mathf.Lerp(4500f, 8000f, u) ? 1 : 2;
            while (recordings[tier].Count == 0) tier = (tier + 1) % 3;
            var bag = bags[tier];
            if (bag.Count == 0)
            {
                bag.AddRange(recordings[tier]);
                for (int i = bag.Count - 1; i > 0; i--) { int j = pick.Next(i + 1); (bag[i], bag[j]) = (bag[j], bag[i]); }
            }
            var clip = bag[bag.Count - 1]; bag.RemoveAt(bag.Count - 1);

            float c = 331.3f + 0.606f * temperatureC, delay = nearM / c;
            double now = AudioSettings.dspTime, start = Math.Max(flashDsp + delay, now + 0.02);
            float gain = thunderVolume * Mathf.Min(1f, Mathf.Pow(thunderFullLevelM / Mathf.Max(nearM, 30f), 0.9f));
            float pitch = Mathf.Lerp(0.94f, 1.04f, (float)pick.NextDouble());
            var go = new GameObject("Thunder"); go.transform.SetParent(transform, false); go.hideFlags = HideFlags.DontSave;
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false; src.clip = clip; src.volume = gain; src.pitch = pitch;
            src.spatialBlend = 0f;
            src.panStereo = Vector3.Dot(ears.right, (from - ears.position).normalized) * 0.6f;
            // air takes the highs: 11 kHz at 2 km, 5.5 kHz at 6 km, 2.6 kHz at 15 km
            var lp = go.AddComponent<AudioLowPassFilter>();
            lp.cutoffFrequency = Mathf.Clamp(22000f / (1f + nearM / 2000f), 900f, 22000f); lp.lowpassResonanceQ = 1f;
            src.PlayScheduled(start);
            voices.Add(new Voice { source = src, startsAt = start, endsAt = start + clip.length / pitch + 0.2, nearestM = nearM });
            ThunderPlayed++;
            LastThunder = new ThunderHeard { nearestM = nearM, delaySeconds = delay, lengthSeconds = clip.length / pitch,
                                             levelDb = RecordingDb + 20f * Mathf.Log10(gain + 1e-6f), what = $"{clip.name}, low-pass {lp.cutoffFrequency:0} Hz" };
        }

        static void Release(Voice v)
        {
            if (v.source) { v.source.Stop(); Destroy(v.source.gameObject); }
            if (v.clip) Destroy(v.clip);
        }
    }
}
