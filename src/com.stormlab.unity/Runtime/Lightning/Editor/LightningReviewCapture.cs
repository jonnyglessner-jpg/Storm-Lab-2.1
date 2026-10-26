using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab.Lightning.EditorTools
{
    public static class LightningReviewCapture
    {
        [MenuItem("StormLab/Lightning/Capture Review Frames")]
        public static void Capture()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Capture review frames outside Play mode.");
            var controls = UnityEngine.Object.FindAnyObjectByType<LightningPreviewControls>();
            if (!controls) { LightningPreview.Open(); controls = UnityEngine.Object.FindAnyObjectByType<LightningPreviewControls>(); }
            var rig = controls.lightning;
            LightningPreview.ApplyLook(controls);
            var camera = controls.previewCamera;
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../validation/lightning/out/review"));
            Directory.CreateDirectory(folder);
            bool originalThunder = rig.thunder; rig.thunder = false;
            var events = new List<FlashEvent>();
            LightningSchedule.Query(rig.settings, -1e-6, 600, events);
            var e = events.Find(f => f.kind == FlashKind.NegativeGround);
            var positions = LightningFlash.Create(e, rig.settings, System.Numerics.Vector3.Zero, 0).Channels[0].points;
            var end = positions[128];
            try
            {
                foreach (float km in new[] { 8f, 16f, 30f })
                {
                    camera.transform.position = new Vector3(end.X, 100, end.Z - km * 1000);
                    camera.transform.LookAt(new Vector3(end.X, 4200, end.Z));
                    foreach (float age in new[] { .02f, .036f, .055f, .12f })
                    {
                        rig.ResetPlayback(); rig.SampleAt(e.time + age);
                        Save(camera, Path.Combine(folder, $"negative_{km:0}km_{age * 1000:000}ms.png"));
                    }
                }
                foreach (FlashKind kind in new[] { FlashKind.InCloud, FlashKind.PositiveGround })
                {
                    var flashEvent = events.Find(f => f.kind == kind);
                    var flash = LightningFlash.Create(flashEvent, rig.settings, System.Numerics.Vector3.Zero, 0);
                    var point = flash.Channels[0].points[128];
                    camera.transform.position = new Vector3(point.X, 100, point.Z - 16000);
                    camera.transform.LookAt(new Vector3(point.X, 4200, point.Z));
                    rig.ResetPlayback(); rig.SampleAt(flashEvent.time + .036);
                    Save(camera, Path.Combine(folder, kind + "_16km_peak.png"));
                }
                Debug.Log("Lightning review frames: " + folder);
            }
            finally { rig.ResetPlayback(); rig.thunder = originalThunder; controls.FrameCamera(); }
        }
        static void Save(Camera camera, string path)
        {
            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            var oldTarget = camera.targetTexture;
            camera.targetTexture = rt;
            UnityEngine.Object.FindAnyObjectByType<LightningPreviewControls>().lightning.PrepareForCamera(camera);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(camera, request)) RenderPipeline.SubmitRenderRequest(camera, request);
            else { camera.targetTexture = rt; camera.Render(); camera.targetTexture = null; }
            camera.targetTexture = oldTarget;
            var previous = RenderTexture.active; RenderTexture.active = rt;
            var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); image.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
