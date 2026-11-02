// Wear and repairs (user, 2026-11-02): a vehicle's condition (0..1, kept in the profile per vehicle) falls with the
// miles driven, three times as fast on gravel and nine times off the road, with every collision (by the change of
// speed) and with time spent in a tornado's wind (debris past about 55 mph). A worn engine gives less power, and at
// 12 % the truck can only limp. Every town has a mechanic: stop in the marked service bay and press F; the bill is the
// missing condition at the vehicle's rate and the shop's.
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    public class ChaseWear : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("condition lost per km on pavement, on gravel, and off the road")]
        public float wearPaved = 0.0004f, wearGravel = 0.0012f, wearOffRoad = 0.0036f;
        [Tooltip("tornado wind over this, m/s, wears the truck: per second, (over / 40) x rate")]
        public float windWearMps = 25f, windWearRate = 0.012f;
        [Tooltip("a collision's wear: (change of speed / 10 m/s)^1.5 x this")]
        public float impactWear = 0.06f;
        [Tooltip("how near the service bay's mark the truck must be, m, and how slow, mph")]
        public float bayReachM = 5f, bayMph = 2f;

        public float Condition => State?.condition ?? 1f;
        /// <summary>the engine's power from its condition: full down to half at 12 %, then a limp</summary>
        public float PowerScale { get { float c = Condition; return c > 0.12f ? Mathf.Lerp(0.5f, 1f, (c - 0.12f) / 0.88f) : 0.12f; } }
        public bool Limping => Condition <= 0.12f;
        /// <summary>"paved", "gravel" or "off road": what the truck is driving on (sampled twice a second)</summary>
        public string Surface { get; private set; } = "paved";
        public PlaceMechanic AtMechanic { get; private set; }
        public PlaceTown MechanicTown { get; private set; }
        public int RepairCost => AtMechanic == null ? 0 : CostAt(AtMechanic);
        public bool Confirming { get; private set; }
        /// <summary>condition lost to collisions, wind and the road this session (the checks read them)</summary>
        public float ImpactWear { get; private set; }
        public float WindWear { get; private set; }
        public float RoadWear { get; private set; }

        PlaceTown[] towns = new PlaceTown[0];
        double lastOdo = -1; float lastImpactSeen = -1e9f, surfaceTimer; bool saidLimp;
        GUIStyle small, bold, body, button;

        ChaseProfile.VehicleState State => game && game.InChase ? ChaseProfile.Current.Vehicle(ChaseSetup.vehicle) : null;

        void Start() { towns = TestMapPlaces.Load(game && game.world ? game.world.navResource : "TestMapNav"); }

        public int CostAt(PlaceMechanic m) => Mathf.CeilToInt((1f - Condition) * 100f * ChaseSetup.Vehicle.repairRate * (m.rate / 110f));

        void Update()
        {
            var world = game ? game.world : null;
            if (!world || !world.car) return;
            var s = State;
            if (s == null) { AtMechanic = null; Confirming = false; lastOdo = -1; return; }
            var car = world.car;
            if (!ChaseSetup.morning && !(game.Sleep && game.Sleep.Busy) && Time.timeScale > 0f)
            {
                // the road: what is under the truck, twice a second
                surfaceTimer -= Time.deltaTime;
                if (surfaceTimer <= 0f)
                {
                    surfaceTimer = 0.5f;
                    var roads = world.GetComponent<TestMapMinimap>()?.Roads;
                    if (roads != null)
                    {
                        var sp = roads.Nearest(new Vector2(car.transform.position.x, car.transform.position.z));
                        Surface = sp.edge < 0 || sp.off > 12f ? "off road" : roads.edges[sp.edge].cls == 2 ? "gravel" : "paved";
                    }
                }
                if (lastOdo < 0) lastOdo = car.OdometerM;
                double km = (car.OdometerM - lastOdo) / 1000.0; lastOdo = car.OdometerM;
                float rate = Surface == "off road" ? wearOffRoad : Surface == "gravel" ? wearGravel : wearPaved;
                float w = (float)km * rate;
                RoadWear += w; s.condition -= w;
                // the tornado's wind
                var wind = world.gameMode && world.game ? world.game.TornadoWindAt(car.transform.position) : Vector3.zero;
                float over = wind.magnitude - windWearMps;
                if (over > 0f) { float ww = over / 40f * windWearRate * Time.deltaTime; WindWear += ww; s.condition -= ww; }
                // collisions
                if (car.LastImpactTime > lastImpactSeen)
                {
                    lastImpactSeen = car.LastImpactTime;
                    float iw = Mathf.Pow(car.LastImpact / 10f, 1.5f) * impactWear;
                    if (car.LastImpact > 2f) { ImpactWear += iw; s.condition -= iw; if (iw > 0.02f) game.Say($"That hit cost {iw * 100f:0} % of the truck"); }
                }
                s.condition = Mathf.Clamp01(s.condition);
                if (Limping && !saidLimp) { saidLimp = true; game.Say("The truck is worn out: it can only limp. Find a mechanic."); }
                if (!Limping) saidLimp = false;
            }
            // the service bay
            AtMechanic = null; MechanicTown = null;
            foreach (var t in towns)
            {
                if (t.mechanic?.spot == null) continue;
                if (TestMapPlaces.Flat(t.mechanic.spot.Position, car.transform.position) < bayReachM) { AtMechanic = t.mechanic; MechanicTown = t; break; }
            }
            if (AtMechanic == null) Confirming = false;
            var kb = ChaseGame.InputBlocked ? null : Keyboard.current;
            if (AtMechanic != null && kb != null && !(game.Player && game.Player.OnFoot) && car.SpeedKph * 0.621f < bayMph && !ChaseSetup.morning)
            {
                if (kb.fKey.wasPressedThisFrame && !Confirming && Condition < 0.999f) Confirming = true;
                else if (kb.escapeKey.wasPressedThisFrame) Confirming = false;
            }
        }

        /// <summary>Pay the mechanic and leave with the truck as new (false: not at one, or short of credits).</summary>
        public bool Repair()
        {
            var s = State; if (s == null || AtMechanic == null) return false;
            var p = ChaseProfile.Current;
            int cost = RepairCost;
            if (p.credits < cost) { game.Say($"{AtMechanic.name}: {cost} cr for the repairs, you have {p.credits}"); return false; }
            p.credits -= cost; s.condition = 1f; p.repairs++; Confirming = false; saidLimp = false;
            p.Save();
            game.Say($"{AtMechanic.name}: repaired for {cost} cr");
            return true;
        }

        /// <summary>For the checks: set the condition.</summary>
        public void TestSetCondition(float c) { var s = State; if (s != null) s.condition = Mathf.Clamp01(c); }

        void OnGUI()
        {
            var world = game ? game.world : null;
            if (!world || world.hudHidden || ChaseGame.InputBlocked || AtMechanic == null || !game.InChase || ChaseSetup.morning) return;
            if (game.Player && game.Player.OnFoot) return;
            if (small == null)
            {
                small = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter }; small.normal.textColor = Color.white;
                bold = new GUIStyle(small) { fontStyle = FontStyle.Bold, fontSize = 17 };
                body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true }; body.normal.textColor = Color.white;
                button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 36 };
            }
            float W = Screen.width, H = Screen.height;
            var v = ChaseSetup.Vehicle;
            if (!Confirming)
            {
                var r = new Rect(W * 0.5f - 320, H - 112, 640, 30);
                GUI.Box(r, GUIContent.none);
                string line = Condition >= 0.999f ? $"{AtMechanic.name}: nothing to fix on the {v.name}"
                    : world.car.SpeedKph * 0.621f >= bayMph ? $"{AtMechanic.name}: stop in the bay for repairs"
                    : $"F  repairs at {AtMechanic.name}  ·  {v.name} at {Condition:P0}  ·  {RepairCost} cr to make it right";
                GUI.Label(new Rect(r.x, r.y + 3, r.width, 24), line, bold);
                return;
            }
            var p = ChaseProfile.Current;
            var box = new Rect(W * 0.5f - 260, H * 0.5f - 90, 520, 180);
            GUI.Box(box, GUIContent.none); GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 20, box.y + 14, box.width - 40, box.height - 28));
            GUILayout.Label(AtMechanic.name, bold);
            GUILayout.Label($"{v.name}: condition {Condition:P0}. A full service and repairs bring it back to 100 %.\nThe bill: {RepairCost} cr ({v.repairRate:0} cr a point at this shop's rate). You have {p.credits:n0} cr.", body);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.enabled = p.credits >= RepairCost;
            if (GUILayout.Button($"Repair for {RepairCost} cr", button, GUILayout.Width(220))) Repair();
            GUI.enabled = true;
            if (GUILayout.Button("Not now", button, GUILayout.Width(140))) Confirming = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
