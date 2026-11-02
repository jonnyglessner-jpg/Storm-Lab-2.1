// The game's menus (IMGUI, like the rest of the test map's HUD): the main menu over the map at start-up (a new career,
// the career so far, free roam, the shop, the chaser's record), the new-career set-up (map, vehicle, gear, seed), the
// shop (gear bought with credits, kept in the profile), the pause menu (Esc) and the summary when a career day is abandoned.
// The game is Never Stop Chasing (user, 2026-11-02). The game clock and
// the physics stop while the pause menu or the summary is up (Time.timeScale); the main menu lets the map run behind it.
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace StormLab.Game
{
    public class ChaseMenu : MonoBehaviour
    {
        public enum Page { None, Main, NewChase, Shop, Pause, Summary }

        public ChaseGame game;
        public Page page = Page.None;
        public bool Showing => page != Page.None;

        string seedText; Vector2 scroll; bool confirmReset; Page shopFrom = Page.Main;
        GUIStyle title, head, body, small, button, tabOn, tabOff, boxStyle;
        string notice; float noticeUntil;

        public void ShowMain() { page = Page.Main; Time.timeScale = 1f; seedText = ChaseSetup.seed.ToString(); }
        public void ShowSummary() { page = Page.Summary; Time.timeScale = 0f; }
        public void Close() { page = Page.None; Time.timeScale = 1f; }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (game.Photo && game.Photo.Active && page == Page.None) return;   // the camera closes on Esc
            var world = game.world;
            if (page == Page.None && world && world.GetComponent<TestMap.TestMapMinimap>() is { } mm && mm.expanded) return;   // the map closes on Esc
            switch (page)
            {
                case Page.None:
                    if (ChaseSetup.state != ChaseSetup.State.Menu) { page = Page.Pause; Time.timeScale = 0f; }
                    break;
                case Page.Pause: Close(); break;
                case Page.Shop: page = shopFrom; break;
                case Page.NewChase: page = Page.Main; break;
            }
        }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            head = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 38 };
            tabOn = new GUIStyle(GUI.skin.button) { fontSize = 15, fixedHeight = 34, fontStyle = FontStyle.Bold };
            tabOff = new GUIStyle(GUI.skin.button) { fontSize = 15, fixedHeight = 34 };
            tabOn.normal.textColor = tabOn.hover.textColor = new Color(1f, 0.9f, 0.4f);
            boxStyle = new GUIStyle(GUI.skin.box);
            title.normal.textColor = head.normal.textColor = body.normal.textColor = Color.white;
            small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        }

        void OnGUI()
        {
            if (!Showing) return;
            Styles();
            GUI.depth = -10;
            float W = Screen.width, H = Screen.height;
            GUI.color = new Color(0f, 0f, 0f, page == Page.Main ? 0.45f : 0.6f);
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;
            float w = Mathf.Min(900f, W - 40f), h = Mathf.Min(page == Page.NewChase || page == Page.Shop ? 760f : 560f, H - 40f);
            var r = new Rect((W - w) * 0.5f, (H - h) * 0.5f, w, h);
            GUI.Box(r, GUIContent.none, boxStyle); GUI.Box(r, GUIContent.none, boxStyle);
            GUILayout.BeginArea(new Rect(r.x + 28, r.y + 20, r.width - 56, r.height - 40));
            switch (page)
            {
                case Page.Main: MainPage(); break;
                case Page.NewChase: NewChasePage(); break;
                case Page.Shop: ShopPage(); break;
                case Page.Pause: PausePage(); break;
                case Page.Summary: SummaryPage(); break;
            }
            GUILayout.EndArea();
            if (notice != null && Time.unscaledTime < noticeUntil)
                GUI.Label(new Rect(r.x, r.yMax + 8, r.width, 26), notice, new GUIStyle(body) { alignment = TextAnchor.MiddleCenter });
        }

        void Notice(string s) { notice = s; noticeUntil = Time.unscaledTime + 3f; }

        void MainPage()
        {
            var p = ChaseProfile.Current;
            GUILayout.BeginHorizontal();
            GUILayout.Label("NEVER STOP CHASING", title, GUILayout.Height(60));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{p.credits:n0} credits", head, GUILayout.Height(60));
            GUILayout.EndHorizontal();
            GUILayout.Label("Chase the storms of the Plains day after day. Photograph tornadoes for credits, buy a mesonet, keep the truck fuelled and fixed, and get some sleep.", body);
            GUILayout.Space(16);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(300));
            if (p.careerStarted && GUILayout.Button($"Continue career  ·  day {p.day}", button)) { ChaseSetup.BeginChase(); StartChase(); }
            if (p.careerStarted) GUILayout.Space(6);
            if (GUILayout.Button(p.careerStarted ? "New career" : "New career", button)) { page = Page.NewChase; seedText = ChaseSetup.seed.ToString(); }
            GUILayout.Space(6);
            if (GUILayout.Button("Free roam (the sandbox)", button)) { ChaseSetup.BeginFreeRoam(); Close(); game.ApplyState(); }
            GUILayout.Space(6);
            if (GUILayout.Button("Gear and shop", button)) { shopFrom = Page.Main; page = Page.Shop; }
            GUILayout.Space(6);
            if (!confirmReset) { if (GUILayout.Button("Reset chaser profile", button)) confirmReset = true; }
            else
            {
                GUILayout.Label("Credits and gear will be lost.", small);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Reset", button)) { ChaseProfile.Reset(); confirmReset = false; Notice("A new chaser."); }
                if (GUILayout.Button("Keep", button)) confirmReset = false;
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Quit", button)) Quit();
            GUILayout.EndVertical();
            GUILayout.Space(30);
            GUILayout.BeginVertical();
            GUILayout.Label("Your record", head);
            GUILayout.Label($"Days chased: {p.chases}     Best day: {p.bestChaseCredits:n0} cr     Earned in all: {p.creditsEarned:n0} cr", body);
            if (p.careerStarted) GUILayout.Label($"Career: day {p.day}, seed {p.careerSeed}     Motel nights: {p.motelNights}     Nights in the truck: {p.carCampNights}     Passed out: {p.passOuts}     Repairs: {p.repairs}     Fuel bought: {p.fuelBought:0} gal", body);
            GUILayout.Label($"Photos: {p.photos}     Tornadoes photographed: {p.tornadoesPhotographed}     Mesonet data: {p.mesonetSeconds / 60.0:0.0} min", body);
            GUILayout.Space(10);
            GUILayout.Label("Gear owned", head);
            foreach (var g in ChaseCatalog.Gear) if (g.Owned) GUILayout.Label($"•  {g.name}", body);
            GUILayout.Space(10);
            GUILayout.Label("How it plays", head);
            GUILayout.Label("Every day opens at 8 am on the forecast: pick the storm to play for and the truck stop to start from, and the day fast-forwards to the first towering cumulus. " +
                            "Two storms cross the map over an hour (T speeds the clock, ] skips 5 min, never back). E gets you out of the truck, X brings the camera up: a tornado in the frame, well exposed and sharp, pays best; the mesonet logs within a mile of one. " +
                            "Fuel up at a truck stop (hold F at a pump: regular, premium or diesel, whatever your truck takes), get the truck repaired at a mechanic, and sleep at a motel or on a truck stop's lot (F on the marked space) to save the day. " +
                            "Stay up and the sanity bar drains: slower on your feet and in the truck, but everything pays more... until you pass out.", small);
            GUILayout.Label($"Photos are saved to {ChaseCamera.PhotoDir}", small);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        void NewChasePage()
        {
            GUILayout.Label("New career", head);
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("Map", head);
            GUILayout.BeginHorizontal();
            foreach (var m in ChaseCatalog.Maps) if (GUILayout.Button(m.name, ChaseSetup.map == m.key ? tabOn : tabOff)) ChaseSetup.map = m.key;
            GUILayout.EndHorizontal();
            GUILayout.Label(ChaseSetup.Map.blurb, small);
            GUILayout.Space(10);
            GUILayout.Label("Vehicle", head);
            GUILayout.BeginHorizontal();
            foreach (var v in ChaseCatalog.Vehicles) if (GUILayout.Button(v.name, ChaseSetup.vehicle == v.key ? tabOn : tabOff)) ChaseSetup.vehicle = v.key;
            GUILayout.EndHorizontal();
            var veh = ChaseSetup.Vehicle;
            GUILayout.Label($"{veh.blurb}  ·  {veh.massKg:n0} kg, {veh.topKph * 0.621f:0} mph top speed", small);
            GUILayout.Space(10);
            GUILayout.Label("Gear", head);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Camera body", body, GUILayout.Width(120));
            foreach (var g in ChaseCatalog.Gear)
                if (g.kind == GearKind.CameraBody && g.Owned && GUILayout.Button(g.name, ChaseSetup.cameraBody == g.key ? tabOn : tabOff)) ChaseSetup.cameraBody = g.key;
            GUILayout.EndHorizontal();
            GUILayout.Label(ChaseSetup.CameraBody.blurb + $"  ·  ISO {ChaseSetup.CameraBody.isoMin}-{ChaseSetup.CameraBody.isoMax}", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lens", body, GUILayout.Width(120));
            foreach (var g in ChaseCatalog.Gear)
                if (g.kind == GearKind.Lens && g.Owned && GUILayout.Button(g.name, ChaseSetup.lens == g.key ? tabOn : tabOff)) ChaseSetup.lens = g.key;
            GUILayout.EndHorizontal();
            GUILayout.Label(ChaseSetup.Lens.blurb, small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mesonet", body, GUILayout.Width(120));
            if (ChaseProfile.Current.Owns("mesonet")) ChaseSetup.mountMesonet = GUILayout.Toggle(ChaseSetup.mountMesonet, " mount the roof mesonet", body);
            else GUILayout.Label($"not owned  ·  {ChaseCatalog.GearItem("mesonet").price:n0} cr in the shop", small);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            GUILayout.Label("Game", head);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed", body, GUILayout.Width(120));
            seedText = GUILayout.TextField(seedText ?? ChaseSetup.seed.ToString(), 8, GUILayout.Width(120));
            if (int.TryParse(seedText, out var sd)) ChaseSetup.seed = Mathf.Max(1, sd);
            if (GUILayout.Button("Random", GUILayout.Width(100))) { ChaseSetup.RandomSeed(); seedText = ChaseSetup.seed.ToString(); }
            float startAt = ChaseSetup.StartHourFor(ChaseSetup.seed);
            GUILayout.Label("the career's seed: every day's storms come from it and the day", small);
            GUILayout.EndHorizontal();
            GUILayout.Label("Half of all days initiate about noon, a third in the afternoon, one in seven at dusk and run into the night. A new career starts on day 1 with full tanks; the old one is lost.", small);
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start the career", button, GUILayout.Width(260))) { ChaseProfile.Current.StartCareer(ChaseSetup.seed); ChaseSetup.BeginChase(); StartChase(); }
            if (GUILayout.Button("Shop", button, GUILayout.Width(140))) { shopFrom = Page.NewChase; page = Page.Shop; }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Back", button, GUILayout.Width(140))) page = Page.Main;
            GUILayout.EndHorizontal();
        }

        void ShopPage()
        {
            var p = ChaseProfile.Current;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Gear and shop", head);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{p.credits:n0} credits", head);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            var sections = new (string title, GearKind[] kinds)[]
            {
                ("Instruments", new[] { GearKind.Mesonet }),
                ("Electronics", new[] { GearKind.Laptop, GearKind.Phone }),
                ("Drone", new[] { GearKind.Drone, GearKind.DroneUpgrade }),
                ("Camera bodies", new[] { GearKind.CameraBody }),
                ("Lenses", new[] { GearKind.Lens }),
            };
            foreach (var (sectionTitle, kinds) in sections)
            {
                GUILayout.Label(sectionTitle, head);
                foreach (var g in ChaseCatalog.Gear)
                {
                    if (System.Array.IndexOf(kinds, g.kind) < 0) continue;
                    GUILayout.BeginHorizontal(boxStyle);
                    GUILayout.BeginVertical();
                    GUILayout.Label(g.name, body);
                    GUILayout.Label(g.blurb, small);
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    if (g.Owned) GUILayout.Label(g.price > 0 ? "owned" : "starter gear", body, GUILayout.Width(140));
                    else if (!g.Available) GUILayout.Label($"needs the {ChaseCatalog.GearItem(g.requires)?.name}", small, GUILayout.Width(160));
                    else
                    {
                        GUI.enabled = p.CanAfford(g.price);
                        if (GUILayout.Button($"Buy  {g.price:n0} cr", button, GUILayout.Width(160)))
                        {
                            if (p.Buy(g))
                            {
                                Notice($"Bought the {g.name}.");
                                if (g.kind == GearKind.Mesonet) { ChaseSetup.mountMesonet = true; if (game.Mesonet && ChaseSetup.state != ChaseSetup.State.Menu) game.Mesonet.SetMounted(true); }
                                if (g.kind == GearKind.Drone && game.Drone && ChaseSetup.state != ChaseSetup.State.Menu) game.Drone.SetOwnedNow();
                            }
                        }
                        GUI.enabled = true;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(8);
            }
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Back", button, GUILayout.Width(140))) page = shopFrom;
        }

        void PausePage()
        {
            GUILayout.Label(ChaseSetup.state == ChaseSetup.State.Chase ? "Paused" : "Free roam", head);
            if (ChaseSetup.state == ChaseSetup.State.Chase)
            {
                var pr = ChaseProfile.Current; var vs = pr.Vehicle(ChaseSetup.vehicle);
                GUILayout.Label($"Day {pr.day}, {ChaseSetup.ClockText(ChaseSetup.hour)}  ·  {ChaseSetup.Map.name}  ·  {ChaseSetup.Vehicle.name}  ·  storms from {ChaseSetup.ClockText(ChaseSetup.startHour)}\nEarned today: {ChaseSetup.chaseCredits:n0} cr  ·  photos {ChaseSetup.chasePhotos}  ·  mesonet data {ChaseSetup.chaseMesonetSeconds / 60:0.0} min\nFuel {(vs.fuelGal < 0 ? ChaseSetup.Vehicle.tankGal : vs.fuelGal):0.0} gal  ·  truck {vs.condition:P0}  ·  sanity {pr.sanity:P0} ({pr.awakeHours:0.0} h awake)\nThe day is saved when you sleep; leaving now goes back to this morning.", body);
            }
            GUILayout.Space(16);
            if (GUILayout.Button("Resume", button, GUILayout.Width(300))) Close();
            GUILayout.Space(6);
            if (GUILayout.Button("Gear and shop", button, GUILayout.Width(300))) { shopFrom = Page.Pause; page = Page.Shop; }
            if (ChaseSetup.state == ChaseSetup.State.Chase)
            {
                GUILayout.Space(6);
                if (GUILayout.Button(ChaseSetup.devMode ? "Dev mode: on  (F3)" : "Dev mode: off  (F3)", button, GUILayout.Width(300))) game.SetDevMode(!ChaseSetup.devMode);
                if (ChaseSetup.devMode) GUILayout.Label("F free camera (V puts the truck under it), 1-9 jump to a spot, N new storms, T / P / [ ] the clock, G game mode, Tab the storm and lighting panel (its clock slider moves the day).", small);
            }
            GUILayout.Space(6);
            if (GUILayout.Button(ChaseSetup.state == ChaseSetup.State.Chase ? "Leave the day: main menu" : "Main menu", button, GUILayout.Width(300)))
            {
                if (ChaseSetup.state == ChaseSetup.State.Chase) game.EndChase();
                ChaseSetup.BackToMenu(); ChaseProfile.Current.Save();
                game.ApplyState();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Quit", button, GUILayout.Width(300))) { ChaseProfile.Current.Save(); Quit(); }
        }

        void SummaryPage()
        {
            var p = ChaseProfile.Current;
            GUILayout.Label("Day left behind", title, GUILayout.Height(60));
            GUILayout.Label($"Day {p.day}  ·  {ChaseSetup.Map.name}  ·  {ChaseSetup.Vehicle.name}", body);
            GUILayout.Space(10);
            GUILayout.Label($"Earned {ChaseSetup.chaseCredits:n0} credits today (kept)", head);
            GUILayout.Label($"Photos: {ChaseSetup.chasePhotos}     Mesonet data: {ChaseSetup.chaseMesonetSeconds / 60:0.0} min     Best day so far: {p.bestChaseCredits:n0} cr     Credits now: {p.credits:n0}\nThe day was not slept into: the career goes on from this morning's forecast.", body);
            GUILayout.Space(20);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Back to this morning", button, GUILayout.Width(280))) { ChaseSetup.BeginChase(); StartChase(); }
            if (GUILayout.Button("Gear and shop", button, GUILayout.Width(180))) { shopFrom = Page.Summary; page = Page.Shop; }
            if (GUILayout.Button("Main menu", button, GUILayout.Width(180))) { ChaseSetup.BackToMenu(); game.ApplyState(); }
            GUILayout.EndHorizontal();
        }

        /// <summary>Start the chase set up: on this map at once, or after loading the map's scene.</summary>
        public void StartChase()
        {
            ChaseSetup.BeginChase();
            ChaseProfile.Current.Save();
            Close();
            string sceneName = Path.GetFileNameWithoutExtension(ChaseSetup.Map.scene);
            if (SceneManager.GetActiveScene().name != sceneName) SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
            else game.ApplyState();
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
