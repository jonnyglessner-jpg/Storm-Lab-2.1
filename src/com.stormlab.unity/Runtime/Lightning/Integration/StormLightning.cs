// Lightning for a StormRenderer: add this next to the storm. It adds and wires a LightningSystem (flashes, channel
// drawing, flash light, thunder) and a LightningCloudBridge (flashes start in the drawn cloud and light it from inside),
// and takes the flash rate and types from the storm's plan and cycles (StormLightningActivity).
using UnityEngine;

namespace StormLab.Lightning
{
    public enum LightningClock { Auto, Storm, Wall }

    [ExecuteAlways, DefaultExecutionOrder(115)]
    [RequireComponent(typeof(StormRenderer))]
    public sealed class StormLightning : MonoBehaviour
    {
        [Tooltip("Storm: the lightning runs on storm time (minutes x 60): flash rates and thunder are real at real-time " +
                 "playback (minutesPerSecond = 1/60) and replay exactly on a seek. Wall: it runs on real seconds with the " +
                 "rate at the storm time shown, so flashes stay watchable at the web build's 60x. Auto: Storm when the " +
                 "storm plays within 2x of real time, else Wall")]
        public LightningClock clock = LightningClock.Auto;
        [Tooltip("flash-type split (ground and positive shares)")]
        public ClimoRegion region = ClimoRegion.Plains;
        public StormLightningTuning tuning = new StormLightningTuning();
        [Tooltip("the player or camera that hears thunder (empty: the main camera)")]
        public Transform observer;
        [Tooltip("ground strikes land on scene colliders (a ray down from 6 km); off: on the storm's ground plane")]
        public bool strikeColliders = true;
        [Tooltip("layers the strike ray hits")]
        public LayerMask strikeLayers = ~0;
#pragma warning disable 0414   // shown in the Inspector
        [Tooltip("Live readout in Play: the storm-driven activity now. Not an input.")]
        [SerializeField] string activityNow = "";
#pragma warning restore 0414

        public StormRenderer Storm { get; private set; }
        public LightningSystem Flashes { get; private set; }
        public LightningCloudBridge Bridge { get; private set; }
        public LightningClock BoundClock { get; private set; }

        StormPlan boundPlan;
        LightningCycle[] cycles;
        bool dirty = true;
        double wallSeconds;

        void OnEnable() { dirty = true; Wire(); }
        void OnValidate() { dirty = true; }

        void Wire()
        {
            Storm = GetComponent<StormRenderer>();
            Flashes = GetComponent<LightningSystem>();
            if (!Flashes) Flashes = gameObject.AddComponent<LightningSystem>();
            Bridge = GetComponent<LightningCloudBridge>();
            if (!Bridge) Bridge = gameObject.AddComponent<LightningCloudBridge>();
            Bridge.lightning = Flashes; Bridge.storm = Storm;
            Bridge.Connect();
            Flashes.showInCloudChannels = true;
            Flashes.groundY = Storm.origin.y;
            // the charge region rides with the updraft; its y is the ground the charge heights are measured from
            Flashes.CellCentreAtTime = t =>
            {
                var u = Storm.UpdraftWorld(BoundClock == LightningClock.Storm ? t / 60.0 : Storm.StormTime);
                return new Vector3(u.x, Storm.origin.y, u.z);
            };
            Flashes.TerrainHeight = p =>
                strikeColliders && Physics.Raycast(new Vector3(p.x, Storm.origin.y + 6000f, p.z), Vector3.down, out var hit, 12000f, strikeLayers, QueryTriggerInteraction.Ignore)
                    ? hit.point.y : Storm.origin.y;
            Storm.OriginShifted -= OnOriginShifted;
            Storm.OriginShifted += OnOriginShifted;
        }

        // Flashes in flight are drawn where they were made; after a floating-origin shift they are rebuilt from the
        // (deterministic) schedule at the new positions.
        void OnOriginShifted(StormRenderer s, Vector3 offset)
        {
            if (!Flashes) return;
            Flashes.groundY = s.origin.y;
            Flashes.ResetPlayback();
        }

        void OnDisable() { if (Storm) Storm.OriginShifted -= OnOriginShifted; }

        LightningClock Resolve() => clock != LightningClock.Auto ? clock
            : (Storm && Storm.minutesPerSecond <= 2f / 60f ? LightningClock.Storm : LightningClock.Wall);

        /// <summary>Rebinds when the storm's plan, the clock or the tuning changes; the flash history starts over.</summary>
        public void Rebind() { dirty = true; }

        void LateUpdate()
        {
            if (!Bind() || !Application.isPlaying) return;
            double now;
            if (BoundClock == LightningClock.Storm)
            {
                now = Storm.StormTime * 60.0;
                // thunder only plays at real-time speed
                Flashes.timeScale = Storm.playing ? Storm.minutesPerSecond * 60f : 0f;
            }
            else
            {
                wallSeconds += Time.deltaTime;
                now = wallSeconds;
                Flashes.timeScale = 1f;
            }
            Flashes.SampleAt(now);
            var a = Flashes.CurrentActivity;
            activityNow = a.HasValue ? $"{BoundClock}: {a.Value} at {Storm.stormMinutes:0.0} min" : "";
        }

        /// <summary>fewer flashes as the storm dies (StormLife)</summary>
        static LightningActivity Dying(LightningActivity a, StormPlan P, double tMin)
        {
            double v = StormLife.Vigour(P, tMin);
            return v >= 1 ? a : new LightningActivity(a.flashesPerMinute * (float)v, a.groundFraction, a.positiveGroundFraction, a.reach * (float)v, a.crawlerFraction);
        }

        /// <summary>Wire and bind to the storm's current plan (LateUpdate does this each frame; tools outside Play call it,
        /// then drive Flashes.SampleAt themselves). False while the storm has no plan.</summary>
        public bool Bind()
        {
            if (!Storm || !Flashes) Wire();
            if (Storm.Plan == null) return false;
            var want = Resolve();
            if (dirty || Storm.Plan != boundPlan || want != BoundClock)
            {
                boundPlan = Storm.Plan; BoundClock = want;
                cycles = StormLightningActivity.Cycles(boundPlan);
                var plan = boundPlan; var cyc = cycles; var k = tuning; var r = region; var s = Storm;
                if (want == LightningClock.Storm) Flashes.ActivityAtTime = t => Dying(StormLightningActivity.At(plan, cyc, t / 60.0, k, r), plan, t / 60.0);
                else Flashes.ActivityAtTime = _ => Dying(StormLightningActivity.At(plan, cyc, s.StormTime, k, r), plan, s.StormTime);
                Flashes.settings.seed = plan.seed ?? 1;
                Flashes.groundY = Storm.origin.y;
                Flashes.ResetPlayback();
                wallSeconds = 0;
                dirty = false;
            }
            if (!Flashes.observer) Flashes.observer = observer ? observer : (Camera.main ? Camera.main.transform : null);
            Flashes.advanceClock = false;
            return true;
        }
    }
}
