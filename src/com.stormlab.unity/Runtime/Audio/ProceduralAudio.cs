// Plays an ISynth through an AudioSource. The source loops a clip of ones, which Unity pans and attenuates as usual (2-D
// or 3-D); OnAudioFilterRead then multiplies the synth's output into it, so a procedural sound keeps the source's
// volume, distance roll-off and panning.
using System;
using UnityEngine;

namespace StormLab.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class ProceduralAudio : MonoBehaviour
    {
        public ISynth synth;
        public AudioSource Source { get; private set; }
        /// <summary>audio blocks rendered so far (diagnostics)</summary>
        public int Blocks => blocks;
        volatile int blocks;
        float[] buffer = new float[0];
        static AudioClip ones;

        /// <summary>The output sample rate the synths are built for.</summary>
        public static int SampleRate => AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;

        /// <summary>A child of `parent` playing `synth`: 2-D (spatial false) or 3-D from the child's position.</summary>
        public static ProceduralAudio Create(Transform parent, string name, ISynth synth, bool spatial, float volume = 1f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false; src.loop = true; src.clip = Ones();
            src.spatialBlend = spatial ? 1f : 0f; src.dopplerLevel = 0f; src.volume = volume;
            var p = go.AddComponent<ProceduralAudio>();
            p.synth = synth; p.Source = src;
            src.Play();
            return p;
        }

        static AudioClip Ones()
        {
            if (ones) return ones;
            int n = SampleRate;
            ones = AudioClip.Create("ones", n, 1, n, false);
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = 1f;
            ones.SetData(d, 0);
            ones.hideFlags = HideFlags.DontSave;
            return ones;
        }

        void Awake() { Source = GetComponent<AudioSource>(); }

        void OnAudioFilterRead(float[] data, int channels)
        {
            var s = synth;
            if (s == null) { Array.Clear(data, 0, data.Length); return; }
            if (buffer.Length < data.Length) buffer = new float[data.Length];
            s.Render(buffer, data.Length / channels, channels);
            for (int i = 0; i < data.Length; i++) data[i] *= buffer[i];
            blocks++;
        }
    }
}
