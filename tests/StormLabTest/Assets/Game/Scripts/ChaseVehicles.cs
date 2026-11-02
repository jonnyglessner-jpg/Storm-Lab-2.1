// Fits the scene's chase truck out as the vehicle chosen in the menu: its mass, power, top speed and the wind area the
// tornado pushes on (TestMapWorld.windAreaM2), its paint, and a change of body (the hatchback loses the bed, the dually
// gets wide rear tyres). The truck in the scene is the builder's procedural pickup (TestMapBuilder.BuildCar); nothing
// here needs a scene rebuild, and Revert puts it back as built so another vehicle can be fitted in the same scene.
using System.Collections.Generic;
using StormLab.TestMap;
using UnityEngine;

namespace StormLab.Game
{
    public class ChaseVehicleTag : MonoBehaviour
    {
        public string key;
        public float mass, torque, topKph, windArea;
        public readonly List<GameObject> hidden = new List<GameObject>();
        public readonly List<(Transform t, Vector3 scale, Vector3 pos)> rescaled = new List<(Transform, Vector3, Vector3)>();
        public readonly List<(Renderer r, Material m)> repainted = new List<(Renderer, Material)>();
        public Material paint;
    }

    public static class ChaseVehicles
    {
        public static void Apply(TestMapCar car, TestMapWorld world, ChaseVehicle v)
        {
            if (!car || v == null) return;
            var tag = car.GetComponent<ChaseVehicleTag>();
            if (tag && tag.key == v.key) return;
            if (tag) Revert(car, world, tag);
            else
            {
                tag = car.gameObject.AddComponent<ChaseVehicleTag>();
                tag.mass = car.Body.mass; tag.torque = car.maxMotorTorque; tag.topKph = car.maxSpeedKph; tag.windArea = world ? world.windAreaM2 : 3.2f;
            }
            tag.key = v.key;
            car.Body.mass = v.massKg; car.maxMotorTorque = v.torqueNm; car.maxSpeedKph = v.topKph;
            if (world) world.windAreaM2 = v.windAreaM2;
            var body = car.transform.Find("Body");
            // the paint: one material for every painted panel (the lamps and tails keep theirs, TestMapNightLights swaps those)
            if (body)
            {
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.sharedMaterial || !r.sharedMaterial.name.StartsWith("car_paint")) continue;
                    if (!tag.paint) { tag.paint = new Material(r.sharedMaterial) { name = "car_paint (vehicle)" }; }
                    tag.repainted.Add((r, r.sharedMaterial));
                    r.sharedMaterial = tag.paint;
                }
                if (tag.paint) tag.paint.SetColor("_BaseColor", v.paint);
            }
            switch (v.body)
            {
                case VehicleBody.Hatchback:
                    // no bed: the cab runs back to the tail
                    foreach (var n in new[] { "Bed L", "Bed R", "Tailgate" }) Hide(tag, body, n);
                    Rescale(tag, body, "Cab", new Vector3(1.9f, 0.85f, 4.2f), new Vector3(0f, 1.55f, -0.5f));
                    Rescale(tag, body, "Side windows", new Vector3(1.92f, 0.45f, 4.0f), new Vector3(0f, 1.68f, -0.5f));
                    break;
                case VehicleBody.Dually:
                    // twin rear tyres
                    foreach (var n in new[] { "VisualRL", "VisualRR" })
                    {
                        var tire = car.transform.Find(n + "/Tire");
                        if (tire) { tag.rescaled.Add((tire, tire.localScale, tire.localPosition)); tire.localScale = new Vector3(tire.localScale.x, tire.localScale.y * 2.1f, tire.localScale.z); }
                    }
                    break;
            }
        }

        static void Hide(ChaseVehicleTag tag, Transform body, string name)
        {
            var t = body ? body.Find(name) : null;
            if (t && t.gameObject.activeSelf) { t.gameObject.SetActive(false); tag.hidden.Add(t.gameObject); }
        }

        static void Rescale(ChaseVehicleTag tag, Transform body, string name, Vector3 scale, Vector3 pos)
        {
            var t = body ? body.Find(name) : null;
            if (!t) return;
            tag.rescaled.Add((t, t.localScale, t.localPosition));
            t.localScale = scale; t.localPosition = pos;
        }

        public static void Revert(TestMapCar car, TestMapWorld world, ChaseVehicleTag tag)
        {
            car.Body.mass = tag.mass; car.maxMotorTorque = tag.torque; car.maxSpeedKph = tag.topKph;
            if (world) world.windAreaM2 = tag.windArea;
            foreach (var g in tag.hidden) if (g) g.SetActive(true);
            foreach (var (t, s, p) in tag.rescaled) if (t) { t.localScale = s; t.localPosition = p; }
            foreach (var (r, m) in tag.repainted) if (r) r.sharedMaterial = m;
            tag.hidden.Clear(); tag.rescaled.Clear(); tag.repainted.Clear();
            if (tag.paint) { Object.Destroy(tag.paint); tag.paint = null; }
            tag.key = null;
        }
    }
}
