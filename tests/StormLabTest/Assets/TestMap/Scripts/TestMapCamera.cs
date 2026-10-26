// The test map's camera: chase, hood, storm (from the car, turned toward the storm's tornado or updraft), overhead,
// and free flight. C cycles the car modes; F toggles free flight (WASD, Q/E down/up, right mouse look, scroll speed,
// the car stops taking input while flying). V in free flight puts the truck on the ground under the camera, facing the
// way it looks, and goes back to driving it.
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    public class TestMapCamera : MonoBehaviour
    {
        public enum Mode { Chase, Hood, Storm, Overhead, Free }

        public TestMapCar car;
        public StormRenderer storm;
        [Tooltip("the map (its storms now, the game's in game mode); found when empty")]
        public TestMapWorld world;
        public Mode mode = Mode.Chase;
        public float distance = 8f, height = 2.8f, lookHeight = 1.3f, sharpness = 6f, overheadHeight = 450f;
        public float flySpeed = 60f;

        Vector3 smoothedForward = Vector3.forward;
        Vector2 flyAngles;
        Mode lastCarMode = Mode.Chase;

        public string ModeName => mode.ToString();

        void LateUpdate()
        {
            if (!car) return;
            var kb = Keyboard.current;
            bool cycle = (kb != null && kb.cKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.rightShoulder.wasPressedThisFrame);
            if (kb != null && kb.fKey.wasPressedThisFrame) SetFree(mode != Mode.Free);
            if (kb != null && kb.vKey.wasPressedThisFrame && mode == Mode.Free) DropTruck();
            if (cycle && mode != Mode.Free) mode = (Mode)(((int)mode + 1) % 4);
            float k = 1f - Mathf.Exp(-sharpness * Time.deltaTime);
            var target = car.transform;
            var fwd = Vector3.ProjectOnPlane(target.forward, Vector3.up);
            if (fwd.sqrMagnitude > 0.001f) smoothedForward = Vector3.Slerp(smoothedForward, fwd.normalized, k);
            switch (mode)
            {
                case Mode.Chase:
                {
                    var want = target.position - smoothedForward * distance + Vector3.up * height;
                    if (Physics.Raycast(want + Vector3.up * 30f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore) && want.y < hit.point.y + 0.8f)
                        want.y = hit.point.y + 0.8f;
                    transform.position = Vector3.Lerp(transform.position, want, k);
                    transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * lookHeight - transform.position, Vector3.up);
                    break;
                }
                case Mode.Hood:
                    transform.position = target.TransformPoint(new Vector3(0f, 1.55f, 0.5f));
                    transform.rotation = Quaternion.LookRotation(target.forward, target.up);
                    break;
                case Mode.Storm:
                {
                    // a chaser standing by the car, looking at the nearest storm's base
                    transform.position = target.position + Vector3.up * 1.8f - target.right * 1.6f;
                    var look = NearestStorm(out bool any);
                    var dir = any ? look - transform.position : smoothedForward;
                    dir.y = any ? Mathf.Max(dir.y, 0f) * 0.6f + new Vector2(dir.x, dir.z).magnitude * 0.05f : 0.05f;
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), k);
                    break;
                }
                case Mode.Overhead:
                    transform.position = target.position + Vector3.up * overheadHeight;
                    transform.rotation = Quaternion.LookRotation(Vector3.down, smoothedForward);
                    break;
                case Mode.Free:
                    Fly(kb);
                    break;
            }
        }

        /// <summary>Free flight: put the truck on the ground under the camera (kept on the map), facing the way the camera
        /// looks, and go back to driving it from the camera mode used before flying.</summary>
        public void DropTruck()
        {
            if (!world) world = FindAnyObjectByType<TestMapWorld>();
            var p = transform.position;
            if (world) { float h = world.sizeM / 2 - 60f; p.x = Mathf.Clamp(p.x, -h, h); p.z = Mathf.Clamp(p.z, -h, h); }
            // straight down from well above the camera, so it finds the ground under it from any height
            if (!Physics.Raycast(new Vector3(p.x, p.y + 20000f, p.z), Vector3.down, out var hit, 40000f, ~0, QueryTriggerInteraction.Ignore)) return;
            var heading = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (heading.sqrMagnitude < 1e-4f) heading = Vector3.ProjectOnPlane(transform.up, Vector3.up);   // looking straight down
            car.Teleport(hit.point, Quaternion.LookRotation(heading.normalized, Vector3.up));
            smoothedForward = heading.normalized;
            SetFree(false);
        }

        void SetFree(bool on)
        {
            if (on) { lastCarMode = mode; mode = Mode.Free; var e = transform.eulerAngles; flyAngles = new Vector2(e.x > 180 ? e.x - 360 : e.x, e.y); }
            else mode = lastCarMode;
            car.acceptInput = !on;
        }

        void Fly(Keyboard kb)
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed) flyAngles += new Vector2(-mouse.delta.y.ReadValue(), mouse.delta.x.ReadValue()) * 0.15f;
                float sc = mouse.scroll.y.ReadValue();
                if (sc != 0) flySpeed = Mathf.Clamp(flySpeed * (sc > 0 ? 1.25f : 0.8f), 2f, 5000f);
            }
            flyAngles.x = Mathf.Clamp(flyAngles.x, -89f, 89f);
            transform.rotation = Quaternion.Euler(flyAngles.x, flyAngles.y, 0);
            if (kb == null) return;
            var v = Vector3.zero;
            if (kb.wKey.isPressed) v += transform.forward;
            if (kb.sKey.isPressed) v -= transform.forward;
            if (kb.dKey.isPressed) v += transform.right;
            if (kb.aKey.isPressed) v -= transform.right;
            if (kb.eKey.isPressed) v += Vector3.up;
            if (kb.qKey.isPressed) v -= Vector3.up;
            float boost = kb.leftShiftKey.isPressed ? 4f : 1f;
            transform.position += v * flySpeed * boost * Time.deltaTime;
        }

        Vector3 NearestStorm(out bool any)
        {
            any = false;
            if (!world) world = FindAnyObjectByType<TestMapWorld>();
            var best = Vector3.zero; float bestD = float.MaxValue;
            // the map's storms now (the game's two in game mode), else the camera's own storm
            var live = world ? world.LiveStorms() : storm && storm.isActiveAndEnabled && storm.Plan != null ? new[] { storm } : new StormRenderer[0];
            foreach (var s in live)
            {
                var at = s.TornadoAmount > 0.15f ? s.TornadoGroundWorld + Vector3.up * 400f : s.UpdraftWorld(s.StormTime);
                float d = Vector3.ProjectOnPlane(at - car.transform.position, Vector3.up).sqrMagnitude;
                if (d < bestD) { bestD = d; best = at; any = true; }
            }
            return best;
        }
    }
}
