using UnityEditor;
using UnityEngine;

namespace StormLab.Lightning.EditorTools
{
    [InitializeOnLoad]
    static class LightningEditorLifecycle
    {
        static LightningEditorLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                    Clear();
            };
            // Also repair orphaned channels left by older preview versions.
            EditorApplication.delayCall += () => { if (!EditorApplication.isPlayingOrWillChangePlaymode) Clear(); };
        }

        static void Clear()
        {
            foreach (var rig in Object.FindObjectsByType<LightningSystem>(FindObjectsInactive.Include))
                rig.ResetPlayback();
        }
    }
}
