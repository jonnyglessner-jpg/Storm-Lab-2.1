// The test map's car (a chase vehicle): WheelCollider physics, ported from the Dodge City drive map.
// Keyboard: W/S or arrows throttle, brake, reverse; A/D steer; Space handbrake; R reset upright.
// Gamepad: RT/LT, left stick, A handbrake, Y reset. Editor tests drive it through Step().
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    [RequireComponent(typeof(Rigidbody))]
    public class TestMapCar : MonoBehaviour
    {
        [System.Serializable]
        public class Wheel
        {
            public WheelCollider collider;
            public Transform visual;
            public bool steer, drive, handbrake;
        }

        public Wheel[] wheels = new Wheel[0];

        [Header("Engine")]
        public float maxMotorTorque = 2600f;      // total over the driven wheels, N m
        public float maxSpeedKph = 185f;
        public float reverseSpeedKph = 35f;
        public float brakeTorque = 4500f;
        public float handbrakeTorque = 6000f;

        [Header("Steering")]
        public float maxSteerAngle = 34f;
        public float highSpeedSteerAngle = 6f;
        public float steerSpeed = 3.5f;

        [Header("Stability")]
        public float antiRollStiffness = 9000f;
        public float downforce = 2.0f;            // N per (m/s)^2

        [Header("Condition")]
        [Tooltip("the engine's power as a share of its torque: an empty tank or a worn engine (the chase game sets it)")]
        public float powerScale = 1f;
        [Tooltip("the top speed as a share of the vehicle's (the chaser's sanity; the chase game sets it)")]
        public float speedScale = 1f;
        /// <summary>metres driven since the scene started (the odometer)</summary>
        public double OdometerM { get; private set; }
        /// <summary>the last collision's change of the car's speed, m/s, and when it was (Time.time; -1e9 for none)</summary>
        public float LastImpact { get; private set; }
        public float LastImpactTime { get; private set; } = -1e9f;
        /// <summary>the throttle the engine is actually working at, 0..1 (the fuel burn follows it)</summary>
        public float EngineLoad { get; private set; }

        [Header("Input")]
        [Tooltip("when off, the car ignores the keyboard and gamepad (free camera, tests)")]
        public bool acceptInput = true;
        [Tooltip("extra force on the car from outside, e.g. a tornado's wind, N (cleared each physics step)")]
        public Vector3 externalForce;

        public float SpeedKph => rb ? rb.linearVelocity.magnitude * 3.6f : 0f;
        public float ForwardSpeed => rb ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
        public Rigidbody Body => rb ? rb : rb = GetComponent<Rigidbody>();
        /// <summary>the last control step's throttle and brake, 0..1 (the engine sound reads them)</summary>
        public float Throttle { get; private set; }
        public float Brake { get; private set; }

        Rigidbody rb;
        float steerInput;

        void Awake() { rb = GetComponent<Rigidbody>(); }

        void Update()
        {
            if (acceptInput && ResetPressed()) ResetUpright();
            foreach (var w in wheels)
            {
                if (!w.visual) continue;
                w.collider.GetWorldPose(out var pos, out var rot);
                w.visual.SetPositionAndRotation(pos, rot);
            }
        }

        void FixedUpdate()
        {
            float throttle = 0, steer = 0, brake = 0; bool hand = false;
            if (acceptInput) ReadInput(out throttle, out steer, out brake, out hand);
            Step(throttle, steer, brake, hand, Time.fixedDeltaTime);
        }

        /// <summary>One physics tick of control.</summary>
        public void Step(float throttle, float steerTarget, float brakeIn, bool handbrake, float dt)
        {
            var body = Body;
            Throttle = Mathf.Clamp01(throttle); Brake = Mathf.Clamp01(brakeIn);
            float fwd = ForwardSpeed, kph = Mathf.Abs(fwd) * 3.6f;
            steerInput = Mathf.MoveTowards(steerInput, steerTarget, steerSpeed * dt);
            float steerAngle = steerInput * Mathf.Lerp(maxSteerAngle, highSpeedSteerAngle, Mathf.InverseLerp(10f, 130f, kph));
            float motor = 0f, brake = 0f, drive = throttle - brakeIn;
            float top = maxSpeedKph * Mathf.Clamp01(speedScale), power = maxMotorTorque * Mathf.Clamp01(powerScale);
            if (drive > 0.01f) { if (fwd < -0.5f) brake = drive * brakeTorque; else if (kph < top) motor = drive * power; }
            else if (drive < -0.01f) { if (fwd > 0.5f) brake = -drive * brakeTorque; else if (kph < Mathf.Min(reverseSpeedKph, top)) motor = drive * power * 0.6f; }
            else brake = 60f;
            EngineLoad = powerScale > 0.001f && Mathf.Abs(motor) > 1f ? Mathf.Abs(drive) : 0f;
            OdometerM += Mathf.Abs(fwd) * dt;
            int driven = 0;
            foreach (var w in wheels) if (w.drive) driven++;
            foreach (var w in wheels)
            {
                if (w.steer) w.collider.steerAngle = steerAngle;
                w.collider.motorTorque = w.drive ? motor / Mathf.Max(1, driven) : 0f;
                w.collider.brakeTorque = handbrake && w.handbrake ? Mathf.Max(brake, handbrakeTorque) : brake;
            }
            AntiRoll(0, 1); AntiRoll(2, 3);
            body.AddForce(-transform.up * downforce * body.linearVelocity.sqrMagnitude);
            if (externalForce != Vector3.zero) { body.AddForce(externalForce); externalForce = Vector3.zero; }
        }

        float teleportedAt = -1e9f;

        void OnCollisionEnter(Collision c)
        {
            // (the drop onto the ground after a teleport is not a crash: it cost the chase game 1 % of the truck a morning)
            if (Time.time - teleportedAt < 2f) return;
            float dv = c.impulse.magnitude / Mathf.Max(Body.mass, 1f);
            if (dv < 0.5f) return;
            LastImpact = dv; LastImpactTime = Time.time;
        }

        void AntiRoll(int l, int r)
        {
            if (wheels.Length <= r) return;
            var wl = wheels[l].collider; var wr = wheels[r].collider;
            float tl = 1f, tr = 1f;
            bool gl = wl.GetGroundHit(out var hl), gr = wr.GetGroundHit(out var hr);
            if (gl) tl = (-wl.transform.InverseTransformPoint(hl.point).y - wl.radius) / wl.suspensionDistance;
            if (gr) tr = (-wr.transform.InverseTransformPoint(hr.point).y - wr.radius) / wr.suspensionDistance;
            float f = (tl - tr) * antiRollStiffness;
            if (gl) Body.AddForceAtPosition(wl.transform.up * -f, wl.transform.position);
            if (gr) Body.AddForceAtPosition(wr.transform.up * f, wr.transform.position);
        }

        public void ResetUpright()
        {
            var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var pos = transform.position;
            if (GroundUnder(pos + Vector3.up * 50f, 200f, out var hit)) pos = hit.point;
            Teleport(pos, Quaternion.LookRotation(fwd.normalized, Vector3.up));
        }

        /// <summary>The ground straight down from `from`, skipping the car's own colliders (a ray from above the car's own
        /// spot used to land on its roof, so each teleport to the same spot stacked it 3 m higher; 2026-11-02).</summary>
        public bool GroundUnder(Vector3 from, float length, out RaycastHit ground)
        {
            ground = default; float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(from, Vector3.down, length, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(transform) || h.distance >= best) continue;
                best = h.distance; ground = h;
            }
            return best < float.MaxValue;
        }

        /// <summary>Put the car on the ground at groundPos (found by a raycast down from well above it: the point given may
        /// carry a storm-frame height, far from the terrain's; a cast from 60 m up missed terrain above it and left the car
        /// under the ground, falling, 2026-11-02), facing rot.</summary>
        public void Teleport(Vector3 groundPos, Quaternion rot)
        {
            var body = Body;
            if (GroundUnder(groundPos + Vector3.up * 1500f, 6000f, out var hit))
                groundPos = hit.point;
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(groundPos + Vector3.up * 0.6f, rot);
            body.position = transform.position; body.rotation = rot;
            steerInput = 0f;
            teleportedAt = Time.time;
        }

        void ReadInput(out float throttle, out float steer, out float brake, out bool handbrake)
        {
            throttle = steer = brake = 0f; handbrake = false;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle = 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) brake = 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
                handbrake = kb.spaceKey.isPressed;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                throttle = Mathf.Max(throttle, gp.rightTrigger.ReadValue());
                brake = Mathf.Max(brake, gp.leftTrigger.ReadValue());
                float s = gp.leftStick.x.ReadValue();
                if (Mathf.Abs(s) > 0.08f) steer = Mathf.Clamp(steer + s, -1f, 1f);
                handbrake |= gp.buttonSouth.isPressed;
            }
        }

        static bool ResetPressed() =>
            (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) ||
            (Gamepad.current != null && Gamepad.current.buttonNorth.wasPressedThisFrame);
    }
}
