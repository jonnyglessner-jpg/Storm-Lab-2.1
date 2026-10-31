using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using NVector3 = System.Numerics.Vector3;

namespace StormLab.Lightning
{
    public readonly struct LightningIllumination
    {
        public readonly Vector3 position;
        public readonly float intensity;
        public readonly FlashKind kind;
        public LightningIllumination(Vector3 position, float intensity, FlashKind kind)
        { this.position = position; this.intensity = intensity; this.kind = kind; }
    }

    /// <summary>Independent lightning presentation. Does not read or mutate the brain, genome or cloud renderer.</summary>
    [DefaultExecutionOrder(110)]
    public sealed class LightningSystem : MonoBehaviour
    {
        public LightningSettings settings = new LightningSettings();
        /// <summary>luminous intensity of a flash's light on the ground and cloud at unit flash intensity, candela (HDRP):
        /// 3e8 cd lights the ground 5 km away at 12 lux, fifty times a full moon and nothing beside daylight's 10^5 lux</summary>
        public const float FlashCandela = 3e8f;
        [Tooltip("Local preview clock. Disable and call SampleAt(playbackSeconds) to follow a storm clock.")]
        public bool advanceClock = true;
        [Min(0)] public float timeScale = 1;
        public double playbackSeconds;
        [Tooltip("World ground plane in metres. Charge heights are relative to this plane.")]
        public float groundY;
        [Tooltip("Hide internal channels until the cloud renderer supports lightning transmittance.")]
        public bool showInCloudChannels;
        [Min(.1f)] public float channelWidthM = 3;
        [Tooltip("Minimum apparent core width (the core's full width at half its brightness). Under about 2 a channel " +
                 "that does not clip to white breaks into a dotted line on the diagonal (every other pixel centre).")]
        [Range(.5f, 3)] public float minimumPixelWidth = 2f;
        [Tooltip("the channel's radiance in the storm's units (1 = a sunlit cloud); a return stroke is hundreds of times a " +
                 "sunlit cloud, and must stay well above the flash's light on the cloud, which lights it to tens of units")]
        [Min(0)] public float emission = 600;
        [Tooltip("scales the flashes' light on the scene and their channels' brightness (a game's night exposure: " +
                 "StormLab sets it from StormRenderer.LightningScale)")]
        [Min(0)] public float lightScale = 1;
        [Range(1, 24)] public int maxVisibleFlashes = 8;
        public Transform observer;
        public bool thunder = true;
        [Range(-60, 50)] public float temperatureC = 20;
        [Min(0)] public float audibleRangeM = 20000;

        // The future cloud adapter can read this immediately after SampleAt/LateUpdate.
        // Intensities are artistic relative radiance, not calibrated watts or lux.
        public IReadOnlyList<LightningIllumination> Illumination => illumination;
        /// <summary>Owned by this system; an optional cloud adapter may configure its attenuation inputs.</summary>
        public Material ChannelMaterial => material;
        public event Action<LightningFlash> FlashStarted;
        /// <summary>Optional storm-history adapter. Return the charge region's horizontal centre at emission time.
        /// Its y is the base altitude for the charge-height settings. Enables replay of a moving storm.</summary>
        public Func<double, Vector3> CellCentreAtTime;
        /// <summary>Optional terrain sampling in world metres; used for ground attachments.</summary>
        public Func<Vector3, float> TerrainHeight;
        /// <summary>When connected, null means no valid cloud anchors: suppress this event rather than emit in clear air.</summary>
        public Func<FlashEvent, LightningAnchors?> ResolveCloudAnchors;
        /// <summary>Optional storm-driven rate and flash-type split at a playback time (see LightningStormDriver). It must
        /// depend only on the time for replay. While set, the settings' rate and fractions are ignored and
        /// settings.maxFlashesPerMinute is the ceiling. Call ResetPlayback() after assigning or replacing it.</summary>
        public Func<double, LightningActivity> ActivityAtTime;
        /// <summary>Optional thunder in place of the built-in synthesis: called once per flash when its thunder is due (as the
        /// built-in thunder would play), with how many seconds ago the flash began. Distance, range and voices are the handler's.</summary>
        public Action<LightningFlash, double> ThunderOverride;
        /// <summary>The storm-driven activity at the last sampled time, for readouts and tuning; null without ActivityAtTime.</summary>
        public LightningActivity? CurrentActivity { get; private set; }
        public bool TryGetFlash(long id, out LightningFlash flash)
        {
            bool found = active.TryGetValue(id, out var visual);
            flash = found ? visual.flash : null;
            return found;
        }

        sealed class Visual
        {
            public LightningFlash flash;
            public GameObject root;
            public LineRenderer[] lines;
            public Light light;
        }
        sealed class Voice
        {
            public AudioSource source;
            public AudioClip clip;
            public double endsAt;
        }
        /// <summary>
        /// Draws the channels after the storm has been composited. HDRP draws every transparent before the
        /// BeforeTransparent custom passes when Refraction is off in the frame settings, so a storm composited there
        /// (StormCustomPass) laid its transmittance over the channels on top of their own occlusion. Here, at
        /// BeforePostProcess, the channels go over the finished scene, and cloud and rain hide them once, through
        /// their own optical depth (StormLightning.shader).
        /// </summary>
        sealed class ChannelPass : CustomPass
        {
            static readonly ShaderTagId Tag = new ShaderTagId("StormLightning");
            protected override bool executeInSceneView => true;
            protected override void Execute(CustomPassContext ctx)
            {
                var desc = new UnityEngine.Rendering.RendererUtils.RendererListDesc(Tag, ctx.cullingResults, ctx.hdCamera.camera)
                {
                    renderQueueRange = RenderQueueRange.all, sortingCriteria = SortingCriteria.CommonTransparent,
                    rendererConfiguration = PerObjectData.None,
                };
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                CoreUtils.DrawRendererList(ctx.cmd, ctx.renderContext.CreateRendererList(desc));
            }
        }
        static ChannelPass channelPass;
        /// <summary>HDRP drops its global custom passes when scripts reload, so this is called every sample, as the storm does.</summary>
        static void EnsureChannelPass()
        {
            channelPass ??= new ChannelPass { name = "StormLab lightning channels" };
            CustomPassVolume.RegisterUniqueGlobalCustomPass(CustomPassInjectionPoint.BeforePostProcess, channelPass);
        }

        readonly List<FlashEvent> events = new List<FlashEvent>();
        readonly Dictionary<long, Visual> active = new Dictionary<long, Visual>();
        readonly List<long> expired = new List<long>();
        readonly List<LightningIllumination> illumination = new List<LightningIllumination>();
        readonly List<Voice> voices = new List<Voice>();
        Material material;
        // one reusable gradient for a crawling channel's head (LineRenderer copies it on assignment)
        readonly Gradient headGradient = new Gradient();
        readonly GradientColorKey[] colourKeys = new GradientColorKey[3];
        readonly GradientAlphaKey[] alphaKeys = { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) };
        double previous = double.NaN;
        long consideredThrough = -1;

        void LateUpdate()
        {
            if (advanceClock) { playbackSeconds += Time.deltaTime * timeScale; SampleAt(playbackSeconds); }
            CleanupVoices();
        }

        public void SampleAt(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 1e9)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            settings.Validate();
            EnsureChannelPass();
            double dt = seconds - previous;
            bool seek = double.IsNaN(previous) || dt < 0 || dt > .25;
            if (seek) ClearPresentation();
            double exposureStart = seek ? seconds : previous;
            expired.Clear();
            foreach (var pair in active)
                if (seconds - pair.Value.flash.Event.time > Math.Max(1, pair.Value.flash.Duration)) expired.Add(pair.Key);
            foreach (long key in expired) { Release(active[key].root); active.Remove(key); }
            // back far enough to rebuild the longest flash after a seek: a crawler crawls for up to 2.2 s and pulses on
            LightningSchedule.Query(settings, Math.Max(-1e-6, seconds - 3), seconds, events, ActivityAtTime);
            CurrentActivity = ActivityAtTime?.Invoke(seconds);
            // At high rates the newest flashes win; all allocation budgets remain bounded.
            for (int i = Math.Max(0, events.Count - maxVisibleFlashes); i < events.Count; i++)
            {
                FlashEvent e = events[i];
                if (active.ContainsKey(e.id) || e.id <= consideredThrough) continue;
                consideredThrough = e.id;
                LightningAnchors? anchors = ResolveCloudAnchors?.Invoke(e);
                if (ResolveCloudAnchors != null && !anchors.HasValue) continue;
                var centre = transform.position; centre.y = groundY;
                if (CellCentreAtTime != null) centre = CellCentreAtTime(e.time);
                var flash = LightningFlash.Create(e, settings, ToNumerics(centre), groundY,
                    TerrainHeight == null ? null : p => TerrainHeight(ToUnity(p)), anchors);
                if (seconds - e.time > flash.Duration && e.time < exposureStart) continue;
                if (active.Count >= maxVisibleFlashes)
                {
                    long oldest = long.MaxValue;
                    foreach (long key in active.Keys) oldest = Math.Min(oldest, key);
                    if (oldest >= e.id) continue;
                    Release(active[oldest].root); active.Remove(oldest);
                }
                var visual = BuildVisual(flash);
                active.Add(e.id, visual);
                FlashStarted?.Invoke(flash);
                if (!seek && thunder && Math.Abs(timeScale - 1) < .01f && e.time > previous)
                {
                    if (ThunderOverride != null) ThunderOverride(flash, seconds - e.time);
                    else PlayThunder(flash, seconds - e.time);
                }
            }
            illumination.Clear();
            // The light scale goes into the material, not the vertex colours: those are 8-bit, and a night scale of a few
            // thousandths rounded the branches to black and the trunk to one step.
            ApplyEmission();
            foreach (var pair in active)
            {
                var visual = pair.Value; var flash = visual.flash;
                double age = seconds - flash.Event.time;
                float intensity = flash.Intensity(exposureStart - flash.Event.time, age);
                bool visible = flash.Event.kind != FlashKind.InCloud || showInCloudChannels;
                bool crawler = flash.Event.kind == FlashKind.Crawler;
                for (int c = 0; c < visual.lines.Length; c++)
                {
                    var line = visual.lines[c]; var channel = flash.Channels[c];
                    // each channel runs out from its root in its own time: with the stepped leader for a bolt, after the
                    // trunk's leader has passed its root for a crawler (LightningChannel.startAge)
                    float progress = channel.Progress(age);
                    if (progress <= 0) { line.enabled = false; continue; }
                    float power = intensity * channel.brightness;
                    // Failed side leaders are mostly visible during the first return stroke; a crawler's branches keep
                    // carrying current through its pulses.
                    if (!channel.main && age > flash.StrokeTimes[0] + .06) power *= crawler ? .6f : .08f;
                    float head = power;
                    if (crawler)
                    {
                        // The crawl: a trail behind a bright head while the channel runs out, the trail kept glowing while
                        // any part of the flash still crawls, and the pulses over the channels already formed.
                        float trail = .5f * channel.brightness;
                        if (age < flash.LeadSeconds + .1) power = Math.Max(power, trail);
                        head = progress < 1 ? Math.Max(power, channel.brightness) : power;
                    }
                    else if (age < flash.LeadSeconds) head = power = .008f * channel.brightness;
                    line.enabled = visible && Math.Max(power, head) > .0005f;
                    line.positionCount = Math.Max(2, (int)(channel.points.Length * progress));
                    for (int p = 0; p < line.positionCount; p++) line.SetPosition(p, ToUnity(channel.points[p]));
                    if (crawler && progress < 1)
                    {
                        // the head is the last quarter of what has formed, not a fade over the whole channel
                        colourKeys[0] = new GradientColorKey(new Color(power, power, power), 0);
                        colourKeys[1] = new GradientColorKey(new Color(power, power, power), .75f);
                        colourKeys[2] = new GradientColorKey(new Color(head, head, head), 1);
                        headGradient.SetKeys(colourKeys, alphaKeys);
                        line.colorGradient = headGradient;
                    }
                    else
                    {
                        line.startColor = new Color(power, power, power, 1);
                        line.endColor = channel.main || crawler ? new Color(head, head, head, 1) : new Color(0, 0, 0, 0);
                    }
                }
                visual.light.intensity = intensity * FlashCandela * lightScale;
                if (intensity > .0005f)
                {
                    var path = flash.Channels[0].points;
                    // An extended discharge lights more than its initiation point. Bound the source count per flash. A
                    // crawler lights the cloud along the part of its trunk that has formed, its light riding with the head.
                    float formed = crawler ? flash.Channels[0].Progress(age) : 1;
                    for (int sample = 0; sample < 4; sample++)
                        illumination.Add(new LightningIllumination(ToUnity(path[(int)((path.Length - 1) * formed * sample / 3)]), intensity * .25f, flash.Event.kind));
                    if (crawler) visual.light.transform.position = ToUnity(path[(int)((path.Length - 1) * formed)]);
                }
            }
            previous = seconds; playbackSeconds = seconds;
        }

        Visual BuildVisual(LightningFlash flash)
        {
            if (!material)
            {
                Shader shader = Resources.Load<Shader>("StormLightning");
                if (!shader) throw new InvalidOperationException("StormLightning shader is missing.");
                material = new Material(shader) { name = "Lightning emission (runtime)", hideFlags = HideFlags.DontSave };
            }
            ApplyEmission();
            var root = new GameObject("Lightning " + flash.Event.id); root.layer=gameObject.layer; root.transform.SetParent(transform, false);
            root.hideFlags = HideFlags.DontSave;
            root.AddComponent<LightningGeneratedVisual>();
            var lines = new LineRenderer[flash.Channels.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var channel = flash.Channels[i]; var go = new GameObject(i == 0 ? "Return channel" : "Side leader");
                go.layer=gameObject.layer;
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(root.transform, false);
                var line = go.AddComponent<LineRenderer>(); lines[i] = line;
                line.enabled = false;
                line.sharedMaterial = material; line.useWorldSpace = true;
                line.widthMultiplier = channelWidthM * (channel.main ? 1 : .4f);
                if (!channel.main) line.widthCurve = AnimationCurve.Linear(0, 1, 1, .15f);
                line.numCornerVertices = 2; line.numCapVertices = 2;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false; line.textureMode = LineTextureMode.Stretch;
                line.positionCount = channel.points.Length;
                for (int p = 0; p < channel.points.Length; p++) line.SetPosition(p, ToUnity(channel.points[p]));
            }
            var light = root.AddComponent<Light>(); light.type = LightType.Point;
            root.AddComponent<HDAdditionalLightData>();
            light.lightUnit = LightUnit.Candela; light.intensity = 0;
            bool aloft = flash.Event.kind == FlashKind.InCloud || flash.Event.kind == FlashKind.Crawler;
            light.transform.position = aloft ? ToUnity(flash.LightPosition) :
                ToUnity(flash.Channels[0].points[128]) + Vector3.up * 120;
            light.color = new Color(.86f, .9f, 1); light.range = flash.Event.kind == FlashKind.Crawler ? 12000 : 6000;
            light.shadows = LightShadows.None;
            return new Visual { flash = flash, root = root, lines = lines, light = light };
        }

        void ApplyEmission()
        {
            if (!material) return;
            float e = emission * lightScale;
            material.SetColor("_Color", new Color(e, e * .96f, e * 1.08f, 1));
        }

        void PlayThunder(LightningFlash flash, double lateSeconds)
        {
            CleanupVoices();
            if (voices.Count >= 16) return;
            Transform ear = observer;
            if (!ear && Camera.main) ear = Camera.main.transform;
            if (!ear) return;
            var path = flash.Channels[0].points;
            float distance = float.MaxValue;
            foreach (var p in path) distance = Math.Min(distance, NVector3.Distance(p, ToNumerics(ear.position)));
            if (distance > audibleRangeM) return;
            var sound = LightningThunder.Synthesize(flash, ToNumerics(ear.position), temperatureC);
            var clip = AudioClip.Create("Procedural thunder", sound.samples.Length, 1, ThunderSound.SampleRate, false);
            clip.hideFlags = HideFlags.DontSave;
            clip.SetData(sound.samples, 0);
            var go = new GameObject("Delayed thunder"); go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<LightningGeneratedVisual>().clip = clip;
            var source = go.AddComponent<AudioSource>(); source.playOnAwake = false; source.clip = clip;
            // Distance filtering and attenuation are already baked in; apply only listener-relative pan here.
            source.spatialBlend = 0;
            source.panStereo = Vector3.Dot(ear.right, (ToUnity(flash.LightPosition) - ear.position).normalized) * .75f;
            double delay = Math.Max(0, sound.delaySeconds - lateSeconds);
            source.PlayScheduled(AudioSettings.dspTime + delay);
            voices.Add(new Voice { source = source, clip = clip, endsAt = AudioSettings.dspTime + delay + clip.length + .1 });
        }

        void CleanupVoices()
        {
            for (int i = voices.Count - 1; i >= 0; i--)
                if (AudioSettings.dspTime >= voices[i].endsAt)
                { Release(voices[i].source.gameObject); Release(voices[i].clip); voices.RemoveAt(i); }
        }

        /// <summary>Call after changing rate, seed, geometry or origin to begin a new presentation segment.</summary>
        public void ResetPlayback() { ClearPresentation(); previous = double.NaN; }
        void ClearPresentation()
        {
            foreach (var visual in active.Values) Release(visual.root);
            active.Clear(); illumination.Clear();
            consideredThrough = -1;
            foreach (var voice in voices) { voice.source.Stop(); Release(voice.source.gameObject); Release(voice.clip); }
            voices.Clear();
            // Serialized children can outlive the nonserialized dictionaries across an editor reload.
            // Sweep owned roots too, including the exact legacy shape created before ownership markers.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (!child.gameObject.activeSelf && Application.isPlaying) continue; // Already queued for destruction.
                var generated = child.GetComponent<LightningGeneratedVisual>();
                var legacy = child.Find("Return channel");
                var line = legacy ? legacy.GetComponent<LineRenderer>() : null;
                bool legacyChannel = child.name.StartsWith("Lightning ", StringComparison.Ordinal) &&
                    long.TryParse(child.name.Substring(10), out _) && child.GetComponent<Light>() &&
                    line && line.sharedMaterial && line.sharedMaterial.shader.name == "StormLab/Lightning";
                if (!generated && !legacyChannel) continue;
                if (generated && generated.clip) Release(generated.clip);
                Release(child.gameObject);
            }
        }
        void OnEnable() { ResetPlayback(); EnsureChannelPass(); RenderPipelineManager.beginCameraRendering += PrepareCamera; }
        void OnDisable() { RenderPipelineManager.beginCameraRendering -= PrepareCamera; ResetPlayback(); }
        void PrepareCamera(ScriptableRenderContext context, Camera camera)
            => PrepareForCamera(camera);
        /// <summary>Also call before explicit render requests, which can bypass pipeline camera callbacks.</summary>
        public void PrepareForCamera(Camera camera)
        {
            if (!camera || camera.pixelHeight <= 0) return;
            foreach (var visual in active.Values)
            for (int i = 0; i < visual.lines.Length; i++)
            {
                var channel = visual.flash.Channels[i];
                var midpoint = channel.points[channel.points.Length / 2];
                float depth = Math.Max(1, Vector3.Dot(ToUnity(midpoint) - camera.transform.position, camera.transform.forward));
                float metresPerPixel = camera.orthographic ? 2 * camera.orthographicSize / camera.pixelHeight :
                    2 * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) / camera.pixelHeight;
                float core = Math.Max(channelWidthM * (channel.main ? 1 : .4f), metresPerPixel * minimumPixelWidth);
                visual.lines[i].widthMultiplier = core * 6;
            }
        }
        void OnDestroy() { if (material) Release(material); }
        static void Release(UnityEngine.Object obj)
        {
            if (!obj) return;
            // Destroy is deferred in Play mode; stop drawing/playing immediately on reset or seek.
            if (obj is GameObject go) go.SetActive(false);
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }
        static NVector3 ToNumerics(Vector3 p) => new NVector3(p.x, p.y, p.z);
        static Vector3 ToUnity(NVector3 p) => new Vector3(p.X, p.Y, p.Z);
    }
}
