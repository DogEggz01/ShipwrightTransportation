using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipwrightAndTransport
{
    // ==== Storage ====

    // Storing a ship at a shipyard and taking her out (§5.5, §5.7). A stored ship keeps her record until she is taken
    // out; a booked transport moves the record to its destination on arrival (§6).
    internal static class Storage
    {
        internal static StoredShip RecordOf(int index) => State.Save.stored.Find(s => s.saveIndex == index);

        // Stored somewhere or on the way: she is on the parking field (or not built yet), not where the player left her.
        internal static bool IsAway(int index) => RecordOf(index) != null;

        internal static bool IsAway(Transform boatRoot)
        {
            var saveable = boatRoot ? boatRoot.GetComponent<SaveableObject>() : null;
            return saveable && IsAway(saveable.sceneIndex);
        }

        internal static int DailyFee(StoredShip record, int currency) => Fees.Coins(Fees.StorageUnits(record.feeUnits, record.cargoUnits), Money.Rate(currency));

        // The value of every item aboard her, in fee units (Fees, the cargo shares): each item's ShipItem.value, the game's base
        // value. A sealed crate's value is its goods' (ShipItemCrate.OnLoad); a plate's grows with its size (PlateScaling).
        internal static int CargoValue(int index)
        {
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (index <= 0 || prefabs == null) return 0;
            long sum = 0;
            foreach (var prefab in prefabs)
            {
                if (!prefab || prefab.GetParentObject() != index) continue;
                var item = prefab.GetComponent<ShipItem>();
                if (item && item.value > 0) sum += item.value;
            }
            return (int)Math.Min(int.MaxValue, sum);
        }

        internal static int Days(StoredShip record) => Fees.StorageDays(GameClock.Day, record.dayStored);

        internal static int FeeDue(StoredShip record, int currency) => Fees.StorageDue(Days(record), DailyFee(record, currency));

        // The length used for transport (decisions 25, 27): the root capsule's height times the root's scale along it.
        // The Baghlah counts her full 27.8 m hull; her capsule is only 20.8 m.
        internal static float LengthOf(PurchasableBoat boat, int hull)
        {
            if (hull == 30) return 27.8f;
            var capsule = boat ? boat.GetComponent<CapsuleCollider>() : null;
            if (!capsule) return 10f;
            Vector3 scale = boat.transform.localScale;
            float axis = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
            return capsule.height * Mathf.Abs(axis);
        }

        // Why Store ship can't take her now (§5.5 step 1), or null. Store ship is hidden for houses and rented ships. The player
        // can't be aboard while she is in the shipyard menu, so there is no check for it (the user, 2026-10-27).
        // A tow is refused for the same (SPEC.md §6.3), but a ship far off has her cargo in her cache: the game
        // packs a ship over 1 km from the player (BoatLocalItems.Update, closeToPlayer), so for a tow the cache is read too.
        internal static string RefusalToStore(PurchasableBoat boat) => RefusalToStore(boat, false);

        internal static string RefusalToStore(PurchasableBoat boat, bool tow)
        {
            int index = Ships.IndexOf(boat);
            if (!tow && Transport.RecordOf(index) != null) return Tow.Booked;
            var items = boat.GetComponent<BoatLocalItems>();
            int filled = FilledCrates(index, tow ? items : null).Count;
            if (filled > 0) return FilledCratesText(filled);
            // Her cache still holds items the game hasn't unpacked: packing her again would replace it.
            if (GameState.loadingBoatLocalItems || (!tow && GameAccess.CacheHoldsItems(items))) return CargoLoading;
            if (index == 212) return "This boat stays with\nthe Leopard.";
            if (Compat.EconomyOverhaul.IsPledged(index)) return "Repay the loan first.";
            string attached = AttachedBoat(boat);
            if (attached != null) return attached;
            if (Parking.FreeSlot(State.Save.stored) < 0) return FieldFull;
            return null;
        }

        internal const string CargoLoading = "Cargo is still loading.";

        internal static string FilledCratesText(int count) =>
            count == 1 ? "Take the items out of\nthe crate first." : "Take the items out of\nthe " + count + " crates first.";

        // Crates of hers that hold items refuse the store and the tow (the user, 2026-10-30; before 0.1.2 every opened crate
        // did, which caught Kemy's Furniture's storage pieces, crates whose amount stays 0 even empty): packing a crate's items
        // was prone to bugs the game's crate handling makes (ShipwrightAndTransport_FAILED.md, "Opened crates"); an empty crate
        // packs as any item. Hers: loaded aboard her, or packed in her cache (null: not read). An item is in a crate by its
        // crate id only (CrateInventory.InsertItem), wherever it lies: unsealing gives a crate's goods a parent not hers, so when
        // the game packs her far off the crate goes into her cache and its goods stay in the world (the tow run 20261029-191154).
        internal static HashSet<int> FilledCrates(int index, BoatLocalItems cache)
        {
            var hers = new HashSet<int>();
            var items = new List<CrateRule.Item>();
            if (index <= 0) return new HashSet<int>();
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (prefabs != null)
                foreach (var prefab in prefabs)
                {
                    if (!prefab) continue;
                    items.Add(new CrateRule.Item(prefab.instanceId, prefab.GetParentObject(), prefab.currentCrateId));
                    if (prefab.GetParentObject() == index) hers.Add(prefab.instanceId);
                }
            var cached = cache ? cache.GetCachedItems() : null;
            if (cached != null)
                foreach (var data in cached)
                    if (data != null) { items.Add(new CrateRule.Item(data.instanceId, index, data.crateId)); hers.Add(data.instanceId); }
            return CrateRule.FilledCrates(hers, items);
        }

        // 3 s after a load, once the game's load coroutine has put items back in their crates by id (ShipItem's start).
        internal static IEnumerator LogCratesAfterLoad()
        {
            yield return new WaitForSecondsRealtime(3f);
            foreach (var ship in Ships.Owned())
                if (ship.Boat) LogCrates("after load", ship.Index, ship.Boat.GetComponent<BoatLocalItems>());
        }

        // For the tests (the user's handoff, kept at Debug): the items in her crates, loaded or in her cache. After a store,
        // tow or take-out of a ship whose crates were empty: "0 item(s) in 0 crate(s)".
        internal static void LogCrates(string when, int index, BoatLocalItems cache)
        {
            if (!Log.Verbose || index <= 0) return;
            var crates = FilledCrates(index, cache);
            var found = new List<string>();
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (prefabs != null)
                foreach (var prefab in prefabs)
                    if (prefab && CrateRule.InCrate(new CrateRule.Item(prefab.instanceId, 0, prefab.currentCrateId), crates))
                        found.Add(prefab.name + " in " + prefab.currentCrateId);
            var cached = cache ? cache.GetCachedItems() : null;
            if (cached != null)
                foreach (var data in cached)
                    if (data != null && CrateRule.InCrate(new CrateRule.Item(data.instanceId, 0, data.crateId), crates))
                        found.Add("packed " + data.instanceId + " in " + data.crateId);
            Log.Debug("Crate check (" + when + ", " + Ships.NameOf(index) + "): " + found.Count + " item(s) in " + crates.Count + " crate(s)" +
                (found.Count > 0 ? ": " + string.Join(", ", found.ToArray()) : "") + ".");
        }

        // The items the game packed into her cache: each item's prefab from the game's directory (an item in a cache is data
        // only; ShipItem destroys the live one, ProcessSaveable). Their values as CargoValue reads them: a crate's as
        // ShipItemCrate.OnLoad sets it from its prefab's full amount, any other item's its prefab's value.
        // shortcut: a name plate counts at its prefab's value, not its size's (PlateScaling); replace if plates go aboard as cargo.
        internal static int CachedCargoValue(BoatLocalItems items)
        {
            var cached = items ? items.GetCachedItems() : null;
            if (cached == null) return 0;
            long sum = 0;
            foreach (var data in cached)
            {
                var prefab = data != null ? PrefabOf(data.prefabIndex) : null;
                var item = prefab ? prefab.GetComponent<ShipItem>() : null;
                if (!item) continue;
                int value = item.value;
                var crate = item as ShipItemCrate;
                var contained = crate ? GameAccess.Get<GameObject>(crate, "containedPrefab") : null;
                var goods = contained ? contained.GetComponent<ShipItem>() : null;
                if (goods) value = Fees.Round((float)goods.value * (crate.smokedFood ? 1.2f : 1f) * crate.amount) + 20;
                if (value > 0) sum += value;
            }
            return (int)Math.Min(int.MaxValue, sum);
        }

        internal static GameObject PrefabOf(int prefabIndex)
        {
            var directory = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
            return directory != null && prefabIndex >= 0 && prefabIndex < directory.Length ? directory[prefabIndex] : null;
        }

        // Another boat hanging on her, or she on another: a Dinghies cutter on its davits (hooked or stowed), or a tow.
        // Moving one alone would tear the joint.
        private static string AttachedBoat(PurchasableBoat boat)
        {
            Transform root = boat.transform;
            foreach (var joint in boat.GetComponentsInChildren<Joint>(true))
            {
                var other = joint && joint.connectedBody ? joint.connectedBody.GetComponentInParent<PurchasableBoat>() : null;
                if (other && other != boat) return Hooked(other);
            }
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            if (objects != null)
                foreach (var saveable in objects)
                {
                    var other = saveable ? saveable.GetComponent<PurchasableBoat>() : null;
                    if (!other || other == boat || other.isHouse) continue;
                    foreach (var joint in other.GetComponentsInChildren<Joint>(true))
                        if (joint && joint.connectedBody && joint.connectedBody.transform.IsChildOf(root)) return Hooked(other);
                }
            return Compat.Dinghies.Stowed(boat) ? "Unhook the cutter first." : null;
        }

        private static string Hooked(PurchasableBoat other) =>
            ShipTypes.Clean(other.name) == "DNG Cutter" ? "Unhook the cutter first." : "Untie the other boat first.";

        // Store steps (§5.5): close the shipyard as its exit button does, pack the cargo, free ropes and anchor, drop the
        // game's references to her, and move her to her parking slot. Her hull gets dirty while stored, as any ship of the
        // player's does (code review R4: 0.1.4 switched her CleanableObject off, but only until the next game start).
        internal static IEnumerator Store(Shipyard yard, ShipyardInfo info, PurchasableBoat boat)
        {
            // The shipyard slides an admitted ship to its edit position over 2 s of game time (Shipyard.MoveShip sets her place
            // every frame until it ends, then holds her kinematic), even when she lies there already: a store started
            // meanwhile was undone, her move overwritten by the slide (0.1.3's first in-game runs, the confirm 0.3 s after the
            // admit). So the store waits until the slide is over, and goes on only if the shipyard still holds her; otherwise
            // (Exit pressed meanwhile) the player is told, as the confirm said she would be stored.
            for (float end = Time.realtimeSinceStartup + SlideRealLimit; Time.realtimeSinceStartup < end && Time.time < admittedAt + SlideSeconds;) yield return null;
            int index = boat ? Ships.IndexOf(boat) : -1;
            if (!boat || GameState.currentShipyard != yard || yard.GetCurrentBoat() != boat.gameObject || index <= 0)
            {
                Notify.Show(StoreCancelled);
                Log.Info("Storing " + (index > 0 ? Ships.NameOf(index) : "a ship") + " was cancelled: the shipyard no longer held her when her slide ended.");
                yield break;
            }
            int slot = Parking.FreeSlot(State.Save.stored);
            if (slot < 0) { Notify.Show(FieldFull); yield break; }
            string name = Ships.NameOf(index);
            var record = new StoredShip
            {
                saveIndex = index, shipyard = info.Name, slot = slot, dayStored = GameClock.Day,
                lengthM = LengthOf(boat, Ships.Describe(index, boat).Hull), feeUnits = Fees.DailyUnits(boat.price),
            };
            yard.DischargeShip();
            yield return null;
            // Her cargo's value counts in her fees from now on; it is packed with her, so it can't change while she is stored.
            var items = boat.GetComponent<BoatLocalItems>();
            record.massLb = Weights.ShipMass(boat);
            record.cargoUnits = GameAccess.PackCargo(items, out record.cargoLb);
            LogCrates("store", index, items);
            ForgetBoat(boat.transform);
            State.Save.stored.Add(record);
            Log.Info("Stored " + name + " (save index " + index + ") at " + info.Name + ", slot " + slot + ", cargo worth " + record.cargoUnits + " units, " +
                Weights.Text(record.massLb, record.cargoLb) + ".");
            yield return Teleporter.Move(boat, Parking.World(State.Save.parkingField, slot), Parking.Heading, false);
        }

        internal const string StoreCancelled = "Storing was cancelled.", FieldFull = "The storage field is full.";

        // The admit's slide takes 2 s of game time (t += deltaTime * 0.5 up to 1), and ends at the end of a frame. The waits
        // for it count game time as the slide does; the real-time limit only guards against a clock that stops. At a low frame
        // rate game time falls behind real time (each frame counts at most Time.maximumDeltaTime): 5 s real time ran out before
        // the slide ended in 0.1.3's test at about 10 fps, and a take-out's cargo came back where the slide had left her.
        private const float SlideSeconds = 2.5f, SlideRealLimit = 30f;
        private static float admittedAt = float.MinValue;

        // Shipyard.AdmitShip ran (StoreShipMenu.AdmitPatch): its slide started now.
        internal static void Admitted() => admittedAt = Time.time;

        // Daily wear and leaks apply to lastBoat only, and the rescue reaches for lastOwnedBoat (§5.5 step 5).
        internal static void ForgetBoat(Transform root)
        {
            if (GameState.lastBoat == root) GameState.lastBoat = null;
            if (GameState.lastOwnedBoat == root) GameState.lastOwnedBoat = null;
        }

        // Why she can't be taken out here now (§5.7), or null.
        internal static string RefusalToTakeOut(Shipyard yard, ShipyardInfo info, StoredShip record)
        {
            if (record.shipyard != info.Name) return "Take it out at\n" + record.shipyard + ".";
            var transport = Transport.RecordOf(record.saveIndex);
            if (transport != null) return transport.tow ? "It is under tow." : "It is booked for transport.";
            if (GameState.currentShipyard) return "The shipyard is busy.";
            if (Shipyards.BoatsInArea(yard).Count > 0) return "The shipyard must be empty.";
            var copy = CopyEngine.RecordOfIndex(record.saveIndex);
            if (copy != null && !copy.spawned) return CopyEngine.CanSpawn(copy) ? null : "This ship can't be\nbuilt right now.";
            return Ships.BoatOf(record.saveIndex) ? null : "This ship can't be found.";
        }

        // Collect steps (§5.7): pay, bring her to the shipyard's edit position (a copy never taken out is built there),
        // clean up her rigging and admit her, so the shipyard menu opens.
        internal static IEnumerator TakeOut(Shipyard yard, ShipyardInfo info, StoredShip record)
        {
            int due = FeeDue(record, yard.region);
            if (!Money.Pay(yard.region, due)) yield break;
            Transform target = yard.editedShipPosition;
            PurchasableBoat boat;
            var copy = CopyEngine.RecordOfIndex(record.saveIndex);
            if (copy != null && !copy.spawned)
            {
                boat = CopyEngine.Spawn(copy, target.position, target.rotation);
                if (!boat) { Money.Refund(yard.region, due); yield break; }
                State.Save.stored.Remove(record);
            }
            else
            {
                boat = Ships.BoatOf(record.saveIndex);
                if (!boat) { Money.Refund(yard.region, due); yield break; }
                State.Save.stored.Remove(record);
                if (!boat.gameObject.activeSelf) boat.gameObject.SetActive(true);
                // After a game start her cache is no longer marked loaded: marked again, the game leaves it to
                // UnpackAfterSlide (code review R2).
                GameAccess.KeepPacked(boat.GetComponent<BoatLocalItems>());
                yield return Teleporter.Move(boat, target.position, target.rotation, true);
            }
            GameAccess.RefreshHorizon(boat);
            Rerig(boat);
            Log.Info("Took out " + Ships.NameOf(record.saveIndex) + " at " + info.Name + ".");
            yield return new WaitForFixedUpdate();
            // Through the documents click, as a player admits her (GameAccess.ActivateDocuments); AdmitShip alone only if that
            // didn't admit her at all (not half-way).
            bool admitted = GameState.currentShipyard != null || GameAccess.ActivateDocuments(yard, boat.gameObject);
            if (!admitted && GameState.currentShipyard == null)
            {
                Log.Warn("The documents click didn't admit " + Ships.NameOf(record.saveIndex) + "; admitting her directly.");
                admitted = GameAccess.AdmitShip(yard, boat.gameObject);
            }
            if (!admitted)
            {
                // An admit that stopped halfway leaves the menu open without an order, and every button and Exit then
                // throws (0.1.0's Instant Cog, AllSailsAllShipyards' menu postfix): backed out, she is released at the shipyard.
                Admitted();
                GameAccess.CloseHalfAdmit(yard);
                Notify.Show(AdmitFailed);
                Plugin.Instance.StartCoroutine(ReleaseAfterSlide(yard, boat));
            }
            Plugin.Instance.StartCoroutine(UnpackAfterSlide(boat));
        }

        internal const string AdmitFailed = "The shipyard couldn't\ntake her in. She is\nreleased here.";

        // The admit's slide still runs and leaves her kinematic at the edit spot: once it is over she goes to the release
        // spot, unless a shipyard took her in since.
        private static IEnumerator ReleaseAfterSlide(Shipyard yard, PurchasableBoat boat)
        {
            for (float end = Time.realtimeSinceStartup + SlideRealLimit; Time.realtimeSinceStartup < end && Time.time < admittedAt + SlideSeconds;) yield return null;
            if (boat && yard && !(GameState.currentShipyard && GameState.currentShipyard.GetCurrentBoat() == boat.gameObject))
                GameAccess.ReleaseShip(yard, boat.transform);
        }

        // Storing marked her cache loaded so the game wouldn't unpack it before she left; the mark goes once the admit's
        // slide is over. Until LatestMove, leaving the menu sooner moved her to the release spot and the slide then put her back
        // at the edit spot, so cargo unpacked between stayed at the release spot, up to 40 m from her (0.1.3's in-game test);
        // now Exit stops the slide, and the cargo comes back wherever she is once its time is over.
        private static IEnumerator UnpackAfterSlide(PurchasableBoat boat)
        {
            for (float end = Time.realtimeSinceStartup + SlideRealLimit; Time.realtimeSinceStartup < end && Time.time < admittedAt + SlideSeconds;) yield return null;
            if (!boat) yield break;
            GameAccess.UnpackWhenNear(boat.GetComponent<BoatLocalItems>());
            GameAccess.RefreshHorizon(boat);
            // After the game's own re-insert of crate items, two frames after they spawn.
            if (!Log.Verbose) yield break;
            yield return new WaitForSecondsRealtime(3f);
            if (boat) LogCrates("take-out", Ships.IndexOf(boat), boat.GetComponent<BoatLocalItems>());
        }

        // As vanilla CancelOrder rebuilds the rig (LoadData of GetData), and 0.39's purchase resets the jib hinges.
        internal static void Rerig(PurchasableBoat boat)
        {
            try
            {
                var customization = boat.GetComponent<SaveableBoatCustomization>();
                if (customization) customization.LoadData(customization.GetData());
            }
            catch (Exception e) { Log.Warn("Rebuilding the rig failed: " + e.Message); }
            ResetJibs(boat);
        }

        internal static void ResetJibs(PurchasableBoat boat)
        {
            var refs = boat ? boat.GetComponent<BoatRefs>() : null;
            if (!refs || refs.masts == null) return;
            foreach (var mast in refs.masts)
            {
                if (!mast || mast.sails == null) continue;
                foreach (var sail in mast.sails)
                {
                    var jib = sail ? sail.GetComponent<JibAngleMaster>() : null;
                    if (jib) jib.ResetHingeRestingRot();
                }
            }
        }
    }

    // The rescue (§5.8): it reaches for lastOwnedBoat, or the nearest purchased ship. A stored ship must never be moved
    // to port; with no other ship the rescue must still finish, which vanilla waits for forever.
    [HarmonyPatch(typeof(Recovery), nameof(Recovery.RecoverPlayer))]
    internal static class RecoveryStartPatch
    {
        private static void Prefix()
        {
            if (GameState.lastOwnedBoat && Storage.IsAway(GameState.lastOwnedBoat)) GameState.lastOwnedBoat = null;
        }
    }

    [HarmonyPatch(typeof(Recovery), "FindClosestBoat")]
    internal static class RecoveryClosestBoatPatch
    {
        private static void Postfix(ref Transform __result)
        {
            if (!__result || !Storage.IsAway(__result)) return;
            __result = null;
            float best = float.MaxValue;
            var objects = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null;
            if (objects != null && Refs.observerMirror)
                foreach (var saveable in objects)
                {
                    if (!saveable || !saveable.extraSetting || !saveable.GetComponent<BoatRefs>() || Storage.IsAway(saveable.sceneIndex)) continue;
                    float distance = Vector3.Distance(saveable.transform.position, Refs.observerMirror.transform.position);
                    if (distance < best) { best = distance; __result = saveable.transform; }
                }
            if (!__result)
            {
                Log.Warn("Rescue: every ship is stored or on its way; the player is rescued without a ship.");
                GameAccess.SetBoatRecovered();
            }
        }
    }

    // ==== StoreShipMenu ====

    // A button of the mod's own on the shipyard menu.
    internal sealed class MenuButton : GoPointerButton
    {
        internal Action Clicked;

        public override void OnActivate()
        {
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayUISound(UISounds.buttonHover, 0.33f, 3f);
            try { Clicked?.Invoke(); }
            catch (Exception e) { Log.Error("Store ship failed: " + e); }
        }
    }

    // Store ship (§5.5, §11): a copy of the Exit shipyard button directly under it, and a confirm card. Shown while a ship
    // is admitted; hidden for houses, rented ships, and while Swappable Staysail's Sail Maker mode is on. Placed from the live
    // Exit button each time it shows (0.1.6, B1: in the user's game Exit stood elsewhere than the fixed place assumed).
    internal sealed class StoreShipMenu : MonoBehaviour
    {
        private static StoreShipMenu instance;
        private static bool failed;
        private GameObject button, card;
        private TextMesh cardText;
        private Renderer buttonPlate;
        private Material buttonMaterial;
        private GameObject confirmingShip;
        private Transform exit;
        private bool placementLogged;

        // Where Store ship was built (the game's own layout; the confirm card's parts are placed for it) and the gap left
        // under Exit, in the menu's units.
        private static readonly Vector3 BuiltAt = new Vector3(-11.57f, 8.895f, 10.15f);
        private const float Gap = 0.15f;

        // Built once, on the first admitted ship: the shipyard menu is one object shared by every shipyard. If the menu
        // has changed so it can't be built, Store ship stays away for the session.
        internal static void Ensure()
        {
            if (instance || failed) return;
            var ui = ShipyardUI.instance;
            var menu = GameAccess.ShipyardMenu(ui);
            if (!menu && ui) { var child = ui.transform.Find("UI"); menu = child ? child.gameObject : null; }
            if (!menu) { Log.Error("Store ship: the shipyard menu was not found."); return; }
            var holder = new GameObject("Shipwright store ship") { layer = menu.layer };
            holder.transform.SetParent(menu.transform, false);
            var built = holder.AddComponent<StoreShipMenu>();
            try
            {
                built.Build(menu.transform);
                instance = built;
            }
            catch (Exception e)
            {
                failed = true;
                Object.Destroy(holder);
                Log.Error("Store ship: building the button failed: " + e);
            }
        }

        private void Build(Transform menu)
        {
            exit = menu.Find("shipyard ui button exit");
            button = Clone(exit, transform, BuiltAt, new Vector3(2.886384f, 1.604956f, 0.2576f), "Store\nship", OnStoreShip);
            buttonPlate = button.GetComponent<Renderer>();
            buttonMaterial = buttonPlate ? buttonPlate.sharedMaterial : null;
            card = new GameObject("shipwright store confirm") { layer = menu.gameObject.layer };
            card.transform.SetParent(transform, false);
            Clone(menu.Find("panel Current Order/shipyard ui bg"), card.transform, new Vector3(-11.43f, 5.673f, 10.23f), new Vector3(1.52f, 2.08f, 6.6f), null, null);
            var text = Clone(menu.Find("panel Current Order/order text"), card.transform, new Vector3(-13.05f, 5.053f, 9.88f), new Vector3(0.239685f, 0.29545f, 0.1083f), null, null);
            cardText = text.GetComponent<TextMesh>();
            Clone(menu.Find("panel Current Order/shipyard ui button confirm"), card.transform, new Vector3(-12.44f, 4.553f, 9.93f), new Vector3(1.56492f, 0.707785f, 0.2576f), "Store", OnConfirm);
            Clone(menu.Find("panel Current Order/shipyard ui button cancel purchase"), card.transform, new Vector3(-10.34f, 4.553f, 9.93f), new Vector3(1.56492f, 0.707785f, 0.2576f), "Cancel", HideCard);
            card.SetActive(false);
            button.SetActive(false);
        }

        // A copy without the vanilla scripts (ShipyardButton, ShipyardUIOrderText) and outlines; a button gets a MenuButton.
        private static GameObject Clone(Transform source, Transform parent, Vector3 position, Vector3 scale, string text, Action clicked)
        {
            if (!source) throw new InvalidOperationException("a shipyard menu part is missing");
            var go = Object.Instantiate(source.gameObject, parent, false);
            go.name = "shipwright " + source.name;
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is ShipyardButton || behaviour is ShipyardUIOrderText || behaviour.GetType().FullName == "cakeslice.Outline")
                    Object.DestroyImmediate(behaviour);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            if (text != null && go.transform.childCount > 0)
            {
                var label = go.transform.GetChild(0).GetComponent<TextMesh>();
                if (label) label.text = text;
            }
            if (clicked != null) go.AddComponent<MenuButton>().Clicked = clicked;
            go.SetActive(true);
            return go;
        }

        private static GameObject CurrentShip => GameState.currentShipyard ? GameState.currentShipyard.GetCurrentBoat() : null;

        private static bool Shown(GameObject ship, out PurchasableBoat boat, out ShipyardInfo info)
        {
            boat = ship ? ship.GetComponent<PurchasableBoat>() : null;
            info = boat ? Shipyards.InfoOf(GameState.currentShipyard) : null;
            return boat && info != null && !State.Off && !boat.isHouse && !Compat.EconomyOverhaul.IsRented(Ships.IndexOf(boat))
                && !Compat.SwappableStaysail.SailMakerOn;
        }

        private void LateUpdate()
        {
            if (!button || !card) return;
            var ship = CurrentShip;
            bool shown = Shown(ship, out _, out _);
            if (shown && !button.activeSelf) PlaceUnderExit();
            if (button.activeSelf != shown) button.SetActive(shown);
            if (card.activeSelf && (!shown || ship != confirmingShip)) HideCard();
        }

        // Store ship goes right under the live Exit button: the centres of their boxes on one vertical line, its top a small gap
        // below Exit's bottom; the confirm card moves with it. The boxes are the meshes' bounds through each button's own turn
        // and scale, in the menu's units (both are children of the menu, Store ship through a holder at its origin): a button's
        // pivot needn't be its middle (candidate B matched pivots and put Store ship off the screen's left edge).
        private void PlaceUnderExit()
        {
            if (!exit || !button) return;
            if (!BoxOf(exit, out Vector3 exitMin, out Vector3 exitMax) || !BoxOf(button.transform, out Vector3 ownMin, out Vector3 ownMax)) return;
            var own = button.transform.localPosition;
            var target = own + new Vector3((exitMin.x + exitMax.x) / 2f - (ownMin.x + ownMax.x) / 2f, exitMin.y - Gap - ownMax.y, 0f);
            target.z = own.z;
            button.transform.localPosition = target;
            if (card) card.transform.localPosition = target - BuiltAt;
            if (placementLogged) return;
            placementLogged = true;
            Log.Info("Store ship: Exit shipyard spans " + exitMin.ToString("F2") + " to " + exitMax.ToString("F2") + " in the menu; Store ship placed at " +
                target.ToString("F3") + " (built for " + BuiltAt.ToString("F3") + ").");
        }

        // A button's box in its parent's space: its mesh's bounds through its local turn and scale, around its local position.
        private static bool BoxOf(Transform t, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            var filter = t.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh) return false;
            var b = filter.sharedMesh.bounds;
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var p = t.localPosition + t.localRotation * Vector3.Scale(t.localScale, corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return true;
        }

        private void OnStoreShip()
        {
            var ship = CurrentShip;
            if (!Shown(ship, out var boat, out var info)) return;
            string refusal = Storage.RefusalToStore(boat);
            if (refusal != null) { Notify.Show(refusal); return; }
            int region = GameState.currentShipyard.region;
            // With 1% of her cargo's value, as the store will record it.
            int daily = Fees.Coins(Fees.StorageUnits(Fees.DailyUnits(boat.price), Storage.CargoValue(Ships.IndexOf(boat))), Money.Rate(region));
            cardText.text = "Store " + Ships.NameOf(Ships.IndexOf(boat)) + " at\n" + info.Name + "?\n\n" + Money.Amount(daily) + " " + Money.Names[region] +
                " a day,\npaid when you take it out.";
            confirmingShip = ship;
            card.SetActive(true);
            if (buttonPlate && ShipyardUI.instance && ShipyardUI.instance.selectedButtonMaterial) buttonPlate.sharedMaterial = ShipyardUI.instance.selectedButtonMaterial;
        }

        private void OnConfirm()
        {
            var ship = CurrentShip;
            // HideCard clears confirmingShip: the ship the card was for is kept first.
            var confirming = confirmingShip;
            HideCard();
            if (!Shown(ship, out var boat, out var info) || ship != confirming) return;
            string refusal = Storage.RefusalToStore(boat);
            if (refusal != null) { Notify.Show(refusal); return; }
            Plugin.Instance.StartCoroutine(Storage.Store(GameState.currentShipyard, info, boat));
        }

        private void HideCard()
        {
            if (card) card.SetActive(false);
            confirmingShip = null;
            if (buttonPlate && buttonMaterial) buttonPlate.sharedMaterial = buttonMaterial;
        }

        [HarmonyPatch(typeof(Shipyard), "AdmitShip")]
        internal static class AdmitPatch
        {
            private static void Postfix()
            {
                Storage.Admitted();
                Ensure();
            }
        }
    }

    // ==== LatestMove ====

    // Only a ship's latest shipyard move runs (§5.7, "Exit during the admit's slide"). Shipyard.MoveShip is a coroutine, and
    // nothing stops one still running when the next starts: AdmitShip's slide to the edit spot lasts 2 s of game time and
    // DischargeShip's move to the release spot is instant, so Exit pressed during the slide put her at the release spot and
    // the slide put her back at the edit spot on the next frame; the game then let her go there, and she lay on the land under
    // Gold Rock City's and Dragon Cliffs' raised edit spots, or fell off Fort Aestrin's into the water (the user's test of
    // 0.1.0, 2026-10-28; run 20261028-233843). A take-out shows her at the edit spot at once, so Exit comes during the slide; a
    // vanilla admit does it too when Exit is pressed within 2 s. Each move gets a number; one that is no longer its ship's
    // latest stops before its next step, so the move called last wins, as the game means it to.
    internal static class LatestMove
    {
        private static readonly Dictionary<int, int> latest = new Dictionary<int, int>();
        private static int count;

        internal static IEnumerator Wrap(IEnumerator move, int ship)
        {
            int number = ++count;
            latest[ship] = number;
            return Run(move, ship, number);
        }

        private static IEnumerator Run(IEnumerator move, int ship, int number)
        {
            try
            {
                while (IsLatest(ship, number) && move.MoveNext()) yield return move.Current;
            }
            finally
            {
                if (IsLatest(ship, number)) latest.Remove(ship);
            }
        }

        private static bool IsLatest(int ship, int number) => latest.TryGetValue(ship, out int last) && last == number;
    }

    [HarmonyPatch(typeof(Shipyard), "MoveShip")]
    internal static class MoveShipPatch
    {
        private static void Postfix(Transform ship, ref IEnumerator __result)
        {
            if (ship && __result != null) __result = LatestMove.Wrap(__result, ship.GetInstanceID());
        }
    }

    // ==== Parking ====

    // The parking fields (§5.6): slots 200 m apart, far out at sea. A stored ship waits in her slot, switched on, like any
    // ship left far away. Positions are unshifted (world minus the floating origin's offset).
    internal static class Parking
    {
        internal const int Columns = 35, SlotCount = Columns * Columns;
        internal const float Pitch = 200f, SwitchDistance = 5000f;

        // Main field at 45N 11W, filling toward the northwest; backup at 27N 31E, filling toward the southeast.
        internal static Vector3 Unshifted(int field, int slot)
        {
            int c = slot % Columns, r = slot / Columns;
            return field == 0 ? new Vector3(-99000f - Pitch * c, 0f, 81000f + Pitch * r)
                : new Vector3(279000f + Pitch * c, 0f, -81000f - Pitch * r);
        }

        // The lowest slot no record uses; -1 when the field is full.
        internal static int FreeSlot(IEnumerable<StoredShip> stored)
        {
            var used = new HashSet<int>();
            foreach (var record in stored) if (record.slot >= 0) used.Add(record.slot);
            for (int slot = 0; slot < SlotCount; slot++) if (!used.Contains(slot)) return slot;
            return -1;
        }

        internal static Vector3 Offset => FloatingOriginManager.instance ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;

        internal static Vector3 World(int field, int slot) => Unshifted(field, slot) + Offset;

        // Every parked ship faces north.
        internal static readonly Quaternion Heading = Quaternion.identity;

        private static float nextCheck;
        private static bool switching;

        // Every few seconds: when the player comes within 5 km of a slot in use, the whole fleet moves to the same slots
        // in the other field, so nobody finds the stored ships and takes one without paying.
        internal static void Tick()
        {
            if (switching || Time.time < nextCheck || !GameState.playing || GameState.currentlyLoading || State.Off) return;
            nextCheck = Time.time + 3f;
            if (!Refs.observerMirror) return;
            Vector3 player = Refs.observerMirror.transform.position - Offset;
            int field = State.Save.parkingField;
            foreach (var record in State.Save.stored)
            {
                if (record.slot < 0) continue;
                Vector3 slot = Unshifted(field, record.slot);
                float dx = slot.x - player.x, dz = slot.z - player.z;
                if (dx * dx + dz * dz < SwitchDistance * SwitchDistance)
                {
                    Plugin.Instance.StartCoroutine(SwitchFields());
                    return;
                }
            }
        }

        private static IEnumerator SwitchFields()
        {
            switching = true;
            try
            {
                int field = 1 - State.Save.parkingField;
                State.Save.parkingField = field;
                Log.Info("The player came near the parking field: stored ships move to the " + (field == 0 ? "main" : "backup") + " field.");
                foreach (var record in State.Save.stored.ToArray())
                {
                    if (record.slot < 0) continue;
                    var boat = Ships.BoatOf(record.saveIndex);
                    if (boat) yield return Teleporter.Move(boat, World(field, record.slot), Heading, false);
                }
            }
            finally { switching = false; }
        }
    }

    // Moves a ship the way the game's DebugBoatTeleporter and rescue (Recovery.RecoverBoat) do (0.39): unmoor, reset the
    // anchor, wait two physics steps, move, wait, turn, wait. The anchor sits outside the ship's hierarchy, so it moves
    // with her.
    // The rescue holds every ship kinematic meanwhile (GameState.recovering); a move must too, and her rigidbody is moved
    // with her transform. Ships interpolate (BoatPhysicsSwitcher sets it in every LateUpdate): the game draws such a
    // body between its last two physics poses, so on a frame after a move with no physics step yet it drew her back where
    // she was, kinematic or not. 0.1.3's first in-game runs found stored ships at the slot for one frame, then switched off
    // as far away on that very frame, 5 m from the shipyard, where they stayed. So interpolation stays off from the start
    // until two physics steps after the last placement (NoInterpolation). The game sets her physics again by its own
    // rules (BoatHorizon.UpdateKinematic, BoatPhysicsSwitcher) once she is placed.
    internal static class Teleporter
    {
        internal static int Running;

        internal static IEnumerator Move(PurchasableBoat boat, Vector3 position, Quaternion rotation, bool near)
        {
            Running++;
            bool moving = true;
            try
            {
                var mooring = boat.GetComponent<BoatMooringRopes>();
                var controller = mooring ? mooring.GetAnchorController() : null;
                if (mooring)
                {
                    mooring.UnmoorAllRopes();
                    // 0.1.6, B7: a set anchor is frozen (Anchor.SetAnchor: kinematic) until the game releases it, which it does
                    // only in the anchor's own physics step (a short line) or when it is pulled loose. Released here the game's
                    // way, it hangs from her bow again; 0.1.4 carried it, still set, wherever it was (seen in the air).
                    if (mooring.anchor && GameAccess.ReleaseAnchor(mooring.anchor))
                        Log.Info(Ships.NameOf(Ships.IndexOf(boat)) + ": her anchor was set; released before the move.");
                    if (controller) controller.ResetAnchor();
                }
                var body = boat.GetComponent<Rigidbody>();
                Hold(body);
                if (body) Plugin.Instance.StartCoroutine(NoInterpolation(body, () => moving));
                yield return Settle();
                Transform anchor = mooring && mooring.anchor ? mooring.anchor.transform : null;
                var anchorBody = anchor ? anchor.GetComponent<Rigidbody>() : null;
                // The anchor keeps its place relative to her; released and reeled in, its line draws it to her bow (placing it at
                // the line's fitting put it inside her hull, and the physics threw it 35 m: candidate A's run).
                Vector3 anchorLocal = anchor ? boat.transform.InverseTransformPoint(anchor.position) : Vector3.zero;
                Hold(body);
                Place(boat.transform, body, position, boat.transform.rotation);
                if (anchor) Place(anchor, anchorBody, boat.transform.TransformPoint(anchorLocal), anchor.rotation);
                yield return Settle();
                Hold(body);
                Place(boat.transform, body, boat.transform.position, rotation);
                if (anchor)
                {
                    Place(anchor, anchorBody, boat.transform.TransformPoint(anchorLocal), anchor.rotation);
                    if (anchorBody && !anchorBody.isKinematic) anchorBody.velocity = Vector3.zero;
                }
                yield return Settle();
                // Reeled in again after the move (the game's own steps may have let line out while it was released).
                if (controller) controller.ResetAnchor();
                if (mooring && mooring.anchor && mooring.anchor.IsSet())
                    Log.Warn(Ships.NameOf(Ships.IndexOf(boat)) + ": her anchor is still set after the move.");
                if (anchor && controller)
                    Log.Info(Ships.NameOf(Ships.IndexOf(boat)) + ": her anchor is " + Vector3.Distance(anchor.position, controller.transform.position).ToString("F1") +
                        " m from its line's fitting after the move (kinematic " + (anchorBody && anchorBody.isKinematic) + ").");
                // Brought near the player: check her distance at once, so she switches on and her cargo comes back.
                if (near) GameAccess.RefreshHorizon(boat);
            }
            finally { moving = false; Running--; }
        }

        // Kinematic, still and not interpolated, as the game holds a ship in a shipyard or a rescue.
        private static void Hold(Rigidbody body)
        {
            if (!body) return;
            if (!body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
        }

        // BoatPhysicsSwitcher turns interpolation on again in every LateUpdate; turned off again at each end of frame, it is
        // off at the next frame's interpolation, for as long as the move lasts (at most 30 s, should the move be stopped
        // without ending).
        private static IEnumerator NoInterpolation(Rigidbody body, Func<bool> moving)
        {
            var endOfFrame = new WaitForEndOfFrame();
            float end = Time.realtimeSinceStartup + 30f;
            while (body && moving() && Time.realtimeSinceStartup < end)
            {
                body.interpolation = RigidbodyInterpolation.None;
                yield return endOfFrame;
            }
        }

        // The transform and the rigidbody together, so the next physics step and the interpolation agree on the place.
        private static void Place(Transform transform, Rigidbody body, Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            if (!body) return;
            body.position = position;
            body.rotation = rotation;
        }

        private static IEnumerator Settle()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
        }

        // 0.1.6, B7: the shipyard's discharge (Shipyard.DischargeShip → MoveShip with instantMove) puts the ship at its release
        // spot in one step, 35 m from the raised edit spot at Gold Rock, and leaves her anchor behind: a set one frozen there,
        // a free one asleep (a body jointed to a kinematic one isn't woken when that one is teleported), so it hung at the edit
        // spot "high above her, its line taut" (the user's picture; run 20261007-220103: 0.35 m from her bow after the
        // take-out's admit, 35.7 m after the discharge). The anchor goes with her, at its place relative to her, awake.
        [HarmonyPatch(typeof(Shipyard), "DischargeShip")]
        internal static class DischargePatch
        {
            private static readonly AccessTools.FieldRef<Shipyard, GameObject> CurrentShip = AccessTools.FieldRefAccess<Shipyard, GameObject>("currentShip");

            internal struct Carry
            {
                internal Transform Ship, Anchor;
                internal Vector3 Local;
                internal Quaternion Turn;
            }

            private static void Prefix(Shipyard __instance, out Carry __state)
            {
                __state = default;
                try
                {
                    var ship = CurrentShip(__instance);
                    var mooring = ship ? ship.GetComponent<BoatMooringRopes>() : null;
                    if (!mooring || !mooring.anchor) return;
                    var anchor = mooring.anchor.transform;
                    __state = new Carry { Ship = ship.transform, Anchor = anchor, Local = ship.transform.InverseTransformPoint(anchor.position), Turn = Quaternion.Inverse(ship.transform.rotation) * anchor.rotation };
                }
                catch (Exception e) { Log.Warn("Shipyard exit: noting the anchor failed: " + e.Message); }
            }

            private static void Postfix(Carry __state)
            {
                if (!__state.Ship || !__state.Anchor) return;
                try
                {
                    var anchor = __state.Anchor.GetComponent<Anchor>();
                    var controller = __state.Ship.GetComponent<BoatMooringRopes>().GetAnchorController();
                    if (anchor && anchor.IsSet()) GameAccess.ReleaseAnchor(anchor);
                    if (controller) controller.ResetAnchor();
                    var body = __state.Anchor.GetComponent<Rigidbody>();
                    Place(__state.Anchor, body, __state.Ship.TransformPoint(__state.Local), __state.Ship.rotation * __state.Turn);
                    if (body && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.WakeUp(); }
                }
                catch (Exception e) { Log.Warn("Shipyard exit: moving the anchor with the ship failed: " + e.Message); }
            }
        }
    }

    // ==== CrateTakeOut ====

    // A crate's items when a copy's cargo comes back (SPEC.md §5.7). An item in a crate lies at the crate's own place
    // (CrateInventory.LateUpdate keeps it there), so it is packed there. When her cache is unpacked the game loads it as a
    // full-size body and puts it back into its crate only two frames later (ShipItem's load coroutine,
    // CrateInventory.InsertItem); at a low frame rate its colliders come first (ItemRigidbody.AddCollider, three physics
    // steps), and the physics pushed the crate away from it: up to 4.75 m along her deck, or off a cutter (Shipwright and
    // Storage 0.1.3's in-game test, a model ship in a Scroll Shelf). A copy in play can carry an opened crate, and after a load
    // her restored cache unpacks like this, so each item of a crate goes into it as soon as it is loaded. Every other ship
    // unpacks as in vanilla: Store ship refuses a ship with a crate holding items, so a stored ship's cache holds no crate's
    // items, only empty crates (until
    // Shipwright and Storage 0.1.7 the take-out did this too).
    // Since 0.39.1 that load coroutine also holds every item with a crate ID (attached, colliders off, inStove) before it
    // looks for the crate, and only the crate's InsertItem or WithdrawItem ends the hold: an item whose crate isn't in the
    // world stays frozen for good, in every later load too. It reads the crate ID two frames after the load, so for such an
    // item the unpacking clears the ID first, as WithdrawItem does, and it stays loose.
    [HarmonyPatch(typeof(BoatLocalItems), "SpawnCachedItems")]
    internal static class CrateTakeOut
    {
        // Shipwright's business: a copy's cache.
        internal static bool Applies(int index) => CopyEngine.IsCopyIndex(index);

        internal enum CrateStep { Insert, Hold, Release }

        // What happens to an item of a crate as it is loaded: into its crate at once; held for a crate that is there but gets
        // its inventory only a frame later (a supply crate adds it in OnLoad), until the game's own insert; or, when its crate
        // isn't in the world at all, let go of its crate: the game's insert never comes then, and 0.1.3 (and the game since
        // 0.39.1) held such an item for good, frozen where it was packed with its colliders off.
        internal static CrateStep StepFor(bool crateInWorld, bool crateHasInventory) =>
            !crateInWorld ? CrateStep.Release : crateHasInventory ? CrateStep.Insert : CrateStep.Hold;

        // The items of a crate in her cache, read before the game lets go of the list.
        private static void Prefix(BoatLocalItems __instance, out List<int> __state)
        {
            __state = null;
            try
            {
                var saveable = __instance.GetComponent<SaveableObject>();
                int index = saveable ? saveable.sceneIndex : 0;
                if (!Applies(index)) return;
                var cached = __instance.GetCachedItems();
                if (cached == null) return;
                foreach (var data in cached)
                    if (data != null && data.crateId != 0) (__state ?? (__state = new List<int>())).Add(data.instanceId);
            }
            catch (Exception e) { Log.Warn("Crate contents: reading the cache failed (" + e.Message + "); her cargo comes back as in vanilla."); }
        }

        // Each of them, now loaded, goes into its crate as the game's own load does two frames later (InsertItem leaves an
        // item already in as it is). For a crate without its inventory yet (the game waits for one) the item's body is held
        // as InsertItem would hold it, until the game's insert. An item whose crate isn't in the world loses its crate ID,
        // so the game's load neither holds it nor looks for the crate, now or after a later load.
        private static void Postfix(List<int> __state)
        {
            if (__state == null) return;
            try
            {
                var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
                if (prefabs == null) return;
                // The latest of an instance ID wins: the item just loaded is the newest in the list.
                var byId = new Dictionary<int, SaveablePrefab>();
                foreach (var prefab in prefabs) if (prefab) byId[prefab.instanceId] = prefab;
                int inserted = 0, held = 0;
                var released = new List<string>();
                string failed = null;
                foreach (int id in __state)
                {
                    try
                    {
                        if (!byId.TryGetValue(id, out var prefab) || prefab.currentCrateId == 0) continue;
                        var item = prefab.GetComponent<ShipItem>();
                        if (!item) continue;
                        SaveablePrefab crateItem = null;
                        bool found = prefab.currentCrateId != id && byId.TryGetValue(prefab.currentCrateId, out crateItem) && crateItem;
                        var crate = found ? crateItem.GetComponent<CrateInventory>() : null;
                        switch (StepFor(found, crate != null))
                        {
                            case CrateStep.Insert: crate.InsertItem(item); inserted++; break;
                            case CrateStep.Hold: if (Hold(item)) held++; break;
                            default: prefab.currentCrateId = 0; released.Add(item.name); break;
                        }
                    }
                    catch (Exception e) { failed = failed ?? e.Message; }
                }
                Log.Debug("Crate contents: " + inserted + " put back into their crates at once, " + held + " held until their crate is ready, " + released.Count +
                    " let go (their crate isn't in the world).");
                if (released.Count > 0) Log.Info("Crate contents: " + released.Count + " item(s) came back without their crate, which isn't in the world, and lie loose: " + string.Join(", ", released.ToArray()) + ".");
                if (failed != null) Log.Warn("Crate contents: putting one back failed (" + failed + "); the game puts it back two frames later.");
            }
            catch (Exception e) { Log.Warn("Crate contents: putting them back failed (" + e.Message + "); the game puts them back two frames later."); }
        }

        // As CrateInventory.InsertItem holds an item's body: attached and out of the physics (inStove keeps
        // ItemRigidbody.FixedUpdate from switching its colliders back on; disableCol switches them off there).
        private static bool Hold(ShipItem item)
        {
            var body = item.GetItemRigidbody();
            if (!body) return false;
            body.attached = true;
            body.disableCol = true;
            body.inStove = true;
            return true;
        }
    }

    // ==== BoardShips (ship list) ====

    // The "ship list" tab (§5.3, §5.4); the transport pages are in Transport.cs.
    internal sealed partial class Board
    {
        private const string Separator = "  -  ";

        private static string Days(int days) => days + (days == 1 ? " day" : " days");

        // The ship list: every ship the player owns, wherever she is, five to a page. The page opens on the given ship,
        // or on the first row.
        internal void ShowShips(int index)
        {
            var entries = Ships.ListFor(info.Name, yard);
            items.Clear();
            int selected = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var ship = entries[i].Key;
                var facts = entries[i].Value;
                var where = Whereabouts.Decide(facts, info.Name);
                string note = Whereabouts.Note(where, facts);
                int shipIndex = ship.Index;
                items.Add(new BoardItem
                {
                    Text = note.Length == 0 ? ship.Name : ship.Name + Separator + note,
                    Plate = where == Where.ArrivingHere ? parts.Dark : null,
                    Clicked = () => ShowShips(shipIndex),
                });
                if (shipIndex == index) selected = i;
            }
            if (selected < 0 && entries.Count > 0) selected = 0;
            SetTabs(BoardTab.Ships);
            ShowList(BoardList.Ships, selected, true, "close", () => Close());
            if (selected < 0)
            {
                BeginPage(() => ShowShips(-1));
                page.Title("Ship list", false);
                page.Block(BoardPage.Left, 0.70f, "Your ships:", "none yet");
                return;
            }
            ShipPage(entries[selected].Key, entries[selected].Value);
        }

        private void ShipPage(OwnedShip ship, ShipFacts facts)
        {
            int index = ship.Index;
            BeginPage(() => ShowShips(index));
            NameTitle(() => Ships.NameOf(index), () => ShipNames.Typed(State.Save, index), name => ShipNames.SetTyped(State.Save, index, name));
            var where = Whereabouts.Decide(facts, info.Name);
            var record = Storage.RecordOf(index);
            var transport = Transport.RecordOf(index);
            if (transport != null && transport.tow)
            {
                // Booked or under way: the tow's own page with its map, wherever it goes.
                TowTripPage(transport);
                page.MainButton("Name plate", () => ShowStyles(index));
                return;
            }
            switch (where)
            {
                case Where.StoredHere: StoragePage(ship, record); break;
                case Where.LeavingHere: TripPage(transport, false); break;
                case Where.ArrivingHere: TripPage(transport, true); break;
                case Where.StoredElsewhere:
                    // Booked and waiting for sailors: her trip, as her storage yard shows it (the user, 2026-10-29: from another
                    // yard the page said only "Stored:", and "Yes, show it").
                    if (transport != null) { TripPage(transport, false); break; }
                    // Booked from here too (SPEC.md §6), with the same Transport button as her storage page's.
                    page.Block(BoardPage.Left, 0.64f, Whereabouts.Label(where, facts), Whereabouts.Value(where, facts));
                    var small = new Vector3(0.7f, 0.7f, 1f);
                    page.Button("Transport", new Vector3(0.49f, -0.602f, 0.639f), small, () => ShowDestinations(index, null, false));
                    page.Button("Name plate", new Vector3(0f, -0.602f, 0.639f), small, () => ShowStyles(index));
                    break;
                default:
                    // Any other ship: one line saying where she is, Transport (a tow, SPEC.md §6: the same button as a
                    // stored ship's) and Name plate (decision 72).
                    page.Block(BoardPage.Left, 0.64f, Whereabouts.Label(where, facts), Whereabouts.Value(where, facts));
                    var buttons = new Vector3(0.7f, 0.7f, 1f);
                    if (ship.Boat && transport == null)
                        page.Button("Transport", new Vector3(0.49f, -0.602f, 0.639f), buttons, () => ShowTowDestinations(index, null, false));
                    page.Button("Name plate", new Vector3(0f, -0.602f, 0.639f), buttons, () => ShowStyles(index));
                    break;
            }
        }

        // A ship stored here (picture 05, storage.json).
        private void StoragePage(OwnedShip ship, StoredShip record)
        {
            int currency = yard.region;
            int daily = Storage.DailyFee(record, currency);
            int due = Storage.FeeDue(record, currency);
            page.Block(BoardPage.Left, 0.64f, "Stored since:", "day " + record.dayStored);
            page.Block(BoardPage.Left, 0.41f, "Storage days:", Days(Storage.Days(record)));
            page.Block(BoardPage.Left, 0.18f, "Storage fee:", daily + " " + Money.Short[currency] + " a day", "paid when taken out");
            page.Block(BoardPage.Right, 0.64f, "To take it out:", "empty shipyard", "no other ship in it");
            // The fee due: left-aligned in the right column, a size larger.
            page.Text(parts.PriceLabel, "Fee due:", new Vector3(BoardPage.Right, 0.36f, 0.6488f), new Vector3(0.010695f, 0.01196f, 0.0104f), BoardPage.AnchorMiddleLeft, BoardPage.AlignLeft);
            page.Text(parts.Price, Money.Amount(due), new Vector3(BoardPage.Right, 0.225f, 0.651f), new Vector3(0.016875f, 0.016875f, 0.0125f), BoardPage.AnchorMiddleLeft, BoardPage.AlignLeft);
            page.Text(parts.Label, Money.Names[currency], new Vector3(BoardPage.Right, 0.117f, 0.648f), new Vector3(0.00782f, 0.009775f, 0.0085f), BoardPage.AnchorMiddleLeft, BoardPage.AlignLeft);
            int index = ship.Index;
            var small = new Vector3(0.7f, 0.7f, 1f);
            page.Button("Transport", new Vector3(0.49f, -0.602f, 0.639f), small, () => ShowDestinations(index, null, false));
            page.Button("Name plate", new Vector3(0f, -0.602f, 0.639f), small, () => ShowStyles(index));
            page.Button("Take out", new Vector3(-0.49f, -0.602f, 0.639f), small, () => TakeOut(index));
        }

        private void TakeOut(int index)
        {
            var record = Storage.RecordOf(index);
            if (record == null) { Refresh(); return; }
            string refusal = Storage.RefusalToTakeOut(yard, info, record);
            if (refusal != null) { Notify.Show(refusal); return; }
            int due = Storage.FeeDue(record, yard.region);
            if (PlayerGold.currency == null || PlayerGold.currency[yard.region] < due) { Notify.Show("Not enough money."); return; }
            var target = yard;
            var shipyard = info;
            Close();
            Plugin.Instance.StartCoroutine(Storage.TakeOut(target, shipyard, record));
        }
    }
}
