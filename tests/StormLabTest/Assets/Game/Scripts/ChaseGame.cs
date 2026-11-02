// The chase game on a map (added to the world by TestMapWorld at Start): puts the menu's set-up into effect (ChaseSetup:
// the vehicle, the gear, the sandbox keys and rewinding off), owns the chaser on foot (ChasePlayer), the photo camera
// (ChaseCamera), the mesonet (ChaseMesonet), the drone (ChaseDrone), the tank (ChaseFuel), the truck's condition
// (ChaseWear), the chaser's sanity (ChaseSanity), sleeping (ChaseSleep) and the morning forecast (ChaseForecast), keeps
// the credits (ChaseProfile) and puts the map away until its screen is owned (MapAllowed). Free roam is the test map as
// it was, with the gear owned and no credits.
//
// A career (user, 2026-11-02) runs day by day: every day opens at 8 am on the forecast, the chaser picks a truck stop to
// start from, the day fast-forwards to the first towering cumulus (the storms' initiation hour) and the day's two storms
// (ChaseSetup.DaySeed) cross the map over the game hour; when they are gone the clock keeps running and the chaser drives
// to a motel or a truck stop and sleeps, which saves the career and brings the next morning. Fuel burns, the truck wears,
// and the sanity bar drains with the hours awake.
//
//   Esc  menu and shop    E  get out / in    X  photo camera    B  drone    F  fuel / repairs / sleep at a marked spot
//   T / P / ]  clock speed, pause, +5 min (no rewind in a chase)
//   F3  dev mode (user, 2026-11-02): the sandbox keys back in a career (F free camera, V truck under it, 1-9 spots, N new
//       storms, [ rewind, G game mode, the Tab storm and lighting panel; its clock slider moves the day's clock)
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
        public ChaseFuel Fuel { get; private set; }
        public ChaseWear Wear { get; private set; }
        public ChaseSanity Sanity { get; private set; }
        public ChaseSleep Sleep { get; private set; }
        public ChaseForecast Forecast { get; private set; }

        static ChaseGame instance;
        TestMapMinimap minimap;
        /// <summary>a menu, the forecast or a night is up: the map's keys and the car ignore the keyboard</summary>
        public static bool InputBlocked => instance && ((instance.Menu && instance.Menu.Showing) || (instance.Forecast && instance.Forecast.Showing) || (instance.Sleep && instance.Sleep.Busy));
        /// <summary>photos and mesonet data pay: in a running chase day only</summary>
        public bool EarnsCredits => ChaseSetup.state == ChaseSetup.State.Chase && !ChaseSetup.chaseOver && !ChaseSetup.morning;
        public bool InChase => ChaseSetup.state == ChaseSetup.State.Chase;
        /// <summary>how far the day's clock went this frame, hours (the sanity's clock)</summary>
        public float ClockDeltaHours { get; private set; }
        /// <summary>the day's storms have come and gone (the game hour is up)</summary>
        public bool StormsDone { get; private set; }
        /// <summary>yesterday's tally for the morning forecast</summary>
        public int LastDay { get; set; } public int LastDayCredits { get; set; } public int LastDayPhotos { get; set; }

        struct Toast { public string text; public float until; }
        readonly List<Toast> toasts = new List<Toast>();
        float flashAmount, flashUntil;
        GUIStyle big, small, mid, tiny;
        TestMapStormPanel panel;
        bool saidDone;
        float devHourOffset, lastPanelHour = -1f, extraHours, lastGameMinutes;

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
            Fuel = gameObject.AddComponent<ChaseFuel>(); Fuel.game = this;
            Wear = gameObject.AddComponent<ChaseWear>(); Wear.game = this;
            Sanity = gameObject.AddComponent<ChaseSanity>(); Sanity.game = this;
            Sleep = gameObject.AddComponent<ChaseSleep>(); Sleep.game = this;
            Forecast = gameObject.AddComponent<ChaseForecast>(); Forecast.game = this;
            minimap = GetComponent<TestMapMinimap>();
            Time.timeScale = 1f;
            ApplyState();
        }

        void OnDestroy() { if (instance == this) instance = null; Time.timeScale = 1f; }

        /// <summary>Put ChaseSetup.state into effect on this map: a chase (a career day) starts at its morning, free roam is
        /// the sandbox with the gear owned, the menu shows over whatever the map is doing.</summary>
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
                    if (Forecast) Forecast.Close();
                    if (Menu) Menu.Close();
                    break;
                default:
                    // the menu over the map: the sandbox storm keeps going behind it
                    world.sandboxKeys = true; world.allowRewind = true;
                    if (panel) { panel.locked = false; panel.show = false; }
                    if (Forecast) Forecast.Close();
                    if (Menu) Menu.ShowMain();
                    break;
            }
        }

        /// <summary>A career day's morning: the truck fitted and parked at the last station, no storms out, 8 am, the forecast up.</summary>
        void StartChaseHere()
        {
            var map = ChaseSetup.Map;
            if (Photo && Photo.Active) Photo.SetActive(false);
            if (Player && Player.OnFoot) Player.GetIn();
            ChaseVehicles.Apply(world.car, world, ChaseSetup.Vehicle);
            world.sandboxKeys = false; world.allowRewind = false; world.showHelp = true;
            if (panel) { panel.locked = true; panel.show = false; }
            // game mode without storms until the chaser heads out (BeginDay)
            if (!world.gameMode) world.SetGameMode(true);
            if (world.game) { world.game.preset = map.preset; world.game.Clear(); world.game.playing = true; }
            world.SetClockSpeed(0);
            ChaseSetup.morning = true; ChaseSetup.dayStarted = false; ChaseSetup.hour = 8f; StormsDone = false; saidDone = false;
            devHourOffset = 0f; lastPanelHour = -1f; extraHours = 0f; lastGameMinutes = 0f;
            if (panel) { panel.clockRuns = false; panel.hour = 8f; panel.Apply(); }
            ApplyDevMode();
            // parked at the last station (or the first town's)
            var towns = TestMapPlaces.Load(world.navResource);
            var t = TestMapPlaces.TownOf(towns, ChaseProfile.Current.spawnTown);
            if (t != null && t.gas?.sleep != null) world.car.Teleport(t.gas.sleep.Position, Quaternion.Euler(0f, t.gas.sleep.yaw, 0f));
            else if (world.spots.Length > 0) world.TeleportTo(0);
            // first person from the driver's seat; the view from behind the truck is the drone's follow cam
            if (world.cameraRig) { world.cameraRig.mode = ChaseProfile.Current.Owns("drone_follow") ? TestMapCamera.Mode.Chase : TestMapCamera.Mode.Cab; world.cameraRig.held = false; }
            Mesonet.SetMounted(ChaseSetup.mountMesonet && ChaseProfile.Current.Owns("mesonet"));
            Drone.ResetForChase();
            if (Menu) Menu.Close();
            toasts.Clear();
            Sanity.Refill(ChaseProfile.Current.sanity);   // (the bar as saved; the clock behind it)
            Forecast.Open();
        }

        /// <summary>Head out (the forecast's choice): the day's storms from the day's seed at their initiation hour, the truck
        /// parked at the chosen town's truck stop.</summary>
        public void BeginDay(string townKey)
        {
            var p = ChaseProfile.Current;
            var map = ChaseSetup.Map;
            int seed = ChaseSetup.DaySeed(p.careerSeed, p.day);
            if (!world.gameMode) world.SetGameMode(true);
            if (world.game)
            {
                world.game.preset = map.preset; world.game.seed = seed;
                world.game.Spawn(); world.game.playing = true; world.game.gameMinutes = 0f; world.game.Apply();
            }
            world.SetClockSpeed(0);
            ChaseSetup.seed = seed;
            ChaseSetup.startHour = ChaseSetup.StartHourFor(seed);
            ChaseSetup.hour = ChaseSetup.startHour; ChaseSetup.morning = false; ChaseSetup.dayStarted = true; StormsDone = false; saidDone = false;
            devHourOffset = 0f; lastPanelHour = -1f; extraHours = 0f; lastGameMinutes = 0f;
            if (panel) { panel.clockRuns = false; panel.hour = ChaseSetup.hour; panel.Apply(); }
            var towns = TestMapPlaces.Load(world.navResource);
            var t = TestMapPlaces.TownOf(towns, townKey);
            if (t != null && t.gas?.sleep != null)
            {
                if (Player && Player.OnFoot) Player.GetIn();
                world.car.Teleport(t.gas.sleep.Position, Quaternion.Euler(0f, t.gas.sleep.yaw, 0f));
                p.spawnTown = t.key;
            }
            world.car.acceptInput = !(Player && Player.OnFoot) && !(Photo && Photo.Active) && !(Drone && Drone.Flying);
            Drone.ResetForChase();
            p.Save();
            Say($"Day {p.day}: {ChaseSetup.ClockText(ChaseSetup.hour)}, towering cumulus to the west. {(t != null ? t.gas.name + ", " + t.name : map.name)}, {ChaseSetup.Vehicle.name}");
        }

        void Update()
        {
            ClockDeltaHours = 0f;
            var kb = InputBlocked ? null : Keyboard.current;
            if (kb != null && kb.f3Key.wasPressedThisFrame && InChase) SetDevMode(!ChaseSetup.devMode);
            if (InChase && world.game)
            {
                bool dev = ChaseSetup.devMode;
                // dev mode: the panel's clock slider (and its time-of-day buttons) move the day's clock
                float drag = 0f;
                if (dev && panel && lastPanelHour >= 0f && Mathf.Abs(panel.hour - lastPanelHour) > 1e-4f) drag = Mathf.DeltaAngle(lastPanelHour * 15f, panel.hour * 15f) / 15f;
                // the day's clock: with the game clock while the storms are out, then on at the same speed (T sets it)
                if (!ChaseSetup.morning && !(Sleep && Sleep.Busy) && Time.timeScale > 0f && (world.game.playing || dev))
                {
                    // the hour of day = the storms' start hour + the game clock (which T, ] and dev's [ and N move) + the
                    // panel's drags in dev mode + the time run on after the game hour (which a rewind into the hour drops)
                    float speed = world.game.playing ? world.game.minutesPerSecond * 60f : 0f;
                    float before = ChaseSetup.hour;
                    if (world.game.Scenario != null) lastGameMinutes = world.game.gameMinutes;
                    if (world.game.Scenario != null && world.game.gameMinutes < world.game.gameLength - 1e-3f) extraHours = 0f;
                    else extraHours += Time.deltaTime * speed / 3600f;
                    devHourOffset += drag;
                    ChaseSetup.hour = Mathf.Repeat(ChaseSetup.startHour + lastGameMinutes / 60f + devHourOffset + extraHours, 24f);
                    ClockDeltaHours = Mathf.Repeat(ChaseSetup.hour - before + 24f, 24f);
                    if (ClockDeltaHours > 12f) ClockDeltaHours = 0f;
                }
                if (panel) { panel.clockRuns = false; panel.hour = ChaseSetup.hour; lastPanelHour = panel.hour; }
                bool done = ChaseSetup.dayStarted && world.game.Scenario != null && world.game.gameMinutes >= world.game.gameLength - 1e-3f;
                if (done && !saidDone) { saidDone = true; Say("The storms have moved off the map. Find a motel or a truck stop and sleep to save the day."); }
                StormsDone = done;
            }
            if (minimap) minimap.hidden = !MapAllowed;
        }

        void LateUpdate()
        {
            if (!world || !world.car) return;
            if (InChase)
            {
                world.car.powerScale = Fuel.PowerScale * Wear.PowerScale;
                world.car.speedScale = Sanity.SpeedScale;
                // the forecast and a night take the wheel away
                if ((Forecast && Forecast.Showing) || (Sleep && Sleep.Busy)) world.car.acceptInput = false;
            }
            else { world.car.powerScale = 1f; world.car.speedScale = 1f; if (Player) Player.speedScale = 1f; }
        }

        /// <summary>Dev mode on or off (F3, the pause menu): the sandbox keys and rewinding, the storm panel unlocked, the free
        /// camera allowed; off again puts the truck back under a free camera and locks the panel.</summary>
        public void SetDevMode(bool on)
        {
            ChaseSetup.devMode = on;
            ApplyDevMode();
            if (!on && world.cameraRig && world.cameraRig.mode == TestMapCamera.Mode.Free) world.cameraRig.DropTruck();
            Say(on ? "DEV MODE: F free camera, V truck here, 1-9 spots, N new storms, T / P / [ ] clock, G game mode, Tab panel, M map" : "Dev mode off");
        }

        void ApplyDevMode()
        {
            bool on = ChaseSetup.devMode && InChase;
            world.sandboxKeys = on; world.allowRewind = on;
            if (panel) { panel.locked = !on; if (!on) panel.show = false; }
        }

        /// <summary>The map (bottom right) is a screen the chaser must own (user, 2026-11-02): the laptop shows it in the
        /// truck, the phone on foot. Free roam and the menu have it as the sandbox does.</summary>
        public bool MapAllowed
        {
            get
            {
                if (!InChase || ChaseSetup.devMode) return true;   // (dev mode: the map whatever is owned)
                bool onFoot = Player && Player.OnFoot;
                return ChaseProfile.Current.Owns(onFoot ? "phone" : "laptop");
            }
        }

        /// <summary>The career is abandoned for now (the pause menu): the tally goes to the profile and the summary comes up.
        /// The day itself is not saved: a career resumes at the morning of the day last slept into.</summary>
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

        /// <summary>Credits earned (a chase only; the caller checks EarnsCredits), times the sanity's bonus. why null: quietly (the mesonet's stream).</summary>
        public void Earn(int amount, string why)
        {
            if (amount <= 0 || !EarnsCredits) return;
            float mult = Sanity ? Sanity.ProfitMultiplier : 1f;
            int paid = Mathf.Max(1, Mathf.RoundToInt(amount * mult));
            ChaseProfile.Current.Earn(paid);
            ChaseSetup.chaseCredits += paid;
            flashAmount += paid; flashUntil = Time.unscaledTime + 1.5f;
            if (why != null) Say($"{why}   +{paid} cr{(mult > 1.01f ? $" (x{mult:0.00} sleepless)" : "")}");
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
                tiny = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                big.normal.textColor = mid.normal.textColor = small.normal.textColor = tiny.normal.textColor = Color.white;
            }
            float W = Screen.width, H = Screen.height;
            if (ChaseSetup.state != ChaseSetup.State.Menu)
            {
                // the credits, top centre, with what this day has earned
                var r = new Rect(W * 0.5f - 130, 16, 260, 50);
                GUI.Box(r, GUIContent.none);
                GUI.Label(new Rect(r.x, r.y + 2, r.width, 30), $"{ChaseProfile.Current.credits:n0} credits", big);
                string sub = InChase ? $"today +{ChaseSetup.chaseCredits:n0}" : "free roam: nothing to earn";
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
                    var tr = new Rect(W * 0.5f - 300, y, 600, 26);
                    GUI.Box(tr, GUIContent.none); GUI.Label(tr, t.text, mid);
                    GUI.color = Color.white; y += 28;
                }
            }
            if (InChase) CareerBox(H);
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

        /// <summary>The day, the clock, the tank, the truck's condition and the sanity bar, beside the speed box.</summary>
        void CareerBox(float H)
        {
            var p = ChaseProfile.Current; var v = ChaseSetup.Vehicle;
            var r = new Rect(452, H - 132, 380, 116);
            GUI.Box(r, GUIContent.none);
            float x = r.x + 12, y = r.y + 6;
            string where = "";
            var town = TestMapPlaces.TownAt(TestMapPlaces.Load(world.navResource), world.car.transform.position, 200f);
            if (town != null) where = "  ·  " + town.name;
            GUI.Label(new Rect(x, y, 360, 20), $"Day {p.day}  ·  {ChaseSetup.ClockText(ChaseSetup.hour)}{(ChaseSetup.morning ? "  ·  morning" : StormsDone ? "  ·  storms gone: sleep to save" : "")}{where}{(ChaseSetup.devMode ? "  ·  DEV" : "")}", tiny); y += 22;
            float gal = Fuel.Gallons, tank = Fuel.TankGal;
            Bar(new Rect(x, y + 3, 110, 12), gal / Mathf.Max(tank, 1f), gal / Mathf.Max(tank, 1f) > 0.15f ? new Color(0.95f, 0.75f, 0.2f) : new Color(1f, 0.35f, 0.3f));
            GUI.Label(new Rect(x + 118, y - 2, 250, 20), $"Fuel {gal:0.0} / {tank:0} gal {v.FuelName}{(Fuel.Empty ? "  ·  EMPTY" : Fuel.BurnGph > 0.01f ? $"  ·  {Fuel.BurnGph:0.0} gal/h" : "")}", tiny); y += 22;
            float c = Wear.Condition;
            Bar(new Rect(x, y + 3, 110, 12), c, c > 0.4f ? new Color(0.4f, 0.8f, 0.4f) : c > 0.12f ? new Color(0.95f, 0.75f, 0.2f) : new Color(1f, 0.35f, 0.3f));
            GUI.Label(new Rect(x + 118, y - 2, 250, 20), $"Truck {c:P0}{(Wear.Limping ? "  ·  LIMPING" : "")}  ·  {Wear.Surface}", tiny); y += 22;
            float s = Sanity.Sanity; int ph = Sanity.Phase;
            Bar(new Rect(x, y + 3, 110, 12), s, ph == 0 ? new Color(0.5f, 0.75f, 1f) : ph == 1 ? new Color(0.95f, 0.75f, 0.2f) : new Color(1f, 0.35f, 0.3f));
            GUI.Label(new Rect(x + 118, y - 2, 260, 20), $"Sanity {s:P0}  ·  awake {Sanity.AwakeHours:0.0} h{(ph > 0 ? $"  ·  pays x{Sanity.ProfitMultiplier:0.00}, speed {Sanity.SpeedScale:P0}" : "")}{(Sanity.NearTornado ? "  ·  near a tornado" : "")}", tiny);
        }

        static void Bar(Rect r, float f, Color c)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c; GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(f), r.height - 2), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
