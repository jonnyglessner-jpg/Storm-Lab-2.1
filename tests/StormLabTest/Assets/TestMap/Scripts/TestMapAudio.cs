// The test map's sound: the storms' tornado wind, rain, hail and thunder (StormAudio) at the truck, or at the camera in
// free flight; the truck's engine and tyres (TestMapCarAudio); the plains ambience, breeze and birds (CountryAmbience).
// TestMapWorld adds it at Start. 0 mutes and unmutes everything.
using StormLab.Audio;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    [DefaultExecutionOrder(95)]
    public class TestMapAudio : MonoBehaviour
    {
        public TestMapWorld world;
        public StormAudio Storm { get; private set; }
        public CountryAmbience Ambience { get; private set; }
        public TestMapCarAudio Car { get; private set; }

        void Start()
        {
            if (!world) world = GetComponent<TestMapWorld>();
            var root = new GameObject("Audio").transform;
            root.SetParent(transform, false);
            Storm = root.gameObject.AddComponent<StormAudio>();
            Storm.Storms = world.LiveStorms;
            Ambience = root.gameObject.AddComponent<CountryAmbience>();
            Ambience.storm = Storm;
            if (world.car && !world.car.GetComponent<TestMapCarAudio>()) Car = world.car.gameObject.AddComponent<TestMapCarAudio>();
        }

        void Update()
        {
            // the wind and rain at the truck, or where the free camera is
            var rig = world.cameraRig;
            Storm.player = rig && rig.mode == TestMapCamera.Mode.Free ? rig.transform : world.car ? world.car.transform : null;
            // 0 mutes (M was the mute until 2026-11-02; the user gave M to the map)
            var kb = Game.ChaseGame.InputBlocked ? null : Keyboard.current;
            if (kb != null && kb.digit0Key.wasPressedThisFrame) AudioListener.volume = AudioListener.volume > 0f ? 0f : 1f;
        }
    }
}
