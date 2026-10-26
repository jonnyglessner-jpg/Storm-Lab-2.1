// Plains ambience round the listener: a soft breeze, birds calling now and then from the grass and fence lines, and
// cattle mooing in the distance. The birds go quiet as soon as a tornado's wind is heard (StormAudio.TornadoAudio) and
// stay quiet until it has been gone a while; the cattle keep on.
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
        [Tooltip("seconds between birds calling (a new bird), min and max")]
        public Vector2 birdEvery = new Vector2(4f, 14f);
        [Tooltip("seconds between cattle mooing, min and max")]
        public Vector2 cowEvery = new Vector2(9f, 30f);
        [Tooltip("seconds after the tornado's wind dies away before the birds call again")]
        public float birdsReturnAfter = 120f;
        public int seed = 11;

        /// <summary>true while the birds are quiet for a tornado</summary>
        public bool BirdsSilenced { get; private set; }

        readonly List<AudioClip>[] birds = new List<AudioClip>[4];
        readonly List<AudioClip> cows = new List<AudioClip>();
        readonly List<AudioSource> pool = new List<AudioSource>();
        readonly List<AudioSource> birdVoices = new List<AudioSource>();
        Task<(float[][] birds, float[][] cows)> making;
        ProceduralAudio breezeOut;
        float nextBird, nextCow, silentUntil, repeatAt;
        int repeats; AudioClip repeatClip; Vector3 repeatFrom;
        Rng rng;

        const int BirdVariants = 4, CowVariants = 6;

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
                var c = new float[CowVariants][];
                for (int v = 0; v < CowVariants; v++) c[v] = AmbienceSynth.Cow(s * 17 + (uint)v + 1);
                return (b, c);
            });
            nextBird = Time.time + rng.Range(1f, 4f);
            nextCow = Time.time + rng.Range(3f, 8f);
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
                    var (b, c) = making.Result;
                    for (int k = 0; k < 4; k++) { birds[k] = new List<AudioClip>(); for (int v = 0; v < BirdVariants; v++) birds[k].Add(Clip($"{(BirdKind)k} {v}", b[k * BirdVariants + v], AmbienceSynth.BirdRate)); }
                    for (int v = 0; v < c.Length; v++) cows.Add(Clip($"Cow {v}", c[v], AmbienceSynth.CowRate));
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

            if (cows.Count > 0 && t >= nextCow)
            {
                nextCow = t + rng.Range(cowEvery.x, cowEvery.y);
                // a cow some way off in the pasture; now and then another answers from nearby
                var at = Around(ears.position, rng.Range(250f, 1300f), 1.2f);
                float d = Vector3.Distance(at, ears.position);
                Play(cows[rng.Int(cows.Count)], at, Mathf.Clamp01(Mathf.Pow(220f / d, 0.85f)) * 0.75f, rng.Range(0.92f, 1.08f));
                if (rng.U() < 0.3f) nextCow = t + rng.Range(1.5f, 4f);
            }

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

        static float BirdLevel(Vector3 from, Vector3 ears) => Mathf.Clamp01(Mathf.Pow(30f / Mathf.Max(Vector3.Distance(from, ears), 1f), 0.9f)) * 0.55f;

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
            foreach (var l in birds) if (l != null) foreach (var c in l) if (c) Destroy(c);
            foreach (var c in cows) if (c) Destroy(c);
        }
    }
}
