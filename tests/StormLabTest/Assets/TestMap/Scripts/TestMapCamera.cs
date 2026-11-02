// The test map's camera: chase (behind the truck; hold the right mouse button to look round it, and 0.5 s after the
// button is let go it eases back behind the truck), roof (a camera mounted on the cab roof, locked on the nearest
// tornado, or updraft with none down) and free flight. C switches chase and roof (user, 2026-10-26: the hood and overhead views
// are gone); F toggles free flight (WASD, Q/E down/up, right mouse look, scroll speed, the car stops taking input while
// flying). V in free flight puts the truck on the ground under the camera, facing the way it looks, and goes back to
// driving it.
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    public class TestMapCamera : MonoBehaviour
    {
        // Cab (2026-11-02, the chase game): first person from the driver's seat, the mouse looks round without a button
        public enum Mode { Chase, Storm, Free, Cab }

        public TestMapCar car;
        public StormRenderer storm;
        [Tooltip("the map (its storms now, the game's in game mode); found when empty")]
        public TestMapWorld world;
        public Mode mode = Mode.Chase;
        public float distance = 8f, height = 2.8f, lookHeight = 1.3f, sharpness = 6f;
        public float flySpeed = 60f;
        [Tooltip("chase free look: degrees per mouse count, and per second at full right-stick deflection")]
        public float lookSensitivity = 0.15f, stickLookSpeed = 120f;
        [Tooltip("chase free look: seconds after the right mouse button is let go (or the stick centred) before it eases back behind the truck, and how fast it eases")]
        public float recentreDelay = 0.5f, recentreRate = 4f;
        [Tooltip("the roof camera, in the truck's frame: above the cab roof (its top is about 2 m up)")]
        public Vector3 roofMount = new Vector3(0f, 2.3f, 0.35f);
        [Tooltip("the driver's eye, in the truck's frame (the cab's floor is at about 1.1 m, its roof at 2 m; the eye sits a near-clip length under the headliner, so looking up does not cut it)")]
        public Vector3 cabSeat = new Vector3(-0.4f, 1.64f, 0.55f);
        [Tooltip("another script drives the camera (walking about on foot, the photo camera): the modes do nothing")]
        public bool held;
        [Tooltip("the chase view behind the truck is allowed; off, C cycles roof / cab only (the chase game, 2026-11-02: that view is the drone's follow cam, until it is owned)")]
        public bool thirdPerson = true;

        Vector3 smoothedForward = Vector3.forward;
        Vector2 flyAngles;
        // chase free look: yaw round the truck and pitch above the usual view, degrees; when it was last held
        Vector2 look; float lastLook = -1e9f;
        Mode lastCarMode = Mode.Chase;

        public string ModeName => mode == Mode.Storm ? "Roof" : mode.ToString();
        // the truck's glass panels (the builder's dark opaque boxes), hidden from the driver's seat: seen from behind they
        // are a black wall across the view (found by the chase game's photo check, 2026-11-02); and a headliner under the
        // roof, shown only from the seat
        Renderer[] glass; bool glassHidden; GameObject headliner;
        /// <summary>where the cab camera looks, yaw (+ right) and pitch (+ up) from the truck's heading, degrees</summary>
        public Vector2 CabLook => look;

        void LateUpdate()
        {
            if (!car) return;
            HideGlass(!held && mode == Mode.Cab);
            if (held) return;
            var kb = Game.ChaseGame.InputBlocked ? null : Keyboard.current;
            bool cycle = (kb != null && kb.cKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.rightShoulder.wasPressedThisFrame);
            if (kb != null && kb.fKey.wasPressedThisFrame && (world == null || world.sandboxKeys)) SetFree(mode != Mode.Free);
            if (kb != null && kb.vKey.wasPressedThisFrame && mode == Mode.Free) DropTruck();
            // C: chase / roof / cab; without the third-person view (the drone's follow cam) roof / cab, since the roof
            // camera is the truck's own, locked on the tornado (user, 2026-11-02: bring the snap camera back)
            if (cycle && mode != Mode.Free)
            {
                mode = mode == Mode.Chase ? Mode.Storm : mode == Mode.Storm ? Mode.Cab : thirdPerson ? Mode.Chase : Mode.Storm;
                look = Vector2.zero;
            }
            if (!thirdPerson && mode == Mode.Chase) { mode = Mode.Cab; look = Vector2.zero; }
            float k = 1f - Mathf.Exp(-sharpness * Time.deltaTime);
            var target = car.transform;
            var fwd = Vector3.ProjectOnPlane(target.forward, Vector3.up);
            if (fwd.sqrMagnitude > 0.001f) smoothedForward = Vector3.Slerp(smoothedForward, fwd.normalized, k);
            switch (mode)
            {
                case Mode.Chase:
                {
                    ChaseLook();
                    // the usual view, swung round the truck by the look's yaw and raised or lowered by its pitch
                    float R = Mathf.Sqrt(distance * distance + height * height);
                    float el = Mathf.Clamp(Mathf.Atan2(height, distance) * Mathf.Rad2Deg + look.y, -5f, 75f) * Mathf.Deg2Rad;
                    var back = Quaternion.AngleAxis(look.x, Vector3.up) * -smoothedForward;
                    var want = target.position + back * (Mathf.Cos(el) * R) + Vector3.up * (Mathf.Sin(el) * R);
                    if (Physics.Raycast(want + Vector3.up * 30f, Vector3.down, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore) && want.y < hit.point.y + 0.8f)
                        want.y = hit.point.y + 0.8f;
                    transform.position = Vector3.Lerp(transform.position, want, k);
                    transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * lookHeight - transform.position, Vector3.up);
                    break;
                }
                case Mode.Storm:
                {
                    // a camera mounted on the cab roof, locked on the nearest tornado (or, with none on the ground, the
                    // nearest updraft): it snaps to it every frame, however the truck turns (user, 2026-10-26: it used to
                    // ease round toward it)
                    transform.position = target.TransformPoint(roofMount);
                    var aim = NearestStorm(out bool any);
                    var dir = any ? aim - transform.position : smoothedForward;
                    dir.y = any ? Mathf.Max(dir.y, 0f) * 0.6f + new Vector2(dir.x, dir.z).magnitude * 0.05f : 0.05f;
                    transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    break;
                }
                case Mode.Cab:
                {
                    // the driver's seat: the mouse looks round (no button; the cursor is locked by ChasePlayer), as far as
                    // a driver can turn, and the view turns with the truck
                    CabLookInput();
                    transform.position = target.TransformPoint(cabSeat);
                    var heading = Quaternion.LookRotation(smoothedForward.sqrMagnitude > 0.001f ? smoothedForward : Vector3.forward, Vector3.up);
                    // the cab's pitch and roll too, damped (a truck on a gravel road shakes the view otherwise)
                    var cab = Quaternion.Slerp(heading, target.rotation, 0.5f);
                    transform.rotation = cab * Quaternion.Euler(-look.y, look.x, 0f);
                    break;
                }
                case Mode.Free:
                    Fly(kb);
                    break;
            }
        }

        void HideGlass(bool hide)
        {
            if (hide == glassHidden) return;
            var body = car.transform.Find("Body");
            if (glass == null)
            {
                var list = new System.Collections.Generic.List<Renderer>();
                if (body) foreach (var n in new[] { "Windscreen", "Side windows" }) { var t = body.Find(n); if (t && t.TryGetComponent<Renderer>(out var r)) list.Add(r); }
                glass = list.ToArray();
            }
            foreach (var r in glass) if (r) r.enabled = !hide;
            // the headliner: the cab is a box seen from inside, so its roof is culled and whatever sits on it (the light
            // bar, the mesonet, the drone) showed at the top of the frame (user, 2026-11-02); a dark ceiling just under the
            // roof, drawn only from the seat (from outside it is inside the cab box)
            if (!headliner && body)
            {
                headliner = GameObject.CreatePrimitive(PrimitiveType.Cube);
                headliner.name = "Headliner";
                Destroy(headliner.GetComponent<Collider>());
                headliner.transform.SetParent(body, false);
                headliner.transform.localPosition = new Vector3(0f, 1.955f, 0.35f);   // the cab box: centre 1.55 m, 0.85 m tall, 2.5 m long
                headliner.transform.localScale = new Vector3(1.88f, 0.02f, 2.48f);
                var mr = headliner.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var sh = Shader.Find("HDRP/Lit");
                if (sh) mr.sharedMaterial = new Material(sh) { name = "headliner", color = new Color(0.16f, 0.16f, 0.17f), hideFlags = HideFlags.DontSave };
            }
            if (headliner) headliner.SetActive(hide);
            glassHidden = hide;
        }

        void OnDisable() { HideGlass(false); }

        void CabLookInput()
        {
            var mouse = Mouse.current; var pad = Gamepad.current;
            var d = Vector2.zero;
            if (mouse != null && (Cursor.lockState == CursorLockMode.Locked || mouse.rightButton.isPressed)) d += mouse.delta.ReadValue() * lookSensitivity;
            if (pad != null) { var st = pad.rightStick.ReadValue(); if (st.sqrMagnitude > 0.01f) d += st * (stickLookSpeed * Time.unscaledDeltaTime); }
            look.x = Mathf.Clamp(look.x + d.x, -160f, 160f);
            look.y = Mathf.Clamp(look.y + d.y, -60f, 75f);
        }

        /// <summary>Chase free look: the right mouse button held turns it with the mouse (and the right stick turns it
        /// with no button). The view stays where it was put while the button is held; recentreDelay seconds after it is
        /// let go (or the stick centred) it eases back behind the truck (user, 2026-10-26).</summary>
        void ChaseLook()
        {
            var mouse = Mouse.current; var pad = Gamepad.current;
            var d = Vector2.zero;
            bool held = false;
            if (mouse != null && mouse.rightButton.isPressed) { held = true; d += mouse.delta.ReadValue() * lookSensitivity; }
            if (pad != null)
            {
                var st = pad.rightStick.ReadValue();
                if (st.sqrMagnitude > 0.01f) { held = true; d += st * (stickLookSpeed * Time.unscaledDeltaTime); }
            }
            look.x = Mathf.DeltaAngle(0f, look.x + d.x);
            look.y = Mathf.Clamp(look.y - d.y, -20f, 60f);
            if (held) lastLook = Time.unscaledTime;
            else if (Time.unscaledTime - lastLook > recentreDelay && look.sqrMagnitude > 1e-6f)
                look = Vector2.Lerp(look, Vector2.zero, 1f - Mathf.Exp(-recentreRate * Time.unscaledDeltaTime));
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
