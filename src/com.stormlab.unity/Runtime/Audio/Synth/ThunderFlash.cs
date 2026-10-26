// A lightning flash (StormLab.Lightning) as the thunder synth's input. Copies the geometry, so the synth can run on a
// worker thread while the flash lives on.
using System;
using System.Numerics;
using StormLab.Lightning;

namespace StormLab.Audio
{
    public static class ThunderFlash
    {
        public static ThunderInput From(LightningFlash flash, Vector3 observer, float temperatureC = 20f)
        {
            var ch = flash.Channels;
            var input = new ThunderInput
            {
                channels = new Vector3[ch.Length][], weights = new float[ch.Length],
                observer = observer, temperatureC = temperatureC, seed = flash.Event.seed,
                aloft = flash.Event.kind == FlashKind.InCloud || flash.Event.kind == FlashKind.Crawler,
                positive = flash.Event.kind == FlashKind.PositiveGround,
            };
            for (int i = 0; i < ch.Length; i++)
            {
                input.channels[i] = (Vector3[])ch[i].points.Clone();
                input.weights[i] = ch[i].main ? 1f : Math.Min(Math.Max(ch[i].brightness, 0.1f), 1f) * 0.45f;
            }
            var st = flash.StrokeTimes;
            input.strokes = new float[st.Length];
            for (int i = 0; i < st.Length; i++) input.strokes[i] = st[i] - st[0];
            return input;
        }

        /// <summary>seconds from the flash event to its first return stroke, after the leader (the thunder's clock starts there)</summary>
        public static float StrokeOffset(LightningFlash flash) => flash.StrokeTimes.Length > 0 ? flash.StrokeTimes[0] : flash.LeadSeconds;
    }
}
