// The photo camera (X): a real camera's controls on HDRP's physical camera. ISO (Q/E), aperture (left/right), shutter
// speed (up/down) and focal length (scroll, within the lens's range) set the exposure through HDRP's physical-camera
// exposure mode (EV100 = log2(N^2 / t) - log2(ISO / 100)), so an under- or over-exposed photo really comes out dark or
// blown out; the aperture drives the physical depth of field (autofocus at the frame's centre) and the shutter the
// motion blur. A (auto) sets the shutter from the scene's light (the storm panel's meter, TestMapStormPanel.ExposureEV)
// as a program mode would. Left click (or Space) takes the photo: the HUD hides for one frame, the frame is saved as a
// PNG under Application.persistentDataPath/StormLab/Photos, and the shot is judged (what is in it, how far, how well
// exposed, how sharp) and paid in credits in a chase. In the truck the view is the cab's; on foot the walker's eyes.
using System.Collections.Generic;
using System.IO;
using StormLab.Lightning;
using StormLab.TestMap;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab.Game
{
    public class ChaseCamera : MonoBehaviour
    {
        public ChaseGame game;
        public bool Active { get; private set; }
        public int iso = 400;
        public float aperture = 5.6f, shutter = 1f / 250f, focalMm = 35f;
        public bool autoShutter = true;
        /// <summary>the scene's metered exposure and the settings' EV100 now; their difference in stops (+ is over-exposed)</summary>
        public float SceneEV { get; private set; }
        public float SettingsEV => Mathf.Log(aperture * aperture / shutter, 2f) - Mathf.Log(iso / 100f, 2f);
        public float ExposureError => SceneEV - SettingsEV;
        public static string PhotoDir => Path.Combine(Application.persistentDataPath, "StormLab", "Photos");
        /// <summary>the last photo's verdict, for the HUD and the checks</summary>
        public string LastVerdict { get; private set; } = "";
        public int LastCredits { get; private set; }

        static readonly float[] Shutters = { 1f / 4000, 1f / 2000, 1f / 1000, 1f / 500, 1f / 250, 1f / 125, 1f / 60, 1f / 30, 1f / 15, 1f / 8, 1f / 4, 1f / 2, 1f, 2f, 4f, 8f, 15f, 30f };
        static readonly float[] Apertures = { 1.4f, 2f, 2.8f, 4f, 5.6f, 8f, 11f, 16f, 22f };
        static readonly int[] Isos = { 50, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200 };

        Camera cam; float savedFov; TestMapCamera.Mode savedMode; bool savedCarInput;
        Exposure exposure; DepthOfField dof; MotionBlur blur; TestMapStormPanel panel;
        bool shooting; float reviewUntil; Texture2D thumb; string reviewText;
        AudioSource click; AudioClip clickClip;
        GUIStyle big, small, bold;
        // the lightning seen: the last flash's time and where it lit, per lightning system subscribed
        readonly HashSet<LightningSystem> subscribed = new HashSet<LightningSystem>();
        float lastFlashTime = -1e9f; Vector3 lastFlashAt; float nextScan;
        // diminishing returns on the same tornado: photos of each storm's tornado in the last 90 s
        readonly Dictionary<StormRenderer, List<float>> recent = new Dictionary<StormRenderer, List<float>>();

        TestMapWorld World => game ? game.world : null;
        ChaseGear Body => ChaseSetup.CameraBody;
        ChaseGear Lens => ChaseSetup.Lens;

        void Start()
        {
            cam = Camera.main;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal && v.sharedProfile && v.sharedProfile.Has<Exposure>())
                {
                    v.profile.TryGet(out exposure);
                    if (!v.profile.TryGet(out dof)) { dof = v.profile.Add<DepthOfField>(false); dof.name = "DepthOfField"; }
                    if (!v.profile.TryGet(out blur)) { blur = v.profile.Add<MotionBlur>(false); blur.name = "MotionBlur"; }
                    break;
                }
            // the shutter's click: a short burst of noise with a snap
            clickClip = AudioClip.Create("shutter", 2205, 1, 44100, false);
            var d = new float[2205]; var rng = new System.Random(7);
            for (int i = 0; i < d.Length; i++) { float t = i / 2205f; d[i] = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 18f) * (i < 60 ? 1f : 0.35f) * 0.6f; }
            clickClip.SetData(d, 0);
            click = gameObject.AddComponent<AudioSource>(); click.playOnAwake = false; click.spatialBlend = 0f; click.volume = 0.5f;
        }

        void Update()
        {
            var world = World; if (!world || !cam) return;
            if (!panel) panel = world.GetComponent<TestMapStormPanel>();
            var kb = ChaseGame.InputBlocked ? null : Keyboard.current;
            if (kb != null && (kb.xKey.wasPressedThisFrame || (Active && kb.escapeKey.wasPressedThisFrame))) SetActive(!Active);
            if (!Active) return;
            ClampToGear();
            SceneEV = panel ? panel.ExposureEV : 12f;
            if (autoShutter) MeterShutter();
            if (kb != null && !shooting)
            {
                if (kb.upArrowKey.wasPressedThisFrame) { autoShutter = false; shutter = Step(Shutters, shutter, -1); }
                if (kb.downArrowKey.wasPressedThisFrame) { autoShutter = false; shutter = Step(Shutters, shutter, +1); }
                if (kb.rightArrowKey.wasPressedThisFrame) aperture = Mathf.Max(Step(Apertures, aperture, +1), Lens.apertureMin);
                if (kb.leftArrowKey.wasPressedThisFrame) aperture = Mathf.Max(Step(Apertures, aperture, -1), Lens.apertureMin);
                if (kb.eKey.wasPressedThisFrame) iso = Mathf.Clamp(StepI(Isos, iso, +1), Body.isoMin, Body.isoMax);
                if (kb.qKey.wasPressedThisFrame) iso = Mathf.Clamp(StepI(Isos, iso, -1), Body.isoMin, Body.isoMax);
                if (kb.aKey.wasPressedThisFrame) autoShutter = !autoShutter;
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    float sc = mouse.scroll.y.ReadValue();
                    if (Mathf.Abs(sc) > 0.01f) focalMm = Mathf.Clamp(focalMm * Mathf.Pow(1.12f, sc > 0 ? 1 : -1), Lens.focalMin, Lens.focalMax);
                }
                bool shoot = (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked) || kb.spaceKey.wasPressedThisFrame;
                if (shoot) TakePhoto();
            }
            ApplyToCamera();
            WatchLightning();
        }

        /// <summary>The body's ISO range and the lens's focal and aperture range (the gear may have changed with the chase).</summary>
        void ClampToGear()
        {
            iso = Mathf.Clamp(iso, Body.isoMin, Body.isoMax);
            focalMm = Mathf.Clamp(focalMm, Lens.focalMin, Lens.focalMax);
            aperture = Mathf.Max(aperture, Lens.apertureMin);
        }

        /// <summary>Program mode: the shutter stop nearest the scene's exposure at this aperture and ISO, raising the ISO
        /// when even the slowest handholdable shutter falls short.</summary>
        void MeterShutter()
        {
            for (int tries = 0; tries < 12; tries++)
            {
                float t = aperture * aperture / Mathf.Pow(2f, SceneEV + Mathf.Log(iso / 100f, 2f));
                shutter = Nearest(Shutters, t);
                if (shutter <= 1f / 30f || iso >= Body.isoMax) break;
                iso = Mathf.Clamp(StepI(Isos, iso, +1), Body.isoMin, Body.isoMax);
            }
        }

        static float Nearest(float[] stops, float v)
        {
            float best = stops[0];
            foreach (var s in stops) if (Mathf.Abs(Mathf.Log(s / v)) < Mathf.Abs(Mathf.Log(best / v))) best = s;
            return best;
        }
        static float Step(float[] stops, float v, int by)
        {
            int i = System.Array.IndexOf(stops, Nearest(stops, v));
            return stops[Mathf.Clamp(i + by, 0, stops.Length - 1)];
        }
        static int StepI(int[] stops, int v, int by)
        {
            int best = 0; for (int i = 0; i < stops.Length; i++) if (Mathf.Abs(stops[i] - v) < Mathf.Abs(stops[best] - v)) best = i;
            return stops[Mathf.Clamp(best + by, 0, stops.Length - 1)];
        }

        public void SetActive(bool on)
        {
            var world = World; if (!world || !cam || on == Active) return;
            var rig = world.cameraRig;
            Active = on;
            if (on)
            {
                savedFov = cam.fieldOfView; savedCarInput = world.car && world.car.acceptInput;
                if (rig) { savedMode = rig.mode; if (rig.mode != TestMapCamera.Mode.Free) rig.mode = TestMapCamera.Mode.Cab; }
                if (world.car) world.car.acceptInput = false;
                cam.usePhysicalProperties = true; cam.sensorSize = new Vector2(36f, 24f); cam.gateFit = Camera.GateFitMode.Horizontal;
                if (panel) panel.externalExposure = true;
                if (exposure) { exposure.mode.Override(ExposureMode.UsePhysicalCamera); exposure.compensation.Override(0f); }
                if (dof) { dof.active = true; dof.focusMode.Override(DepthOfFieldMode.UsePhysicalCamera); dof.focusDistanceMode.Override(FocusDistanceMode.Camera); }
                if (blur) blur.active = true;
                ApplyToCamera();
            }
            else
            {
                cam.usePhysicalProperties = false; cam.fieldOfView = savedFov;
                if (panel) panel.externalExposure = false;
                if (dof) dof.active = false;
                if (blur) { blur.intensity.Override(0f); blur.active = false; }
                if (rig && rig.mode != TestMapCamera.Mode.Free) rig.mode = savedMode;
                if (world.car && !(game.Player && game.Player.OnFoot) && !(game.Drone && game.Drone.Flying)) world.car.acceptInput = true;
                reviewUntil = 0f;
            }
        }

        void ApplyToCamera()
        {
            cam.focalLength = focalMm; cam.iso = iso; cam.shutterSpeed = shutter; cam.aperture = aperture;
            // autofocus on what the frame's centre is on: the ground or a building there, else far away
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            cam.focusDistance = Physics.Raycast(ray, out var hit, 20000f, ~0, QueryTriggerInteraction.Ignore) ? Mathf.Max(hit.distance, 0.5f) : 5000f;
            if (blur) blur.intensity.Override(Mathf.Clamp01(shutter * 60f));
        }

        /// <summary>Hear each live storm's lightning, so a photo can be judged lit by a flash.</summary>
        void WatchLightning()
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 2f;
            foreach (var s in World.LiveStorms())
            {
                var ls = s.GetComponent<LightningSystem>();
                if (ls && subscribed.Add(ls)) ls.FlashStarted += f => { lastFlashTime = Time.time; var p = f.LightPosition; lastFlashAt = new Vector3(p.X, p.Y, p.Z); };
            }
        }

        /// <summary>The shutter (also for the checks): the frame is rendered again by the camera alone (no HUD) into a
        /// texture, so it works wherever the camera draws (the editor's batch mode has no end of frame to wait for).</summary>
        public void TakePhoto()
        {
            if (!Active || shooting) return;
            var world = World;
            shooting = true;
            if (click && clickClip) click.PlayOneShot(clickClip);
            Texture2D tex = null;
            try
            {
                // at the camera's own size: the storm's buffers and history are that size (another size shows the old frame's
                // edge as a black block until it has drawn a few frames at it)
                int w = cam.pixelWidth > 8 ? cam.pixelWidth : 1280, h = cam.pixelHeight > 8 ? cam.pixelHeight : 720;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
                rt.Create();
                var req = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
                else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active; RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                RenderTexture.active = prev; rt.Release(); Destroy(rt);
            }
            catch (System.Exception e) { Debug.LogWarning($"ChaseCamera: could not render the photo: {e.Message}"); }
            Develop(tex);
        }

        void Develop(Texture2D tex)
        {
            var world = World;
            // judged before saving, so the verdict goes on the file's name
            int credits = Judge(out string verdict, out string subject);
            string file = null;
            if (tex && tex.width >= 8 && tex.height >= 8)
            {
                try
                {
                    Directory.CreateDirectory(PhotoDir);
                    file = Path.Combine(PhotoDir, $"{System.DateTime.Now:yyyyMMdd_HHmmss_fff}_{subject.Replace(' ', '_')}.png");
                    File.WriteAllBytes(file, tex.EncodeToPNG());
                }
                catch (System.Exception e) { Debug.LogWarning($"ChaseCamera: could not save the photo: {e.Message}"); }
                // the review thumbnail
                int tw = 360, th = Mathf.Max(8, Mathf.RoundToInt(360f * tex.height / tex.width));
                var rt = RenderTexture.GetTemporary(tw, th, 0);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                if (thumb) Destroy(thumb);
                thumb = new Texture2D(tw, th, TextureFormat.RGB24, false);
                thumb.ReadPixels(new Rect(0, 0, tw, th), 0, 0); thumb.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            }
            if (tex) Destroy(tex);
            LastVerdict = verdict; LastCredits = credits;
            ChaseSetup.chasePhotos++; ChaseProfile.Current.photos++;
            if (credits > 0) game.Earn(credits, verdict);
            reviewText = verdict + (game.EarnsCredits ? $"\n{credits} credits" : "\n(free roam: no credits)") + (file != null ? $"\nsaved to {Path.GetFileName(file)}" : "");
            reviewUntil = Time.unscaledTime + 4f;
            shooting = false;
        }

        /// <summary>What the photo shows and what it is worth: a tornado (by how near and how big in the frame), the
        /// storm's structure, or the landscape; times how well exposed, how sharp (handheld rule and the truck's speed),
        /// how noisy (the body's range), lit by lightning, and the body's payout; less for the same tornado again and
        /// again.</summary>
        int Judge(out string verdict, out string subject)
        {
            var world = World;
            float best = 0f; string what = "landscape"; subject = "landscape"; StormRenderer bestStorm = null; float bestMi = 0f;
            foreach (var s in world.LiveStorms())
            {
                float topKm = s.Plan != null ? (float)s.Plan.zb : 1.2f;
                if (s.TornadoAmount > 0.15f)
                {
                    var g = s.TornadoGroundWorld; var top = g + Vector3.up * (topKm * 1000f);
                    var vg = cam.WorldToViewportPoint(g); var vt = cam.WorldToViewportPoint(top); var vm = cam.WorldToViewportPoint((g + top) * 0.5f);
                    bool inFrame = (In(vg) || In(vm) || In(vt));
                    if (inFrame)
                    {
                        float mi = Vector3.ProjectOnPlane(g - cam.transform.position, Vector3.up).magnitude / 1609.344f;
                        float frac = Mathf.Abs(vt.y - vg.y);                   // the funnel's height as a share of the frame
                        float size = Mathf.Clamp01(frac / 0.35f) * (frac > 1.3f ? 0.7f : 1f);
                        float near = Mathf.Clamp01(1f - (mi - 0.3f) / 6f);
                        float v = 30f + 90f * near * size;
                        if (v > best) { best = v; what = $"{Shape(s)} {mi:0.0} mi"; subject = "tornado"; bestStorm = s; bestMi = mi; }
                    }
                }
                var up = cam.WorldToViewportPoint(s.UpdraftWorld(s.StormTime));
                if (In(up) && 12f > best) { best = 12f; what = "storm structure"; subject = "structure"; }
            }
            if (best <= 0f) best = 3f;
            var notes = new List<string>();
            // exposure: within half a stop is right; three stops out is unusable
            float err = Mathf.Abs(ExposureError);
            float qExp = err < 0.5f ? 1f : Mathf.Lerp(1f, 0.15f, Mathf.InverseLerp(0.5f, 3f, err));
            if (err >= 0.5f) notes.Add(ExposureError > 0 ? $"{ExposureError:+0.0} stops over" : $"{ExposureError:0.0} stops under");
            // sharpness: the handheld rule (1 / focal length), tighter from a moving truck or drone
            bool drone = game.Drone && game.Drone.Flying;
            float speed = drone ? game.Drone.Velocity.magnitude : game.Player && game.Player.OnFoot ? 0f : world.car ? world.car.Body.linearVelocity.magnitude : 0f;
            float limit = 1f / (focalMm * (1f + speed / 4f));
            float qBlur = shutter <= limit ? 1f : Mathf.Lerp(1f, 0.25f, Mathf.InverseLerp(0f, 3f, Mathf.Log(shutter / limit, 2f)));
            if (qBlur < 0.95f) notes.Add(speed > 1f ? (drone ? "blurred from the moving drone" : "blurred from the moving truck") : "camera shake");
            if (drone) notes.Add("from the drone");
            // noise: the body's top quarter of its range
            float isoFrac = Mathf.InverseLerp(Body.isoMax / 4f, Body.isoMax, iso);
            float qNoise = 1f - 0.3f * isoFrac;
            if (isoFrac > 0.3f) notes.Add("noisy");
            // lit by lightning in the frame
            float lightning = 1f;
            if (Time.time - lastFlashTime < 0.6f && In(cam.WorldToViewportPoint(lastFlashAt))) { lightning = 1.6f; notes.Add("with lightning"); }
            // a tornado at dusk or night is the harder shot
            float dark = subject == "tornado" && SceneEV < 6f ? 1.3f : 1f;
            if (dark > 1f) notes.Add("in low light");
            // the same tornado again: each shot of it in the last 90 s pays 60 % of the one before
            float again = 1f;
            if (bestStorm)
            {
                if (!recent.TryGetValue(bestStorm, out var list)) recent[bestStorm] = list = new List<float>();
                list.RemoveAll(t => Time.time - t > 90f);
                again = Mathf.Pow(0.6f, list.Count);
                if (list.Count > 0) notes.Add("same tornado again");
                list.Add(Time.time);
                if (list.Count == 1) ChaseProfile.Current.tornadoesPhotographed++;
            }
            int credits = Mathf.RoundToInt(best * qExp * qBlur * qNoise * lightning * dark * again * Body.payout);
            verdict = what + (notes.Count > 0 ? "  ·  " + string.Join(", ", notes) : "");
            return credits;
        }

        static bool In(Vector3 vp) => vp.z > 0f && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;

        static string Shape(StormRenderer s)
        {
            var t = s.Tornadoes; int c = s.Frame.cycleIndex;
            if (t != null && c >= 0 && c < t.Length && t[c] != null) return $"EF{t[c].ef} {t[c].shape.ToString().ToLowerInvariant()} tornado";
            return "tornado";
        }

        static string ShutterText(float t) => t >= 1f ? $"{t:0}\"" : $"1/{Mathf.RoundToInt(1f / t)}";

        void OnGUI()
        {
            var world = World;
            if (!Active || shooting || !world || world.hudHidden || ChaseGame.InputBlocked) return;
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                bold = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
                small = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                big.normal.textColor = bold.normal.textColor = small.normal.textColor = Color.white;
            }
            float W = Screen.width, H = Screen.height;
            var line = new Color(1f, 1f, 1f, 0.55f);
            // the viewfinder: the frame's edge, thirds, the focus box
            GUI.color = line;
            float ix = W * 0.04f, iy = H * 0.06f;
            GUI.DrawTexture(new Rect(ix, iy, W - 2 * ix, 1), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(ix, H - iy, W - 2 * ix, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(ix, iy, 1, H - 2 * iy), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(W - ix, iy, 1, H - 2 * iy), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            for (int i = 1; i < 3; i++) { GUI.DrawTexture(new Rect(ix + (W - 2 * ix) * i / 3f, iy, 1, H - 2 * iy), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(ix, iy + (H - 2 * iy) * i / 3f, W - 2 * ix, 1), Texture2D.whiteTexture); }
            GUI.color = line;
            float bx = W * 0.5f - 28, by = H * 0.5f - 20;
            GUI.DrawTexture(new Rect(bx, by, 56, 1), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(bx, by + 40, 56, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(bx, by, 1, 40), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(bx + 56, by, 1, 40), Texture2D.whiteTexture);
            GUI.color = Color.white;
            // the readout
            string focus = cam.focusDistance >= 4999f ? "∞" : cam.focusDistance >= 1000f ? $"{cam.focusDistance / 1000f:0.0} km" : $"{cam.focusDistance:0} m";
            GUI.Box(new Rect(W * 0.5f - 360, H - iy - 52, 720, 40), GUIContent.none);
            GUI.Label(new Rect(W * 0.5f - 350, H - iy - 48, 700, 32), $"ISO {iso}     f/{aperture:0.#}     {ShutterText(shutter)}{(autoShutter ? " A" : "")}     {focalMm:0} mm     focus {focus}     EV {SettingsEV:0.0}", big);
            // the meter: -3 .. +3 stops, the needle at the scene against the settings
            float mx = W * 0.5f - 150, my = H - iy - 76;
            GUI.color = line; GUI.DrawTexture(new Rect(mx, my + 8, 300, 1), Texture2D.whiteTexture);
            for (int i = -3; i <= 3; i++) GUI.DrawTexture(new Rect(mx + 150 + i * 50, my + 4, 1, 9), Texture2D.whiteTexture);
            float e = Mathf.Clamp(ExposureError, -3.2f, 3.2f);
            GUI.color = Mathf.Abs(ExposureError) < 0.5f ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.75f, 0.3f);
            GUI.DrawTexture(new Rect(mx + 150 + e * 50 - 1.5f, my - 2, 3, 20), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(mx - 50, my - 6, 46, 22), "-3", small); GUI.Label(new Rect(mx + 306, my - 6, 46, 22), "+3", small);
            GUI.Label(new Rect(W * 0.5f - 200, my - 30, 400, 22), Mathf.Abs(ExposureError) < 0.5f ? "exposure right" : ExposureError > 0 ? $"{ExposureError:+0.0} stops: over-exposed" : $"{ExposureError:0.0} stops: under-exposed", new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            // the gear and the controls
            GUI.Label(new Rect(ix + 10, iy + 8, 600, 22), $"PHOTO   {Body.name} + {Lens.name}{(game.Drone && game.Drone.Flying ? "   from the drone" : "")}", bold);
            GUI.Label(new Rect(ix + 10, iy + 30, 900, 22), "up / down  shutter     left / right  aperture     Q / E  ISO     scroll  zoom     A  auto shutter     click or Space  shoot     X / Esc  close", small);
            // the last photo
            if (Time.unscaledTime < reviewUntil && thumb)
            {
                float tw = thumb.width, th = thumb.height;
                var r = new Rect(W - ix - tw - 16, iy + 16, tw + 16, th + 84);
                GUI.Box(r, GUIContent.none);
                GUI.DrawTexture(new Rect(r.x + 8, r.y + 8, tw, th), thumb);
                GUI.Label(new Rect(r.x + 8, r.y + th + 12, tw, 70), reviewText, small);
            }
        }
    }
}
