// Builds the plains test map (Assets/Scenes/TestMap.unity) from TestMapData/, which tools/testmap/*.py write,
// or a real-data map from its own data, which tools/testmap/real/*.py write: the Dixie map (Assets/Scenes/DixieMap.unity,
// DixieMapData/) or the Midwest map (Assets/Scenes/MidwestMap.unity, MidwestMapData/):
// 100 terrain tiles of 5 x 5 miles with eight ground layers and their trees, the road, bridge and water meshes, the
// farmsteads, the power lines, a horizon skirt three map widths across, then the scene: HDRP sky and sun, the car and
// its camera, and a StormLab storm (StormRenderer with lightning) whose track crosses the map.
//   StormLab > Test Map > Build World       rebuilds everything under Assets/TestMap/Generated and the scene
//   StormLab > Test Map > Open Test Map     opens the scene and presses Play (builds it first if missing)
//   StormLab > Test Map > Rebuild Farmsteads and Lights   only the farmsteads, their yard lights and the truck's
//                                           headlights, in the scene as it is (keeps everything else in it)
//   StormLab > Dixie Map > Build World / Open Dixie Map         the same for the Dixie map (Natchez, MS)
//   StormLab > Midwest Map > Build World / Open Midwest Map     the same for the Midwest map (Effingham, IL)
// Batch: -executeMethod TestMapBuilder.BuildFromCommandLine (or RebuildFarmsteadsFromCommandLine, BuildDixieFromCommandLine,
// BuildMidwestFromCommandLine)
// Generated assets are not in git; the Python tools and this builder recreate them (README in tools/testmap).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StormLab;
using StormLab.Lightning;
using StormLab.TestMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

public static class TestMapBuilder
{
    /// <summary>One map: where its data, generated assets and scene are, and what grows and storms there.</summary>
    public sealed class MapSpec
    {
        public string data, gen, scene, world, nav, preset;
        public bool real;          // built from real data (tools/testmap/real): canopy shell, real trees, road names
        public static readonly MapSpec Plains = new MapSpec { data = "TestMapData", gen = "Assets/TestMap/Generated",
            scene = "Assets/Scenes/TestMap.unity", world = "Plains Test Map", nav = "TestMapNav", preset = "plains" };
        public static readonly MapSpec Dixie = new MapSpec { data = "DixieMapData", gen = "Assets/TestMap/GeneratedDixie",
            scene = "Assets/Scenes/DixieMap.unity", world = "Dixie Map", nav = "DixieMapNav", preset = "dixie", real = true };
        public static readonly MapSpec Midwest = new MapSpec { data = "MidwestMapData", gen = "Assets/TestMap/GeneratedMidwest",
            scene = "Assets/Scenes/MidwestMap.unity", world = "Midwest Map", nav = "MidwestMapNav", preset = "ohio", real = true };
    }

    /// <summary>The map the builder works on (plains unless a Dixie or Midwest menu or command set it).</summary>
    public static MapSpec Map = MapSpec.Plains;
    public static string Gen => Map.gen;
    public static string ScenePath => Map.scene;
    public static string DataDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", Map.data));
    const float Mile = 1609.344f;

    [Serializable] public class SkirtMeta { public int res; public float size, origin, hmin, hmax; }
    [Serializable] public class SpawnMeta { public string name; public float x, y, z, yaw; }
    [Serializable] public class RouteMeta { public string name, cls; public float[] pts; }
    [Serializable]
    public class Meta
    {
        public float sizeM, tileM, datum, hmin, hmax, scenarioOriginKm;
        public int tiles, tileRes, splatRes;
        public string[] layers, materials;
        public SkirtMeta skirt;
        public SpawnMeta[] spawns;
        public RouteMeta[] routes;
    }
    [Serializable] class Building { public string kind; public float x, y, z, yaw, w, l, h, roof; public float[] wall, roofc; }
    [Serializable] class Buildings { public Building[] items; }
    [Serializable] class PowerLine { public bool three; public float[] poles; }
    [Serializable] class Power { public PowerLine[] lines; }

    /// <summary>Where the farmsteads are: each farmhouse, world position.</summary>
    public static List<Vector3> Farmsteads() =>
        JsonUtility.FromJson<Buildings>(File.ReadAllText(Path.Combine(DataDir, "buildings.json"))).items
            .Where(b => b.kind == "house").Select(b => new Vector3(b.x, b.y, b.z)).ToList();

    public static Meta LoadMeta() => JsonUtility.FromJson<Meta>(File.ReadAllText(Path.Combine(DataDir, "meta.json")));

    [MenuItem("StormLab/Test Map/Build World")]
    public static void BuildMenu() { Map = MapSpec.Plains; Build(); }

    [MenuItem("StormLab/Dixie Map/Build World")]
    public static void BuildDixieMenu() { Map = MapSpec.Dixie; try { Build(); } finally { Map = MapSpec.Plains; } }

    [MenuItem("StormLab/Dixie Map/Open Dixie Map")]
    public static void OpenDixie() { Map = MapSpec.Dixie; try { Open(); } finally { Map = MapSpec.Plains; } }

    public static void BuildDixieFromCommandLine() { Map = MapSpec.Dixie; BuildFromCommandLine(); }

    [MenuItem("StormLab/Midwest Map/Build World")]
    public static void BuildMidwestMenu() { Map = MapSpec.Midwest; try { Build(); } finally { Map = MapSpec.Plains; } }

    [MenuItem("StormLab/Midwest Map/Open Midwest Map")]
    public static void OpenMidwest() { Map = MapSpec.Midwest; try { Open(); } finally { Map = MapSpec.Plains; } }

    public static void BuildMidwestFromCommandLine() { Map = MapSpec.Midwest; BuildFromCommandLine(); }

    [MenuItem("StormLab/Test Map/Open Test Map")]
    public static void OpenMenu() { Map = MapSpec.Plains; Open(); }

    public static void Open()
    {
        if (EditorApplication.isPlaying) { EditorApplication.ExitPlaymode(); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (!File.Exists(ScenePath)) Build();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EditorApplication.EnterPlaymode();
    }

    [MenuItem("StormLab/Test Map/Rebuild Farmsteads and Lights")]
    public static void RebuildFarmsteads()
    {
        var meta = LoadMeta();
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var worldGo = GameObject.Find(Map.world);
        if (!worldGo) throw new InvalidOperationException($"no '{Map.world}' in {ScenePath}: build the world first");
        foreach (var n in new[] { "Farmsteads", "Yard lights" }) { var t = worldGo.transform.Find(n); if (t) Object.DestroyImmediate(t.gameObject); }
        BuildBuildings(meta, worldGo.transform);
        var w = worldGo.GetComponent<TestMapWorld>();
        WireNightLights(worldGo, w ? w.car : Object.FindAnyObjectByType<TestMapCar>(), w ? w.storm : Object.FindAnyObjectByType<StormRenderer>());
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[TestMap] farmsteads and lights rebuilt");
    }

    public static void RebuildFarmsteadsFromCommandLine()
    {
        bool ok;
        try { RebuildFarmsteads(); ok = true; }
        catch (Exception e) { Debug.LogException(e); ok = false; }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    public static void BuildFromCommandLine()
    {
        bool ok;
        try { Build(); ok = true; }
        catch (Exception e) { Debug.LogException(e); ok = false; }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    static void Progress(string what, float f)
    {
        Debug.Log($"[TestMap] {what}");
        if (!Application.isBatchMode) EditorUtility.DisplayProgressBar("Building the test map", what, f);
    }

    public static void Build()
    {
        var t0 = DateTime.Now;
        if (!File.Exists(Path.Combine(DataDir, "meta.json")))
            throw new FileNotFoundException(Map.real
                ? $"{Map.data}/meta.json is missing: run tools/testmap/real (fetch, land, roadnet, groundtex, places, then navmap.py) first"
                : "TestMapData/meta.json is missing: run tools/testmap (fetch_terrain, terrain, roads, textures, world) first");
        var meta = LoadMeta();
        try
        {
            StormLabTestKit.EnsurePipeline();
            foreach (var f in new[] { Gen, Gen + "/Materials", Gen + "/Layers", Gen + "/Terrain", Gen + "/Meshes", Gen + "/Trees", "Assets/Scenes" })
                StormLabTestKit.EnsureFolder(f);
            Progress("textures", 0.02f);
            ImportTextures();
            var mats = Materials(meta);
            var layers = TerrainLayers(meta);
            var trees = TreePrototypes();
            Progress("scene", 0.05f);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var world = new GameObject(Map.world);
            BuildTerrain(meta, layers, trees, world.transform);
            BuildMeshes(meta, mats, world.transform);
            BuildBuildings(meta, world.transform);
            BuildPower(meta, world.transform);
            BuildSkirt(meta, layers, world.transform);
            BuildWalls(meta, world.transform);
            Progress("scene objects", 0.97f);
            BuildRig(meta, world);
            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!list.Exists(s => s.path == ScenePath)) list.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[TestMap] built in {(DateTime.Now - t0).TotalMinutes:0.0} min: {ScenePath}");
        }
        finally { if (!Application.isBatchMode) EditorUtility.ClearProgressBar(); }
    }

    // ------------------------------------------------------------------------------------------------- textures

    static void ImportTextures()
    {
        AssetDatabase.Refresh();
        foreach (var path in Directory.GetFiles(Gen + "/Textures", "*.png"))
        {
            var p = path.Replace('\\', '/');
            var imp = (TextureImporter)AssetImporter.GetAtPath(p);
            if (!imp) continue;
            string n = Path.GetFileNameWithoutExtension(p);
            bool normal = n.EndsWith("_n"), road = n.StartsWith("road_"), side = n == "canopy_side";
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = !normal;
            imp.mipmapEnabled = true;
            imp.anisoLevel = 8;
            imp.wrapModeU = road ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            imp.wrapModeV = side ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;     // (the wall spans it once, ground to crowns)
            imp.maxTextureSize = 2048;
            imp.alphaSource = (n.StartsWith("ground_") || side) && !normal ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            // the canopy walls' crown fringe is alpha-tested: keep it from thinning out in the small mips
            imp.mipMapsPreserveCoverage = side; imp.alphaTestReferenceValue = 0.5f;
            imp.SaveAndReimport();
        }
    }

    static Texture2D Tex(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/Textures/{name}.png");

    static Material Lit(string name, Texture tex, Color col, float smooth, bool unlit = false)
    {
        string path = $"{Gen}/Materials/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find(unlit ? "HDRP/Unlit" : "HDRP/Lit");
        if (!m) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        m.shader = shader;
        if (unlit) { m.SetColor("_UnlitColor", col); }
        else
        {
            m.SetTexture("_BaseColorMap", tex);
            m.SetColor("_BaseColor", col);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
        }
        m.enableInstancing = true;
        HDMaterial.ValidateMaterial(m);
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material[] Materials(Meta meta)
    {
        var byName = new Dictionary<string, Material>
        {
            ["road_state"] = Lit("road_state", Tex("road_state"), Color.white, 0.3f),
            ["road_state_nopass"] = Lit("road_state_nopass", Tex("road_state_nopass"), Color.white, 0.3f),
            ["road_county"] = Lit("road_county", Tex("road_county"), Color.white, 0.3f),
            ["road_gravel"] = Lit("road_gravel", Tex("road_gravel"), Color.white, 0.12f),
            ["plain_asphalt"] = Lit("plain_asphalt", Tex("plain_asphalt"), Color.white, 0.3f),
            ["plain_gravel"] = Lit("plain_gravel", Tex("plain_gravel"), Color.white, 0.12f),
            ["concrete"] = Lit("concrete", Tex("concrete"), Color.white, 0.25f),
            ["water"] = Lit("water", null, new Color(0.16f, 0.2f, 0.2f), 0.93f),
        };
        if (Map.real)
        {
            byName["river"] = Lit("river", null, new Color(0.33f, 0.28f, 0.20f), 0.9f);    // the Mississippi's silt
            byName["canopy_hard"] = Lit("canopy_hard", Tex("canopy_hard"), Color.white, 0.1f);
            byName["canopy_pine"] = Lit("canopy_pine", Tex("canopy_pine"), Color.white, 0.1f);
            // the shell's walls: their top is a fringe of crowns cut out against the sky
            var side = byName["canopy_side"] = Lit("canopy_side", Tex("canopy_side"), Color.white, 0.05f);
            side.SetFloat("_AlphaCutoffEnable", 1f); side.SetFloat("_AlphaCutoff", 0.5f); side.EnableKeyword("_ALPHATEST_ON");
            side.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(side);
            EditorUtility.SetDirty(side);
            foreach (var n in new[] { "canopy_hard", "canopy_pine" })
            {
                byName[n].SetTexture("_NormalMap", Tex(n + "_n"));
                byName[n].EnableKeyword("_NORMALMAP");
                HDMaterial.ValidateMaterial(byName[n]);
                EditorUtility.SetDirty(byName[n]);
            }
        }
        return meta.materials.Select(n => byName[n]).ToArray();
    }

    static TerrainLayer[] TerrainLayers(Meta meta)
    {
        var smooth = new Dictionary<string, float> { ["stubble"] = 0.18f, ["greencrop"] = 0.25f, ["fallow"] = 0.1f, ["range"] = 0.18f,
            ["grass"] = 0.22f, ["sorghum"] = 0.2f, ["sand"] = 0.12f, ["dirt"] = 0.08f,
            ["pasture"] = 0.2f, ["tilled"] = 0.1f, ["youngcrop"] = 0.18f, ["forest"] = 0.1f, ["lawn"] = 0.22f, ["wetland"] = 0.35f };
        return meta.layers.Select(n =>
        {
            string path = $"{Gen}/Layers/{n}.terrainlayer";
            var tl = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (!tl) { tl = new TerrainLayer(); AssetDatabase.CreateAsset(tl, path); }
            tl.diffuseTexture = Tex($"ground_{n}");
            tl.normalMapTexture = Tex($"ground_{n}_n");
            tl.tileSize = new Vector2(12f, 12f);
            tl.smoothness = smooth.TryGetValue(n, out var s) ? s : 0.15f;
            tl.metallic = 0f;
            tl.normalScale = 1f;
            EditorUtility.SetDirty(tl);
            return tl;
        }).ToArray();
    }

    // ---------------------------------------------------------------------------------------------------- trees

    // plains: 0 cottonwood, 1 elm / hackberry, 2 eastern red cedar, 3 shrub (plum, Russian olive)
    // real-data maps: 0 loblolly pine, 1 oak / sweetgum / hickory, 2 bald cypress, 3 understory (yaupon, privet)
    static TreePrototype[] TreePrototypes()
    {
        var bark = Lit("bark", null, new Color(0.30f, 0.27f, 0.23f), 0.1f);
        var leafTex = Tex(Map.real ? "ground_lawn" : "ground_grass");
        var leaves = Map.real ? new[]
        {
            Lit("leaves_pine", leafTex, new Color(0.36f, 0.46f, 0.34f), 0.15f),
            Lit("leaves_oak", leafTex, new Color(0.58f, 0.74f, 0.46f), 0.2f),
            Lit("leaves_cypress", leafTex, new Color(0.62f, 0.78f, 0.50f), 0.2f),
            Lit("leaves_understory", leafTex, new Color(0.50f, 0.64f, 0.40f), 0.2f),
        } : new[]
        {
            Lit("leaves_cottonwood", leafTex, new Color(0.62f, 0.80f, 0.50f), 0.2f),
            Lit("leaves_elm", leafTex, new Color(0.55f, 0.72f, 0.44f), 0.2f),
            Lit("leaves_cedar", leafTex, new Color(0.34f, 0.46f, 0.36f), 0.15f),
            Lit("leaves_shrub", leafTex, new Color(0.66f, 0.76f, 0.52f), 0.2f),
        };
        var protos = new TreePrototype[4];
        for (int k = 0; k < 4; k++)
        {
            string path = $"{Gen}/Trees/tree_{k}.prefab";
            var mesh = Map.real ? RealTreeMesh(k) : TreeMesh(k);
            mesh.name = $"tree_{k}";
            string mpath = $"{Gen}/Trees/tree_{k}.asset";
            AssetDatabase.DeleteAsset(mpath);
            AssetDatabase.CreateAsset(mesh, mpath);
            var go = new GameObject($"tree_{k}");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { bark, leaves[k] };
            if (k <= (Map.real ? 2 : 1))
            {
                var cap = go.AddComponent<CapsuleCollider>();
                cap.radius = k == 0 && !Map.real ? 0.45f : 0.3f; cap.height = 6f; cap.center = new Vector3(0, 3f, 0);
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            protos[k] = new TreePrototype { prefab = prefab, bendFactor = 0f };
        }
        return protos;
    }

    static Mesh TreeMesh(int type)
    {
        var rnd = new System.Random(17 + type);
        var b = new MeshBuilder(2);
        float R(float a, float c) => a + (float)rnd.NextDouble() * (c - a);
        switch (type)
        {
            case 0:     // cottonwood: tall trunk, broad open crown of several lobes
                b.Cylinder(0, Vector3.zero, 0.55f, 0.32f, 8.5f, 8);
                for (int i = 0; i < 7; i++)
                {
                    float a = i * 51f * Mathf.Deg2Rad;
                    var c = new Vector3(Mathf.Cos(a) * R(2f, 4.5f), R(11f, 16f), Mathf.Sin(a) * R(2f, 4.5f));
                    b.Blob(1, c, new Vector3(R(3.5f, 5f), R(3f, 4.2f), R(3.5f, 5f)), rnd);
                }
                break;
            case 1:     // elm / hackberry: rounded crown
                b.Cylinder(0, Vector3.zero, 0.35f, 0.22f, 5f, 7);
                for (int i = 0; i < 5; i++)
                {
                    float a = i * 72f * Mathf.Deg2Rad;
                    var c = new Vector3(Mathf.Cos(a) * R(1f, 2.5f), R(7f, 9.5f), Mathf.Sin(a) * R(1f, 2.5f));
                    b.Blob(1, c, new Vector3(R(2.6f, 3.4f), R(2.3f, 3f), R(2.6f, 3.4f)), rnd);
                }
                break;
            case 2:     // eastern red cedar: dense narrow cone
                b.Cylinder(0, Vector3.zero, 0.18f, 0.12f, 1.2f, 6);
                b.Cone(1, new Vector3(0, 0.6f, 0), 1.7f, 6.8f, 10, 6, rnd);
                break;
            default:    // shrub
                for (int i = 0; i < 3; i++)
                    b.Blob(1, new Vector3(R(-1f, 1f), R(1.2f, 2f), R(-1f, 1f)), new Vector3(R(1.4f, 2f), R(1.1f, 1.6f), R(1.4f, 2f)), rnd);
                break;
        }
        return b.ToMesh(true);
    }

    static Mesh RealTreeMesh(int type)
    {
        var rnd = new System.Random(31 + type);
        var b = new MeshBuilder(2);
        float R(float a, float c) => a + (float)rnd.NextDouble() * (c - a);
        switch (type)
        {
            case 0:     // loblolly pine: tall clear trunk, a small rounded crown of tufts at the top
                b.Cylinder(0, Vector3.zero, 0.32f, 0.16f, 19f, 7);
                for (int i = 0; i < 6; i++)
                {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var c = new Vector3(Mathf.Cos(a) * R(0.8f, 2f), R(16.5f, 21f), Mathf.Sin(a) * R(0.8f, 2f));
                    b.Blob(1, c, new Vector3(R(1.8f, 2.6f), R(1.4f, 2f), R(1.8f, 2.6f)), rnd);
                }
                break;
            case 1:     // oak / sweetgum: a stout trunk and a broad dome
                b.Cylinder(0, Vector3.zero, 0.45f, 0.28f, 7f, 8);
                for (int i = 0; i < 7; i++)
                {
                    float a = i * 51f * Mathf.Deg2Rad;
                    var c = new Vector3(Mathf.Cos(a) * R(2f, 4f), R(10f, 14f), Mathf.Sin(a) * R(2f, 4f));
                    b.Blob(1, c, new Vector3(R(3.2f, 4.5f), R(2.6f, 3.6f), R(3.2f, 4.5f)), rnd);
                }
                break;
            case 2:     // bald cypress: flared base, straight trunk, a narrow open cone
                b.Cylinder(0, Vector3.zero, 0.9f, 0.35f, 1.6f, 8);
                b.Cylinder(0, new Vector3(0, 1.6f, 0), 0.35f, 0.18f, 14f, 7);
                b.Cone(1, new Vector3(0, 6f, 0), 3.2f, 14f, 10, 6, rnd);
                break;
            default:    // understory
                for (int i = 0; i < 3; i++)
                    b.Blob(1, new Vector3(R(-1f, 1f), R(1.2f, 2.2f), R(-1f, 1f)), new Vector3(R(1.4f, 2.2f), R(1.2f, 1.8f), R(1.4f, 2.2f)), rnd);
                break;
        }
        return b.ToMesh(true);
    }

    // -------------------------------------------------------------------------------------------------- terrain

    static void BuildTerrain(Meta meta, TerrainLayer[] layers, TreePrototype[] protos, Transform parent)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>($"{Gen}/Materials/terrain.mat");
        if (!mat) { mat = new Material(Shader.Find("HDRP/TerrainLit")); AssetDatabase.CreateAsset(mat, $"{Gen}/Materials/terrain.mat"); }
        var root = new GameObject("Terrain").transform; root.SetParent(parent, false);
        int res = meta.tileRes, sres = meta.splatRes;
        var heights = new float[res, res];
        var alpha = new float[sres, sres, 8];
        var bytes = new byte[res * res * 2];
        for (int r = 0; r < meta.tiles; r++)
        {
            Progress($"terrain row {r + 1} of {meta.tiles}", 0.06f + 0.6f * r / meta.tiles);
            for (int c = 0; c < meta.tiles; c++)
            {
                using (var fs = File.OpenRead(Path.Combine(DataDir, "heights", $"h_{c}_{r}.raw"))) fs.Read(bytes, 0, bytes.Length);
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                        heights[j, i] = BitConverter.ToUInt16(bytes, 2 * (j * res + i)) / 65535f;
                // the asset has to exist before the alphamaps are written, or they are not saved with it
                string path = $"{Gen}/Terrain/td_{c}_{r}.asset";
                AssetDatabase.DeleteAsset(path);
                var td = new TerrainData();
                AssetDatabase.CreateAsset(td, path);
                td.heightmapResolution = res;
                td.size = new Vector3(meta.tileM, meta.hmax - meta.hmin, meta.tileM);
                td.SetHeights(0, 0, heights);
                td.alphamapResolution = sres;
                td.baseMapResolution = 1024;
                td.terrainLayers = layers;
                for (int half = 0; half < 2; half++)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    tex.LoadImage(File.ReadAllBytes(Path.Combine(DataDir, "splat", $"s_{c}_{r}_{half}.png")));
                    var px = tex.GetPixels32();
                    for (int j = 0; j < sres; j++)
                        for (int i = 0; i < sres; i++)
                        {
                            var q = px[j * sres + i];
                            alpha[j, i, 4 * half] = q.r / 255f; alpha[j, i, 4 * half + 1] = q.g / 255f;
                            alpha[j, i, 4 * half + 2] = q.b / 255f; alpha[j, i, 4 * half + 3] = q.a / 255f;
                        }
                    Object.DestroyImmediate(tex);
                }
                td.SetAlphamaps(0, 0, alpha);
                td.treePrototypes = protos;
                string tf = Path.Combine(DataDir, "trees", $"t_{c}_{r}.bin");
                if (File.Exists(tf))
                {
                    var tb = File.ReadAllBytes(tf);
                    int n = tb.Length / 20;
                    var inst = new TreeInstance[n];
                    for (int k = 0; k < n; k++)
                    {
                        float type = BitConverter.ToSingle(tb, 20 * k), nx = BitConverter.ToSingle(tb, 20 * k + 4), nz = BitConverter.ToSingle(tb, 20 * k + 8);
                        float sc = BitConverter.ToSingle(tb, 20 * k + 12), yaw = BitConverter.ToSingle(tb, 20 * k + 16);
                        float tint = 0.85f + 0.15f * Mathf.Repeat(nx * 977f + nz * 613f, 1f);
                        inst[k] = new TreeInstance
                        {
                            prototypeIndex = Mathf.Clamp((int)type, 0, protos.Length - 1), position = new Vector3(nx, 0f, nz),
                            widthScale = sc, heightScale = sc * (0.9f + 0.2f * Mathf.Repeat(nx * 431f, 1f)), rotation = yaw * Mathf.Deg2Rad,
                            color = new Color(tint, tint, tint, 1f), lightmapColor = Color.white,
                        };
                    }
                    td.SetTreeInstances(inst, true);
                }
                EditorUtility.SetDirty(td);
                var go = Terrain.CreateTerrainGameObject(td);
                go.name = $"Terrain {c} {r}";
                go.transform.SetParent(root, false);
                go.transform.position = new Vector3(c * meta.tileM - meta.sizeM / 2, meta.hmin, r * meta.tileM - meta.sizeM / 2);
                var t = go.GetComponent<Terrain>();
                t.materialTemplate = mat;
                t.heightmapPixelError = 4f;
                t.basemapDistance = 2500f;
                t.treeDistance = Map.real ? 3000f : 5000f;      // (real-data maps: the canopy shell carries the forest beyond)
                t.treeBillboardDistance = t.treeDistance;
                t.treeMaximumFullLODCount = 100000;
                t.drawInstanced = true;
                t.groupingID = 1;
                t.allowAutoConnect = true;
                t.detailObjectDistance = 0f;
                t.shadowCastingMode = ShadowCastingMode.On;
            }
            AssetDatabase.SaveAssets();
        }
    }

    static void BuildSkirt(Meta meta, TerrainLayer[] layers, Transform parent)
    {
        Progress("horizon skirt", 0.9f);
        var sk = meta.skirt;
        int n = sk.res;
        var bytes = File.ReadAllBytes(Path.Combine(DataDir, "skirt.raw"));
        var h = new float[n, n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                h[j, i] = BitConverter.ToUInt16(bytes, 2 * (j * n + i)) / 65535f;
        string path = $"{Gen}/Terrain/skirt.asset";
        AssetDatabase.DeleteAsset(path);
        var td = new TerrainData();
        AssetDatabase.CreateAsset(td, path);
        td.heightmapResolution = n;
        td.size = new Vector3(sk.size, sk.hmax - sk.hmin, sk.size);
        td.SetHeights(0, 0, h);
        td.alphamapResolution = 2048;
        td.baseMapResolution = 1024;
        td.terrainLayers = layers;
        var alpha = new float[2048, 2048, 8];
        for (int half = 0; half < 2; half++)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            tex.LoadImage(File.ReadAllBytes(Path.Combine(DataDir, "splat", $"skirt_{half}.png")));
            var px = tex.GetPixels32();
            for (int j = 0; j < 2048; j++)
                for (int i = 0; i < 2048; i++)
                {
                    var q = px[j * 2048 + i];
                    alpha[j, i, 4 * half] = q.r / 255f; alpha[j, i, 4 * half + 1] = q.g / 255f;
                    alpha[j, i, 4 * half + 2] = q.b / 255f; alpha[j, i, 4 * half + 3] = q.a / 255f;
                }
            Object.DestroyImmediate(tex);
        }
        td.SetAlphamaps(0, 0, alpha);
        EditorUtility.SetDirty(td);
        var go = Terrain.CreateTerrainGameObject(td);
        go.name = "Horizon skirt";
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(sk.origin, sk.hmin, sk.origin);
        Object.DestroyImmediate(go.GetComponent<TerrainCollider>());
        var t = go.GetComponent<Terrain>();
        t.materialTemplate = AssetDatabase.LoadAssetAtPath<Material>($"{Gen}/Materials/terrain.mat");
        t.heightmapPixelError = 8f;
        t.basemapDistance = 0f;
        t.drawInstanced = true;
        t.groupingID = 2;
        t.allowAutoConnect = false;
    }

    static void BuildWalls(Meta meta, Transform parent)
    {
        var root = new GameObject("Map edge").transform; root.SetParent(parent, false);
        float s = meta.sizeM, h = 2000f;
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0, 0, s / 2 + 5), new Vector3(s + 20, h, 10)), (new Vector3(0, 0, -s / 2 - 5), new Vector3(s + 20, h, 10)),
            (new Vector3(s / 2 + 5, 0, 0), new Vector3(10, h, s + 20)), (new Vector3(-s / 2 - 5, 0, 0), new Vector3(10, h, s + 20)),
        })
        {
            var w = new GameObject("Wall"); w.transform.SetParent(root, false); w.transform.position = pos;
            w.AddComponent<BoxCollider>().size = size;
        }
    }

    // --------------------------------------------------------------------------------------------------- meshes

    static void BuildMeshes(Meta meta, Material[] mats, Transform parent)
    {
        var root = new GameObject("Roads, bridges and water").transform; root.SetParent(parent, false);
        var files = Directory.GetFiles(Path.Combine(DataDir, "meshes"), "*.bin").OrderBy(f => f).ToArray();
        for (int k = 0; k < files.Length; k++)
        {
            if (k % 40 == 0) Progress($"meshes {k} of {files.Length}", 0.66f + 0.18f * k / files.Length);
            string name = Path.GetFileNameWithoutExtension(files[k]);
            var parts = name.Split('_');
            string layer = parts[0]; int c = int.Parse(parts[1]), r = int.Parse(parts[2]);
            var mesh = ReadMesh(files[k], out int[] matIds);
            mesh.name = name;
            string path = $"{Gen}/Meshes/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            var go = new GameObject($"{layer} {c} {r}");
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(c * meta.tileM - meta.sizeM / 2, 0f, r * meta.tileM - meta.sizeM / 2);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = matIds.Select(m => mats[m]).ToArray();
            mr.shadowCastingMode = layer == "bridges" || layer == "canopy" ? ShadowCastingMode.On : ShadowCastingMode.Off;
            // no collider on the water, nor on the canopy shell (a car driving into the woods would climb it)
            if (layer != "water" && layer != "canopy") go.AddComponent<MeshCollider>().sharedMesh = mesh;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }
        AssetDatabase.SaveAssets();
    }

    static Mesh ReadMesh(string file, out int[] matIds)
    {
        var data = File.ReadAllBytes(file);
        int o = 0;
        int Int() { int v = BitConverter.ToInt32(data, o); o += 4; return v; }
        int n = Int();
        matIds = new int[n];
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var subs = new List<int[]>();
        for (int s = 0; s < n; s++)
        {
            matIds[s] = Int(); int nv = Int(), ni = Int();
            int b = verts.Count;
            var f = new float[nv * 5];
            Buffer.BlockCopy(data, o, f, 0, nv * 12); o += nv * 12;
            Buffer.BlockCopy(data, o, f, nv * 12, nv * 8); o += nv * 8;
            for (int i = 0; i < nv; i++) verts.Add(new Vector3(f[3 * i], f[3 * i + 1], f[3 * i + 2]));
            for (int i = 0; i < nv; i++) uvs.Add(new Vector2(f[3 * nv + 2 * i], f[3 * nv + 2 * i + 1]));
            var idx = new int[ni];
            Buffer.BlockCopy(data, o, idx, 0, ni * 4); o += ni * 4;
            for (int i = 0; i < ni; i++) idx[i] += b;
            subs.Add(idx);
        }
        var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = n;
        for (int s = 0; s < n; s++) mesh.SetTriangles(subs[s], s, false);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ------------------------------------------------------------------------------------------------ buildings

    static Material ColourMat(Dictionary<int, Material> cache, float[] c, float smooth)
    {
        int key = Mathf.RoundToInt(c[0] * 255) << 16 | Mathf.RoundToInt(c[1] * 255) << 8 | Mathf.RoundToInt(c[2] * 255);
        if (!cache.TryGetValue(key, out var m))
            cache[key] = m = Lit($"paint_{key:x6}", null, new Color(c[0], c[1], c[2]), smooth);
        return m;
    }

    static void BuildBuildings(Meta meta, Transform parent)
    {
        Progress("farmsteads", 0.85f);
        var items = JsonUtility.FromJson<Buildings>(File.ReadAllText(Path.Combine(DataDir, "buildings.json"))).items;
        var root = new GameObject("Farmsteads").transform; root.SetParent(parent, false);
        var cache = new Dictionary<int, Material>();
        var fixture = Lit("yard_fixture", null, new Color(0.10f, 0.17f, 0.12f), 0.6f);
        var bulbMat = Lit("yard_bulb", null, new Color(1f, 0.95f, 0.85f), 0.9f);
        var yardLights = new List<(Vector3 pos, Quaternion q)>();
        foreach (var tile in items.GroupBy(b => (Mathf.FloorToInt((b.x + meta.sizeM / 2) / meta.tileM), Mathf.FloorToInt((b.z + meta.sizeM / 2) / meta.tileM))))
        {
            var origin = new Vector3(tile.Key.Item1 * meta.tileM - meta.sizeM / 2, 0, tile.Key.Item2 * meta.tileM - meta.sizeM / 2);
            var mats = new List<Material>();
            var b = new MeshBuilder(0);
            int Sub(Material m) { int i = mats.IndexOf(m); if (i < 0) { mats.Add(m); b.AddSub(); i = mats.Count - 1; } return i; }
            foreach (var it in tile)
            {
                var p = new Vector3(it.x, it.y - 0.3f, it.z) - origin;
                var q = Quaternion.Euler(0, it.yaw, 0);
                int wall = Sub(ColourMat(cache, it.wall, it.kind == "house" ? 0.2f : 0.45f));
                int roof = Sub(ColourMat(cache, it.roofc, 0.5f));
                switch (it.kind)
                {
                    case "bin":
                        b.Cylinder(wall, p, it.w / 2, it.w / 2, it.h, 20);
                        b.ConeSmooth(roof, p + Vector3.up * it.h, it.w / 2 + 0.2f, it.roof * it.w / 2, 20);
                        break;
                    case "shed":
                    {
                        b.Gabled(wall, roof, p, q, it.w, it.l, it.h, it.roof * it.w / 2);
                        // the main door: a sliding door on the gable end toward the yard and the house (-forward, as
                        // world.py lays a farmstead out), and a gooseneck yard light on the wall right above it, its
                        // arm reaching out from a wall plate and a shade over the bulb (user, 2026-10-26)
                        float hl = it.l / 2, dw = Mathf.Clamp(0.45f * it.w, 5f, 9f), dh = Mathf.Min(it.h - 0.9f, 4.8f);
                        var dark = new[] { it.wall[0] * 0.55f, it.wall[1] * 0.55f, it.wall[2] * 0.55f };
                        b.Box(Sub(ColourMat(cache, dark, 0.35f)), p + q * new Vector3(0f, dh / 2f, -hl - 0.05f), q, new Vector3(dw, dh, 0.1f));
                        float ly = dh + 0.6f;
                        int fx = Sub(fixture);
                        b.Box(fx, p + q * new Vector3(0f, ly, -hl - 0.03f), q, new Vector3(0.25f, 0.25f, 0.06f));
                        b.Box(fx, p + q * new Vector3(0f, ly, -hl - 0.45f), q, new Vector3(0.05f, 0.05f, 0.85f));
                        b.Box(fx, p + q * new Vector3(0f, ly - 0.12f, -hl - 0.85f), q, new Vector3(0.05f, 0.25f, 0.05f));
                        var shadeAt = p + q * new Vector3(0f, ly - 0.32f, -hl - 0.85f);
                        b.Cylinder(fx, shadeAt, 0.3f, 0.06f, 0.22f, 14);
                        b.Disc(Sub(bulbMat), shadeAt + Vector3.up * 0.03f, Quaternion.LookRotation(Vector3.down), 0.2f, 14);
                        yardLights.Add((origin + shadeAt - Vector3.up * 0.05f, q));
                        break;
                    }
                    case "windmill":
                        b.Frustum(wall, p, q, 1.6f, 0.25f, it.h);
                        b.Disc(roof, p + Vector3.up * (it.h + 0.3f) + q * Vector3.forward * 0.4f, q, 1.5f, 16);
                        break;
                    default:
                        b.Gabled(wall, roof, p, q, it.w, it.l, it.h, it.roof * it.w / 2);
                        break;
                }
            }
            var mesh = b.ToMesh(false);
            string name = $"farms_{tile.Key.Item1}_{tile.Key.Item2}";
            mesh.name = name;
            AssetDatabase.DeleteAsset($"{Gen}/Meshes/{name}.asset");
            AssetDatabase.CreateAsset(mesh, $"{Gen}/Meshes/{name}.asset");
            var go = new GameObject($"Farmsteads {tile.Key.Item1} {tile.Key.Item2}");
            go.transform.SetParent(root, false); go.transform.position = origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }
        // the yard lights: warm spots shining down and out over the door, culled past 1.5 km (TestMapNightLights turns
        // them on from dusk to dawn)
        var lroot = new GameObject("Yard lights").transform; lroot.SetParent(parent, false);
        foreach (var (pos, q) in yardLights)
        {
            var dir = (Vector3.down + q * Vector3.back * 0.5f).normalized;
            var l = Spot("Yard light", lroot, pos, Quaternion.LookRotation(dir), 4000f, 140f, 70f, 45f, new Color(1f, 0.86f, 0.68f), 1500f);
            l.gameObject.SetActive(false);   // (TestMapNightLights activates those near the camera at night)
        }
        Debug.Log($"[TestMap] {yardLights.Count} barns, each with a door and a yard light");
    }

    /// <summary>An HDRP spot light: its luminous flux (lm) aimed into its cone, no shadows, culled past fade m.</summary>
    static Light Spot(string name, Transform parent, Vector3 pos, Quaternion rot, float lumens, float angle, float inner, float range, Color c, float fade)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        var l = go.AddComponent<Light>(); l.type = LightType.Spot;
        var hd = go.AddComponent<HDAdditionalLightData>();
        l.lightUnit = LightUnit.Lumen; l.intensity = lumens; l.color = c;
        l.spotAngle = angle; l.innerSpotAngle = inner; l.range = range; l.shadows = LightShadows.None;
        l.enableSpotReflector = true; hd.fadeDistance = fade; hd.volumetricDimmer = 0f;
        return l;
    }

    /// <summary>The truck's headlights, at its lamps (replacing any it has), aimed a little down the road.</summary>
    static Light[] AddHeadlights(TestMapCar car)
    {
        var body = car.transform.Find("Body");
        foreach (Transform t in body.Cast<Transform>().ToArray()) if (t.name.StartsWith("Headlight")) Object.DestroyImmediate(t.gameObject);
        var o = new Light[2];
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -0.7f : 0.7f;
            o[i] = Spot(i == 0 ? "Headlight L" : "Headlight R", body, Vector3.zero, Quaternion.identity, 5000f, 60f, 25f, 160f, new Color(1f, 0.97f, 0.9f), 1200f);
            o[i].transform.localPosition = new Vector3(x, 1.0f, 2.86f);
            o[i].transform.localRotation = Quaternion.Euler(4f, 0f, 0f);
        }
        return o;
    }

    /// <summary>The scene's night lights: the switch on the world, wired to the barns' lights and bulbs and the truck.</summary>
    static void WireNightLights(GameObject world, TestMapCar car, StormRenderer storm)
    {
        var old = world.GetComponent<TestMapNightLights>(); if (old) Object.DestroyImmediate(old);   // (fresh, with this build's values)
        var nl = world.AddComponent<TestMapNightLights>();
        nl.storm = storm; nl.car = car;
        nl.yardLights = world.transform.Find("Yard lights");
        nl.farmsteads = world.transform.Find("Farmsteads");
        nl.bulb = AssetDatabase.LoadAssetAtPath<Material>($"{Gen}/Materials/yard_bulb.mat");
        if (car)
        {
            nl.headlights = AddHeadlights(car);
            var body = car.transform.Find("Body");
            Renderer R(string n) { var t = body.Find(n); return t ? t.GetComponent<Renderer>() : null; }
            nl.lamps = new[] { R("Lamp L"), R("Lamp R") };
            nl.tails = new[] { R("Tail L"), R("Tail R") };
        }
        EditorUtility.SetDirty(nl);
    }

    // ---------------------------------------------------------------------------------------------------- power

    static void BuildPower(Meta meta, Transform parent)
    {
        Progress("power lines", 0.88f);
        var lines = JsonUtility.FromJson<Power>(File.ReadAllText(Path.Combine(DataDir, "power.json"))).lines;
        var wood = Lit("pole_wood", null, new Color(0.36f, 0.30f, 0.24f), 0.1f);
        var wire = Lit("wire", null, new Color(0.08f, 0.08f, 0.08f), 0f, unlit: true);
        var root = new GameObject("Power lines").transform; root.SetParent(parent, false);
        var poles = new Dictionary<(int, int), MeshBuilder>();
        var wires = new Dictionary<(int, int), (List<Vector3> v, List<int> i)>();
        (int, int) TileOf(Vector3 p) => (Mathf.Clamp(Mathf.FloorToInt((p.x + meta.sizeM / 2) / meta.tileM), 0, meta.tiles - 1),
                                         Mathf.Clamp(Mathf.FloorToInt((p.z + meta.sizeM / 2) / meta.tileM), 0, meta.tiles - 1));
        Vector3 Origin((int, int) t) => new Vector3(t.Item1 * meta.tileM - meta.sizeM / 2, 0, t.Item2 * meta.tileM - meta.sizeM / 2);
        foreach (var ln in lines)
        {
            int n = ln.poles.Length / 3;
            var P = new Vector3[n];
            for (int i = 0; i < n; i++) P[i] = new Vector3(ln.poles[3 * i], ln.poles[3 * i + 1], ln.poles[3 * i + 2]);
            float top = ln.three ? 11.5f : 9.5f;
            for (int i = 0; i < n; i++)
            {
                var dir = (i + 1 < n ? P[i + 1] - P[i] : P[i] - P[i - 1]); dir.y = 0; dir.Normalize();
                var across = new Vector3(dir.z, 0, -dir.x);
                var t = TileOf(P[i]);
                if (!poles.TryGetValue(t, out var mb)) poles[t] = mb = new MeshBuilder(1);
                var lp = P[i] - Origin(t);
                mb.Cylinder(0, lp - Vector3.up * 1.5f, 0.16f, 0.12f, top + 1.5f, 6);
                if (ln.three) mb.Box(0, lp + Vector3.up * (top - 0.5f), Quaternion.LookRotation(dir), new Vector3(2.4f, 0.1f, 0.1f));
                if (i + 1 >= n) continue;
                float span = Vector3.Distance(P[i], P[i + 1]);
                if (span > 260f) continue;
                if (!wires.TryGetValue(t, out var wl)) wires[t] = wl = (new List<Vector3>(), new List<int>());
                var offs = ln.three
                    ? new[] { new Vector3(0, top + 0.2f, 0), across * -1.05f + Vector3.up * (top - 0.4f), across * 1.05f + Vector3.up * (top - 0.4f) }
                    : new[] { new Vector3(0, top + 0.2f, 0), across * 0.2f + Vector3.up * (top - 1.6f) };
                float sag = 1.1f * (span / 100f) * (span / 100f);
                var a0 = P[i] - Origin(t); var a1 = P[i + 1] - Origin(t);
                // each wire a 4 cm ribbon on edge, both faces: it thins out to nothing with distance like a real one
                foreach (var o in offs)
                {
                    int b0 = wl.v.Count;
                    const int segs = 6;
                    for (int s = 0; s <= segs; s++)
                    {
                        float f = s / (float)segs;
                        var q = Vector3.Lerp(a0, a1, f) + o - Vector3.up * (4f * sag * f * (1 - f));
                        wl.v.Add(q + Vector3.up * 0.02f); wl.v.Add(q - Vector3.up * 0.02f);
                        if (s == 0) continue;
                        int a = b0 + 2 * (s - 1), b = a + 1, c = a + 2, d = a + 3;
                        wl.i.AddRange(new[] { a, c, b, b, c, d, a, b, c, b, d, c });
                    }
                }
            }
        }
        foreach (var kv in poles)
        {
            var mesh = kv.Value.ToMesh(false);
            Save(mesh, $"poles_{kv.Key.Item1}_{kv.Key.Item2}");
            var go = new GameObject($"Poles {kv.Key.Item1} {kv.Key.Item2}");
            go.transform.SetParent(root, false); go.transform.position = Origin(kv.Key);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = wood;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }
        foreach (var kv in wires)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(kv.Value.v);
            mesh.SetTriangles(kv.Value.i, 0);
            mesh.RecalculateBounds();
            Save(mesh, $"wires_{kv.Key.Item1}_{kv.Key.Item2}");
            var go = new GameObject($"Wires {kv.Key.Item1} {kv.Key.Item2}");
            go.transform.SetParent(root, false); go.transform.position = Origin(kv.Key);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = wire; mr.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    static void Save(Mesh mesh, string name)
    {
        mesh.name = name;
        string path = $"{Gen}/Meshes/{name}.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
    }

    // ------------------------------------------------------------------------------------------------ scene rig

    static void BuildRig(Meta meta, GameObject world)
    {
        // the game's look under HDRP: physically based sky, a 100,000 lux sun, ACES, light fog; no HDRP clouds
        var sunGo = new GameObject("Sun");
        var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional;
        sunGo.AddComponent<HDAdditionalLightData>();
        sun.lightUnit = LightUnit.Lux; sun.intensity = 100000f; sun.color = Color.white; sun.shadows = LightShadows.Soft;
        sunGo.transform.rotation = Quaternion.Euler(32f, 70f, 0f);      // late afternoon, sun in the west-south-west
        RenderSettings.sun = sun;

        string vpath = $"{Gen}/TestMapVolume.asset";      // (one per map: Gen is the map's folder)
        AssetDatabase.DeleteAsset(vpath);
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, vpath);
        T Add<T>() where T : VolumeComponent { var c = profile.Add<T>(true); c.name = typeof(T).Name; AssetDatabase.AddObjectToAsset(c, profile); return c; }
        var env = Add<VisualEnvironment>(); env.skyType.Override((int)SkyType.PhysicallyBased); env.cloudType.Override(0);
        Add<PhysicallyBasedSky>();
        var ex = Add<Exposure>(); ex.mode.Override(ExposureMode.Fixed); ex.fixedExposure.Override(13.5f);
        var tm = Add<Tonemapping>(); tm.mode.Override(TonemappingMode.ACES);
        var fog = Add<Fog>(); fog.enabled.Override(true); fog.meanFreePath.Override(40000f); fog.maxFogDistance.Override(120000f);
        var vc = Add<VolumetricClouds>(); vc.enable.Override(false);
        var sh = Add<HDShadowSettings>(); sh.maxShadowDistance.Override(2500f);
        EditorUtility.SetDirty(profile);
        var volGo = new GameObject("Sky and fog");
        var vol = volGo.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10; vol.sharedProfile = profile;

        var car = BuildCar();
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.3f; cam.farClipPlane = 250000f; cam.fieldOfView = 60f;
        camGo.AddComponent<AudioListener>();
        var hd = camGo.AddComponent<HDAdditionalCameraData>();
        hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        var rig = camGo.AddComponent<TestMapCamera>(); rig.car = car;

        var stormGo = new GameObject("Storm");
        var storm = stormGo.AddComponent<StormRenderer>();
        storm.preset = Map.preset; storm.varied = true; storm.seed = 1;
        // the storm is lit by the scene's sun, in the game's physical units under HDRP's exposure and tonemapping
        storm.sun = sun; storm.sunSource = SunSource.Light;
        storm.stormMinutes = 20f;
        stormGo.AddComponent<StormLightning>();
        rig.storm = storm;

        var w = world.AddComponent<TestMapWorld>();
        w.car = car; w.cameraRig = rig; w.storm = storm; w.sizeM = meta.sizeM; w.datum = meta.datum; w.navResource = Map.nav; w.sectionGrid = !Map.real;
        // the storm and lighting panel (Tab): storm controls and a 24 hour clock for the sun, moon and exposure
        var panel = world.AddComponent<TestMapStormPanel>(); panel.world = w; panel.storm = storm; panel.sun = sun;
        w.spots = meta.spawns.Select(s => new TestMapWorld.Spot { name = s.name, position = new Vector3(s.x, s.y, s.z), heading = s.yaw }).ToArray();
        w.routes = meta.routes.Select(r => new TestMapWorld.Route
        {
            name = r.name, cls = r.cls,
            points = Enumerable.Range(0, r.pts.Length / 3).Select(i => new Vector3(r.pts[3 * i], r.pts[3 * i + 1], r.pts[3 * i + 2])).ToArray(),
        }).ToArray();
        WireNightLights(world, car, storm);
        if (w.spots.Length > 0)
        {
            car.transform.position = w.spots[0].position + Vector3.up;
            car.transform.rotation = Quaternion.Euler(0, w.spots[0].heading, 0);
            camGo.transform.position = car.transform.position - car.transform.forward * 8f + Vector3.up * 3f;
            camGo.transform.LookAt(car.transform);
        }
    }

    static TestMapCar BuildCar()
    {
        var paint = Lit("car_paint", null, new Color(0.82f, 0.82f, 0.80f), 0.7f);
        var glass = Lit("car_glass", null, new Color(0.05f, 0.06f, 0.07f), 0.95f);
        var tireM = Lit("car_tire", null, new Color(0.04f, 0.04f, 0.04f), 0.2f);
        var rimM = Lit("car_rim", null, new Color(0.6f, 0.6f, 0.62f), 0.7f);
        var lamp = Lit("car_lamp", null, new Color(0.95f, 0.95f, 0.85f), 0.9f);
        var tail = Lit("car_tail", null, new Color(0.6f, 0.05f, 0.04f), 0.8f);
        var car = new GameObject("Chase truck");
        var rb = car.AddComponent<Rigidbody>();
        rb.mass = 2300f; rb.linearDamping = 0.02f; rb.angularDamping = 0.3f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.automaticCenterOfMass = false; rb.centerOfMass = new Vector3(0f, 0.5f, 0.1f);
        var body = new GameObject("Body"); body.transform.SetParent(car.transform, false);
        void Box(string name, Vector3 pos, Vector3 size, Material m, bool collider)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = name; b.transform.SetParent(body.transform, false);
            b.transform.localPosition = pos; b.transform.localScale = size;
            b.GetComponent<MeshRenderer>().sharedMaterial = m;
            if (!collider) Object.DestroyImmediate(b.GetComponent<Collider>());
        }
        // a crew-cab pickup, the chaser's usual ride
        Box("Frame", new Vector3(0f, 0.85f, 0f), new Vector3(2.0f, 0.6f, 5.6f), paint, true);
        Box("Cab", new Vector3(0f, 1.55f, 0.35f), new Vector3(1.9f, 0.85f, 2.5f), paint, true);
        Box("Windscreen", new Vector3(0f, 1.62f, 1.62f), new Vector3(1.8f, 0.6f, 0.05f), glass, false);
        Box("Side windows", new Vector3(0f, 1.68f, 0.35f), new Vector3(1.92f, 0.45f, 2.3f), glass, false);
        Box("Bed L", new Vector3(-0.95f, 1.35f, -1.7f), new Vector3(0.08f, 0.5f, 2.0f), paint, false);
        Box("Bed R", new Vector3(0.95f, 1.35f, -1.7f), new Vector3(0.08f, 0.5f, 2.0f), paint, false);
        Box("Tailgate", new Vector3(0f, 1.35f, -2.72f), new Vector3(1.98f, 0.5f, 0.08f), paint, false);
        Box("Lamp L", new Vector3(-0.7f, 1.0f, 2.8f), new Vector3(0.4f, 0.16f, 0.04f), lamp, false);
        Box("Lamp R", new Vector3(0.7f, 1.0f, 2.8f), new Vector3(0.4f, 0.16f, 0.04f), lamp, false);
        Box("Tail L", new Vector3(-0.85f, 1.25f, -2.78f), new Vector3(0.14f, 0.35f, 0.04f), tail, false);
        Box("Tail R", new Vector3(0.85f, 1.25f, -2.78f), new Vector3(0.14f, 0.35f, 0.04f), tail, false);
        var ctrl = car.AddComponent<TestMapCar>();
        const float radius = 0.42f, susp = 0.28f;
        var wheels = new List<TestMapCar.Wheel>();
        string[] names = { "FL", "FR", "RL", "RR" };
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2; float x = i % 2 == 0 ? -0.86f : 0.86f; float z = front ? 1.85f : -1.75f;
            var wcGo = new GameObject("Wheel" + names[i]); wcGo.transform.SetParent(car.transform, false);
            wcGo.transform.localPosition = new Vector3(x, radius + susp * 0.55f, z);
            var wc = wcGo.AddComponent<WheelCollider>();
            wc.mass = 30f; wc.radius = radius; wc.suspensionDistance = susp; wc.wheelDampingRate = 0.6f; wc.forceAppPointDistance = 0.15f;
            wc.suspensionSpring = new JointSpring { spring = 52000f, damper = 5200f, targetPosition = 0.45f };
            wc.forwardFriction = new WheelFrictionCurve { extremumSlip = 0.4f, extremumValue = 1f, asymptoteSlip = 0.8f, asymptoteValue = 0.6f, stiffness = 1.6f };
            wc.sidewaysFriction = new WheelFrictionCurve { extremumSlip = 0.25f, extremumValue = 1f, asymptoteSlip = 0.5f, asymptoteValue = 0.75f, stiffness = front ? 1.9f : 2.1f };
            var pivot = new GameObject("Visual" + names[i]).transform; pivot.SetParent(car.transform, false);
            var tire = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tire.name = "Tire"; Object.DestroyImmediate(tire.GetComponent<Collider>());
            tire.transform.SetParent(pivot, false); tire.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            tire.transform.localScale = new Vector3(radius * 2f, 0.14f, radius * 2f);
            tire.GetComponent<MeshRenderer>().sharedMaterial = tireM;
            var rim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rim.name = "Rim"; Object.DestroyImmediate(rim.GetComponent<Collider>());
            rim.transform.SetParent(pivot, false); rim.transform.localPosition = new Vector3(x < 0 ? -0.08f : 0.08f, 0f, 0f);
            rim.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); rim.transform.localScale = new Vector3(radius * 1.3f, 0.08f, radius * 1.3f);
            rim.GetComponent<MeshRenderer>().sharedMaterial = rimM;
            pivot.localPosition = wcGo.transform.localPosition - Vector3.up * susp * 0.45f;
            wheels.Add(new TestMapCar.Wheel { collider = wc, visual = pivot, steer = front, drive = true, handbrake = !front });
        }
        ctrl.wheels = wheels.ToArray();
        wheels[0].collider.ConfigureVehicleSubsteps(5f, 12, 15);
        return ctrl;
    }
}

/// <summary>Small procedural mesh helper for the trees, farm buildings and poles: submeshes, boxes, cylinders,
/// cones, gabled buildings. Front faces follow Unity's winding (clockwise seen from outside).</summary>
public class MeshBuilder
{
    readonly List<Vector3> v = new List<Vector3>();
    readonly List<Vector3> n = new List<Vector3>();
    readonly List<Vector2> uv = new List<Vector2>();
    readonly List<List<int>> subs = new List<List<int>>();

    public MeshBuilder(int submeshes) { for (int i = 0; i < submeshes; i++) subs.Add(new List<int>()); }
    public void AddSub() => subs.Add(new List<int>());

    int Vert(Vector3 p, Vector3 nn) { v.Add(p); n.Add(nn); uv.Add(new Vector2(p.x + p.z, p.y) * 0.25f); return v.Count - 1; }

    /// <summary>A triangle facing `outward` (flipped if needed).</summary>
    void Tri(int s, int a, int b, int c, Vector3 outward)
    {
        var nn = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
        if (Vector3.Dot(nn, outward) < 0) (b, c) = (c, b);
        subs[s].Add(a); subs[s].Add(b); subs[s].Add(c);
    }

    void Quad(int s, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        var nn = outward.normalized;
        int ia = Vert(a, nn), ib = Vert(b, nn), ic = Vert(c, nn), id = Vert(d, nn);
        Tri(s, ia, ib, ic, outward); Tri(s, ia, ic, id, outward);
    }

    public void Box(int s, Vector3 centre, Quaternion q, Vector3 size)
    {
        var h = size * 0.5f;
        Vector3 P(float x, float y, float z) => centre + q * new Vector3(x * h.x, y * h.y, z * h.z);
        Vector3 X = q * Vector3.right, Y = q * Vector3.up, Z = q * Vector3.forward;
        Quad(s, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), X);
        Quad(s, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), -X);
        Quad(s, P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), Y);
        Quad(s, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), -Y);
        Quad(s, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), Z);
        Quad(s, P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), -Z);
    }

    public void Cylinder(int s, Vector3 b, float r0, float r1, float h, int sides)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.PI * 2 / sides, a1 = (i + 1) * Mathf.PI * 2 / sides;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)); var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            int i0 = Vert(b + d0 * r0, d0), i1 = Vert(b + d1 * r0, d1), i2 = Vert(b + d1 * r1 + Vector3.up * h, d1), i3 = Vert(b + d0 * r1 + Vector3.up * h, d0);
            var o = (d0 + d1) * 0.5f;
            Tri(s, i0, i1, i2, o); Tri(s, i0, i2, i3, o);
        }
    }

    public void Frustum(int s, Vector3 b, Quaternion q, float w0, float w1, float h)
    {
        for (int i = 0; i < 4; i++)
        {
            var d0 = q * Quaternion.Euler(0, 90 * i + 45, 0) * Vector3.forward; var d1 = q * Quaternion.Euler(0, 90 * i + 135, 0) * Vector3.forward;
            Quad(s, b + d0 * w0 * 0.7f, b + d1 * w0 * 0.7f, b + d1 * w1 * 0.7f + Vector3.up * h, b + d0 * w1 * 0.7f + Vector3.up * h, d0 + d1);
        }
    }

    public void ConeSmooth(int s, Vector3 b, float r, float h, int sides)
    {
        var tip = b + Vector3.up * h;
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.PI * 2 / sides, a1 = (i + 1) * Mathf.PI * 2 / sides;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)); var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            var o = (d0 + d1) * 0.5f + Vector3.up * (r / Mathf.Max(h, 0.1f));
            int i0 = Vert(b + d0 * r, o.normalized), i1 = Vert(b + d1 * r, o.normalized), i2 = Vert(tip, Vector3.up);
            Tri(s, i0, i1, i2, o);
        }
    }

    public void Disc(int s, Vector3 c, Quaternion q, float r, int sides)
    {
        var f = q * Vector3.forward;
        int ic = Vert(c, f);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * Mathf.PI * 2 / sides, a1 = (i + 1) * Mathf.PI * 2 / sides;
            int i0 = Vert(c + q * new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * r, f), i1 = Vert(c + q * new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * r, f);
            Tri(s, ic, i0, i1, f); Tri(s, ic, i1, i0, -f);
        }
    }

    /// <summary>A building of footprint w (across) x l (along q's forward), eaves at h, a gable roof rising rise
    /// above the eaves along its length, overhanging 0.4 m.</summary>
    public void Gabled(int wall, int roof, Vector3 b, Quaternion q, float w, float l, float h, float rise)
    {
        Vector3 X = q * Vector3.right, Z = q * Vector3.forward, Y = Vector3.up;
        float hw = w / 2, hl = l / 2;
        Vector3 P(float x, float y, float z) => b + X * x + Y * y + Z * z;
        Quad(wall, P(hw, 0, -hl), P(hw, h, -hl), P(hw, h, hl), P(hw, 0, hl), X);
        Quad(wall, P(-hw, 0, -hl), P(-hw, 0, hl), P(-hw, h, hl), P(-hw, h, -hl), -X);
        Quad(wall, P(-hw, 0, hl), P(hw, 0, hl), P(hw, h, hl), P(-hw, h, hl), Z);
        Quad(wall, P(-hw, 0, -hl), P(-hw, h, -hl), P(hw, h, -hl), P(hw, 0, -hl), -Z);
        foreach (float sz in new[] { 1f, -1f })
        {
            int a = Vert(P(-hw, h, hl * sz), Z * sz), c = Vert(P(hw, h, hl * sz), Z * sz), t = Vert(P(0, h + rise, hl * sz), Z * sz);
            Tri(wall, a, c, t, Z * sz);
        }
        float o = 0.4f, dy = rise * o / hw;
        foreach (float sx in new[] { 1f, -1f })
        {
            var up = (Y * hw + X * sx * rise).normalized;
            Quad(roof, P(sx * (hw + o), h - dy, -hl - o), P(0, h + rise, -hl - o), P(0, h + rise, hl + o), P(sx * (hw + o), h - dy, hl + o), up);
            Quad(roof, P(sx * (hw + o), h - dy - 0.05f, -hl - o), P(0, h + rise - 0.05f, -hl - o), P(0, h + rise - 0.05f, hl + o), P(sx * (hw + o), h - dy - 0.05f, hl + o), -up);
        }
    }

    /// <summary>A lumpy ellipsoid of foliage: an icosphere pushed about by noise, normals from its centre.</summary>
    public void Blob(int s, Vector3 c, Vector3 radii, System.Random rnd)
    {
        var (verts, tris) = Ico(2);
        int b = v.Count;
        float ph = (float)rnd.NextDouble() * 10f;
        foreach (var d in verts)
        {
            float k = 1f + 0.18f * Mathf.Sin(d.x * 5.1f + ph) * Mathf.Sin(d.y * 4.3f + ph * 1.7f) * Mathf.Sin(d.z * 4.7f + ph * 0.6f) + 0.12f * ((float)rnd.NextDouble() - 0.5f);
            var p = c + Vector3.Scale(d, radii) * k;
            v.Add(p); n.Add(d); uv.Add(new Vector2(p.x + p.y, p.z + p.y) * 0.3f);
        }
        for (int i = 0; i < tris.Count; i += 3) Tri(s, b + tris[i], b + tris[i + 1], b + tris[i + 2], verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]);
    }

    /// <summary>A cedar's cone of foliage, rings jittered.</summary>
    public void Cone(int s, Vector3 b, float r, float h, int sides, int rings, System.Random rnd)
    {
        int start = v.Count;
        for (int k = 0; k <= rings; k++)
        {
            float f = k / (float)rings, rr = r * (1 - f) * (k == rings ? 0 : 1);
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2 / sides;
                float jit = 1f + 0.15f * ((float)rnd.NextDouble() - 0.5f);
                var d = new Vector3(Mathf.Cos(a), 0.35f, Mathf.Sin(a)).normalized;
                var p = b + new Vector3(Mathf.Cos(a) * rr * jit, h * f, Mathf.Sin(a) * rr * jit);
                v.Add(p); n.Add(d); uv.Add(new Vector2(p.x + p.z, p.y) * 0.3f);
            }
        }
        for (int k = 0; k < rings; k++)
            for (int i = 0; i < sides; i++)
            {
                int a = start + k * sides + i, bb = start + k * sides + (i + 1) % sides, c = a + sides, d = bb + sides;
                var o = n[a] + n[bb];
                Tri(s, a, bb, d, o); Tri(s, a, d, c, o);
            }
        int cb = Vert(b, Vector3.down);
        for (int i = 0; i < sides; i++) Tri(s, cb, start + i, start + (i + 1) % sides, Vector3.down);
    }

    static (List<Vector3>, List<int>) Ico(int subdiv)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var verts = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
        var tris = new List<int> { 0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1 };
        for (int s = 0; s < subdiv; s++)
        {
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
                return mid[key] = verts.Count - 1;
            }
            var nt = new List<int>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                nt.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            tris = nt;
        }
        return (verts, tris);
    }

    public Mesh ToMesh(bool recalcNormals)
    {
        var m = new Mesh { indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv);
        m.subMeshCount = subs.Count;
        for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i, false);
        if (recalcNormals) m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
