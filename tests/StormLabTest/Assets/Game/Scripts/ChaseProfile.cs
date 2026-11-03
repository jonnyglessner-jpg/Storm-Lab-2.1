// The player's profile, kept between games: credits, the gear owned, photos taken and the best chases. Saved as JSON in
// Application.persistentDataPath/StormLab/profile.json (ChaseProfile.Path); a missing or unreadable file starts a new
// chaser with the starter gear and no credits.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StormLab.Game
{
    [Serializable]
    public sealed class ChaseProfile
    {
        public int credits;
        public List<string> owned = new List<string>();
        public int photos, chases;
        public int bestChaseCredits;
        public int tornadoesPhotographed;
        public double mesonetSeconds;
        public int creditsEarned;

        // the career (user, 2026-11-02): day by day, saved when the chaser sleeps
        /// <summary>the career's seed (each day's storms come from it and the day), the day number, and whether a career is on</summary>
        public int careerSeed = 1, day = 1; public bool careerStarted;
        /// <summary>the sanity bar (0..1) and the hours awake since the last sleep (sped up near tornadoes)</summary>
        public float sanity = 1f, awakeHours;
        /// <summary>the town whose truck stop the chaser spawned at last (the morning's default choice)</summary>
        public string spawnTown = "";
        /// <summary>every vehicle's fuel and condition, carried between days</summary>
        public List<VehicleState> vehicles = new List<VehicleState>();
        public int motelNights, carCampNights, passOuts, repairs; public float fuelBought;

        [Serializable]
        public sealed class VehicleState
        {
            public string key;
            /// <summary>gallons in the tank (-1: not yet driven, filled on first use)</summary>
            public float fuelGal = -1f;
            /// <summary>mechanical condition 0..1</summary>
            public float condition = 1f;
            public float odometerKm;
        }

        public VehicleState Vehicle(string key)
        {
            foreach (var v in vehicles) if (v.key == key) return v;
            var n = new VehicleState { key = key };
            vehicles.Add(n);
            return n;
        }

        /// <summary>A career from day one: rested, every tank full, the seed set.</summary>
        public void StartCareer(int seed)
        {
            careerSeed = seed; day = 1; careerStarted = true; sanity = 1f; awakeHours = 0f; spawnTown = "";
            foreach (var v in vehicles) { v.fuelGal = -1f; v.condition = 1f; }
            Save();
        }

        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, "StormLab", "profile.json");

        static ChaseProfile current;
        public static ChaseProfile Current => current ??= Load();

        /// <summary>raised when the credits change (the HUD flashes the amount)</summary>
        public static event Action<int> CreditsChanged;

        public bool Owns(string gearKey) => owned.Contains(gearKey);

        public bool CanAfford(int price) => credits >= price;

        /// <summary>Buy a catalog item: false when it is owned already, needs something not owned, or costs more than the credits held.</summary>
        public bool Buy(ChaseGear g)
        {
            if (g == null || Owns(g.key) || !g.Available || !CanAfford(g.price)) return false;
            credits -= g.price; owned.Add(g.key);
            Save();
            CreditsChanged?.Invoke(-g.price);
            return true;
        }

        public void Earn(int amount)
        {
            if (amount <= 0) return;
            credits += amount; creditsEarned += amount;
            CreditsChanged?.Invoke(amount);
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonUtility.ToJson(this, true));
            }
            catch (Exception e) { Debug.LogWarning($"ChaseProfile: could not save {Path}: {e.Message}"); }
        }

        public static ChaseProfile Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var p = JsonUtility.FromJson<ChaseProfile>(File.ReadAllText(Path));
                    if (p != null) { p.owned ??= new List<string>(); return p; }
                }
            }
            catch (Exception e) { Debug.LogWarning($"ChaseProfile: could not read {Path}: {e.Message}"); }
            return new ChaseProfile();
        }

        /// <summary>A fresh chaser (the menu's reset, and the checks).</summary>
        public static void Reset() { current = new ChaseProfile(); current.Save(); }
    }
}
