using System;
using System.Numerics;

namespace StormLab.Lightning
{
    public sealed class ThunderSound
    {
        public const int SampleRate = 12000;
        public readonly float[] samples;
        public readonly float delaySeconds;
        public ThunderSound(float[] samples, float delay) { this.samples = samples; delaySeconds = delay; }
    }

    public static class LightningThunder
    {
        /// <summary>A bounded procedural approximation: channel sections arrive separately, with distant sound softened.
        /// Observer is frozen at emission. No refraction, wind, obstacles or atmospheric ray tracing.</summary>
        public static ThunderSound Synthesize(LightningFlash flash, Vector3 observer, float temperatureC = 20)
        {
            var channel = flash.Channels[0].points;
            const int sections = 12;
            var distances = new float[sections];
            var arrivals = new float[sections];
            float first = float.MaxValue, last = 0;
            for (int i = 0; i < sections; i++)
            {
                Vector3 p = channel[i * (channel.Length - 1) / (sections - 1)];
                distances[i] = Vector3.Distance(p, observer);
                arrivals[i] = LightningAcoustics.ArrivalSeconds(p, observer, temperatureC);
                first = Math.Min(first, arrivals[i]); last = Math.Max(last, arrivals[i]);
            }
            // Four-second section tails; cap the total clip to bound memory for very long anvil channels.
            int length = (int)(Math.Min(30, last - first + 4) * ThunderSound.SampleRate);
            var data = new float[Math.Max(1, length)];
            var rng = new LightningRandom(LightningRandom.Hash(flash.Event.seed, 0x7468756eu));
            for (int section = 0; section < sections; section++)
            {
                int offset = (int)((arrivals[section] - first) * ThunderSound.SampleRate);
                float distance = distances[section];
                float amplitude = .38f / (1 + distance / 1800);
                float cutoff = 180 + 2200 / (1 + distance / 1200);
                float alpha = 1 - (float)Math.Exp(-2 * Math.PI * cutoff / ThunderSound.SampleRate);
                float low = 0, bass = 0;
                int count = Math.Min(4 * ThunderSound.SampleRate, length - offset);
                for (int j = 0; j < count; j++)
                {
                    float t = j / (float)ThunderSound.SampleRate;
                    float noise = rng.Range(-1, 1);
                    low += alpha * (noise - low); bass += .035f * (low - bass);
                    float crack = (float)Math.Exp(-t * 28) * (distance < 3000 ? .9f : .08f);
                    float rumble = (1 - (float)Math.Exp(-t * 18)) * (float)Math.Exp(-t * 1.3f);
                    data[offset + j] += amplitude * (low * crack + bass * rumble * 3);
                }
            }
            // Soft limiting preserves distance attenuation without normalizing every strike to equal loudness.
            for (int i = 0; i < data.Length; i++) data[i] = data[i] / (1 + Math.Abs(data[i]));
            return new ThunderSound(data, first + LightningFlash.LeaderSeconds);
        }
    }
}
