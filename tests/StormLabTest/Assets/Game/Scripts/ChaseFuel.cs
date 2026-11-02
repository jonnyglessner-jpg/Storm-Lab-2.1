// Fuel (user, 2026-11-02): every vehicle has a tank and burns fuel while its engine runs (idle plus the engine's load,
// more at speed); the half-ton takes regular unleaded, the dually diesel and the rally hatchback premium only. Every
// town's truck stop sells all three at its own prices (Kansas averages with a few cents' spread, tools/testmap/world.py
// FUEL_BASE): pull up beside a pump island, stop, and hold F to pump; the credits go as the gallons come. An empty tank
// stops the engine (TestMapCar.powerScale through ChaseGame). The tank is kept in the profile per vehicle, between days.
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    public class ChaseFuel : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("gallons a second the pump delivers while F is held")]
        public float pumpGps = 2f;
        [Tooltip("how near a pump island the truck must be, m, and how slow, mph")]
        public float pumpReachM = 7f, pumpMph = 2f;
        [Tooltip("the low-fuel warning, as a share of the tank")]
        public float lowFraction = 0.12f;

        public float TankGal => ChaseSetup.Vehicle.tankGal;
        public FuelGrade Grade => ChaseSetup.Vehicle.fuel;
        public float Gallons { get { var s = State; return s == null ? TankGal : s.fuelGal; } }
        public bool Empty => State != null && State.fuelGal <= 0.0005f;
        /// <summary>the engine's power from the tank: 1, or 0 when it is dry</summary>
        public float PowerScale => Empty ? 0f : 1f;
        /// <summary>gallons an hour burning now</summary>
        public float BurnGph { get; private set; }
        /// <summary>the pump island the truck is at (null: none within reach), how far, and whether F is held on it</summary>
        public PlaceGas AtPump { get; private set; }
        public float PumpDistance { get; private set; }
        public bool Pumping { get; private set; }
        /// <summary>the running bill at the pump, credits (what the gallons so far cost)</summary>
        public float Tab { get; private set; }

        PlaceTown[] towns = new PlaceTown[0];
        float tabPaid, lastWarn = -1e9f; bool saidDry;
        GUIStyle small, bold;

        ChaseProfile.VehicleState State
        {
            get
            {
                if (!game || !game.InChase) return null;
                var s = ChaseProfile.Current.Vehicle(ChaseSetup.vehicle);
                if (s.fuelGal < 0f) s.fuelGal = TankGal;     // first use: a full tank
                return s;
            }
        }

        void Start() { towns = TestMapPlaces.Load(game && game.world ? game.world.navResource : "TestMapNav"); }

        /// <summary>The engine runs: the chaser is in the truck, the day is on and nobody is asleep.</summary>
        public bool EngineRunning => game && game.InChase && !ChaseSetup.morning && !(game.Player && game.Player.OnFoot) && !(game.Sleep && game.Sleep.Busy) && !Empty;

        void Update()
        {
            var world = game ? game.world : null;
            if (!world || !world.car) return;
            var s = State;
            if (s == null) { BurnGph = 0f; AtPump = null; Pumping = false; return; }
            var car = world.car; var v = ChaseSetup.Vehicle;
            // the burn, in real seconds (the engine runs in real time whatever the storm clock does)
            if (EngineRunning && Time.timeScale > 0f)
            {
                float speedFrac = Mathf.Clamp01(car.SpeedKph / Mathf.Max(v.topKph, 1f));
                BurnGph = v.idleGph + (v.fullGph - v.idleGph) * car.EngineLoad * (0.35f + 0.65f * speedFrac);
                s.fuelGal = Mathf.Max(0f, s.fuelGal - BurnGph * Time.deltaTime / 3600f);
                s.odometerKm = (float)(car.OdometerM / 1000.0);
                if (s.fuelGal <= 0.0005f && !saidDry) { saidDry = true; game.Say("Out of fuel: the engine died. Find a truck stop."); }
                if (s.fuelGal > 0.0005f) saidDry = false;
                if (s.fuelGal < TankGal * lowFraction && s.fuelGal > 0f && Time.unscaledTime - lastWarn > 90f) { lastWarn = Time.unscaledTime; game.Say($"Low fuel: {s.fuelGal:0.0} gal of {v.FuelName} left"); }
            }
            else BurnGph = 0f;

            // the pump: within reach of an island, stopped
            AtPump = null;
            if (towns.Length > 0)
            {
                var g = TestMapPlaces.NearestPump(towns, car.transform.position, out float d, out _);
                if (g != null && d < pumpReachM) { AtPump = g; PumpDistance = d; }
            }
            bool was = Pumping;
            Pumping = false;
            if (AtPump != null && !(game.Player && game.Player.OnFoot) && car.SpeedKph * 0.621f < pumpMph && !ChaseGame.InputBlocked && !ChaseSetup.morning)
            {
                var kb = Keyboard.current;
                if (kb != null && kb.fKey.isPressed) Pump(Time.unscaledDeltaTime);
            }
            if (was && !Pumping) EndPumping();
        }

        /// <summary>Pump for `seconds` (F held, or a check): gallons into the tank, credits off the balance as they go.</summary>
        public void Pump(float seconds)
        {
            var s = State; if (s == null || AtPump == null) return;
            var p = ChaseProfile.Current;
            float price = AtPump.Price(Grade);
            float room = TankGal - s.fuelGal;
            if (room <= 0.001f) { Pumping = false; return; }
            float want = Mathf.Min(pumpGps * seconds, room);
            // what the credits allow (the bill is charged a whole credit at a time)
            float canPay = p.credits + (tabPaid - Tab);     // credits left plus the fraction already paid toward the next
            if (canPay <= 0f) { if (Time.unscaledTime - lastWarn > 3f) { lastWarn = Time.unscaledTime; game.Say("No credits left for fuel"); } Pumping = false; return; }
            want = Mathf.Min(want, canPay / price);
            s.fuelGal += want; Tab += want * price; p.fuelBought += want;
            while (tabPaid + 1f <= Tab + 1e-4f && p.credits > 0) { p.credits--; tabPaid += 1f; }
            Pumping = true;
        }

        void EndPumping()
        {
            // the odd cents round up to the next credit, as a pump does
            var p = ChaseProfile.Current;
            if (Tab > tabPaid + 1e-3f && p.credits > 0) { p.credits--; tabPaid += 1f; }
            if (Tab > 0f) game.Say($"Fuelled up: {Tab / Mathf.Max(AtPump != null ? AtPump.Price(Grade) : 1f, 0.01f):0.0} gal, {Mathf.CeilToInt(Tab)} cr");
            Tab = 0f; tabPaid = 0f;
            p.Save();
        }

        /// <summary>For the checks: set the tank.</summary>
        public void TestSetGallons(float gal) { var s = State; if (s != null) s.fuelGal = Mathf.Clamp(gal, 0f, TankGal); }

        void OnGUI()
        {
            var world = game ? game.world : null;
            if (!world || world.hudHidden || ChaseGame.InputBlocked || AtPump == null || !game.InChase || ChaseSetup.morning) return;
            if (game.Player && game.Player.OnFoot) return;
            if (small == null)
            {
                small = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter }; small.normal.textColor = Color.white;
                bold = new GUIStyle(small) { fontStyle = FontStyle.Bold, fontSize = 17 };
            }
            float W = Screen.width, H = Screen.height;
            var v = ChaseSetup.Vehicle; var s = State; if (s == null) return;
            float price = AtPump.Price(Grade), room = TankGal - s.fuelGal;
            var r = new Rect(W * 0.5f - 320, H - 112, 640, 52);
            GUI.Box(r, GUIContent.none);
            if (Pumping) GUI.Label(new Rect(r.x, r.y + 4, r.width, 24), $"PUMPING  {v.FuelName}   {s.fuelGal:0.0} / {TankGal:0} gal   {Mathf.CeilToInt(Tab)} cr", bold);
            else if (room < 0.05f) GUI.Label(new Rect(r.x, r.y + 4, r.width, 24), $"{AtPump.name}: the tank is full", bold);
            else if (world.car.SpeedKph * 0.621f >= pumpMph) GUI.Label(new Rect(r.x, r.y + 4, r.width, 24), $"{AtPump.name}: stop at the pump to fuel up", bold);
            else GUI.Label(new Rect(r.x, r.y + 4, r.width, 24), $"F  hold to fuel up  ·  {v.FuelName} {price:0.00} cr/gal  ·  {room:0.0} gal to fill = {Mathf.CeilToInt(room * price)} cr", bold);
            GUI.Label(new Rect(r.x, r.y + 28, r.width, 22), $"{AtPump.name}   regular {AtPump.regular:0.00}  ·  premium {AtPump.premium:0.00}  ·  diesel {AtPump.dieselPrice:0.00}   ({v.name}: {v.FuelName} only)", small);
        }
    }
}
