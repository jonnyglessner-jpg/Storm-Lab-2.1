// Plains ambience round the listener: a soft breeze and birds calling now and then from the grass and fence lines. The
// birds go quiet as soon as a tornado's wind is heard (StormAudio.TornadoAudio) and stay quiet until it has been gone a
// while.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace StormLab.Audio
{
    [DefaultExecutionOrder(125)]
    public sealed class CountryAmbience : MonoBehaviour
    {
        [Tooltip("the ears; empty: the scene's AudioListener")]
        public Transform listener;
        [Tooltip("the storms' sound: its tornado wind silences the birds")]
        public StormAudio storm;
        [Range(0f, 1f)] public float volume = 0.8f;
        [Range(0f, 1f)] public float breezeVolume = 1f;
        [Tooltip("the birds' level (0.25 = 12 dB quieter than first built, user 2026-10-26)")]
        [Range(0f, 1f)] public float birdVolume = 0.25f;
        [Tooltip("seconds between birds calling (a new bird), min and max")]
        public Vector2 birdEvery = new Vector2(4f, 14f);
        [Tooltip("seconds after the tornado's wind dies away before the birds call again")]
        public float birdsReturnAfter = 120f;
        public int seed = 11;

        /// <summary>true while the birds are quiet for a tornado</summary>
        public bool BirdsSilenced { get; private set; }

        readonly List<AudioClip>[] birds = new List<AudioClip>[4];
        readonly List<AudioSource> pool = new List<AudioSource>();
        readonly List<AudioSource> birdVoices = new List<AudioSource>();
        Task<float[][]> making;
        ProceduralAudio breezeOut;
        float nextBird, silentUntil, repeatAt;
        int repeats; AudioClip repeatClip; Vector3 repeatFrom;
        Rng rng;

        const int BirdVariants = 4;

        void Start()
        {
            rng = new Rng((uint)seed);
            breezeOut = ProceduralAudio.Create(transform, "Breeze", new BreezeSynth(ProceduralAudio.SampleRate, (uint)seed), false);
            uint s = (uint)seed;
            // the calls are rendered off the main thread, then turned into clips
            making = Task.Run(() =>
            {
                var b = new float[4 * BirdVariants][];
                for (int k = 0; k < 4; k++) for (int v = 0; v < BirdVariants; v++) b[k * BirdVariants + v] = AmbienceSynth.Bird((BirdKind)k, s * 31 + (uint)(k * 7 + v + 1));
                return b;
            });
            nextBird = Time.time + rng.Range(1f, 4f);
        }

        Transform Ears()
        {
            if (listener) return listener;
            var l = FindAnyObjectByType<AudioListener>();
            return l ? l.transform : transform;
        }

        void Update()
        {
            if (making != null && making.IsCompleted)
            {
                if (making.Status == TaskStatus.RanToCompletion)
                {
                    var b = making.Result;
                    for (int k = 0; k < 4; k++) { birds[k] = new List<AudioClip>(); for (int v = 0; v < BirdVariants; v++) birds[k].Add(Clip($"{(BirdKind)k} {v}", b[k * BirdVariants + v], AmbienceSynth.BirdRate)); }
                }
                else if (making.Exception != null) Debug.LogException(making.Exception);
                making = null;
            }
            breezeOut.Source.volume = volume * breezeVolume;
            var ears = Ears();
            float t = Time.time;

            // the birds stop the moment the tornado is heard, and come back a while after it has gone
            if (storm && storm.TornadoAudio > 0.01f) silentUntil = t + birdsReturnAfter;
            bool silent = t < silentUntil;
            if (silent && !BirdsSilenced) { foreach (var v in birdVoices) if (v) v.Stop(); birdVoices.Clear(); repeats = 0; }
            BirdsSilenced = silent;

            if (silent || birds[0] == null) return;
            if (repeats > 0 && t >= repeatAt)
            {
                // the same bird sings again from the same place
                repeats--; repeatAt = t + rng.Range(2f, 6f);
                birdVoices.Add(Play(repeatClip, repeatFrom, BirdLevel(repeatFrom, ears.position), rng.Range(0.97f, 1.03f)));
            }
            if (t >= nextBird)
            {
                nextBird = t + rng.Range(birdEvery.x, birdEvery.y);
                // meadowlarks most often (the plains' bird), then blackbirds and sparrows; doves now and then
                float u = rng.U();
                int kind = u < 0.4f ? 0 : u < 0.65f ? 1 : u < 0.88f ? 2 : 3;
                var clip = birds[kind][rng.Int(BirdVariants)];
                var from = Around(ears.position, rng.Range(20f, 140f), rng.Range(1f, 6f));
                birdVoices.Add(Play(clip, from, BirdLevel(from, ears.position), rng.Range(0.95f, 1.05f)));
                if (rng.U() < 0.6f) { repeats = 1 + rng.Int(3); repeatAt = t + clip.length + rng.Range(1.5f, 5f); repeatClip = clip; repeatFrom = from; }
            }
            birdVoices.RemoveAll(v => !v || !v.isPlaying);
        }

        float BirdLevel(Vector3 from, Vector3 ears) => Mathf.Clamp01(Mathf.Pow(30f / Mathf.Max(Vector3.Distance(from, ears), 1f), 0.9f)) * 0.55f * birdVolume;

        Vector3 Around(Vector3 c, float dist, float height)
        {
            float a = rng.U() * Mathf.PI * 2f;
            var p = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * dist;
            // on the ground where there is one
            if (Physics.Raycast(p + Vector3.up * 2000f, Vector3.down, out var hit, 4000f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
            p.y += height;
            return p;
        }

        AudioSource Play(AudioClip clip, Vector3 at, float level, float pitch)
        {
            AudioSource src = null;
            foreach (var s in pool) if (s && !s.isPlaying) { src = s; break; }
            if (!src)
            {
                var go = new GameObject("Ambience voice"); go.transform.SetParent(transform, false); go.hideFlags = HideFlags.DontSave;
                src = go.AddComponent<AudioSource>();
                src.playOnAwake = false; src.spatialBlend = 1f; src.dopplerLevel = 0f;
                // the level is set by distance here (a gentler fall than Unity's), so the roll-off curve is flat
                src.rolloffMode = AudioRolloffMode.Custom; src.maxDistance = 5000f;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
                pool.Add(src);
            }
            src.transform.position = at;
            src.clip = clip; src.volume = level * volume; src.pitch = pitch;
            src.Play();
            return src;
        }

        static AudioClip Clip(string name, float[] samples, int rate)
        {
            var c = AudioClip.Create(name, samples.Length, 1, rate, false);
            c.SetData(samples, 0); c.hideFlags = HideFlags.DontSave;
            return c;
        }

        void OnDestroy()
        {
            foreach (var l in birds) if (l != null) foreach (var c in l) if (c) Destroy(c);        }
    }
}
