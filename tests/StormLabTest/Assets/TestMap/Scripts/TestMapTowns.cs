// The towns whose lights glow orange on the storms' bases at night (StormRenderer.Towns; user, 2026-10-28), by the map's
// radar site. Each town's glow is its population / 15,000 (2020 census) and its lit area 2.5 km x sqrt(glow) across.
// Positions are latitude and longitude about the map's own reference, on a flat local projection (good to a few hundred
// metres over these distances): the plains map's radar site (KDDC; the map lies north-east of Dodge City), and on the
// real-data maps their downtown, the first spawn spot (Natchez, Effingham).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab.TestMap
{
    public static class TestMapTowns
    {
        struct Town { public string name; public double lat, lon; public float pop; }
        struct MapTowns { public bool fromSpot; public double lat, lon; public Town[] towns; }

        static Town T(string name, double lat, double lon, float pop) => new Town { name = name, lat = lat, lon = lon, pop = pop };

        static readonly Dictionary<string, MapTowns> Maps = new Dictionary<string, MapTowns>
        {
            // the plains map: Dodge City just off its south-west corner, Garden City 80 km west, the county towns round it
            ["KDDC"] = new MapTowns
            {
                fromSpot = false, lat = 37.76083, lon = -99.96889, towns = new[]
                {
                    T("Dodge City", 37.7528, -100.0171, 27800), T("Garden City", 37.9717, -100.8727, 28150),
                    T("Kinsley", 37.9231, -99.4096, 1450), T("Spearville", 37.8484, -99.7551, 800),
                    T("Cimarron", 37.8067, -100.3482, 2200), T("Jetmore", 38.0845, -99.8935, 850),
                    T("Ford", 37.6364, -99.7529, 200), T("Bucklin", 37.5475, -99.6343, 700),
                }
            },
            // Natchez and Vidalia across the river, Ferriday in the delta
            ["KDGX"] = new MapTowns
            {
                fromSpot = true, lat = 31.5604, lon = -91.4032, towns = new[]
                {
                    T("Natchez", 31.5604, -91.4032, 14500), T("Vidalia", 31.5654, -91.4257, 3800),
                    T("Ferriday", 31.6304, -91.5543, 3200), T("Woodville", 31.1046, -91.2996, 1000),
                }
            },
            // Effingham, Teutopolis and Altamont beside it, Mattoon and Charleston 40 km north, Vandalia to the south-west
            ["KILX"] = new MapTowns
            {
                fromSpot = true, lat = 39.1200, lon = -88.5434, towns = new[]
                {
                    T("Effingham", 39.1200, -88.5434, 12600), T("Teutopolis", 39.1331, -88.4720, 1600),
                    T("Altamont", 39.0617, -88.7484, 2300), T("Mattoon", 39.4831, -88.3728, 17000),
                    T("Charleston", 39.4961, -88.1761, 17300), T("Vandalia", 38.9606, -89.0937, 6800),
                }
            },
        };

        /// <summary>Set StormRenderer.Towns for this map (none for a map not listed: the bases then take an even town light).</summary>
        public static void Register(TestMapWorld world, string nav)
        {
            StormRenderer.Towns.Clear();
            if (!world) return;
            var radar = TestMapRoads.Load(nav)?.radar;
            if (radar == null || string.IsNullOrEmpty(radar.name) || !Maps.TryGetValue(radar.name, out var map)) return;
            Vector3 anchor;
            if (map.fromSpot)
            {
                if (world.spots.Length == 0) return;
                anchor = world.spots[0].position;
            }
            else anchor = new Vector3(radar.x, radar.y, radar.z);
            double mx = 111320.0 * Math.Cos(map.lat * Math.PI / 180.0), my = 110950.0;
            foreach (var t in map.towns)
            {
                float glow = t.pop / 15000f;
                StormRenderer.Towns.Add(new StormRenderer.TownGlow
                {
                    position = anchor + new Vector3((float)((t.lon - map.lon) * mx), 0f, (float)((t.lat - map.lat) * my)),
                    radiusM = 2500f * Mathf.Sqrt(glow), glow = glow
                });
            }
        }
    }
}
