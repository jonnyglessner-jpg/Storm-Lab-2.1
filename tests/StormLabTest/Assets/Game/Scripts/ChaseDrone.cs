// The camera drone (shop, 2026-11-02): a quadcopter docked on the truck's roof. B launches it and flies it in first
// person (WASD along the view, Q/E down and up, Shift fast, the mouse looks; X takes photos from it), B again brings it
// home on its own. It flies within a range of the controller (the truck, or the chaser on foot) and on a battery:
// past the range it stops at the fence; at a tenth of the battery it comes home by itself, and a flat battery lands it
// wherever the truck is. It charges on the roof. The tornado's wind pushes it (a light airframe: it drifts with a third
// of the wind, and past its top speed it is carried). The range and the battery grow with the shop's upgrades
// (ChaseCatalog.DroneRangeM / DroneMinutes).
//
// The follow cam (drone_follow) is the same drone flying itself behind the truck: the test map's chase camera mode
// (TestMapCamera.thirdPerson), on a quarter of the flight drain; when the battery is flat it lands to charge and the
// view goes back to the cab. Without the upgrade a chase has the cab and the truck's own roof camera (locked on the
// tornado) only.
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    public class ChaseDrone : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("where it sits on the roof, in the truck's frame (the cab roof is about 2 m up; the mesonet is on the right)")]
        public Vector3 dock = new Vector3(-0.45f, 2.06f, -0.35f);
        [Tooltip("flight speed, m/s: level, with Shift, and climbing")]
        public float speed = 12f, fastSpeed = 22f, climbSpeed = 6f;
        public float acceleration = 14f;
        [Tooltip("degrees per mouse count")]
        public float lookSensitivity = 0.12f;
        [Tooltip("the ceiling above the ground, m (the 400 ft rule)")]
        public float ceilingM = 120f;
        [Tooltip("the follow cam's drain as a share of flight, and charging on the roof: flat to full takes this many flight times")]
        public float followDrain = 0.25f, chargeTime = 1.5f;
        [Tooltip("it comes home by itself at this much battery")]
        public float reserve = 0.1f;
        [Tooltip("a check's stick: level move (x right, z forward, in the view's frame) and climb (y), added to the keys")]
        public Vector3 commandMove;

        public bool Owned => ChaseProfile.Current.Owns("drone");
        public bool FollowOwned => ChaseProfile.Current.Owns("drone_follow");
        public bool Flying { get; private set; }
        /// <summary>flying home on its own (B, the reserve, or a lost link)</summary>
        public bool Returning { get; private set; }
        /// <summary>0..1</summary>
        public float Battery { get; private set; } = 1f;
        public float RangeM => ChaseCatalog.DroneRangeM;
        public float FlightSeconds => ChaseCatalog.DroneMinutes * 60f;
        public Vector3 Position => pos;
        public Vector3 Velocity => vel;
        /// <summary>level distance from the controller, m</summary>
        public float DistanceM => Flying ? Vector3.ProjectOnPlane(pos - Controller, Vector3.up).magnitude : 0f;
        public float AltitudeM => Flying ? pos.y - GroundY(pos) : 0f;
        /// <summary>the follow cam is flying (the chase or roof view in a chase)</summary>
        public bool Following { get; private set; }
        public string Status { get; private set; } = "";

        Vector3 pos, vel, drift; Vector2 look;
        Camera cam; Transform model;
        float statusUntil;
        GUIStyle small, bold, tiny;

        TestMapWorld World => game ? game.world : null;
        /// <summary>the controller: the chaser on foot, else the truck</summary>
        Vector3 Controller => game && game.Player ? game.Player.Position : World && World.car ? World.car.transform.position : pos;

        void Start() { cam = Camera.main; }

        /// <summary>A new chase: docked and charged.</summary>
        public void ResetForChase()
        {
            if (Flying) Land(false);
            Battery = 1f; Returning = false; Following = false;
            BuildModel();
        }

        /// <summary>Bought in a chase: the drone appears on the roof, charged.</summary>
        public void SetOwnedNow() { Battery = 1f; BuildModel(); }

        /// <summary>For the checks (no keys in batch, and flying somewhere takes real seconds): the drone here, looking at
        /// a point; the next step still holds it to the fence and the ground.</summary>
        public void TestPlace(Vector3 at, Vector3 lookAt)
        {
            if (!Flying) return;
            pos = at; vel = Vector3.zero; drift = Vector3.zero;
            var d = lookAt - at;
            var level = Vector3.ProjectOnPlane(d, Vector3.up);
            look = new Vector2(level.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(level.normalized, Vector3.up).eulerAngles.y : look.x,
                               Mathf.Atan2(d.y, level.magnitude) * Mathf.Rad2Deg);
        }

        public void TestBattery(float fraction) { Battery = Mathf.Clamp01(fraction); }

        /// <summary>the flight's state for a check's notes</summary>
        public string DebugState
        {
            get
            {
                var world = World;
                var home = world && world.car ? world.car.transform.TransformPoint(dock) + Vector3.up * 0.3f : Vector3.zero;
                return $"to home {(home - pos).magnitude:0.0} m (level {Vector3.ProjectOnPlane(home - pos, Vector3.up).magnitude:0.0}, up {pos.y - home.y:0.0}), vel {vel.magnitude:0.0} m/s, drift {drift.magnitude:0.0} m/s, ground {GroundY(pos):0.0} vs pos.y {pos.y:0.0}, returning {Returning}, battery {Battery:P0}";
            }
        }

        void Update()
        {
            var world = World; if (!world || !world.car) return;
            if (!cam) cam = Camera.main;
            var rig = world.cameraRig;
            bool chase = game.InChase;
            // the third-person views are the follow cam's
            if (rig) rig.thirdPerson = !chase || (FollowOwned && Battery > 0.02f);
            Following = chase && !Flying && rig && !rig.held && rig.mode == TestMapCamera.Mode.Chase;   // the roof camera is the truck's own
            if (!Owned) { if (model) model.gameObject.SetActive(false); Flying = false; return; }
            if (!model) BuildModel();
            model.gameObject.SetActive(!Flying && !Following);

            var kb = ChaseGame.InputBlocked ? null : Keyboard.current;
            bool photo = game.Photo && game.Photo.Active;
            if (kb != null && kb.bKey.wasPressedThisFrame && !photo)
            {
                if (!Flying) Launch();
                else if (!Returning) { Returning = true; Say("Coming home"); }
            }

            float dt = Time.deltaTime;
            if (Flying)
            {
                Battery = Mathf.Max(0f, Battery - dt / Mathf.Max(FlightSeconds, 1f));
                if (!Returning && Battery <= reserve) { Returning = true; Say("Battery low: coming home"); }
                if (Battery <= 0f) { Say("Battery flat: the drone came down and is back on the roof"); Land(true); return; }
                if (Returning) FlyHome(dt); else Fly(kb, photo, dt);
            }
            else if (Following)
            {
                Battery = Mathf.Max(0f, Battery - followDrain * dt / Mathf.Max(FlightSeconds, 1f));
                if (Battery <= 0.02f && rig) { rig.mode = TestMapCamera.Mode.Cab; Say("Drone battery flat: it is charging on the roof"); }
            }
            else Battery = Mathf.Min(1f, Battery + dt / Mathf.Max(FlightSeconds * chargeTime, 1f));

            if (model && model.gameObject.activeSelf) { model.localPosition = dock; model.localRotation = Quaternion.identity; }
        }

        void LateUpdate()
        {
            if (!Flying || !cam) return;
            cam.transform.position = pos;
            // the gimbal: level, with a little of the airframe's lean into its motion
            var lean = Quaternion.Euler(Mathf.Clamp(Vector3.Dot(vel, cam.transform.forward) * 0.25f, -6f, 6f), 0f, Mathf.Clamp(-Vector3.Dot(vel, cam.transform.right) * 0.25f, -6f, 6f));
            cam.transform.rotation = Quaternion.Euler(-look.y, look.x, 0f) * lean;
        }

        public void Launch()
        {
            var world = World; if (!world || !world.car || Flying || !Owned || Battery < 0.05f) { if (!Flying && Owned && Battery < 0.05f) Say("Drone battery flat"); return; }
            pos = world.car.transform.TransformPoint(dock) + Vector3.up * 0.3f;
            vel = Vector3.up * 2f; drift = Vector3.zero;
            // looking the way the controller looks
            var fwd = cam ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) : world.car.transform.forward;
            look = new Vector2(fwd.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y : world.car.transform.eulerAngles.y, -5f);
            Flying = true; Returning = false;
            world.car.acceptInput = false;
            if (world.cameraRig) world.cameraRig.held = true;
            Say("Drone up");
        }

        /// <summary>Back on the roof. fell: it came down flat (it is simply put back).</summary>
        public void Land(bool fell)
        {
            var world = World;
            Flying = false; Returning = false; vel = Vector3.zero;
            if (fell) Battery = 0f;
            if (world)
            {
                bool onFoot = game.Player && game.Player.OnFoot;
                if (world.cameraRig) world.cameraRig.held = onFoot;
                if (world.car) world.car.acceptInput = !onFoot && !(game.Photo && game.Photo.Active);
            }
            if (!fell) Say("Drone home");
        }

        void Fly(Keyboard kb, bool photo, float dt)
        {
            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                var d = mouse.delta.ReadValue() * lookSensitivity;
                look.x = Mathf.Repeat(look.x + d.x, 360f);
                look.y = Mathf.Clamp(look.y + d.y, -89f, 60f);
            }
            var heading = Quaternion.Euler(0f, look.x, 0f);
            Vector3 fwd = heading * Vector3.forward, right = heading * Vector3.right;
            var move = Vector3.zero; float climb = 0f; bool fast = false;
            if (kb != null && !photo)   // behind the photo camera the drone hovers (its keys are the camera's)
            {
                if (kb.wKey.isPressed) move += fwd;
                if (kb.sKey.isPressed) move -= fwd;
                if (kb.dKey.isPressed) move += right;
                if (kb.aKey.isPressed) move -= right;
                if (kb.eKey.isPressed) climb += 1f;
                if (kb.qKey.isPressed) climb -= 1f;
                fast = kb.leftShiftKey.isPressed;
            }
            var gp = Gamepad.current;
            if (gp != null && !photo) { var st = gp.leftStick.ReadValue(); if (st.sqrMagnitude > 0.02f) move += fwd * st.y + right * st.x; climb += gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue(); }
            move += fwd * commandMove.z + right * commandMove.x; climb += commandMove.y;
            if (move.sqrMagnitude > 1f) move.Normalize();
            var want = move * (fast ? fastSpeed : speed) + Vector3.up * Mathf.Clamp(climb, -1f, 1f) * climbSpeed;
            vel = Vector3.MoveTowards(vel, want, acceleration * dt);
            Step(vel, dt);
        }

        void FlyHome(float dt)
        {
            var world = World;
            var home = world.car.transform.TransformPoint(dock) + Vector3.up * 0.3f;
            var to = home - pos; var level = Vector3.ProjectOnPlane(to, Vector3.up);
            // over to the truck a few metres up, then down onto the roof; the autopilot holds its track against the wind's
            // drift (an approach that only slowed with the distance hovered short of the dock in a breeze until the battery
            // was flat), as far as its top speed allows
            Vector3 desired;
            if (level.magnitude > 3f) { float up = Mathf.Max(0f, home.y + 6f - pos.y); desired = level.normalized * fastSpeed + Vector3.up * Mathf.Clamp(up, -climbSpeed, climbSpeed); }
            else desired = to.normalized * Mathf.Clamp(to.magnitude * 3f, 1.5f, climbSpeed);
            var want = Vector3.ClampMagnitude(desired - drift, fastSpeed);
            vel = Vector3.MoveTowards(vel, want, acceleration * dt);
            Step(vel, dt, true);
            if (level.sqrMagnitude > 1e-3f) { var lookTo = Quaternion.LookRotation(level.normalized, Vector3.up).eulerAngles.y; look.x = Mathf.MoveTowardsAngle(look.x, lookTo, 180f * dt); }
            look.y = Mathf.MoveTowards(look.y, -20f, 60f * dt);
            if (to.magnitude < 0.8f) Land(false);
        }

        /// <summary>A step of flight: the wind, the ground, the ceiling and the range fence.</summary>
        void Step(Vector3 v, float dt, bool home = false)
        {
            var world = World;
            var wind = world.gameMode && world.game ? world.game.TornadoWindAt(pos) : world.storm && world.storm.isActiveAndEnabled ? world.storm.TornadoWindAt(pos) : Vector3.zero;
            // a light airframe: a third of the wind as drift, and all of what is over its top speed
            float over = Mathf.Max(0f, wind.magnitude - fastSpeed);
            var wantDrift = wind * 0.33f + (over > 0f ? wind.normalized * over : Vector3.zero);
            drift = Vector3.MoveTowards(drift, wantDrift, 20f * dt);
            pos += (v + drift) * dt;
            float ground = GroundY(pos);
            pos.y = Mathf.Clamp(pos.y, ground + (home ? 0.2f : 1.0f), ground + ceilingM);
            if (!home)
            {
                var c = Controller; var level = Vector3.ProjectOnPlane(pos - c, Vector3.up);
                float range = RangeM;
                if (level.magnitude > range)
                {
                    pos = new Vector3(c.x, pos.y, c.z) + level.normalized * range;
                    vel = Vector3.ProjectOnPlane(vel, level.normalized);   // the fence takes the outward speed
                    Say($"At the range limit ({range:0} m)");
                }
            }
        }

        float GroundY(Vector3 at)
        {
            var world = World;
            if (world && world.car && world.car.GroundUnder(new Vector3(at.x, at.y + 300f, at.z), 2000f, out var hit)) return hit.point.y;
            return at.y - 1f;
        }

        /// <summary>A toast, not repeated while the same one is up (the range fence says it every frame).</summary>
        void Say(string s)
        {
            if (s == Status && Time.unscaledTime < statusUntil) return;
            Status = s; statusUntil = Time.unscaledTime + 3f;
            if (game) game.Say(s);
        }

        /// <summary>The quadcopter on the roof: a body, four arms and four rotor discs, in the truck's dark grey.</summary>
        void BuildModel()
        {
            var world = World; if (!world || !world.car) return;
            if (model) return;
            var car = world.car.transform;
            model = new GameObject("Drone").transform;
            model.SetParent(car, false); model.localPosition = dock;
            var dark = new Material(Shader.Find("HDRP/Lit")) { color = new Color(0.12f, 0.12f, 0.13f) };
            var light = new Material(Shader.Find("HDRP/Lit")) { color = new Color(0.75f, 0.75f, 0.72f) };
            Transform Part(PrimitiveType t, Vector3 at, Vector3 size, Material m, Quaternion? rot = null)
            {
                var g = GameObject.CreatePrimitive(t);
                Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(model, false); g.transform.localPosition = at; g.transform.localScale = size; g.transform.localRotation = rot ?? Quaternion.identity;
                g.GetComponent<Renderer>().sharedMaterial = m;
                return g.transform;
            }
            Part(PrimitiveType.Cube, new Vector3(0f, 0.05f, 0f), new Vector3(0.18f, 0.07f, 0.24f), light);
            Part(PrimitiveType.Cube, new Vector3(0f, 0.01f, 0f), new Vector3(0.10f, 0.03f, 0.10f), dark);   // the gimbal and camera
            var grey = new Material(Shader.Find("HDRP/Lit")) { color = new Color(0.3f, 0.3f, 0.3f) };
            foreach (var s in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
            {
                var end = new Vector3(s.x * 0.22f, 0.06f, s.y * 0.2f);
                Part(PrimitiveType.Cube, end * 0.5f, new Vector3(0.3f, 0.02f, 0.025f), dark, Quaternion.Euler(0f, -Mathf.Atan2(end.z, end.x) * Mathf.Rad2Deg, 0f));
                Part(PrimitiveType.Cylinder, end + Vector3.up * 0.02f, new Vector3(0.22f, 0.003f, 0.22f), grey);
            }
        }

        void OnGUI()
        {
            var world = World;
            if (!Owned || !world || world.hudHidden || ChaseGame.InputBlocked) return;
            float W = Screen.width, H = Screen.height;
            if (small == null)
            {
                small = new GUIStyle(GUI.skin.label) { fontSize = 14 }; small.normal.textColor = Color.white;
                bold = new GUIStyle(small) { fontStyle = FontStyle.Bold, fontSize = 15 };
                tiny = new GUIStyle(small) { fontSize = 12 };
            }
            if (Flying)
            {
                // the drone's panel, top right under the photo camera's frame: battery, link, height, distance
                var r = new Rect(W - 16 - 300, 80, 300, 118);
                GUI.Box(r, GUIContent.none);
                GUI.Label(new Rect(r.x + 10, r.y + 6, 280, 22), Returning ? "DRONE   coming home" : "DRONE", bold);
                Bar(new Rect(r.x + 10, r.y + 32, 280, 12), Battery, Battery > 0.25f ? new Color(0.35f, 0.9f, 0.4f) : new Color(1f, 0.4f, 0.3f));
                GUI.Label(new Rect(r.x + 10, r.y + 44, 280, 20), $"battery {Battery:P0}  ·  {Battery * FlightSeconds / 60f:0.0} min", small);
                float link = Mathf.Clamp01(DistanceM / Mathf.Max(RangeM, 1f));
                Bar(new Rect(r.x + 10, r.y + 66, 280, 12), 1f - link, link < 0.85f ? new Color(0.4f, 0.7f, 1f) : new Color(1f, 0.6f, 0.3f));
                GUI.Label(new Rect(r.x + 10, r.y + 78, 280, 20), $"link  {DistanceM:0} / {RangeM:0} m  ·  {AltitudeM:0} m up  ·  {(vel + drift).magnitude * 2.237f:0} mph", small);
                GUI.Label(new Rect(r.x + 10, r.y + 96, 280, 20), game.Photo && game.Photo.Active ? "X / Esc  close the camera    B  come home" : "WASD  fly    Q / E  down / up    Shift  fast    X  photo    B  come home", tiny);
            }
            else if (!(game.Photo && game.Photo.Active))
            {
                // docked: a line in the corner above the map (the map's corner size scales with the screen)
                float k = H / 1080f;
                var r = new Rect(W - 16 - 300, H - (16f + 340f) * k - 30, 300, 24);
                string s = Following ? $"Drone following  ·  battery {Battery:P0}" : Battery < 1f ? $"Drone charging  ·  {Battery:P0}" : "Drone on the roof  ·  B launches";
                GUI.Box(r, GUIContent.none); GUI.Label(new Rect(r.x + 8, r.y + 2, 290, 22), s, small);
            }
        }

        static void Bar(Rect r, float f, Color c)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c; GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(f), r.height - 2), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
