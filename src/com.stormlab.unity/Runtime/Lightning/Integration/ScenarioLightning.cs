using UnityEngine;

namespace StormLab.Lightning
{
    /// <summary>Gives every storm a game scenario creates (StormScenarioRunner) its lightning, as 2.0's designed storms got
    /// theirs on spawn. The storm assembly stays independent of this one: it only raises StormCreated.</summary>
    public static class ScenarioLightning
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            // remove first so entering Play with domain reload off cannot hook it twice
            StormScenarioRunner.StormCreated -= Connect;
            StormScenarioRunner.StormCreated += Connect;
        }

        public static void Connect(StormRenderer storm)
        {
            if (!storm || storm.GetComponent<StormLightning>()) return;
            var sl = storm.gameObject.AddComponent<StormLightning>();
            sl.region = StormTornadoes.RegionOf(storm.preset);
        }
    }
}
