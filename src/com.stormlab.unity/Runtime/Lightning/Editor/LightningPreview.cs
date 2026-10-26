using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StormLab.Lightning.EditorTools
{
    public static class LightningPreview
    {
        [MenuItem("StormLab/Lightning/Open Independent Preview")]
        public static void Open()
        {
            var existing = UnityEngine.Object.FindAnyObjectByType<LightningPreviewControls>();
            if (existing) { ApplyLook(existing); Selection.activeGameObject = existing.gameObject; return; }
            // Unity refuses additive creation beside an unsaved scene. Reuse that scene without removing its objects.
            var current = SceneManager.GetActiveScene();
            var scene = string.IsNullOrEmpty(current.path) ? current :
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            if (scene.rootCount == 0) scene.name = "Independent Lightning Preview";
            var rig = new GameObject("Lightning preview (metres)").AddComponent<LightningSystem>();
            rig.settings.flashesPerMinute = 24;
            rig.settings.groundFraction = .65f;
            rig.showInCloudChannels = true;
            rig.channelWidthM = 3;
            rig.advanceClock = false;
            var camera = new GameObject("Lightning preview camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 100, -16000);
            camera.transform.LookAt(new Vector3(0, 4500, 0));
            camera.nearClipPlane = 1; camera.farClipPlane = 100000;
            camera.fieldOfView = 58; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.004f, .006f, .012f);
            camera.allowHDR = true;
            camera.depth = 100;
            camera.gameObject.AddComponent<AudioListener>();
            rig.observer = camera.transform;
            var controls = rig.gameObject.AddComponent<LightningPreviewControls>();
            controls.lightning = rig; controls.previewCamera = camera;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Preview ground"; ground.transform.localScale = new Vector3(6000, 1, 6000);
            ApplyLook(controls);
            Selection.activeGameObject = rig.gameObject;
            Debug.Log("Lightning preview opened additively. Use this scene alone in Play mode for an isolated view. " +
                "In-cloud channels are exposed for inspection; cloud scattering is a separate integration.");
        }

        internal static void ApplyLook(LightningPreviewControls controls)
        {
            var ground = GameObject.Find("Preview ground");
            if (!ground) return;
            var renderer = ground.GetComponent<Renderer>();
            if (renderer.sharedMaterial && renderer.sharedMaterial.name == "Lightning preview soil") return;
            var material = new Material(Shader.Find("HDRP/Lit"));
            material.name = "Lightning preview soil";
            material.SetColor("_BaseColor", new Color(.012f, .018f, .026f));
            material.SetFloat("_Smoothness", .05f);
            renderer.sharedMaterial = material;
        }
    }
}
