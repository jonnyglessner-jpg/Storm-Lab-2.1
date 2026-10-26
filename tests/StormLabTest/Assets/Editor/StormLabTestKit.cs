// Shared set-up for the test project's editor tools (from StormLab 2.0's SkinTests): the HDRP asset the tests render
// with, folders, and an HDRP camera.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class StormLabTestKit
{
    public static string ProjectDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    public static string RepoRoot => Path.GetFullPath(Path.Combine(ProjectDir, "..", ".."));

    internal static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    public const string PipelinePath = "Assets/Settings/SkinTest_HDRP.asset", StandaloneProfilePath = "Assets/Settings/SkinTest_Standalone.asset";

    /// <summary>
    /// The HDRP asset the tests render with. Its volume profile is the standalone look (StormRenderer.UseStandaloneLook):
    /// HDRP's tonemapping, bloom, fog and sky off, so the storm's own display curve and backdrop reach the screen as the
    /// web build draws them. A scene that tests the game's look adds its own global volume over it (the test map).
    /// </summary>
    internal static HDRenderPipelineAsset EnsurePipeline()
    {
        if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
        EnsureFolder("Assets/Settings");
        var rp = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelinePath);
        if (!rp) { rp = ScriptableObject.CreateInstance<HDRenderPipelineAsset>(); AssetDatabase.CreateAsset(rp, PipelinePath); }
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(StandaloneProfilePath);
        if (!profile) { profile = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(profile, StandaloneProfilePath); }
        T Get<T>() where T : VolumeComponent
        {
            if (!profile.TryGet<T>(out var c)) { c = profile.Add<T>(true); c.name = typeof(T).Name; AssetDatabase.AddObjectToAsset(c, profile); }
            return c;
        }
        var tm = Get<Tonemapping>(); tm.mode.Override(TonemappingMode.None);
        var bloom = Get<Bloom>(); bloom.intensity.Override(0f);
        var fog = Get<Fog>(); fog.enabled.Override(false);
        var ex = Get<Exposure>(); ex.mode.Override(ExposureMode.Fixed); ex.fixedExposure.Override(0f);
        var env = Get<VisualEnvironment>(); env.skyType.Override(0); env.cloudType.Override(0);
        EditorUtility.SetDirty(profile);
        rp.volumeProfile = profile;
        EditorUtility.SetDirty(rp);
        GraphicsSettings.defaultRenderPipeline = rp;
        int q = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = rp; }
        QualitySettings.SetQualityLevel(q, false);
        // HDRP's global settings (default frame settings and resources): its own Ensure, which the HDRP wizard calls
        Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineGlobalSettings, Unity.RenderPipelines.HighDefinition.Runtime")
            ?.GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, new object[] { true });
        AssetDatabase.SaveAssets();
        return rp;
    }

    /// <summary>An HDRP camera for the test scenes: cleared to black (the storm's backdrop covers it), no antialiasing.</summary>
    internal static HDAdditionalCameraData SetupCamera(GameObject camGo)
    {
        var hd = camGo.GetComponent<HDAdditionalCameraData>();
        if (!hd) hd = camGo.AddComponent<HDAdditionalCameraData>();
        hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hd.backgroundColorHDR = Color.black;
        hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
        return hd;
    }

    /// <summary>Wait for the GPU (a tiny readback).</summary>
    internal static void Sync() { var b = new ComputeBuffer(1, 4); var a = new int[1]; b.GetData(a); b.Release(); }

    /// <summary>Render `frames` frames of a camera into a w x h target and save the last as PNG.</summary>
    internal static void Render(Camera cam, string path, int w, int h, int frames, Action beforeFrame = null)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
        rt.Create();
        for (int f = 0; f < frames; f++)
        {
            beforeFrame?.Invoke();
            var req = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
            else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
        }
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
        RenderTexture.active = prev;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
    }
}
