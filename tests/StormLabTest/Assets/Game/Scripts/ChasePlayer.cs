// The chaser on foot: E gets out of the truck (when it is nearly stopped) onto the driver's side and walks in first
// person (WASD, Shift runs, the mouse looks; the cursor is locked while walking, in the cab and behind the camera, and
// freed by holding Alt or opening a menu). E within reach of the truck gets back in. A tornado's wind pushes the walker
// as it pushes the truck (drag on a person-sized body): past about 70 mph there is no walking against it.
// While on foot the camera rig (TestMapCamera) is held and this script places the camera at the walker's eyes.
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.Game
{
    public class ChasePlayer : MonoBehaviour
    {
        public ChaseGame game;
        public float walkSpeed = 1.8f, runSpeed = 4.6f, eyeHeight = 1.65f;
        [Tooltip("walking and running speed as a share (the chaser's sanity; ChaseSanity sets it)")]
        public float speedScale = 1f;
        [Tooltip("degrees per mouse count")]
        public float lookSensitivity = 0.12f;
        [Tooltip("how close to the truck E gets back in, m, and how slow it must be going to get out, mph")]
        public float reachM = 3.5f, getOutMph = 4f;
        public bool OnFoot { get; private set; }
        /// <summary>the walker's eye, where the camera is on foot</summary>
        public Vector3 Eye => walker ? walker.transform.position + Vector3.up * (eyeHeight - cc.height * 0.5f) : Vector3.zero;
        public Vector2 Look => look;   // yaw (compass), pitch (+ up), degrees
        public bool NearTruck { get; private set; }

        GameObject walker; CharacterController cc;
        Vector2 look; float vy; Vector3 pushVel;
        Camera cam;

        TestMapWorld World => game ? game.world : null;

        void Start()
        {
            cam = Camera.main;
        }

        void Update()
        {
            var world = World; if (!world || !world.car) return;
            var kb = ChaseGame.InputBlocked ? null : Keyboard.current;
            var rig = world.cameraRig;
            bool free = rig && rig.mode == TestMapCamera.Mode.Free;
            bool photo = game.Photo && game.Photo.Active;
            bool drone = game.Drone && game.Drone.Flying;   // the keys are the drone's, the view is its camera
            if (OnFoot)
            {
                var toTruck = world.car.transform.position - walker.transform.position; toTruck.y = 0f;
                NearTruck = toTruck.magnitude < reachM + 1.5f;
                if (kb != null && kb.eKey.wasPressedThisFrame && NearTruck && !photo && !drone) { GetIn(); return; }
                Walk(kb, photo || drone);
            }
            else
            {
                NearTruck = false;
                if (kb != null && kb.eKey.wasPressedThisFrame && !free && !photo && !drone && world.car.SpeedKph * 0.621f < getOutMph) GetOut();
            }
        }

        void LateUpdate()
        {
            var world = World; if (!world || !cam) return;
            if (OnFoot && walker && !(game.Drone && game.Drone.Flying))
            {
                cam.transform.position = Eye;
                cam.transform.rotation = Quaternion.Euler(-look.y, look.x, 0f);
            }
            UpdateCursor();
        }

        /// <summary>The cursor: locked while walking, in the cab or behind the camera, and nowhere a menu or the map wants it.</summary>
        void UpdateCursor()
        {
            var world = World;
            var rig = world ? world.cameraRig : null;
            var kb = Keyboard.current;
            bool menu = ChaseGame.InputBlocked;
            bool map = world && world.GetComponent<TestMapMinimap>() is { } mm && mm.expanded;
            bool alt = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
            bool photo = game && game.Photo && game.Photo.Active;
            bool drone = game && game.Drone && game.Drone.Flying;
            bool wantLock = !menu && !map && !alt && (OnFoot || photo || drone || (rig && rig.mode == TestMapCamera.Mode.Cab));
            var want = wantLock ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != want) { Cursor.lockState = want; Cursor.visible = !wantLock; }
        }

        void Walk(Keyboard kb, bool photo)
        {
            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                var d = mouse.delta.ReadValue() * lookSensitivity;
                look.x = Mathf.Repeat(look.x + d.x, 360f);
                look.y = Mathf.Clamp(look.y + d.y, -85f, 85f);
            }
            var heading = Quaternion.Euler(0f, look.x, 0f);
            Vector3 fwd = heading * Vector3.forward, right = heading * Vector3.right;
            var move = Vector3.zero;
            if (kb != null && !photo)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move += fwd;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move -= fwd;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += right;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move -= right;
            }
            var gp = Gamepad.current;
            if (gp != null) { var st = gp.leftStick.ReadValue(); if (st.sqrMagnitude > 0.02f) move += fwd * st.y + right * st.x; }
            if (move.sqrMagnitude > 1f) move.Normalize();
            float speed = (kb != null && kb.leftShiftKey.isPressed ? runSpeed : walkSpeed) * Mathf.Clamp01(speedScale);
            // the tornado's wind on a person (drag on about 0.6 m^2, 80 kg): past 30 m/s it carries the walker along
            var wind = World.gameMode && World.game ? World.game.TornadoWindAt(walker.transform.position) : World.storm && World.storm.isActiveAndEnabled ? World.storm.TornadoWindAt(walker.transform.position) : Vector3.zero;
            var rel = wind - pushVel;
            pushVel += 0.5f * 1.1f * 0.6f / 80f * rel.magnitude * rel * Time.deltaTime;
            pushVel = Vector3.MoveTowards(pushVel, Vector3.zero, 6f * Time.deltaTime);   // feet on the ground fight it
            float lean = Mathf.Clamp01(1f - wind.magnitude / 32f);   // headway against the wind
            vy = cc.isGrounded ? -1f : vy - 9.81f * Time.deltaTime;
            cc.Move((move * speed * lean + pushVel) * Time.deltaTime + Vector3.up * vy * Time.deltaTime);
        }

        /// <summary>Out of the truck onto the driver's side, looking the way it points.</summary>
        public void GetOut()
        {
            var world = World; if (!world || !world.car) return;
            if (!walker)
            {
                walker = new GameObject("Chaser");
                cc = walker.AddComponent<CharacterController>();
                cc.height = 1.8f; cc.radius = 0.3f; cc.center = Vector3.zero; cc.stepOffset = 0.4f; cc.slopeLimit = 50f;
                cc.minMoveDistance = 0f;   // a step at a very high frame rate is under the default millimetre
            }
            var car = world.car.transform;
            var at = car.TransformPoint(new Vector3(-1.6f, 0f, 0.6f));
            if (world.car.GroundUnder(at + Vector3.up * 10f, 40f, out var hit)) at = hit.point;
            // a CharacterController keeps its own position: it must be off while its transform is moved, or Move snaps it back
            cc.enabled = false;
            walker.transform.position = at + Vector3.up * (cc.height * 0.5f + 0.05f);
            walker.SetActive(true);
            cc.enabled = true;
            Physics.SyncTransforms();
            look = new Vector2(car.eulerAngles.y, 0f); vy = 0f; pushVel = Vector3.zero;
            world.car.acceptInput = false;
            // the truck stays put (the wind still pushes it)
            world.car.Step(0f, 0f, 1f, true, Time.fixedDeltaTime);
            if (world.cameraRig) world.cameraRig.held = true;
            OnFoot = true;
        }

        public void GetIn()
        {
            var world = World; if (!world) return;
            if (walker) walker.SetActive(false);
            OnFoot = false;
            bool drone = game.Drone && game.Drone.Flying;
            if (world.cameraRig) { world.cameraRig.held = drone; }
            if (world.car) world.car.acceptInput = !(game.Photo && game.Photo.Active) && !drone;
        }

        /// <summary>The ground point a mesonet or camera reads from: the walker on foot, else the truck.</summary>
        public Vector3 Position => OnFoot && walker ? walker.transform.position : World && World.car ? World.car.transform.position : Vector3.zero;
    }
}
