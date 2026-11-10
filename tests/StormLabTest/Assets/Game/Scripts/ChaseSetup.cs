// The chase being set up or played: the main menu's choices (map, vehicle, gear, seed), carried across the map's scene
// load as statics, and whether a chase is running. ChaseGame reads it at Start on every map.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab.Game
{
    public static class ChaseSetup
    {
        public enum State { Menu, Chase, FreeRoam }

        public static State state = State.Menu;
        public static string map = "plains", vehicle = "halfton", cameraBody = "cam_compact", lens = "lens_kit";
        public static bool mountMesonet;
        public static int seed = 1;
        /// <summary>the time of day the chase starts, hours (24 h clock), from the seed (BeginChase); the game's hour runs it on</summary>
        public static float startHour = 17f;
        /// <summary>the chase's running tally: credits earned this chase, photos, mesonet seconds</summary>
        public static int chaseCredits, chasePhotos; public static double chaseMesonetSeconds;
        /// <summary>a chase that ended (the summary shows it until the menu is used again)</summary>
        public static bool chaseOver;
        /// <summary>the clock of day in the chase, hours (the morning forecast holds it at 8; the day's storms set it to startHour)</summary>
        public static float hour = 8f;
        /// <summary>the morning: the forecast is up and the day's storms are not out yet</summary>
        public static bool morning;
        /// <summary>the day's storms were spawned (and have been chased, or missed)</summary>
        public static bool dayStarted;
        /// <summary>dev mode (F3, user 2026-11-02): the sandbox keys back in a career (free camera, spot jumps, V, N, [ ], the Tab panel)</summary>
        public static bool devMode;

        /// <summary>The seed of a career day's storms.</summary>
        public static int DaySeed(int careerSeed, int day) => (careerSeed * 131 + day * 7919) % 999983 + 1;

        /// <summary>Start the chase the menu set up (the caller loads the map's scene when it is not the one open).</summary>
        public static void BeginChase()
        {
            state = State.Chase; chaseOver = false; morning = true; dayStarted = false; hour = 8f;
            chaseCredits = 0; chasePhotos = 0; chaseMesonetSeconds = 0;
            if (!ChaseCatalog.GearItem(cameraBody)?.Owned ?? true) cameraBody = "cam_compact";
            if (!ChaseCatalog.GearItem(lens)?.Owned ?? true) lens = "lens_kit";
            if (!ChaseProfile.Current.Owns("mesonet")) mountMesonet = false;
            startHour = StartHourFor(DaySeed(ChaseProfile.Current.careerSeed, ChaseProfile.Current.day));
            ChaseProfile.Current.chases++;
        }

        /// <summary>When a seed's chase starts (user, 2026-11-02): noon half the time (11:30 to 13:30), afternoon 35 % (14:00
        /// to 17:00), dusk 15 % (18:00 to 19:30, the sun down at 19:06 on the panel's clock: the hour runs into night).</summary>
        public static float StartHourFor(int seed)
        {
            var rng = new System.Random(seed * 7919 + 101);
            double pick = rng.NextDouble(), f = rng.NextDouble();
            if (pick < 0.50) return (float)(11.5 + 2.0 * f);
            if (pick < 0.85) return (float)(14.0 + 3.0 * f);
            return (float)(18.0 + 1.5 * f);
        }

        public static string TimeOfDayName(float hour) => hour < 13.75f ? "noon" : hour < 17.75f ? "afternoon" : "dusk";
        public static string ClockText(float hour) { int h = Mathf.FloorToInt(hour), m = Mathf.FloorToInt((hour - h) * 60f); return $"{h:00}:{m:00}"; }

        public static void BeginFreeRoam() { state = State.FreeRoam; chaseOver = false; }

        public static void BackToMenu() { state = State.Menu; }

        public static void RandomSeed() { seed = UnityEngine.Random.Range(1, 100000); }

        public static ChaseMap Map => ChaseCatalog.Map(map);
        public static ChaseVehicle Vehicle => ChaseCatalog.Vehicle(vehicle);
        public static ChaseGear CameraBody => ChaseCatalog.GearItem(cameraBody) ?? ChaseCatalog.GearItem("cam_compact");
        public static ChaseGear Lens => ChaseCatalog.GearItem(lens) ?? ChaseCatalog.GearItem("lens_kit");
    }
}
