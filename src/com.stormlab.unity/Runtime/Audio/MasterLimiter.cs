// A peak limiter on the final mix. On the AudioListener's GameObject, OnAudioFilterRead sees everything heard at once,
// so a near thunderclap on top of the tornado roar and the rain dips the level for a moment instead of clipping the
// output. StormAudio adds it to the listener.
using UnityEngine;

namespace StormLab.Audio
{
    [RequireComponent(typeof(AudioListener))]
    public sealed class MasterLimiter : MonoBehaviour
    {
        [Tooltip("the mix never peaks above this")]
        [Range(0.5f, 1f)] public float ceiling = 0.9f;
        /// <summary>the deepest cut since this was last read, dB (0: none); diagnostics</summary>
        public float DeepestCutDb => limiter == null ? 0f : 20f * Mathf.Log10(Mathf.Max(limiter.Deepest, 1e-4f));
        Limiter limiter;

        // (AudioSettings may not be read in a MonoBehaviour constructor)
        void Awake() => limiter = new Limiter(ProceduralAudio.SampleRate);

        void OnAudioFilterRead(float[] data, int channels)
        {
            var limiter = this.limiter; if (limiter == null) return;
            limiter.ceiling = ceiling;
            limiter.Process(data, data.Length / channels, channels);
        }
    }
}
