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
