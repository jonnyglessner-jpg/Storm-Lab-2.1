// Sanity (user, 2026-11-02): the chaser must sleep every day. The bar drains with the hours awake on the day's clock:
// half gone after 12 hours, a quarter left after 20, empty after 32; time within half a mile of a tornado drains it
// half again as fast and counts toward the same clock. At each phase the chaser moves slower (walking, running and
// the truck's top speed at 50 %, 25 %, then 10 %) and everything pays more (+25 %, +50 %, +75 %). With the bar empty
// the chaser passes out at a random moment within the next eight hours: on foot, right there; flying the drone, once
// it has been brought home; driving, once the truck has come to a stop. A forced sleep refills the bar only to half.
// The bar and the hours are in the profile; ChaseSleep refills them.
using StormLab.TestMap;
using UnityEngine;

namespace StormLab.Game
{
    public class ChaseSanity : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("hours awake at which the bar is at half, at a quarter, and empty")]
        public float halfHours = 12f, quarterHours = 20f, emptyHours = 32f;
        [Tooltip("the drain's speed within tornadoMi of a tornado")]
        public float tornadoDrain = 1.5f, tornadoMi = 0.5f;
        [Tooltip("after the bar empties, the chaser passes out at a random moment within this many hours")]
        public float passOutWindow = 8f;

        public float Sanity => ChaseProfile.Current.sanity;
        public float AwakeHours => ChaseProfile.Current.awakeHours;
        /// <summary>0 rested .. 3 the bar is empty</summary>
        public int Phase { get { float s = Sanity; return s <= 0.0005f ? 3 : s <= 0.25f ? 2 : s <= 0.5f ? 1 : 0; } }
        public float SpeedScale => Phase == 0 ? 1f : Phase == 1 ? 0.5f : Phase == 2 ? 0.25f : 0.1f;
        public float ProfitMultiplier => 1f + 0.25f * Phase;
        public bool NearTornado { get; private set; }
        public float NearestTornadoMi { get; private set; } = float.PositiveInfinity;
        /// <summary>the hours awake at which the chaser passes out (set when the bar empties; -1 before)</summary>
        public float PassOutAt { get; private set; } = -1f;
        /// <summary>the moment has come: waiting for the drone to land or the truck to stop, then asleep on the spot</summary>
        public bool PassingOut { get; private set; }
        public string Warning { get; private set; } = "";

        int lastPhase = -1;
        GUIStyle warn;

        /// <summary>The bar for `hours` awake: 1 at 0, 0.5 at halfHours, 0.25 at quarterHours, 0 at emptyHours, straight lines between.</summary>
        public float Curve(float hours)
        {
            if (hours <= halfHours) return 1f - 0.5f * hours / halfHours;
            if (hours <= quarterHours) return 0.5f - 0.25f * (hours - halfHours) / (quarterHours - halfHours);
            if (hours <= emptyHours) return 0.25f * (1f - (hours - quarterHours) / (emptyHours - quarterHours));
            return 0f;
        }

        /// <summary>The hours awake that give a bar of `sanity`: a sleep that refills it only partly starts the clock there.</summary>
        public float HoursFor(float sanity)
        {
            if (sanity >= 0.5f) return (1f - sanity) / 0.5f * halfHours;
            if (sanity >= 0.25f) return halfHours + (0.5f - sanity) / 0.25f * (quarterHours - halfHours);
            return quarterHours + (0.25f - Mathf.Max(sanity, 0f)) / 0.25f * (emptyHours - quarterHours);
        }

        /// <summary>A sleep's refill: the bar and the clock behind it; the pass-out scheduled afresh.</summary>
        public void Refill(float sanity)
        {
            var p = ChaseProfile.Current;
            p.sanity = Mathf.Clamp01(sanity); p.awakeHours = HoursFor(p.sanity);
            PassOutAt = -1f; PassingOut = false; Warning = ""; lastPhase = -1;
        }

        void Update()
        {
            var world = game ? game.world : null;
            if (!world || !game.InChase) { NearTornado = false; Warning = ""; return; }
            var p = ChaseProfile.Current;
            // the tornado near the chaser (in the truck or on foot)
            var at = game.Player ? game.Player.Position : world.car.transform.position;
            NearestTornadoMi = float.PositiveInfinity;
            foreach (var s in world.LiveStorms())
                if (s.TornadoAmount > 0.15f) NearestTornadoMi = Mathf.Min(NearestTornadoMi, TestMapPlaces.Flat(s.TornadoGroundWorld, at) / 1609.344f);
            NearTornado = NearestTornadoMi <= tornadoMi;
            // the clock: the day's hours, faster near a tornado
            float dtHours = game.ClockDeltaHours;
            if (dtHours > 0f && !ChaseSetup.morning && !(game.Sleep && game.Sleep.Busy))
            {
                p.awakeHours += dtHours * (NearTornado ? tornadoDrain : 1f);
                p.sanity = Curve(p.awakeHours);
            }
            int phase = Phase;
            if (phase != lastPhase)
            {
                if (lastPhase >= 0 && phase > lastPhase)
                    game.Say(phase == 1 ? "12 hours without sleep: half speed, but everything pays 25 % more"
                           : phase == 2 ? "20 hours without sleep: a quarter speed, everything pays 50 % more"
                           : "32 hours without sleep: you can barely move, everything pays 75 % more... and you will pass out");
                lastPhase = phase;
            }
            // the pass-out
            if (phase == 3 && PassOutAt < 0f)
            {
                var rng = new System.Random(ChaseSetup.DaySeed(p.careerSeed, p.day) * 3 + 11);
                PassOutAt = p.awakeHours + (float)rng.NextDouble() * passOutWindow;
            }
            if (PassOutAt >= 0f && p.awakeHours >= PassOutAt && !PassingOut && game.Sleep && !game.Sleep.Busy) PassingOut = true;
            if (PassingOut) PassOut();
            else Warning = "";
            // the body's speed
            if (game.Player) game.Player.speedScale = SpeedScale;
        }

        /// <summary>The moment has come: the drone must be home, the truck stopped, then the chaser is out.</summary>
        void PassOut()
        {
            var world = game.world;
            if (game.Sleep.Busy) { PassingOut = false; return; }
            if (game.Drone && game.Drone.Flying) { game.Drone.ComeHome(); Warning = "YOU ARE PASSING OUT  ·  the drone is coming home"; return; }
            bool onFoot = game.Player && game.Player.OnFoot;
            if (!onFoot && world.car.SpeedKph * 0.621f > 0.5f) { Warning = "YOU ARE PASSING OUT  ·  STOP THE TRUCK"; return; }
            Warning = "";
            PassingOut = false;
            game.Sleep.ForceSleep();
        }

        /// <summary>For the checks: the hours awake (and the bar from them).</summary>
        public void TestSetAwake(float hours) { var p = ChaseProfile.Current; p.awakeHours = hours; p.sanity = Curve(hours); }
        /// <summary>For the checks: pass out as soon as the drone and the truck allow.</summary>
        public void TestPassOutNow() { PassOutAt = Mathf.Min(PassOutAt < 0 ? float.MaxValue : PassOutAt, AwakeHours); }

        void OnGUI()
        {
            var world = game ? game.world : null;
            if (!world || world.hudHidden || string.IsNullOrEmpty(Warning)) return;
            if (warn == null) { warn = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }; warn.normal.textColor = new Color(1f, 0.35f, 0.3f); }
            float W = Screen.width, H = Screen.height;
            // the view closing in
            float pulse = 0.35f + 0.25f * Mathf.Sin(Time.unscaledTime * 3f);
            GUI.color = new Color(0f, 0f, 0f, pulse);
            GUI.DrawTexture(new Rect(0, 0, W, H * 0.18f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, H * 0.82f, W, H * 0.18f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(0, H * 0.3f, W, 50), Warning, warn);
        }
    }
}
