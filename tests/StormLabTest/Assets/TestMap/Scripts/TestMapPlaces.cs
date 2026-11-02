// The towns' places for the game (user, 2026-11-02): each town's name and extent, its traffic light, its truck stop
// (the pump islands, the diesel lanes, the marked overnight spot, its fuel prices), its motel (the marked guest spot,
// its rate) and its mechanic (the marked service spot), from Resources/TestMapPlaces.json, which tools/testmap/world.py
// writes (towns.py lays them out). World metres, x east, z north, y the ground.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab.TestMap
{
    [Serializable] public class PlaceSpot { public float x, y, z, yaw; public Vector3 Position => new Vector3(x, y, z); }
    [Serializable] public class PlaceBox { public float x0, z0, x1, z1; public bool Contains(Vector3 p) => p.x >= x0 && p.x <= x1 && p.z >= z0 && p.z <= z1; }
    [Serializable] public class PlaceLight { public float x, y, z, nsHalf, ewHalf; public string main, cross; public Vector3 Position => new Vector3(x, y, z); }
    [Serializable]
    public class PlaceGas
    {
        public string name; public float x, y, z, yaw; public PlaceSpot canopy; public PlaceSpot[] pumps, diesel; public PlaceSpot sleep;
        public float regular, premium, dieselPrice;
        public Vector3 Position => new Vector3(x, y, z);
        /// <summary>the price a gallon of a grade, credits (dollars)</summary>
        public float Price(FuelGrade g) => g == FuelGrade.Diesel ? dieselPrice : g == FuelGrade.Premium ? premium : regular;
    }
    [Serializable] public class PlaceMotel { public string name; public float x, y, z, yaw; public PlaceSpot sleep; public int rate; public Vector3 Position => new Vector3(x, y, z); }
    [Serializable] public class PlaceMechanic { public string name; public float x, y, z, yaw; public PlaceSpot spot; public float rate; public Vector3 Position => new Vector3(x, y, z); }
    [Serializable]
    public class PlaceTown
    {
        public string key, name; public int pop; public PlaceSpot centre; public PlaceBox box; public PlaceLight light;
        public PlaceGas gas; public PlaceMotel motel; public PlaceMechanic mechanic;
    }
    [Serializable] class PlacesData { public PlaceTown[] towns; }

    public enum FuelGrade { Regular, Premium, Diesel }

    public static class TestMapPlaces
    {
        static Dictionary<string, PlaceTown[]> cache = new Dictionary<string, PlaceTown[]>();

        /// <summary>The towns of a map (empty for a map without them; the plains map's resource is TestMapPlaces).</summary>
        public static PlaceTown[] Load(string nav = "TestMapNav")
        {
            string res = nav.Replace("Nav", "Places");
            if (cache.TryGetValue(res, out var t)) return t;
            var text = Resources.Load<TextAsset>(res);
            t = text ? JsonUtility.FromJson<PlacesData>(text.text)?.towns ?? new PlaceTown[0] : new PlaceTown[0];
            cache[res] = t;
            return t;
        }

        public static PlaceTown TownOf(PlaceTown[] towns, string key) { foreach (var t in towns) if (t.key == key) return t; return towns.Length > 0 ? towns[0] : null; }

        /// <summary>the town whose extent (plus margin m) holds a world point, or null</summary>
        public static PlaceTown TownAt(PlaceTown[] towns, Vector3 p, float margin = 0f)
        {
            foreach (var t in towns)
                if (p.x >= t.box.x0 - margin && p.x <= t.box.x1 + margin && p.z >= t.box.z0 - margin && p.z <= t.box.z1 + margin) return t;
            return null;
        }

        public static PlaceTown NearestTown(PlaceTown[] towns, Vector3 p)
        {
            PlaceTown best = null; float bd = float.MaxValue;
            foreach (var t in towns) { float d = Flat(t.centre.Position, p); if (d < bd) { bd = d; best = t; } }
            return best;
        }

        public static float Flat(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }

        /// <summary>The nearest pump island (petrol or diesel) of any town and its distance, m.</summary>
        public static PlaceGas NearestPump(PlaceTown[] towns, Vector3 p, out float distance, out bool dieselLane)
        {
            PlaceGas best = null; distance = float.MaxValue; dieselLane = false;
            foreach (var t in towns)
            {
                if (t.gas == null) continue;
                foreach (var s in t.gas.pumps) { float d = Flat(s.Position, p); if (d < distance) { distance = d; best = t.gas; dieselLane = false; } }
                foreach (var s in t.gas.diesel) { float d = Flat(s.Position, p); if (d < distance) { distance = d; best = t.gas; dieselLane = true; } }
            }
            return best;
        }
    }
}
