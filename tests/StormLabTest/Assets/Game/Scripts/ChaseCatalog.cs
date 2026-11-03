// What a chaser can pick and buy: the maps, the vehicles and the gear (camera bodies, lenses, the mesonet mount, the
// laptop and phone that show the map, the drone and its upgrades), with their prices in credits, the game's currency. The catalog is fixed data; what the player owns is in ChaseProfile and
// what they took on this chase in ChaseSetup.
using System.Linq;
using StormLab.TestMap;
using UnityEngine;

namespace StormLab.Game
{
    public sealed class ChaseMap
    {
        public string key, name, blurb, scene, preset;
    }

    public sealed class ChaseVehicle
    {
        public string key, name, blurb;
        /// <summary>mass kg, total motor torque N m, top speed kph, side-on drag area m^2 (how much a tornado's wind pushes it)</summary>
        public float massKg, torqueNm, topKph, windAreaM2;
        public Color paint;
        /// <summary>what it burns (every truck stop sells all three; the vehicle takes only its own), its tank, gallons an hour at
        /// idle and at full throttle at its top speed, and what the mechanic charges per point of condition (credits)</summary>
        public FuelGrade fuel; public float tankGal, idleGph, fullGph, repairRate;
        public string FuelName => fuel == FuelGrade.Diesel ? "diesel" : fuel == FuelGrade.Premium ? "premium" : "regular unleaded";
        /// <summary>Hatchback: the pickup bed is hidden and the cab stretched; Dually: wider rear tyres</summary>
        public VehicleBody body;
    }

    public enum VehicleBody { Pickup, Hatchback, Dually }

    // Laptop: the map and radar in the cab; Phone: the map on foot (user, 2026-11-02: no map in first person until bought);
    // Drone: a free-flying camera with a range and a battery; DroneUpgrade: more range, more battery, the follow cam
    // that gives the third-person view (the chase camera behind the truck is the drone's, locked until it is owned; the
    // roof camera locked on the tornado is the truck's own and always there)
    public enum GearKind { CameraBody, Lens, Mesonet, Laptop, Phone, Drone, DroneUpgrade }

    public sealed class ChaseGear
    {
        public string key, name, blurb;
        public GearKind kind;
        /// <summary>credits; 0 is starter gear everyone owns</summary>
        public int price;
        /// <summary>an item that must be owned first (a drone upgrade needs the drone, a second upgrade the first)</summary>
        public string requires;
        // camera bodies
        /// <summary>ISO range the body offers, and how many credits its photos earn relative to the starter body</summary>
        public int isoMin, isoMax; public float payout = 1f;
        // lenses
        /// <summary>focal length range, mm (full-frame equivalent), and the widest aperture</summary>
        public float focalMin, focalMax, apertureMin;
        // the drone and its upgrades
        /// <summary>how far from the controller the drone flies, m, and how long its battery lasts in flight, minutes</summary>
        public float droneRangeM, droneMinutes;
        public bool Owned => price <= 0 || ChaseProfile.Current.Owns(key);
        /// <summary>what it needs is owned (or it needs nothing)</summary>
        public bool Available => string.IsNullOrEmpty(requires) || ChaseProfile.Current.Owns(requires);
    }

    public static class ChaseCatalog
    {
        public static readonly ChaseMap[] Maps =
        {
            new ChaseMap { key = "plains", name = "Kansas plains", blurb = "Dodge City to Larned: a full mile grid of gravel roads over open wheat and pivots. The classic chase country.", scene = "Assets/Scenes/TestMap.unity", preset = "plains" },
            new ChaseMap { key = "dixie", name = "Dixie Alley", blurb = "Natchez, Mississippi: winding roads through pine woods and river bottoms. The storms are rain-wrapped and the views are short.", scene = "Assets/Scenes/DixieMap.unity", preset = "dixie" },
            new ChaseMap { key = "midwest", name = "Midwest", blurb = "Effingham, Illinois: corn country on a county grid broken by woods and towns.", scene = "Assets/Scenes/MidwestMap.unity", preset = "ohio" },
        };

        public static readonly ChaseVehicle[] Vehicles =
        {
            new ChaseVehicle { key = "halfton", name = "Half-ton pickup", blurb = "The chaser's usual ride: quick enough, steady enough, room on the roof for a mesonet.",
                               massKg = 2300f, torqueNm = 2600f, topKph = 185f, windAreaM2 = 3.2f, paint = new Color(0.82f, 0.82f, 0.80f), body = VehicleBody.Pickup,
                               fuel = FuelGrade.Regular, tankGal = 26f, idleGph = 0.4f, fullGph = 6.0f, repairRate = 4f },
            new ChaseVehicle { key = "dually", name = "Heavy-duty dually", blurb = "A ton and a half heavier: slow off the line, but a tornado's inflow barely moves it.",
                               massKg = 3600f, torqueNm = 3600f, topKph = 160f, windAreaM2 = 3.8f, paint = new Color(0.45f, 0.08f, 0.07f), body = VehicleBody.Dually,
                               fuel = FuelGrade.Diesel, tankGal = 34f, idleGph = 0.5f, fullGph = 7.5f, repairRate = 6f },
            new ChaseVehicle { key = "hatch", name = "Rally hatchback", blurb = "Light and fast on gravel, and tossed about by any wind over 60 mph. No roof rack to speak of.",
                               massKg = 1350f, torqueNm = 2100f, topKph = 200f, windAreaM2 = 2.4f, paint = new Color(0.12f, 0.30f, 0.70f), body = VehicleBody.Hatchback,
                               fuel = FuelGrade.Premium, tankGal = 14f, idleGph = 0.3f, fullGph = 4.2f, repairRate = 5f },
        };

        public static readonly ChaseGear[] Gear =
        {
            new ChaseGear { key = "cam_compact", kind = GearKind.CameraBody, name = "Compact camera", price = 0, isoMin = 100, isoMax = 3200, payout = 1f,
                            blurb = "A pocket camera. Noisy past ISO 800, but it takes the picture." },
            new ChaseGear { key = "cam_dslr", kind = GearKind.CameraBody, name = "DSLR", price = 600, isoMin = 100, isoMax = 12800, payout = 1.3f,
                            blurb = "A proper body: clean to ISO 3200, and the agencies pay 30 % more for its files." },
            new ChaseGear { key = "cam_ff", kind = GearKind.CameraBody, name = "Full-frame mirrorless", price = 1800, isoMin = 50, isoMax = 51200, payout = 1.6f,
                            blurb = "Shoots in near darkness. Photos earn 60 % more." },
            new ChaseGear { key = "lens_kit", kind = GearKind.Lens, name = "24-70 mm f/2.8", price = 0, focalMin = 24f, focalMax = 70f, apertureMin = 2.8f,
                            blurb = "The kit zoom: wide enough for a wall cloud overhead, long enough for a funnel a few miles off." },
            new ChaseGear { key = "lens_wide", kind = GearKind.Lens, name = "14-24 mm f/2.8", price = 400, focalMin = 14f, focalMax = 24f, apertureMin = 2.8f,
                            blurb = "Ultra-wide: the whole storm structure in one frame from under the base." },
            new ChaseGear { key = "lens_tele", kind = GearKind.Lens, name = "70-300 mm f/4", price = 700, focalMin = 70f, focalMax = 300f, apertureMin = 4f,
                            blurb = "Telephoto: fills the frame with a tornado from five miles away." },
            new ChaseGear { key = "mesonet", kind = GearKind.Mesonet, name = "Roof mesonet", price = 1500,
                            blurb = "A mast with anemometer, thermometer, hygrometer and barometer. Logs automatically within a mile of a tornado " +
                                    "and earns credits for every second of data, double within half a mile." },
            new ChaseGear { key = "laptop", kind = GearKind.Laptop, name = "Chase laptop", price = 350,
                            blurb = "A laptop on the dash: the GPS map and the radar in the cab (bottom right), with directions to any road you click." },
            new ChaseGear { key = "phone", kind = GearKind.Phone, name = "Smartphone", price = 200,
                            blurb = "The map and the radar in your hand when you are out of the truck." },
            new ChaseGear { key = "drone", kind = GearKind.Drone, name = "Camera drone", price = 1200, droneRangeM = 400f, droneMinutes = 5f,
                            blurb = "A quadcopter with a camera: B launches it from the roof and flies it (WASD, Q/E, mouse), X takes photos from it. " +
                                    "400 m of range from the controller, 5 minutes of battery, charges on the roof. The wind pushes it about." },
            new ChaseGear { key = "drone_range1", kind = GearKind.DroneUpgrade, name = "Long-range link", price = 450, requires = "drone", droneRangeM = 1000f,
                            blurb = "A better radio: the drone flies 1 km from the controller." },
            new ChaseGear { key = "drone_range2", kind = GearKind.DroneUpgrade, name = "Directional antenna", price = 900, requires = "drone_range1", droneRangeM = 2500f,
                            blurb = "2.5 km of range: over a tornado from a mile and a half away." },
            new ChaseGear { key = "drone_batt1", kind = GearKind.DroneUpgrade, name = "High-capacity battery", price = 450, requires = "drone", droneMinutes = 10f,
                            blurb = "10 minutes in the air." },
            new ChaseGear { key = "drone_batt2", kind = GearKind.DroneUpgrade, name = "Dual battery pack", price = 900, requires = "drone_batt1", droneMinutes = 18f,
                            blurb = "18 minutes in the air." },
            new ChaseGear { key = "drone_follow", kind = GearKind.DroneUpgrade, name = "Follow cam", price = 600, requires = "drone",
                            blurb = "The drone flies itself behind the truck while you drive: the third-person chase view (C), on a quarter of " +
                                    "the flight drain. When the battery is flat it lands and charges, and you are back in the cab." },
        };

        /// <summary>the drone's range, m, with the upgrades owned (0 without the drone)</summary>
        public static float DroneRangeM => Best(GearKind.Drone, GearKind.DroneUpgrade, g => g.droneRangeM);
        /// <summary>the drone's flight time, minutes, with the upgrades owned (0 without the drone)</summary>
        public static float DroneMinutes => Best(GearKind.Drone, GearKind.DroneUpgrade, g => g.droneMinutes);

        static float Best(GearKind a, GearKind b, System.Func<ChaseGear, float> of)
        {
            float best = 0f;
            foreach (var g in Gear) if ((g.kind == a || g.kind == b) && g.Owned && of(g) > best) best = of(g);
            return best;
        }

        public static ChaseMap Map(string key) => Maps.FirstOrDefault(m => m.key == key) ?? Maps[0];
        public static ChaseVehicle Vehicle(string key) => Vehicles.FirstOrDefault(v => v.key == key) ?? Vehicles[0];
        public static ChaseGear GearItem(string key) => Gear.FirstOrDefault(g => g.key == key);
        /// <summary>the map whose scene is loaded now</summary>
        public static ChaseMap MapOfScene(string scenePath) => Maps.FirstOrDefault(m => m.scene == scenePath);
        /// <summary>the map whose scene is loaded now, by scene name (the path is not available at run time)</summary>
        public static ChaseMap MapOfSceneName(string sceneName) => Maps.FirstOrDefault(m => System.IO.Path.GetFileNameWithoutExtension(m.scene) == sceneName) ?? Maps[0];
    }
}
