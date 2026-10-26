using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace StormLab.Lightning.EditorTools
{
    public static class LightningLifecycleChecks
    {
        [MenuItem("StormLab/Lightning/Check Channel Lifecycle")]
        public static void Check()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Run lifecycle checks outside Play mode.");
            int checks = 0;
            var root = new GameObject("Lightning lifecycle fixture");
            try
            {
                var rig = root.AddComponent<LightningSystem>();
                rig.advanceClock = false; rig.thunder = false;
                var foreign = new GameObject("Authored child"); foreign.transform.SetParent(root.transform);
                var events = new List<FlashEvent>();
                LightningSchedule.Query(rig.settings, 0, 120, events);
                double peak = events[0].time + .036;
                var active = (IDictionary)typeof(LightningSystem).GetField("active", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(rig);
                rig.SampleAt(peak);
                Require(root.GetComponentsInChildren<LineRenderer>().Length > 0, "Peak creates channels", ref checks);
                foreach (var marker in root.GetComponentsInChildren<LightningGeneratedVisual>())
                    Require((marker.gameObject.hideFlags & HideFlags.DontSave) == HideFlags.DontSave, "Generated root is transient", ref checks);
                // Reproduce domain reload: Unity retains the objects while managed tracking disappears.
                active.Clear();
                rig.ResetPlayback();
                Require(root.GetComponentsInChildren<LineRenderer>(true).Length == 0, "Reset removes untracked channels", ref checks);
                Require(foreign && root.transform.childCount == 1, "Authored child survives cleanup", ref checks);
                for (int i = 0; i < 4; i++) { rig.SampleAt(peak); rig.ResetPlayback(); }
                Require(root.transform.childCount == 1, "Repeated seeks do not accumulate roots", ref checks);
                rig.SampleAt(peak); rig.SampleAt(peak + 2);
                Require(root.GetComponentsInChildren<LineRenderer>(true).Length == 0, "Expired flashes leave no channel", ref checks);
                // Migration path for previews saved before ownership markers were added.
                rig.SampleAt(peak);
                foreach (var marker in root.GetComponentsInChildren<LightningGeneratedVisual>())
                    UnityEngine.Object.DestroyImmediate(marker);
                active.Clear(); rig.ResetPlayback();
                Require(root.transform.childCount == 1, "Legacy orphan removed", ref checks);
                int resolverCalls=0;
                rig.ResolveCloudAnchors=e=> { resolverCalls++; return null; };
                rig.SampleAt(peak); rig.SampleAt(peak+.001);
                Require(resolverCalls==1,"Rejected origin is not retried every frame",ref checks);
                Require(root.GetComponentsInChildren<LineRenderer>(true).Length==0 && rig.Illumination.Count==0,
                    "No channel or illumination without a cloud origin",ref checks);
                rig.ResolveCloudAnchors=null;
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../validation/lightning/out"));
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "lifecycle-checks.txt"), $"PASS: {checks} channel lifecycle checks");
                Debug.Log($"PASS: {checks} channel lifecycle checks");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void Require(bool condition, string message, ref int checks)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        [MenuItem("StormLab/Lightning/Audit Channel Lifecycle")]
        public static void Audit()
        {
            var report=new List<string>();
            foreach(var rig in UnityEngine.Object.FindObjectsByType<LightningSystem>())
            {
                var active=(IDictionary)typeof(LightningSystem).GetField("active",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(rig);
                report.Add($"Rig: {rig.name}; tracked flashes={active.Count}; child roots={rig.transform.childCount}; time={rig.playbackSeconds}");
                foreach(Transform child in rig.transform)
                {
                    var lines=child.GetComponentsInChildren<LineRenderer>(true);
                    if(lines.Length==0) continue;
                    report.Add($"  {child.name}; active={child.gameObject.activeInHierarchy}; flags={child.hideFlags}; lines={lines.Length}; first enabled={lines[0].enabled}; colour={lines[0].startColor}; width={lines[0].widthMultiplier}");
                }
            }
            string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../validation/lightning/out"));
            Directory.CreateDirectory(folder); File.WriteAllLines(Path.Combine(folder,"lifecycle-audit.txt"),report);
            Debug.Log(string.Join("\n",report));
        }
    }
}
