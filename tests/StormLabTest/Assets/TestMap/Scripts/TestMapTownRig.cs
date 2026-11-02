// What the towns carry at play time (built at Start from TestMapPlaces, no scene rebuild needed): the traffic light at
// each town's crossing (mast arms on four corner poles, three-lamp heads over the approaching lanes, cycling with Main
// Street on the long green) and the painted, signed parking spots where a chaser sleeps (the truck stop's overnight
// space, the motel's guest space) or has the truck seen to (the mechanic's bay). The lamps are emissive discs; a lit
// lamp glows at night as the farm bulbs do.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab.TestMap
{
    public class TestMapTownRig : MonoBehaviour
    {
        public TestMapWorld world;
        [Tooltip("the signal timing, s: Main Street's green, the cross street's green, amber, all red")]
        public float mainGreen = 28f, crossGreen = 18f, amber = 4f, allRed = 1.5f;
        [Tooltip("luminance of a lit lamp, nits")]
        public float lampNits = 12000f;

        public sealed class Signal
        {
            public PlaceTown town;
            public Vector3 at;
            // heads facing each approach: [0] north-south traffic (facing north and south), [1] east-west
            public readonly List<Renderer[]> headsNS = new List<Renderer[]>(), headsEW = new List<Renderer[]>();
            public float phase;
            /// <summary>0 red, 1 amber, 2 green, for the north-south approaches and the east-west ones</summary>
            public int NS { get; internal set; } = 2;
            public int EW { get; internal set; }
        }

        public readonly List<Signal> Signals = new List<Signal>();
        /// <summary>every sleep and service spot built: its town, kind ("gas", "motel", "mechanic") and spot</summary>
        public readonly List<(PlaceTown town, string kind, PlaceSpot spot)> Spots = new List<(PlaceTown, string, PlaceSpot)>();

        Material lampRed, lampAmber, lampGreen, lampOff, dark, paint, post, signFace;
        float cycle;

        void Start()
        {
            if (!world) world = GetComponent<TestMapWorld>();
            var towns = TestMapPlaces.Load(world ? world.navResource : "TestMapNav");
            if (towns.Length == 0) return;
            Materials();
            var root = new GameObject("Towns").transform; root.SetParent(transform, false);
            foreach (var t in towns)
            {
                if (t.light != null) BuildSignal(t, root);
                if (t.gas?.sleep != null) BuildSpot(t, "gas", t.gas.sleep, "OVERNIGHT PARKING", root);
                if (t.motel?.sleep != null) BuildSpot(t, "motel", t.motel.sleep, "GUEST PARKING", root);
                if (t.mechanic?.spot != null) BuildSpot(t, "mechanic", t.mechanic.spot, "SERVICE BAY", root);
            }
        }

        void Materials()
        {
            Material M(string n, Color c, float smooth, Color? emissive = null)
            {
                var m = new Material(Shader.Find("HDRP/Lit")) { name = n, hideFlags = HideFlags.DontSave };
                m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", 0f);
                if (emissive.HasValue) { m.EnableKeyword("_EMISSIVE_COLOR"); m.SetColor("_EmissiveColor", emissive.Value); m.SetFloat("_EmissiveExposureWeight", 0.6f); }
                return m;
            }
            lampRed = M("lamp red", new Color(0.5f, 0.05f, 0.03f), 0.6f, new Color(1f, 0.08f, 0.03f) * lampNits);
            lampAmber = M("lamp amber", new Color(0.6f, 0.4f, 0.05f), 0.6f, new Color(1f, 0.6f, 0.05f) * lampNits);
            lampGreen = M("lamp green", new Color(0.05f, 0.5f, 0.2f), 0.6f, new Color(0.1f, 1f, 0.4f) * lampNits);
            lampOff = M("lamp off", new Color(0.12f, 0.12f, 0.12f), 0.5f);
            dark = M("signal housing", new Color(0.12f, 0.13f, 0.12f), 0.4f);
            paint = M("spot paint", new Color(0.95f, 0.8f, 0.15f), 0.2f);
            post = M("post", new Color(0.55f, 0.56f, 0.58f), 0.5f);
            signFace = M("spot sign", new Color(0.12f, 0.35f, 0.70f), 0.3f);
        }

        static GameObject Part(PrimitiveType t, string name, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, Material m, bool collider = false)
        {
            var g = GameObject.CreatePrimitive(t); g.name = name;
            if (!collider) Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localRotation = rot; g.transform.localScale = scale;
            var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.On;
            return g;
        }

        /// <summary>The crossing's signal: a pole on each corner with a mast arm over the near lanes of the crossing
        /// street, two heads on each arm facing the traffic coming toward that corner along the pole's own street.</summary>
        void BuildSignal(PlaceTown t, Transform root)
        {
            var s = new Signal { town = t, at = t.light.Position };
            var go = new GameObject($"Signal {t.name}"); go.transform.SetParent(root, false); go.transform.position = s.at;
            float nsHalf = t.light.nsHalf + 1.5f, ewHalf = t.light.ewHalf + 1.5f;
            // the four corners: (east, north) signs; the pole at the corner, its arm reaching across the street the heads face
            foreach (var (sx, sz) in new[] { (1, 1), (-1, 1), (-1, -1), (1, -1) })
            {
                var corner = new Vector3(sx * nsHalf, 0f, sz * ewHalf);
                Part(PrimitiveType.Cylinder, "Pole", go.transform, corner + Vector3.up * 3.2f, Quaternion.identity, new Vector3(0.3f, 3.2f, 0.3f), post, true);
                // heads for the north-south traffic: on the arm over the east-west street? no: a head faces traffic coming
                // along a street, so it hangs over that street's near lanes. Traffic heading north (coming from the south)
                // meets the heads on the arm at the north side of the crossing, reaching over the north-south street.
                // Each corner pole carries one arm: the (east, north) pole's arm reaches west over the north-south street
                // for northbound traffic... to keep it simple, each corner gets the arm for the traffic that reaches it
                // along the north-south street (sz > 0: northbound, facing south) and one over the east-west street
                // (sx > 0: eastbound, facing west), as a diagonal mast arm pair at two of the corners.
                if (sz * sx > 0)
                {
                    // arm reaching across the north-south street (along -sx), heads facing -sz (toward the oncoming north-south traffic)
                    float reach = nsHalf * 2f - 2f;
                    Part(PrimitiveType.Cylinder, "Arm", go.transform, corner + Vector3.up * 6.2f + new Vector3(-sx * reach / 2f, 0f, 0f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.16f, reach / 2f, 0.16f), post);
                    var heads = new List<Renderer>();
                    foreach (float f in new[] { 0.35f, 0.75f })
                        heads.AddRange(Head(go.transform, corner + new Vector3(-sx * reach * f, 5.5f, 0f), Quaternion.LookRotation(new Vector3(0f, 0f, -sz))));
                    s.headsNS.Add(heads.ToArray());
                }
                else
                {
                    float reach = ewHalf * 2f - 2f;
                    Part(PrimitiveType.Cylinder, "Arm", go.transform, corner + Vector3.up * 6.2f + new Vector3(0f, 0f, -sz * reach / 2f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.16f, reach / 2f, 0.16f), post);
                    var heads = new List<Renderer>();
                    foreach (float f in new[] { 0.35f, 0.75f })
                        heads.AddRange(Head(go.transform, corner + new Vector3(0f, 5.5f, -sz * reach * f), Quaternion.LookRotation(new Vector3(-sx, 0f, 0f))));
                    s.headsEW.Add(heads.ToArray());
                }
            }
            Signals.Add(s);
            Show(s);
        }

        /// <summary>A three-lamp head hanging under the arm, its lamps (red, amber, green from the top) facing `rot`'s forward. Returns the three lamp renderers.</summary>
        Renderer[] Head(Transform parent, Vector3 at, Quaternion rot)
        {
            var h = new GameObject("Head"); h.transform.SetParent(parent, false); h.transform.localPosition = at; h.transform.localRotation = rot;
            Part(PrimitiveType.Cube, "Housing", h.transform, new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(0.36f, 1.1f, 0.3f), dark);
            Part(PrimitiveType.Cube, "Hanger", h.transform, new Vector3(0f, 0.7f, 0f), Quaternion.identity, new Vector3(0.06f, 0.3f, 0.06f), post);
            var lamps = new Renderer[3];
            for (int i = 0; i < 3; i++)
            {
                var visor = Part(PrimitiveType.Cube, "Visor", h.transform, new Vector3(0f, 0.34f - i * 0.34f + 0.12f, 0.2f), Quaternion.identity, new Vector3(0.3f, 0.04f, 0.26f), dark);
                var lamp = Part(PrimitiveType.Cylinder, "Lamp", h.transform, new Vector3(0f, 0.34f - i * 0.34f, 0.16f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.26f, 0.02f, 0.26f), lampOff);
                lamp.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                lamps[i] = lamp.GetComponent<Renderer>();
            }
            return lamps;
        }

        /// <summary>A marked parking space: a painted outline on the ground and a sign post at its head.</summary>
        void BuildSpot(PlaceTown t, string kind, PlaceSpot spot, string text, Transform root)
        {
            var go = new GameObject($"{kind} spot {t.name}"); go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(spot.Position, Quaternion.Euler(0f, spot.yaw, 0f));
            const float w = 3.6f, l = 7.5f, y = 0.06f, line = 0.12f;
            Part(PrimitiveType.Cube, "Line L", go.transform, new Vector3(-w / 2f, y, 0f), Quaternion.identity, new Vector3(line, 0.01f, l), paint);
            Part(PrimitiveType.Cube, "Line R", go.transform, new Vector3(w / 2f, y, 0f), Quaternion.identity, new Vector3(line, 0.01f, l), paint);
            Part(PrimitiveType.Cube, "Line head", go.transform, new Vector3(0f, y, l / 2f), Quaternion.identity, new Vector3(w + line, 0.01f, line), paint);
            Part(PrimitiveType.Cube, "Line foot", go.transform, new Vector3(0f, y, -l / 2f), Quaternion.identity, new Vector3(w + line, 0.01f, line), paint);
            // a hatched bar across the middle so the space reads as special from the driver's seat
            for (int i = -2; i <= 2; i++)
                Part(PrimitiveType.Cube, "Hatch", go.transform, new Vector3(0f, y, i * 1.2f), Quaternion.identity, new Vector3(w * 0.7f, 0.01f, line * 1.5f), paint);
            var sign = new GameObject("Sign"); sign.transform.SetParent(go.transform, false); sign.transform.localPosition = new Vector3(0f, 0f, l / 2f + 0.8f);
            Part(PrimitiveType.Cylinder, "Post", sign.transform, Vector3.up * 1.2f, Quaternion.identity, new Vector3(0.08f, 1.2f, 0.08f), post);
            Part(PrimitiveType.Cube, "Board", sign.transform, Vector3.up * 2.55f, Quaternion.identity, new Vector3(1.2f, 0.7f, 0.04f), signFace);
            var tm = new GameObject("Text"); tm.transform.SetParent(sign.transform, false);
            tm.transform.localPosition = new Vector3(0f, 2.55f, -0.03f); tm.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var mesh = tm.AddComponent<TextMesh>(); mesh.text = text + "\nSLEEP HERE  (F)"; mesh.fontSize = 48; mesh.characterSize = 0.045f; mesh.anchor = TextAnchor.MiddleCenter; mesh.alignment = TextAlignment.Center; mesh.color = Color.white;
            if (kind == "mechanic") mesh.text = text + "\nREPAIRS  (F)";
            Spots.Add((t, kind, spot));
        }

        void Update()
        {
            cycle = mainGreen + amber + allRed + crossGreen + amber + allRed;
            if (Signals.Count == 0 || cycle <= 0f) return;
            float t = Mathf.Repeat(Time.time, cycle);
            int ns, ew;
            if (t < mainGreen) { ns = 2; ew = 0; }
            else if (t < mainGreen + amber) { ns = 1; ew = 0; }
            else if (t < mainGreen + amber + allRed) { ns = 0; ew = 0; }
            else if (t < mainGreen + amber + allRed + crossGreen) { ns = 0; ew = 2; }
            else if (t < mainGreen + amber + allRed + crossGreen + amber) { ns = 0; ew = 1; }
            else { ns = 0; ew = 0; }
            foreach (var s in Signals)
            {
                if (s.NS == ns && s.EW == ew) continue;
                s.NS = ns; s.EW = ew;
                Show(s);
            }
        }

        void Show(Signal s)
        {
            foreach (var heads in s.headsNS) Light(heads, s.NS);
            foreach (var heads in s.headsEW) Light(heads, s.EW);
        }

        void Light(Renderer[] lamps, int state)
        {
            // lamps come in threes per head (red, amber, green), several heads per arm
            for (int i = 0; i < lamps.Length; i++)
            {
                int which = i % 3;   // 0 red, 1 amber, 2 green
                bool on = (state == 0 && which == 0) || (state == 1 && which == 1) || (state == 2 && which == 2);
                lamps[i].sharedMaterial = !on ? lampOff : which == 0 ? lampRed : which == 1 ? lampAmber : lampGreen;
            }
        }

        /// <summary>The signal's state for traffic heading `yaw` (compass degrees) at the nearest town, 0 red / 1 amber / 2 green (-1: none near).</summary>
        public int StateFor(Vector3 at, float yaw)
        {
            foreach (var s in Signals)
                if (TestMapPlaces.Flat(s.at, at) < 60f)
                {
                    float a = Mathf.Repeat(yaw, 180f);
                    bool northSouth = a < 45f || a > 135f;
                    return northSouth ? s.NS : s.EW;
                }
            return -1;
        }
    }
}
