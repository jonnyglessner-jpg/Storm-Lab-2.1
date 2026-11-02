// The chase game on a map (added to the world by TestMapWorld at Start): puts the menu's set-up into effect (ChaseSetup:
// the vehicle, the gear, the game's storms from the seed, the sandbox keys and rewinding off), owns the chaser on foot
// (ChasePlayer), the photo camera (ChaseCamera), the mesonet (ChaseMesonet) and the drone (ChaseDrone), keeps the
// credits (ChaseProfile), puts the map away until its screen is owned (MapAllowed) and ends the chase when the game
// clock runs out. Free roam is the test map as it was, with the gear owned and no credits.
//
//   Esc  menu and shop    E  get out / in    X  photo camera    B  drone    T / P / ]  clock speed, pause, +5 min (no rewind in a chase)
using System.Collections.Generic;
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    [DefaultExecutionOrder(92)]
    public class ChaseGame : MonoBehaviour
    {
        public TestMapWorld world;
        public ChaseMenu Menu { get; private set; }
        public ChasePlayer Player { get; private set; }
        public ChaseCamera Photo { get; private set; }
        public ChaseMesonet Mesonet { get; private set; }
        public ChaseDrone Drone { get; private set; }

        static ChaseGame instance;
        TestMapMinimap minimap;
        /// <summary>a menu is up: the map's keys and the car ignore the keyboard</summary>
        public static bool InputBlocked => instance && instance.Menu && instance.Menu.Showing;
        /// <summary>photos and mesonet data pay: in a running chase only</summary>
        public bool EarnsCredits => ChaseSetup.state == ChaseSetup.State.Chase && !ChaseSetup.chaseOver;
        public bool InChase => ChaseSetup.state == ChaseSetup.State.Chase;

        struct Toast { public string text; public float until; }
        readonly List<Toast> toasts = new List<Toast>();
        float flashAmount, flashUntil;
        GUIStyle big, small, mid;
        TestMapStormPanel panel;

        void Awake() { instance = this; }

        void Start()
        {
            if (!world) world = GetComponent<TestMapWorld>();
            panel = GetComponent<TestMapStormPanel>();
            Menu = gameObject.AddComponent<ChaseMenu>(); Menu.game = this;
            Player = gameObject.AddComponent<ChasePlayer>(); Player.game = this;
            Photo = gameObject.AddComponent<ChaseCamera>(); Photo.game = this;
            Mesonet = gameObject.AddComponent<ChaseMesonet>(); Mesonet.game = this;
            Drone = gameObject.AddComponent<ChaseDrone>(); Drone.game = this;
            minimap = GetComponent<TestMapMinimap>();
            Time.timeScale = 1f;
            ApplyState();
        }

        void OnDestroy() { if (instance == this) instance = null; Time.timeScale = 1f; }

        /// <summary>Put ChaseSetup.state into effect on this map: a chase starts (or restarts) from the menu's choices, free
        /// roam is the sandbox with the gear owned, the menu shows over whatever the map is doing.</summary>
        public void ApplyState()
        {
            if (!panel) panel = GetComponent<TestMapStormPanel>();
            switch (ChaseSetup.state)
            {
                case ChaseSetup.State.Chase: StartChaseHere(); break;
                case ChaseSetup.State.FreeRoam:
                    world.sandboxKeys = true; world.allowRewind = true;
                    if (panel) { panel.locked = false; panel.show = true; }
                    ChaseVehicles.Apply(world.car, world, ChaseSetup.Vehicle);
                    Mesonet.SetMounted(ChaseProfile.Current.Owns("mesonet"));
                    if (Menu) Menu.Close();
                    break;
                default:
                    // the menu over the map: the sandbox storm keeps going behind it
                    world.sandboxKeys = true; world.allowRewind = true;
                    if (panel) { panel.locked = false; panel.show = false; }
                    if (Menu) Menu.ShowMain();
                    break;
            }
        }

        void StartChaseHere()
        {
            var map = ChaseSetup.Map;
            if (Photo && Photo.Active) Photo.SetActive(false);
            if (Player && Player.OnFoot) Player.GetIn();
            ChaseVehicles.Apply(world.car, world, ChaseSetup.Vehicle);
            world.sandboxKeys = false; world.allowRewind = false; world.showHelp = true;
            if (panel) { panel.locked = true; panel.show = false; }
            // the game's two storms from the chase's seed, at real time, the truck at the map's first spot
            if (!world.gameMode) world.SetGameMode(true);
            if (world.game)
            {
                world.game.preset = map.preset; world.game.seed = ChaseSetup.seed;
                world.game.Spawn(); world.game.playing = true;
            }
            world.SetClockSpeed(0);
            // the time of day from the seed (noon, afternoon or dusk), run on by the game clock (Update)
            if (panel) { panel.clockRuns = false; panel.hour = ChaseSetup.startHour; panel.Apply(); }
            if (world.spots.Length > 0) world.TeleportTo(0);
            // first person from the driver's seat; the view from behind the truck is the drone's follow cam
            if (world.cameraRig) { world.cameraRig.mode = ChaseProfile.Current.Owns("drone_follow") ? TestMapCamera.Mode.Chase : TestMapCamera.Mode.Cab; world.cameraRig.held = false; }
            Mesonet.SetMounted(ChaseSetup.mountMesonet && ChaseProfile.Current.Owns("mesonet"));
            Drone.ResetForChase();
            if (Menu) Menu.Close();
            toasts.Clear();
            Say($"Chase on: {map.name}, {ChaseSetup.Vehicle.name}, game {ChaseSetup.seed}, {ChaseSetup.ClockText(ChaseSetup.startHour)} ({ChaseSetup.TimeOfDayName(ChaseSetup.startHour)})");
        }

        void Update()
        {
            if (InChase && !ChaseSetup.chaseOver && world.game && world.game.Scenario != null && world.game.gameMinutes >= world.game.gameLength - 1e-3f)
                EndChase();
            // the clock of day keeps the game's time: a minute of game is a minute of day, at whatever speed T sets
            if (InChase && panel && world.game) { panel.clockRuns = false; panel.hour = Mathf.Repeat(ChaseSetup.startHour + world.game.gameMinutes / 60f, 24f); }
            if (minimap) minimap.hidden = !MapAllowed;
        }

        /// <summary>The map (bottom right) is a screen the chaser must own (user, 2026-11-02): the laptop shows it in the
        /// truck, the phone on foot. Free roam and the menu have it as the sandbox does.</summary>
        public bool MapAllowed
        {
            get
            {
                if (!InChase) return true;
                bool onFoot = Player && Player.OnFoot;
                return ChaseProfile.Current.Owns(onFoot ? "phone" : "laptop");
            }
        }

        /// <summary>The hour is up: the tally goes to the profile and the summary comes up.</summary>
        public void EndChase()
        {
            if (ChaseSetup.chaseOver) return;
            ChaseSetup.chaseOver = true;
            var p = ChaseProfile.Current;
            p.bestChaseCredits = Mathf.Max(p.bestChaseCredits, ChaseSetup.chaseCredits);
            p.Save();
            if (Photo && Photo.Active) Photo.SetActive(false);
            if (Menu) Menu.ShowSummary();
        }

        /// <summary>Credits earned (a chase only; the caller checks EarnsCredits). why null: quietly (the mesonet's stream).</summary>
        public void Earn(int amount, string why)
        {
            if (amount <= 0 || !EarnsCredits) return;
            ChaseProfile.Current.Earn(amount);
            ChaseSetup.chaseCredits += amount;
            flashAmount += amount; flashUntil = Time.unscaledTime + 1.5f;
            if (why != null) Say($"{why}   +{amount} cr");
            if (ChaseSetup.chaseCredits % 50 == 0 || why != null) ChaseProfile.Current.Save();
        }

        public void Say(string text) { toasts.Add(new Toast { text = text, until = Time.unscaledTime + 4f }); if (toasts.Count > 4) toasts.RemoveAt(0); }

        void OnGUI()
        {
            if (!world || world.hudHidden || InputBlocked) return;
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                mid = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
                small = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
                big.normal.textColor = mid.normal.textColor = small.normal.textColor = Color.white;
            }
            float W = Screen.width, H = Screen.height;
            if (ChaseSetup.state != ChaseSetup.State.Menu)
            {
                // the credits, top centre, with what this chase has earned
                var r = new Rect(W * 0.5f - 130, 16, 260, 50);
                GUI.Box(r, GUIContent.none);
                GUI.Label(new Rect(r.x, r.y + 2, r.width, 30), $"{ChaseProfile.Current.credits:n0} credits", big);
                string sub = InChase ? $"this chase +{ChaseSetup.chaseCredits:n0}" : "free roam: nothing to earn";
                if (Time.unscaledTime < flashUntil && flashAmount > 0) sub = $"+{flashAmount:n0}";
                else flashAmount = 0;
                GUI.Label(new Rect(r.x, r.y + 28, r.width, 20), sub, small);
                // what happened lately
                float y = r.yMax + 6;
                toasts.RemoveAll(t => Time.unscaledTime > t.until);
                foreach (var t in toasts)
                {
                    float a = Mathf.Clamp01((t.until - Time.unscaledTime) / 0.8f);
                    GUI.color = new Color(1f, 1f, 1f, a);
                    var tr = new Rect(W * 0.5f - 260, y, 520, 26);
                    GUI.Box(tr, GUIContent.none); GUI.Label(tr, t.text, mid);
                    GUI.color = Color.white; y += 28;
                }
            }
            // getting in and out, the drone
            if (Player && !(Photo && Photo.Active) && !(Drone && Drone.Flying))
            {
                string hint = null;
                if (Player.OnFoot && Player.NearTruck) hint = "E  get in";
                else if (Player.OnFoot) hint = "walk back to the truck and press E to get in";
                else if (world.car && world.car.SpeedKph * 0.621f < Player.getOutMph && world.cameraRig && world.cameraRig.mode != TestMapCamera.Mode.Free) hint = "E  get out";
                if (Drone && Drone.Owned && !Drone.Following && Drone.Battery > 0.05f) hint = (hint != null ? hint + "      " : "") + "B  launch the drone";
                if (hint != null) GUI.Label(new Rect(W * 0.5f - 300, H - 36, 600, 24), hint, small);
            }
        }
    }
}
