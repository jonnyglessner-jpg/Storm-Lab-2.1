using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab.Lightning
{
    /// <summary>Only attached by the standalone preview. No dependency on storm rendering.</summary>
    public sealed class LightningPreviewControls : MonoBehaviour
    {
        public LightningSystem lightning;
        public Camera previewCamera;
        public bool holding = true;
        public float cameraDistanceM = 16000;
        public float playbackSpeed = 1;
        public int selected;
        public bool cloudConnected;
        readonly List<FlashEvent> catalogue = new List<FlashEvent>();
        double cursor;
        Vector3 target;
        string mode = "Frozen first return stroke";
        readonly List<AudioListener> otherListeners = new List<AudioListener>();
        readonly List<Camera> otherCameras = new List<Camera>();
        readonly List<LightningPreviewControls> otherControls = new List<LightningPreviewControls>();

        void Start()
        {
            if (cloudConnected)
            {
                foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>())
                    if (camera.enabled && camera != previewCamera) { otherCameras.Add(camera); camera.enabled=false; }
                foreach (var control in UnityEngine.Object.FindObjectsByType<LightningPreviewControls>())
                    if (control.enabled && control != this) { otherControls.Add(control); control.enabled=false; }
            }
            foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>())
                if (listener.enabled && listener.gameObject != previewCamera.gameObject)
                { otherListeners.Add(listener); listener.enabled = false; }
            BuildCatalogue(); Hold(Math.Max(0, catalogue.FindIndex(e => e.kind == FlashKind.NegativeGround)));
        }
        void OnDestroy()
        {
            foreach (var listener in otherListeners) if (listener) listener.enabled=true;
            foreach (var camera in otherCameras) if (camera) camera.enabled=true;
            foreach (var control in otherControls) if (control) control.enabled=true;
        }
        void BuildCatalogue()
        {
            LightningSchedule.Query(lightning.settings, -1e-6, 600, catalogue);
            if (catalogue.Count == 0) throw new InvalidOperationException("Preview needs a nonzero flash rate.");
        }
        public void Hold(int index)
        {
            if (catalogue.Count == 0) BuildCatalogue();
            selected = (index + catalogue.Count) % catalogue.Count;
            holding = true; mode = "Frozen first return stroke";
            cursor = catalogue[selected].time + LightningFlash.LeaderSeconds + .001;
            lightning.ResetPlayback(); lightning.SampleAt(cursor);
            Vector3 centre=lightning.transform.position; centre.y=lightning.groundY;
            if(lightning.CellCentreAtTime!=null) centre=lightning.CellCentreAtTime(catalogue[selected].time);
            // Frame the geometry actually accepted by the cloud adapter, including displaced anvil origins.
            if (lightning.TryGetFlash(catalogue[selected].id, out var flash))
            {
                var end = flash.Channels[0].points[128];
                var start = flash.Channels[0].points[0];
                target = new Vector3((start.X + end.X) * .5f, lightning.groundY, (start.Z + end.Z) * .5f);
            }
            else target = centre;
            FrameCamera();
        }
        public void FrameCamera()
        {
            previewCamera.transform.position = target + new Vector3(0, 100, -cameraDistanceM);
            previewCamera.transform.LookAt(target + Vector3.up * 4200);
        }
        void Update()
        {
            if (!lightning || !previewCamera) return;
            if (!holding) cursor += Time.unscaledDeltaTime * playbackSpeed;
            lightning.timeScale = playbackSpeed;
            lightning.SampleAt(cursor);
            lightning.PrepareForCamera(previewCamera);
        }
        void Replay(float speed)
        {
            holding = false; playbackSpeed = speed;
            cursor = Math.Max(0, catalogue[selected].time - .35);
            lightning.ResetPlayback();
            mode = speed == 1 ? "Live timing / thunder" : "Slow motion (audio muted)";
        }
        void OnGUI()
        {
            if (!lightning || catalogue.Count == 0) return;
            float scale = Math.Max(1, Screen.width / 1600f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUILayout.BeginArea(new Rect(18, 18, 370, 310), GUI.skin.box);
            GUILayout.Label("LIGHTNING / STANDALONE REVIEW");
            GUILayout.Label(mode);
            GUILayout.Label($"{catalogue[selected].kind}  |  event {catalogue[selected].id}  |  {cameraDistanceM / 1000:0} km");
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Hold peak", GUILayout.Height(30))) Hold(selected);
            if (GUILayout.Button("Next flash", GUILayout.Height(30))) Hold(selected + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Negative ground", GUILayout.Height(28))) SelectKind(FlashKind.NegativeGround);
            if (GUILayout.Button("Positive ground", GUILayout.Height(28))) SelectKind(FlashKind.PositiveGround);
            if (GUILayout.Button("In-cloud", GUILayout.Height(28))) SelectKind(FlashKind.InCloud);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Replay 1x", GUILayout.Height(30))) Replay(1);
            if (GUILayout.Button("Replay 0.1x", GUILayout.Height(30))) Replay(.1f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            foreach (float km in new[] { 8f, 16f, 30f })
                if (GUILayout.Button(km + " km", GUILayout.Height(28))) { cameraDistanceM = km * 1000; FrameCamera(); }
            GUILayout.EndHorizontal();
            GUILayout.Label("Channel width (m)  " + lightning.channelWidthM.ToString("0.0"));
            float width = GUILayout.HorizontalSlider(lightning.channelWidthM, .5f, 16);
            if (Math.Abs(width - lightning.channelWidthM) > .001f) { lightning.channelWidthM = width; Hold(selected); }
            GUILayout.Label(cloudConnected ? "Cloud illumination and channel occlusion enabled." : "Cloud scattering and occlusion are not connected.");
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
        void SelectKind(FlashKind kind)
        {
            int index = catalogue.FindIndex(e => e.kind == kind);
            if (index >= 0) Hold(index);
        }
    }
}
