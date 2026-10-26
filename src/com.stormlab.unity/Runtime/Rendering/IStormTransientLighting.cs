using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab
{
    /// <summary>Optional local illumination supplied before a camera's cloud march (lightning). Keeps the storm independent
    /// of effects. Return true when a transient is visible; the renderer then bypasses checkerboard and temporal blending.</summary>
    public interface IStormTransientLighting
    {
        bool RecordLighting(CommandBuffer command, StormRenderer storm, Camera camera, ComputeShader march, int kernel);
    }
}
