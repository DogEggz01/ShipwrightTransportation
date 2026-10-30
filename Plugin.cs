using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ShipwrightAndTransport
{
    // ==== Plugin ====

    // Shipwright and Transport: ship storage, transport on paper, copies and name plates (SPEC.md). Shipwright and Storage's
    // 0.1.x line as its own mod. It depends on no other mod; every other mod is found at run time (decision 90). It never
    // loads beside Shipwright and Storage: both use the plate prefabs 650-652 (this mod also 653-655, Shipwright and Storage's
    // three plates in its own bundle), the copies' save indices 1010-2039 and the parking field.
    [BepInPlugin(Guid, PluginName, Version)]
    [BepInIncompatibility(StorageGuid)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.ShipwrightTransport", PluginName = "Shipwright and Transport", Version = "0.1.2";
        internal const string StorageGuid = "DogEggz.ShipwrightandStorage";

        internal static Plugin Instance;
        internal static ConfigEntry<KeyboardShortcut> StatusKey;
        internal static ConfigEntry<bool> InstantShips;
        private static Harmony harmony;
        private static bool latePatched;

        private void Awake()
        {
            Instance = this;
            var debugLog = Config.Bind("Debug", "Log", false, "Also log Shipwright's saving steps and the scenes the game loads.");
            StatusKey = Config.Bind("Debug", "Status key", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl),
                "Writes Shipwright's view of your ships, the shipyards and their fees to the BepInEx log.");
            InstantShips = Config.Bind("Building", "Instant ships", false,
                "On: ordering a ship finishes her at once, with no materials and no building time, and she goes straight into storage at that " +
                "shipyard. The building fee is still paid. An order already open finishes at the next clock check.");
            Log.Init(Logger, debugLog);
            PlateBundle.Load(Path.GetDirectoryName(Info.Location));
            Patch();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.Info(PluginName + " " + Version + " loaded");
        }

        // Class by class, so one patch that no longer fits the game doesn't stop the others.
        private static void Patch()
        {
            harmony = new Harmony(Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.Error("Patch " + type.Name + " failed: " + e.Message); }
            }
        }

        private void Update()
        {
            if (StatusKey.Value.IsDown()) Status.Write();
            Step(PlateFaces.Tick);
            Step(Notify.Tick);
            if (State.Off || !GameState.playing || GameState.currentlyLoading) return;
            Step(Parking.Tick);
            Step(() => Transport.Tick());
            Step(() => Orders.Tick());
            Step(DropAreas.Tick);
        }

        private static void Step(Action tick)
        {
            try { tick(); }
            catch (Exception e) { Log.Error("Tick failed: " + e); }
        }

        // How many scene loads Shipwright's handler has seen (SceneLoadedGuard).
        internal static int ScenesHandled;

        // The main scene (the one holding the save manager) loads after every plugin: the copy templates are taken there,
        // after its Awake and before its Start (§4.3, §7.1), and the patches on other mods are applied.
        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ScenesHandled++;
            Log.Debug("scene loaded: " + scene.name + " (build index " + scene.buildIndex + ", " + mode + ")");
            if (!SaveLoadManager.instance || SaveLoadManager.instance.gameObject.scene != scene) return;
            try { CopyEngine.CaptureTemplates(); }
            catch (Exception e) { Log.Error("Capturing the hull templates failed: " + e); }
            if (latePatched) return;
            latePatched = true;
            try { Compat.Mods.ApplyLatePatches(harmony); }
            catch (Exception e) { Log.Error("Patching other mods failed: " + e); }
        }
    }

    // Unity calls every sceneLoaded handler through one multicast delegate, so a handler that throws skips every one added
    // after it: BetterNPCBoats 0.3.0 throws on every scene load and loads before Shipwright, which then had no hull templates
    // and no patches on other mods. When Shipwright's handler didn't run in a call that threw, it runs after the others; the
    // order and the other handlers stay as they were, and the exception goes on to Unity's log.
    [HarmonyPatch(typeof(SceneManager), "Internal_SceneLoaded")]
    internal static class SceneLoadedGuard
    {
        private static bool reported;

        private static void Prefix(out int __state) => __state = Plugin.ScenesHandled;

        private static Exception Finalizer(Exception __exception, int __state, Scene __0, LoadSceneMode __1)
        {
            if (__exception == null || Plugin.ScenesHandled != __state) return __exception;
            if (!reported)
            {
                reported = true;
                Log.Warn("Another mod's sceneLoaded handler threw before Shipwright's (" + __exception.GetType().Name + ": " + __exception.Message +
                    "); Shipwright's runs after it, on this and every later scene load.");
            }
            Plugin.OnSceneLoaded(__0, __1);
            return __exception;
        }
    }

    // ==== Log ====

    // The BepInEx log. Debug lines are written only with [Debug] Log on; before Init (the tests) nothing is written.
    internal static class Log
    {
        private static ManualLogSource source;
        private static ConfigEntry<bool> debug;

        internal static void Init(ManualLogSource logSource, ConfigEntry<bool> debugLog) { source = logSource; debug = debugLog; }
        internal static bool Verbose => debug != null && debug.Value;

        internal static void Debug(string message) { if (Verbose) source?.LogInfo(message); }
        internal static void Info(string message) => source?.LogInfo(message);
        internal static void Warn(string message) => source?.LogWarning(message);
        internal static void Error(string message) => source?.LogError(message);
    }

    // ==== Notify ====

    // The vanilla notification scroll. It fits 29 characters a line and 3 lines (§12); a longer text is still shown,
    // and logged so it can be shortened.
    internal static class Notify
    {
        internal const int MaxLineLength = 29, MaxLines = 3;

        internal static bool Fits(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            var lines = text.Split('\n');
            if (lines.Length > MaxLines) return false;
            foreach (var line in lines)
                if (line.Length > MaxLineLength) return false;
            return true;
        }

        internal static void Show(string text, float seconds = 0f)
        {
            if (!Fits(text)) Log.Warn("Notification too long for the scroll: \"" + text.Replace("\n", " / ") + "\"");
            if (!NotificationUi.instance) return;
            if (seconds > 0f) NotificationUi.instance.ShowNotification(text, seconds);
            else NotificationUi.instance.ShowNotification(text);
        }

        // The clock's notifications, one after another for the scroll's 3 s each: a notification replaces the one showing
        // (NotificationUi.ShowNotification), so of two transports arriving in one check only the last was read.
        private static readonly Queue<string> waiting = new Queue<string>();
        private static float nextAt;
        internal const float Seconds = 3f;

        internal static void Later(string text)
        {
            waiting.Enqueue(text);
            Tick();
        }

        internal static void Tick()
        {
            if (waiting.Count == 0 || Time.time < nextAt) return;
            Show(waiting.Dequeue());
            nextAt = Time.time + Seconds;
        }
    }

    // ==== GameAccess ====

    // The game's private members Shipwright uses, looked up once. A missing member is logged once and the caller gets
    // a harmless default, so a game update degrades one feature instead of throwing.
    internal static class GameAccess
    {
        private static readonly HashSet<string> reported = new HashSet<string>();

        private static void Report(string member, Exception e = null)
        {
            // The whole exception, its stack included: the message alone didn't show where AdmitShip stopped (0.1.0's test).
            if (reported.Add(member)) Log.Error("Game member not found or failed: " + member + (e != null ? " (" + e + ")" : ""));
        }

        private static readonly MethodInfo CacheItems = AccessTools.Method(typeof(BoatLocalItems), "CacheItemsOnOutOfRange");
        private static readonly MethodInfo ItemExitBoat = AccessTools.Method(typeof(ShipItem), "ExitBoat");
        private static readonly FieldInfo ItemStayedEmbarkCol = AccessTools.Field(typeof(ShipItem), "currentlyStayedEmbarkCol");
        private static readonly MethodInfo AnchorRelease = AccessTools.Method(typeof(Anchor), "ReleaseAnchor");
        private static readonly FieldInfo ItemsLoaded = AccessTools.Field(typeof(BoatLocalItems), "itemsLoaded");
        private static readonly MethodInfo AdmitShipMethod = AccessTools.Method(typeof(Shipyard), "AdmitShip");
        private static readonly MethodInfo MoveShipMethod = AccessTools.Method(typeof(Shipyard), "MoveShip");
        private static readonly FieldInfo YardCurrentShip = AccessTools.Field(typeof(Shipyard), "currentShip");
        private static readonly FieldInfo HorizonCooldown = AccessTools.Field(typeof(BoatHorizon), "updateCooldown");
        private static readonly MethodInfo HorizonDistanceCheck = AccessTools.Method(typeof(BoatHorizon), "DistanceCheck");
        private static readonly FieldInfo BoatRecovered = AccessTools.Field(typeof(Recovery), "boatRecovered");
        private static readonly FieldInfo HintsInMissionUI = AccessTools.Field(typeof(Hints), "inMissionUI");
        private static readonly FieldInfo PCParent = AccessTools.Field(typeof(MissionListUI), "PCparent");
        private static readonly FieldInfo ShipyardUIRoot = AccessTools.Field(typeof(ShipyardUI), "ui");

        // Packs the cargo aboard into the ship's own cache, as when she goes out of range (§5.5 step 3), and marks the cache
        // loaded. A cache not loaded is unpacked by the game as soon as she is near the player and held still, and it holds
        // her still while it isn't (BoatLocalItems.SafeToEnableBoatPhysics): 0.1.2 left it unloaded, so the game unpacked
        // the cargo where she lay before she was moved, and it stayed there. UnpackWhenNear clears the mark at take-out.
        // A cache with items not spawned yet stays as it is (a second call would replace it with what is aboard now; Store ship
        // refuses such a ship, Storage.RefusalToStore); an empty one (HasLocalItems only means "not null") is packed again.
        // No crate of hers holds items (refused too, Storage.FilledCrates), so every item packed is a whole: loose goods,
        // sealed crates, empty crates and storage furniture, tools. An item that still counts as aboard her but lies in a crate
        // off her stays with its crate (StaysWithCrate). Returns the value of the cargo packed (Storage.CargoValue), for her fees,
        // and its weight (Weights.CargoPounds), for her speed. One method, no overload: the QA's watch (and any mod) finds it by
        // name (an overload made AccessTools.Method ambiguous and stopped the QA plugin, run 20261030-172350).
        internal static int PackCargo(BoatLocalItems items, out float pounds)
        {
            pounds = 0f;
            if (!items) return 0;
            int value = 0;
            try
            {
                var saveable = items.GetComponent<SaveableObject>();
                int index = saveable ? saveable.sceneIndex : 0;
                if (!CacheHoldsItems(items))
                {
                    LeaveWithTheirCrates(index);
                    FreeCrateScreen(index);
                    value = Storage.CargoValue(index);
                    pounds = Weights.CargoPounds(index, null);
                    CacheItems.Invoke(items, null);
                }
                items.SetItemsLoaded(true);
            }
            catch (Exception e) { Report("BoatLocalItems.CacheItemsOnOutOfRange", e); }
            return value;
        }

        // Her cache holds items the game hasn't unpacked yet: it sets the cache to null once it has spawned them
        // (BoatLocalItems.SpawnCachedItems).
        internal static bool CacheHoldsItems(BoatLocalItems items)
        {
            if (!items) return false;
            var cached = items.GetCachedItems();
            return cached != null && cached.Count > 0;
        }

        // An item aboard her (by its parent) stays with its crate when the crate is in the world (not packed or sunk with a
        // ship: parent -1 or above) and doesn't count as aboard her. Vanilla's crate ExitBoat leaves its items aboard, so a
        // crate carried or slid off her kept them, and her packing took them without it: they came back at the take-out
        // without a crate, and since 0.39.1 the game holds such an item for good (SPEC.md §5.5, step 3).
        internal static bool StaysWithCrate(int itemParent, int herIndex, int crateId, int itemId, bool crateInWorld, int crateParent) =>
            herIndex > 0 && itemParent == herIndex && crateId != 0 && crateId != itemId && crateInWorld && crateParent >= -1 && crateParent != herIndex;

        // Before her cargo is packed: each such item leaves her (LeaveShip), as NANDFixes' crate cascade does 0.2 s after a
        // crate leaves a ship. A crate aboard another ship (or in a house) gives the item its parent, so the game packs them
        // together there.
        private static void LeaveWithTheirCrates(int index)
        {
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (index <= 0 || prefabs == null) return;
            var byId = new Dictionary<int, SaveablePrefab>();
            foreach (var prefab in prefabs) if (prefab) byId[prefab.instanceId] = prefab;
            var leaving = new List<KeyValuePair<SaveablePrefab, int>>();
            foreach (var prefab in prefabs)
            {
                if (!prefab || prefab.currentCrateId == 0) continue;
                bool inWorld = byId.TryGetValue(prefab.currentCrateId, out var crate) && crate;
                int crateParent = inWorld ? crate.GetParentObject() : 0;
                if (StaysWithCrate(prefab.GetParentObject(), index, prefab.currentCrateId, prefab.instanceId, inWorld, crateParent))
                    leaving.Add(new KeyValuePair<SaveablePrefab, int>(prefab, crateParent));
            }
            if (leaving.Count == 0) return;
            var names = new List<string>();
            foreach (var pair in leaving)
            {
                // Not packed whatever ExitBoat did: CacheCurrentItems takes only items whose parent is hers.
                LeaveShip(pair.Key, pair.Value > 0 ? pair.Value : -1);
                names.Add(pair.Key.name);
            }
            Log.Info("Left " + leaving.Count + " item(s) in their crates off " + Ships.NameOf(index) + ", not packed with her: " + string.Join(", ", names.ToArray()) + ".");
        }

        // The crate screen keeps its place on a child of the crate last opened (CrateInventoryUI.ShowInventory parents its
        // localPosTracker to the crate) and makes a new one only while it shows: destroyed with a packed crate, the next crate
        // opened threw (Shipwright and Storage 0.1.3's test, its GameAccess.EmptyPackedCrates). An empty crate is packed from
        // 0.1.2, most likely the one just emptied, so the tracker leaves any crate of hers first.
        private static void FreeCrateScreen(int index)
        {
            var tracker = CrateInventoryUI.instance ? CrateUITracker?.GetValue(CrateInventoryUI.instance) as Transform : null;
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (!tracker || !tracker.parent || prefabs == null) return;
            foreach (var prefab in prefabs)
            {
                if (!prefab || prefab.GetParentObject() != index || !prefab.GetComponent<CrateInventory>() || !tracker.IsChildOf(prefab.transform)) continue;
                tracker.SetParent(null, true);
                return;
            }
        }

        private static readonly FieldInfo CrateUITracker = AccessTools.Field(typeof(CrateInventoryUI), "localPosTracker");

        // An item leaves the ship it counts aboard for good, as the game's own OnEnterInventory makes one leave: the boarding
        // trigger it last stood in is forgotten first (in a crate its colliders are off, so it never hears it left that trigger,
        // and two physics steps after ExitBoat it would board her again, ShipItem.ExtraFixedUpdate); then ShipItem.ExitBoat
        // (parent -1, out of her BoatMass, its transform under the world); then it takes the parent given (-1, or its crate's
        // ship or house). Its crate's LateUpdate keeps it where the crate is. Shared by the store (LeaveWithTheirCrates) and the
        // save (CopyEngine.LeaveForTheirCrates, code review R1).
        internal static void LeaveShip(SaveablePrefab prefab, int parent)
        {
            if (!prefab) return;
            var item = prefab.GetComponent<ShipItem>();
            if (item)
            {
                try { ItemStayedEmbarkCol.SetValue(item, null); }
                catch (Exception e) { Report("ShipItem.currentlyStayedEmbarkCol", e); }
                try { ItemExitBoat.Invoke(item, null); }
                catch (Exception e) { Report("ShipItem.ExitBoat", e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e); }
            }
            prefab.SetParentObject(parent);
        }

        // Taken out (§5.7): her cache is to be unpacked once her horizon counts her near the player again. True when it holds
        // items for the game to unpack.
        internal static bool UnpackWhenNear(BoatLocalItems items)
        {
            if (!items) return false;
            try
            {
                var cached = items.GetCachedItems();
                if (cached == null || cached.Count == 0) return false;
                items.SetItemsLoaded(false);
                return true;
            }
            catch (Exception e) { Report("BoatLocalItems.SetItemsLoaded", e); return false; }
        }

        // At a take-out, before her move (code review R2): the store's mark on her cache (PackCargo) is in memory only, and every
        // load clears it (SaveableObject.Load). Unmarked, the game unpacks her cache by itself as soon as the move brings her
        // near the player and holds her still, before the admit and before UnpackAfterSlide hands her to CrateTakeOut. Marked
        // again, only UnpackAfterSlide lets the game unpack it, as at a take-out in the session of the store. True when her
        // cache holds items.
        internal static bool KeepPacked(BoatLocalItems items)
        {
            if (!items) return false;
            try
            {
                var cached = items.GetCachedItems();
                if (cached == null || cached.Count == 0) return false;
                items.SetItemsLoaded(true);
                return true;
            }
            catch (Exception e) { Report("BoatLocalItems.SetItemsLoaded", e); return false; }
        }

        // A set anchor released as the game releases it when it is pulled loose (Anchor.ReleaseAnchor: not set, not kinematic,
        // its drag, its sound). True when it was set (0.1.6, B7).
        internal static bool ReleaseAnchor(Anchor anchor)
        {
            if (!anchor || !anchor.IsSet()) return false;
            try { AnchorRelease.Invoke(anchor, null); return true; }
            catch (Exception e) { Report("Anchor.ReleaseAnchor", e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e); return false; }
        }

        internal static bool AreItemsLoaded(BoatLocalItems items)
        {
            try { return items && (bool)ItemsLoaded.GetValue(items); }
            catch (Exception e) { Report("BoatLocalItems.itemsLoaded", e); return false; }
        }

        internal static bool AdmitShip(Shipyard yard, GameObject ship)
        {
            try { AdmitShipMethod.Invoke(yard, new object[] { ship }); return true; }
            catch (Exception e) { Report("Shipyard.AdmitShip", e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e); return false; }
        }

        // The game's documents click, Shipyard.ActivateDocuments, so every mod's patches on it run as when a player clicks them:
        // AllSailsAllShipyards puts its sail list in the yard in a prefix (MoreSailwindSails adds its sails there too), and its
        // ShowUI postfix reads that list. Calling AdmitShip alone skipped them, and the postfix threw on the yard's own list in
        // the middle of the admit (0.1.0's take-out, the user's test, 2026-10-28; §5.7). It admits only the one boat in the area,
        // a list the trigger fills on a later physics step: she is put in it for the call if she isn't there yet (the take-out
        // checked the area was empty) and taken out after, so the trigger keeps it as before. True when she was admitted whole.
        internal static bool ActivateDocuments(Shipyard yard, GameObject ship)
        {
            var area = Shipyards.BoatsInArea(yard);
            bool added = !area.Contains(ship);
            if (added) area.Add(ship);
            try { yard.ActivateDocuments(); }
            catch (Exception e) { Report("Shipyard.ActivateDocuments", e); return false; }
            finally { if (added) area.Remove(ship); }
            return GameState.currentShipyard == yard && yard.GetCurrentBoat() == ship;
        }

        // Shipyard.DischargeShip's steps but its CancelOrder, which throws after an admit that stopped before ResetOrder
        // (originalData unset): the menu closed, the camera and the mouse given back, the shipyard free.
        internal static void CloseHalfAdmit(Shipyard yard)
        {
            try
            {
                BoatCamera.instance.SwitchOff();
                YardCurrentShip.SetValue(yard, null);
                var rotator = yard.GetComponent<ShipyardCameraRotator>();
                if (rotator) rotator.enabled = false;
                if (ShipyardUI.instance) ShipyardUI.instance.HideUI();
                MouseLook.ToggleMouseLook(true);
                MouseLook.ToggleMouseLookAndCursor(true);
            }
            catch (Exception e) { Report("Shipyard.DischargeShip's steps", e); }
            finally
            {
                GameState.currentShipyard = null;
                GameState.currentBoat = null;
            }
        }

        // Shipyard.MoveShip to the release spot, at once and no longer kinematic, as DischargeShip moves her.
        internal static void ReleaseShip(Shipyard yard, Transform ship)
        {
            try { yard.StartCoroutine((IEnumerator)MoveShipMethod.Invoke(yard, new object[] { ship, yard.shipReleasePosition, false, true })); }
            catch (Exception e) { Report("Shipyard.MoveShip", e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e); }
        }

        // Makes the ship's horizon measure her distance now (BoatHorizon.DistanceCheck) and shows her as the game's switcher
        // would (BoatHorizonPerformanceSwitcher: her root active while she is nearer than fullySunkDistance); its 10-20 s pause
        // is cleared too. The horizon measures only in its Update, which returns at once while a shipyard menu is open: a ship
        // taken out from her far parking slot stayed switched off in the menu until the player left it and came back (the
        // user's test of 0.1.0, 2026-10-28; a new copy too, cloned with her template's far distance).
        internal static void RefreshHorizon(Component boat)
        {
            if (!boat) return;
            try
            {
                foreach (var horizon in boat.GetComponentsInChildren<BoatHorizon>(true))
                {
                    HorizonCooldown.SetValue(horizon, 0f);
                    HorizonDistanceCheck.Invoke(horizon, null);
                    var root = horizon.transform.parent;
                    if (root && !root.gameObject.activeSelf && horizon.distanceToPlayer < horizon.fullySunkDistance) root.gameObject.SetActive(true);
                }
            }
            catch (Exception e) { Report("BoatHorizon.DistanceCheck", e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e); }
        }

        internal static void SetBoatRecovered()
        {
            try { BoatRecovered.SetValue(null, true); }
            catch (Exception e) { Report("Recovery.boatRecovered", e); }
        }

        // The tutorial hints start a 12 s mission hint when the mission board closes; Shipwright's board isn't one.
        internal static void ForgetMissionUI()
        {
            try { if (Hints.instance) HintsInMissionUI.SetValue(Hints.instance, false); }
            catch (Exception e) { Report("Hints.inMissionUI", e); }
        }

        internal static Transform MissionBoardAnchor(MissionListUI list)
        {
            try { return list ? (Transform)PCParent.GetValue(list) : null; }
            catch (Exception e) { Report("MissionListUI.PCparent", e); return null; }
        }

        internal static GameObject ShipyardMenu(ShipyardUI ui)
        {
            try { return ui ? (GameObject)ShipyardUIRoot.GetValue(ui) : null; }
            catch (Exception e) { Report("ShipyardUI.ui", e); return null; }
        }

        internal static T Get<T>(object instance, string field) where T : class
        {
            try { return instance == null ? null : AccessTools.Field(instance.GetType(), field).GetValue(instance) as T; }
            catch (Exception e) { Report(instance.GetType().Name + "." + field, e); return null; }
        }
    }

    // ==== GameClock ====

    // Game time (§4.6). Absolute hours = GameState.day × 24 + Sun.sun.globalTime; both are saved, so clocks built on
    // them run on through sleep, time skips and save/load.
    internal static class GameClock
    {
        internal static int Day => GameState.day;
        internal static double Hours => GameState.day * 24.0 + (Sun.sun ? Sun.sun.globalTime : 0f);

        // The day an absolute hour falls on, as GameState.day counts it.
        internal static int DayOf(double hours) => (int)Math.Floor(hours / 24.0);
    }

    // ==== Money ====

    // The fees of §4.6, in single precision as the game computes its shipyard fee. A value is in units; coins are
    // units × the currency's rate.
    internal static class Fees
    {
        // Mathf.RoundToInt: Math.Round on the float, halves to even.
        internal static int Round(float value) => (int)Math.Round(value);

        // The vanilla shipyard fee in units (Shipyard.GetShipyardFee): price × 3.5.
        internal static int DailyUnits(int price) => Round((float)price * 3.5f);

        internal static int Coins(int units, float rate) => Round((float)(units * rate));

        // One shipyard fee a day, in the storing shipyard's currency (decision 3).
        internal static int DailyStorage(int price, float rate) => Coins(DailyUnits(price), rate);

        // Her cargo in the fees (SPEC.md §4.6, the user: "Add 15% of all item's value on ship to transportation fee, and
        // 1% as storage fee (daily)"; 0.1.2: "change the cargo value portion in transport fee to 20%"). cargoUnits is the sum of
        // the items' ShipItem.value aboard her, the game's base value, in the units the fees use (an item's coins are its value
        // × the rate, as a shop's). Each share is rounded to whole units.
        internal const float CargoDailyShare = 0.01f, CargoTransportShare = 0.2f;

        internal static int CargoDailyUnits(int cargoUnits) => Round((float)Math.Max(0, cargoUnits) * CargoDailyShare);

        internal static int CargoTransportUnits(int cargoUnits) => Round((float)Math.Max(0, cargoUnits) * CargoTransportShare);

        // A stored ship's daily fee in units: her ship's (the shipyard fee) and 1% of her cargo.
        internal static int StorageUnits(int shipUnits, int cargoUnits) => shipUnits + CargoDailyUnits(cargoUnits);

        // Midnights passed: storing and collecting on the same day is free (decision 30).
        internal static int StorageDays(int today, int dayStored) => Math.Max(0, today - dayStored);

        internal static int StorageDue(int days, int daily) => (int)Math.Min(int.MaxValue, Math.Max(0L, (long)days * daily));

        // Half the ship's value in local coins, paid when ordering (decision 13). The price is in gold, worth 0.006 a unit.
        internal static int BuildingFee(int price, float rate)
        {
            float half = (float)price * 0.5f;
            float local = half * rate;
            return Round(local / 0.006f);
        }

        // Units per metre of length per mile, in the ordering shipyard's currency (SPEC.md §6; the user, 2026-10-29:
        // "Normal = 0.6 x length", "Other shipyard = 0.8 x length"; the tow's two legs 1 × length, as first given).
        internal const float NormalPerMetre = 0.6f, OtherYardPerMetre = 0.8f, TowPerMetre = 1f;

        internal static int TransportUnits(float lengthM, int miles, float perMetre)
        {
            float perMile = perMetre * lengthM;
            return Round(perMile * miles);
        }

        // With her cargo: 20% of its value on top of the ship's units (a transport, or a tow's tow leg).
        internal static int TransportFee(float lengthM, int miles, int cargoUnits, float rate, float perMetre) =>
            Coins(TransportUnits(lengthM, miles, perMetre) + CargoTransportUnits(cargoUnits), rate);

        // A tow's tug leg, the tug's run to her: her length × the miles, no cargo share ("tug run to ship: RoundToInt(lengthM ×
        // miles) × rateOrigin").
        internal static int TugFee(float lengthM, int miles, float rate) => Coins(TransportUnits(lengthM, miles, TowPerMetre), rate);

        // Fixed at the starting rates, whatever the day's rates: 15 Gold Lions' worth (decision 62). By currency index.
        internal static readonly int[] PlatePrice = { 550, 4750, 1250, 15 };

        // A plate's price at its size (0.1.6, the user: "make plate price scale as plate size"): the fixed price × the size,
        // rounded half up, at least 1. In decimal, the size to one place (0.9f is 0.8999…, which made 15 × 0.9 round down).
        internal static int PlatePriceAt(int currency, float scale) =>
            Math.Max(1, (int)Math.Round(PlatePrice[currency] * Math.Round((decimal)PlateData.CleanScale(scale), 1), MidpointRounding.AwayFromZero));

        // Transport speed (decisions 24, 27): 0.7 × the hull speed 1.34 × √(length in feet). A boat under 32 ft rides a
        // 100 ft carrier, so she makes its speed; her fee still uses her own length.
        internal const float FeetPerMetre = 3.28084f, CarriedBelowMetres = 9.75f;

        internal static float TransportKnots(float lengthM)
        {
            float feet = lengthM < CarriedBelowMetres ? 100f : lengthM * FeetPerMetre;
            return 0.7f * 1.34f * (float)Math.Sqrt(feet);
        }

        // Loaded, slower (the user, 2026-10-30: "If no weight it's as current. The more it load the slower it gets"). The game's
        // sails push a hull with a force (Sail.ApplyForce, ForceMode.Force) and the water's drag slows it by k·v³ + 2k·v whatever
        // its mass (Crest's BoatProbes, ForceMode.Acceleration; HullDrag), so at a steady speed v³ goes as 1 ÷ mass, and its
        // linear parts a little more: the knots × (M ÷ (M + cargo))^0.37, M her own mass (Weights.ShipMass), at least half. The
        // exponent as sailed (SPEC.md §6.1, the probe run 20261030-195201: the Cog, Sanbuq and Brig empty and loaded, 0.36-0.40,
        // 0.372 fitted; the user: "Yes, use 0.37"; ⅓ before). A boat a carrier takes isn't slowed; no cargo, or no mass
        // measured (a record from before 0.1.2), keeps the speed.
        internal const float SlowestLoadFactor = 0.5f;
        internal const double LoadExponent = 0.37;

        internal static float LoadFactor(float massLb, float cargoLb) =>
            massLb <= 0f || cargoLb <= 0f ? 1f : Math.Max(SlowestLoadFactor, (float)Math.Pow(massLb / (massLb + cargoLb), LoadExponent));

        internal static float TransportKnots(float lengthM, float massLb, float cargoLb) =>
            TransportKnots(lengthM) * (lengthM < CarriedBelowMetres ? 1f : LoadFactor(massLb, cargoLb));

        internal static double SeaHours(int miles, float knots) => knots > 0f ? miles / (double)knots : 0.0;

        // "10 days at sea"; under a day, hours (§10.3).
        internal static string AtSea(double hours)
        {
            if (hours < 24.0)
            {
                int h = Math.Max(1, (int)Math.Round(hours));
                return h + (h == 1 ? " hour at sea" : " hours at sea");
            }
            int days = Round((float)(hours / 24.0));
            return days + (days == 1 ? " day at sea" : " days at sea");
        }

        // "3 days", or hours under a day; whole days rounded down (§10.3, the building page).
        internal static string Remaining(double hours)
        {
            if (hours < 24.0)
            {
                int h = Math.Max(1, (int)Math.Ceiling(hours));
                return h + (h == 1 ? " hour" : " hours");
            }
            int days = (int)Math.Floor(hours / 24.0);
            return days + (days == 1 ? " day" : " days");
        }
    }

    // The player's money, as the vanilla shipyard handles it.
    internal static class Money
    {
        internal static readonly string[] Names = { "Al'Ankh Lions", "Emerald Dragons", "Aestrin Crowns", "Gold Lions" };
        internal static readonly string[] Short = { "Lions", "Dragons", "Crowns", "Gold" };
        internal static readonly float[] StartingRates = { 0.22f, 1.9f, 0.5f, 0.006f };

        // Today's rate (CurrencyMarket.currentPrices; EconomyOverhaul writes its own rates there).
        internal static float Rate(int currency)
        {
            var market = CurrencyMarket.instance;
            return market && market.currentPrices != null && currency >= 0 && currency < market.currentPrices.Length
                ? market.currentPrices[currency] : StartingRates[currency];
        }

        // The board writes amounts without separators ("1879").
        internal static string Amount(int amount) => amount.ToString(CultureInfo.InvariantCulture);

        // Like Shipyard.ConfirmOrder: refuse when short, else take the coins, play the sound and write the day log.
        internal static bool Pay(int currency, int amount)
        {
            if (amount < 0 || PlayerGold.currency == null) return false;
            if (PlayerGold.currency[currency] < amount) { Notify.Show("Not enough money."); return false; }
            PlayerGold.currency[currency] -= amount;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound();
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(-amount, TransactionCategory.boat);
            return true;
        }

        // Gives back what Pay took when the step it paid for fails.
        internal static void Refund(int currency, int amount)
        {
            if (amount <= 0 || PlayerGold.currency == null) return;
            PlayerGold.currency[currency] += amount;
            if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(amount, TransactionCategory.boat);
        }
    }

    // ==== Ports ====

    // The game's ports (Port.ports, in the main scene, so always loaded). Entries can be null, and port 7 is a test port
    // beside Neverdin that would otherwise be "nearest" there; it is skipped, as EconomyOverhaul skips it.
    internal static class Ports
    {
        internal const int TestPort = 7;

        internal static IEnumerable<Port> All()
        {
            if (Port.ports == null) yield break;
            foreach (var port in Port.ports)
                if (port && port.portIndex != TestPort) yield return port;
        }

        internal static Port Nearest(Vector3 position)
        {
            Port best = null; float bestDistance = float.MaxValue;
            foreach (var port in All())
            {
                float distance = (port.transform.position - position).sqrMagnitude;
                if (distance < bestDistance) { best = port; bestDistance = distance; }
            }
            return best;
        }

        internal static Port ByName(string name)
        {
            foreach (var port in All())
                if (port.GetPortName() == name) return port;
            return null;
        }
    }

    // ==== Shipyards ====

    // A shipyard, known by the name of the port nearest its documents (§4.6).
    internal sealed class ShipyardInfo
    {
        internal readonly string Name;
        internal readonly int Currency;       // Shipyard.region: 0 Lions, 1 Dragons, 2 Crowns
        internal readonly int[] Builds;       // the hulls it builds, by save index (decision 10)
        internal readonly bool OldChronian;   // exists only with Old Chronian (decision 31)

        internal ShipyardInfo(string name, int currency, int[] builds, bool oldChronian)
        {
            Name = name; Currency = currency; Builds = builds; OldChronian = oldChronian;
        }
    }

    internal static class Shipyards
    {
        internal static readonly ShipyardInfo[] Table =
        {
            new ShipyardInfo("Gold Rock City", 0, new[] { 10, 20, 30 }, false),
            new ShipyardInfo("Fort Aestrin", 2, new[] { 40, 50 }, false),
            new ShipyardInfo("Dragon Cliffs", 1, new[] { 90, 80 }, false),
            new ShipyardInfo("Kicia Bay", 1, new[] { 70 }, false),
            // Old Chronian's FORT shipyard: storage and transport only (decision 16).
            new ShipyardInfo("Chronos", 2, new int[0], true),
        };

        internal static ShipyardInfo ByName(string name)
        {
            foreach (var info in Table)
                if (info.Name == name) return info;
            return null;
        }

        internal static bool Present(ShipyardInfo info) => !info.OldChronian || Compat.Mods.OldChronian;

        // The shipyards on the islands loaded now; they load and unload with their islands.
        internal static Shipyard[] Loaded() => UnityEngine.Object.FindObjectsOfType<Shipyard>();

        internal static ShipyardDocuments Documents(Shipyard yard)
        {
            foreach (var documents in UnityEngine.Object.FindObjectsOfType<ShipyardDocuments>())
                if (documents.shipyard == yard) return documents;
            return null;
        }

        internal static Vector3 DocumentsPosition(Shipyard yard)
        {
            var documents = Documents(yard);
            return documents ? documents.transform.position : yard.transform.position;
        }

        internal static string PortOf(Shipyard yard)
        {
            var port = yard ? Ports.Nearest(DocumentsPosition(yard)) : null;
            return port ? port.GetPortName() : null;
        }

        // Null for a shipyard Shipwright doesn't serve: one another mod adds near another port, or one a mod adds at a port
        // with a vanilla shipyard (the user, 2026-10-28: "ignore modded shipyard, always use vanilla shipyard"; Better Ports'
        // drydock at Fort Aestrin holds a whole second shipyard). Remembered per shipyard object, since the documents' look
        // text asks every frame.
        private static readonly Dictionary<int, ShipyardInfo> known = new Dictionary<int, ShipyardInfo>();

        internal static ShipyardInfo InfoOf(Shipyard yard)
        {
            if (!yard) return null;
            int id = yard.GetInstanceID();
            if (known.TryGetValue(id, out var info)) return info;
            info = ByName(PortOf(yard));
            if (info != null && !info.OldChronian && IsModded(yard.transform))
            {
                Log.Info("Shipyard '" + yard.name + "' near " + info.Name + " was added by a mod; only the game's own shipyard there is served.");
                info = null;
            }
            if (info != null && info.Currency != yard.region)
                Log.Warn("Shipyard " + info.Name + " uses currency " + yard.region + ", not " + info.Currency + "; its own is used.");
            known[id] = info;
            return info;
        }

        // The game's own shipyards are scene objects; a mod puts its own in with Instantiate, so it or a parent is named
        // "(Clone)" (Better Ports: `drydock(Clone)/shipyard Aestrin long`). Old Chronian's Chronos yard, the only one at its
        // port, is served whatever it is (decision 16).
        internal static bool IsModded(Transform yard)
        {
            for (var t = yard; t; t = t.parent)
                if (IsCloneName(t.name)) return true;
            return false;
        }

        internal static bool IsCloneName(string name) => name != null && name.EndsWith("(Clone)", StringComparison.Ordinal);

        // Shipyard.boatsInTrigger (private): the boats in the shipyard area, as their root objects.
        private static AccessTools.FieldRef<Shipyard, List<GameObject>> boatsInTrigger;
        private static bool boatsInTriggerTried;

        internal static List<GameObject> BoatsInArea(Shipyard yard)
        {
            if (!boatsInTriggerTried)
            {
                boatsInTriggerTried = true;
                try { boatsInTrigger = AccessTools.FieldRefAccess<Shipyard, List<GameObject>>("boatsInTrigger"); }
                catch (Exception e) { Log.Error("Shipyard.boatsInTrigger not found: " + e.Message); }
            }
            var boats = yard && boatsInTrigger != null ? boatsInTrigger(yard) : null;
            return boats ?? new List<GameObject>();
        }
    }

    // ==== Ships ====

    // A ship the player owns (§4.7), keyed by the save index of the SaveableObject on her root. Boat is null for a
    // finished copy that has not been taken out yet (§7.3); Hull is the vanilla hull a copy was built from.
    internal sealed class OwnedShip
    {
        internal int Index;
        internal int Hull;
        internal PurchasableBoat Boat;
        internal string Type;
        internal int Number;
        internal string Name;
        internal int Price;
        internal bool IsCopy => CopyEngine.IsCopyIndex(Index);
    }

    internal static class Ships
    {
        // Every ship the player owns: purchased (SaveableObject.extraSetting), a boat (BoatRefs), not a house, and not
        // rented from EconomyOverhaul (decision 85; those go to `rented`), plus finished copies not taken out yet. Read
        // from the save manager's registry, as the game's rescue does: the game switches ships beyond 10 km off, and a
        // scene search skips them.
        internal static List<OwnedShip> Owned(List<PurchasableBoat> rented = null)
        {
            var ships = new List<OwnedShip>();
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            if (objects != null)
                foreach (var saveable in objects)
                {
                    if (!saveable || saveable.sceneIndex <= 0 || !saveable.extraSetting) continue;
                    var boat = saveable.GetComponent<PurchasableBoat>();
                    if (!boat || boat.isHouse || !saveable.GetComponent<BoatRefs>()) continue;
                    if (Compat.EconomyOverhaul.IsRented(saveable.sceneIndex)) { rented?.Add(boat); continue; }
                    ships.Add(Describe(saveable.sceneIndex, boat));
                }
            foreach (var copy in State.Save.copies)
                if (!copy.spawned && Storage.RecordOf(CopyEngine.IndexOf(copy.k)) != null)
                    ships.Add(Describe(CopyEngine.IndexOf(copy.k), null));
            ships.Sort((a, b) => a.Type != b.Type ? string.CompareOrdinal(a.Type, b.Type) : a.Number.CompareTo(b.Number));
            return ships;
        }

        internal static OwnedShip Find(int index)
        {
            foreach (var ship in Owned())
                if (ship.Index == index) return ship;
            return null;
        }

        // Type, number, name and price of a ship. A copy takes her hull's type and her own number (decision 50).
        internal static OwnedShip Describe(int index, PurchasableBoat boat)
        {
            var ship = new OwnedShip { Index = index, Hull = index, Boat = boat, Number = 1 };
            var copy = CopyEngine.RecordOfIndex(index);
            if (copy != null)
            {
                ship.Hull = copy.hull;
                ship.Number = copy.number;
                ship.Type = ShipTypes.Name(copy.hull, null);
                var original = OriginalBoat(copy.hull);
                ship.Price = boat ? boat.price : original ? original.price : 0;
            }
            else
            {
                ship.Type = ShipTypes.Name(index, boat ? boat.name : null);
                ship.Price = boat ? boat.price : 0;
            }
            ship.Name = ShipNames.Display(State.Save, index, ship.Type, ship.Number);
            return ship;
        }

        internal static string NameOf(int index) => Describe(index, BoatOf(index)).Name;

        // The live boat with this save index, switched on or off.
        internal static PurchasableBoat BoatOf(int index)
        {
            var manager = SaveLoadManager.instance;
            if (!manager || index <= 0) return null;
            var objects = manager.GetCurrentObjects();
            if (objects == null || index >= objects.Length || !objects[index]) return null;
            return objects[index].GetComponent<PurchasableBoat>();
        }

        // The game's own ship of a vanilla hull (its save index is the hull's).
        internal static PurchasableBoat OriginalBoat(int hull) => BoatOf(hull);

        internal static int IndexOf(Component boat)
        {
            var saveable = boat ? boat.GetComponent<SaveableObject>() : null;
            return saveable ? saveable.sceneIndex : -1;
        }

        // What the game knows of where she is (§4.7), and Shipwright's records.
        internal static ShipFacts Facts(OwnedShip ship, Shipyard here)
        {
            var facts = new ShipFacts();
            var stored = Storage.RecordOf(ship.Index);
            var transport = Transport.RecordOf(ship.Index);
            if (transport != null && Transport.HasLeft(transport))
            {
                facts.GoingTo = transport.to;
                facts.ArrivesDay = GameClock.DayOf(transport.arriveHour);
                facts.UnderTow = transport.tow;
            }
            // A tow not picked up yet: she is still where she lies.
            else if (transport != null && transport.tow) facts.TowTo = transport.to;
            else if (stored != null)
            {
                facts.StoredAt = stored.shipyard;
                if (transport != null)
                {
                    facts.WaitingForSailors = true;
                    facts.LeavesDay = GameClock.DayOf(transport.departHour);
                }
            }
            var boat = ship.Boat;
            if (!boat || stored != null || transport != null) return facts;
            if (here) facts.InThisShipyard = Shipyards.BoatsInArea(here).Contains(boat.gameObject);
            var damage = boat.GetComponent<BoatDamage>();
            facts.Sunk = damage && damage.sunk;
            var mooring = boat.GetComponent<BoatMooringRopes>();
            if (mooring)
            {
                // The ropes only: AnyRopeMoored() is also true when just the anchor is set.
                if (mooring.ropes != null)
                    foreach (var rope in mooring.ropes)
                        if (rope && rope.IsMoored()) { facts.RopeTied = true; break; }
                facts.AnchorSet = mooring.anchor && mooring.anchor.IsSet();
            }
            var port = Ports.Nearest(boat.transform.position);
            if (port)
            {
                facts.NearestPort = port.GetPortName();
                facts.NearestPortMiles = Miles.ExactBetween(port.transform.position, boat.transform.position);
            }
            return facts;
        }

        // The ship list of a shipyard (§5.3, decision 79): stored here, on the way here, in this shipyard's area, then
        // the rest, each group by type and number.
        internal static List<KeyValuePair<OwnedShip, ShipFacts>> ListFor(string shipyard, Shipyard here)
        {
            var groups = new List<KeyValuePair<OwnedShip, ShipFacts>>[4];
            for (int i = 0; i < groups.Length; i++) groups[i] = new List<KeyValuePair<OwnedShip, ShipFacts>>();
            foreach (var ship in Owned())
            {
                var facts = Facts(ship, here);
                var where = Whereabouts.Decide(facts, shipyard);
                int group = where == Where.StoredHere || where == Where.LeavingHere ? 0
                    : where == Where.ArrivingHere ? 1 : where == Where.InShipyard ? 2 : 3;
                groups[group].Add(new KeyValuePair<OwnedShip, ShipFacts>(ship, facts));
            }
            var list = new List<KeyValuePair<OwnedShip, ShipFacts>>();
            foreach (var group in groups) list.AddRange(group);
            return list;
        }
    }

    // Type names by save index (§4.7).
    internal static class ShipTypes
    {
        private static readonly Dictionary<int, string> ByIndex = new Dictionary<int, string>
        {
            { 10, "Dhow" }, { 20, "Sanbuq" }, { 30, "Baghlah" }, { 40, "Cog" }, { 50, "Brig" }, { 70, "Jong" }, { 80, "Junk" },
            { 90, "Kakam" }, { 144, "Happy Bay boat" }, { 153, "Clipper" }, { 160, "Sh'ba" }, { 182, "Gloriana" },
            { 187, "Chronian" }, { 192, "Caelanor" }, { 197, "Gallus" }, { 207, "Leopard" }, { 212, "Leopard rowboat" },
        };

        // The eight vanilla hulls Shipwright can copy (§7).
        internal static readonly int[] VanillaHulls = { 10, 20, 30, 40, 50, 70, 80, 90 };

        internal static bool IsVanillaHull(int index) => System.Array.IndexOf(VanillaHulls, index) >= 0;

        // The Dinghies cutter takes the first free index, so it is known by its object name. Any other ship reads her root
        // object's name without "BOAT " and "(n)": "BOAT MYSTERY (240)" is "Mystery".
        internal static string Name(int index, string objectName)
        {
            string clean = Clean(objectName);
            if (clean == "DNG Cutter") return "Dinghy cutter";
            if (ByIndex.TryGetValue(index, out string name)) return name;
            return clean.Length == 0 ? "Ship" : SentenceCase(clean);
        }

        internal static string Clean(string objectName) =>
            Regex.Replace(objectName ?? "", @"\(Clone\)|\(\d+\)", "").Replace("BOAT ", "").Trim();

        // A name in capitals reads in sentence case, as EconomyOverhaul writes it: "GLORIANA" is "Gloriana".
        private static string SentenceCase(string name) =>
            name.Length > 1 && name.ToUpperInvariant() == name ? name.Substring(0, 1) + name.Substring(1).ToLowerInvariant() : name;
    }

    // Names (decisions 48, 50; §5.9). The default is "Type #n": #1 is the ship from the game or a ship mod,
    // Shipwright's copies count on from #2, and a number is never given twice. A typed name replaces the default.
    internal static class ShipNames
    {
        internal const int MaxLength = 16;

        // The board fonts have glyphs for these only: letters, digits, space and ' - . &.
        internal static bool Allowed(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
            c == ' ' || c == '\'' || c == '-' || c == '.' || c == '&';

        // What a typed name keeps: the allowed characters, at most 16, without outer spaces. "" brings "Type #n" back.
        internal static string Clean(string typed)
        {
            if (string.IsNullOrEmpty(typed)) return "";
            var kept = new StringBuilder();
            foreach (char c in typed)
                if (Allowed(c) && kept.Length < MaxLength) kept.Append(c);
            return kept.ToString().Trim();
        }

        internal static string Default(string type, int number) => type + " #" + number;

        internal static string Typed(ShipwrightSave save, int index)
        {
            if (save?.names != null)
                foreach (var name in save.names)
                    if (name.saveIndex == index) return name.typed ?? "";
            return "";
        }

        internal static string Display(ShipwrightSave save, int index, string type, int number)
        {
            string typed = Typed(save, index);
            return typed.Length > 0 ? typed : Default(type, number);
        }

        // Renaming is free, and two ships may share a name.
        internal static void SetTyped(ShipwrightSave save, int index, string typed)
        {
            string clean = Clean(typed);
            save.names.RemoveAll(n => n.saveIndex == index);
            if (clean.Length > 0) save.names.Add(new ShipName { saveIndex = index, typed = clean });
        }

        // The number the next finished copy of a type gets, without giving it yet (the order page shows it).
        internal static int PeekNumber(ShipwrightSave save, string type)
        {
            var counter = save.typeNumbers.Find(c => c.type == type);
            return (counter == null || counter.last < 1 ? 1 : counter.last) + 1;
        }

        // Gives the number for the next finished copy of a type, so a cancelled order uses none.
        internal static int NextNumber(ShipwrightSave save, string type)
        {
            var counter = save.typeNumbers.Find(c => c.type == type);
            if (counter == null) { counter = new TypeCounter { type = type, last = 1 }; save.typeNumbers.Add(counter); }
            if (counter.last < 1) counter.last = 1;
            return ++counter.last;
        }
    }

    // ==== Whereabouts ====

    internal enum Where { InShipyard, StoredHere, LeavingHere, ArrivingHere, StoredElsewhere, OnTheWay, TowBooked, Sunk, Moored, Anchored, AtSea }

    // What is known of a ship's place. The game fills the first group; Shipwright's records fill the second from
    // phase 1 on.
    internal struct ShipFacts
    {
        internal bool InThisShipyard, Sunk, RopeTied, AnchorSet;
        internal string NearestPort;
        internal float NearestPortMiles;

        internal string StoredAt;           // the shipyard she is stored at, or null
        internal bool WaitingForSailors;    // stored, with a booked transport that has not left yet
        internal string GoingTo;            // in transit: the destination shipyard, or null
        internal int LeavesDay, ArrivesDay;
        internal bool UnderTow;             // in transit behind a tug
        internal string TowTo;              // a tow booked, not picked up yet: its destination, or null
    }

    // Where a ship is (§4.7): the first rule that matches wins. `here` is the board's shipyard, or null.
    internal static class Whereabouts
    {
        // Game miles (64.3 m each). The draft's 1 and 5 were too small: the docks stand 35–95 m from their port's position.
        internal const float MooredAtMiles = 30f, AnchoredNearMiles = 80f, AtSeaNearMiles = 1f;

        internal static Where Decide(ShipFacts f, string here)
        {
            if (f.TowTo != null) return Where.TowBooked;
            if (f.InThisShipyard) return Where.InShipyard;
            if (f.StoredAt != null && f.StoredAt == here) return f.WaitingForSailors ? Where.LeavingHere : Where.StoredHere;
            if (f.GoingTo != null && f.GoingTo == here) return Where.ArrivingHere;
            if (f.StoredAt != null) return Where.StoredElsewhere;
            if (f.GoingTo != null) return Where.OnTheWay;
            if (f.Sunk) return Where.Sunk;
            if (f.RopeTied) return Where.Moored;
            if (f.AnchorSet && f.NearestPort != null && f.NearestPortMiles <= AnchoredNearMiles) return Where.Anchored;
            return Where.AtSea;
        }

        // The ship page's line ("Moored: at Fort Aestrin"): its label and value blocks on the board. A ship stored here,
        // leaving or arriving has the storage page instead.
        internal static string Line(Where where, ShipFacts f)
        {
            string label = Label(where, f), value = Value(where, f);
            return value.Length == 0 ? label.TrimEnd(':') : label + " " + value;
        }

        internal static string Label(Where where, ShipFacts f)
        {
            switch (where)
            {
                case Where.InShipyard: case Where.Moored: return "Moored:";
                case Where.StoredElsewhere: return "Stored:";
                case Where.OnTheWay: return f.UnderTow ? "Under tow:" : "On the way:";
                case Where.TowBooked: return "Tow booked:";
                case Where.Sunk: return "Sunk:";
                case Where.Anchored: return "Anchored:";
                case Where.AtSea: return "At sea:";
                default: return "";
            }
        }

        internal static string Value(Where where, ShipFacts f)
        {
            switch (where)
            {
                case Where.InShipyard: return "in the shipyard";
                case Where.StoredElsewhere: return "at " + f.StoredAt;
                case Where.OnTheWay: return "to " + f.GoingTo + ", day " + f.ArrivesDay;
                case Where.TowBooked: return "to " + f.TowTo;
                case Where.Sunk: return f.NearestPort == null ? "" : "near " + f.NearestPort;
                case Where.Moored: return f.NearestPort == null ? "" : AtOrNear(f);
                case Where.Anchored: return "near " + f.NearestPort;
                case Where.AtSea:
                    if (f.NearestPort == null) return "";
                    return f.NearestPortMiles < AtSeaNearMiles ? "near " + f.NearestPort
                        : Miles.Text(f.NearestPortMiles) + " from " + f.NearestPort;
                default: return "";
            }
        }

        // The ship list's row note ("Brig #1  -  at Fort Aestrin"); a ship stored here has none.
        internal static string Note(Where where, ShipFacts f)
        {
            switch (where)
            {
                case Where.InShipyard: return "in the shipyard";
                case Where.LeavingHere: return "leaves day " + f.LeavesDay;
                case Where.ArrivingHere: return "arrives day " + f.ArrivesDay;
                case Where.StoredElsewhere: return f.WaitingForSailors ? "leaves day " + f.LeavesDay : "stored at " + f.StoredAt;
                case Where.OnTheWay: return (f.UnderTow ? "under tow to " : "to ") + f.GoingTo;
                case Where.TowBooked: return "tow to " + f.TowTo;
                case Where.Sunk: return "sunk";
                case Where.Moored: return f.NearestPort == null ? "moored" : AtOrNear(f);
                case Where.Anchored: return "near " + f.NearestPort;
                case Where.AtSea: return "at sea";
                default: return "";
            }
        }

        private static string AtOrNear(ShipFacts f) => (f.NearestPortMiles <= MooredAtMiles ? "at " : "near ") + f.NearestPort;
    }

    // ==== Status ====

    // The status key (Ctrl+F6): Shipwright's view of the player's ships, the shipyards and the fees, written to the
    // BepInEx log for the phase 0 checks (§15.0).
    internal static class Status
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static void Write()
        {
            try
            {
                var text = new StringBuilder();
                text.AppendLine("Shipwright status, day " + GameClock.Day + " (absolute hour " + GameClock.Hours.ToString("0.0", Invariant) + ")");
                if (State.Off) text.AppendLine("  OFF for this save: its Shipwright data is newer or unreadable (see the error logged when it loaded)");
                text.Append("  rates:");
                for (int i = 0; i < 4; i++) text.Append(' ').Append(Money.Short[i]).Append(' ').Append(Money.Rate(i).ToString("0.####", Invariant));
                text.AppendLine();
                var rented = new List<PurchasableBoat>();
                var ships = Ships.Owned(rented);
                WriteShipyards(text, ships);
                WriteShips(text, ships, rented);
                Log.Info(text.ToString().TrimEnd());
                Notify.Show("Shipwright status\nwritten to the log.");
            }
            catch (Exception e) { Log.Error("Status failed: " + e); }
        }

        private static void WriteShipyards(StringBuilder text, List<OwnedShip> ships)
        {
            var loaded = Shipyards.Loaded();
            foreach (var info in Shipyards.Table)
            {
                text.Append("  shipyard ").Append(info.Name).Append(", ").Append(Money.Names[info.Currency]);
                if (!Shipyards.Present(info)) { text.AppendLine(": needs Old Chronian, not installed"); continue; }
                Shipyard live = null;
                foreach (var yard in loaded)
                    if (Shipyards.InfoOf(yard) == info) { live = yard; break; }
                if (!live) { text.AppendLine(": not loaded"); continue; }
                text.Append(": loaded");
                if (live.region != info.Currency) text.Append(", but its currency index is ").Append(live.region);
                var inArea = new List<string>();
                foreach (var boat in Shipyards.BoatsInArea(live))
                    if (boat) inArea.Add(NameOf(boat, ships));
                text.Append(", in its area: ").AppendLine(inArea.Count == 0 ? "none" : string.Join(", ", inArea.ToArray()));
            }
            foreach (var yard in loaded)
                if (Shipyards.InfoOf(yard) == null)
                    text.Append("  a shipyard Shipwright doesn't serve: ").Append(yard.name).Append(", near ").AppendLine(Shipyards.PortOf(yard) ?? "no port");
        }

        private static void WriteShips(StringBuilder text, List<OwnedShip> ships, List<PurchasableBoat> rented)
        {
            text.AppendLine("  ships you own: " + ships.Count);
            foreach (var ship in ships)
            {
                var facts = Ships.Facts(ship, null);
                var where = Whereabouts.Decide(facts, null);
                text.Append("    ").Append(ship.Name).Append(" (save index ").Append(ship.Index).Append(", ").Append(ship.Boat ? ship.Boat.name : "not built yet").Append("): ")
                    .Append(Whereabouts.Line(where, facts)).Append(" [row note \"").Append(Whereabouts.Note(where, facts)).Append("\"");
                if (facts.NearestPort != null)
                    text.Append("; ").Append(facts.NearestPort).Append(' ').Append(facts.NearestPortMiles.ToString("0.0", Invariant)).Append(" miles");
                if (facts.RopeTied) text.Append("; rope tied");
                if (facts.AnchorSet) text.Append("; anchor set");
                text.Append(']');
                if (Compat.EconomyOverhaul.IsPledged(ship.Index)) text.Append(", pledged for a bottomry loan");
                // Her cargo: as recorded at the store, or what is aboard her now.
                var record = Storage.RecordOf(ship.Index);
                int cargo = record != null ? record.cargoUnits : Storage.CargoValue(ship.Index);
                int units = record != null ? Fees.StorageUnits(record.feeUnits, cargo) : Fees.StorageUnits(Fees.DailyUnits(ship.Price), cargo);
                text.Append("; cargo worth ").Append(cargo).Append(" units");
                text.Append("; storage ").Append(units).Append(" units a day = ");
                for (int i = 0; i < 3; i++)
                    text.Append(i > 0 ? ", " : "").Append(Fees.Coins(units, Money.Rate(i))).Append(' ').Append(Money.Short[i]);
                text.AppendLine();
            }
            foreach (var boat in rented)
                text.Append("    not listed, rented from EconomyOverhaul: ").AppendLine(boat.name);
        }

        private static string NameOf(GameObject boat, List<OwnedShip> ships)
        {
            foreach (var ship in ships)
                if (ship.Boat && ship.Boat.gameObject == boat) return ship.Name;
            return boat.name + " (not yours)";
        }
    }

    // ==== SaveData ====

    // Shipwright's records: one JSON string in GameState.modData (§4.4), written and read by SaveJson: public fields of
    // values, arrays and lists, no dictionaries. Fields a save lacks keep their defaults, so an older save still reads.
    [Serializable]
    public sealed class ShipwrightSave
    {
        public int version = SaveCodec.CurrentVersion;
        // Copy k gets the save-index block 1000 + 10k (§7.2); k is never reused.
        public int nextCopy = 1;
        // The parking field in use: 0 the main field, 1 the backup (§5.6).
        public int parkingField;
        // Per hull type, the last number given ("Sanbuq" -> 3). The ship from the game or a ship mod is #1.
        public List<TypeCounter> typeNumbers = new List<TypeCounter>();
        // Typed names by save index. A ship without one shows "Type #n".
        public List<ShipName> names = new List<ShipName>();
        public List<StoredShip> stored = new List<StoredShip>();
        public List<TransportRecord> transports = new List<TransportRecord>();
        public List<CopyRecord> copies = new List<CopyRecord>();
        public List<OrderRecord> orders = new List<OrderRecord>();
        public List<PlateRecord> plates = new List<PlateRecord>();
    }

    [Serializable]
    public sealed class TypeCounter
    {
        public string type = "";
        public int last;
    }

    [Serializable]
    public sealed class ShipName
    {
        public int saveIndex;
        public string typed = "";
    }

    // A ship stored at a shipyard (§5). slot is her parking slot, or -1 for a finished copy never taken out (§7.3).
    // feeUnits is her ship's daily fee in units; cargoUnits the value of the cargo packed with her at the store (§4.6, Fees),
    // 0 for a copy just built. massLb and cargoLb (0.1.2): her own mass and her cargo's weight then (Weights), for her
    // transport's speed; 0 in a record from before, and for a copy just built, which keeps the speed (Fees.LoadFactor).
    [Serializable]
    public sealed class StoredShip
    {
        public int saveIndex;
        public string shipyard = "";
        public int slot = -1;
        public int dayStored;
        public float lengthM;
        public int feeUnits;
        public int cargoUnits;
        public float massLb, cargoLb;
    }

    // A booked transport (§6). The storage record stays at the origin until she arrives. orderedAt: the shipyard that booked
    // her, her storage yard or another (SPEC.md §6); empty in a record from before it.
    // A tow of a ship not stored (tow, SPEC.md §6; Tow in Transport.cs): from is the ordering yard, where the tug
    // leaves at departHour; it reaches her at reachHour, waits a day, and picks her up once nobody is near (pickupHour, -1
    // until then; nearHour is the last check that found the player near her after the day's wait); arriveHour is set again
    // from the pickup. Both legs are paid in `currency`; towFee is paid back if she leaves her place at booking (atX, atZ,
    // real coordinates: the floating origin's shift taken out). Her length and cargo (its value, and from 0.1.2 its weight
    // with her own mass, for the tow leg's speed) are measured at booking.
    [Serializable]
    public sealed class TransportRecord
    {
        public int saveIndex;
        public string from = "", to = "", orderedAt = "";
        public double bookedHour, departHour, arriveHour;
        public bool tow;
        public int currency, tugMiles, towMiles, tugFee, towFee, cargoUnits;
        public float lengthM, atX, atZ, massLb, cargoLb;
        public double reachHour, pickupHour = -1, nearHour = -1;
        public bool reachedTold, cratesTold;
        public int cratesTaken;
    }

    // A built copy of a vanilla hull (§7). Its state is the game's own save data for its objects and items,
    // BinaryFormatter'd to base64, kept here instead of in the vanilla save (§7.4).
    [Serializable]
    public sealed class CopyRecord
    {
        public int k;
        public int hull;
        public int number;
        public bool spawned;
        public string objects = "";
        public string items = "";
    }

    // An order at a shipyard (§8). delivered counts logs, lumber, nails, wool, copper, iron.
    [Serializable]
    public sealed class OrderRecord
    {
        public string shipyard = "";
        public int hull;
        public string typed = "";
        public int orderedDay;
        public int feePaid;
        public int currency;
        public int[] delivered = new int[6];
        public double readyHour = -1;
    }

    // A name plate (§9, decision 91): what the plate prefab (650-655) can't hold, found by the plate's instanceId.
    [Serializable]
    public sealed class PlateRecord
    {
        public int instanceId;
        public string style = "", name = "", font = "";
        public int size = 10;
        public int shipIndex;
        // The plate's size (0.1.6, B5; PlateData.Scales). A record from before 0.1.6 lacks it and keeps 1.
        public float scale = 1f;
    }

    internal static class SaveCodec
    {
        internal const int CurrentVersion = 1;

        internal enum Kind { None, Readable, Newer, Unreadable }

        // The version is read with a pattern before the records are parsed, so a newer save is recognised even when its
        // records no longer fit this build.
        private static readonly Regex VersionField = new Regex("\"version\"\\s*:\\s*(-?\\d+)");

        internal static int PeekVersion(string json)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            var match = VersionField.Match(json);
            return match.Success && int.TryParse(match.Groups[1].Value, out int version) ? version : -1;
        }

        internal static Kind Classify(string json)
        {
            if (string.IsNullOrEmpty(json)) return Kind.None;
            int version = PeekVersion(json);
            if (version > CurrentVersion) return Kind.Newer;
            return version >= 1 ? Kind.Readable : Kind.Unreadable;
        }

        // After parsing: missing lists become empty, and records that can't be right are dropped: a name without a ship
        // or text, an earlier name for the same ship, a counter without a type. Of two counters for one type the higher
        // stays, so no number is given twice. A second record for one ship keeps the later one.
        internal static ShipwrightSave Normalize(ShipwrightSave save)
        {
            if (save == null) return new ShipwrightSave();
            var names = new List<ShipName>();
            foreach (var name in save.names ?? new List<ShipName>())
            {
                if (name == null || name.saveIndex <= 0 || string.IsNullOrEmpty(name.typed)) continue;
                names.RemoveAll(n => n.saveIndex == name.saveIndex);
                names.Add(name);
            }
            save.names = names;
            var counters = new List<TypeCounter>();
            foreach (var counter in save.typeNumbers ?? new List<TypeCounter>())
            {
                if (counter == null || string.IsNullOrEmpty(counter.type)) continue;
                var same = counters.Find(c => c.type == counter.type);
                if (same == null) counters.Add(counter);
                else same.last = Math.Max(same.last, counter.last);
            }
            save.typeNumbers = counters;
            save.stored = Unique(save.stored, s => s != null && s.saveIndex > 0 && !string.IsNullOrEmpty(s.shipyard), s => s.saveIndex);
            save.transports = Unique(save.transports, t => t != null && t.saveIndex > 0 && !string.IsNullOrEmpty(t.to), t => t.saveIndex);
            save.copies = Unique(save.copies, c => c != null && c.k > 0, c => c.k);
            save.orders = Unique(save.orders, o => o != null && !string.IsNullOrEmpty(o.shipyard) && o.hull > 0, o => o.shipyard);
            save.plates = Unique(save.plates, p => p != null && p.instanceId != 0, p => p.instanceId);
            foreach (var order in save.orders)
                if (order.delivered == null || order.delivered.Length != 6) order.delivered = Resize(order.delivered, 6);
            foreach (var copy in save.copies)
                if (copy.k >= save.nextCopy) save.nextCopy = copy.k + 1;
            if (save.nextCopy < 1) save.nextCopy = 1;
            if (save.parkingField != 0 && save.parkingField != 1) save.parkingField = 0;
            return save;
        }

        private static List<T> Unique<T, TKey>(List<T> list, Predicate<T> valid, Func<T, TKey> key)
        {
            var kept = new List<T>();
            if (list == null) return kept;
            foreach (var item in list)
            {
                if (!valid(item)) continue;
                TKey id = key(item);
                kept.RemoveAll(x => EqualityComparer<TKey>.Default.Equals(key(x), id));
                kept.Add(item);
            }
            return kept;
        }

        private static int[] Resize(int[] values, int length)
        {
            var resized = new int[length];
            if (values != null) Array.Copy(values, resized, Math.Min(values.Length, length));
            return resized;
        }
    }

    // ==== SaveHooks ====

    // The records of the game in play. Off: the save holds Shipwright data this build can't read (newer, or broken).
    // That string is written back unchanged, and the mod's features stay off until another save loads or a new game
    // starts (§4.4).
    internal static class State
    {
        internal const string Key = Plugin.Guid;
        internal static ShipwrightSave Save = new ShipwrightSave();
        internal static bool Off { get; private set; }
        internal static string Kept { get; private set; }

        internal static void Reset() { Save = new ShipwrightSave(); Off = false; Kept = null; }

        internal static void TurnOff(string json, string why)
        {
            Save = new ShipwrightSave(); Off = true; Kept = json;
            Log.Error(why + " Shipwright is off for this save, and keeps its data as it is.");
        }
    }

    // DoSaveGame calls SaveModData just before it copies GameState.modData into the save.
    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveModData))]
    internal static class SaveModDataPatch
    {
        private static void Postfix()
        {
            try
            {
                if (State.Off)
                {
                    if (State.Kept != null) GameState.modData[State.Key] = State.Kept;
                    return;
                }
                State.Save.version = SaveCodec.CurrentVersion;
                CopyEngine.CaptureForSave();
                string json = SaveJson.Write(State.Save);
                GameState.modData[State.Key] = json;
                Log.Debug("saved (data version " + State.Save.version + ", " + State.Save.names.Count + " typed names)");
            }
            catch (Exception e) { Log.Error("Saving Shipwright's data failed: " + e); }
        }
    }

    // LoadGame replaces GameState.modData only when the save has mod data, so a save without any would inherit the
    // previous game's records: they go first.
    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadGame))]
    internal static class LoadGamePatch
    {
        private static void Prefix()
        {
            State.Reset();
            GameState.modData?.Remove(State.Key);
        }
    }

    // LoadGame calls LoadModData at its very end, after the saved items are in place (§4.4). Plates find their records
    // themselves, when they start.
    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadModData))]
    internal static class LoadModDataPatch
    {
        private static void Postfix()
        {
            if (!Read()) return;
            // LoadGame restored modData after the items: what needed the records happens now (§4.4).
            Run("restoring copies", CopyEngine.RestoreAll);
            Run("transport arrivals", () => Transport.Tick(true));
            Run("order completions", () => Orders.Tick(true));
            if (Log.Verbose) Run("crate check", () => Plugin.Instance.StartCoroutine(Storage.LogCratesAfterLoad()));
        }

        private static bool Read()
        {
            State.Reset();
            string json = null;
            GameState.modData?.TryGetValue(State.Key, out json);
            switch (SaveCodec.Classify(json))
            {
                case SaveCodec.Kind.None:
                    Log.Info("loaded: this save has no Shipwright data yet");
                    return false;
                case SaveCodec.Kind.Newer:
                    State.TurnOff(json, "This save's Shipwright data is version " + SaveCodec.PeekVersion(json) + ", made by a newer build; this build reads up to version " + SaveCodec.CurrentVersion + ".");
                    return false;
                case SaveCodec.Kind.Unreadable:
                    State.TurnOff(json, "This save's Shipwright data has no version.");
                    return false;
            }
            try
            {
                var save = SaveJson.Read<ShipwrightSave>(json);
                if (save == null) { State.TurnOff(json, "This save's Shipwright data could not be read."); return false; }
                State.Save = SaveCodec.Normalize(save);
                Log.Info("loaded (data version " + SaveCodec.PeekVersion(json) + ": " + State.Save.stored.Count + " stored, " +
                    State.Save.transports.Count + " in transport, " + State.Save.copies.Count + " copies, " + State.Save.orders.Count + " orders, " +
                    State.Save.plates.Count + " plates, " + State.Save.names.Count + " typed names)");
                return true;
            }
            catch (Exception e) { State.TurnOff(json, "This save's Shipwright data could not be read (" + e.Message + ")."); return false; }
        }

        private static void Run(string what, Action step)
        {
            try { step(); }
            catch (Exception e) { Log.Error("After loading, " + what + " failed: " + e); }
        }
    }

    // Statics survive a return to the menu, so a new game clears them and drops the key.
    [HarmonyPatch(typeof(StartMenu), "StartNewGame")]
    internal static class NewGamePatch
    {
        private static void Postfix()
        {
            State.Reset();
            GameState.modData?.Remove(State.Key);
            Log.Info("new game: reset");
        }
    }

    // ==== SaveJson ====

    // Shipwright's own JSON for its records (§4.4). Unity's JsonUtility left out every list of the mod's classes in the
    // game (0.1.2 saved only {"version":1,"nextCopy":2,"parkingField":0}), and it can't run outside Unity to be tested.
    // This writes a record's public fields (numbers, bools, strings, arrays and lists of values or records) in the same
    // shape, and reads them back: fields the text lacks keep their defaults, fields it has that the class doesn't are
    // skipped. Bad text throws FormatException.
    internal static class SaveJson
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // ---- Writing ----

        internal static string Write(object value)
        {
            var text = new StringBuilder();
            WriteValue(text, value);
            return text.ToString();
        }

        private static void WriteValue(StringBuilder text, object value)
        {
            switch (value)
            {
                case null: text.Append("null"); return;
                case string s: WriteString(text, s); return;
                case bool b: text.Append(b ? "true" : "false"); return;
                case int i: text.Append(i.ToString(Invariant)); return;
                case long l: text.Append(l.ToString(Invariant)); return;
                case float f: text.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", Invariant)); return;
                case double d: text.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", Invariant)); return;
                case IList list:
                    text.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) text.Append(',');
                        WriteValue(text, list[i]);
                    }
                    text.Append(']');
                    return;
            }
            text.Append('{');
            bool first = true;
            foreach (var field in Fields(value.GetType()))
            {
                if (!first) text.Append(',');
                first = false;
                WriteString(text, field.Name);
                text.Append(':');
                WriteValue(text, field.GetValue(value));
            }
            text.Append('}');
        }

        private static void WriteString(StringBuilder text, string s)
        {
            text.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }

        // A record's public instance fields, in declaration order.
        private static IEnumerable<FieldInfo> Fields(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly && !f.IsNotSerialized).OrderBy(f => f.MetadataToken);

        // ---- Reading ----

        internal static T Read<T>(string json) where T : class
        {
            var parser = new Parser(json ?? "");
            object node = parser.Document();
            return (T)Convert(node, typeof(T));
        }

        // A number as written, kept as text until its field's type is known.
        private sealed class Number
        {
            internal readonly string Text;
            internal Number(string text) { Text = text; }
        }

        private static object Convert(object node, Type type)
        {
            if (node == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return node as string;
            if (type == typeof(bool)) return node is bool b && b;
            if (type == typeof(int)) return (int)Math.Round(ToDouble(node));
            if (type == typeof(long)) return (long)Math.Round(ToDouble(node));
            if (type == typeof(float)) return (float)ToDouble(node);
            if (type == typeof(double)) return ToDouble(node);
            if (type.IsArray)
            {
                var items = node as List<object> ?? new List<object>();
                var element = type.GetElementType();
                var array = Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(Convert(items[i], element), i);
                return array;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in node as List<object> ?? new List<object>()) list.Add(Convert(item, element));
                return list;
            }
            if (!(node is Dictionary<string, object> map)) return type.IsValueType ? Activator.CreateInstance(type) : null;
            object record = Activator.CreateInstance(type);
            foreach (var field in Fields(type))
                if (map.TryGetValue(field.Name, out var child)) field.SetValue(record, Convert(child, field.FieldType));
            return record;
        }

        private static double ToDouble(object node)
        {
            if (node is Number n && double.TryParse(n.Text, NumberStyles.Float, Invariant, out double d)) return d;
            if (node is bool b) return b ? 1 : 0;
            return 0;
        }

        private sealed class Parser
        {
            private readonly string s;
            private int i;

            internal Parser(string text) { s = text; }

            internal object Document()
            {
                object value = Value();
                Space();
                if (i != s.Length) throw Bad("text after the end");
                return value;
            }

            private object Value()
            {
                Space();
                if (i >= s.Length) throw Bad("the text ends early");
                char c = s[i];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == 't') return Word("true", true);
                if (c == 'f') return Word("false", false);
                if (c == 'n') return Word("null", null);
                if (c == '-' || char.IsDigit(c)) return NumberText();
                throw Bad("unexpected '" + c + "'");
            }

            private Dictionary<string, object> Object()
            {
                var map = new Dictionary<string, object>();
                i++;
                Space();
                if (Peek('}')) { i++; return map; }
                while (true)
                {
                    Space();
                    if (!Peek('"')) throw Bad("a name was expected");
                    string name = String();
                    Space();
                    Expect(':');
                    map[name] = Value();
                    Space();
                    if (Peek(',')) { i++; continue; }
                    Expect('}');
                    return map;
                }
            }

            private List<object> Array()
            {
                var list = new List<object>();
                i++;
                Space();
                if (Peek(']')) { i++; return list; }
                while (true)
                {
                    list.Add(Value());
                    Space();
                    if (Peek(',')) { i++; continue; }
                    Expect(']');
                    return list;
                }
            }

            private string String()
            {
                i++;
                var text = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Bad("a text isn't closed");
                    char c = s[i++];
                    if (c == '"') return text.ToString();
                    if (c != '\\') { text.Append(c); continue; }
                    if (i >= s.Length) throw Bad("a text isn't closed");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, Invariant, out int code)) throw Bad("a bad \\u escape");
                            text.Append((char)code);
                            i += 4;
                            break;
                        default: throw Bad("a bad escape \\" + e);
                    }
                }
            }

            private Number NumberText()
            {
                int start = i;
                if (Peek('-')) i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                string text = s.Substring(start, i - start);
                if (!double.TryParse(text, NumberStyles.Float, Invariant, out _)) throw Bad("a bad number " + text);
                return new Number(text);
            }

            private object Word(string word, object value)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Bad("unexpected text");
                i += word.Length;
                return value;
            }

            private void Space()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            private bool Peek(char c) => i < s.Length && s[i] == c;

            private void Expect(char c)
            {
                Space();
                if (!Peek(c)) throw Bad("'" + c + "' was expected");
                i++;
            }

            private FormatException Bad(string why) => new FormatException("Shipwright's data: " + why + " at character " + i + ".");
        }
    }

    // ==== DocumentsPatches ====

    // The shipyard documents (§5.1). Patches only: no component goes on the documents, because other mods clone them.
    //   area empty:      left click and (R) open the Shipwright board
    //   one ship:        left click is vanilla (the menu gains Store ship); (R) opens the board
    //   two or more:     left click is vanilla "too many boats"; (R) opens the board
    internal static class Documents
    {
        internal static bool Serves(ShipyardDocuments documents) =>
            documents && documents.shipyard && !State.Off && !GameState.currentShipyard && Shipyards.InfoOf(documents.shipyard) != null;
    }

    // Vanilla acts only with exactly one ship in the area; with none it does nothing, so the board opens instead.
    [HarmonyPatch(typeof(ShipyardDocuments), nameof(ShipyardDocuments.OnActivate))]
    internal static class DocumentsActivatePatch
    {
        private static bool Prefix(ShipyardDocuments __instance)
        {
            // While the plate's size is previewed, the click brings the plate page back (even with a ship in the area).
            if (Board.ResumeFromPreview(__instance)) return false;
            if (!Documents.Serves(__instance) || __instance.shipyard.GetBoatsInTriggerCount() != 0) return true;
            Board.Open(__instance);
            return false;
        }
    }

    // GoPointer sends alt clicks to the pointed-at button; ShipyardDocuments doesn't override OnAltActivate, so the base
    // method is patched with a type check.
    [HarmonyPatch(typeof(GoPointerButton), nameof(GoPointerButton.OnAltActivate), new Type[0])]
    internal static class DocumentsAltActivatePatch
    {
        private static bool Prefix(GoPointerButton __instance)
        {
            if (!(__instance is ShipyardDocuments documents)) return true;
            if (Board.ResumeFromPreview(documents)) return false;
            if (!Documents.Serves(documents)) return true;
            Board.Open(documents);
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipyardDocuments), "UpdateLookText")]
    internal static class DocumentsLookTextPatch
    {
        private static void Postfix(ShipyardDocuments __instance)
        {
            if (!Documents.Serves(__instance)) return;
            int count = __instance.shipyard.GetBoatsInTriggerCount();
            __instance.lookText = count < 1 ? "shipwright board"
                : count > 1 ? "(too many boats in the shipyard area)\n(R) shipwright" : "enter shipyard\n(R) shipwright";
        }
    }

    // ==== Board ====

    internal enum BoardTab { Order, Ships }

    internal enum BoardList { Hulls, Ships, Destinations, Styles }

    // A row of the board's left list: its text, its plate and what clicking it does.
    internal sealed class BoardItem
    {
        internal string Text;
        internal Material Plate;
        internal Action Clicked;
    }

    // The Shipwright board (§5.2): the port office mission board's look, with the tabs "order" and "ship list". It hangs
    // on the camera like the vanilla board, never enters shipyard mode and never sets GameState.currentShipyard.
    internal sealed partial class Board : MonoBehaviour
    {
        internal static Board Instance;
        private static BoardParts parts;

        private Shipyard yard;
        private ShipyardInfo info;
        private Transform documents;
        private BoardPage page;
        private BoardList list;
        private readonly List<BoardItem> items = new List<BoardItem>();
        private int picked = -1;
        private int listPage;
        // The rows the list shows: 5, or all 6 for the plate styles (ShowList).
        private int rowsShown = BoardParts.PagedRows;
        private Action view;
        private TextMesh title, hint;
        private MeshRenderer nameField;
        private Func<string> titleText;

        internal const string HintText = "Click the name to change it, up to 16 characters.";

        internal static bool IsOpen => Instance && Instance.gameObject.activeSelf;

        // Opened from the shipyard documents (§5.1), as MissionListUI.EnablePortMissionUI opens the vanilla board.
        internal static void Open(ShipyardDocuments documents)
        {
            if (State.Off) { Notify.Show("Shipwright is off\nfor this save."); return; }
            var yard = documents ? documents.shipyard : null;
            var info = Shipyards.InfoOf(yard);
            if (info == null || !Shipyards.Present(info) || IsOpen) return;
            if (parts == null || !parts.Alive)
            {
                parts = BoardParts.Build();
                if (parts == null) return;
                Instance = parts.Root.AddComponent<Board>();
                Instance.page = new BoardPage(parts);
            }
            Instance.Show(documents, yard, info);
        }

        private void Show(ShipyardDocuments documents, Shipyard yard, ShipyardInfo info)
        {
            this.documents = documents.transform;
            this.yard = yard;
            this.info = info;
            confirmCancel = false;
            GameState.inPortMissionList = true;
            MouseLook.ToggleMouseLookAndCursor(false);
            gameObject.SetActive(true);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayOpenSound();
            ShowShips(-1);
        }

        // Closing: the list's close button, vanilla's own close paths (Esc, the inventory key, through
        // DisablePortMissionUI), walking more than 4 m away, sleep and loading. A name being typed is saved first
        // (decision 68). The tutorial hints are told it wasn't the mission board.
        internal static void Close(bool vanillaClosed = false)
        {
            if (!IsOpen) return;
            NameEditor.Commit();
            // A size preview ends with the board: its plate goes, its parts come back for the next opening.
            Instance.EndPreview(false);
            Instance.gameObject.SetActive(false);
            Instance.page.Clear();
            Instance.view = null;
            PlatePreview.Release();
            if (!vanillaClosed)
            {
                GameState.inPortMissionList = false;
                if (GameState.inCursorMenu) MouseLook.ToggleMouseLookAndCursor(true);
                if (UISoundPlayer.instance) UISoundPlayer.instance.PlayCloseSound();
            }
            GameAccess.ForgetMissionUI();
        }

        private void Update()
        {
            if (!documents || !Refs.observerMirror || GameState.sleeping || GameState.currentlyLoading
                || Vector3.Distance(documents.position, Refs.observerMirror.transform.position) > 4f)
            {
                Close();
                return;
            }
            KeepReminder();
            NameEditor.Update();
        }

        // Shows the current list and page again, with fresh data.
        internal void Refresh() => view?.Invoke();

        // ---- The list ----

        // The tab labels sit at 78% of vanilla's size, raised a little; the active tab is lit, the other dark (§5.2).
        // Chronos builds nothing, so it shows only "ship list".
        private void SetTabs(BoardTab active)
        {
            bool builds = info.Builds.Length > 0;
            parts.OrderTab.Root.SetActive(builds);
            SetTab(parts.OrderTab, "order", new Vector3(0f, 0.022f, 0.004f), active == BoardTab.Order, () => { confirmCancel = false; ShowOrders(-1); });
            SetTab(parts.ShipsTab, "ship list", new Vector3(0.008f, 0.026f, 0.004f), active == BoardTab.Ships, () => ShowShips(-1));
        }

        private static void SetTab(BoardRow row, string text, Vector3 position, bool active, Action clicked)
        {
            row.Text.text = text;
            row.Text.transform.localPosition = position;
            row.Text.transform.localScale = new Vector3(0.008736f, 0.009672f, 0.0124f);
            row.Plate.sharedMaterial = active ? parts.Lit : parts.Dark;
            row.Button.Clicked = clicked;
        }

        // Fills the five rows (the plate styles six, BoardParts.Rows); unused rows are hidden. Only the ship list pages,
        // with the vanilla arrows and "page / pages" (§5.2).
        private void ShowList(BoardList kind, int selected, bool paging, string back, Action backClicked)
        {
            list = kind;
            picked = selected;
            rowsShown = kind == BoardList.Styles && !paging ? parts.Rows.Length : BoardParts.PagedRows;
            int pages = Math.Max(1, (items.Count + BoardParts.PagedRows - 1) / BoardParts.PagedRows);
            if (!paging) listPage = 0;
            else if (selected >= 0) listPage = selected / BoardParts.PagedRows;
            listPage = Mathf.Clamp(listPage, 0, pages - 1);
            DrawRows();
            parts.Next.Root.SetActive(paging);
            parts.Previous.Root.SetActive(paging);
            parts.PageCount.gameObject.SetActive(paging);
            if (paging)
            {
                parts.PageCount.text = (listPage + 1) + " / " + pages;
                parts.Next.Button.Clicked = () => TurnPage(1, pages);
                parts.Previous.Button.Clicked = () => TurnPage(-1, pages);
            }
            parts.Back.Text.text = back;
            parts.Back.Button.Clicked = backClicked;
        }

        private void DrawRows()
        {
            for (int i = 0; i < parts.Rows.Length; i++)
            {
                var row = parts.Rows[i];
                int index = listPage * rowsShown + i;
                if (i >= rowsShown || index >= items.Count) { row.Root.SetActive(false); continue; }
                var item = items[index];
                row.Root.SetActive(true);
                TextFit.Fit(row, item.Text);
                row.Plate.sharedMaterial = index == picked ? parts.Medium : item.Plate ?? parts.Lit;
                row.Button.Clicked = item.Clicked;
            }
        }

        private void TurnPage(int step, int pages)
        {
            int next = Mathf.Clamp(listPage + step, 0, pages - 1);
            if (next == listPage) return;
            listPage = next;
            parts.PageCount.text = (listPage + 1) + " / " + pages;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayParchmentSound();
            DrawRows();
        }

        // ---- The page ----

        private void BeginPage(Action redraw)
        {
            EndPreview(true);
            NameEditor.Cancel();
            page.Clear();
            title = null;
            hint = null;
            nameField = null;
            titleText = null;
            view = redraw;
        }

        // A page title that is a name, with the rename hint under it (§5.9). Clicking the name starts typing; saving
        // shows the new name at once, in the title and the row.
        private void NameTitle(Func<string> shown, Func<string> typed, Action<string> save)
        {
            titleText = shown;
            nameField = page.NameField(() =>
            {
                if (NameEditor.Typing) return;
                NameEditor.Begin(typed(), name => { save(name); Refresh(); }, RedrawTitle);
            });
            nameField.enabled = false;
            title = page.Title(shown(), true);
            hint = page.Hint(HintText);
        }

        private void RedrawTitle()
        {
            if (!title) return;
            bool typing = NameEditor.Typing;
            title.text = typing ? NameEditor.Shown : titleText();
            if (nameField) nameField.enabled = typing;
            if (hint) hint.text = typing ? "Enter saves, Esc cancels" : HintText;
        }

        // ---- Vanilla close paths ----

        // Esc / F10, the inventory key and the vanilla back button all call DisablePortMissionUI; it closes this board
        // too. When vanilla closed (its cooldown can make it return early), only the board itself is hidden.
        [HarmonyPatch(typeof(MissionListUI), nameof(MissionListUI.DisablePortMissionUI))]
        internal static class VanillaClosePatch
        {
            private static void Postfix() { if (IsOpen) Close(vanillaClosed: !GameState.inPortMissionList); }
        }

        // Loading closes the board before the world changes under it, and drops names typed on order pages.
        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadGame))]
        internal static class LoadClosePatch
        {
            private static void Prefix()
            {
                Close();
                pendingNames.Clear();
            }
        }
    }

    // ==== BoardPage ====

    // Builds a page on the board's right parchment from copies of the vanilla texts, button and map (§10). Positions
    // are local to `details UI`, where +x is screen-left; the numbers are the board logs' (assets/board/*.json).
    internal sealed partial class BoardPage
    {
        // Text scales of the logs.
        internal static readonly Vector3 TitleScale = new Vector3(0.011122f, 0.013731f, 0.013731f);
        internal static readonly Vector3 LabelScale = new Vector3(0.008414f, 0.010388f, 0.010388f);
        internal static readonly Vector3 ValueScale = new Vector3(0.010388f, 0.010388f, 0.010388f);
        internal static readonly Vector3 SmallScale = new Vector3(0.0068f, 0.0085f, 0.0085f);
        internal static readonly Vector3 LineScale = new Vector3(0.009349f, 0.010388f, 0.010388f);
        internal static readonly Vector3 PriceLabelScale = new Vector3(0.0093f, 0.0104f, 0.0104f);
        internal static readonly Vector3 PriceScale = new Vector3(0.0125f, 0.0125f, 0.0125f);

        // Columns, and the title with and without the rename hint (§5.9: pages with the hint sit 0.06 lower).
        internal const float Left = 0.676f, Right = -0.08f, TableAmount = -0.62f, Centre = -0.306f;
        internal static readonly Vector3 TitleHint = new Vector3(0f, 0.892f, 0.621f);
        internal static readonly Vector3 TitlePlain = new Vector3(0f, 0.851f, 0.621f);
        internal static readonly Vector3 HintPosition = new Vector3(0f, 0.76f, 0.648f);
        internal static readonly Vector3 ButtonSpot = new Vector3(-0.433f, -0.602f, 0.639f);

        internal const int AnchorMiddleLeft = 3, AnchorMiddleCenter = 4, AnchorMiddleRight = 5;
        internal const int AlignLeft = 0, AlignCenter = 1, AlignRight = 2;

        private readonly BoardParts parts;
        // Materials the page made for itself (the route map's), destroyed with it: a page's objects take their materials'
        // copies with them only as references (the code review, 2026-10-28: one was left behind at each transport page).
        private readonly List<Material> materials = new List<Material>();

        internal BoardPage(BoardParts parts) { this.parts = parts; }

        internal void Clear()
        {
            foreach (Transform child in parts.PageRoot)
            {
                child.gameObject.SetActive(false);
                Object.Destroy(child.gameObject);
            }
            foreach (var material in materials) if (material) Object.Destroy(material);
            materials.Clear();
        }

        internal TextMesh Text(GameObject proto, string text, Vector3 position, Vector3 scale, int anchor, int alignment)
        {
            var go = Object.Instantiate(proto, parts.PageRoot, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var mesh = go.GetComponent<TextMesh>();
            mesh.text = text ?? "";
            mesh.anchor = (TextAnchor)anchor;
            mesh.alignment = (TextAlignment)alignment;
            go.SetActive(true);
            return mesh;
        }

        internal TextMesh Title(string text, bool hint) =>
            Text(parts.Title, text, hint ? TitleHint : TitlePlain, TitleScale, AnchorMiddleCenter, AlignCenter);

        internal TextMesh Hint(string text) =>
            Text(parts.Label, text, HintPosition, new Vector3(0.0068f, 0.0085f, 0.0085f), AnchorMiddleCenter, AlignCenter);

        // A label / value / note block (§10.2): the label at y, the value 0.076 below, the note 0.074 below that.
        internal void Block(float x, float y, string label, string value, string note = null)
        {
            Text(parts.Label, label, new Vector3(x, y, 0.648f), LabelScale, AnchorMiddleLeft, AlignLeft);
            if (value != null) Text(parts.Value, value, new Vector3(x, y - 0.076f, 0.648f), ValueScale, AnchorMiddleLeft, AlignLeft);
            if (note != null) Text(parts.Label, note, new Vector3(x, y - 0.15f, 0.648f), SmallScale, AnchorMiddleLeft, AlignLeft);
        }

        internal TextMesh Small(string text, float x, float y, int anchor = AnchorMiddleLeft, int alignment = AlignLeft) =>
            Text(parts.Label, text, new Vector3(x, y, 0.648f), SmallScale, anchor, alignment);

        // The italic lines of the transport and plate pages: "Distance:" style, at most 20 characters each.
        internal TextMesh Line(int index, string text) =>
            Text(parts.Note, text, new Vector3(Left, -0.473f - 0.089f * index, 0.648f), LineScale, AnchorMiddleLeft, AlignLeft);

        // Building fee, remaining time, transport fee, plate price: label, amount and note on one centre line (decision 44).
        // Returns the amount's text, for a page that changes it in place.
        internal TextMesh Fee(string label, string amount, string note)
        {
            Text(parts.PriceLabel, label, new Vector3(Centre, -0.205f, 0.6488f), PriceLabelScale, AnchorMiddleCenter, AlignCenter);
            var shown = Text(parts.Price, amount, new Vector3(Centre, -0.305f, 0.651f), PriceScale, AnchorMiddleCenter, AlignCenter);
            if (note != null) Text(parts.Label, note, new Vector3(Centre, -0.385f, 0.648f), SmallScale, AnchorMiddleCenter, AlignCenter);
            return shown;
        }

        // A copy of the vanilla Accept button. count: the "0 / 1" plate, or null to hide it.
        internal BoardButton Button(string text, Vector3 position, Vector3 scale, Action clicked, string count = null)
        {
            var go = Object.Instantiate(parts.Accept, parts.PageRoot, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var trigger = go.transform.Find("bg+trigger");
            var button = trigger.gameObject.AddComponent<BoardButton>();
            button.Clicked = clicked;
            var label = go.transform.Find("text");
            if (label) label.GetComponent<TextMesh>().text = text;
            var countText = go.transform.Find("text (mission count)");
            var countPlate = go.transform.Find("bg (mission count)");
            if (countText)
            {
                countText.gameObject.SetActive(count != null);
                if (count != null) countText.GetComponent<TextMesh>().text = count;
            }
            if (countPlate) countPlate.gameObject.SetActive(count != null);
            go.SetActive(true);
            return button;
        }

        internal BoardButton MainButton(string text, Action clicked, string count = null) => Button(text, ButtonSpot, Vector3.one, clicked, count);

        // A button's label changed in place (a confirm step, 0.1.6: no rebuild of the page for it).
        internal static void Relabel(BoardButton button, string text)
        {
            var label = button ? button.transform.parent.Find("text") : null;
            var mesh = label ? label.GetComponent<TextMesh>() : null;
            if (mesh) mesh.text = text;
        }

        // The plate behind the title while a name is typed: a copy of a list row's plate (§5.9), in a wrapper that turns
        // the row's frame into the page's. Clickable while hidden, so clicking the name starts typing.
        internal MeshRenderer NameField(Action clicked)
        {
            var wrapper = new GameObject("shipwright name field") { layer = parts.PageRoot.gameObject.layer };
            wrapper.transform.SetParent(parts.PageRoot, false);
            wrapper.transform.localPosition = new Vector3(0f, 0.888f, 0.643f);
            wrapper.transform.localScale = new Vector3(0.8f, 0.62f, 1f);
            var plate = Object.Instantiate(parts.RowPlate, wrapper.transform, false);
            StripOutlines(plate);
            plate.transform.localPosition = Vector3.zero;
            plate.transform.localRotation = new Quaternion(-0.4999995f, -0.4999995f, -0.5000005f, 0.5000005f);
            plate.transform.localScale = new Vector3(0.3632541f, 0.6287296f, 0.2387897f);
            var button = plate.GetComponent<BoardButton>() ?? plate.AddComponent<BoardButton>();
            button.Clicked = clicked;
            button.KeepsTyping = true;
            var renderer = plate.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = parts.Medium;
            plate.SetActive(true);
            return renderer;
        }

        // A copy of the list's page arrow (‹ or ›) on the page (the plate page's font size, §9.7). Its frame and the
        // page's share the parents' turn, so a wrapper that undoes the page's scale (details UI 0.4098 × 0.27 × 0.27)
        // keeps its vanilla shape, here at 70% (× `size`); the arrow is lowered by its plate's offset to centre it.
        internal BoardButton Arrow(BoardRow source, Vector3 position, Action clicked, float size = 1f)
        {
            var wrapper = new GameObject("shipwright arrow") { layer = parts.PageRoot.gameObject.layer };
            wrapper.transform.SetParent(parts.PageRoot, false);
            wrapper.transform.localPosition = position;
            wrapper.transform.localScale = new Vector3(1.708292f, 2.592593f, 3.703676f) * size;
            var arrow = Object.Instantiate(source.Root, wrapper.transform, false);
            StripOutlines(arrow);
            arrow.transform.localPosition = new Vector3(0f, -0.0225f * arrow.transform.localScale.y, 0f);
            var trigger = arrow.transform.Find("bg+trigger");
            var button = trigger.GetComponent<BoardButton>() ?? trigger.gameObject.AddComponent<BoardButton>();
            button.Clicked = clicked;
            arrow.SetActive(true);
            return button;
        }

        internal static void StripOutlines(GameObject go)
        {
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour && behaviour.GetType().FullName == "cakeslice.Outline") Object.DestroyImmediate(behaviour);
        }

        // The vanilla mission map with the route, as MissionDetailsUI.UpdateMap draws it: the ocean map when the ports are
        // on different local maps, their local map when they share one, and "(map unavailable)" when there is no map.
        internal void RouteMap(Port origin, Port destination)
        {
            if (!origin || !destination) return;
            bool unavailable = (!origin.oceanMapLocation && !origin.localMapLocation) || (!destination.oceanMapLocation && !destination.localMapLocation)
                || (origin.localMap != destination.localMap && (!origin.oceanMapLocation || !destination.oceanMapLocation));
            if (unavailable)
            {
                Vector3 globe = FloatingOriginManager.instance ? FloatingOriginManager.instance.GetGlobeCoords(destination.transform) : Vector3.zero;
                int lon = Mathf.RoundToInt(globe.x), lat = Mathf.RoundToInt(globe.z);
                if (parts.Coords)
                {
                    var coords = Object.Instantiate(parts.Coords, parts.PageRoot, false);
                    coords.GetComponent<TextMesh>().text = "(map unavailable)\n\napproximate location:\n" + lat + " " + (lat < 0 ? "S" : "N") + ", " + lon + " " + (lon < 0 ? "W" : "E");
                    coords.SetActive(true);
                }
                return;
            }
            bool ocean = origin.localMap != destination.localMap || origin.localMap == LocalMap.none || destination.localMap == LocalMap.none;
            Texture texture = ocean ? parts.OceanMap : origin.localMap == LocalMap.alankh ? parts.AlankhMap : origin.localMap == LocalMap.emerald ? parts.EmeraldMap
                : origin.localMap == LocalMap.medi ? parts.MediMap : origin.localMap == LocalMap.lagoon ? parts.LagoonMap : null;
            if (!texture) return;
            var map = MapCopy(texture);
            Transform from = ocean ? origin.oceanMapLocation : origin.localMapLocation;
            Transform to = ocean ? destination.oceanMapLocation : destination.localMapLocation;
            var line = map.GetComponentInChildren<LineRenderer>(true);
            if (line && from && to)
            {
                line.SetPosition(0, from.localPosition);
                line.SetPosition(1, to.localPosition);
            }
            var marker = map.transform.Find("Line destination");
            if (marker && to) marker.localPosition = to.localPosition;
            map.SetActive(true);
        }

        // A copy of the mission map showing `texture`, not active yet.
        private GameObject MapCopy(Texture texture)
        {
            var map = Object.Instantiate(parts.Map, parts.PageRoot, false);
            var renderer = map.GetComponent<Renderer>();
            // As Renderer.material would make it: a copy of the map's material, kept to be destroyed with the page.
            var material = new Material(renderer.sharedMaterial);
            materials.Add(material);
            renderer.sharedMaterial = material;
            material.SetTexture("_EmissionMap", texture);
            material.SetTexture("_MainTex", texture);
            return map;
        }

        // The map quad as a picture frame (the plate page, §9.7): at its place and scale, its route children hidden.
        internal Renderer Picture(Vector3 position, Vector3 scale)
        {
            var map = Object.Instantiate(parts.Map, parts.PageRoot, false);
            map.transform.localPosition = position;
            map.transform.localScale = scale;
            foreach (Transform child in map.transform) child.gameObject.SetActive(false);
            var collider = map.GetComponent<Collider>();
            if (collider) collider.enabled = false;
            map.SetActive(true);
            return map.GetComponent<Renderer>();
        }
    }

    // Fits a list row's text to its plate: a row too long for its plate is set smaller (§5.2). TextMesh draws a font pixel
    // as 0.1 units at character size 1.
    internal static class TextFit
    {
        internal static float Width(TextMesh mesh, string text)
        {
            var font = mesh ? mesh.font : null;
            if (!font || string.IsNullOrEmpty(text)) return 0f;
            int size = mesh.fontSize > 0 ? mesh.fontSize : font.fontSize;
            font.RequestCharactersInTexture(text, size, mesh.fontStyle);
            float width = 0f;
            foreach (char c in text)
                if (font.GetCharacterInfo(c, out CharacterInfo info, size, mesh.fontStyle)) width += info.advance;
            return width * 0.1f * mesh.characterSize;
        }

        // The plate's width along the text's x axis, in the text's parent frame.
        internal static float PlateWidth(Transform plate, MeshFilter filter, Transform text)
        {
            var mesh = filter ? filter.sharedMesh : null;
            if (!mesh) return 0f;
            Bounds b = mesh.bounds;
            Vector3 axis = text.localRotation * Vector3.right;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 local = plate.localPosition + plate.localRotation * Vector3.Scale(plate.localScale, corner);
                float along = Vector3.Dot(local, axis);
                min = Math.Min(min, along);
                max = Math.Max(max, along);
            }
            return max - min;
        }

        internal static void Fit(BoardRow row, string text)
        {
            row.Text.text = text;
            row.Text.transform.localScale = row.TextScale;
            float allowed = PlateWidth(row.Plate.transform, row.Plate.GetComponent<MeshFilter>(), row.Text.transform) * 0.9f;
            float width = Width(row.Text, text) * Mathf.Abs(row.TextScale.x);
            if (allowed > 0f && width > allowed)
            {
                float k = allowed / width;
                row.Text.transform.localScale = new Vector3(row.TextScale.x * k, row.TextScale.y * k, row.TextScale.z);
            }
        }
    }

    // ==== BoardParts ====

    // A button on the Shipwright board (§5.2). Clicking anything but the name field ends the typing and saves the name
    // first (decision 68).
    internal sealed class BoardButton : GoPointerButton
    {
        internal Action Clicked;
        internal bool KeepsTyping;

        public override void OnActivate()
        {
            if (!KeepsTyping) NameEditor.Commit();
            if (Clicked == null) return;
            try { Clicked(); }
            catch (Exception e) { Log.Error("Board button failed: " + e); }
        }
    }

    // A row, tab or list button of the board: its trigger plate and its text.
    internal sealed class BoardRow
    {
        internal GameObject Root;
        internal BoardButton Button;
        internal MeshRenderer Plate;
        internal TextMesh Text;
        internal Vector3 TextScale;
    }

    // Copies of the port office mission board's visual parts, under the mod's own root (§5.2). MissionListUI and
    // MissionDetailsUI are singletons that set `instance` in Start, so only their visual children are copied, and the
    // vanilla button scripts are replaced by the mod's own.
    internal sealed class BoardParts
    {
        internal GameObject Root;
        internal Transform List, Details, PageRoot;
        // The vanilla list's five rows and a sixth, a copy of the fifth one step lower, where the page arrows sit: only a list
        // that never pages shows it (the plate styles, six since round 7: "Six rows, one page").
        internal readonly BoardRow[] Rows = new BoardRow[6];
        // The vanilla rows' step down the list (their y in `vanilla-mission-ui.json`: 0.1174, 0.0682, 0.0202, -0.0266,
        // -0.0758; 0.0483 on average), and how many a paging list shows.
        internal const float RowStep = 0.0483f;
        internal const int PagedRows = 5;
        internal BoardRow OrderTab, ShipsTab, Back, Next, Previous;
        internal TextMesh PageCount;
        // Prototypes inside `details UI`, kept hidden: every page is built from copies of them (§10.1).
        internal GameObject Title, Label, Value, PriceLabel, Price, Note, Coords, Accept, Map, RowPlate;
        internal Material Lit, Medium, Dark;
        internal Texture OceanMap, AlankhMap, EmeraldMap, MediMap, LagoonMap;

        internal static readonly string[] Copied = { "mission_list_UI_bg", "current mission buttons", "mission_details_UI_bg" };

        // The vanilla list's own children and its page buttons', by their level24 names (`vanilla-mission-ui.json`). Another
        // mod may move one (Mission Rework 1.0.4 moves `mission button (back)` from `page buttons` up to the list) or add its
        // own (its "sort button"): the copy puts each vanilla part back where vanilla has it and drops the rest (§13).
        internal static readonly string[] ListParts =
            { "mission button", "mission button (1)", "mission button (2)", "mission button (3)", "mission button (4)", "page buttons" };
        internal static readonly string[] PageParts =
        {
            "mission button (back)", "mission button (next", "mission button (previous", "text (page count)",
            "mission button (world", "mission button (local)",
        };

        internal bool Alive => Root;

        // The one lookup for the mission board's parts: the vanilla path, or else the part's name anywhere under `root`,
        // wherever another mod moved it. A name may hold a '/' itself (`accept/cancel button`), which Find reads as a path.
        internal static Transform Part(Transform root, string path)
        {
            if (!root) return null;
            var found = root.Find(path);
            if (found) return found;
            string name = path.Substring(path.LastIndexOf('/') + 1);
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != root && (t.name == path || t.name == name));
        }

        internal static BoardParts Build()
        {
            var list = MissionListUI.instance;
            var details = MissionDetailsUI.instance;
            if (!list || !details) { Log.Error("Board: the vanilla mission board was not found."); return null; }
            Transform vanilla = list.transform;
            var parts = new BoardParts();
            try
            {
                parts.ReadVanilla(vanilla, details);
                parts.Copy(vanilla, GameAccess.MissionBoardAnchor(list) ?? vanilla.parent);
                parts.Wire();
                return parts;
            }
            catch (Exception e)
            {
                Log.Error("Board: building it failed: " + e);
                if (parts.Root) Object.Destroy(parts.Root);
                return null;
            }
        }

        // The plate materials come from the vanilla tab buttons, the map textures from MissionDetailsUI.
        private void ReadVanilla(Transform vanilla, MissionDetailsUI details)
        {
            var worldTab = Part(Part(vanilla, "current mission buttons/page buttons/mission button (world"), "bg+trigger");
            var tab = worldTab ? worldTab.GetComponent<GPButtonMissionListWorld>() : null;
            Lit = tab ? tab.activeMat : null;
            Dark = tab ? tab.inactiveMat : null;
            var count = Part(Part(Part(vanilla, "mission_details_UI_bg/details UI"), "accept/cancel button"), "bg (mission count)");
            Medium = count ? count.GetComponent<Renderer>().sharedMaterial : null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (!material) continue;
                if (!Lit && material.name == "notification_UI_Lit") Lit = material;
                else if (!Medium && material.name == "notification_UI_Lit Medium") Medium = material;
                else if (!Dark && material.name == "notification_UI_Lit Dark") Dark = material;
            }
            OceanMap = GameAccess.Get<Texture>(details, "oceanMap");
            AlankhMap = GameAccess.Get<Texture>(details, "alankhMap");
            EmeraldMap = GameAccess.Get<Texture>(details, "emeraldMap");
            MediMap = GameAccess.Get<Texture>(details, "mediMap");
            LagoonMap = GameAccess.Get<Texture>(details, "lagoonMap");
        }

        // The board hangs where the vanilla board hangs: the camera's `mission UI anchor (PC)`, at the local transform the
        // vanilla board takes there. It is never placed in the world.
        private void Copy(Transform vanilla, Transform anchor)
        {
            Root = new GameObject("Shipwright board") { layer = vanilla.gameObject.layer };
            Root.SetActive(false);
            Root.transform.SetParent(anchor, false);
            Root.transform.localPosition = Vector3.zero;
            Root.transform.localRotation = Quaternion.identity;
            Root.transform.localScale = Vector3.one;
            foreach (string name in Copied)
            {
                var source = Part(vanilla, name);
                if (!source) throw new InvalidOperationException("mission UI/" + name + " is missing");
                var copy = Object.Instantiate(source.gameObject, Root.transform, false);
                copy.name = name;
                copy.SetActive(true);
            }
            // The vanilla buttons go, and the outlines their Start added: each new button adds its own.
            foreach (var behaviour in Root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is GoPointerButton || behaviour.GetType().FullName == "cakeslice.Outline")
                    Object.DestroyImmediate(behaviour);
            RestoreList(Part(Root.transform, "current mission buttons"));
        }

        // The copied list as vanilla lays it out: a page button another mod moved goes back into `page buttons` where it
        // stands (a move that kept its place, as Mission Rework's, gets its vanilla local place back), and what other mods
        // added is dropped. Their own code holds the vanilla objects, never these copies.
        private static void RestoreList(Transform list)
        {
            var pages = Part(list, "page buttons");
            if (!pages) throw new InvalidOperationException("a mission board button is missing");
            foreach (string name in PageParts)
            {
                var part = Part(list, name);
                if (!part || part.parent == pages) continue;
                Log.Info("Board: the mission board's \"" + name + "\" was moved by another mod; the copy puts it back.");
                part.SetParent(pages, true);
            }
            DropAdded(list, ListParts);
            DropAdded(pages, PageParts);
        }

        private static void DropAdded(Transform parent, string[] vanilla)
        {
            foreach (var child in parent.Cast<Transform>().Where(c => !vanilla.Contains(c.name)).ToList())
            {
                Log.Info("Board: \"" + child.name + "\", added to the mission board by another mod, is left out of the copy.");
                Object.DestroyImmediate(child.gameObject);
            }
        }

        private void Wire()
        {
            List = Part(Root.transform, "current mission buttons");
            // The sixth row: copied before any row gets its button, so the copy has none of its own yet.
            var fifth = Part(List, "mission button (4)");
            if (!fifth) throw new InvalidOperationException("a mission board button is missing");
            var sixth = Object.Instantiate(fifth.gameObject, List, false);
            sixth.name = "mission button (5)";
            sixth.transform.localPosition = fifth.localPosition - new Vector3(0f, RowStep, 0f);
            for (int i = 0; i < Rows.Length; i++)
                Rows[i] = Row(Part(List, i == 0 ? "mission button" : "mission button (" + i + ")"));
            Transform pages = Part(List, "page buttons");
            OrderTab = Row(Part(pages, "mission button (local)"));
            ShipsTab = Row(Part(pages, "mission button (world"));
            Back = Row(Part(pages, "mission button (back)"));
            Next = Row(Part(pages, "mission button (next"));
            Previous = Row(Part(pages, "mission button (previous"));
            PageCount = Part(pages, "text (page count)").GetComponent<TextMesh>();
            RowPlate = Rows[0].Plate.gameObject;
            // Copied as they were shown: the vanilla board hides them when it closes (Mission Rework the back button too,
            // once it moved it out of `page buttons`). The rows, the order tab and the arrows are shown by each list.
            foreach (var shown in new[] { List.gameObject, pages.gameObject, ShipsTab.Root, Back.Root })
                shown.SetActive(true);

            Details = Part(Root.transform, "mission_details_UI_bg/details UI");
            Details.gameObject.SetActive(true);
            foreach (Transform child in Details)
            {
                var text = child.GetComponent<TextMesh>();
                switch (child.name)
                {
                    case "text name": Title = child.gameObject; break;
                    case "text (destination)": Value = child.gameObject; break;
                    case "text (total gold)": Price = child.gameObject; break;
                    case "text (distance)": Note = child.gameObject; break;
                    case "text (coords)": Coords = child.gameObject; break;
                    case "accept/cancel button": Accept = child.gameObject; break;
                    case "map": Map = child.gameObject; break;
                    case "static text":
                        if (text && text.text == "Origin port:") Label = child.gameObject;
                        else if (text && text.text == "Reward:") PriceLabel = child.gameObject;
                        break;
                }
                child.gameObject.SetActive(false);
            }
            if (!Title || !Label || !Value || !PriceLabel || !Price || !Note || !Accept || !Map)
                throw new InvalidOperationException("details UI has changed");
            PageRoot = new GameObject("Shipwright page") { layer = Details.gameObject.layer }.transform;
            PageRoot.SetParent(Details, false);
        }

        private static BoardRow Row(Transform root)
        {
            if (!root) throw new InvalidOperationException("a mission board button is missing");
            var trigger = root.Find("bg+trigger");
            var text = root.Find("text");
            var row = new BoardRow
            {
                Root = root.gameObject,
                Button = trigger.gameObject.AddComponent<BoardButton>(),
                Plate = trigger.GetComponent<MeshRenderer>(),
                Text = text ? text.GetComponent<TextMesh>() : null,
            };
            if (row.Text) row.TextScale = row.Text.transform.localScale;
            return row;
        }
    }

    // ==== NameEditor ====

    // Typing a ship's name on the board (§5.9). Input.inputString holds the typed characters with the keyboard layout and
    // Shift applied; '\b' deletes, Enter saves, Esc cancels. While typing, the game's keys are off.
    internal static class NameEditor
    {
        internal static bool Typing { get; private set; }
        // The frame Esc ended the typing: the pause menu and the inventory hotkeys skip it too.
        private static int escFrame = -1;
        private static string buffer = "";
        private static Action<string> save;
        private static Action redraw;
        private static float blinkTime;

        internal static bool BlocksKeys => Typing || escFrame == Time.frameCount;

        // The current typed name stays, with the caret at its end; an unnamed ship starts empty.
        internal static void Begin(string typed, Action<string> onSave, Action onRedraw)
        {
            Commit();
            buffer = ShipNames.Clean(typed);
            save = onSave;
            redraw = onRedraw;
            Typing = true;
            blinkTime = Time.unscaledTime;
            redraw?.Invoke();
        }

        // The title while typing: the name so far and a blinking "_".
        internal static string Shown => buffer + ((int)((Time.unscaledTime - blinkTime) * 2f) % 2 == 0 ? "_" : " ");

        // Enter, a click elsewhere on the board, or closing it: saves. An empty name brings the number back.
        internal static void Commit()
        {
            if (!Typing) return;
            Typing = false;
            var onSave = save;
            var onRedraw = redraw;
            save = null;
            redraw = null;
            try { onSave?.Invoke(ShipNames.Clean(buffer)); }
            catch (Exception e) { Log.Error("Saving the name failed: " + e); }
            onRedraw?.Invoke();
        }

        internal static void Cancel()
        {
            if (!Typing) return;
            Typing = false;
            var onRedraw = redraw;
            save = null;
            redraw = null;
            onRedraw?.Invoke();
        }

        // Called by the board every frame while it is open.
        internal static void Update()
        {
            if (!Typing) return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                escFrame = Time.frameCount;
                Cancel();
                return;
            }
            string typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed))
            {
                foreach (char c in typed)
                {
                    if (c == '\b') { if (buffer.Length > 0) buffer = buffer.Substring(0, buffer.Length - 1); }
                    else if (c == '\n' || c == '\r') { Commit(); return; }
                    else if (ShipNames.Allowed(c) && buffer.Length < ShipNames.MaxLength) buffer += c;
                }
                blinkTime = Time.unscaledTime;
            }
            redraw?.Invoke();
        }
    }

    // While a name is typed the game's keys are off (§5.9): GameInput's key reads return nothing, the scroll keys don't
    // scroll, and the inventory hotkeys and the pause menu (raw key reads) skip their update.
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKey))]
    internal static class TypingGetKeyPatch
    {
        private static bool Prefix(ref bool __result) { if (!NameEditor.BlocksKeys) return true; __result = false; return false; }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKeyDown))]
    internal static class TypingGetKeyDownPatch
    {
        private static bool Prefix(ref bool __result) { if (!NameEditor.BlocksKeys) return true; __result = false; return false; }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetKeyUp))]
    internal static class TypingGetKeyUpPatch
    {
        private static bool Prefix(ref bool __result) { if (!NameEditor.BlocksKeys) return true; __result = false; return false; }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetScrollAxis))]
    internal static class TypingScrollPatch
    {
        private static bool Prefix(ref float __result) { if (!NameEditor.BlocksKeys) return true; __result = 0f; return false; }
    }

    [HarmonyPatch(typeof(InventoryHotkeys), "Update")]
    internal static class TypingHotkeysPatch
    {
        private static bool Prefix() => !NameEditor.BlocksKeys;
    }

    [HarmonyPatch(typeof(StartMenu), "LateUpdate")]
    internal static class TypingPauseMenuPatch
    {
        private static bool Prefix() => !NameEditor.BlocksKeys;
    }
}

namespace ShipwrightAndTransport.Compat
{
    // ==== Mods ====

    // Other mods are found by BepInEx GUID and reflection, never by compile-time reference or a BepInDependency (§0,
    // decision 90). Every lookup happens on first use, when all plugins are loaded; a missing mod or member just turns
    // that piece of compatibility off.
    internal static class Mods
    {
        internal const string OldChronianGuid = "com.Aquilartz.OldChronian";
        internal const string EconomyOverhaulGuid = "DogEggz.EconomyOverhaul";
        internal const string DinghiesGuid = "pr0skynesis.dinghies";

        internal static bool Installed(string guid) => Chainloader.PluginInfos.ContainsKey(guid);
        internal static bool OldChronian => Installed(OldChronianGuid);

        internal static Func<int, bool> StaticBool(string typeName, string methodName)
        {
            var type = AccessTools.TypeByName(typeName);
            var method = type != null ? AccessTools.Method(type, methodName, new[] { typeof(int) }) : null;
            if (method == null || !method.IsStatic || method.ReturnType != typeof(bool)) return null;
            return (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), method);
        }

        // Patches applied once the main scene loads, when every plugin is loaded.
        internal static void ApplyLatePatches(Harmony harmony)
        {
            EconomyOverhaul.PatchOwnShips(harmony);
            VirtualCrew.Apply(harmony);
        }
    }

    // EconomyOverhaul, the user's economy mod (§13). A rented ship has extraSetting set, so she would pass the owned
    // test (decision 85); a pledged ship is collateral for a bottomry loan (decision 87).
    internal static class EconomyOverhaul
    {
        private static bool resolved, failed;
        private static Func<int, bool> isRented, isPledged;
        private static Func<int, object> seized;
        private static Action<PurchasableBoat> showSale;

        internal static bool IsRented(int saveIndex) { Resolve(); return Ask(isRented, saveIndex); }
        internal static bool IsPledged(int saveIndex) { Resolve(); return Ask(isPledged, saveIndex); }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            if (!Mods.Installed(Mods.EconomyOverhaulGuid)) return;
            isRented = Mods.StaticBool("EconomyOverhaul.Rentals", "IsIndex");
            isPledged = Mods.StaticBool("EconomyOverhaul.Bottomry", "Pledged");
            seized = SeizedQuery(AccessTools.TypeByName("EconomyOverhaul.Bottomry"));
            showSale = ShowSaleAction(AccessTools.TypeByName("EconomyOverhaul.Pledges"));
            Log.Info("EconomyOverhaul found. Rented ships: " + (isRented != null ? "recognised" : "NOT recognised, so they count as owned") +
                ". Bottomry pledges: " + (isPledged != null ? "recognised" : "NOT recognised") +
                ". Seized ships: " + (seized != null && showSale != null ? "recognised" : "NOT recognised, so a copy it seized is yours again after a load") + ".");
        }

        // EconomyOverhaul 0.8.x: Bottomry.Seized(int) gives a ship's seized record (SeizedPledge), or null.
        internal static Func<int, object> SeizedQuery(Type bottomry)
        {
            var method = bottomry != null ? AccessTools.Method(bottomry, "Seized", new[] { typeof(int) }) : null;
            if (method == null || !method.IsStatic || method.ReturnType == typeof(void) || method.ReturnType.IsValueType) return null;
            return index => method.Invoke(null, new object[] { index });
        }

        // Pledges.ShowSale(PurchasableBoat): her sale sign out while she isn't the player's, at her price.
        internal static Action<PurchasableBoat> ShowSaleAction(Type pledges)
        {
            var method = pledges != null ? AccessTools.Method(pledges, "ShowSale", new[] { typeof(PurchasableBoat) }) : null;
            if (method == null || !method.IsStatic || method.ReturnType != typeof(void)) return null;
            return boat => method.Invoke(null, new object[] { boat });
        }

        // Code review R3: EconomyOverhaul's seizure wins over what Shipwright saved. It saves a seized ship as owned (its
        // MarkOwnedForSave, so the game keeps her place, rig and damage) and hands her back after a load in its LoadModData
        // postfix (Bottomry.Loaded). A copy's state is loaded a frame later (CopyEngine.LoadAfterStart), which made her the
        // player's again, so once she is loaded what Loaded does for a seized ship is done for her: no longer the player's,
        // no longer the ship the rescue reaches for, her sale sign out. (Its berth step never applies to a copy: it records
        // berths only of ships not owned when a save loads.) True when she was handed back.
        internal static bool HandBackIfSeized(PurchasableBoat boat)
        {
            Resolve();
            var saveable = boat ? boat.GetComponent<SaveableObject>() : null;
            if (seized == null || !saveable || !saveable.extraSetting) return false;
            try
            {
                if (seized(saveable.sceneIndex) == null) return false;
                saveable.extraSetting = false;
                if (GameState.lastOwnedBoat == boat.transform) GameState.lastOwnedBoat = null;
                showSale?.Invoke(boat);
                Log.Info(Ships.NameOf(saveable.sceneIndex) + " was seized by EconomyOverhaul's bottomry: after the load she is not yours (handed back as EconomyOverhaul hands back its seized ships).");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("Handing a seized copy back to EconomyOverhaul's lender failed: " + (e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message));
                return false;
            }
        }

        private static bool Ask(Func<int, bool> query, int saveIndex)
        {
            if (query == null) return false;
            try { return query(saveIndex); }
            catch (Exception e)
            {
                if (!failed) { failed = true; Log.Warn("Asking EconomyOverhaul failed: " + e.Message); }
                return false;
            }
        }

        // When EconomyOverhaul takes the ship the player is aboard, it puts the player aboard their nearest own ship,
        // the nearest of Pledges.OwnShips(). A stored ship must never be that ship, or the player would land on the
        // parking field (§13).
        internal static void PatchOwnShips(Harmony harmony)
        {
            if (!Mods.Installed(Mods.EconomyOverhaulGuid)) return;
            var type = AccessTools.TypeByName("EconomyOverhaul.Pledges");
            var method = type != null ? AccessTools.Method(type, "OwnShips", Type.EmptyTypes) : null;
            if (method == null || method.ReturnType != typeof(IEnumerable<PurchasableBoat>))
            {
                Log.Warn("EconomyOverhaul's Pledges.OwnShips was not found; its forced moves may pick a stored ship.");
                return;
            }
            try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(EconomyOverhaul), nameof(OwnShipsPostfix))); }
            catch (Exception e) { Log.Warn("Patching EconomyOverhaul's Pledges.OwnShips failed: " + e.Message); }
        }

        private static void OwnShipsPostfix(ref IEnumerable<PurchasableBoat> __result)
        {
            if (__result != null) __result = __result.Where(b => b && !Storage.IsAway(b.transform)).ToList();
        }
    }

    // Swappable Staysail 1.2.x: Store ship is hidden while its Sail Maker mode is on (§11). Found by its type alone:
    // its plugin GUID is the bare "DogEggz".
    internal static class SwappableStaysail
    {
        private static bool resolved;
        private static Func<bool> active;

        internal static bool SailMakerOn
        {
            get
            {
                if (!resolved)
                {
                    resolved = true;
                    var type = AccessTools.TypeByName("SwappableStaysail.SailmakerSession");
                    var getter = type != null ? AccessTools.PropertyGetter(type, "Active") : null;
                    if (getter != null && getter.IsStatic && getter.ReturnType == typeof(bool))
                        active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
                }
                try { return active != null && active(); }
                catch { return false; }
            }
        }
    }

    // Dinghies: a cutter stowed on a ship's davits (DinghiesScripts.Davits keeps her in its private `boat`).
    internal static class Dinghies
    {
        private static bool resolved;
        private static FieldInfo list, boat;

        internal static bool Stowed(PurchasableBoat ship)
        {
            if (!resolved)
            {
                resolved = true;
                if (Mods.Installed(Mods.DinghiesGuid))
                {
                    var type = AccessTools.TypeByName("DinghiesScripts.Davits");
                    list = type != null ? AccessTools.Field(type, "list") : null;
                    boat = type != null ? AccessTools.Field(type, "boat") : null;
                }
            }
            if (list == null || boat == null || !ship) return false;
            try
            {
                if (!(list.GetValue(null) is IList davits)) return false;
                foreach (var item in davits)
                {
                    var davit = item as Component;
                    var stowed = davit ? boat.GetValue(davit) as GameObject : null;
                    if (!stowed) continue;
                    if (davit.transform.IsChildOf(ship.transform) || stowed.transform.IsChildOf(ship.transform) || ship.transform.IsChildOf(stowed.transform)) return true;
                }
            }
            catch (Exception e) { Log.Warn("Reading Dinghies' davits failed: " + e.Message); }
            return false;
        }
    }
}
