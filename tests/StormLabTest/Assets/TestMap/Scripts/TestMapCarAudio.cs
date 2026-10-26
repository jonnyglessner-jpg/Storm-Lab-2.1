// The chase truck's sound (TruckSynth, from the car): engine rpm from its speed through a six-speed automatic (in
// reverse, the throttle is the brake pedal), tyre squeal from how far the tyres slide past their grip, road roar, and a
// thump when the suspension takes a hard hit. 3-D from the truck, so it fades as the free camera flies off.
using StormLab.Audio;
using UnityEngine;

namespace StormLab.TestMap
{
    [RequireComponent(typeof(TestMapCar))]
    public class TestMapCarAudio : MonoBehaviour
    {
        [Range(0f, 1f)] public float volume = 0.9f;
        [Tooltip("sideways slip at which the tyres start to squeal, and slip at which they squeal fully (WheelHit.sidewaysSlip)")]
        public Vector2 sideSlip = new Vector2(0.22f, 0.55f);
        [Tooltip("forward slip at which the tyres start to squeal (wheelspin, locked brakes), and full squeal")]
        public Vector2 forwardSlip = new Vector2(0.45f, 0.95f);

        /// <summary>the gearbox's state and the slide now, for readouts</summary>
        public Gearbox Gearbox { get; } = new Gearbox();
        public float Skid { get; private set; }

        TestMapCar car;
        TruckSynth synth;
        ProceduralAudio output;
        float[] lastComp = new float[0];

        void Start()
        {
            car = GetComponent<TestMapCar>();
            synth = new TruckSynth(ProceduralAudio.SampleRate);
            output = ProceduralAudio.Create(transform, "Truck sound", synth, true, volume);
            output.transform.localPosition = new Vector3(0f, 0.9f, 1.2f);
            var src = output.Source;
            src.rolloffMode = AudioRolloffMode.Logarithmic; src.minDistance = 8f; src.maxDistance = 600f;
            lastComp = new float[car.wheels.Length];
            if (car.wheels.Length > 0) Gearbox.wheelRadius = car.wheels[0].collider.radius;
        }

        void FixedUpdate()
        {
            if (synth == null) return;
            float v = car.ForwardSpeed, dt = Time.fixedDeltaTime;
            // in reverse the brake pedal drives the truck
            float pedal = v < -0.3f || (car.Brake > 0.01f && Mathf.Abs(v) < 0.5f && car.Throttle < 0.01f) ? car.Brake : car.Throttle;
            Gearbox.Step(v, pedal, dt);
            float skid = 0f, bump = 0f;
            for (int i = 0; i < car.wheels.Length; i++)
            {
                var wc = car.wheels[i].collider;
                if (!wc.GetGroundHit(out var hit)) { lastComp[i] = 0f; continue; }
                float s = Mathf.InverseLerp(sideSlip.x, sideSlip.y, Mathf.Abs(hit.sidewaysSlip))
                        + Mathf.InverseLerp(forwardSlip.x, forwardSlip.y, Mathf.Abs(hit.forwardSlip));
                skid = Mathf.Max(skid, Mathf.Clamp01(s));
                // how fast the spring is being squeezed: a hard hit is a thump
                float comp = 1f - (-wc.transform.InverseTransformPoint(hit.point).y - wc.radius) / Mathf.Max(wc.suspensionDistance, 0.01f);
                float rate = (comp - lastComp[i]) / dt;
                lastComp[i] = comp;
                if (rate > 4f) bump = Mathf.Max(bump, Mathf.InverseLerp(4f, 14f, rate));
            }
            // a slide needs some speed to squeal
            Skid = skid * Mathf.InverseLerp(1.5f, 5f, car.Body.linearVelocity.magnitude);
            synth.rpm = Gearbox.rpm; synth.load = pedal; synth.speed = car.Body.linearVelocity.magnitude; synth.skid = Skid;
            if (bump > 0.05f) synth.bump = bump;
            output.Source.volume = volume;
        }
    }
}
