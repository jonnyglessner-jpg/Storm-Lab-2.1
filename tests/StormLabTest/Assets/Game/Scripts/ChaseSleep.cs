// Sleeping (user, 2026-11-02): at the marked spot of any truck stop (car camping, a third of a motel's rate, the bar
// refilled to 75 %) or motel (the night's rate, the bar to 100 %) the chaser stops, presses F and sleeps: the screen
// fades to black, a quote fades in, holds five seconds and fades out, and the chaser wakes at 8 am the next day with a
// fade from black and the morning forecast (ChaseForecast). Every sleep saves the career. A forced sleep (ChaseSanity:
// the chaser passes out) is the same with "you passed out." for the quote, no cost and the bar to half.
using System.Collections;
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    public class ChaseSleep : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("seconds: the fade to black, the quote's fade in, hold and fade out, the fade from black")]
        public float fadeOut = 1.4f, quoteIn = 0.9f, quoteHold = 5f, quoteOutS = 0.9f, fadeIn = 1.6f;
        [Tooltip("how near a marked spot's centre the truck must be, m, and how slow, mph")]
        public float spotReachM = 4f, spotMph = 1f;
        [Tooltip("car camping at a truck stop costs this share of the town's motel rate")]
        public float campShare = 1f / 3f;
        [Tooltip("the bar after a night in a motel, car camping, and passing out")]
        public float motelFill = 1f, campFill = 0.75f, forcedFill = 0.5f;

        /// <summary>the sequence runs (the keys are the screen's)</summary>
        public bool Busy { get; private set; }
        /// <summary>the marked spot the truck is on (kind "gas" or "motel"; null: none)</summary>
        public PlaceTown SpotTown { get; private set; }
        public string SpotKind { get; private set; }
        public bool Confirming { get; private set; }
        /// <summary>what the last sleep did (the checks read them)</summary>
        public string LastQuote { get; private set; } = "";
        public bool LastForced { get; private set; }
        public int LastCost { get; private set; }
        public float Black { get; private set; }
        public float TextAlpha { get; private set; }
        public int Sleeps { get; private set; }

        static readonly string[] Quotes =
        {
            "Keep your face always toward the sunshine, and shadows will fall behind you.\n— Walt Whitman",
            "The best way out is always through.\n— Robert Frost",
            "Do not go where the path may lead; go instead where there is no path and leave a trail.\n— Ralph Waldo Emerson",
            "Difficulties strengthen the mind, as labor does the body.\n— Seneca",
            "We are all in the gutter, but some of us are looking at the stars.\n— Oscar Wilde",
            "A ship in harbor is safe, but that is not what ships are built for.\n— John A. Shedd",
            "Go confidently in the direction of your dreams. Live the life you have imagined.\n— Henry David Thoreau",
            "The sky is the daily bread of the eyes.\n— Ralph Waldo Emerson",
            "He who has a why to live can bear almost any how.\n— Friedrich Nietzsche",
            "Well done is better than well said.\n— Benjamin Franklin",
            "Energy and persistence conquer all things.\n— Benjamin Franklin",
            "The journey of a thousand miles begins with a single step.\n— Lao Tzu",
            "Fall seven times, stand up eight.\n— Japanese proverb",
            "Fortune favors the bold.\n— Virgil",
            "Hope is the thing with feathers that perches in the soul.\n— Emily Dickinson",
            "What we achieve inwardly will change outer reality.\n— Plutarch",
            "The road is long and the sky is wide. Rest now; the storms will be back.",
            "Patience is the chaser's first instrument.",
            "You cannot outrun a storm. You can only be where it is going.",
            "A good night's sleep is the best forecast tool you own.",
            "Dryline at dawn, wall cloud by dusk.",
            "Never stop chasing.",
        };

        PlaceTown[] towns = new PlaceTown[0];
        string overlayText = ""; Coroutine running;
        GUIStyle quote, bold, body, button;

        void Start() { towns = TestMapPlaces.Load(game && game.world ? game.world.navResource : "TestMapNav"); }

        public int CostOf(PlaceTown t, string kind) => kind == "motel" ? t.motel.rate : Mathf.CeilToInt(t.motel.rate * campShare);
        public float FillOf(string kind) => kind == "motel" ? motelFill : campFill;

        void Update()
        {
            var world = game ? game.world : null;
            if (!world || !world.car || Busy || !game.InChase || ChaseSetup.morning) { SpotTown = null; SpotKind = null; Confirming = false; return; }
            var car = world.car;
            SpotTown = null; SpotKind = null;
            bool inTruck = !(game.Player && game.Player.OnFoot);
            if (inTruck)
                foreach (var t in towns)
                {
                    if (t.gas?.sleep != null && TestMapPlaces.Flat(t.gas.sleep.Position, car.transform.position) < spotReachM) { SpotTown = t; SpotKind = "gas"; break; }
                    if (t.motel?.sleep != null && TestMapPlaces.Flat(t.motel.sleep.Position, car.transform.position) < spotReachM) { SpotTown = t; SpotKind = "motel"; break; }
                }
            if (SpotTown == null) { Confirming = false; return; }
            var kb = ChaseGame.InputBlocked ? null : Keyboard.current;
            if (kb != null && car.SpeedKph * 0.621f < spotMph)
            {
                if (kb.fKey.wasPressedThisFrame && !Confirming) Confirming = true;
                else if (kb.escapeKey.wasPressedThisFrame) Confirming = false;
            }
        }

        /// <summary>Sleep at the spot the truck is on (false: not on one, or short of credits).</summary>
        public bool Sleep()
        {
            if (SpotTown == null || SpotKind == null || Busy) return false;
            return Sleep(SpotTown, SpotKind);
        }

        public bool Sleep(PlaceTown t, string kind)
        {
            if (Busy || t == null) return false;
            int cost = CostOf(t, kind);
            if (ChaseProfile.Current.credits < cost) { game.Say($"{(kind == "motel" ? t.motel.name : t.gas.name)}: {cost} cr for the night, you have {ChaseProfile.Current.credits}"); return false; }
            Confirming = false;
            var rng = new System.Random(ChaseProfile.Current.day * 7919 + ChaseProfile.Current.careerSeed);
            running = StartCoroutine(Run(FillOf(kind), cost, false, Quotes[rng.Next(Quotes.Length)], kind, t));
            return true;
        }

        /// <summary>The chaser passed out where they are (ChaseSanity).</summary>
        public void ForceSleep()
        {
            if (Busy) return;
            running = StartCoroutine(Run(forcedFill, 0, true, "you passed out.", "forced", null));
        }

        IEnumerator Run(float fill, int cost, bool forced, string text, string kind, PlaceTown t)
        {
            Busy = true; LastForced = forced; LastCost = cost; LastQuote = text;
            var world = game.world;
            // the truck stops where it is, the drone comes down, the camera stays
            if (world.car) { world.car.acceptInput = false; }
            if (game.Drone && game.Drone.Flying) game.Drone.Land(false);
            if (game.Photo && game.Photo.Active) game.Photo.SetActive(false);
            for (float f = 0f; f < 1f; f += Time.unscaledDeltaTime / Mathf.Max(fadeOut, 0.01f)) { Black = f; yield return null; }
            Black = 1f;
            overlayText = text;
            for (float f = 0f; f < 1f; f += Time.unscaledDeltaTime / Mathf.Max(forced ? quoteIn * 0.7f : quoteIn, 0.01f)) { TextAlpha = f; yield return null; }
            TextAlpha = 1f;
            for (float f = 0f; f < (forced ? quoteHold * 0.4f : quoteHold); f += Time.unscaledDeltaTime) yield return null;
            for (float f = 1f; f > 0f; f -= Time.unscaledDeltaTime / Mathf.Max(quoteOutS, 0.01f)) { TextAlpha = f; yield return null; }
            TextAlpha = 0f; overlayText = "";
            Advance(fill, cost, forced, kind, t);
            yield return null;
            for (float f = 1f; f > 0f; f -= Time.unscaledDeltaTime / Mathf.Max(fadeIn, 0.01f)) { Black = f; yield return null; }
            Black = 0f;
            Busy = false; running = null;
            if (world.car) world.car.acceptInput = !(game.Player && game.Player.OnFoot);
            game.Forecast.Open();
        }

        /// <summary>The night: the day's tally to the record, the bill paid, the bar refilled, the clock at 8 am next day, the
        /// storms gone, the career saved.</summary>
        void Advance(float fill, int cost, bool forced, string kind, PlaceTown t)
        {
            var p = ChaseProfile.Current;
            var world = game.world;
            p.credits -= cost;
            p.bestChaseCredits = Mathf.Max(p.bestChaseCredits, ChaseSetup.chaseCredits);
            game.LastDayCredits = ChaseSetup.chaseCredits; game.LastDayPhotos = ChaseSetup.chasePhotos; game.LastDay = p.day;
            ChaseSetup.chaseCredits = 0; ChaseSetup.chasePhotos = 0; ChaseSetup.chaseMesonetSeconds = 0;
            if (forced) p.passOuts++; else if (kind == "motel") p.motelNights++; else p.carCampNights++;
            if (t != null) p.spawnTown = t.key;
            p.day++;
            game.Sanity.Refill(fill);
            ChaseSetup.hour = 8f; ChaseSetup.morning = true; ChaseSetup.dayStarted = false;
            if (world.game) world.game.Clear();
            var panel = world.GetComponent<TestMapStormPanel>();
            if (panel) { panel.clockRuns = false; panel.hour = 8f; panel.Apply(); }
            Sleeps++;
            p.Save();
            game.Say(forced ? $"Day {p.day}: you woke at 8 am where you dropped, half rested"
                            : $"Day {p.day}: a {(kind == "motel" ? "night at " + t.motel.name : "night in the truck at " + t.gas.name)}, {cost} cr. Rested to {fill:P0}");
        }

        void OnGUI()
        {
            if (quote == null)
            {
                quote = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Italic, alignment = TextAnchor.MiddleCenter, wordWrap = true }; quote.normal.textColor = Color.white;
                bold = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }; bold.normal.textColor = Color.white;
                body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true }; body.normal.textColor = Color.white;
                button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 36 };
            }
            float W = Screen.width, H = Screen.height;
            if (Black > 0f || TextAlpha > 0f)
            {
                GUI.depth = -200;
                GUI.color = new Color(0f, 0f, 0f, Black);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, TextAlpha);
                if (!string.IsNullOrEmpty(overlayText)) GUI.Label(new Rect(W * 0.2f, H * 0.35f, W * 0.6f, H * 0.3f), overlayText, quote);
                GUI.color = Color.white;
                return;
            }
            var world = game ? game.world : null;
            if (!world || world.hudHidden || ChaseGame.InputBlocked || SpotTown == null || !game.InChase) return;
            var t = SpotTown; bool motel = SpotKind == "motel";
            string place = motel ? t.motel.name : t.gas.name;
            int cost = CostOf(t, SpotKind);
            if (!Confirming)
            {
                var r = new Rect(W * 0.5f - 320, H - 112, 640, 30);
                GUI.Box(r, GUIContent.none);
                string line = world.car.SpeedKph * 0.621f >= spotMph ? $"{place}: stop on the mark to sleep here"
                    : motel ? $"F  sleep here: {place}, {cost} cr a night  ·  rested to {motelFill:P0}"
                            : $"F  car camp here: {place}, {cost} cr  ·  rested to {campFill:P0}";
                GUI.Label(new Rect(r.x, r.y + 3, r.width, 24), line, bold);
                return;
            }
            var p = ChaseProfile.Current;
            var box = new Rect(W * 0.5f - 260, H * 0.5f - 95, 520, 190);
            GUI.Box(box, GUIContent.none); GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 20, box.y + 14, box.width - 40, box.height - 28));
            GUILayout.Label(place + ", " + t.name, bold);
            GUILayout.Label((motel ? $"A room for the night: {cost} cr. You wake at 8 am fully rested (sanity 100 %)."
                                   : $"A night in the truck on the lot: {cost} cr (a third of the motel). You wake at 8 am at {campFill:P0} sanity.")
                            + $"\nThe day's tally is saved and the next day's forecast comes with the morning. You have {p.credits:n0} cr.", body);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.enabled = p.credits >= cost;
            if (GUILayout.Button("Sleep until 8 am", button, GUILayout.Width(220))) Sleep();
            GUI.enabled = true;
            if (GUILayout.Button("Not now", button, GUILayout.Width(140))) Confirming = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
