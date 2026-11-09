// The sailing transport (SAILING_TRANSPORT_DRAFT.md, its decisions and defaults by number): a booked ship really sails between
// shipyards, her own sailor at the helm. Moved from Shipwright and Storage's Source\Sailing\ on 2026-11-03 (decision 226; the
// user: "the actual sailing part become a separate .cs file"), each of its files a section below.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Crest;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShipwrightAndTransport.Sailing
{
    // ==== Voyages ====

    // The sailing transport (§16, SAILING_TRANSPORT_DRAFT.md): a Voyage for each sailing booking, its phases (waiting at the
    // origin berth, sailing, moored at the destination, stored), the booking's move to the berth, and the berth rules (D2,
    // D3). The patches ask it about every voyage in service.
    internal static class Voyages
    {
        private static readonly Dictionary<int, Voyage> live = new Dictionary<int, Voyage>();
        private static readonly HashSet<int> boarding = new HashSet<int>();
        private static readonly Dictionary<int, string> lastWhy = new Dictionary<int, string>();
        private static readonly HashSet<int> toldStepAshore = new HashSet<int>();
        private static float resumeAfter, nextPoll;
        private static ConfigEntry<Vector3> npcOffset;
        private static ConfigEntry<float> npcYaw;
        private static ConfigEntry<bool> shipsSail;
        private static GameObject templateOwner, helmsmanTemplate;
        private static float helmsmanRetryAt;

        // The hooks are in place (SailingPatches): without them every transport goes on paper (D1).
        internal static bool Enabled { get; private set; }

        internal static void Init(ConfigFile config, Harmony harmony)
        {
            shipsSail = config.Bind("Sailing", "Ships sail", false,"A ship booked at the shipyard it is stored in waits a day by the shipyard, then really sails to the other shipyard; one booked at another shipyard, and a tow, go on paper. Off: every transport goes on paper.");
            npcOffset = config.Bind("Sailing", "Helmsman offset", new Vector3(0f, -1f, -0.7f), "Where the helmsman figure stands, from the wheel, in the ship's axes (feet on deck).");
            npcYaw = config.Bind("Sailing", "Helmsman yaw", 0f, "The helmsman figure's turn, in degrees.");
            Enabled = SailingPatches.Install(harmony);
            // SailMaster is looked for once every plugin is loaded (Compat.Mods.ApplyLatePatches; review M6), not here.
            if (Enabled) Log.Info("Sailing transport ready.");
        }

        internal static Vector3 HelmsmanOffset => npcOffset?.Value ?? new Vector3(0f, -1f, -0.7f);
        internal static float HelmsmanYaw => npcYaw?.Value ?? 0f;

        internal static IEnumerable<Voyage> Live => live.Values;
        internal static Voyage Of(int saveIndex) => live.TryGetValue(saveIndex, out var voyage) ? voyage : null;

        // ---- The patches' questions ----

        internal static bool IsProtected(Component component)
        {
            if (!component || live.Count == 0) return false;
            foreach (var voyage in live.Values) if (voyage.OwnsControl(component)) return true;
            return false;
        }

        // A ship in service (taken in service from the booking to her storage): other mods' automation keeps off her (D8,
        // Compat\VirtualCrew).
        internal static bool InService(Component component)
        {
            if (!component || live.Count == 0) return false;
            foreach (var voyage in live.Values) if (voyage.Boat != null && voyage.Boat.Contains(component)) return true;
            return false;
        }

        internal static bool IsProtectedDamage(BoatDamage damage)
        {
            if (!damage || live.Count == 0) return false;
            foreach (var voyage in live.Values) if (voyage.OwnsDamageProtection(damage)) return true;
            return false;
        }

        internal static void ObserveOverflow(BoatDamage damage, float depth)
        {
            foreach (var voyage in live.Values) voyage.ObserveOverflow(damage, depth);
        }

        internal static bool AllowHorizonUpdate(BoatHorizon horizon)
        {
            if (!horizon || live.Count == 0) return true;
            foreach (var voyage in live.Values)
            {
                var answer = voyage.HandleHorizon(horizon);
                if (answer.HasValue) return answer.Value;
            }
            return true;
        }

        internal static bool KeepsIsland(IslandHorizon island)
        {
            if (!island) return false;
            foreach (var voyage in live.Values) if (voyage.KeepsIsland(island)) return true;
            return false;
        }

        internal static bool AllowPhysicsStep(Component component)
        {
            if (live.Count == 0) return true;
            foreach (var voyage in live.Values) if (!voyage.AllowPhysicsStep(component)) return false;
            return true;
        }

        internal static bool IsBoardingBlocked(Transform model)
        {
            foreach (var voyage in live.Values) if (voyage.BlocksBoarding(model)) return true;
            return false;
        }

        // ---- Ticks ----

        internal static void FixedUpdate()
        {
            if (!Enabled || live.Count == 0) return;
            foreach (var voyage in live.Values.ToArray()) voyage.FixedTick();
        }

        // Four times a second: a voyage for each sailing booking, taken in service when her ship is ready, and its phase.
        internal static void Update()
        {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.25f;
            double hours = GameClock.Hours;
            // Decision 225 (4): the sailing hooks not installed (SailingPatches.Install, after a game update), every sailing booking goes on
            // paper (decision 206's path); it was neither sailed nor carried, for good.
            if (!Enabled) { CarryOnPaper(hours); return; }
            foreach (var record in State.Save.transports.ToArray())
            {
                if (record.mode != 1 || boarding.Contains(record.saveIndex)) continue;
                if (!live.TryGetValue(record.saveIndex, out var voyage))
                {
                    try { voyage = new Voyage(record); }
                    catch (Exception e)
                    {
                        // A voyage that can't be read can't sail: the paperwork rules leave a ship that isn't stored where she is.
                        Log.Error("Sailing: " + Ships.NameOf(record.saveIndex) + "'s voyage can't be read (" + e.Message + "); her booking goes on paper.");
                        record.mode = 0; record.job = null; record.phase = 0;
                        continue;
                    }
                    live[record.saveIndex] = voyage;
                }
                try { Poll(record, voyage, hours); }
                catch (Exception e) { Report(record, "can't go on yet: " + e.Message); }
            }
            foreach (var index in live.Keys.ToArray())
                if (!State.Save.transports.Exists(t => t.saveIndex == index && t.mode == 1))
                {
                    live[index].Detach(Ships.NameOf(index) + ": her booking is gone.");
                    live.Remove(index);
                }
        }

        private static void Poll(TransportRecord record, Voyage voyage, double hours)
        {
            NoteGoingOn(record, voyage, hours);
            if (voyage.Mode == VoyageMode.Detached)
            {
                if (!WorldReady()) return;
                string notYet;
                try { notYet = voyage.Attach(out string attachWhy) ? null : "waiting for her ship and rig to register: " + attachWhy; }
                catch (Exception e) { notYet = "can't be taken in service yet: " + e.Message; }
                if (notYet != null)
                {
                    Report(record, notYet);
                    // Arrived on paper (D4), she goes into storage at her hour even if she can't be taken in service (decision
                    // 104): storing her needs no sailor. Any other voyage that can't go on stays where she is, and a day later goes on
                    // paper (decisions 98, 206: NoteGoingOn), arriving and stored as one that arrived on paper.
                    if (record.phase != 2 || !voyage.Job.onPaper) return;
                }
            }
            string why;
            switch (record.phase)
            {
                case 0:
                    if (voyage.Mode == VoyageMode.Waiting && voyage.Job.physics == null) voyage.CaptureBodies();
                    if (hours < record.departHour) return;
                    if (!WorldReady()) { Report(record, "due to leave, waiting for the game: " + WorldNotReady()); return; }
                    if (voyage.StartSailing(out why))
                    {
                        record.phase = 1;
                        // Far away, her clock counts from her departure hour, not from the moment it was noticed.
                        if (voyage.Simulation == SimulationMode.Background) voyage.Job.clockHour = Math.Min(voyage.Job.clockHour, record.departHour);
                        Log.Info("Sailing: " + voyage.Name + " sets out from " + record.from + " for " + record.to + ".");
                    }
                    else Report(record, why);
                    return;
                case 1:
                    if (voyage.Mode == VoyageMode.Attached && WorldReady() && !voyage.StartSailing(out why)) Report(record, why);
                    voyage.NoteLeftOrigin();
                    return;
                case 2:
                    // Arrived on paper (D4): her sailor stays, so she sails in for real if the player comes near.
                    if (voyage.Mode == VoyageMode.Attached && voyage.Job.onPaper && WorldReady() && !voyage.StartSailing(out why)) Report(record, why);
                    if (hours < record.storeHour) return;
                    // Decision 233: unless she has got nowhere for a day: then she goes into storage from where she lies.
                    if (voyage.Mode == VoyageMode.Sailing && voyage.Physical && voyage.Job.onPaper && hours < record.goingOnHour + Transport.HeldUpAfterHours) return;
                    if (WaitsForPlayerAshore(record, voyage) || WaitsForTiedBoat(record)) return;
                    Store(record, voyage);
                    return;
            }
        }

        // Decision 225 (4): what one look at a sailing booking with the sailing off does: on paper at once (Transport.OnPaper), arrived at her
        // quoted hour (Transport.PaperArrival), due for storage StoreAfterHours later, as decision 206's paper voyage.
        internal enum PaperProgress { None, OnPaper, Arrived, Due }
        internal static PaperProgress PaperStep(TransportRecord record, double hours)
        {
            if (record == null || record.mode != 1) return PaperProgress.None;
            if (record.phase < 2 && !Transport.OnPaper(record)) { record.paperHour = hours; return PaperProgress.OnPaper; }
            if (record.phase < 2 && hours >= Transport.PaperArrival(record))
            {
                record.phase = 2; record.storeHour = Transport.PaperArrival(record) + ReleaseSpots.StoreAfterHours;
                if (record.job != null) record.job.onPaper = true;
                return PaperProgress.Arrived;
            }
            return record.phase == 2 && hours >= record.storeHour ? PaperProgress.Due : PaperProgress.None;
        }

        private static void CarryOnPaper(double hours)
        {
            if (!Sun.sun) return;
            foreach (var record in State.Save.transports.ToArray())
            {
                switch (PaperStep(record, hours))
                {
                    case PaperProgress.OnPaper:
                        Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + "'s voyage goes on paper, the sailing transport being off (decision 225 (4)): she arrives in " +
                            record.to + " on day " + GameClock.DayOf(Transport.PaperArrival(record)) + ".");
                        break;
                    case PaperProgress.Arrived:
                        Arrived(record);
                        break;
                    case PaperProgress.Due:
                        if (!WorldReady() || WaitsForPlayerAshore(record, null) || WaitsForTiedBoat(record)) break;
                        Store(record, null);
                        break;
                }
            }
        }

        // Decision 98: her voyage goes on while it sails, holds (D6) or lies moored at the destination (arrived on paper and in
        // service counts as moored, D4); that hour is kept in her record, so the board can tell a held-up voyage
        // (Transport.HeldUp) with or without a live one. A voyage held up goes on paper (decision 206, NoteGoingOn). Decision 233: sailing
        // or holding in physics, only while she gets somewhere (Voyage.LastHeadway).
        internal static bool GoingOn(TransportRecord record, VoyageMode mode, bool onPaper) =>
            mode == VoyageMode.Sailing || mode == VoyageMode.Holding || mode == VoyageMode.Moored ||
            (record.phase == 2 && onPaper && mode == VoyageMode.Attached);

        private static void NoteGoingOn(TransportRecord record, Voyage voyage, double hours)
        {
            bool going = GoingOn(record, voyage.Mode, voyage.Job != null && voyage.Job.onPaper);
            if (going) record.goingOnHour = voyage.LastHeadway(hours);
            if (going && hours < record.goingOnHour + Transport.HeldUpAfterHours)
            {
                // Decision 206: her sailor has her again (the player came near her, or what held her up has gone): off paper, she sails on.
                if (Transport.OnPaper(record))
                {
                    record.paperHour = -1;
                    Log.Info("Sailing: " + voyage.Name + " sails again: her voyage is no longer on paper (decision 206).");
                }
            }
            // Decision 206 (the user, 2026-10-13: "make it become on paper, if player approach it later it come back as sailing"): held up,
            // she goes on paper; it was decision 98's stay where she is, her booking open.
            else if (record.phase < 2 && Transport.HeldUp(record, hours))
            {
                record.paperHour = hours;
                Log.Warn("Sailing: " + voyage.Name + " is held up: her voyage hasn't gone on for a day" + (lastWhy.TryGetValue(record.saveIndex, out var why) ? " (" + why + ")" : "") +
                    "; she goes on paper (decision 206): she arrives in " + record.to + " on day " + GameClock.DayOf(Transport.PaperArrival(record)) + ", unless her sailor can take her again first.");
            }
            // On paper (206), at her arrival: arrived on paper (D4), into storage half a game hour later; her sailor still sails her in
            // if he can take her with the player near.
            if (Transport.OnPaper(record) && hours >= Transport.PaperArrival(record))
            {
                double hour = Transport.PaperArrival(record);
                record.phase = 2; record.storeHour = hour + ReleaseSpots.StoreAfterHours;
                if (voyage.Job != null) voyage.Job.onPaper = true;
                Log.Info("Sailing: " + voyage.Name + " reached " + record.to + " on paper (decision 206); she goes into storage on day " + GameClock.DayOf(record.storeHour) + ".");
                Arrived(record);
            }
        }

        // D5: storage waits until the player is off her deck. A voyage not taken in service asks her ship itself.
        private static bool WaitsForPlayerAshore(TransportRecord record, Voyage voyage)
        {
            bool aboard;
            if (voyage?.Boat != null) aboard = voyage.Boat.PlayerAboard;
            else
            {
                var boat = Ships.BoatOf(record.saveIndex);
                var current = GameState.currentBoat;
                aboard = boat && current && (current == boat.transform || current.IsChildOf(boat.transform));
            }
            if (aboard && toldStepAshore.Add(record.saveIndex)) Notify.Show(StepAshore);
            return aboard;
        }

        internal const string StepAshore = "Step ashore: the ship\ngoes into storage.";

        // Decision 225 (23): as the player's Store refuses one (Storage.AttachedBoat: a boat tied to her or she to it, a cutter on her
        // davits), she isn't stored with a boat attached (her move to the storage field would drag it): she waits where she lies, logged.
        // So too while a crate of hers holds items (Storage.FilledCrates, Shipwright and Transport's store rule; crates may have been
        // loaded aboard while she waited at her berth): Claude's reading of 225 (23) for Shipwright and Transport. Decision 234: her crates
        // in her cache count too (a voyage's end on paper), and the player is told once, in Store ship's own words.
        private static bool WaitsForTiedBoat(TransportRecord record)
        {
            var boat = Ships.BoatOf(record.saveIndex);
            if (!boat) return false;
            int filled = Storage.FilledCrates(record.saveIndex, boat.GetComponent<BoatLocalItems>()).Count;
            string refusal = Storage.AttachedBoat(boat) ?? (filled > 0 ? Storage.FilledCratesText(filled) : null);
            if (refusal == null) return false;
            Report(record, "waits to go into storage: " + refusal.Replace("\n", " "));
            if (toldWaits.Add(record.saveIndex)) Notify.Show(refusal);
            return true;
        }
        private static readonly HashSet<int> toldWaits = new HashSet<int>();

        private static bool WorldReady() =>
            GameState.playing && !GameState.justStarted && !GameState.currentlyLoading && !GameState.changingStartRegion && !GameState.recovering &&
            GameState.loadingScenes == 0 && !GameState.loadingBoatLocalItems && SaveLoadManager.instance && FloatingOriginManager.instance &&
            !FloatingOriginManager.ShiftingThisFrame && Time.realtimeSinceStartup >= resumeAfter && !SaveBusy;

        // Which of WorldReady's conditions fail (for the log).
        private static string WorldNotReady() => string.Join(", ", new[]
        {
            GameState.playing ? null : "not playing", GameState.justStarted ? "just started" : null, GameState.currentlyLoading ? "loading" : null,
            GameState.changingStartRegion ? "changing start region" : null, GameState.recovering ? "recovering" : null,
            GameState.loadingScenes != 0 ? "loading scenes " + GameState.loadingScenes : null, GameState.loadingBoatLocalItems ? "loading boat items" : null,
            SaveLoadManager.instance && FloatingOriginManager.instance ? null : "no save or origin manager", FloatingOriginManager.ShiftingThisFrame ? "origin shifting" : null,
            Time.realtimeSinceStartup >= resumeAfter ? null : "resuming after a load or save", SaveBusy ? "saving" : null,
        }.Where(s => s != null).ToArray());

        private static bool SaveBusy => GameBindings.SaveBusy != null && SaveLoadManager.instance && (bool)GameBindings.SaveBusy.GetValue(SaveLoadManager.instance);

        // A waiting reason is logged when it changes, not every poll.
        private static void Report(TransportRecord record, string why)
        {
            if (string.IsNullOrEmpty(why)) return;
            if (lastWhy.TryGetValue(record.saveIndex, out var last) && last == why) return;
            lastWhy[record.saveIndex] = why;
            Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + ": " + why);
        }

        // ---- Phases ----

        // Held at the destination's release spot by her sailor (decision 203): half a game hour later she goes into storage (the user's
        // half hour; it was an hour at the berth).
        internal static void Moored(Voyage voyage)
        {
            var record = voyage.Record;
            bool told = record.phase == 2;
            record.phase = 2; voyage.Job.onPaper = false; record.storeHour = GameClock.Hours + ReleaseSpots.StoreAfterHours;
            voyage.EnterMoored();
            voyage.CaptureForSave(false);
            if (!told) Arrived(record);
        }

        // Her clock reached the route's end with the player away (D4): she counts as arrived at that hour, and goes into storage half a
        // game hour later (decision 203, the user's "6 ok").
        internal static void ArrivedOnPaper(Voyage voyage, double hour)
        {
            var record = voyage.Record;
            if (record.phase == 2) return;
            record.phase = 2; voyage.Job.onPaper = true; record.storeHour = hour + ReleaseSpots.StoreAfterHours;
            voyage.CaptureForSave(false);
            Log.Info("Sailing: " + voyage.Name + " reached " + record.to + " far from the player; she goes into storage on day " + GameClock.DayOf(record.storeHour) + ".");
            Arrived(record);
        }

        // D3: the destination's release spot stayed taken for half a game hour (decision 203, the user's 3b); she goes into storage from
        // where she holds, once the player is off her deck (D5). Until then she holds off, and comes in if it comes free.
        internal static void BerthStayedTaken(Voyage voyage)
        {
            var record = voyage.Record;
            if (record.phase != 2) { record.phase = 2; record.storeHour = GameClock.Hours; Arrived(record); }
            if (WaitsForPlayerAshore(record, voyage) || WaitsForTiedBoat(record)) return;
            Store(record, voyage);
        }

        private static void Arrived(TransportRecord record)
        {
            string name = Ships.NameOf(record.saveIndex);
            Log.Info(name + " arrived in " + record.to + ".");
            Notify.Show(name + " arrived in\n" + record.to + " shipyard.");
        }

        // Her storage at the destination (the draft's "Storage"): the service ends, her cargo is packed, and she goes to a
        // free slot of the parking field. With the field full she stays where she is, and the service ends there.
        private static void Store(TransportRecord record, Voyage voyage)
        {
            string name = Ships.NameOf(record.saveIndex);
            voyage?.Detach(name + "'s service ends at " + record.to + ".");
            live.Remove(record.saveIndex); lastWhy.Remove(record.saveIndex); toldStepAshore.Remove(record.saveIndex); toldWaits.Remove(record.saveIndex);
            State.Save.transports.Remove(record);
            int slot = Parking.FreeSlot(State.Save.stored);
            if (slot < 0)
            {
                Notify.Show(Storage.FieldFull);
                Log.Warn(name + " stays at " + record.to + ": the storage field is full.");
                return;
            }
            var stored = new StoredShip
            {
                saveIndex = record.saveIndex, shipyard = record.to, slot = slot, dayStored = GameClock.Day,
                lengthM = record.lengthM, feeUnits = record.feeUnits,
            };
            State.Save.stored.Add(stored);
            var boat = Ships.BoatOf(record.saveIndex);
            if (!boat) { Log.Warn(name + " was not found; her storage record waits at " + record.to + "."); return; }
            // Her cargo's value and weight, as Store ship measures them (Storage.Store: her fees, her next transport's speed).
            stored.massLb = Weights.ShipMass(boat);
            stored.cargoUnits = GameAccess.PackCargo(boat.GetComponent<BoatLocalItems>(), out stored.cargoLb);
            Storage.ForgetBoat(boat.transform);
            Plugin.Instance.StartCoroutine(Teleporter.Move(boat, Parking.World(State.Save.parkingField, slot), Parking.Heading, false));
            Log.Info("Stored " + name + " (save index " + record.saveIndex + ") at " + record.to + ", slot " + slot + ", after her voyage, cargo worth " +
                stored.cargoUnits + " units, " + Weights.Text(stored.massLb, stored.cargoLb) + ".");
        }

        // ---- Saving and loading ----

        // The game is about to save (SaveLoadManager.SaveGame): under vanilla's own refusal conditions nothing happens. Each
        // sailor lets go, so the game saves her settled, and takes her back 2 s later.
        internal static void BeforeSave()
        {
            if (!Enabled || live.Count == 0) return;
            if (!SaveLoadManager.instance || SaveBusy || GameState.inBed || !SaveLoadManager.readyToSave || GameState.currentShipyard) return;
            foreach (var voyage in live.Values)
            {
                try { voyage.CaptureForSave(voyage.Mode == VoyageMode.Sailing || voyage.Mode == VoyageMode.Holding); }
                catch (Exception e) { Log.Error("Sailing: saving " + voyage.Name + "'s voyage failed: " + e); }
            }
            resumeAfter = Time.realtimeSinceStartup + 2f;
        }

        // Shipwright's data is written (SaveModDataPatch): every voyage's latest state goes into its record first.
        internal static void CaptureForSave()
        {
            foreach (var voyage in live.Values)
            {
                try { voyage.CaptureForSave(false); }
                catch (Exception e) { Log.Error("Sailing: recording " + voyage.Name + "'s voyage failed: " + e); }
            }
        }

        // A load or a new game: every voyage of the old world lets go.
        internal static void Reset()
        {
            foreach (var voyage in live.Values)
            {
                try { voyage.Detach(voyage.Name + ": the world is reloading."); }
                catch (Exception e) { Log.Warn("Sailing: letting go of a voyage failed: " + e.Message); }
            }
            live.Clear(); boarding.Clear(); lastWhy.Clear(); toldStepAshore.Clear();
            resumeAfter = Time.realtimeSinceStartup + 3f;
        }

        internal static void AfterLoad() => resumeAfter = Time.realtimeSinceStartup + 3f;

        // ---- Booking ----

        // Why she would be carried rather than sail (D1), or null when she sails. Answers are kept 10 s (the board asks often).
        private static readonly Dictionary<int, KeyValuePair<float, string>> carried = new Dictionary<int, KeyValuePair<float, string>>();

        internal static string WhyCarried(StoredShip stored)
        {
            if (!Enabled || !GameBindings.WaterPatchReady || !Voyage.MooringBindingsReady) return "Sailing transport is off.";
            if (shipsSail != null && !shipsSail.Value) return "Ships sail is off in the config.";
            if (CompatibilityGate.HasConflict) return CompatibilityGate.ConflictReason;
            if (stored.lengthM < Fees.CarriedBelowMetres) return "She rides aboard a carrier.";
            if (carried.TryGetValue(stored.saveIndex, out var known) && Time.realtimeSinceStartup < known.Key) return known.Value;
            string why = null;
            try
            {
                var boat = ShipForChecks(stored.saveIndex, out bool owned);
                if (!boat) why = "Her ship isn't in the game.";
                else
                {
                    var context = BoatContext.Resolve(boat, owned);
                    var rig = RigScanner.Scan(context);
                    if (!rig.Supported) why = "The sailor doesn't know her rig: " + rig.Errors.FirstOrDefault();
                    else new NavigationWater(context, context.Contains);
                }
            }
            catch (Exception e) { why = e.Message; }
            carried[stored.saveIndex] = new KeyValuePair<float, string>(Time.realtimeSinceStartup + 10f, why);
            return why;
        }

        // Her own ship, or for a copy never built yet her hull's original (same hull and stock rig).
        private static PurchasableBoat ShipForChecks(int index, out bool owned)
        {
            var copy = CopyEngine.RecordOfIndex(index);
            if (copy != null && !copy.spawned) { owned = false; return Ships.OriginalBoat(copy.hull); }
            owned = true;
            return Ships.BoatOf(index);
        }

        // D2: no booking while another boat lies at the origin's service berth (decision 107: its hull in her footprint there),
        // or a ship waits there for her departure (the shipyard itself is closed meanwhile: ReleaseRefusal).
        internal static string BerthRefusal(StoredShip stored, ShipyardInfo info)
        {
            var waiting = WaitingAt(info.Name);
            if (waiting != null) return BerthTaken(waiting);
            var berth = Berths.Service(info.Name, stored.lengthM);
            if (!berth) return NoServiceBerth;
            Berths.Footprint footprint;
            try { footprint = new Berths.Footprint(WaterOf(stored)); }
            catch (Exception e) { Log.Info("Sailing: " + Ships.NameOf(stored.saveIndex) + "'s hull can't be measured for the berth (" + e.Message + ")."); return BerthMustBeFree; }
            var occupant = Berths.Occupant(berth.Place(footprint.Side), berth.Heading, footprint, null);
            if (!occupant) return null;
            Log.Info("Sailing: the service berth at " + info.Name + " is taken by " + occupant.name + " (its hull in her footprint there, its middle " +
                Vector3.Distance(occupant.transform.position, berth.Place(footprint.Side)).ToString("0") + " m from the berth" +
                (occupant.isPurchased() ? "" : ", not the player's") + ").");
            return BerthMustBeFree;
        }

        internal const string BerthMustBeFree = "The service berth must\nbe free.", NoServiceBerth = "This port has no\nservice berth.";

        // Her hull's envelope (NavigationWater), measured on her own ship, or for a copy never built on her hull's original.
        private static NavigationWater WaterOf(StoredShip stored)
        {
            var boat = ShipForChecks(stored.saveIndex, out bool owned);
            if (!boat) throw new InvalidOperationException("Her ship isn't in the game.");
            var context = BoatContext.Resolve(boat, owned);
            return new NavigationWater(context, context.Contains);
        }

        // Why a booking from this shipyard is carried rather than sailed for want of a service berth (D1, decision 129), or
        // null. Every shipyard's port has one in the game (Berths.Service); the destination's is read when she gets there, and
        // a port without one stores her without mooring.
        internal static string WhyNoServiceBerth(string from) => Berths.Service(from) ? null : "No service berth at " + from + ".";

        // Decision 106 (at every port since decision 203; at the origin since 218, which deleted 99's free shipyard): the game's exit puts
        // every ship it releases at once onto the shipyard's release spot (Shipyard.DischargeShip), so while a ship in service waits at the
        // shipyard's cleats beside it for her departure until her hull is clear of the shipyard (218), comes in to its release spot (her final approach
        // begun, or Kicia Bay's tow) or lies there until her storage, the shipyard admits, takes out and books no ship (the documents, the
        // board's take-out, Transport.Book). Else null.
        internal static string ReleaseRefusal(ShipyardInfo info)
        {
            if (info == null) return null;
            return State.Save.transports.Exists(t => WaitingAtBerth(t, info.Name) || AtReleaseSpot(t, info.Name)) ? ShipAtShipyard : null;
        }

        internal const string ShipAtShipyard = "A transported ship lies\nat the shipyard.";

        // A ship in service waiting at this port's shipyard cleats for her departure, or set out but still in its shipyard (decision 218:
        // "let it block the shipyard"; then the user, 2026-11-04: "as long as ship don't occupied shipyard anymore you should be able to do
        // all these, not 250m"): until her hull is clear of the shipyard's area and its release spot (Voyage.NoteLeftOrigin, asked afresh).
        internal static bool WaitingAtBerth(TransportRecord t, string shipyard)
        {
            if (t == null || t.mode != 1 || t.from != shipyard) return false;
            if (t.phase == 0) return true;
            if (t.phase != 1) return false;
            var voyage = Of(t.saveIndex);
            voyage?.NoteLeftOrigin();
            return !(voyage?.Job?.leftOrigin ?? t.job?.leftOrigin ?? false);
        }

        // A ship in service at (or coming in to) this port's release spot (decision 203): arrived and lying there, not on paper (D4: far
        // from the player she isn't there; she sails in for real if the player comes near, and counts from her final approach on); or her
        // final approach, Kicia Bay's tow in, or her hold there begun.
        internal static bool AtReleaseSpot(TransportRecord t, string shipyard)
        {
            if (t == null || t.mode != 1 || t.to != shipyard || t.phase < 1) return false;
            var job = Of(t.saveIndex)?.Job;
            int docking = job?.dockingPhase ?? t.job?.dockingPhase ?? 0;
            bool onPaper = job?.onPaper ?? t.job?.onPaper ?? false;
            return docking > 0 || t.phase == 2 && !onPaper;
        }

        private static TransportRecord WaitingAt(string shipyard) => State.Save.transports.Find(t => t.mode == 1 && t.phase == 0 && t.from == shipyard);
        private static string BerthTaken(TransportRecord waiting) => "The berth is taken until\nday " + GameClock.DayOf(waiting.departHour) + ".";

        // The route at the booking, while the origin island is loaded (the player is at its shipyard): out of the harbour from
        // the service berth, then around the islands to a staging point outside the destination's island (on a fine grid out
        // of the harbour first when the one-grid plan finds no way, review M4). Null, with why, when there is no clear way.
        internal static VoyageJob PlanJob(StoredShip stored, Transport.Quote quote, Shipyard yard, out string why)
        {
            why = null;
            try
            {
                var boat = ShipForChecks(stored.saveIndex, out bool owned);
                if (!boat) { why = "Her ship isn't in the game."; return null; }
                var context = BoatContext.Resolve(boat, owned);
                var rig = RigScanner.Scan(context);
                if (!rig.Supported) { why = "Unsupported rig."; return null; }
                var water = new NavigationWater(context, context.Contains);
                var service = Berths.Service(quote.From.Name, stored.lengthM);
                if (!service) { why = "No service berth at " + quote.From.Name + "."; return null; }
                float yaw = service.Heading;
                var origin = Voyage.Point(service.StablePlace(water.Radius));
                // Decision 220: from Kicia Bay her route starts at the tow start, where the tow out of its channel leaves her.
                var towOut = service.AtShipyard ? ReleaseSpots.TowOutFor(quote.From.Name) : null;
                var release = yard ? yard.shipReleasePosition : null;
                if (towOut != null && release && FloatingOriginManager.instance)
                {
                    double spotYaw = NavigationMath.Normalize(release.eulerAngles.y);
                    origin = ReleaseSpots.InAxes(Voyage.Point(FloatingOriginManager.instance.ShiftingPosToRealPos(release.position)), spotYaw, towOut.X, towOut.Z);
                    yaw = (float)NavigationMath.Normalize(spotYaw + ReleaseSpots.TowOutHeading(towOut));
                }
                var port = Ports.ByName(quote.To.Name);
                if (!port) { why = "The destination port isn't in the game."; return null; }
                var destination = Voyage.Point(FloatingOriginManager.instance.ShiftingPosToRealPos(port.transform.position));
                var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                double windFrom = NavigationMath.Heading(-wind.x, -wind.z);
                double noGo = rig.Sails.Any(s => s.squareSail) ? 75 : 60;
                var goal = water.ChartGoal(origin, destination);
                var route = RoutePlanner.PlanDepartureStaged(origin, goal, Voyage.Ahead(origin, yaw, Math.Max(60, water.Length * 4)), water.Clear, windFrom, noGo);
                if (route.Length < 2) { why = "No clear way out of the harbour: " + water.Obstruction; return null; }
                double length = Math.Max(1, RouteMath.Length(route));
                double seaHours = Math.Max(1, quote.Arrive - quote.Depart);
                var job = new VoyageJob
                {
                    jobId = Guid.NewGuid().ToString("D"), vesselName = context.Root.name, rig = Voyage.RigDescriptor(context),
                    vesselScene = stored.saveIndex, route = route, waypoint = 1, destination = destination,
                    length = length, done = 0, clockHour = quote.Depart, speed = length / seaHours,
                };
                why = job.Validate();
                return why == null ? job : null;
            }
            catch (Exception e) { why = e.Message; return null; }
        }

        // Her own name and rig, a frame after the rebuild (a copy was checked through her hull's original); her sails furled and
        // her yards on her centre line.
        private static void ReadRig(PurchasableBoat boat, TransportRecord record)
        {
            try
            {
                var context = BoatContext.Resolve(boat);
                record.job.vesselName = context.Root.name;
                record.job.rig = Voyage.RigDescriptor(context);
                var rebuilt = RigScanner.Scan(context);
                SailControlAdapters.FurlAtOnce(rebuilt); SailControlAdapters.HoldOnCentreLine(rebuilt);
            }
            catch (Exception e) { Log.Warn("Sailing: " + Ships.NameOf(record.saveIndex) + "'s rig: " + e.Message); }
        }

        // After paying: she comes from her slot to the origin's service berth (a copy never built is built there) and is moored
        // there as the game's rescue moors a ship (decision 129); her cargo comes back aboard, and the voyage takes her in
        // service (Voyage.TickWaiting checks her lines). If she can't be brought, the booking goes on paper and she stays stored.
        internal static IEnumerator Board(TransportRecord record, StoredShip stored, Shipyard yard)
        {
            boarding.Add(record.saveIndex);
            // Out of storage before she moves, as Take out does (the parking switch must not take her back meanwhile).
            State.Save.stored.Remove(stored);
            try
            {
                var service = Berths.Service(record.from, record.lengthM);
                // Decision 114: out at the shipyard's release spot, as the game releases a ship there (Shipyard.DischargeShip), and
                // the tow brings her to her berth (Voyage.TowInTick); a shipyard without one: placed at the berth as before.
                if (!yard) yard = Berths.Yard(record.from);
                bool released = yard && yard.shipReleasePosition && service;
                var copy = CopyEngine.RecordOfIndex(record.saveIndex);
                // The game's own way out of a shipyard (the user, 2026-10-07: "are you really using vanilla code for shipyard
                // release?"): as Take out brings a ship (Storage.TakeOut: the edit position, the game's AdmitShip, its slide, her
                // cargo unpacked after it), then the game's DischargeShip, what its Exit button does, releases her.
                if (released && !GameState.currentShipyard && yard.editedShipPosition)
                {
                    var edit = yard.editedShipPosition;
                    PurchasableBoat ship;
                    if (copy != null && !copy.spawned) ship = CopyEngine.Spawn(copy, edit.position, edit.rotation);
                    else
                    {
                        ship = Ships.BoatOf(record.saveIndex);
                        if (ship)
                        {
                            if (!ship.gameObject.activeSelf) ship.gameObject.SetActive(true);
                            GameAccess.KeepPacked(ship.GetComponent<BoatLocalItems>());
                            yield return Teleporter.Move(ship, edit.position, edit.rotation, true);
                        }
                    }
                    if (ship)
                    {
                        GameAccess.RefreshHorizon(ship);
                        Storage.Rerig(ship);
                        yield return null;
                        ReadRig(ship, record);
                        yield return new WaitForFixedUpdate();
                        float started = Time.realtimeSinceStartup;
                        // Through the documents click, as Take out admits (Storage.Admit); one that fails is backed out of.
                        if (GameState.currentShipyard == null && Storage.Admit(yard, ship, Ships.NameOf(record.saveIndex)))
                        {
                            while (!Storage.SlideOver(started)) yield return null;
                            if (GameAccess.UnpackWhenNear(ship.GetComponent<BoatLocalItems>())) CrateTakeOut.TakingOut(record.saveIndex);
                            for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();
                            if (GameState.currentShipyard == yard) yard.DischargeShip();
                            yield return new WaitForFixedUpdate();
                            record.job.toBerth = true; ++record.job.revision;
                            GameAccess.RefreshHorizon(ship);
                            Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + " is released by the shipyard of " + record.from + "; the tow brings her to her berth; she leaves on day " + GameClock.DayOf(record.departHour) + ".");
                            yield break;
                        }
                        // Backed out (Storage.Admit leaves her release to its caller): once the admit's slide is over, so it can't put her
                        // back at the edit spot, she is placed at the release spot below.
                        while (!Storage.SlideOver(started)) yield return null;
                        Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + ": the shipyard couldn't admit her; she is placed at its release spot instead.");
                    }
                }
                // Where she is put: the release spot, else her place at the berth for her own beam (decision 129).
                bool placed = released || service;
                Vector3 at = Vector3.zero;
                Quaternion turn = Quaternion.identity;
                if (released) { at = yard.shipReleasePosition.position; turn = yard.shipReleasePosition.rotation; }
                else if (service)
                {
                    float halfBeam = Berths.DefaultHalfBeam;
                    try { halfBeam = WaterOf(stored).Radius; }
                    catch (Exception e) { Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + "'s beam couldn't be measured for the berth (" + e.Message + "); a Dhow's is used."); }
                    at = service.Place(halfBeam); turn = service.Rotation;
                }
                PurchasableBoat boat = null;
                if (placed && copy != null && !copy.spawned) boat = CopyEngine.Spawn(copy, at, turn);
                else if (placed)
                {
                    boat = Ships.BoatOf(record.saveIndex);
                    if (boat)
                    {
                        if (!boat.gameObject.activeSelf) boat.gameObject.SetActive(true);
                        GameAccess.KeepPacked(boat.GetComponent<BoatLocalItems>());
                        // Her yards on her centre line before she is moved beside the dock (the user's pictures, 2026-10-07: swung
                        // out, the Dhow's yard lay on Dragon Cliffs' boardwalk within 10 frames of the move), half a second to swing in.
                        try { var rig = RigScanner.Scan(BoatContext.Resolve(boat)); SailControlAdapters.FurlAtOnce(rig); SailControlAdapters.HoldOnCentreLine(rig); }
                        catch (Exception e) { Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + "'s yards couldn't be hauled in before the move (" + e.Message + ")."); }
                        for (int i = 0; i < 25; i++) yield return new WaitForFixedUpdate();
                        yield return Teleporter.Move(boat, at, turn, true);
                    }
                }
                if (!boat)
                {
                    Log.Error("Sailing: " + Ships.NameOf(record.saveIndex) + " could not be brought to the berth; her booking goes on paper.");
                    record.mode = 0; record.job = null; record.phase = 0;
                    if (!State.Save.stored.Contains(stored)) State.Save.stored.Add(stored);
                    yield break;
                }
                if (released) { record.job.toBerth = true; ++record.job.revision; }
                else
                {
                    // Her lines laid on the berth's cleats as the game's rescue does it, then two physics steps for them to tie.
                    try { Berths.MoorAtService(boat.GetComponent<BoatMooringRopes>(), service); }
                    catch (Exception e) { Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + "'s lines couldn't be laid on the berth's cleats (" + e.Message + ")."); }
                    // Their slack at once (decision 113), and again after the two steps a line takes to tie.
                    Voyage.SlackenAtTie(boat.GetComponent<BoatMooringRopes>());
                    yield return new WaitForFixedUpdate();
                    yield return new WaitForFixedUpdate();
                    Voyage.SlackenAtTie(boat.GetComponent<BoatMooringRopes>());
                }
                GameAccess.RefreshHorizon(boat);
                Storage.Rerig(boat);
                // The rebuild destroys her old sails at the end of this frame (Object.Destroy): her rig is read a frame later.
                // Read at once, it held a sail twice and never matched her again (launch 5, 2026-10-06: "her rig differs").
                yield return null;
                ReadRig(boat, record);
                yield return new WaitForFixedUpdate();
                if (GameAccess.UnpackWhenNear(boat.GetComponent<BoatLocalItems>())) CrateTakeOut.TakingOut(record.saveIndex);
                GameAccess.RefreshHorizon(boat);
                Log.Info("Sailing: " + Ships.NameOf(record.saveIndex) + (released ? " is released at the shipyard of " + record.from + "; the tow brings her to her berth; she leaves on day " : " lies at the service berth of " + record.from + " until day ") + GameClock.DayOf(record.departHour) + ".");
            }
            finally { boarding.Remove(record.saveIndex); }
        }

        // ---- The helmsman figure (looks only) ----

        internal static GameObject HelmsmanTemplate()
        {
            if (helmsmanTemplate) return helmsmanTemplate;
            if (Time.realtimeSinceStartup < helmsmanRetryAt) return null;
            helmsmanRetryAt = Time.realtimeSinceStartup + 30f;
            try
            {
                if (!templateOwner) { templateOwner = new GameObject("ShipwrightHelmsmanTemplate"); UnityEngine.Object.DontDestroyOnLoad(templateOwner); }
                helmsmanTemplate = StaticHelmsman.CaptureTemplate(templateOwner.transform);
            }
            catch (Exception e) { Log.Info("Sailing: no helmsman figure yet: " + e.Message); }
            return helmsmanTemplate;
        }
    }

    // ==== Voyage ====

    // Adapted from the Ship Transport Prototype's TransportService (ChatGPT "Astra"'s work for the user, handed over 2026-10-06;
    // decisions 92-93): one per sailing booking (SAILING_TRANSPORT_DRAFT.md). Faults hold instead of letting go (D6); far away
    // she travels by the game clock (decision 95); her controls stay locked from the booking to her storage (decision 97).

    // Detached: no live ship (not found yet, or after a load). Waiting: moored at the origin berth. Attached: locked, the
    // sailor not started yet (after a load or a save). Sailing: the sailor has her. Holding: a fault; furled, retrying (D6).
    // Moored: at the destination berth.
    internal enum VoyageMode { Detached, Waiting, Attached, Sailing, Holding, Moored }

    internal sealed partial class Voyage : IDisposable
    {
        internal readonly TransportRecord Record;
        internal VoyageJob Job;
        internal VoyageMode Mode { get; private set; } = VoyageMode.Detached;
        private BoatContext boat;
        private RigSnapshot rig;
        private ControlOwnership ownership;
        private IDisposable helmsman;
        private SailControlAdapters sails;
        private WaterIngressSensor waterIngress;
        private HelmAdapter helm;
        private BackgroundTravel background;
        private readonly HeadingController heading = new HeadingController();
        private HeelGovernor heel = new HeelGovernor(1);
        private float tickClock, signatureClock;
        private ProgressWatchdog progressWatchdog = new ProgressWatchdog();
        private int exceptionReported;
        private float holdUntil;

        internal TransportStatus Status { get; private set; } = new TransportStatus { Phase = TransportPhase.Idle };
        internal bool TakingWater { get; private set; }
        internal float DeckOverflowDepth => waterIngress?.OverflowDepth ?? 0;
        internal BoatContext Boat => boat;
        internal SimulationMode Simulation => background?.Mode ?? SimulationMode.Physical;
        internal bool Physical => boat != null && (background == null || background.Mode == SimulationMode.Physical);
        internal string Name => Ships.NameOf(Record.saveIndex);
        // Decision 233: the last hour she got somewhere, sailing or holding in physics; `now` while the clock carries her (which moves her
        // anyway), and in any other state.
        private readonly Headway headway = new Headway();
        internal double LastHeadway(double now)
        {
            if (!Physical || !boat.Valid || Mode != VoyageMode.Sailing && Mode != VoyageMode.Holding) { headway.Reset(); return now; }
            var here = boat.StablePosition;
            return headway.Observe(now, here.x, here.z, DistanceToTarget());
        }

        internal Voyage(TransportRecord record)
        {
            Record = record ?? throw new ArgumentNullException(nameof(record));
            if (record.job != null) Job = VoyageJob.From(record.job, record.saveIndex);
        }

        // ---- Her live context and locks: from the booking (or a load) to her storage ----

        // Finds her, scans her rig and locks her controls (decision 97). False while she isn't ready yet.
        // `notYet`: why not yet (logged by Voyages when it changes; launch 5's first runs could not tell the two apart).
        internal bool Attach(out string notYet)
        {
            notYet = null;
            if (boat != null) return true;
            var purchasable = Ships.BoatOf(Record.saveIndex);
            if (!purchasable) { notYet = "her ship isn't in the game yet"; return false; }
            // Switched off by the game (beyond her horizon) she is taken in service all the same, judged as if on as the rig scan and
            // the departure's checks judge her (RigScanner.On): the clock carries her from there (the user, 2026-10-10 night: "Fix:
            // take her in service"). A save loaded with her far away left her voyage detached for good, "her ship is switched off",
            // never carried by the clock nor arriving while the player stayed away (the arrival lab from a save, run 20261010-210406).
            var context = BoatContext.Resolve(purchasable);
            if (Job != null && context.Root.name != Job.vesselName) throw new InvalidOperationException("Saved vessel type/name does not match.");
            var snapshot = RigScanner.Scan(context);
            if (!snapshot.Supported) throw new NotSupportedException("Unsupported rig: " + string.Join(" | ", snapshot.Errors));
            string current = Job != null ? RigDescriptor(context) : null;
            if (Job != null && !RigIdentity.Matches(Job.rig, current)) { notYet = "her rig differs from her booking's (booked " + Job.rig + "; now " + current + ")"; return false; }
            var locks = new ControlOwnership(context, snapshot);
            locks.Acquire();
            boat = context; rig = snapshot; ownership = locks;
            // Off every shipyard's area she was counted in, so none admits her (SailingPatches.ShipyardAreaPrefix keeps her off).
            foreach (var yard in Shipyards.Loaded()) Shipyards.BoatsInArea(yard).Remove(context.Root.gameObject);
            signatureClock = 0;
            Mode = RestingMode;
            Status = new TransportStatus { Phase = TransportPhase.Preparing, Message = "Locked at her berth." };
            Log.Info("Sailing: " + Name + " in service (" + rig.Description + ").");
            return true;
        }

        internal bool OwnsControl(Component component) => ownership != null && ownership.IsProtected(component);
        internal bool OwnsDamageProtection(BoatDamage damage) => boat != null && damage && damage == boat.Damage;
        internal void ObserveOverflow(BoatDamage damage, float depth)
        {
            if (sails != null && boat != null && !boat.Body.isKinematic) waterIngress?.ObserveOverflow(damage, depth);
        }
        internal bool BlocksBoarding(Transform model) => background != null && background.BlocksBoarding(model);
        internal bool AllowPhysicsStep(Component component)
        {
            if (background == null || background.Mode == SimulationMode.Physical || boat == null) return true;
            if (component is Sail sail) return sail.shipRigidbody != boat.Body;
            if (component is Rudder rudder) return rudder.shipRigidbody != boat.Body;
            return component != boat.Anchor;
        }

        // Null when the horizon isn't hers to run; otherwise whether the game's own Update may still run.
        internal bool? HandleHorizon(BoatHorizon horizon)
        {
            if (background == null && DepartureUnpack(horizon)) return false;
            if (background == null || !background.Owns(horizon)) return null;
            try { background.RefreshHorizon(); return false; }
            catch (Exception ex) { Hold("Horizon handover fault: " + ex.Message); return true; }
        }

        // Decision 221: due to leave with the player beyond the game's 1 km but within SailAgainWithin (she waited under the game's horizon,
        // which packed her cargo and froze her there), her horizon is held "close" so the game unpacks her where she lies (BoatLocalItems
        // spawns a frozen ship's cache), frozen until her cargo is settled; then her sailor starts as within 1 km. Beyond SailAgainWithin
        // she leaves by the clock as before (Claude's call, decision 229: the clock's return would bring her back moving 2.5 m/s ahead at
        // her berth, past the push and Kicia Bay's tow out).
        private bool DepartureUnpack(BoatHorizon horizon)
        {
            if (boat == null || horizon != boat.Horizon || Mode != VoyageMode.Waiting || Record.phase != 0 || GameClock.Hours < Record.departHour) return false;
            double d = boat.ObserverDistance;
            if (boat.PlayerAboard || !NavigationMath.Finite(d) || d <= 1000 || d > DistanceHandover.SailAgainWithin) return false;
            if (!GameState.playing || GameState.currentlyLoading || GameState.justStarted || GameState.recovering || GameState.currentShipyard || GameState.sleeping) return false;
            if (horizon.fullySunkDistance > 1f) horizon.distanceToPlayer = Mathf.Min((float)d, horizon.fullySunkDistance - 1f);
            horizon.closeToPlayer = true;
            if (boat.Body) boat.Body.isKinematic = !boat.Items || !boat.Items.SafeToEnableBoatPhysics();
            return true;
        }

        // Decision 218 (the user, 2026-11-04: "as long as ship don't occupied shipyard anymore you should be able to do all these, not
        // 250m"): her origin shipyard opens again (Voyages.WaitingAtBerth) once she is under way on her own (her departure's stages done:
        // the push back onto the spot, Kicia Bay's tow out) and her hull is clear of its area and its release spot (ShipyardArea). Where
        // the shipyard isn't loaded the player isn't at it, so she is in no ship's way. Checked at most once a second.
        private float leftOriginCheckAt;
        internal void NoteLeftOrigin()
        {
            if (boat == null || Job == null || Job.leftOrigin || Record == null) return;
            if (!Job.onPaper && (!Job.departureComplete || Job.unberthing)) return;
            if (Time.time < leftOriginCheckAt) return;
            leftOriginCheckAt = Time.time + 1f;
            var her = water != null ? new Berths.Footprint(water)
                : new Berths.Footprint { Fore = Record.lengthM / 2, Aft = Record.lengthM / 2, Side = Berths.DefaultHalfBeam };
            var hull = ShipyardArea.Hull(boat.Root.position.x, boat.Root.position.z, boat.Root.eulerAngles.y, her);
            foreach (var yard in Shipyards.Loaded())
                if (Shipyards.InfoOf(yard)?.Name == Record.from && ShipyardArea.Holds(yard, hull)) return;
            Job.leftOrigin = true;
            Log.Info("Sailing: " + Name + " is clear of " + Record.from + "'s shipyard (its area and its release spot)" + (Job.route != null && Job.route.Length > 0
                ? ", " + HorizontalDistance(boat.StablePosition, Vector(Job.route[0])).ToString("F0") + " m from her route's start" : "") + "; the shipyard is open again (decision 218).");
        }

        // ---- The sailor (the prototype's service) ----

        // Starts or resumes the sailor: at her departure (phase 0 to 1), after a load, after a save, or after a hold.
        internal bool StartSailing(out string why)
        {
            why = null;
            if (boat == null || Job == null) { why = "Not attached."; return false; }
            if (sails != null) return true;
            // A start that failed is tried again 5 s later, not every poll (it plans a route).
            if (Time.realtimeSinceStartup < nextStartAt) { why = lastStartWhy; return false; }
            nextStartAt = Time.realtimeSinceStartup + 5f;
            why = lastStartWhy = TryStartSailing();
            return why == null;
        }

        private float nextStartAt;
        private string lastStartWhy;
        // Decision 225 (2): her sail controller kept across a game save (BeforeSave lets her sailor go, StartSailing takes her back 2 s
        // later): her balance reefs (128, 178), the sails furled for her limit (153) and her sail mix (199) live in it, and were made anew
        // from nothing after every save. Kept while her rig is the one it was made for.
        private SailControlAdapters keptSails;
        private RigSnapshot keptSailsRig;
        // Decision 225 (2): her tack loss learned (161), kept for every navigator made for her (a save, a hold, back from the clock).
        private double keptTackLoss = TackNavigator.TackLossStart;

        private string TryStartSailing()
        {
            // Far away, or frozen by the game with her cargo packed at any distance (N13: at 388 m after a save she was neither
            // far enough for the clock nor unpacked for the sailor, and stayed so for a day): the clock's resume, which restores
            // her when she is within SailAgainWithin (decision 221).
            bool packed = !boat.PlayerAboard && boat.Items.HasLocalItems() && !GameAccess.AreItemsLoaded(boat.Items);
            // Decision 221: by the clock only beyond SailAgainWithin; due to leave nearer, she is unpacked where she lies and leaves in
            // physics (DepartureUnpack).
            bool cachedResume = packed && (boat.ObserverDistance > DistanceHandover.SailAgainWithin || boat.Body.isKinematic && Job.departureComplete);
            string why = StartBlockReason(boat, cachedResume);
            if (why != null) return why;
            if (!GameBindings.WaterPatchReady) return "Required water/damage protection patch is unavailable.";
            if (CompatibilityGate.HasConflict) return CompatibilityGate.ConflictReason;
            bool keptSailState = keptSails != null && keptSailsRig == rig;
            var sailControls = keptSailState ? keptSails : new SailControlAdapters(boat, rig);
            keptSails = null; keptSailsRig = null;
            try
            {
                water = new NavigationWater(boat, IsOwnVesselCollider, () => background != null && background.Mode == SimulationMode.Background
                    ? boat.Root.forward * background.Speed : boat.Body.velocity);
                anchorWinch = boat.Root.GetComponentsInChildren<GPButtonRopeWinch>(true).SingleOrDefault(w => w.rope == boat.Moorings.GetAnchorController());
                if (!anchorWinch) throw new NotSupportedException("Automatic departure needs the vessel's mapped anchor winch.");
                sails = sailControls;
                if (!keptSailState) ReadUpwindLimit();
                helm = new HelmAdapter(boat.Wheels);
                // Decision 199: her rudder for her reef plan (whole sails); unread, her sails are reefed together as before.
                sails.Rudder = helm.Model();
                Log.Info("Sailing: " + Name + "'s rudder for her sail plan (decision 199): " + (sails.Rudder != null
                    ? "rudderPower " + sails.Rudder.Power.ToString("F0") + ", dragPower " + sails.Rudder.Drag.ToString("F3") + ", full helm at " +
                      sails.Rudder.FullAngle.ToString("F0") + " degrees"
                    : "unread: her sails are reefed together as before") + ".");
                TryCreateHelmsman();
                heel = new HeelGovernor(Job.deployment);
                waterIngress = new WaterIngressSensor(boat); TakingWater = false;
                heading.Reset(); tickClock = signatureClock = 0; exceptionReported = 0;
                progressWatchdog = new ProgressWatchdog(Job.progressState);
                sails.Plan((float)heel.Deployment, false);
                background = new BackgroundTravel(boat, Job, Record.arriveHour) { LeavingPhysics = LeavingPhysics, NoGo = rig.Sails.Any(s => s.squareSail) ? 75f : 60f };
                if (cachedResume)
                {
                    // Far from the player at her departure: the clock takes her off the berth (her lines go first), and a ship
                    // never seen physical since the load lends her manifest from her bodies as they lie.
                    bool underWay = Job.departureComplete;
                    LeavingPhysics();
                    if (Job.physics == null) Job.physics = BackgroundTravel.Manifest(boat);
                    background.ResumeCached(Job, underWay);
                }
                else PrepareNavigation(NoGo);
                Mode = VoyageMode.Sailing;
                Status = new TransportStatus { Phase = Job.departureComplete ? TransportPhase.Sailing : TransportPhase.Preparing,
                    Message = cachedResume ? "Far travel by the clock." : "Route ready." };
                Log.Info("Sailing: " + Name + " " + (cachedResume ? "resumed far away, by the clock" : "has her sailor") + "; waypoint " + Job.waypoint + "/" + (Job.route.Length - 1) + ".");
                return null;
            }
            catch (Exception ex)
            {
                ReleaseSailor(true);
                return "Start failed: " + ex.Message;
            }
        }

        private static string StartBlockReason(BoatContext c, bool cachedResume)
        {
            if (!GameState.playing || GameState.currentlyLoading || GameState.justStarted || GameState.loadingBoatLocalItems || GameState.changingStartRegion || GameState.recovering || Time.timeScale == 0f) return "Game must be playable, loaded, and unpaused.";
            if (GameState.currentShipyard && GameState.currentShipyard.GetCurrentBoat() == c.Root.gameObject) return "The vessel is in a shipyard.";
            if (!FloatingOriginManager.instance || FloatingOriginManager.ShiftingThisFrame) return "Wait for world coordinates to settle.";
            // Decision 221: her cargo still aboard (a game save let her go while she sailed), she carries on out to SailWithin.
            if (!cachedResume && !c.PlayerAboard && !(c.ObserverDistance <= DistanceHandover.SailWithin)) return "Far away: waiting for the game to cache her cargo.";
            // Judged as if her ship were on (RigScanner.On): beyond the horizon the game switches her root off, and her helm read
            // inactive, so a ship the player had left never set out by the clock and was held up (run 20261010-093953, the arrival
            // lab's Dhow at Fort Aestrin, 89 km off).
            if (c.Wheels.Any(w => !RigScanner.On(w, c.Root))) return "A selected helm is inactive.";
            // Which of the two (launch 5, candidate K: once at her departure, then no start for 10 minutes; not known which).
            if ((!cachedResume && c.Body.isKinematic) || c.Damage.sunk)
                return "Vessel must be afloat and physically simulated (" + (c.Damage.sunk ? "sunk" : "frozen: kinematic, cargo " + (c.Items.SafeToEnableBoatPhysics() ? "settled" : "not settled")
                    + ", " + c.ObserverDistance.ToString("F0") + " m from the player") + ").";
            if (!NavigationMath.Finite(c.Body.velocity.sqrMagnitude) || !NavigationMath.Finite(c.Body.angularVelocity.sqrMagnitude)) return "Vessel physics is not ready.";
            if (!cachedResume && !c.Items.SafeToEnableBoatPhysics()) return "Vessel cargo/physics is not ready.";
            if (cachedResume && (!c.Items.HasLocalItems() || !c.Body.isKinematic)) return "Distant resume is waiting for vanilla cargo caching.";
            return null;
        }

        // The game is going to save: her state goes into the record, and the sailor lets go so the game saves her settled.
        // StartSailing takes her back once the save is done (Voyages).
        internal void CaptureForSave(bool releaseSailor)
        {
            if (Job == null) return;
            if (sails != null && boat != null)
            {
                Job.deployment = (float)heel.Deployment;
                Job.savedSpeed = Mathf.Clamp(background != null ? background.Speed : Vector3.Dot(boat.Body.velocity, boat.Root.forward), 0, 12);
                Job.referenceWind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up).magnitude;
                var flags = background?.CaptureBodyFlags();
                if (flags != null && flags.Length > 0) Job.physics = flags;
                Job.progressState = progressWatchdog.Capture();
                ++Job.revision;
            }
            Record.job = Job.ToRecord();
            if (releaseSailor && sails != null)
            {
                keptSails = sails; keptSailsRig = rig;
                if (background != null) background.ForSave = true;
                ReleaseSailor(false);
            }
        }

        // Before a far departure the body manifest comes from her moored at the berth, physical (BackgroundTravel's restore).
        internal void CaptureBodies()
        {
            if (boat == null || Job == null || boat.Body.isKinematic) return;
            Job.physics = BackgroundTravel.Manifest(boat);
        }

        // She goes out of the player's range before her departure was done: the clock does it, so her lines and anchor go
        // first (a frozen ship moved on her route must not drag a dock's spring).
        private void LeavingPhysics()
        {
            if (Job == null || Job.departureComplete || boat == null) return;
            boat.Moorings.UnmoorAllRopes();
            var controller = boat.Moorings.GetAnchorController();
            if (controller && (controller.currentLength > 0.005f || boat.Anchor.IsSet())) controller.ResetAnchor();
            Job.departureComplete = true; ++Job.revision;
            Log.Info("Sailing: " + Name + " left her berth by the clock (the player was away).");
        }

        // How she lies when she holds: her way, heading and the wind, whether the game has her switched on, how far the player
        // is (run 20261010-104154: physical 3 km out from Dragon Cliffs she held again and again, "Manoeuvre timed out without
        // steerage", and nothing said why).
        private string HoldState()
        {
            try
            {
                if (boat == null || !boat.Root) return "no ship";
                var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                double heading = boat.Root.eulerAngles.y, windFrom = NavigationMath.Heading(-wind.x, -wind.z);
                float along = boat.Body ? Vector3.Dot(boat.Body.velocity, boat.Root.forward) : 0f;
                return "speed " + (boat.Body ? boat.Body.velocity.magnitude : 0f).ToString("F2") + " m/s, " + along.ToString("F2") + " ahead; heading " + heading.ToString("F0") +
                    ", the wind " + wind.magnitude.ToString("F1") + " m/s from " + NavigationMath.Delta(heading, windFrom).ToString("F0") + " degrees off her bow; " +
                    (boat.Root.gameObject.activeInHierarchy ? "on" : "switched off by the game") + ", " + (background != null ? background.Mode.ToString() : "no simulation") +
                    ", the player " + boat.ObserverDistance.ToString("F0") + " m off; " + (navigator != null ? "manoeuvre " + navigator.State + ": " + navigator.Reason : "no navigator") +
                    "; sails " + (heel != null ? heel.Deployment.ToString("F2") : "?");
            }
            catch (Exception e) { return "state unreadable: " + e.Message; }
        }

        // ---- Holding (D6) ----

        // A fault never ends the service: she furls and holds, keeps her locks, and the sailor tries again in 2 minutes; far
        // away the clock carries her on.
        internal void Hold(string reason)
        {
            // Decision 225 (11): "in irons" and its side cleared too: kept, her sails stayed aback after the retry toward the side chosen
            // before the hold (BackSailInIrons).
            backing = false; ResetHelmWatch(); ironsSeconds = 0f; ironsSide = 0;
            if (Mode != VoyageMode.Sailing && Mode != VoyageMode.Holding) { Log.Warn("Sailing: " + Name + ": " + reason); return; }
            if (Mode == VoyageMode.Sailing) Log.Warn("Sailing: " + Name + " holds: " + reason + " (" + HoldState() + ")");
            EndTowContacts();
            Mode = VoyageMode.Holding;
            holdUntil = Time.realtimeSinceStartup + 120f;
            if (helm != null) { helm.Demand = 0; try { helm.Apply(); } catch { } }
            Status.Phase = TransportPhase.Suspended; Status.Message = "Holding: " + reason;
        }

        private void TickHolding(float dt)
        {
            // Far away the clock carries her on; near, she furls at the winches' rate and waits for the retry.
            if (background != null && background.Mode != SimulationMode.Physical) { TickFar(dt); return; }
            if (background != null && background.Tick(dt)) return;
            if (helm != null) { helm.Demand = 0; helm.Apply(); }
            try { if (sails != null && !boat.Body.isKinematic) sails.FurlAtNativeRate(dt); }
            catch (Exception e) { if (exceptionReported++ == 0) Log.Warn("Sailing: furling while holding failed: " + e.Message); }
            if (Time.realtimeSinceStartup < holdUntil) return;
            holdUntil = Time.realtimeSinceStartup + 120f;
            try
            {
                float noGo = NoGo;
                PrepareNavigation(noGo);
                Mode = VoyageMode.Sailing; heel = new HeelGovernor(0.25); heading.Reset();
                Status.Phase = TransportPhase.Sailing; Status.Message = "Sailing again after a hold.";
                Log.Info("Sailing: " + Name + " sails again after holding.");
            }
            catch (Exception e) { Status.Message = "Holding: " + e.Message; }
        }

        // ---- Ticks ----

        // Every physics step (Plugin.FixedUpdate).
        internal void FixedTick()
        {
            if (boat == null) return;
            if (!boat.Valid || !boat.Body || !boat.Root) { Detach("Vessel context became invalid."); return; }
            if (!GameState.playing || GameState.currentlyLoading || GameState.justStarted || GameState.changingStartRegion || GameState.recovering ||
                Time.timeScale == 0f || FloatingOriginManager.ShiftingThisFrame) return;
            float dt = Time.fixedDeltaTime;
            if (tickFault.Waiting(Time.realtimeSinceStartup)) return;
            try
            {
                switch (Mode)
                {
                    case VoyageMode.Waiting: TickWaiting(dt); break;
                    case VoyageMode.Sailing: TickSailing(dt); break;
                    case VoyageMode.Holding: TickHolding(dt); break;
                    // Decision 203: held at the release spot until her storage (the user's 1a).
                    case VoyageMode.Moored: TickMoored(dt); break;
                }
            }
            catch (Exception ex)
            {
                // Decision 225 (24): waiting or moored (no sailor to hold) the fault is logged once and her tick tried again TickFault's
                // seconds later; it was a warning every physics step.
                if (Mode != VoyageMode.Sailing && Mode != VoyageMode.Holding)
                {
                    if (tickFault.Note(ex.Message, Time.realtimeSinceStartup)) Log.Warn("Sailing: " + Name + "'s " + Mode + " tick failed (tried again every " +
                        TickFault.RetrySeconds.ToString("F0") + " s): " + ex);
                    return;
                }
                if (exceptionReported++ == 0) Log.Error("Sailing: tick failed for " + Name + "; she holds: " + ex);
                Hold("Controller fault: " + ex.Message);
            }
        }

        private readonly TickFault tickFault = new TickFault();

        private void TickSailing(float dt)
        {
            if (boat.Damage.sunk) { Hold("Vessel is sinking."); return; }
            signatureClock += dt;
            if (signatureClock >= 2f)
            {
                signatureClock -= 2f;
                string signature = RigScanner.Signature(boat);
                if (signature != rig.Signature)
                {
                    // Which sail and which of its values (runs 20261010-005136 and -020912 named nothing).
                    var sailNames = boat.Root.GetComponentsInChildren<Sail>(true).GroupBy(s => s.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ToDictionary(g => g.Key, g => g.First().name);
                    Log.Info("Sailing: " + Name + ": her rig's fingerprint changed: " + RigScanner.Difference(rig.Signature, signature, id => sailNames.TryGetValue(id, out var n) ? n : null) + ".");
                    Hold("Vessel or rig changed."); return;
                }
                TryCreateHelmsman();
            }
            if (background.Mode != SimulationMode.Physical) { TickFar(dt); return; }
            if (GameState.loadingBoatLocalItems) return;
            if (navigator == null) PrepareNavigation(NoGo);
            if (Status.Phase == TransportPhase.Suspended) { Status.Phase = TransportPhase.Sailing; Status.Message = "Sailing."; }
            var progressTarget = Job.dockingPhase > 0 ? Job.destination : Job.route[Job.waypoint];
            // Decision 222: her wait at anchor is no fault: her progress watch paused there (a sample of no time neither counts nor resets it);
            // decision 238: her wait for the seabed elsewhere too.
            progressWatchdog.Observe((Job.anchorStage == 1 && AnchorLegOpen) || seabedWait ? 0 : dt, RoutePoint.Distance(Point(boat.StablePosition), progressTarget), progressTarget.x, progressTarget.z);
            if (progressWatchdog.StalledSeconds >= 1200) { Hold("No voyage progress within the playable-time budget."); progressWatchdog = new ProgressWatchdog(); return; }
            // The player gone while she comes off the dock: the tow lets go and the clock takes her on. The unberthing stage never
            // reached the far handover below, and beyond the horizon the game switches her off, so the tow pulled a body that
            // can't move (run 20261010-092446's arrival launch: 28 kN, 0.00 m/s, touching nothing, the player 88 km off; held). Beyond her
            // physics range since decision 221.
            if (Job.unberthing && !boat.PlayerAboard && boat.ObserverDistance > DistanceHandover.SailWithin) LeaveUnberthingFar();
            // Every minute under way physically, how she lies (run 20261010-131113: physical 2.7 km out from Dragon Cliffs she
            // stood still for 30 minutes, "Direct: Following clear route leg", and no line said why).
            if ((underWayNoteIn -= dt) <= 0f)
            {
                underWayNoteIn = 60f;
                Log.Info("Sailing: " + Name + " under way: " + HoldState() + "; the apparent wind " + Math.Abs(NavigationMath.Delta(boat.Root.eulerAngles.y, ApparentFrom())).ToString("F0") + " degrees off her bow (her dead zone " + RigMinimum.ToString("F0") + ", close-hauled " + TackNavigator.BeatOff(DeadZone, Margin).ToString("F0") + "; her routes' no-go " + NoGo.ToString("F0") + " off the true wind; " + (sails != null ? sails.AngleReport() : "no sails") + "); route " + Job.done.ToString("F0") + " of " + Job.length.ToString("F0") + " m, waypoint " + Job.waypoint +
                    " (" + RoutePoint.Distance(Point(boat.StablePosition), Job.route[Math.Min(Job.waypoint, Job.route.Length - 1)]).ToString("F0") + " m off it, " +
                    WaypointOffBow().ToString("F0") + " degrees off her bow; helm " + (helm == null ? "none" : helm.Released ? "let go, the game's rudder at " + helm.Current.ToString("F2")
                    : helm.Demand.ToString("F2")) + (helmChanges > 0 ? ", let go or taken " + helmChanges + " times this minute" : "") + ")" +
                    (Job.unberthing ? ", unberthing" : "") + (Job.dockingPhase > 0 ? ", docking " + Job.dockingPhase : "") +
                    "; her navigation's cost this minute: the longest step " + navMostMs.ToString("F1") + " ms, " + navTotalMs.ToString("F0") + " ms in all, " +
                    (water != null ? water.Checks : 0) + " clearance checks; her sails' plans this minute: " + sailPlans + ", the longest " + sailMostMs.ToString("F1") +
                    " ms, " + sailTotalMs.ToString("F0") + " ms in all.");
                helmChanges = 0; navMostMs = navTotalMs = 0; if (water != null) water.Checks = 0; sailPlans = 0; sailMostMs = sailTotalMs = 0;
            }
            BackSailInIrons(dt);
            navClock.Restart();
            bool navigated = NavigateTick(dt);
            navClock.Stop();
            double navMs = navClock.Elapsed.TotalMilliseconds;
            navTotalMs += navMs; if (navMs > navMostMs) navMostMs = navMs;
            // Decision 225 (3): the handover to the clock runs whether or not her navigation holds (a tide wait, an obstructed approach,
            // the docking's waits): a player who left her then was never followed by the clock, and beyond her horizon the game switched
            // her off. Her sailor gone in the tick (moored at her destination) leaves nothing to hand over.
            if (background == null) return;
            if (background.Tick(dt)) return;
            if (background.ReturnedToPhysics) { BackInPhysics(); navigator?.GatherWay(45); }
            if (!navigated) return;
            if (!NavigationMath.Finite(boat.Body.velocity.sqrMagnitude) || !NavigationMath.Finite(boat.Body.angularVelocity.sqrMagnitude))
                throw new InvalidOperationException("Invalid vessel physics sample.");
            if (boat.Body.isKinematic) return;
            // Her cargo left in the game's cache (decision 121) doesn't stop her steering (run 20261010-151355: back in physics 2.6 km
            // out from Dragon Cliffs she sailed at 2-3 m/s with her helm never used, and wandered from her waypoint).
            if (!boat.Items.SafeToEnableBoatPhysics() && !background.CargoLeftCached) return;
            // Physical progress counts on her clock too, so far travel picks up where she is: projected from the leg her progress
            // has reached, not from her guide point (decision 127: cutting corners to a farther point mustn't jump her clock ahead).
            Job.done = Math.Max(0, Math.Min(Job.length, RouteMath.Project(Job.route, Point(boat.StablePosition), RouteMath.LegAt(Job.route, Job.done))));
            Job.clockHour = GameClock.Hours;
            Steer(dt);
            Trace();
        }

        // Far away: the clock moves her, hidden (BackgroundTravel); at the route's end with the player beyond SailAgainWithin she arrives
        // on paper (D4, decision 221; within it she is back in physics).
        private void TickFar(float dt)
        {
            background.Tick(dt);
            Status.Message = background.Message;
            if (background.ReturnedToPhysics)
            {
                BackInPhysics();
                PrepareNavigation(NoGo);
                return;
            }
            if (background.Mode == SimulationMode.Background && background.Reached && !boat.PlayerAboard && boat.ObserverDistance > DistanceHandover.SailAgainWithin && Record.phase == 1)
                Voyages.ArrivedOnPaper(this, double.IsNaN(background.ReachedHour) ? GameClock.Hours : background.ReachedHour);
        }

        // Back from the clock (in the physics step BackgroundTravel gave her bodies back, before the next): her helm and sails start
        // afresh, and decision 138 sets her sails at once as her sailor far away would have had them (SailControlAdapters.TrimAtOnce);
        // then the trace's 3 minutes.
        private void BackInPhysics()
        {
            heading.Reset(); heel = new HeelGovernor(0.25); tickClock = 0.5f; TraceFor(180);
            // Decision 215 (Claude's, a bug fix): a backing begun before she went far ends here, as a hold ends it: left set, her sails
            // were set at once (below) braced aback for it, and her first tick back ended it as a backing that failed.
            ResetHelmWatch(); ironsSeconds = 0f; ironsSide = 0; backing = false;
            helmHeldUntil = Time.time + HelmHeldBackSeconds;
            try
            {
                SetSailTurn();
                sails.ResetReefPlan();
                sails.Plan((float)heel.Deployment, false);
                string set = sails.TrimAtOnce();
                Log.Info("Sailing: " + Name + "'s sails are set at once as she comes back from the clock (decision 138): " + set + "; heading " +
                    boat.Root.eulerAngles.y.ToString("F0") + ", " + Vector3.Dot(boat.Body.velocity, boat.Root.forward).ToString("F2") + " m/s ahead, the apparent wind " +
                    NavigationMath.Delta(boat.Root.eulerAngles.y, ApparentFrom()).ToString("F0") + " degrees off her bow (+ starboard).");
            }
            catch (Exception e) { Log.Warn("Sailing: " + Name + "'s sails couldn't be set at once as she came back (" + e.Message + "); her winches set them."); }
        }

        // In irons (the wind within her no-go, under 0.5 m/s, for 10 s): her crew backs her sail to bring her bow off to the side
        // she lies toward, until the wind is 20 degrees past her no-go. Runs 20261010-142616 to -144150: outside Dragon Cliffs the
        // Dhow lay head to wind again and again, no steerage to wear or tack, and held. Through the sail (decision 126; the user,
        // 2026-10-10: no push): each sail set for the turning moment the game's forces give it (SailControlAdapters.TurnSide),
        // among the angles its ropes can hold: square sails braced aback, jibs held to windward, a lateen on its one sheet only
        // sheeted in or eased.
        private float ironsSeconds;
        private int ironsSide;

        private void BackSailInIrons(float dt)
        {
            if (backing || Job.unberthing || Job.dockingPhase > 0 || boat.Body.isKinematic) { ironsSeconds = 0; ironsSide = 0; SetSailTurn(); return; }
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            if (wind.sqrMagnitude < 1f) { ironsSeconds = 0; ironsSide = 0; SetSailTurn(); return; }
            // Decision 133: in irons is the apparent wind inside her least angle to it; off the wind again 20 degrees outside it.
            double heading = boat.Root.eulerAngles.y, windFrom = ApparentFrom();
            double off = NavigationMath.Delta(windFrom, heading);
            float noGo = (float)RigMinimum;
            if (ironsSide == 0)
            {
                // Inside close-hauled (her dead zone + 7: she can't sail closer) with under 0.5 m/s ahead, sternway counted as none
                // (run 20261011-015947, back from the clock 2.7 km off Gold Rock City: 19 degrees off a dead zone of 18, going astern at 0.69 m/s, her bow wouldn't fall off and she
                // held "Bearing away stalls"; the rule read her dead zone and her speed in any direction, and never fired).
                float ahead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
                // Not while she goes astern toward something (decision 164): her sail aback would drive her onto it.
                ironsSeconds = Math.Abs(off) < TackNavigator.BeatOff(noGo, Margin) && ahead < 0.5f && Time.time >= driftAsternUntil ? ironsSeconds + dt : 0f;
                if (ironsSeconds >= 10f)
                {
                    ironsSide = off >= 0 ? 1 : -1;
                    Log.Info("Sailing: " + Name + " lies in irons (the wind " + off.ToString("F0") + " degrees off her bow, " + boat.Body.velocity.magnitude.ToString("F2") +
                        " m/s): her sail is set aback to bring her bow off to " + (ironsSide > 0 ? "starboard" : "port") + ".");
                }
            }
            else if (Time.time < driftAsternUntil)
            {
                Log.Info("Sailing: " + Name + "'s sail is no longer set aback: she goes astern toward something (decision 164).");
                ironsSeconds = 0; ironsSide = 0;
            }
            else if (Math.Abs(off) >= noGo + 20 || Math.Sign(off) == -ironsSide && Math.Abs(off) > 30)
            {
                Log.Info("Sailing: " + Name + " is off the wind again (" + off.ToString("F0") + " degrees; her sail's turning moment was " + sails.TurnMoment.ToString("F0") + " N·m).");
                ironsSeconds = 0; ironsSide = 0;
            }
            SetSailTurn();
        }

        // Her sails' turn (SailControlAdapters.TurnSide): backing, braced for it; in irons, set aback toward ironsSide; else for drive.
        // Against a weather helm she reefs instead (decision 178: decision 134's eased sheets and its low-speed turn-only sail gone).
        private void SetSailTurn()
        {
            // Backing (decision 155): her sails for the turn her bow falls off in, square sails braced for the most push astern.
            // Decision 178 (the user, 2026-10-11 night: "you should reef when you are sail overpower rudder, not letting out the sheet"):
            // no sheet set to bear her away against a weather helm; she reefs (Steer).
            sails.TurnSide = backing ? backSide : ironsSide != 0 ? ironsSide : 0;
            sails.TurnOnly = backing || ironsSide != 0;
            sails.Astern = backing;
        }

        private double WaypointOffBow()
        {
            var p = Point(boat.StablePosition);
            var w = Job.route[Math.Min(Job.waypoint, Job.route.Length - 1)];
            return NavigationMath.Delta(boat.Root.eulerAngles.y, NavigationMath.Heading(w.x - p.x, w.z - p.z));
        }

        // A fine trace (2026-10-10 night: her first minutes back in physics and her turns couldn't be read from the minute lines):
        // every 2 s while it runs, her heading, way, turn, helm, sails and the apparent wind. TraceFor starts or lengthens it.
        private float traceUntil = -1f, traceAt;
        internal void TraceFor(float seconds) => traceUntil = Mathf.Max(traceUntil, Time.time + seconds);

        private void Trace()
        {
            if (Time.time > traceUntil || Time.time < traceAt || helm == null || sails == null) return;
            traceAt = Time.time + 2f;
            float heading = boat.Root.eulerAngles.y, ahead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            float turning = Vector3.Dot(boat.Body.angularVelocity, Vector3.up) * Mathf.Rad2Deg;
            Log.Info("Sailing: " + Name + " trace: heading " + heading.ToString("F0") + ", " + ahead.ToString("F2") + " m/s ahead, turning " + turning.ToString("F1") +
                " degrees a second; helm " + (helm.Released ? "let go, the game's rudder at " + helm.Current.ToString("F2") : helm.Demand.ToString("F2")) +
                "; sails " + heel.Deployment.ToString("F2") + " (unrolled " + sails.UnrollReport() + "), " + sails.AngleReport() + "; the apparent wind " + NavigationMath.Delta(heading, ApparentFrom()).ToString("F0") +
                " degrees off her bow (+ starboard); " + (navigator != null ? navigator.State + ": " + navigator.Reason : "no navigator") + (easing ? "; bearing away" : "") +
                (ironsSide != 0 ? "; in irons" : "") + (backing ? "; backing to " + backTarget.ToString("F0") : "") + (Job.unberthing ? "; leaving the berth: " + unberthStage + " to " + (double.IsNaN(escapeHeading) ? "none" : escapeHeading.ToString("F0")) +
                ", " + displaced.ToString("F1") + " m off the dock" : "") + ".");
        }

        private float weatherHelmSeconds;
        private bool noWaySlowLogged, circleLogged;
        private readonly TurnCircle.Watch circleWatch = new TurnCircle.Watch();
        private bool easing;
        private const float EaseReefStep = 2f;
        // Decision 211: how far inside her close-hauled angle her bow lies before her hard-over helm counts as overpowered whatever her
        // course's error (Claude's number: a bow pinching a degree or two is steering, not a weather helm).
        internal const double PinchedBy = 5;
        private float easeReefIn;

        // Decision 132 (HelmHold): on her course she lets go of the helm and the game's own rudder holds her (HelmAdapter.Release);
        // she takes it again when she strays, or off a plain course.
        private float helmNoteAt = -999f, guardNoteAt = -999f, fellNoteAt = -999f;
        // Decision 176: the turn rates for swinging through the wind's eye and for spinning against her helm (degrees a second).
        internal const float FellTurnRate = 3f, SpinRate = 10f, SpinEnd = 4f, SpinSeconds = 1f;
        // Decision 183: a spin is reefed against a step every SpinReefStep (was every second: the Dhow went to 30% in 7 s); a reef is let
        // out a step every LetOutStep while she is slow with nothing overpowering her helm and her helm light (SailBalance.SlowLetOut,
        // decision 186; her balance lets out only with way on, decision 128, and a slow Dhow at 30% never gathered way: runs
        // 20261011-205725 and -205735).
        internal const float SpinReefStep = 2f, LetOutStep = 2f;
        private float letOutIn = LetOutStep;
        private bool spinning;
        private float spinSeconds;
        private int helmChanges;

        // Decision 184 (d): her helm's watches start afresh when her sailor does (back from the clock, after a hold, off the dock): a
        // weather helm or a spin noted before (Steer runs in none of those stages, so nothing ended them) would reef her at once on her
        // new course and keep her from gathering way (TackNavigator.BearingAway) until her course came within 15 degrees of her bow.
        // Her reefs stay (decision 178).
        private void ResetHelmWatch()
        {
            easing = false; weatherHelmSeconds = 0f; easeReefIn = 0f; spinning = false; spinSeconds = 0f; letOutIn = LetOutStep;
        }

        // Back from the clock she keeps her helm this long before she may let go of it (run 20261013-020142: let go in her first moment
        // back, her turn zeroed by the return, she slewed round in the sea with her helm centred; HelmHold.TakeHelmYawRate).
        private const float HelmHeldBackSeconds = 30f;
        private float helmHeldUntil = float.NegativeInfinity;

        private void UpdateHelmHold(double error, float yawRate, float forward)
        {
            // Not close-hauled (decision 166, the user, 2026-10-11: "could've sail more close to the wind while still be able to go
            // forward"): let go on a beat, the game's rudder let her sag 6-7 degrees off the wind (run 20261011-163940 on EF: 46-48 off
            // the apparent wind, her close-hauled 36, at 1.6-1.8 m/s); she keeps the helm there.
            bool plain = navigator != null && navigator.State == Manoeuvre.Direct && !backing && !easing &&
                ironsSide == 0 && !GivingWay && Job.dockingPhase == 0 && !Job.unberthing && !narrowTurn && Time.time >= helmHeldUntil;
            var next = HelmHold.Next(helm.Released, plain, error, yawRate, forward, helm.Released ? helm.Current : helm.Demand);
            string change = null;
            if (next == HelmChange.Take)
            {
                change = "takes the helm again (" + (Math.Abs(error) > HelmHold.TakeHelmBeyond ? "her course " + error.ToString("F0") + " degrees off" : "not on a plain course with way on") +
                    "; the game's rudder at " + helm.Current.ToString("F2") + ")";
                // Decision 225 (5): her helm's controller from where her wheel lies (its sense reversed going astern, Steer).
                heading.Reset(forward > 0 ? helm.Current : -helm.Current);
                helm.Demand = helm.Current;
                helm.Apply();
            }
            else if (next == HelmChange.LetGo)
            {
                change = "lets go of the helm on her course (the helm at " + helm.Demand.ToString("F2") + "; the game's rudder holds her)";
                helm.Release();
            }
            if (change == null) return;
            ++helmChanges;
            // At most every 20 s in the log (the minute line counts them all).
            if (Time.time - helmNoteAt < 20f) return;
            helmNoteAt = Time.time;
            Log.Info("Sailing: " + Name + " " + change + "; " + forward.ToString("F2") + " m/s ahead.");
        }

        private float underWayNoteIn = 60f;
        // The cost of her navigation (the review of 2026-10-11 night: where her frame time goes, measured before optimizing further):
        // each NavigateTick timed (one clock for every voyage: they tick one after another), the longest and the total given in the
        // minute line with her clearance checks (NavigationWater.Checks), then started again.
        private static readonly System.Diagnostics.Stopwatch navClock = new System.Diagnostics.Stopwatch();
        private double navMostMs, navTotalMs;


        private void Steer(float dt)
        {
            var stablePos = boat.StablePosition;
            var toTarget = steeringTarget - stablePos;
            toTarget.y = 0;
            float targetHeading = (float)NavigationMath.Heading(toTarget.x, toTarget.z);
            float currentHeading = boat.Root.eulerAngles.y;
            float yawRate = Vector3.Dot(boat.Body.angularVelocity, Vector3.up) * Mathf.Rad2Deg;
            // Decision 133: never closer to the apparent wind than her least angle to it, but in a tack, or backing across the wind
            // (decision 160: DV's runs 20261011-142610 and -142625, the guard held her helm out of the wind's eye and she never turned).
            if ((navigator == null || navigator.State != Manoeuvre.Tacking) && !(backing && backThroughWind))
            {
                double guarded = UpwindLimits.Guard(targetHeading, currentHeading, ApparentFrom(), RigMinimum);
                if (Math.Abs(NavigationMath.Delta(targetHeading, guarded)) > 0.5)
                {
                    if (Time.time - guardNoteAt >= 30f)
                    {
                        guardNoteAt = Time.time;
                        Log.Info("Sailing: " + Name + " keeps out of the wind's dead zone: her course " + targetHeading.ToString("F0") + " lies within " + RigMinimum.ToString("F0") +
                            " degrees of the apparent wind (from " + ApparentFrom().ToString("F0") + "); steered " + guarded.ToString("F0") + ".");
                    }
                    targetHeading = (float)guarded;
                }
                // Nor turned through it (the user, 2026-10-10 night: "i notice you still turn into dead zone and you're not tacking"):
                // where the short way from her heading to her course crosses the apparent wind's eye, she turns the long way, round
                // her stern, led 60 degrees at a time; only the navigator's tack crosses the wind.
                double apparentNow = ApparentFrom();
                int sideNow = TackNavigator.Side(apparentNow, currentHeading);
                // Decision 173: her bow fallen across the wind's eye (inside close-hauled, slow or in irons, the wind shifting), her course
                // just across it on the side she was on: she bears off on the side she now lies on, and her sailor takes that side
                // (run 20261011-182737 on EN: the Dhow at 0.2-1.0 m/s off Dragon Cliffs, her bow a few degrees across, was turned 270
                // degrees round her stern 26 times and more, and never left the harbour). A wear still goes round her stern.
                // Decision 176: only when she has fallen across, turning under FellTurnRate; swinging through faster (her sail rounding
                // her up against her helm), she is steered back onto her own side, not borne off down the new one (ET, runs
                // 20261011-200820 and -200830: borne off on the side she swung onto, the Dhow spun on round, the user saw her helm locked
                // to one side).
                bool acrossEye = navigator != null && navigator.State != Manoeuvre.Wearing && navigator.Tack != 0 && navigator.Tack != sideNow &&
                    Math.Abs(NavigationMath.Delta(apparentNow, currentHeading)) < TackNavigator.BeatOff(RigMinimum, Margin) &&
                    TackNavigator.CrossesEye(currentHeading, targetHeading, apparentNow);
                if (acrossEye && Math.Abs(yawRate) >= FellTurnRate)
                {
                    float back = (float)NavigationMath.Normalize(apparentNow + navigator.Tack * TackNavigator.BeatOff(RigMinimum, Margin));
                    if (Time.time - fellNoteAt >= 30f)
                    {
                        fellNoteAt = Time.time;
                        Log.Info("Sailing: " + Name + " swings through the wind's eye (" + NavigationMath.Delta(apparentNow, currentHeading).ToString("F0") + " degrees off it, turning " +
                            yawRate.ToString("F0") + " degrees a second): steered back onto her side, " + back.ToString("F0") + " (decision 176).");
                    }
                    targetHeading = back;
                }
                else if (acrossEye)
                {
                    float onHerSide = (float)NavigationMath.Normalize(apparentNow + sideNow * TackNavigator.BeatOff(RigMinimum, Margin));
                    if (Time.time - fellNoteAt >= 30f)
                    {
                        fellNoteAt = Time.time;
                        Log.Info("Sailing: " + Name + "'s bow has fallen across the wind's eye (" + NavigationMath.Delta(apparentNow, currentHeading).ToString("F0") +
                            " degrees off it, her course " + targetHeading.ToString("F0") + " back across): she bears off on the side she lies on, steered " + onHerSide.ToString("F0") + " (decision 173).");
                    }
                    navigator.FellAcross("her bow fell across the wind's eye");
                    targetHeading = onHerSide;
                }
                else if (TackNavigator.CrossesEye(currentHeading, targetHeading, apparentNow))
                {
                    int side = sideNow;
                    float roundStern = (float)NavigationMath.Normalize(currentHeading + side * 60);
                    if (Time.time - guardNoteAt >= 30f)
                    {
                        guardNoteAt = Time.time;
                        Log.Info("Sailing: " + Name + " won't turn through the wind's dead zone to her course " + targetHeading.ToString("F0") + " (no tack under way): she turns round her stern, steered " +
                            roundStern.ToString("F0") + ".");
                    }
                    targetHeading = roundStern;
                }
            }
            // Decision 176: spinning against her helm (faster than SpinRate away from her course, the helm hard over against the turn
            // for SpinSeconds), she reefs a step every SpinSeconds (decision 178: reef, not the sheet) until the turn is under SpinEnd; the
            // reef is kept: at 1-1.4 m/s the Dhow's lateen turned her 12-27 degrees a second against full helm (runs 20261011-182737,
            // -191709, -200820).
            double spinError = NavigationMath.Delta(currentHeading, targetHeading);
            float spinAhead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            bool against = !backing && spinAhead > 0 && Math.Abs(yawRate) > SpinRate && Math.Abs(spinError) > 20 && Math.Sign(yawRate) == -Math.Sign(spinError) &&
                Math.Abs(helm.Demand) >= 0.9f && Math.Sign(helm.Demand) == Math.Sign(yawRate);
            // Decision 225 (17): reefed at the sail on the side she spins to (SpinReefSide), not always her aftmost.
            int spinReef = SpinReefSide(yawRate, NavigationMath.Delta(currentHeading, ApparentFrom()));
            if (!spinning) { spinSeconds = against ? spinSeconds + dt : 0f; if (spinSeconds >= SpinSeconds) { spinning = true; spinSeconds = 0f; Log.Info("Sailing: " + Name + " spins against her helm (" + yawRate.ToString("F0") + " degrees a second, her course " + spinError.ToString("F0") + " degrees off, " + spinAhead.ToString("F2") + " m/s): she reefs (decisions 176, 178): " + (sails.Balance(spinReef) ?? "her sail already at its least") + "."); } }
            else if (Math.Abs(yawRate) < SpinEnd || backing) { spinning = false; spinSeconds = 0f; Log.Info("Sailing: " + Name + " stops spinning (" + yawRate.ToString("F0") + " degrees a second): her reef kept (decision 178)."); }
            else if ((spinSeconds += dt) >= SpinReefStep) { spinSeconds = 0f; string more = sails.Balance(spinReef); if (more != null) Log.Info("Sailing: " + Name + " still spins (" + yawRate.ToString("F0") + " degrees a second): " + more + " (decision 178)."); }
            // Giving way (decision 102): her sails in to steerage, at the winches' rate, her course kept.
            // Backing (decision 155): her sail eased over 1.5 m/s of sternway (Backing.SailLimit).
            // Decision 162 (d): nothing clear within her safety distance (she looks nearer), her sail at steerage too, so she slows while
            // she finds a way (run 20261011-153651 on EC: running at 3.6 m/s with the wind astern into Dragon Cliffs' north gap, nothing
            // clear within 108 m, she kept every sail and struck Cube at 3.5 m/s). Not for an obstacle she can steer round.
            // Decision 171: also while she keeps clear on a heading that makes no way toward her guide point (EK's two Cog runs,
            // 20261011-173145 and -173154: "none makes way; taken: keeping clear", she ran down the wind at 3.2 m/s into Dragon
            // Cliffs' basin end and struck Cube at 3.1-3.2 m/s).
            bool keepingClearNoWay = navigator != null && navigator.State == Manoeuvre.Avoiding && navigator.NoWay && !backing;
            if (keepingClearNoWay && !noWaySlowLogged)
                Log.Info("Sailing: " + Name + " keeps clear with no course making way toward her guide point: her sail at steerage (decision 171).");
            noWaySlowLogged = keepingClearNoWay;
            bool slowForWay = (lookScale < 1f || keepingClearNoWay) && !backing;
            float limit = backing ? backSail : GivingWay || slowForWay ? Mathf.Min(DockingSailLimit(), (float)EmergencySailPolicy.SteerageDeployment) : DockingSailLimit();
            // Decision 196: in narrow water, by her size. Decision 201 (g): near her berth before her destination is charted, steerage.
            if (!backing) limit = Mathf.Min(limit, Mathf.Min(NarrowSailLimit(), ArrivalChartSailLimit()));
            // Her sail overpowering her rudder (decision 134's condition): the helm hard over 4 s, her course 30 degrees and more off and
            // further off the wind than her heading, her bow not turning toward it (run 20261010-155241: outside Dragon Cliffs the Dhow
            // rounded up against full helm; run -160852: she lay 17-21 degrees off the wind at 0.7 m/s for minutes, the game's rudder
            // weak at that speed against her lateen). Decision 178: she reefs (below) until she is within 15 degrees of it; no sheet is
            // eased. Further off the wind, and the side of it she sails on, on the apparent wind (decision 136).
            double windFromNow = ApparentFrom(), error = NavigationMath.Delta(currentHeading, targetHeading);
            bool bearingAway = Math.Abs(NavigationMath.Delta(windFromNow, targetHeading)) > Math.Abs(NavigationMath.Delta(windFromNow, currentHeading));
            bool notTurning = yawRate * Math.Sign(error) < 1f;
            float forward = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            // Decision 211 (the user, 2026-10-13: "proposal a approved"): also with her bow PinchedBy or more inside her close-hauled angle,
            // whatever her course's error (run 20261013-023006 off Gold Rock City: the Dhow 11-17 degrees inside it against a hard-over helm,
            // her course under 30 degrees off, so 178 never fired; her sail grew to all of it and she lay in irons).
            double pinched = TackNavigator.BeatOff(RigMinimum, Margin) - Math.Abs(NavigationMath.Delta(windFromNow, currentHeading));
            // Not in a tack: her bow crosses that angle on purpose there.
            bool tackingNow = navigator != null && navigator.State == Manoeuvre.Tacking;
            bool overpowered = bearingAway && notTurning && (Math.Abs(error) >= 30 || !tackingNow && pinched >= PinchedBy && Math.Abs(error) >= 1);
            // Decision 184 (a): the helm hard over as the turn-rate helm (decision 179) gives it, HeadingController.HardOver.
            weatherHelmSeconds = !backing && !helm.Released && Math.Abs(helm.Demand) >= HeadingController.HardOver - 0.01 && overpowered ? weatherHelmSeconds + dt : easing ? weatherHelmSeconds : 0f;
            if (!easing && weatherHelmSeconds >= 4f)
            {
                easing = true; easeReefIn = 0f; SetSailTurn();
                Log.Info("Sailing: " + Name + "'s sail overpowers her rudder (her course " + error.ToString("F0") + " degrees off" + (pinched > 0 ? ", her bow " + pinched.ToString("F0") +
                    " degrees inside her close-hauled angle" : "") + ", the helm hard over, " + forward.ToString("F2") + " m/s ahead): she reefs (decisions 178, 211).");
            }
            if (easing && Math.Abs(error) < 15 && pinched < PinchedBy)
            {
                easing = false; weatherHelmSeconds = 0f; SetSailTurn();
                Log.Info("Sailing: " + Name + " answers her helm again; her reef is kept (her balance lets it out on a steady course, decision 128).");
            }
            // Decision 178: a weather helm the rudder can't hold, the helm hard over and her bow still not answering, at any speed: the
            // sail making it is reefed a tenth at once and every EaseReefStep (her aftmost sail; a single sail as a whole), down to 30%,
            // until it does; the balance lets it out again on her course while her helm is light (decision 128), and decision 183 while
            // she is slow.
            if (easing && notTurning && (easeReefIn -= dt) <= 0f)
            {
                easeReefIn = EaseReefStep;
                string reefed = sails.Balance(1);
                if (reefed != null)
                    Log.Info("Sailing: " + Name + " reefs against her weather helm (her course " + error.ToString("F0") + " degrees off, the helm hard over, her bow not answering, " +
                        forward.ToString("F2") + " m/s): " + reefed + ".");
            }
            // Decision 183: slow, with nothing overpowering her helm, her reef comes out a step at a time, so she can gather way; decision
            // 186: only while her helm is light (SailBalance.SlowLetOut).
            float helmNow = helm.Released ? helm.Current : helm.Demand;
            // Decision 198 (a): her turning circle measured as she turns (toward the wind: turning to the side it comes from), a sample a
            // second once her helm's controller has asked its full turn rate the same way for a while (TurnCircle.Watch; decision 225 (10)).
            bool towardWind = Math.Sign(yawRate) == Math.Sign(NavigationMath.Delta(currentHeading, windFromNow));
            int askedFull = helm.Released ? 0 : heading.FullRateSide;
            if (water != null && circleWatch.Due(askedFull, Time.time) && Circle.Sample(forward, yawRate, askedFull, towardWind) && !circleLogged && (Circle.Measured(true) || Circle.Measured(false)))
            {
                circleLogged = true;
                Log.Info("Sailing: " + Name + "'s turning circle measured (decision 198 a): " + Circle.Describe(Math.Max(10, water.Length), HullWithoutBowsprit) + ".");
            }
            if (SailBalance.SlowLetOut(forward, easing || spinning || backing || weatherHelmSeconds > 0f, helmNow))
            {
                if ((letOutIn -= dt) <= 0f)
                {
                    letOutIn = LetOutStep;
                    string outNow = sails.Balance(0);
                    if (outNow != null) Log.Info("Sailing: " + Name + " is slow (" + forward.ToString("F2") + " m/s) with nothing overpowering her helm (" + Math.Abs(helmNow).ToString("F2") + "): " + outNow + " (decisions 183, 186).");
                }
            }
            else letOutIn = LetOutStep;
            UpdateSailSafety(dt, limit);
            UpdateHelmHold(error, yawRate, forward);
            if (!helm.Released)
            {
                // Decision 179: steered by her turn rate. Something on her way ahead (VoyageNavigation's avoidUntil): a faster turn wanted
                // toward her clear course (AvoidTurnRate), no longer the helm forced hard over (run 20261011-120507 had hard over each way).
                helm.Demand = (float)heading.Tick(currentHeading, targetHeading, yawRate, dt, Time.time < avoidUntil ? AvoidTurnRate : HeadingController.TurnRateMost);
                // Vanilla reverses its yaw torque for every non-positive forward velocity, including near-zero drift.
                if (forward <= 0) helm.Demand = -helm.Demand;
                helm.Apply();
            }
            BalanceSails(dt, windFromNow, currentHeading, forward);
        }

        // Decision 128 (SailBalance): on a steady course with way on, the helm she needs balanced by her sails: weather helm (the
        // helm bearing her away, a negative demand turning her to starboard) reefs a sail aft, lee helm one forward, a light helm
        // lets a reefed one out.
        private readonly SailBalance balance = new SailBalance();

        private void BalanceSails(float dt, double windFrom, float heading, float forward)
        {
            bool steady = navigator != null && (navigator.State == Manoeuvre.Direct || navigator.State == Manoeuvre.CloseHauled) && !backing && !easing &&
                ironsSide == 0 && !GivingWay && forward > 1f && Job.dockingPhase == 0 && !Job.unberthing;
            if (!steady || dt <= 0) { balance.Reset(); return; }
            // The helm she needs: hers when she steers, the game's rudder when she has let go (decision 132).
            var action = balance.Sample(-TackNavigator.Side(windFrom, heading) * (helm.Released ? helm.Current : helm.Demand), dt);
            if (action == BalanceAction.None) return;
            string change = sails.Balance(action == BalanceAction.LetOut ? 0 : action == BalanceAction.ReefAft ? 1 : -1);
            if (change != null)
                Log.Info("Sailing: " + Name + " balances her sails (" + (balance.WeatherHelm >= 0 ? "weather" : "lee") + " helm " +
                    Math.Abs(balance.WeatherHelm).ToString("F2") + " over " + SailBalance.Window.ToString("F0") + " s, " + forward.ToString("F2") + " m/s): " + change + ".");
        }

        private float HorizontalDistance(Vector3 a, Vector3 b) { float x = a.x - b.x, z = a.z - b.z; return Mathf.Sqrt(x * x + z * z); }
        private float DistanceToTarget() => HorizontalDistance(boat.StablePosition, Vector(Job.destination));

        private static PlayerEmbarkerNew embarker;

        private bool IsOwnVesselCollider(Collider collider)
        {
            // Vanilla's head-mounted inventory menu has non-trigger BoxColliders.
            // They follow an approaching passenger and are UI, not water hazards.
            if (PlayerNeedsUI.instance && collider.transform.IsChildOf(PlayerNeedsUI.instance.transform)) return true;
            if (collider.transform == boat.Root || collider.transform.IsChildOf(boat.Root)) return true;
            if (boat.OwnsPlayerHullCollider(collider)) return true;
            if (collider.GetComponentInParent<Anchor>() == boat.Anchor) return true;
            var mooring = collider.GetComponentInParent<PickupableBoatMooringRope>();
            if (mooring && boat.Contains(mooring)) return true;
            // The walking body lives outside the model hierarchy. A passenger in
            // this ship's embark volume must be allowed to complete boarding.
            if (global::Refs.charController && collider == global::Refs.charController)
            {
                // Aboard first; the embarker found once, not by a scene-wide search for each collider a clearance query meets.
                if (boat.PlayerAboard) return true;
                if (!embarker) embarker = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
                var contact = embarker ? GameBindings.BoardingBoat.GetValue(embarker) as EmbarkBoat : null;
                if (contact != null && boat.Contains(contact.worldBoat)) return true;
            }
            // Vanilla HullPlayerCollider creates an unparented collision proxy. Its
            // cleaning link identifies the actual hull; other boats remain obstacles.
            var proxy = collider.GetComponent<CleanableObjectCollider>();
            return proxy && proxy.parentCleanable && boat.Contains(proxy.parentCleanable);
        }

        // Docking proved native-rate furling and a 30-second settled berth (VoyageDocking).
        private void CompleteVoyage(string message)
        {
            Log.Info("Sailing: " + Name + ": " + message);
            Voyages.Moored(this);
        }

        // ---- Letting go ----

        // The sailor lets go (helm, sails, far travel, helmsman); her locks stay.
        private void ReleaseSailor(bool emergencyFurl)
        {
            try { if (emergencyFurl) sails?.EmergencyFurlImmediate(); else sails?.StopInputs(); }
            catch (Exception e) { Log.Warn("Sailing: sail control cleanup: " + e.Message); }
            try { background?.Dispose(); } catch (Exception e) { Log.Warn("Sailing: background cleanup: " + e.Message); }
            try { helm?.Dispose(); } catch (Exception e) { Log.Warn("Sailing: helm cleanup: " + e.Message); }
            try { helmsman?.Dispose(); } catch (Exception e) { Log.Warn("Sailing: helmsman cleanup: " + e.Message); }
            if (anchorWinch) GameBindings.WinchInput.SetValue(anchorWinch, 0f);
            anchorWinch = null;
            if (navigator != null) keptTackLoss = navigator.LossPerDegree;
            background = null; helmsman = null; helm = null; sails = null;
            water = null; navigator = null; waterIngress = null; TakingWater = false;
            voyageIslands.Clear(); islandLeaseAt = 0; voyageDestinationPinned = false; ResetDocking();
            if (boat != null && Mode != VoyageMode.Detached) Mode = RestingMode;
        }

        // Her mode without a sailor, by the booking's phase: moored at either berth, or attached and waiting for the sailor.
        private VoyageMode RestingMode => Record.phase == 0 ? VoyageMode.Waiting
            : Record.phase == 2 && (Job == null || !Job.onPaper) ? VoyageMode.Moored : VoyageMode.Attached;

        // Moored at the destination (VoyageDocking): the sailor goes, her locks stay until her storage.
        internal void EnterMoored()
        {
            ReleaseSailor(false);
            Mode = VoyageMode.Moored;
            Status = new TransportStatus { Phase = TransportPhase.Finished, Message = "Moored at the destination berth." };
        }

        // Lets go of everything: for a load, a new game, her storage, or a ship that is gone.
        internal void Detach(string reason)
        {
            if (boat == null && ownership == null) { Mode = VoyageMode.Detached; return; }
            ReleaseSailor(false);
            keptSails = null; keptSailsRig = null;
            try { ownership?.Dispose(); } catch (Exception e) { Log.Warn("Sailing: input cleanup: " + e.Message); }
            try { EndTowContacts(); } catch { }
            ownership = null; boat = null; rig = null;
            Mode = VoyageMode.Detached;
            Status = new TransportStatus { Phase = TransportPhase.Finished, Message = reason };
            Log.Info("Sailing: " + reason);
        }

        public void Dispose() => Detach("Voyage disposed.");

        // Decision 225 (17), decision 211's "aftmost or foremost sail by the helm she really needs": a spin toward the wind (rounding up)
        // is reefed at her aftmost sail (Balance(+1)), a spin away from it (bearing away against her helm) at her foremost (Balance(-1)).
        // `toWind`: the apparent wind's bearing off her bow (+ to starboard).
        internal static int SpinReefSide(double yawRate, double toWind) => Math.Sign(yawRate) == Math.Sign(toWind) ? 1 : -1;

        // Decision 225 (13): the turn lead (196) steers her only on a plain or close-hauled course, its heading on her side of the wind
        // (`tack`, TackNavigator.Side) no closer to it than close-hauled, and its way clear: it overrode her keeping clear and her side.
        internal static bool TurnLeadTakes(Manoeuvre state, int tack, double lead, double windFrom, double closeHauled, bool wayClear)
        {
            if (state != Manoeuvre.Direct && state != Manoeuvre.CloseHauled || !wayClear || !NavigationMath.Finite(lead)) return false;
            return Math.Abs(NavigationMath.Delta(windFrom, lead)) >= closeHauled && (tack == 0 || TackNavigator.Side(windFrom, lead) == tack);
        }

        // Decision 225 (16): a turn whose way is blocked is given up with way on (a refill given up with none left her stalled, run
        // 20261011-012651); a wear she chose, at any speed (decision 191: a wear only where its whole turn is clear).
        internal static bool GivesUpTurn(bool blocked, float forward, bool committedWear) => blocked && (forward > 1f || committedWear);
    }

    // Decision 225 (24): a fault in a voyage's tick while she waits or lies moored: logged when it is new (its message changed), and her
    // tick tried again RetrySeconds later (real time), not every physics step.
    internal sealed class TickFault
    {
        internal const float RetrySeconds = 5f;
        private string last;
        private double retryAt = double.NegativeInfinity;
        internal bool Waiting(double now) => now < retryAt;
        // True when this fault is to be logged.
        internal bool Note(string message, double now)
        {
            retryAt = now + RetrySeconds;
            if (message == last) return false;
            last = message;
            return true;
        }
    }

    // ==== VoyageNavigation ====

    // Adapted from the Ship Transport Prototype's TransportNavigation (ChatGPT "Astra"'s work for the user, handed over 2026-10-06;
    // decisions 92-93). Faults hold instead of letting go (D6); the destination berth is read from the live shipyard.

    internal sealed partial class Voyage
    {
        private NavigationWater water;
        private TackNavigator navigator;
        private Vector3 steeringTarget;
        private float navigationClock, departureSeconds, replanAt, giveWayUntil;
        // Decision 102: a boat in her way: her sails in to steerage, her course kept, full sail again once it is clear.
        internal bool GivingWay { get; private set; }
        private string navMessage;
        private GPButtonRopeWinch anchorWinch;
        internal Manoeuvre Manoeuvre => navigator?.State ?? Manoeuvre.Direct;

        // The route as saved, replanned from where she is when its next leg is no longer clear.
        private void PrepareNavigation(float noGo)
        {
            var windNow = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            float windFrom = (float)NavigationMath.Heading(-windNow.x, -windNow.z);
            var origin = Point(boat.StablePosition);
            if (!Job.berthKnown) TryResolveDestinationBerth();
            // Coming off a dock (decisions 110-111), the stage starts again from where she is and her route is checked at its end.
            unberthStage = UnberthStage.Read; towSeconds = 0; unberthEntries = 0; towOffs = 0; towWhole = false;
            if (Job.dockingPhase == 0 && !Job.unberthing && !water.Clear(origin, Job.route[Job.waypoint]))
            {
                var goal = water.ChartGoal(origin, NavigationDestination());
                bool restrained = boat.Anchor.IsSet() || boat.Moorings.AnyRopeMoored() || boat.Moorings.GetAnchorController().currentLength > 0.02f;
                var replacement = restrained && !Job.departureComplete
                    ? RoutePlanner.PlanDepartureStaged(origin, goal, Ahead(origin, boat.Root.eulerAngles.y, Math.Max(60, water.Length * 4)), water.Clear, windFrom, noGo)
                    : RoutePlanner.Plan(origin, goal, water.Clear, windFrom, noGo);
                // Still at her berth with no plan from there (launch 5, candidate N: the Cog never started, "Saved route is no
                // longer clear: Cube_002"): she comes off the dock by the unberthing stage (decisions 110-111, the tow if need be),
                // and her route is planned again from open water.
                if (replacement.Length < 2 && restrained && !Job.departureComplete)
                    Log.Info("Sailing: " + Name + ": no clear route out from her berth (" + water.Obstruction + "); she comes off the dock first.");
                // In her harbour's chart her guide is the chart's (decisions 157, 198), not her route: a coarse route not found from here
                // keeps the one she has (decision 204 c; runs 20261013-041904 and -042420, GC's retest: the Brig held once in Dragon
                // Cliffs' basin by Cube_002, then every try to sail again threw "Saved route is no longer clear" and she never sailed again).
                else if (replacement.Length < 2 && InHarbourChart)
                    Log.Info("Sailing: " + Name + ": her route's next leg isn't clear from here (" + water.Obstruction + ") and no coarse route is found; in her harbour's chart " +
                        "her chart's guide takes her on (decision 204 c).");
                // Still in her origin's harbour (decision 123's last resort; run 20261010-100003: the Cog held 105 m out, "Saved route is
                // no longer clear: Cube"): the tow takes her all the way out, and her route is planned again from open water.
                else if (replacement.Length < 2 && NearOriginBerth())
                {
                    Log.Info("Sailing: " + Name + ": no clear route from here in her origin's harbour (" + water.Obstruction + "); the tow takes her out.");
                    towOffs = MaximumTowOffs + 1; Job.unberthing = true; ++Job.revision;
                }
                else if (replacement.Length < 2) throw new InvalidOperationException("Saved route is no longer clear: " + water.Obstruction);
                else ReplaceRoute(replacement);
            }
            double learned = navigator?.LossPerDegree ?? keptTackLoss;
            navigator = new TackNavigator(); navigator.KeepLoss(learned); navigator.Restore(Job.tack, boat.Root.eulerAngles.y, ApparentFrom(), DeadZone);
            // Little way on (back from far travel, or a retry after a hold): time to set her sails and gather way.
            if (boat.Body && boat.Body.velocity.magnitude < 1f) navigator.GatherWay(45);
            navigationClock = 1; departureSeconds = 0; replanAt = 0; giveWayUntil = 0; GivingWay = false;
            // Decision 125: her route out chosen by the wind once she leaves her origin's harbour (again after a resume there).
            departureRouteChosen = !NearOriginHarbour(); routeWind = double.NaN; guideAt = 0;
            steeringTarget = Vector(Job.route[Job.waypoint]);
            Log.Info($"Sailing: {Name}: route {Job.route.Length} waypoints, {Job.length:F0} m, at waypoint {Job.waypoint}; hull radius={water.Radius:F1}, draft={water.Draft:F1}, length={water.Length:F1}.");
            if (Job.route.Length >= 2) Log.Info("Sailing: " + Name + "'s route: " + RouteReport(Job.route) + ".");
        }

        // A new route from where she is: her clock counts it from its start (the expected day moves with its length).
        private void ReplaceRoute(RoutePoint[] route)
        {
            Job.route = route; Job.waypoint = 1; ++Job.revision;
            Job.length = Math.Max(1, RouteMath.Length(route)); Job.done = 0; Job.clockHour = GameClock.Hours;
            if (route.Length >= 2) Log.Info("Sailing: " + Name + "'s route: " + RouteReport(route) + ".");
        }

        // Her route's first points (the user, 2026-10-11: "please check it's check point"), each as its bearing and distance from
        // the one before, from her first point; then its length.
        internal static string RouteReport(RoutePoint[] route, int most = 8)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 1; i < route.Length && i <= most; i++)
                parts.Add(i + ": " + NavigationMath.Heading(route[i].x - route[i - 1].x, route[i].z - route[i - 1].z).ToString("F0") + " degrees, " +
                    RoutePoint.Distance(route[i - 1], route[i]).ToString("F0") + " m");
            return (route.Length - 1) + " legs, " + RouteMath.Length(route).ToString("F0") + " m; " + string.Join("; ", parts.ToArray()) + (route.Length - 1 > most ? "; ..." : "");
        }

        // Where the route ends: before the berth is known, the destination port (NavigationWater.ChartGoal stops outside its
        // island while it isn't loaded); then the approach point along the berth's axis.
        private RoutePoint NavigationDestination() => !Job.berthKnown ? Job.destination
            : Ahead(Job.destination, Job.dockHeading + 180, ApproachDistance());

        // How far out along the berth's axis her approach point lies, astern of her as she will lie (Job.dockHeading): the farthest
        // clear one of 5 lengths (at least 100 m), 75 m, 50 m, 3 and 2 lengths (decision 146: farthest first), clear water with a clear
        // run for her hull to the berth (runs 20261011-022916 and -021619: at Fort Aestrin and Gold Rock City she held 2.2-2.3 km out,
        // "Destination approach obstructed", no route to a point 100 m off the berth that may lie on the quay or the shore); none
        // clear, further out (BerthApproach.Further). Looked at again every ApproachCheckSeconds, every ApproachRetrySeconds while
        // none is clear (the island's colliders may not be loaded yet: run 20261011-044001, Gold Rock City).
        // Decision 143 (the user, 2026-10-11: arriving, she may lie at the berth either way round): each look weighs the berth's other
        // end too, and she turns to it when it serves her better (BerthApproach.Turn: a clear approach point, the wind, her way in).
        private const float ApproachCheckSeconds = 30, ApproachRetrySeconds = 2;
        private float approachDistance = -1, approachCheckedAt = -1;
        private bool approachNoneLogged;

        private float ApproachDistance()
        {
            if (approachDistance > 0 && Time.time - approachCheckedAt < ApproachCheckSeconds) return approachDistance;
            approachCheckedAt = Time.time;
            float hersAt = ClearApproach(Job.dockHeading, out bool hersClear, out string hersWhy);
            var p = Point(boat.StablePosition);
            // The true wind against her dead zone turned to it (NoGo): the way in is a leg she isn't sailing yet (decision 136's rule for
            // routes; it was her apparent dead zone against the true wind, which could read an end she can't sail in from as sailable).
            double windFrom = WindFrom(), closeHauled = TackNavigator.BeatOff(NoGo, Margin);
            // Only on her way to the approach point: in the final approach she keeps the end she is taking; Kicia Bay's tow start has one
            // (decision 203: her bow toward its channel).
            if (Job.dockingPhase == 0 && !ArrivalOneWay)
            {
                double otherHeading = NavigationMath.Normalize(Job.dockHeading + 180);
                float otherAt = ClearApproach(otherHeading, out bool otherClear, out string otherWhy);
                var hers = new BerthApproach.End(hersClear, BerthApproach.WindRank(Job.dockHeading, windFrom, closeHauled),
                    RoutePoint.Distance(p, Ahead(Job.destination, Job.dockHeading + 180, hersAt)));
                var other = new BerthApproach.End(otherClear, BerthApproach.WindRank(otherHeading, windFrom, closeHauled),
                    RoutePoint.Distance(p, Ahead(Job.destination, otherHeading + 180, otherAt)));
                bool near = DistanceToTarget() < BerthApproach.NearFrom;
                if (BerthApproach.Turn(other, hers, near))
                {
                    var oldApproach = NavigationDestination0(hersAt);
                    Log.Info("Sailing: " + Name + " will lie the other way round at her berth (decision 143), " + WayRound(otherHeading) + ": from that end " +
                        EndReport(other, otherAt) + "; from the end she had " + EndReport(hers, hersAt) + (hersClear ? "" : " (" + hersWhy + ")") +
                        "; the wind from " + windFrom.ToString("F0") + ", close-hauled " + closeHauled.ToString("F0") + " off it.");
                    Job.dockHeading = (float)otherHeading; ++Job.revision;
                    hersAt = otherAt; hersClear = otherClear; hersWhy = otherWhy;
                    // On her way in to the old approach point: her way in is planned afresh from here (her route's last point reached
                    // at once, so NavigateTick plans to the new point on its fine grid, in two stages when far out).
                    if (Job.berthKnown && Job.route.Length > 0 && RoutePoint.Distance(Job.route[Job.route.Length - 1], oldApproach) <= 1)
                    { ReplaceRoute(new[] { p, p }); approachPlanAt = 0; approachObstructedLogged = false; }
                }
            }
            if (!hersClear)
            {
                // Decision 146: never a silent unchecked point. She holds off ("no route to her approach point") while none is clear.
                if (!approachNoneLogged)
                    Log.Info("Sailing: " + Name + ": no clear approach point at either end of her berth, out to " + BerthApproach.Further(HullLength).Last().ToString("F0") +
                        " m (" + hersWhy + "); she looks again every " + ApproachRetrySeconds.ToString("F0") + " s.");
                approachNoneLogged = true;
                approachCheckedAt = Time.time - ApproachCheckSeconds + ApproachRetrySeconds;
            }
            else approachNoneLogged = false;
            if (Math.Abs(hersAt - approachDistance) > 0.5f)
                Log.Info("Sailing: " + Name + "'s approach point lies " + hersAt.ToString("F0") + " m out along her berth, " + WayRound(Job.dockHeading) +
                    (hersClear ? "" : " (not clear: " + hersWhy + ")") + ".");
            approachDistance = hersAt;
            return hersAt;
        }

        // Her length for the approach distances: 1 m until her hull is measured (a length not yet read never throws there).
        private float HullLength => water != null && water.Length > 1 ? water.Length : 1f;

        // The approach point as it was before a turn (the distance given, along her heading then).
        private RoutePoint NavigationDestination0(float distance) => Ahead(Job.destination, Job.dockHeading + 180, distance);

        // Her approach point astern of the berth for this heading of hers there: the farthest clear of the usual distances, else the
        // nearest clear of the further ones (decision 146); none clear: the farthest usual one, `clear` false, and what blocks it.
        // Decision 201 (a) (the user, 2026-10-12 night; Gold Rock City's Dhow had her approach point where her bow and stern lay in cells
        // her hull doesn't fit, its own cell's clearance the only test, so no line could end there): once her destination's chart is
        // probed, a point counts only where her whole hull lies along her berth's way with her line's room at each of her hull's points
        // (LinePlanner's: twice her half-beam and her spare), and on a straight run-in of her length behind it (WaterField.HullRoom),
        // at the depth her chart's flood judges by.
        private float ClearApproach(double heading, out bool clear, out string why)
        {
            why = null;
            bool charted = chartArriving && chart != null && chartNext >= chart.Count;
            double length = Math.Max(10, water.Length), half = Math.Max(0.5, water.Radius);
            var axis = LinePlanner.Axis(length, half);
            double room = LinePlanner.FullRoom(length, half);
            var depth = charted ? DepthNow() : default(ChartDepth);
            foreach (double d in BerthApproach.Distances(HullLength).Concat(BerthApproach.Further(HullLength)))
            {
                var a = Ahead(Job.destination, heading + 180, d);
                if (charted && !chart.HullRoom(a, heading, axis, room, length, depth, out string tight))
                {
                    why = why ?? "at " + d.ToString("F0") + " m her hull or her run-in lacks her line's room (decision 201 a: " + tight + ")";
                    continue;
                }
                // The point clear water, and her run from it to the berth clear for her hull (run 20261011-041505: a Leopard lying across
                // Gold Rock City's way in, "no route to her approach point").
                if (water.Clear(a, a) && water.ClearHull(a, Job.destination)) { clear = true; return (float)d; }
                why = why ?? water.Obstruction;
            }
            clear = false;
            return (float)BerthApproach.Distances(HullLength)[0];
        }

        // Which way round she lies at the release spot with this heading (decision 203): as the game puts a ship there, or the other way;
        // at Kicia Bay's tow start, her bow toward its channel.
        private string WayRound(double heading)
        {
            if (ArrivalOneWay) return "her bow toward Kicia Bay's channel, at " + NavigationMath.Normalize(heading).ToString("F0");
            if (!arrivalSpotKnown) return "her bow at " + NavigationMath.Normalize(heading).ToString("F0");
            return Math.Abs(NavigationMath.Delta(arrivalYaw, heading)) < 90 ? "as the game puts a ship there" : "the other way round from how the game puts a ship there";
        }

        private static string EndReport(BerthApproach.End end, float at) =>
            (end.Clear ? "a clear approach point " + at.ToString("F0") + " m out" : "no clear approach point") +
            ", the wind " + (end.Wind == 0 ? "across her way in" : end.Wind == 1 ? "behind her (a run in)" : "too far ahead to sail in") +
            ", " + end.WayIn.ToString("F0") + " m from her";

        private bool NavigateTick(float dt)
        {
            EnsureVoyageScenes();
            if (GameState.loadingScenes > 0)
            {
                HoldNavigation(dt, Job.dockingPhase == 2 ? 0 : .15f);
                Status.Phase = TransportPhase.Suspended; Status.Message = "Waiting for island geometry to finish loading.";
                return false;
            }
            // Decision 157: her harbour charted before she leaves it (and again after a load while she is still in it).
            var at = Point(boat.StablePosition);
            seabedWait = false;
            if (!chartDone && !chartNone && !chartLeft && !chartArriving && (!Job.departureComplete || NearOriginHarbour()) && !ChartStep(at, at))
            { seabedWait = chartPaused; HoldNavigation(dt, 0); return false; }
            // Decision 198 (h): her destination charted from 2 km out, a slice a frame while her route takes her in.
            ArrivalChartStep(at);
            // Decision 238: past her anchor leg (after a load) or with none, near her berth, her chart not made for want of the seabed: she holds where she lies.
            if (Job.departureComplete && Job.dockingPhase == 0 && !Job.unberthing && Job.berthKnown && !AnchorLegOpen && !(chartArriving && (chartDone || chartNone)) &&
                DistanceToTarget() <= ArrivalChartFrom && !NearOriginHarbour() && !ChartSeabedOn(BerthIsland()))
            {
                seabedWait = true;
                HoldNavigation(dt, 0);
                Status.Message = "Waiting until the player is near enough to chart her harbour.";
                return false;
            }
            // Decision 198 (b, j): her line searched a slice a frame.
            LineStep();
            // Decision 198 (g): the tide watched; where her way closes for want of water she waits for it (moored, if she hasn't left).
            TideTick(at);
            if (TideHolds(dt, at)) return false;
            if (!Job.departureComplete)
            {
                departureSeconds += dt;
                if (departureSeconds > 120) { Hold("Anchor could not be recovered; departure waits."); return false; }
                // Recover the anchor against vanilla resistance before letting the moorings go.
                var rope = boat.Moorings.GetAnchorController();
                if (!rope) throw new InvalidOperationException("Anchor controller disappeared.");
                if (rope.currentLength > 0.005f || boat.Anchor.IsSet())
                {
                    HoldNavigation(dt, 0);
                    WinchDrive.Apply(rope, anchorWinch, 0, dt);
                    Status.Phase = TransportPhase.Preparing; Status.Message = "Recovering anchor through its winch (" + rope.currentLength.ToString("P0") + ").";
                    return false;
                }
                // Her straight way out (as far as the sailor looks ahead), checked just before her lines go. Blocked (a dock or a
                // boat ahead): she comes off by the unberthing stage below (decisions 110-111), which reads the berth while her
                // lines still hold her.
                var here = Point(boat.StablePosition);
                bool straightOut = water.Clear(here, Ahead(here, boat.Root.eulerAngles.y, Look(0)));
                // An onshore wind lays her over onto the dock once her sail is set (launch 5, run 20261007-080048: the Dhow heeled
                // against Dragon Cliffs' pier, her mast under its boardwalk): she comes off by the stage (the tow) unsailed.
                string pinned = PinnedAtBerth(here);
                // Her route lying off her bow where she can't turn to it alongside (her stern swings into the dock): she comes off the
                // dock first (run 20261011-120507, Dragon Cliffs to Chronos: her route's first point, the gap north, lay 49 degrees to
                // port; her way ahead was clear, so she set full sail and ran east before the wind past the turn she couldn't make).
                if (InHarbourChart) { harbourGuideAt = 0; UpdateHarbourGuide(here, WindFrom()); }
                var first = GuidePoint();
                double toRoute = NavigationMath.Heading(first.x - here.x, first.z - here.z), bowNow = boat.Root.eulerAngles.y;
                if (pinned == null && straightOut && RoutePoint.Distance(here, first) > 1 && Math.Abs(NavigationMath.Delta(bowNow, toRoute)) > RouteTurnFree &&
                    !water.TurnClear(here, bowNow, toRoute))
                    pinned = "her route lies " + NavigationMath.Delta(bowNow, toRoute).ToString("F0") + " degrees off her bow, a turn she can't make alongside (" + water.Obstruction + ")";
                Log.Info("Sailing: " + Name + " casts off: " + StuckReport());
                // Decision 225 (7): the stage marked before it starts, so a stage that ends at once (nothing alongside: FinishUnberthing,
                // its own "left her berth") stays ended; it was set again after it, and the stage ran twice.
                // Decision 220: at Kicia Bay always by the stage, which tows her out of its channel.
                bool towOut = TowOutPending, unberth = !straightOut || pinned != null || towOut;
                Job.departureComplete = true; Job.unberthing = unberth; ++Job.revision;
                if (unberth) BeginUnberthing(towOut ? "Kicia Bay's tow out of its channel (decision 220)" : !straightOut ? "her way ahead is blocked (" + water.Obstruction + ")" : pinned);
                boat.Moorings.UnmoorAllRopes();
                GameBindings.WinchInput.SetValue(anchorWinch, 0f);
                tickClock = 0.5f;
                Status.Phase = TransportPhase.Sailing;
                if (!unberth)
                {
                    sails.Plan((float)heel.Deployment, false);
                    Log.Info("Sailing: " + Name + " left her berth: anchor recovered, moorings released, full-sail target set.");
                }
            }
            if (Job.unberthing) { GivingWay = false; return UnberthTick(dt); }
            if (Job.dockingPhase > 0) { GivingWay = false; return NavigateDocking(dt); }
            navigationClock += dt;
            if (navigationClock < 0.5f) return true;
            float elapsed = navigationClock; navigationClock = 0;
            // Long paused frames do not count as manoeuvre time.
            elapsed = Mathf.Min(1, elapsed);
            var p = Point(boat.StablePosition);
            // Decision 155: backing her square sail, every half second until she is round.
            if (backing) return BackingTick(p);
            // Decision 157: in her harbour's chart her guide is the water distance's, not her route's.
            bool harbour = UpdateHarbourGuide(p, WindFrom());
            UpdatePort();
            // Decision 222: at anchor off her destination until her harbour is charted, then the anchor weighed; charted on her way in
            // before she anchors, she goes straight in (the user, 2026-11-04).
            if (Job.anchorStage == 0 && AnchorLegOpen && chartArriving && (chartDone || chartNone)) SkipAnchor();
            if (Job.anchorStage == 1 && AnchorLegOpen) return AnchorTick(dt);
            // Decision 198 (h): near her approach point in her destination's chart, at her line in's end: her final approach.
            if (harbour && Job.dockingPhase == 0 && ArrivalApproachReached(p))
            {
                Log.Info("Sailing: " + Name + " is at her approach point (" + RoutePoint.Distance(p, NavigationDestination()).ToString("F0") + " m off it" + (line != null ? ", her line in at " +
                    line[lineAt].Along.ToString("F0") + " of " + line[line.Count - 1].Along.ToString("F0") + " m" : "") + "): her final approach (decision 198 h).");
                line = null; linePlanner = null;
                return BeginFinalApproach(dt);
            }
            var waypoint = harbour ? harbourGuide : Job.route[Job.waypoint];
            // Her last point reached: within her usual radius; or within 200 m once she has run past the leg's end or the point is
            // only a chart goal short of the port (run 20261010-135332: at 3-4 m/s she passed the chart goal outside Dragon Cliffs
            // wide, "87148 of 87148 m", circled it never within 40 m, and sailed away from the port).
            bool lastReached = false;
            if (!harbour && Job.waypoint == Job.route.Length - 1)
            {
                double toLast = RoutePoint.Distance(p, waypoint);
                lastReached = toLast < Math.Max(40, water.Length * 1.5f) || toLast < 600 && Job.done >= Job.length - 1 ||
                    toLast < 200 && RoutePoint.Distance(waypoint, RouteEnd()) > 1 ||
                    // Decision 222: charted on her way in (SkipAnchor), a route still ending at her anchor spot is planned again at once.
                    Job.anchorKnown && !AnchorLegOpen && RoutePoint.Distance(waypoint, Job.anchor) < 1 && RoutePoint.Distance(waypoint, RouteEnd()) > 1;
            }
            // Already by her berth (run 20261011-010840: the approach point lay 100 m beyond it, and she circled 5-80 m off the berth for
            // minutes trying to reach it): inside the approach distance, within BerthTakeAlign of the berth's heading with the berth
            // ahead of her, and slow, she takes the berth from where she is (the final approach, NavigateDocking).
            if (!lastReached && Job.berthKnown && Job.dockingPhase == 0 && !AnchorLegOpen)
            {
                var fwd = Quaternion.Euler(0, Job.dockHeading, 0) * Vector3.forward;
                float ahead = Vector3.Dot(Vector(Job.destination) - boat.StablePosition, fwd);
                float speedNow = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
                // Not mid-turn, and slow (run 20261011-032545, Fort Aestrin: taken at 2.0 m/s in the middle of a wear, she ran past her berth).
                bool turningNow0 = navigator != null && (navigator.State == Manoeuvre.Tacking || navigator.State == Manoeuvre.Wearing);
                if (!turningNow0 && DistanceToTarget() < RoutePoint.Distance(NavigationDestination(), Job.destination) && ahead > 0 && speedNow < BerthTakeSpeed &&
                    Mathf.Abs(Mathf.DeltaAngle(boat.Root.eulerAngles.y, Job.dockHeading)) <= BerthTakeAlign)
                {
                    Log.Info("Sailing: " + Name + " takes her berth from here: " + DistanceToTarget().ToString("F0") + " m off it, " + speedNow.ToString("F1") +
                        " m/s, her heading " + Mathf.DeltaAngle(Job.dockHeading, boat.Root.eulerAngles.y).ToString("F0") + " degrees off the berth's.");
                    return BeginFinalApproach(dt);
                }
            }
            if (lastReached)
            {
                if (!Job.berthKnown && !TryResolveDestinationBerth())
                { HoldNavigation(dt, 0); Status.Message = "Waiting for the destination shipyard to load."; return false; }
                // Decision 222: her route ends at her anchor spot until she has anchored there, then at her approach point.
                var destination = RouteEnd();
                if (RoutePoint.Distance(waypoint, destination) > 1)
                {
                    var nextGoal = water.ChartGoal(p, destination);
                    if (RoutePoint.Distance(nextGoal, waypoint) < 1)
                    { HoldNavigation(dt, 0); Status.Message = "Waiting for destination island geometry."; return false; }
                    // Planned at most every ApproachPlanSeconds while it fails (it was every tick).
                    if (Time.time < approachPlanAt) { HoldNavigation(dt, 0); Status.Message = "Destination approach obstructed (" + water.Obstruction + "); retaining voyage."; return false; }
                    approachPlanAt = Time.time + ApproachPlanSeconds;
                    var nextWind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                    double approachWind = NavigationMath.Heading(-nextWind.x, -nextWind.z);
                    // Review finding (c), 2026-10-11: how long its route searches take (up to 33 in two stages, each up to 6,000 cells).
                    var planClock = System.Diagnostics.Stopwatch.StartNew();
                    var next = RoutePlanner.Plan(p, nextGoal, water.Clear, approachWind, NoGo, 6000, RoutePoint.Distance(p, nextGoal) < 600 ? 8 : 20);
                    // From far out, in two stages (run 20261011-050035 at Gold Rock City, "no route to her approach point 2171 m off": a 33 m
                    // grid can't see the harbour's narrow way in, and 2 km of an 8 m grid is too many cells): a staging point 150 or 300 m
                    // from the approach point, clear and with a fine route in, and the coarse route out at sea to it.
                    if (next.Length < 2 && RoutePoint.Distance(p, nextGoal) >= 600)
                        foreach (double r in new[] { 150.0, 300.0 })
                        {
                            for (int k = 0; k < 8 && next.Length < 2; k++)
                            {
                                var stage = Ahead(nextGoal, k * 45, r);
                                if (!water.Clear(stage, stage)) continue;
                                var inner = RoutePlanner.Plan(stage, nextGoal, water.Clear, approachWind, NoGo, 6000, 8);
                                if (inner.Length < 2) continue;
                                var outer = RoutePlanner.Plan(p, stage, water.Clear, approachWind, NoGo);
                                if (outer.Length < 2) continue;
                                var joined = new System.Collections.Generic.List<RoutePoint>(outer);
                                for (int i = 1; i < inner.Length; i++) joined.Add(inner[i]);
                                next = joined.ToArray();
                                Log.Info("Sailing: " + Name + ": her way in to her approach point through a staging point " + r.ToString("F0") + " m off it.");
                            }
                            if (next.Length >= 2) break;
                        }
                    if (next.Length < 2)
                    {
                        if (!approachObstructedLogged)
                            Log.Info("Sailing: " + Name + ": no route to her approach point " + RoutePoint.Distance(p, nextGoal).ToString("F0") + " m off (" + water.Obstruction + "; its route searches took " + planClock.ElapsedMilliseconds + " ms).");
                        approachObstructedLogged = true;
                        HoldNavigation(dt, 0); Status.Message = "Destination approach obstructed (" + water.Obstruction + "); retaining voyage."; return false;
                    }
                    approachObstructedLogged = false; approachPlanAt = 0;
                    ReplaceRoute(next); waypoint = next[1];
                }
                else if (AnchorLegOpen) return AnchorTick(dt);
                else return BeginFinalApproach(dt);
            }
            if (!harbour && RoutePoint.Distance(p, waypoint) < Math.Max(40, water.Length * 1.5f) && Job.waypoint < Job.route.Length - 1 && water.Clear(p, Job.route[Job.waypoint + 1]))
            { ++Job.waypoint; ++Job.revision; waypoint = Job.route[Job.waypoint]; }
            // Decision 127: her route is a guide; she aims at the farthest of its next points in clear water (corners cut).
            if (!harbour && UpdateGuide(p)) waypoint = Job.route[Job.waypoint];
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            if (wind.sqrMagnitude < 0.01f)
            { HoldNavigation(dt, 0); Status.Message = "Waiting for enough wind for steerage."; return false; }
            // Her routes on the true wind and her no-go turned to it; her course, tacks and gathering way on the apparent wind and her
            // dead-zone angle (decision 136).
            float windFrom = (float)NavigationMath.Heading(-wind.x, -wind.z);
            float noGo = NoGo;
            double apparentFrom = ApparentFrom(), deadZone = DeadZone;
            float speed = boat.Body.velocity.magnitude;
            float forward = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            // Her safety distance, looked nearer while nothing is clear that far (lookScale, below), never under her stopping distance.
            float stopFloor = Mathf.Max(water.Length, speed * StopSeconds);
            if (lookScale < 1f && Time.time >= lookScaleUntil) lookScale = 1f;
            float look = Mathf.Max(stopFloor, Look(speed) * lookScale);
            bool turningNow = navigator.State == Manoeuvre.Tacking || navigator.State == Manoeuvre.Wearing;
            // Decision 125: her routes chosen by the wind she has: at her departure (here when she needed no unberthing stage; else
            // EnterTurn), and after a wind shift of over RouteWindShift within RouteHarbour of her origin berth.
            if (harbour) departureRouteChosen = true;
            if (!departureRouteChosen)
            {
                departureRouteChosen = true;
                if (Time.time >= routeChoiceAt && ChooseRoute("her departure"))
                { waypoint = Job.route[Job.waypoint]; navigator.Restore(Job.tack, boat.Root.eulerAngles.y, apparentFrom, deadZone); }
            }
            else if (!harbour && !turningNow && !double.IsNaN(routeWind) && Time.time >= routeChoiceAt && NearOriginHarbour() &&
                Math.Abs(NavigationMath.Delta(routeWind, windFrom)) > RouteWindShift)
            {
                if (ChooseRoute("the wind shifted " + Math.Abs(NavigationMath.Delta(routeWind, windFrom)).ToString("F0") + " degrees in her origin's harbour"))
                { waypoint = Job.route[Job.waypoint]; navigator.Restore(Job.tack, boat.Root.eulerAngles.y, apparentFrom, deadZone); }
            }
            // Land and lying boats choose her course; moving traffic never does (decision 102): she gives way to it below.
            bool routeClear = water.Clear(p, waypoint);
            // Decision 162: in her harbour's chart, her guide out of a clear line from her is chosen again at once, not after its 2 s (run
            // 20261011-150902 on DY: coming up to Dragon Cliffs' narrow north gap off its middle, the line to her guide beyond the gap
            // clipped its bank; she kept clear on the fan of headings round it until none was left, and struck Cube at 3 m/s).
            if (harbour && !routeClear && Time.time >= harbourGuideRushAt)
            {
                harbourGuideAt = 0; harbourGuideRushAt = Time.time + 0.5f;
                var rush = System.Diagnostics.Stopwatch.StartNew();
                if (UpdateHarbourGuide(p, WindFrom())) { waypoint = harbourGuide; routeClear = water.Clear(p, waypoint); }
                // Its cost watched (the user, 2026-10-11 night: "if no performance problem, sure"): over 5 ms, at most every 30 s.
                if (rush.Elapsed.TotalMilliseconds > 5 && Time.time >= harbourGuideSlowAt)
                {
                    harbourGuideSlowAt = Time.time + 30;
                    Log.Info("Sailing: " + Name + "'s harbour guide chosen again at once took " + rush.Elapsed.TotalMilliseconds.ToString("F1") + " ms (decision 162 c).");
                }
            }
            bool direct = routeClear;
            if (!harbour && !routeClear && Time.time >= replanAt && !turningNow)
            {
                replanAt = Time.time + 8;
                var route = RoutePlanner.Plan(p, water.ChartGoal(p, NavigationDestination()), water.Clear, windFrom, noGo);
                if (route.Length >= 2) { ReplaceRoute(route); waypoint = route[1]; direct = water.Clear(p, waypoint); navigator.Rechoose("her route was planned again (" + water.Obstruction + ")"); }
            }
            double bearing = NavigationMath.Heading(waypoint.x - p.x, waypoint.z - p.z);
            // Decision 153: the limit she sails at, chosen from her set-up scan by her way made good toward her guide point (every
            // LimitSeconds), and each sail set or furled for it with the wind on her heading. Never in the middle of a tack, a wear,
            // irons or bearing away, and only a limit whose best heading lies on her side of the wind (run 20261011-114159: mid-tack
            // the Cog's limit went back to every sail, she lost her way at 0.5 m/s and struck Cube). A new limit chooses her course
            // again only when her course no longer fits it (her course is chosen again only on a wind shift or when it no longer holds).
            if (sails.HasLimits && Time.time >= limitAt && !turningNow && ironsSide == 0 && !easing)
            {
                limitAt = Time.time + LimitSeconds;
                // Only headings clear where she is count (the user, 2026-10-11: "you cannot pick best VMG inside port unless you consider
                // the terrain also"): her turn to it and her way along it, as her course's headings are judged.
                double headingThen = boat.Root.eulerAngles.y;
                int side = NavigationMath.Delta(apparentFrom, headingThen) < 0 ? -1 : 1;
                double bearingThen = bearing; var guideThen = waypoint;
                // Decision 162: in her harbour's chart a limit's heading counts when it leads HarbourLimitLengths of her lengths clear (when any limit's
                // does): run 20261011-145358 on DX, beating up Dragon Cliffs' channel on her jib, she took every sail for a heading 23
                // degrees lower that met the channel's bank within moments, and was hemmed in.
                Func<double, bool> far = null;
                if (InHarbourChart)
                    far = off =>
                    {
                        double h = NavigationMath.Normalize(apparentFrom + off);
                        return water.TurnClear(p, headingThen, h) && WayClear(p, h, Mathf.Max(look, HarbourLimitLengths * water.Length), bearingThen, guideThen);
                    };
                string chose = sails.ChooseLimit(NavigationMath.Delta(apparentFrom, bearing), off =>
                {
                    double h = NavigationMath.Normalize(apparentFrom + off);
                    return water.TurnClear(p, headingThen, h) && WayClear(p, h, look, bearingThen, guideThen);
                }, Time.time < limitHeldUntil, side, far);
                if (chose != null)
                {
                    limitHeldUntil = Time.time + LimitHold;
                    Log.Info("Sailing: " + Name + " " + chose + " (decision 153).");
                    deadZone = DeadZone;
                    if (Math.Abs(NavigationMath.Delta(apparentFrom, navigator.CourseHeading)) < TackNavigator.BeatOff(deadZone, Margin))
                        navigator.Rechoose("the angle her sails can sail at changed");
                }
            }
            string setChange = sails.UpdateSet(NavigationMath.Delta(apparentFrom, boat.Root.eulerAngles.y));
            if (setChange != null) Log.Info("Sailing: " + Name + ": " + setChange + " (decision 153).");
            var oldManoeuvre = navigator.State;
            // Decision 133: her sails draw when the apparent wind is outside her least angle to it.
            navigator.SailsDrawing = Math.Abs(NavigationMath.Delta(apparentFrom, boat.Root.eulerAngles.y)) >= RigMinimum;
            navigator.BearingAway = easing;
            // Decision 209 (d): on her line, straight at her guide on it comes first.
            bool onLineNow = harbour && line != null;
            if (onLineNow != navigator.OnLine) { navigator.OnLine = onLineNow; if (onLineNow) navigator.Rechoose("she is on her line (decision 209 d)"); }
            // A heading counts as clear when her turn to it where she lies is (her hull's swing, the seabed under her bow and stern:
            // the user, 2026-10-11) and her way along it is.
            double headingNow = boat.Root.eulerAngles.y;
            double demand = navigator.Tick(headingNow, bearing, apparentFrom, deadZone, forward, elapsed, direct, h => water.TurnClear(p, headingNow, h) && WayClear(p, h, look, bearing, waypoint));
            if (navigator.LossLearned != null)
                Log.Info("Sailing: " + Name + "'s tack lost " + navigator.LossLearned + " (decision 161: the speed she needs to tack).");
            if (navigator.TackAborted != null)
                Log.Info("Sailing: " + Name + " gives up her tack (decision 133: " + navigator.TackAborted + "); back on her old tack to gather way.");
            if (navigator.Chose)
                Log.Info("Sailing: " + Name + " chooses her course (" + navigator.Choice + "); the apparent wind from " + apparentFrom.ToString("F0") + " (her dead zone " +
                    deadZone.ToString("F0") + ", close-hauled " + TackNavigator.BeatOff(deadZone, Margin).ToString("F0") + " off it), the true wind from " + windFrom.ToString("F0") + " at " +
                    wind.magnitude.ToString("F1") + " m/s, " + forward.ToString("F2") + " m/s ahead, heading " + boat.Root.eulerAngles.y.ToString("F0") +
                    ", her guide point (waypoint " + Job.waypoint + ") " + RoutePoint.Distance(p, waypoint).ToString("F0") + " m off at " + bearing.ToString("F0") + ".");
            if (navigator.GatherBegan)
                Log.Info("Sailing: " + Name + " gathers way on heading " + navigator.GatherHeading.ToString("F0") + " (decision 131: her sail draws there, the apparent wind " +
                    Math.Abs(NavigationMath.Delta(apparentFrom, navigator.GatherHeading)).ToString("F0") + " degrees off; " + forward.ToString("F2") + " m/s ahead; her guide point " +
                    NavigationMath.Delta(boat.Root.eulerAngles.y, bearing).ToString("F0") + " degrees off her bow waits).");
            if (navigator.GatherEnded != null)
                Log.Info("Sailing: " + Name + " ends gathering way: " + navigator.GatherEnded + "; heading " + boat.Root.eulerAngles.y.ToString("F0") + ", " + forward.ToString("F2") + " m/s ahead.");
            Job.tack = navigator.Tack; Job.deployment = (float)heel.Deployment;
            navMessage = $"Waypoint {Job.waypoint}/{Job.route.Length - 1} · {navigator.State}: {navigator.Reason}";
            Status.Message = navMessage;
            // At Info, with how she lies (run 20261010-125554: outside Dragon Cliffs she came back on a beam reach at 2.5 m/s and
            // still held head to wind; the turns between were not in the log).
            if (oldManoeuvre != navigator.State) TraceFor(30);
            if (oldManoeuvre != navigator.State)
                Log.Info("Sailing: " + Name + ": " + navMessage + " (heading " + boat.Root.eulerAngles.y.ToString("F0") + ", the apparent wind " +
                    NavigationMath.Delta(boat.Root.eulerAngles.y, apparentFrom).ToString("F0") + " degrees off her bow, " + forward.ToString("F2") + " m/s ahead, to her waypoint " +
                    NavigationMath.Delta(boat.Root.eulerAngles.y, bearing).ToString("F0") + " degrees off, demand " + demand.ToString("F0") + ")");
            // Decision 160 (the user, 2026-10-11: "do both"): in her harbour's chart, too slow to tack, a square rig backs round where she
            // lies instead of wearing (run 20261011-140306: at 2.5 m/s the Cog wore 230 degrees down the wind, away from Dragon Cliffs'
            // upwind exit, into a moored Junk's way, and ran on down the east channel onto Cube_005). Her bow turns across the wind onto her
            // new tack, as the tack she is too slow for would (DV; DU's box-haul away from the wind wore her forward down it). She wears as
            // before when she can't back.
            if (navigator.WearBegan && InHarbourChart && !navigator.CanTack(forward, boat.Root.eulerAngles.y, apparentFrom) &&
                TryBacking("decision 160: in her harbour, too slow to tack (" + forward.ToString("F1") + " m/s), instead of a wear: " + navigator.Reason, p, bearing, apparentFrom, deadZone, look, true))
                return true;
            // Decision 169: blocked with way on and her heading's way blocked within her stopping distance, she doesn't hold that heading
            // while she looks nearer (run 20261011-173145 on EK: at 3.1 m/s her guide fell to 8 m off in her no-go, nothing made way,
            // and she held heading 354 onto Cube at 3.1 m/s): hemmed in now.
            if (navigator.State == Manoeuvre.Blocked && forward > TurnLateSpeed && !water.Clear(p, Ahead(p, boat.Root.eulerAngles.y, stopFloor)))
            {
                string fast = navigator.Reason + " Her way ahead blocked within her stopping distance, " + stopFloor.ToString("F0") + " m at " + forward.ToString("F1") +
                    " m/s (decision 169): " + water.Obstruction;
                if (TryBacking(fast, p, bearing, apparentFrom, deadZone, look)) return true;
                HemmedIn(fast); return false;
            }
            if (navigator.State == Manoeuvre.Blocked && navigator.NoWay && look > stopFloor + 0.5f)
            {
                // Nothing clear and sailable within her safety distance (run 20261011-014629: Dragon Cliffs' narrow inner harbour, her
                // guide point 42 m off, every heading blocked within 65 m): she looks nearer, half as far, down to her stopping distance,
                // for LookNearerSeconds, before her routes are chosen again or she is hemmed in.
                lookScale = Mathf.Max(0.01f, lookScale * 0.5f); lookScaleUntil = Time.time + LookNearerSeconds;
                Log.Info("Sailing: " + Name + " finds nothing clear within " + look.ToString("F0") + " m (" + water.Obstruction + "); she looks nearer, " +
                    Mathf.Max(stopFloor, Look(speed) * lookScale).ToString("F0") + " m.");
                navigator.Restore(Job.tack, boat.Root.eulerAngles.y, apparentFrom, deadZone);
                return true;
            }
            if (navigator.State == Manoeuvre.Blocked)
            {
                // Decision 125: no clear, sailable course makes way toward her guide point: her routes are chosen again (at most once
                // a minute); only when none serves her better, hemmed in (near her origin berth the unberthing stage and its short
                // tow, decision 123; elsewhere a hold, D6).
                if (!harbour && navigator.NoWay && Time.time >= routeChoiceAt && ChooseRoute("no course makes way toward her guide point", true))
                { navigator.Restore(Job.tack, boat.Root.eulerAngles.y, apparentFrom, deadZone); return true; }
                // Decision 155: a square rig backs round where she lies before the old way.
                string hemmed = navigator.Reason + " " + water.Obstruction;
                if (TryBacking(hemmed, p, bearing, apparentFrom, deadZone, look)) return true;
                HemmedIn(hemmed); return false;
            }
            // Keeping clear with no way toward her guide point (the user, 2026-10-11: she steers clear rather than holding): her routes
            // are chosen again meanwhile, at most once a minute.
            if (!harbour && navigator.NoWay && Time.time >= routeChoiceAt && ChooseRoute("keeping clear, no course makes way toward her guide point", true))
            { waypoint = Job.route[Job.waypoint]; navigator.Rechoose("her route was chosen again"); }
            // What lies ahead (the user, 2026-10-11: "you should not reef when you detect obstacle, instead change course to avoid it";
            // "consider your own speed, then have a safety distance according to that"): her heading's way blocked within her safety
            // distance (Look: SafetySeconds of her speed) makes the navigator choose a clear heading (its own check of her demand's
            // way, and here her heading's: she may still be turning to it). Only with both her heading's and her demand's way
            // blocked within her stopping distance is she hemmed in.
            float stop = Mathf.Max(water.Length, speed * StopSeconds);
            // Decision 164: astern or sideways, her actual motion checked (VoyageDrift.cs).
            string drifting = DriftCheck();
            if (drifting != null) { HemmedIn(drifting); return false; }
            // Her course, not her helm's demand: coming up toward the wind the demand is led 15 degrees ahead of her bow (decision 131)
            // and may point at what she turns away from (run 20261011-005455: chosen again every tick).
            double course = turningNow ? demand : navigator.CourseHeading;
            if (!WayClear(p, boat.Root.eulerAngles.y, look, bearing, waypoint))
            {
                // Her helm hard over toward her clear course while her heading's way stays blocked (Steer; the user, 2026-10-11: "grc test
                // hit the leopard boat parking in port", run 20261011-105743: the Sanbuq saw it 127 m ahead at 4.4 m/s and turned 1-2 degrees
                // a second, the helm at 0.4-0.75, into it at 4.3 m/s).
                avoidUntil = Time.time + AvoidHold;
                // Waiting to tack or gathering way, her helm keeps her side or her heading (its demand): the course across the wind is no turn she makes yet (EO, run
                // 20261011-183945: "her turn to 292 (179 degrees at 1.1 m/s)" hemmed the Dhow in five times while she waited to tack).
                // In a tack or a wear (begun this tick too), not at all: a wear's arc has its own check (decision 172), a tack runs up
                // into the wind (EQ, run 20261011-191718: a wear begun that tick read as a plain 119-degree turn and hemmed the Dhow in).
                bool inTurn = navigator.State == Manoeuvre.Tacking || navigator.State == Manoeuvre.Wearing;
                string late = inTurn ? null : TurnTooLate(p, forward, navigator.WaitingToTack || navigator.Gathering ? demand : course);
                // Decision 198 (e): on her line, her way is its arcs: hemmed in only when they are blocked too.
                if (late != null || !water.Clear(p, Ahead(p, boat.Root.eulerAngles.y, stop)) && !water.Clear(p, Ahead(p, course, stop)) && !(line != null && LineAheadClear(stop)))
                {
                    string tight = late ?? "Insufficient manoeuvring clearance: " + water.Obstruction;
                    if (TryBacking(tight, p, bearing, apparentFrom, deadZone, look)) return true;
                    HemmedIn(tight); return false;
                }
                if (!WayClear(p, course, look, bearing, waypoint) && !turningNow)
                {
                    if (!aheadLogged) Log.Info("Sailing: " + Name + " has something ahead within " + look.ToString("F0") + " m (" + water.Obstruction + "); she keeps clear of it.");
                    aheadLogged = true;
                    navigator.Rechoose("her way ahead closes (" + water.Obstruction + ")");
                }
            }
            else aheadLogged = false;
            // A turn whose way is blocked is given up where her bow is and her course chosen again (she held instead: "Downwind
            // recovery turn is obstructed", runs 20261011-002639 and -003228).
            // With way on only: with none (her refill, bearing away to fill her sails after losing way) she hardly moves, and giving
            // it up left her stalled, choosing again every tick by Dragon Cliffs' terrain (run 20261011-012651); her stopping distance
            // still guards her (HemmedIn above).
            // A tack's helm is led just across the wind's eye (decision 167): its way is her new course's.
            double turnWay = navigator.State == Manoeuvre.Tacking ? navigator.CourseHeading : demand;
            // Decision 172: a wear's way is its arc round her turning circle, not a straight line `look` along her helm's lead.
            bool turnBlocked = navigator.State == Manoeuvre.Wearing ? !WearArcClear(p, forward, demand) :
                navigator.State == Manoeuvre.Tacking && (!TackArcClear(p, forward, apparentFrom) || !water.Clear(p, Ahead(p, turnWay, look)));
            if (GivesUpTurn(turnBlocked, forward, navigator.CommittedWear))
            {
                Log.Info("Sailing: " + Name + " gives up her " + (navigator.State == Manoeuvre.Wearing ? "wear" : "tack") + ": its way is blocked (" + water.Obstruction + "); she keeps clear.");
                bool wasWearing = navigator.State == Manoeuvre.Wearing;
                navigator.Interrupt("her turn's way is blocked (" + water.Obstruction + ")");
                // Decision 155: no room to wear and too slow to tack: a square rig backs round where she lies.
                if (wasWearing && !navigator.CanTack(forward, boat.Root.eulerAngles.y, apparentFrom) && TryBacking("her wear's way is blocked and she is too slow to tack (" + forward.ToString("F1") + " m/s)", p, bearing, apparentFrom, deadZone, look))
                    return true;
            }
            // Decision 102: a boat predicted across her way (on her velocity, or her intended course): she gives way, her sails in
            // to steerage (Steer), her course kept; full sail again 3 s after it is clear. No hold, no furl.
            if (!water.VelocityTrafficClear(p) || !water.MovingClear(p, Ahead(p, demand, look)))
            {
                if (!GivingWay) Log.Info("Sailing: " + Name + " gives way: " + water.Obstruction + ".");
                giveWayUntil = Time.time + 3;
            }
            GivingWay = Time.time < giveWayUntil;
            if (GivingWay) Status.Message = "Giving way: " + water.Obstruction;
            // Her course's heading (at her guide point, or held off it while she builds speed; close-hauled; in a turn).
            // Decision 196, the turn lead: within the lead of a turn coming in narrow water she steers for her way's heading round it, her
            // helm at its quick rate (avoidUntil), not in a tack or a wear.
            // Decision 198 (c): not on her line (its arcs round the turn). Decision 225 (13): only on a plain or close-hauled course, its
            // heading on her side of the wind and its way clear (TurnLeadTakes).
            if (line == null && NavigationMath.Finite(narrowLead) && Time.time - narrowLeadAt < 2 && !backing &&
                TurnLeadTakes(navigator.State, navigator.Tack, narrowLead, apparentFrom, TackNavigator.BeatOff(deadZone, Margin), WayClear(p, narrowLead, look, bearing, waypoint)))
            { demand = narrowLead; avoidUntil = Time.time + AvoidHold; }
            steeringTarget = Vector(Ahead(p, demand, Math.Max(120, look * 2)));
            return true;
        }

        // Decision 169 (Claude, 2026-10-11 night, a bug against decision 140's stopping distance): a turn at speed carries her on along
        // her heading about her turning circle's radius (MyTurnRadius; her speed over 5 degrees a second until decision 198 (a)) times
        // the sine of the turn (a quarter circle at most); her course is a way out only when her heading's way is clear that far (run
        // 20261011-173145 on EK: turning 48 degrees to port at 3.1 m/s, 6 degrees a second, toward a clear course, she ran onto Cube 7 m
        // off her bow). The reason when it isn't (TurnTooLate).
        // Only from this speed (EQ/ER, runs 20261011-191709 and -193101: at 1.0-1.4 m/s it hemmed the Dhow in a dozen times, where a
        // touch is gentle and she can luff to a stop; the strikes it was made for came at 3.1 m/s and more).
        internal const float TurnLateSpeed = 1.5f;
        // Decision 172 (Claude, 2026-10-11 night, under "fix along your way"; for the user to confirm): a wear's way checked along the
        // arc she sails, round her turning circle (MyTurnRadius, decision 198 (a)), every WearArcStep
        // of the turn left to her course in the wear's direction (at most WearArcMost), not a straight line her look-ahead long along
        // her helm's lead (run 20261011-182329 on EN: the Dhow at 1.8 m/s in Dragon Cliffs' basin, a 20 m turning circle, gave up her
        // wear on a 45 m line into Terrain; hemmed in, the long tow took her).
        internal const double WearArcStep = 45, WearArcMost = 200;
        // The turn left from `heading` to `course` going `sign` (+1 clockwise) round, 0-360.
        internal static double TurnLeft(double heading, double course, int sign) { double a = ((course - heading) * sign) % 360; return a < 0 ? a + 360 : a; }
        private bool WearArcClear(RoutePoint p, float forward, double demand)
        {
            double heading = boat.Root.eulerAngles.y;
            int sign = NavigationMath.Delta(heading, demand) < 0 ? -1 : 1;
            double left = Math.Min(WearArcMost, TurnLeft(heading, navigator.CourseHeading, sign));
            if (left < 1) left = Math.Abs(NavigationMath.Delta(heading, demand));
            return TurnArcClear(p, forward, heading, sign, left);
        }

        // A tack's way is its arc too: up through the wind's eye to her new course (run 20261012-070702 on FI: the Brig's tack from 107
        // degrees off the wind swung her 62 degrees to port before she stopped, her bow onto Cube at 2.5 m/s; only the line along her
        // new course was checked).
        private bool TackArcClear(RoutePoint p, float forward, double windFrom)
        {
            double heading = boat.Root.eulerAngles.y;
            int sign = NavigationMath.Delta(heading, windFrom) < 0 ? -1 : 1;
            return TurnArcClear(p, forward, heading, sign, Math.Min(WearArcMost, TurnLeft(heading, navigator.CourseHeading, sign)));
        }

        // Her turn's arc from `heading`, `left` degrees going `sign` (+1 clockwise), round her turning circle, every WearArcStep.
        private bool TurnArcClear(RoutePoint p, float forward, double heading, int sign, double left)
        {
            if (left < 1) return water.Clear(p, Ahead(p, heading, water.Length));
            // Decision 198 (a): her measured circle when she has one.
            double radius = MyTurnRadius();
            var from = p;
            for (double phi = Math.Min(WearArcStep, left); ; phi = Math.Min(phi + WearArcStep, left))
            {
                double r = phi * Math.PI / 180;
                var at = Ahead(Ahead(p, heading, radius * Math.Sin(r)), heading + 90, sign * radius * (1 - Math.Cos(r)));
                if (!water.Clear(from, at)) return false;
                from = at;
                if (phi >= left) return true;
            }
        }

        // Her turn's arc checked, not her heading's straight line (EL, run 20261011-175017: an 8-degree turn in Dragon Cliffs' east gate
        // at 3.3 m/s, Cube 8 m off her port side, read her heading's line blocked within 5 m and called the long tow): halfway round
        // and at its end (a quarter circle at most).
        private string TurnTooLate(RoutePoint p, float forward, double course)
        {
            double heading = boat.Root.eulerAngles.y, turn = NavigationMath.Delta(heading, course);
            // Decision 198 (a): round her measured circle when she has one (decision 169's 5 degrees a second before).
            double radius = MyTurnRadius(), most = Math.Min(90, Math.Abs(turn)) * Math.PI / 180;
            float advance = (float)(radius * Math.Sin(most));
            if (forward <= TurnLateSpeed || advance < 1f) return null;
            var from = p;
            foreach (double fraction in new[] { 0.5, 1.0 })
            {
                double ahead = radius * Math.Sin(most * fraction), aside = Math.Sign(turn) * radius * (1 - Math.Cos(most * fraction));
                var at = Ahead(Ahead(p, heading, (float)ahead), heading + 90, (float)aside);
                if (!water.Clear(from, at))
                    return "Her turn to " + course.ToString("F0") + " (" + Math.Abs(turn).ToString("F0") + " degrees at " + forward.ToString("F1") + " m/s) carries her " +
                        advance.ToString("F0") + " m on along her heading, and her turn's way is blocked (decision 169): " + water.Obstruction;
                from = at;
            }
            return null;
        }

        // Decision 127: her guide point, the farthest of her next route points (within GuideReach along her route, at most
        // GuidePoints of them) in clear water from where she is; the points before it count as passed. Every 2 s; true when it
        // moved on. Decision 131 ("distance the checkpoint further apart"): 5 km and 10 points (were 3 km and 6), so a tack runs
        // longer toward a farther point.
        private const double GuideReach = 5000;
        private const int GuidePoints = 10;
        private float guideAt;

        // Her way `look` metres along heading `h`, clear (her hull's sweep and the seabed, NavigationWater.Clear). Toward her guide
        // point (within GuideTolerance of its bearing) and nearer than `look`, along her route: to the guide point, then on along her
        // next legs for the rest (run 20261011-114159: leaving Dragon Cliffs for Chronos at 4.5 m/s, a look-ahead of 130 m ran
        // straight past the corners of her 89-100 m harbour legs into the shore; every course read as blocked and the headings she
        // kept clear on walked from 59 to 164 degrees, her helm hard over one way, then the other).
        internal const double GuideTolerance = 5;
        private bool WayClear(RoutePoint p, double h, float look, double bearing, RoutePoint guide)
        {
            double toGuide = RoutePoint.Distance(p, guide);
            // Decision 198 (e): on her line her guide is close ahead and she turns along it, her heading leading its bearing.
            if (toGuide < 1 || toGuide >= look || Math.Abs(NavigationMath.Delta(h, bearing)) > (line != null ? LineGuideTolerance : GuideTolerance)) return water.Clear(p, Ahead(p, h, look));
            if (!water.Clear(p, guide)) return false;
            double left = look - toGuide;
            var from = guide;
            // On beyond it: in her harbour's chart down its water distance (decision 157), else along her route.
            var onward = new System.Collections.Generic.List<RoutePoint>();
            bool chart = InHarbourChart;
            if (chart) onward.AddRange(harbourOnward);
            else for (int i = Job.waypoint + 1; i < Job.route.Length; i++) onward.Add(Job.route[i]);
            foreach (var next in onward)
            {
                if (left <= 0.5) break;
                var to = next;
                double leg = RoutePoint.Distance(from, to);
                if (leg < 0.01) continue;
                if (leg > left) to = Ahead(from, NavigationMath.Heading(to.x - from.x, to.z - from.z), left);
                // In her harbour's chart, cell to cell as the chart judged them (decision 162: NavigationWater.ClearBeam).
                if (!(chart ? water.ClearBeam(from, to) : water.Clear(from, to))) return false;
                left -= Math.Min(leg, left);
                from = to;
            }
            return true;
        }

        private bool UpdateGuide(RoutePoint p)
        {
            if (Time.time < guideAt) return false;
            guideAt = Time.time + 2;
            int farthest = Job.waypoint;
            double along = RoutePoint.Distance(p, Job.route[Job.waypoint]);
            for (int i = Job.waypoint + 1; i < Job.route.Length && i <= Job.waypoint + GuidePoints; i++)
            {
                along += RoutePoint.Distance(Job.route[i - 1], Job.route[i]);
                if (along > GuideReach) break;
                farthest = i;
            }
            for (int i = farthest; i > Job.waypoint; i--)
                if (water.Clear(p, Job.route[i])) { Job.waypoint = i; ++Job.revision; return true; }
            return false;
        }

        // Decision 125 (the user, 2026-10-10): her route and up to 2 others from here to her goal, through offings RouteOffing off
        // in other directions (toward her goal, turned 60 and 120 degrees either way, those she can sail to straight first), each
        // scored by the time it would take with the wind now (RoutePlanner.SailingTime: legs in her no-go as tacks, never a straight
        // leg into the wind); the best is taken (ReplaceRoute) when it beats hers by a tenth. `hersFailed` (no course makes way on
        // her route): hers is left out and the best of the others taken. True when her route was replaced. At most once a minute:
        // each planning costs about 0.1 s on the main thread (Voyages.PlanJob, 100-108 ms in the runs).
        internal const double RouteOffing = 1200, RouteHarbour = 1500, RouteWindShift = 45;
        private float routeChoiceAt;
        private double routeWind = double.NaN;
        private bool departureRouteChosen;

        private bool NearOriginHarbour()
        {
            var berth = OriginBerth();
            return Job.dockingPhase == 0 && berth && HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) <= RouteHarbour;
        }

        private bool ChooseRoute(string why, bool hersFailed = false)
        {
            routeChoiceAt = Time.time + 60;
            var p = Point(boat.StablePosition);
            double windFrom = WindFrom();
            float noGo = NoGo;
            routeWind = windFrom;
            RoutePoint goal;
            try { goal = water.ChartGoal(p, NavigationDestination()); }
            catch (Exception e) { Log.Info("Sailing: " + Name + ": no routes to choose from (" + why + "): " + e.Message); return false; }
            var names = new System.Collections.Generic.List<string>();
            var routes = new System.Collections.Generic.List<RoutePoint[]>();
            int hers = -1;
            if (!hersFailed && Job.waypoint < Job.route.Length && water.Clear(p, Job.route[Job.waypoint]))
            {
                var rest = new RoutePoint[Job.route.Length - Job.waypoint + 1];
                rest[0] = p;
                Array.Copy(Job.route, Job.waypoint, rest, 1, Job.route.Length - Job.waypoint);
                hers = routes.Count; names.Add("hers"); routes.Add(rest);
            }
            // Hers re-planned by the wind she has, from here to the checkpoint after her next one, then on as it was (the user,
            // 2026-10-11, run 20261011-105718: the wind veered and the Cog's first short leg at Dragon Cliffs, laid at the booking,
            // fell inside her close-hauled; her route, through the harbour's gate, was dropped for a detour the other way round, "for
            // cog its wind is okay for it to sail to exit"). It counts as hers: another is taken only when a tenth faster.
            int hersAgain = -1;
            if (Job.waypoint < Job.route.Length)
            {
                int rejoin = Math.Min(Job.waypoint + 1, Job.route.Length - 1);
                var target = Job.route[rejoin];
                var leg = RoutePlanner.Plan(p, target, water.Clear, windFrom, noGo, 6000, RoutePoint.Distance(p, target) < 600 ? 8 : 20);
                if (leg.Length >= 2)
                {
                    var joined = new System.Collections.Generic.List<RoutePoint>(leg);
                    for (int i = rejoin + 1; i < Job.route.Length; i++) joined.Add(Job.route[i]);
                    hersAgain = routes.Count; names.Add("hers re-planned by the wind to her checkpoint " + rejoin); routes.Add(joined.ToArray());
                }
            }
            double toGoal = NavigationMath.Heading(goal.x - p.x, goal.z - p.z);
            int others = 0, plannings = 0;
            foreach (double turn in new[] { 0.0, 60, -60, 120, -120 }.OrderBy(t => Unberthing.Sailable(toGoal + t, windFrom, noGo) ? 0 : 1))
            {
                if (others >= 2 || plannings >= 3) break;
                var offing = Ahead(p, toGoal + turn, RouteOffing);
                if (!water.Clear(offing, offing)) continue;
                plannings++;
                var route = RoutePlanner.PlanVia(p, offing, goal, water.Clear, windFrom, noGo);
                if (route.Length < 2) continue;
                others++; names.Add("via the offing at " + NavigationMath.Normalize(toGoal + turn).ToString("F0")); routes.Add(route);
            }
            if (routes.Count == 0) { Log.Info("Sailing: " + Name + ": no route from here to choose (" + why + "): " + water.Obstruction); return false; }
            var times = routes.Select(r => RoutePlanner.SailingTime(r, windFrom, noGo)).ToArray();
            int best = 0;
            for (int i = 1; i < times.Length; i++) if (times[i] < times[best]) best = i;
            // Hers, or hers re-planned when hers can't be followed from here; another only when a tenth faster.
            int keep = hers >= 0 ? hers : hersAgain;
            int take = keep < 0 || times[best] < times[keep] * 0.9 ? best : keep;
            Log.Info("Sailing: " + Name + " chooses her route (" + why + "; the wind from " + windFrom.ToString("F0") + "): " +
                string.Join("; ", names.Select((n, i) => n + " " + RouteMath.Length(routes[i]).ToString("F0") + " m, time " + times[i].ToString("F0")).ToArray()) +
                "; " + (take == hers ? "she keeps hers" : "taken: " + names[take]) + ".");
            if (take == hers) return false;
            ReplaceRoute(routes[take]);
            return true;
        }

        // ---- Leaving the berth (decisions 110-111, 123, 181; SAILING_TRANSPORT_DRAFT.md "Leaving the berth") ----
        // TowOff: alongside, pushed off the dock with her heading held (Unberthing.PushOff: her length; decisions 181, 192), then
        // her sailor takes her. TowOut: the tow by the bow where no clear water lies beside her, until she is clear; or
        // the backup tow (decisions 123, 165). Read: the stage starts again (after a resume or a hold).
        // TowOutChannel: Kicia Bay's tow out of its channel to its tow start (decision 220; VoyageTowIn.cs).
        private enum UnberthStage { Read, TowOff, TowOut, TowOutChannel }
        private UnberthStage unberthStage;
        private RoutePoint unberthStart;
        private int dockSide;
        private double dockBearing, escapeHeading = double.NaN, offGap;
        private float stageSeconds, towSeconds, unberthProbeAt, displaced, escapeCheckAt;
        private string closingWhy;
        private bool wayOut, outwardClear = true;
        private RoutePoint[] towPath;
        private int towIndex;
        private int unberthEntries;
        internal const float NearOrigin = 300;
        private const int MaximumUnberthings = 4;

        // A fault that hems her in (no room to turn, no clear tack, an obstructed wear). Within 300 m of her origin berth, still
        // leaving it, she comes off the dock again by the stage (decisions 110-111: her sails first, from the second time the
        // tow at once) instead of holding; elsewhere, or after 4 times since her sailor started, she holds (D6).
        // Within 300 m of her origin berth, leaving it, the player near (far off, LeaveUnberthingFar ends the stage at once: a
        // hold instead, which the clock carries on).
        private bool NearOriginBerth()
        {
            var berth = OriginBerth();
            return Job.dockingPhase == 0 && berth && HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) <= NearOrigin &&
                (boat.PlayerAboard || boat.ObserverDistance <= DistanceHandover.SailWithin);
        }

        private void HemmedIn(string reason)
        {
            // Decision 198 (b): a check failed on her line: planned again from where she lies.
            if (line != null) ReplanLine("she was hemmed in");
            // Or hemmed in within a minute of the tow letting her go, wherever she is: her way out isn't done (run 20261010-132249's
            // S5: the whole tow left the Cog 222 m out, a square rig with the wind astern, her wear blocked by the island's rock,
            // past the 300 m; she held). The whole tow takes her on from there.
            bool fresh = Job.dockingPhase == 0 && Time.time - unberthFinishedAt < FreshAfterTow && (boat.PlayerAboard || boat.ObserverDistance <= DistanceHandover.SailWithin);
            if (unberthEntries >= MaximumUnberthings || !(NearOriginBerth() || fresh)) { Hold(reason); return; }
            unberthEntries++;
            // Hemmed in again is her own way out failing too (decision 123's count; run 20261010-100003: the Cog, towed clear in a
            // dead onshore wind, went through every re-entry and held, the whole tow never reached): pushed off again, and after
            // MaximumTowOffs of them the backup tow (decision 181). Once the backup tow has had her, the count is past it, so it
            // takes her again.
            // Decision 184 (b): no longer the backup tow at once when hemmed in within FreshAfterTow of leaving the stage (added for
            // run 20261010-112726, when the stage ended only with her on a clear heading): since decision 181 it ends 5 m off the
            // dock, where that sent every first hemming-in straight to the backup tow, against 181's "pushed off again; after 2".
            towOffs++;
            // Decision 195: the way she failed by closed in her harbour chart, so she tries another.
            CloseFailedWay(Point(boat.StablePosition));
            Job.unberthing = true; ++Job.revision;
            BeginUnberthing(reason, unberthEntries > 1);
        }

        // Decision 133: her rig's least angle to the apparent wind (SailControlAdapters.UpwindLimit), read once her sails are set
        // up (ReadUpwindLimit); before that the old no-go (75 for square sails, else 60).
        private double rigMinimum = double.NaN;
        // Decision 153: the limit chosen for her sails when there is one (SailControlAdapters.Limit).
        private double RigMinimum => sails != null && NavigationMath.Finite(sails.Limit) ? sails.Limit : double.IsNaN(rigMinimum) ? (rig.Sails.Any(s => s.squareSail) ? 75 : 60) : rigMinimum;
        // Decision 177 (the user, 2026-10-11 night, to sail out of port faster): in port (in her harbour's chart, or within RouteHarbour of
        // her origin or destination berth) she beats right at her dead zone, at sea 5 degrees outside it (her dead zone where her sails
        // catch 0.5 of the wind, at sea and in port since decision 214). Her close-hauled's margin beyond her dead zone.
        private bool InPort => sails != null && sails.InPort;
        private double Margin => InPort ? TackNavigator.PortCloseMargin : TackNavigator.CloseMargin;
        private bool PortNow()
        {
            var berth = OriginBerth();
            double fromOrigin = Job.dockingPhase == 0 && berth ? HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) : double.PositiveInfinity;
            // Decision 198 (h): in port still from RouteHarbour of her berth, not from her destination's chart (2 km).
            return UpwindLimits.InPort(InPort, InOriginChart, Job.dockingPhase > 0, fromOrigin, DistanceToTarget(), RouteHarbour);
        }
        // Set each navigation tick (and at her scan): the sails' set, her navigator's margin; a change logged and her course chosen again.
        private void UpdatePort()
        {
            if (sails == null) return;
            bool now = PortNow();
            // Decision 198 (c): 180 (her heading kept rather than a big turn) not on her line.
            if (!sails.SetInPort(now)) { if (navigator != null) { navigator.Margin = Margin; navigator.NoWear = now; navigator.KeepHeading = now && line == null; } return; }
            if (navigator != null) { navigator.Margin = Margin; navigator.NoWear = now; navigator.KeepHeading = now && line == null; navigator.Rechoose(now ? "she is in port (decision 177)" : "she is out of port (decision 177)"); }
            Log.Info("Sailing: " + Name + (now ? " is in port" : " is out of port") + ": her dead zone " + DeadZone.ToString("F0") + " degrees, close-hauled " +
                TackNavigator.BeatOff(DeadZone, Margin).ToString("F0") + " (decision 177: " + (now ? "in port she beats at her dead zone" : "at sea she beats " +
                TackNavigator.CloseMargin.ToString("F0") + " degrees outside it") + ").");
        }
        private float limitAt, limitHeldUntil;
        // A limit taken is kept at least LimitHold, unless it has no clear heading (SailControlAdapters.ChooseLimit's `held`).
        private const float LimitSeconds = 5, LimitHold = 60;
        // Decision 162 (b), as the user answered it (2026-10-11 night: "150m is too close for small boat, it should be depend on ship
        // size"; "cog is the small boat, 10 times is too much, make it 3 times"; "okay change b to 5 times"): in her harbour's chart, a limit's heading must lead this
        // many of her lengths clear (her safety distance when longer).
        private const float HarbourLimitLengths = 5;

        private void ReadUpwindLimit()
        {
            try
            {
                rigMinimum = sails.UpwindLimit(out string report);
                Log.Info("Sailing: " + Name + "'s least angle to the apparent wind is " + rigMinimum.ToString("F0") + " degrees (decisions 133 and 214: each sail where it receives " +
                    UpwindLimits.WindShare.ToString("F1") + " of the wind, at sea and in port; " + report + ").");
                UpdatePort();
            }
            catch (Exception e) { rigMinimum = double.NaN; Log.Warn("Sailing: " + Name + "'s upwind limit couldn't be read (" + e.Message + "); the old no-go is used."); }
        }

        // Decision 136 (the user, 2026-10-10 night: "all things should base on apparent wind"): near the player her course, tacks,
        // gathering way and departure headings are judged on the apparent wind (ApparentFrom) against her rig's dead-zone angle as
        // it is, kept within the navigator's limits (a rig whose limit reads 90 or more still gets a course).
        private double DeadZone => NavigationMath.Clamp(RigMinimum, 10, 89);
        // Decision 160: her turn off the dock judged on the least dead zone of her sail sets (decision 153 sets her sails for the heading
        // once she sails), not the sails set as she comes off it (run 20261011-143723 on DW: with every sail set, the square sail's 70
        // degrees put every heading toward Dragon Cliffs' exit in her no-go, and she turned east; a moment later she sailed on her jib at 29).
        private double EscapeDeadZone => sails != null && NavigationMath.Finite(sails.LeastLimit) ? NavigationMath.Clamp(sails.LeastLimit, 10, 89) : DeadZone;

        // Her routes' no-go off the true wind: her dead-zone angle turned to the true wind at her speed ahead now, in the wind now.
        // Route planning (RoutePlanner) keeps it: a route's legs run in many directions, and the apparent wind on a leg she isn't
        // sailing is the true wind and her speed along that leg, which is what turning her dead-zone angle to the true wind reckons.
        private float NoGo
        {
            get
            {
                var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                float ahead = boat != null && boat.Body ? Vector3.Dot(boat.Body.velocity, boat.Root.forward) : 0f;
                return (float)NavigationMath.Clamp(UpwindLimits.TrueAngle(RigMinimum, wind.magnitude, ahead), 10, 89);
            }
        }

        // The apparent wind as her sails feel it (Sail.GetApparentWind): the wind less her velocity; the bearing it comes from (the
        // true wind's when the two cancel, so the bearing is never read from nothing).
        private double ApparentFrom()
        {
            var apparent = Vector3.ProjectOnPlane(Wind.currentWind - boat.Body.velocity, Vector3.up);
            return apparent.sqrMagnitude < 0.01f ? WindFrom() : NavigationMath.Heading(-apparent.x, -apparent.z);
        }
        // The sailor's own look ahead (NavigateTick's `look`): her way out is clear when it is, so she never holds on leaving the
        // stage. Launch 5's first run: the Dhow's 21.5 m departure corridor was clear of Dragon Cliffs' dock, this wasn't.
        // Her safety distance (the user, 2026-10-11: "consider your own speed, then have a safety distance according to that"):
        // SafetySeconds of her speed, the room to turn away from what lies ahead (the Dhow turns 3-5 degrees a second), at least 3
        // of her lengths and 45 m; it was 20 s, and at 4 m/s she held 32 m off Dragon Cliffs' shoal and ran onto it. Within
        // StopSeconds of her speed (at least her length) she can no longer turn clear of it.
        internal const float SafetySeconds = 30, StopSeconds = 8;
        // Taking her berth from where she is (NavigateTick): her heading within this of the berth's, under this speed.
        private const float BerthTakeAlign = 30, BerthTakeSpeed = 1.5f;
        private bool aheadLogged, approachObstructedLogged;
        private float approachPlanAt;
        private const float ApproachPlanSeconds = 10;
        // Her safety distance looked nearer (NavigateTick) while nothing is clear that far: a share of it, back to all of it after
        // LookNearerSeconds.
        private float lookScale = 1f, lookScaleUntil;
        // Her heading's way blocked within her look-ahead (NavigateTick): the helm hard over toward her course until AvoidHold after it clears.
        private float avoidUntil;
        private const float AvoidHold = 1.5f;
        // Decision 179: the turn rate wanted while she keeps clear of something ahead (degrees a second).
        private const double AvoidTurnRate = 10;
        private const float LookNearerSeconds = 10f;
        private float Look(float speed) => Mathf.Max(45, Mathf.Max(water.Length * 3, speed * SafetySeconds));
        // A heading off the dock: clear for her safety distance at the speed she has (it was 45 m whatever her speed: run
        // 20261011-114159, at 3.6 m/s each heading she turned to was found blocked 2 s later), along her route toward her guide point.
        // Decision 160 (the user, 2026-10-11: "do both"): judged over her turn's look-ahead (TurnLook), not her safety distance: at 3.2
        // m/s that was 97 m, and coming off Dragon Cliffs' dock every heading toward the exit read as blocked, so she turned the other
        // way, down the wind, away from it (run 20261011-140252, and DQ's starboard swing). The sailor's own look-ahead takes over
        // once she sails, nearer when nothing is clear that far.
        private bool EscapeClear(RoutePoint p, double candidate) => WayAlongRoute(p, candidate, TurnLook(boat.Body ? boat.Body.velocity.magnitude : 0f));
        // Her turn off the dock and her settling on its heading: at least 45 m and three of her lengths, her stopping distance when longer.
        private float TurnLook(float speed) => Mathf.Max(45, Mathf.Max(water.Length * 3, speed * StopSeconds));
        private bool WayAlongRoute(RoutePoint p, double h, float look)
        {
            var guide = GuidePoint();
            return WayClear(p, h, look, NavigationMath.Heading(guide.x - p.x, guide.z - p.z), guide);
        }
        // Her route off her bow by more than this at her departure is checked for her turn alongside.
        private const double RouteTurnFree = 30;
        private static double WindFrom()
        {
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            return NavigationMath.Heading(-wind.x, -wind.z);
        }

        // Reads the berth (her lines still hold her): the dock's side (the berth's cleats, else the water each side); and chooses
        // how she comes off.
        private void BeginUnberthing(string because, bool towNow = false)
        {
            var p = Point(boat.StablePosition);
            double h = boat.Root.eulerAngles.y;
            // The berth's cleats tell the dock's side only while she still lies at the berth (launch 5, candidate M: hemmed in
            // 100 m on and turned, she was pulled toward the dock she had left, for 5 minutes); elsewhere the water each side does.
            var berth = OriginBerth();
            bool atBerth = berth && HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) < water.Length;
            var cleats = atBerth ? berth.Cleats.Select(c => Point(FloatingOriginManager.instance.ShiftingPosToRealPos(c.transform.position))).ToArray() : new RoutePoint[0];
            int side = Unberthing.DockSide(h, p, cleats);
            string sideFrom = "the berth's cleats";
            bool alongside = true;
            if (side == 0)
            {
                float probe = 2 * water.Radius + 3;
                bool starboard = water.ClearHull(p, Ahead(p, h + 90, probe)), port = water.ClearHull(p, Ahead(p, h - 90, probe));
                sideFrom = "the water each side";
                if (starboard && port) { alongside = false; side = 1; }
                else side = starboard ? -1 : 1;
            }
            dockSide = side; dockBearing = h + side * 90; unberthStart = p; displaced = 0; towPath = null; towStuck = 0; backing = false;
            // Her coming off the dock traced (run 20261011-124724: sailing off to port, she swung to starboard, 75 to 132 degrees).
            TraceFor(90);
            closingWhy = null; wayOut = false; stageSeconds = 0; unberthProbeAt = 0; heading.Reset();
            towWhole = towOffs > MaximumTowOffs;
            escapeHeading = double.NaN;
            // Decision 181 (the user, 2026-10-11 night: "drop turn stage, but when unberth still tow her away from pier like 5m (like how
            // a player will push the boat away from pier)"): alongside, she is pushed off the dock, her heading held, and her sailor takes
            // her from there (decision 123's one of her lengths, its sailing off and its turn stage gone): 10 m whatever the wind
            // (decision 192: her own length, Unberthing.PushOff).
            // Decision 198 (i): her length where it fits, else to the middle of the waterway. Decision 219: at the shipyard's own cleats
            // (217) only back onto the release spot's line; lying within PushLeast of it, nothing is pushed and her sailor takes her.
            double push;
            string pushHow;
            if (alongside && atBerth && berth.AtShipyard && ReadOriginSpot(out var spot, out double spotYaw))
            {
                push = Unberthing.PushToSpot(p, h - side * 90, spot, spotYaw, water.Length);
                pushHow = "back onto the release spot, decision 219";
                if (push < PushLeast) { alongside = false; sideFrom += ", already by the release spot's line (decision 219)"; }
            }
            else push = PushDistance(p, h, h - side * 90, out pushHow);
            offGap = towClearGap = push;
            Log.Info("Sailing: " + Name + " unberths (" + because + "): " + (alongside ? "pushed " + push.ToString("F1") + " m off (" + pushHow + "), her heading held (decisions 181, 192, 198 i)" : "her sailor takes her where she lies")
                + "; the dock on her " + (side > 0 ? "starboard" : "port")
                + " side by " + sideFrom + (alongside ? "" : " (nothing alongside)") + "; heading " + h.ToString("F0") + ". " + StuckReport());
            // Decision 220: at Kicia Bay the tow takes her out of its channel before her sailor has her: after the push back onto the spot
            // where she lies at the cleats (219; FinishUnberthing hands her to the tow out), else from where she lies (a restart after a save
            // or a hold); never the backup tow while it is to come.
            if (TowOutPending)
            {
                if (alongside && atBerth) StartTow("the push before Kicia Bay's tow out", towNow);
                else BeginTowOut(because);
                return;
            }
            if (towWhole) { TowWhole("her own way out failed " + towOffs + " times"); return; }
            // Decision 194 (the user, 2026-10-12, seeing the Brig towed round by the bow after her push: "shouldn't unberth only push brig
            // out?"; then, asked what may help her once she sails: "push off dock again only"): no tow but the push off the dock and the
            // backup tow. Nothing alongside, her sailor takes her from where she lies, whatever failed before (it was the tow by the bow,
            // runs 20261012-121825 and -121906 on FL: "is towed (her own way out failed already)"; FM's push astern is gone too).
            if (!alongside) { FinishUnberthing(true); return; }
            StartTow("her own way out failed already", towNow);
        }

        // Decision 225 (14): her final approach (203) ends a tack or a wear under way first: kept, Steer's dead-zone guard and decision 211
        // stayed off for the whole approach.
        private bool BeginFinalApproach(float dt)
        {
            if (navigator != null && (navigator.State == Manoeuvre.Tacking || navigator.State == Manoeuvre.Wearing)) navigator.Interrupt("her final approach begins");
            Job.dockingPhase = 1; ++Job.revision; return NavigateDocking(dt);
        }

        // Once off the dock, where she turns: toward her next waypoint when her way along it is clear (and `usable`), so the
        // sailor needn't turn her across the dock afterwards; else the first heading away from the dock.
        private double Escape(RoutePoint p, double h, Func<double, bool> usable)
        {
            var waypoint = GuidePoint();
            bool toPoint = RoutePoint.Distance(p, waypoint) > 1;
            double toWaypoint = toPoint ? NavigationMath.Heading(waypoint.x - p.x, waypoint.z - p.z) : h;
            if (toPoint && usable(toWaypoint)) return toWaypoint;
            double away = Unberthing.EscapeHeading(h, dockSide, usable);
            // Hemmed in away from the berth, the clear water may lie on either side.
            return double.IsNaN(away) ? Unberthing.EscapeHeading(h, -dockSide, usable) : away;
        }

        // Decision 181 (fix (d), run 20261011-205735: pulled off bodily at her middle in a 15-19 m/s onshore wind, the Dhow swung from
        // 49 to 222 degrees): pushed off with her heading held, her bow and her stern each brought to `bearing` at `speed`, each end's
        // strength adapting as the tow's does (Unberthing.TowStrength), half her mass at each.
        private void PushOff(double bearing, double speed)
        {
            if (boat.Body.isKinematic) return;
            towPulledSeconds += Time.fixedDeltaTime;
            var target = Vector(Ahead(new RoutePoint(0, 0), bearing, speed));
            PushEnd(water.ForeExtent - water.Radius, target, speed, ref towDrawFore);
            PushEnd(-(water.AftExtent - water.Radius), target, speed, ref towDrawAft);
        }

        private void PushEnd(float along, Vector3 target, double speed, ref double strength)
        {
            var world = boat.Root.TransformPoint(new Vector3(0, 0, along));
            var v = boat.Body.GetPointVelocity(world);
            double made = speed > 1e-6 ? (v.x * target.x + v.z * target.z) / speed : 0;
            AdaptTow(ref strength, made, speed, "pushing her off at an end");
            Unberthing.Tow(v.x, v.z, target.x, target.z, out var ax, out var az, strength);
            world.y = boat.Body.worldCenterOfMass.y;
            var force = new Vector3((float)ax, 0, (float)az) * boat.Body.mass * 0.5f;
            boat.Body.AddForceAtPosition(force, world);
            NoteTowForce(force);
        }

        private bool UnberthTick(float dt)
        {
            if (unberthStage == UnberthStage.Read) BeginUnberthing("a restart while coming off the dock");
            stageSeconds += dt;
            Trace();
            bool towing = unberthStage == UnberthStage.TowOff || unberthStage == UnberthStage.TowOut;
            // The metres she is towed (the departure lab and S4 read it: decision 123's tow is one of her lengths off the dock).
            if (towing)
            {
                var at = boat.StablePosition;
                if (towLastSet) TowedMetres += HorizontalDistance(at, towLast);
                towLast = at; towLastSet = true;
            }
            else towLastSet = false;
            // Along her route the tow may go 1.5 km: its time is 15 minutes.
            if (towing && (towSeconds += dt) > (towRouteNext > 0 ? 900 : Unberthing.TowBudget))
            {
                Log.Info("Sailing: " + Name + ": the tow's 5 minutes are out (the pull " + towStrength.ToString("F1") + " m/s², up to " + towStrengthMost.ToString("F1") + "): " + StuckReport() + "; " + TowContactReport());
                EndTowContacts(); Hold("The tow could not take her off the dock in 5 minutes: " + water.Obstruction); return false;
            }
            if (Time.time >= unberthProbeAt) { unberthProbeAt = Time.time + .25f; ProbeUnberthing(); }
            switch (unberthStage)
            {
                case UnberthStage.TowOutChannel:
                    TowOutTick(dt);
                    break;
                case UnberthStage.TowOff:
                    // Off far enough (decisions 181, 192: Unberthing.PushOff): her sailor takes her (decision 123). (The backup tow never
                    // comes here: BeginUnberthing hands it to TowWhole before any push.)
                    if (displaced >= towClearGap && closingWhy == null) { FinishUnberthing(true); break; }
                    // Decision 194: no clear water further out: the push ends where it is and her sailor takes her (it was the tow by the bow).
                    if (!outwardClear) { Log.Info("Sailing: " + Name + ": no clear water further out after " + displaced.ToString("F1") + " m of the push (decision 194): her sailor takes her here."); FinishUnberthing(true); break; }
                    // No headway sideways (launch 5, candidate P: aground on the island's terrain, 5 minutes for nothing): under
                    // 0.5 m in 30 s, the tow takes her out by the bow instead.
                    if (stageSeconds >= towMarkAt + 30)
                    {
                        if (displaced < towMark + 0.5f) { Log.Info("Sailing: " + Name + ": no headway in 30 s of the push (decision 194: her sailor takes her here): " + StuckReport()); FinishUnberthing(true); break; }
                        towMark = displaced; towMarkAt = stageSeconds;
                    }
                    helm.Demand = 0; helm.Apply(); UpdateSailSafety(dt, 0);
                    TowHeadway(displaced);
                    PushOff(dockBearing + 180, Unberthing.TowSpeed);
                    Status.Message = "Pushed off the dock: " + displaced.ToString("F1") + " of " + offGap.ToString("F1") + " m.";
                    break;
                case UnberthStage.TowOut:
                    // The backup tow only (TowWhole; decision 194 took out the tow by the bow off the dock, which ended here once she was
                    // clear of it). Decision 165: it tows her only to a fair spot, then lets her go to sail on.
                    if (towWhole && closingWhy == null && Time.time >= fairCheckAt)
                    {
                        fairCheckAt = Time.time + FairCheckEvery;
                        if (FairSpot(out string fair))
                        {
                            Log.Info("Sailing: " + Name + ": the backup tow lets her go where she can sail (decision 165): " + fair + ".");
                            FinishUnberthing(); break;
                        }
                    }
                    if (towPath != null)
                    {
                        // Along the planned way as a line (D17): toward its point a length ahead of her nearest one, which brings
                        // her back onto it when the wind sets her off; done at its end, out of the harbour.
                        var at = Point(boat.StablePosition);
                        if (Unberthing.Pursue(towPath, ref towIndex, at, Math.Max(15, water.Length), out towNearest, out towTarget, out towOffWay))
                        {
                            // Towed along her route: she sails on from the waypoint after the tow's end.
                            if (towRouteNext > 0) { Job.waypoint = Math.Max(Job.waypoint, towRouteNext); ++Job.revision; }
                            FinishUnberthing(); break;
                        }
                        towPursuing = true;
                        escapeHeading = NavigationMath.Heading(towTarget.x - at.x, towTarget.z - at.z);
                    }
                    else if (wayOut) { FinishUnberthing(); break; }
                    UpdateSailSafety(dt, 0); SteerTo(escapeHeading, dt);
                    double outMade = RoutePoint.Distance(Point(boat.StablePosition), towOutStart);
                    TowHeadway(outMade);
                    // Slow while still by the dock (the user, 2026-10-09: candidate AU's stronger tow struck east_dock (72) at 0.89
                    // m/s): the tow's own speed until she is two lengths (at least 30 m) out, then the planned way's.
                    bool byDock = outMade < Math.Max(30, 2 * water.Length);
                    Pull(escapeHeading, !towAtMiddle, towPath != null && !byDock ? Unberthing.TowOutSpeed : Unberthing.TowSpeed);
                    if (towPath != null && towPursuing) HoldOnWay(Point(boat.StablePosition), towNearest, towOffWay);
                    // Towed along her route, the way made is her route's (run 20261010-153436's S5: 286 m towed, 8 m counted).
                    if (towRouteNext > 0) Job.done = Math.Max(Job.done, Math.Min(Job.length, RouteMath.Project(Job.route, Point(boat.StablePosition), Job.waypoint)));
                    Status.Message = towPath != null ? "Towed out along a clear way: leg " + (towIndex + 1) + " of " + (towPath.Length - 1) + ", " + towOffWay.ToString("F1") + " m off it." : "Towed out to clear water.";
                    break;
            }
            return false;
        }

        // Every quarter second: how far she is off the dock, whether she closes on it or on what lies ahead, and whether her way
        // ahead is clear.
        private void ProbeUnberthing()
        {
            var p = Point(boat.StablePosition);
            float h = boat.Root.eulerAngles.y;
            var outward = Vector(Ahead(new RoutePoint(0, 0), dockBearing + 180, 1));
            displaced = Vector3.Dot(Vector(p) - Vector(unberthStart), outward);
            closingWhy = Closing(p, h);
            outwardClear = water.ClearHull(p, Ahead(p, dockBearing + 180, Math.Max(3, Math.Max(offGap, towClearGap) - displaced)));
            float speed = boat.Body.velocity.magnitude;
            // On the heading she turns to (within 20 degrees), with her turn's look-ahead clear along it (decision 160).
            wayOut = (double.IsNaN(escapeHeading) || Math.Abs(NavigationMath.Delta(h, escapeHeading)) < 20) && WayAlongRoute(p, h, TurnLook(speed));
            // That heading no longer clear from where she is now (launch 5, candidate N: towed 5 minutes toward 58 degrees with
            // "Cube" in the way): every 2 s another is chosen, for sailing (Turn) or for the tow (TowOut).
            // Along a planned tow, at most every 10 s: the way ahead of her nearest point on it blocked since (a boat), or she
            // has left its wide corridor after TowPulledBeforeReplan of being pulled along it (the user, 2026-10-10; not while
            // she still turns onto it): planned again from here. Her own line to the way is not judged (D17, run
            // 20261007-171636: off the way by the wind, that line crossed the cliff, and the replans narrowed onto it).
            if (towPath != null && towPursuing && unberthStage == UnberthStage.TowOut && Time.time >= escapeCheckAt &&
                (towOffWay > Unberthing.TowClearance(water.Radius, water.Length) - water.Radius && towPulledSeconds >= TowPulledBeforeReplan ||
                 !water.Clear(towNearest, towTarget)))
            {
                escapeCheckAt = Time.time + 10;
                string why = towOffWay > Unberthing.TowClearance(water.Radius, water.Length) - water.Radius ? "she is " + towOffWay.ToString("F1") + " m off it" : "blocked (" + water.Obstruction + ")";
                towPath = PlanTowOut(p); towIndex = 0; ResetPursuit(p);
                Log.Info("Sailing: " + Name + ": the tow's way: " + why + "; " + (towPath != null ? "planned again." : "a clear heading instead."));
            }
            if (!wayOut && Time.time >= escapeCheckAt && unberthStage == UnberthStage.TowOut && towPath == null &&
                !double.IsNaN(escapeHeading) && !EscapeClear(p, escapeHeading))
            {
                escapeCheckAt = Time.time + 2;
                double old = escapeHeading;
                string blockedBy = water.Obstruction;
                double next = Escape(p, h, c => EscapeClear(p, c));
                if (!double.IsNaN(next) && Math.Abs(NavigationMath.Delta(next, old)) > 1)
                {
                    escapeHeading = next;
                    Log.Info("Sailing: " + Name + ": the way to " + old.ToString("F0") + " is blocked (" + blockedBy + "); she is towed toward " + next.ToString("F0") + " instead.");
                }
            }
        }

        // Closing on the dock (at her bow, middle or stern) or on what lies right ahead faster than ContactSpeed, within 3 m of it.
        private string Closing(RoutePoint p, float h)
        {
            if (boat.Body.isKinematic) return null;
            var toDock = Vector(Ahead(new RoutePoint(0, 0), dockBearing, 1));
            foreach (float along in new[] { water.ForeExtent - water.Radius, 0f, -(water.AftExtent - water.Radius) })
            {
                var world = boat.Root.TransformPoint(new Vector3(0, 0, along));
                var at = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(world));
                float closing = Vector3.Dot(boat.Body.GetPointVelocity(world), toDock);
                if (closing > Unberthing.ContactSpeed && !water.ClearHull(at, Ahead(at, dockBearing, 3)))
                    return "closing on the dock at " + closing.ToString("F2") + " m/s (" + water.Obstruction + ")";
            }
            float forward = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            if (forward > Unberthing.ContactSpeed && !water.ClearHull(p, Ahead(p, h, water.ForeExtent - water.Radius + 3)))
                return "closing on what lies ahead at " + forward.ToString("F2") + " m/s (" + water.Obstruction + ")";
            return null;
        }

        // The harbour's tow (decision 111): when her own sails and rudder can't take her off.
        private float towMark, towMarkAt, towProgressAt;
        private double towStuck, towProgressFrom = double.NaN;
        private bool towAtMiddle;
        private RoutePoint towOutStart;
        // Following the tow's way (Unberthing.Pursue): her nearest point on it, the point she is towed toward, how far off it
        // she is; `towPursuing` once they are set for the current way.
        private RoutePoint towNearest, towTarget;
        private double towOffWay;
        private bool towPursuing;

        private void ResetPursuit(RoutePoint p) { towNearest = towTarget = p; towOffWay = 0; towPursuing = false; towPulledSeconds = 0; }

        // The tow's turn (Unberthing.TurnController), and how long she has been pulled along its line since the way was planned
        // (the user, 2026-10-10: being off the way plans it again only after TowPulledBeforeReplan of pulling; before, the
        // check came every 10 s while she was still turning onto the new way, and she drifted off it each time, run
        // 20261010-024308).
        private readonly Unberthing.TurnController towTurn = new Unberthing.TurnController();
        private double towPulledSeconds;
        internal const double TowPulledBeforeReplan = 10;

        // The tow's strength (m/s², Unberthing.TowStrength), its highest in this tow, and the next strength worth a log line
        // (2, 4, 8, 16 m/s²).
        private double towStrength = Unberthing.TowStrengthStart, towStrengthMost = Unberthing.TowStrengthStart, towStrengthNote = 2;

        private void ResetTowStrength() { towStrength = Unberthing.TowStrengthStart; towStrengthMost = towStrength; towStrengthNote = 2; towTurn.Reset(); towForceMax = towForceAcross = 0; }

        // What her hull touches while towed (TowContacts), reported when the tow gains nothing and when its time runs out.
        private TowContacts towContacts;

        private void WatchTowContacts()
        {
            if (towContacts) return;
            towContacts = boat.Body.GetComponent<TowContacts>();
            if (!towContacts) towContacts = boat.Body.gameObject.AddComponent<TowContacts>();
        }

        private string TowContactReport() => "touching her: " + (towContacts ? towContacts.Report(5) : "not watched");

        private void EndTowContacts()
        {
            if (towContacts) UnityEngine.Object.Destroy(towContacts);
            towContacts = null;
        }

        // `what` names the speed measured ("along her length", "of an end closing on its place"); `off` the degrees she lies off
        // the tow's line, when bow first. The log line is written only when the strength passes the next note.
        private void AdaptTow(ref double strength, double along, double speed, string what, double off = double.NaN)
        {
            strength = Unberthing.TowStrength(strength, along, speed, Time.fixedDeltaTime);
            towStrengthMost = Math.Max(towStrengthMost, strength);
            if (strength < towStrengthNote) return;
            Log.Info("Sailing: " + Name + ": the tow pulls harder, " + strength.ToString("F1") + " m/s² (" + (strength * boat.Body.mass).ToString("F0") + " N): she makes " +
                along.ToString("F2") + " of " + speed.ToString("F2") + " m/s " + what + (double.IsNaN(off) ? "" : ", " + off.ToString("F0") + " degrees off the tow's line, turning " +
                (boat.Body.angularVelocity.y * Mathf.Rad2Deg).ToString("F1") + " degrees a second (the turn's built push " + towTurn.Built.ToString("F0") + " degrees a second²)") +
                "; the wind " + Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up).magnitude.ToString("F1") + " m/s.");
            towStrengthNote = Math.Max(2, towStrengthNote);
            while (towStrengthNote <= strength) towStrengthNote *= 2;
        }

        // Headway under tow, judged every 10 s by the metres gained (run U: grinding along "Cube_003" her speed jittered over the
        // reset while she gained nothing): `made` is how far off the dock (TowOff) or from where the tow out began (TowOut).
        private void TowHeadway(double made)
        {
            if (stageSeconds < towProgressAt + 10) return;
            if (!double.IsNaN(towProgressFrom))
            {
                double was = towStuck;
                towStuck = Unberthing.StuckTime(towStuck, (made - towProgressFrom) / 10.0, 10);
                if (towStuck == 0) towAtMiddle = false;
                if (was <= Unberthing.StuckSeconds && towStuck > Unberthing.StuckSeconds)
                {
                    Log.Info("Sailing: " + Name + ": no headway under tow for 30 s at a pull of " + towStrength.ToString("F1") + " m/s²; the wind " + Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up).magnitude.ToString("F1") + " m/s; " + TowForceReport() + ". " + StuckReport() + "; " + TowContactReport());
                    // Out by the bow she pivoted along what held her (run V: 1.32 m/s, no headway): her way planned again from
                    // here, and the pull at her middle until she makes headway.
                    if (unberthStage == UnberthStage.TowOut)
                    {
                        var p = Point(boat.StablePosition);
                        towPath = PlanTowOut(p); towIndex = 0; ResetPursuit(p); towOutStart = p; towAtMiddle = true;
                        if (towPath == null) escapeHeading = Escape(p, boat.Root.eulerAngles.y, c => EscapeClear(p, c));
                        Log.Info("Sailing: " + Name + ": the tow's way planned again from here (" + (towPath != null ? (towPath.Length - 1) + " legs" : "none: a clear heading") + "), pulled at her middle.");
                        towProgressFrom = 0; towProgressAt = stageSeconds; return;
                    }
                }
            }
            towProgressFrom = made; towProgressAt = stageSeconds;
        }

        // The push off the dock (decisions 181, 192). Each time her own way out fails she is pushed off again; once it has failed more
        // than MaximumTowOffs times (counted once, in HemmedIn) BeginUnberthing gives her to the backup tow instead (TowWhole).
        private void StartTow(string why, bool fallback)
        {
            unberthStage = UnberthStage.TowOff; stageSeconds = 0; heading.Reset(); towMark = displaced; towMarkAt = 0;
            towProgressAt = 0; towProgressFrom = double.NaN; towStuck = 0; ResetTowStrength(); WatchTowContacts();
            towDrawFore = towDrawAft = Unberthing.TowStrengthStart;
            if (fallback) Log.Info("Sailing: " + Name + " is pushed off the dock again (her own way failed: " + why + ").");
        }

        // How far the tow has taken her on this voyage (the QA reads it), and where she was at the last towed step.
        internal float TowedMetres { get; private set; }
        private Vector3 towLast;
        private bool towLastSet;

        // Decision 123: the tow takes her only until she is clear of the dock; these are the last resort, the whole way out.
        private bool towWhole;
        private int towOffs;
        private double towClearGap;
        private float unberthFinishedAt = float.NegativeInfinity;
        // How long after leaving the stage she may come back to it when hemmed in wherever she is (HemmedIn; run 20261010-142728: a
        // minute was too short at Dragon Cliffs). Since decision 184 (b) it no longer sends her to the backup tow at once.
        private const float FreshAfterTow = 180f;
        private const int MaximumTowOffs = 2;

        // Decision 165 (the user, 2026-10-11: "the fall back long tow change to tow into favorable wind and position then let go to let
        // it keep sailing"): along the whole tow's clear way, every FairCheckEvery once she is FairFrom off where it began (two of her
        // lengths, at least), a fair spot is one where she lays her guide point without beating (its bearing FairMargin outside her
        // close-hauled off the true wind, her best sails' as the chart reckons it, decision 163), her turn to it clear where she lies
        // and her way along it clear for her safety distance at FairSpeed. There she is let go.
        private const float FairCheckEvery = 2f, FairSpeed = 3f;
        private const double FairFrom = 30, FairMargin = 10;
        private float fairCheckAt;

        private bool FairSpot(out string why)
        {
            why = null;
            var p = Point(boat.StablePosition);
            double from = RoutePoint.Distance(p, towOutStart);
            if (from < Math.Max(FairFrom, 2 * water.Length)) return false;
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            if (wind.sqrMagnitude < 1f) return false;
            if (InHarbourChart) { harbourGuideAt = 0; UpdateHarbourGuide(p, WindFrom()); }
            var guide = GuidePoint();
            if (RoutePoint.Distance(p, guide) < 1) return false;
            double toGuide = NavigationMath.Heading(guide.x - p.x, guide.z - p.z), windFrom = WindFrom();
            double beat = TackNavigator.BeatOff(UpwindLimits.TrueAngle(EscapeDeadZone, wind.magnitude, ChartBeatSpeed), Margin);
            double off = Math.Abs(NavigationMath.Delta(windFrom, toGuide));
            if (off < beat + FairMargin) return false;
            if (!water.TurnClear(p, boat.Root.eulerAngles.y, toGuide) || !WayClear(p, toGuide, Look(FairSpeed), toGuide, guide)) return false;
            why = "her guide point " + RoutePoint.Distance(p, guide).ToString("F0") + " m off at " + toGuide.ToString("F0") + ", " + off.ToString("F0") +
                " degrees off the true wind (from " + windFrom.ToString("F0") + ", her close-hauled " + beat.ToString("F0") + "), her way clear for " + Look(FairSpeed).ToString("F0") +
                " m; " + from.ToString("F0") + " m from where the tow began";
            return true;
        }

        // Decision 197 (the user, 2026-10-12, the Brig taking the backup tow out of Dragon Cliffs: "i don't really want any tow after
        // unbearth"): no tow once she has left the dock; she held instead. Changed by decision 198 (f) the same day (the user: "don't
        // hold, start backup tow to take her out to sailable ground"): where her own way has failed and no other is left, the backup tow
        // (decision 165) takes her to water she can sail from; a failed departure in the lab still (183 a).
        private void TowWhole(string why)
        {
            towWhole = true; fairCheckAt = 0;
            // The last resort gets the tow's whole 5 minutes (run 20261010-100003: at Fort Aestrin the short tows had spent them, and
            // the whole tow held at once, "The tow could not take her off the dock in 5 minutes").
            towSeconds = 0;
            Log.Info("Sailing: " + Name + ": the backup tow takes her (" + why + "; decision 165: let go where she can sail).");
            if (unberthStage != UnberthStage.TowOff && unberthStage != UnberthStage.TowOut) { ResetTowStrength(); WatchTowContacts(); }
            EnterTowOut();
        }

        private void EnterTowOut()
        {
            var p = Point(boat.StablePosition);
            double h = boat.Root.eulerAngles.y;
            towTurn.Reset();
            towPath = PlanTowOut(p); towIndex = 0; ResetPursuit(p); escapeCheckAt = Time.time + 10;
            towOutStart = p; towProgressAt = 0; towProgressFrom = double.NaN; towStuck = 0; towAtMiddle = false; WatchTowContacts();
            if (towPath != null)
            {
                escapeHeading = NavigationMath.Heading(towPath[1].x - p.x, towPath[1].z - p.z);
                unberthStage = UnberthStage.TowOut; stageSeconds = 0; heading.Reset();
                Log.Info("Sailing: " + Name + " is towed off the dock (" + displaced.ToString("F1") + " m); the tow takes her out along a clear way, "
                    + (towPath.Length - 1) + " legs, " + RouteMath.Length(towPath).ToString("F0") + " m, first to " + escapeHeading.ToString("F0") + ".");
                return;
            }
            escapeHeading = Escape(p, h, c => EscapeClear(p, c));
            if (double.IsNaN(escapeHeading)) escapeHeading = NavigationMath.Normalize(dockBearing + 180);
            unberthStage = UnberthStage.TowOut; stageSeconds = 0; heading.Reset();
            Log.Info("Sailing: " + Name + " is towed off the dock (" + displaced.ToString("F1") + " m); no clear way planned; the tow takes her out to " + escapeHeading.ToString("F0") + ".");
        }

        // A clear way for the tow (her hull's swept clearance, no wind: a tow needs none) to an offing 200 m off: toward her next
        // waypoint first, then turned by 30 degrees either way up to astern. Null when none is found.
        private RoutePoint[] PlanTowOut(RoutePoint p)
        {
            towRouteNext = -1;
            if (towWhole) { var along = TowAlongRoute(p); if (along != null) return along; }
            // Pressed against a dock her own spot isn't clear, and no way starts there (launch 5, candidate Q: "no clear way
            // planned" by east_dock (54)): the way starts from the nearest clear spot 3 or 6 m off, where the tow takes her first.
            if (!water.ClearHull(p, p))
            {
                foreach (double off in new[] { 3.0, 6.0 })
                    for (int i = 0; i < 8; i++)
                    {
                        var spot = Ahead(p, i * 45, off);
                        if (!water.ClearHull(spot, spot)) continue;
                        // The first clear spot only: each planning costs up to 12 searches on the main thread.
                        var onward = PlanTowFrom(spot);
                        if (onward == null) return null;
                        var path = new RoutePoint[onward.Length + 1]; path[0] = p;
                        Array.Copy(onward, 0, path, 1, onward.Length);
                        return path;
                    }
                return null;
            }
            return PlanTowFrom(p);
        }

        // The last resort (decision 123) along her own route: from here through its waypoints (her route is clear water for her
        // hull), up to the first one after which the leg is sailable for her rig, at most WholeTowMetres. Run 20261010-142728's
        // S5: the Cog, a square rig in an onshore wind at Dragon Cliffs, was towed 300 m out toward her next waypoint, could not
        // sail on, and the next tow took her back past the harbour, her route going backwards.
        private const double WholeTowMetres = 1500;
        private int towRouteNext = -1;

        private RoutePoint[] TowAlongRoute(RoutePoint p)
        {
            if (Job.route == null || Job.waypoint < 1 || Job.waypoint >= Job.route.Length) return null;
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            double windFrom = NavigationMath.Heading(-wind.x, -wind.z);
            var points = new System.Collections.Generic.List<RoutePoint> { p };
            var previous = p;
            double total = 0;
            int next = -1;
            for (int i = Job.waypoint; i < Job.route.Length && total < WholeTowMetres; i++)
            {
                var w = Job.route[i];
                if (!water.Clear(previous, w)) break;
                total += RoutePoint.Distance(previous, w);
                points.Add(w); previous = w; next = i + 1;
                if (i + 1 < Job.route.Length)
                {
                    var after = Job.route[i + 1];
                    if (Math.Abs(NavigationMath.Delta(windFrom, NavigationMath.Heading(after.x - w.x, after.z - w.z))) >= NoGo + 8) break;
                }
            }
            if (points.Count < 2) return null;
            towRouteNext = Math.Min(next, Job.route.Length - 1);
            Log.Info("Sailing: " + Name + ": the tow takes her along her route, " + (points.Count - 1) + " legs, " + total.ToString("F0") + " m, to waypoint " +
                (next - 1) + " (her next leg " + (next < Job.route.Length ? "sailable" : "her route's end") + " or " + WholeTowMetres.ToString("F0") + " m).");
            return points.ToArray();
        }

        // D17: the tow's wide clearance (half her length + 3 m).
        private bool TowWide(RoutePoint a, RoutePoint b) => water.ClearWide(a, b, (float)Unberthing.TowClearance(water.Radius, water.Length), true);
        // The tow in from the release spot to the berth (VoyageTowIn): her hull's wide sweep only, as it ends over the berth's seabed.
        private bool TowWideHull(RoutePoint a, RoutePoint b) => water.ClearWide(a, b, (float)Unberthing.TowClearance(water.Radius, water.Length));

        private RoutePoint[] PlanTowFrom(RoutePoint p)
        {
            var waypoint = Job.route[Job.waypoint];
            double toWaypoint = RoutePoint.Distance(p, waypoint) > 1 ? NavigationMath.Heading(waypoint.x - p.x, waypoint.z - p.z) : boat.Root.eulerAngles.y;
            // The wide way first (D17); where none is found, her own clearance as before, and the log says so.
            foreach (bool wide in new[] { true, false })
            {
                Func<RoutePoint, RoutePoint, bool> clear = wide ? (a, b) => Unberthing.TowLegClear(p, a, b, water.Clear, TowWide) : (Func<RoutePoint, RoutePoint, bool>)water.Clear;
                foreach (double turn in Unberthing.OffingTurns)
                {
                    var offing = Ahead(p, toWaypoint + turn, Unberthing.TowOffing);
                    if (!clear(offing, offing)) continue;
                    var path = RoutePlanner.Plan(p, offing, clear, 0, 0, 1500);
                    if (path.Length < 2) continue;
                    Log.Info("Sailing: " + Name + ": the tow's way, " + (wide ? Unberthing.TowClearance(water.Radius, water.Length).ToString("F1") + " m clear each side" : "her own clearance only (no wider way)")
                        + ": " + string.Join(" ", path.Select(q => "(" + q.x.ToString("F0") + ", " + q.z.ToString("F0") + ")").ToArray()) + ".");
                    return path;
                }
            }
            return null;
        }

        // The player more than 1 km off while she comes off the dock (Voyage.TickSailing): the stage ends where she is and the far
        // handover takes her on by the clock, as it does a ship sailing.
        private void LeaveUnberthingFar()
        {
            EndTowContacts();
            Job.unberthing = false; ++Job.revision; unberthStage = UnberthStage.Read;
            Log.Info("Sailing: " + Name + ": the player is " + boat.ObserverDistance.ToString("F0") + " m off while she comes off the dock; the tow lets go and the clock takes her on.");
        }

        // Her way ahead is clear: the tow lets go, her sails are set, and her route goes on from here (replanned when its next
        // leg isn't clear).
        // `offTheDock` (decision 181: pushed or towed clear of the dock, the turn stage gone): her route out chosen by the wind she has
        // (decision 125, at most once a minute) or her harbour guide taken afresh, as the turn stage did.
        private void FinishUnberthing(bool offTheDock = false)
        {
            EndTowContacts();
            // Decision 220: off the cleats at Kicia Bay, the tow out comes before her sailor.
            if (TowOutPending) { BeginTowOut("off the shipyard's cleats"); return; }
            Job.unberthing = false; ++Job.revision; unberthFinishedAt = Time.time;
            if (offTheDock)
            {
                departureRouteChosen = true;
                if (!InHarbourChart && Time.time >= routeChoiceAt) ChooseRoute("off the dock");
                if (InHarbourChart) { harbourGuideAt = 0; UpdateHarbourGuide(Point(boat.StablePosition), WindFrom()); }
            }
            float h = boat.Root.eulerAngles.y;
            navigator.Restore(Job.tack, h, ApparentFrom(), DeadZone);
            ResetHelmWatch();
            sails.Plan((float)heel.Deployment, false); tickClock = 0.5f; navigationClock = 1; replanAt = 0; heading.Reset();
            Log.Info("Sailing: " + Name + " left her berth (" + (towSeconds > 0 ? "towed " + TowedMetres.ToString("F0") + " m, " + towSeconds.ToString("F0") + " s, the pull up to " + towStrengthMost.ToString("F1") + " m/s²" : "not towed")
                + "): " + (offTheDock ? "her sailor takes her at heading " : "her way ahead is clear at heading ") + h.ToString("F0") + ", " + displaced.ToString("F1") + " m off the dock; full-sail target set.");
        }

        // Alongside her berth's dock (its cleats tell the side) with an onshore wind of at least 1 m/s: why, else null.
        private string PinnedAtBerth(RoutePoint p)
        {
            var berth = OriginBerth();
            if (!berth || HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) >= water.Length) return null;
            double h = boat.Root.eulerAngles.y;
            int side = Unberthing.DockSide(h, p, berth.Cleats.Select(c => Point(FloatingOriginManager.instance.ShiftingPosToRealPos(c.transform.position))).ToArray());
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            if (side == 0 || wind.magnitude < Unberthing.MinimumWind) return null;
            double angle = Unberthing.WindToDock(h, side, WindFrom());
            return angle >= Unberthing.OnshoreAngle ? "an onshore wind would lay her onto the dock (" + angle.ToString("F0") + " degrees off it, " + wind.magnitude.ToString("F1") + " m/s)" : null;
        }

        // ---- What holds her (launch 5, runs P, R, S: unmoved by sail and tow for 10 minutes; run 20261007-080048's pictures: heeled
        // onto the pier, her mast under its boardwalk) ----

        // Under her keel at bow, middle and stern: the gap down to whatever lies there within 3 m (NaN: nothing), and its name.
        private double[] KeelGaps(out string report)
        {
            var gaps = new double[3]; var parts = new string[3];
            float[] along = { water.KeelFore * 0.6f, 0f, water.KeelAft * 0.6f };
            string[] label = { "bow", "middle", "stern" };
            for (int i = 0; i < 3; i++)
            {
                var keel = boat.Root.TransformPoint(new Vector3(0, water.KeelY, along[i]));
                gaps[i] = double.NaN; string what = "nothing within 3 m"; float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(keel + Vector3.up, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (!hit.collider || IsOwnVesselCollider(hit.collider) || hit.collider.GetComponentInParent<ShipItem>() || hit.distance >= best) continue;
                    best = hit.distance; gaps[i] = keel.y - hit.point.y;
                    what = hit.collider.name + (hit.collider.transform.parent ? " (" + hit.collider.transform.parent.name + ")" : "");
                }
                parts[i] = label[i] + " " + (double.IsNaN(gaps[i]) ? "-" : gaps[i].ToString("F2") + " m") + " to " + what;
            }
            report = string.Join("; ", parts);
            return gaps;
        }

        private string StuckReport()
        {
            var body = boat.Body;
            var gaps = KeelGaps(out var keel);
            var ocean = Crest.OceanRenderer.Instance;
            float keelY = boat.Root.TransformPoint(new Vector3(0, water.KeelY, 0)).y;
            string sea = ocean ? "sea level " + ocean.SeaLevel.ToString("F2") + ", keel " + (keelY - ocean.SeaLevel).ToString("F2") + " m under it" : "no ocean";
            var lines = boat.Moorings.ropes.Select((r, i) => !r ? null : "line " + i + (r.IsMoored() ? " tied" : " free")).Where(s => s != null);
            var joints = boat.Root.GetComponentsInChildren<Joint>(true)
                .Where(j => j.connectedBody && j.connectedBody != body && !j.connectedBody.transform.IsChildOf(boat.Root))
                .Select(j => j.GetType().Name + " " + j.name + " to " + j.connectedBody.name).Take(6).ToArray();
            var centre = boat.Root.TransformPoint(new Vector3(0, water.KeelY / 2f, (water.KeelFore + water.KeelAft) / 2f));
            var half = new Vector3(water.Radius, Mathf.Abs(water.KeelY) / 2f + 0.3f, (water.KeelFore - water.KeelAft) / 2f);
            var touching = Physics.OverlapBox(centre, half, boat.Root.rotation, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => c && !IsOwnVesselCollider(c) && !c.GetComponentInParent<ShipItem>())
                .Select(c => c.name + (c.transform.parent ? " (" + c.transform.parent.name + ")" : "")).Distinct().Take(8).ToArray();
            var rope = boat.Moorings.GetAnchorController();
            return "aground " + (Unberthing.Aground(gaps) ? "yes" : "no") + " (under her keel: " + keel + "); " + sea
                + "; heel " + CourseSafety.HeelDegrees(boat.Root.right.y, boat.Root.up.y).ToString("F0") + " degrees"
                + "; body " + (body.isKinematic ? "kinematic" : "dynamic") + ", constraints " + body.constraints + ", drag " + body.drag.ToString("F2")
                + "/" + body.angularDrag.ToString("F2") + ", mass " + body.mass.ToString("F0") + ", speed " + body.velocity.magnitude.ToString("F2") + " m/s"
                + "; water " + boat.Damage.waterLevel.ToString("F2") + (boat.Damage.sunk ? " (sunk)" : "")
                + "; " + string.Join(", ", lines.ToArray()) + "; anchor " + (boat.Anchor.IsSet() ? "set" : "up") + (rope ? " (" + rope.currentLength.ToString("F2") + ")" : "")
                + "; joints to other bodies: " + (joints.Length == 0 ? "none" : string.Join(", ", joints))
                + "; in her hull's box: " + (touching.Length == 0 ? "nothing" : string.Join(", ", touching));
        }

        private void SteerTo(double target, float dt)
        {
            float yawRate = Vector3.Dot(boat.Body.angularVelocity, Vector3.up) * Mathf.Rad2Deg;
            helm.Demand = (float)heading.Tick(boat.Root.eulerAngles.y, target, yawRate, dt);
            // As in Steer: the game reverses the rudder's yaw for no way or sternway.
            if (Vector3.Dot(boat.Body.velocity, boat.Root.forward) <= 0) helm.Demand = -helm.Demand;
            helm.Apply();
        }

        // The tow's pull, toward TowSpeed along `bearing`: bodily at her centre of mass (off the dock, or at her middle when held on
        // something), or bow first (PullBowFirst: out of the harbour, in to her berth).
        private void Pull(double bearing, bool atBow, double speed = Unberthing.TowSpeed)
        {
            if (boat.Body.isKinematic) return;
            if (atBow) { PullBowFirst(bearing, speed); return; }
            // Pulled bodily (off the dock, or at her middle when held): pulled along the tow, for the re-plan's wait.
            towPulledSeconds += Time.fixedDeltaTime;
            var target = Vector(Ahead(new RoutePoint(0, 0), bearing, speed));
            var v = boat.Body.velocity;
            // Her speed along the tow is the standard: the strength rises until she makes the tow's speed (Unberthing.TowStrength).
            double along = speed > 0 ? (v.x * target.x + v.z * target.z) / speed : 0;
            AdaptTow(ref towStrength, along, speed, "along the tow");
            Unberthing.Tow(v.x, v.z, target.x, target.z, out var ax, out var az, towStrength);
            var force = new Vector3((float)ax, 0, (float)az) * boat.Body.mass;
            NoteTowForce(force);
            boat.Body.AddForce(force);
        }

        // Decision 124 (the user, 2026-10-10: "all tow can have sideway force, if situation needed"): set off the tow's way by the
        // wind, she is pulled sideways at her middle back onto it, at most 0.5 m/s across, its strength adapting as the tow's
        // does (run 20261010-115247: at Gold Rock City the wind set the Dhow 8-13 m off the way, onto the dock 16 times).
        private double sideStrength = Unberthing.TowStrengthStart;

        private void HoldOnWay(RoutePoint at, RoutePoint nearest, double offWay)
        {
            if (boat.Body.isKinematic) return;
            if (offWay < 1.0) { sideStrength = Unberthing.TowStrengthStart; return; }
            var toWay = Vector(nearest) - Vector(at); toWay.y = 0;
            if (toWay.sqrMagnitude < 1e-6f) return;
            toWay.Normalize();
            double want = Math.Min(0.5, offWay * 0.1);
            double across = Vector3.Dot(boat.Body.velocity, toWay);
            sideStrength = Unberthing.TowStrength(sideStrength, across, want, Time.fixedDeltaTime);
            var force = toWay * (float)(Unberthing.TowAlong(across, want, sideStrength) * boat.Body.mass);
            NoteTowForce(force);
            boat.Body.AddForce(force);
        }

        // Bow first (Unberthing.BowFirstShare): turned onto the tow's line about her middle, pulled along her length only, at her
        // centre of mass (no heel, no swing of her stern); her speed along her length is the standard for the tow's strength.
        private void PullBowFirst(double bearing, double speed)
        {
            var forward = boat.Root.forward; forward.y = 0;
            if (forward.sqrMagnitude < 1e-6f) return;
            forward.Normalize();
            double off = NavigationMath.Delta(NavigationMath.Heading(forward.x, forward.z), bearing);
            double rate = boat.Body.angularVelocity.y * Mathf.Rad2Deg;
            // The turn with what it lacks built up (Unberthing.TurnController; the user, 2026-10-10).
            double turn = towTurn.Tick(off, rate, Time.fixedDeltaTime);
            // How long she has pointed within BowFirstAngle of the line, pulled along it: the re-plan for being off the way
            // waits for TowPulledBeforeReplan of it (ProbeUnberthing).
            towPulledSeconds = Math.Abs(off) < Unberthing.BowFirstAngle ? towPulledSeconds + Time.fixedDeltaTime : 0;
            boat.Body.AddTorque(Vector3.up * (float)(turn * Mathf.Deg2Rad), ForceMode.Acceleration);
            double target = speed * Unberthing.BowFirstShare(off);
            double along = Vector3.Dot(boat.Body.velocity, forward);
            AdaptTow(ref towStrength, along, target, "along her length", off);
            var force = forward * (float)(Unberthing.TowAlong(along, target, towStrength) * boat.Body.mass);
            NoteTowForce(force);
            boat.Body.AddForce(force);
        }

        internal static RoutePoint Point(Vector3 v) => new RoutePoint(v.x, v.z);
        internal static Vector3 Vector(RoutePoint p) => new Vector3(p.x, 0, p.z);
        internal static RoutePoint Ahead(RoutePoint p, double heading, double length)
        { double r = heading * Math.PI / 180; return new RoutePoint((float)(p.x + Math.Sin(r) * length), (float)(p.z + Math.Cos(r) * length)); }

        // Stable across process loads; no Unity instance IDs, transient rotations, or display-name-only identity.
        internal static string RigDescriptor(BoatContext c)
        {
            return string.Join("|", c.Root.GetComponentsInChildren<Sail>(true).Where(s => s.shipRigidbody == c.Body && s.IsInstalled())
                .Select(s => FormattableString.Invariant($"{Path(s.transform, c.Root)}:{s.sailArea:R}:{s.minAngle:R}:{s.maxAngle:R}:{s.squareSail}:{s.transform.parent.localScale.x:R},{s.transform.parent.localScale.y:R},{s.transform.parent.localScale.z:R}"))
                .OrderBy(s => s, StringComparer.Ordinal));
        }
        // Each step by its name and its count among the same-named siblings before it, not GetSiblingIndex: other children
        // coming or going under a mast don't change it (run 20261010-002606: the Cog booked in the shipyard had every sail one
        // index higher than after her release, so her rig never matched her booking and her voyage never began).
        internal static string Path(Transform t, Transform root)
        { string result = ""; for (var p = t; p && p != root; p = p.parent) result = p.name + "[" + SameNameIndex(p) + "]/" + result; return result; }

        internal static int SameNameIndex(Transform t)
        {
            var parent = t.parent;
            if (!parent) return 0;
            int n = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i);
                if (sibling == t) return n;
                if (sibling.name == t.name) n++;
            }
            return n;
        }
    }

    // ==== VoyageHarbour ====

    // Decision 157 (the user, 2026-10-11): leaving her harbour by its waters, not by checkpoints. Her own terrain detection is run once
    // over the whole harbour (NavigationWater.WaterCell: the seabed under her keel and her hull's beam clear) and flooded from the open
    // sea at its edge (WaterField); her guide is the farthest point down that water distance she can reach in a straight, clear line;
    // her course, tacks and live look-ahead steer to it as to any guide point. Out of the chart, her route is planned afresh from where
    // she is.

    internal sealed partial class Voyage
    {
        // The chart's cells (8 m), how far past the island's envelope it reaches (its edge in open sea), how far down the water
        // distance her guide is looked for, and the frame time its building may take.
        internal const double ChartCell = 8, ChartBeyond = 100, ChartReach = 400, ChartMostHalf = 2000;
        // The middle of the waterway (the user, 2026-10-11): water nearer than ChartWant to what her hull doesn't fit costs up to
        // ChartWeight times more in the flood; her guide first along a line ChartSpare clear of her hull each side.
        internal const double ChartWant = 50, ChartWeight = 3, ChartSpare = 10;
        // Decision 193 (the user, 2026-10-12: "when doing water distance map, it should consider it's own ship size, for brig it's much
        // easier for it to go out from eastern exit, even if it take a little bit longer"): the middle of the waterway is wanted by her
        // size, ChartWantLengths of her lengths (never under ChartWant), and a step is a beat by the sails that carry most of her area
        // (her rig's least angle, decision 133: a square rig's squares), not her closest-winded ones. On Dragon Cliffs' map (run
        // 20261012-070702) the Brig's flood then leaves by the eastern way, down the wind, for Fort Aestrin and Chronos alike; the
        // Dhow's stays the northern channel (a 50 m channel, 50-60 degrees off the westerly: a reach for a lateen, a beat too narrow to
        // tack in for the Brig's squares).
        internal const double ChartWantLengths = 4;
        // Decision 193 amended (2026-10-12, the Brig's chart still chose Dragon Cliffs' northern channel for Fort Aestrin in a westerly,
        // runs 20261012-124120 and -123333): a beat where too narrow to tack in costs WaterField.NarrowBeatFactor times its length for a
        // hull up to NarrowBeatLength long, in proportion to her length beyond (the Brig, 29 m: 5.8). On the chart's own cells (the
        // harbour map's, run 20261012-070702) the Brig then leaves by the eastern way to Fort Aestrin and Chronos in winds from 273 to 297;
        // hulls up to 15 m (Dhow, Cog, Kakam) are unchanged.
        internal const double NarrowBeatLength = 15;
        internal static double NarrowBeatFor(double length) => WaterField.NarrowBeatFactor * Math.Max(1, length / NarrowBeatLength);
        private const double ChartBudgetMs = 3;
        private WaterField chart;
        private int chartNext;
        private bool chartDone, chartLeft, chartNone;
        private int chartFrames, floodFrames;
        private double chartMs, floodMs;
        // The managed heap (GC.GetTotalMemory) when her chart was begun and when a flood was, and the longest gap between their slices (a
        // frame's length, or more): the log shows whether a chart or a reflood grows the heap or holds a frame (decision 201, the flood's
        // buffers kept: a reflood allocates nothing).
        private long chartHeap, floodHeap;
        private double chartLongest, floodLongest;
        private float chartFrameAt = -1, floodFrameAt = -1;
        private static double Gap(ref float at, double longest)
        {
            float now = Time.realtimeSinceStartup;
            if (at >= 0) longest = Math.Max(longest, (now - at) * 1000);
            at = now;
            return longest;
        }
        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("F1") + " MB";
        // A reflood under way (decision 198 j): why, and its cost so far.
        private string refloodWhy;
        private RoutePoint harbourGuide;
        private readonly List<RoutePoint> harbourOnward = new List<RoutePoint>();
        private float harbourGuideAt, harbourLogAt, harbourGuideRushAt, harbourGuideSlowAt;
        private RoutePoint harbourLogged;

        // Her guide while she is in her harbour's chart (the QA's harbour map reads it). Arriving (decision 198 h), only once she is in its
        // water.
        internal bool InHarbourChart => chartDone && chart != null && !chartLeft && (!chartArriving || inArrivalChart);
        internal RoutePoint HarbourGuide => harbourGuide;
        // Her origin's chart (decision 157): her port's (decision 177; her destination's chart doesn't put her in port, 198 h).
        private bool InOriginChart => InHarbourChart && !chartArriving;

        // Builds the chart around the island holding `around`, ChartBudgetMs a frame; true when it is ready (or there is no island
        // round it: no chart, her route as before). `p`: where she is.
        private List<NavigationWater.ChartExclusion> chartUnknown;
        private int chartShallow;
        private bool ChartStep(RoutePoint around, RoutePoint p)
        {
            if (chartDone || chartNone || chartLeft) return true;
            // Decision 238: her cells probed only with the island's seabed on; a chart begun waits where it got to.
            chartPaused = (chart == null || chartNext < chart.Count) && !ChartSeabedOn(ChartIsland());
            if (chartPaused)
            {
                Status.Message = "Waiting until the player is near enough to chart her harbour.";
                return false;
            }
            if (chart == null)
            {
                if (!water.IslandAround(around, out var island, out float radius)) { chartNone = true; return true; }
                double half = Math.Min(ChartMostHalf, radius + ChartBeyond);
                chartHeap = GC.GetTotalMemory(false); chartLongest = 0; chartFrameAt = -1;
                chart = new WaterField(island.x, island.z, half, ChartCell);
                chartUnknown = water.UnloadedNear(island, (float)(half * Math.Sqrt(2)));
                chartNext = 0; chartFrames = 0; chartMs = 0; floodMs = 0; floodFrames = 0; chartShallow = 0;
                Log.Info("Sailing: " + Name + " charts " + (chartArriving ? "her destination's harbour (decision 198 h)" : "her harbour (decision 157)") + ": " + chart.Width + " by " + chart.Height +
                    " cells of " + ChartCell.ToString("F0") + " m round the island's centre, " + radius.ToString("F0") + " m its envelope" +
                    (chartUnknown.Count > 0 ? ", " + chartUnknown.Count + " islands not loaded within it (never water)" : "") + ".");
            }
            chartLongest = Gap(ref chartFrameAt, chartLongest);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Decision 198 (g): each cell's shallowest probe kept with it. Decisions 222, 236: her chart's island judged at its true height,
            // however low (or high) it stands for the player now (IslandHeight.Drop): its tops kept true.
            float limit = NavigationWater.RealSea - water.Draft, low = ChartIslandDrop();
            while (chartNext < chart.Count && clock.Elapsed.TotalMilliseconds < ChartBudgetMs)
            {
                bool fits = water.ChartCell(chart.Centre(chartNext), (float)ChartCell, chartUnknown, out float top, low);
                chart.SetCell(chartNext, fits, top);
                if (fits && top > limit) chartShallow++;
                chartNext++;
            }
            chartFrames++; chartMs += clock.Elapsed.TotalMilliseconds;
            if (chartNext < chart.Count) { Status.Message = "Charting her harbour (" + (100.0 * chartNext / chart.Count).ToString("F0") + "%)."; return false; }
            // Decision 198 (j): the flood a slice a frame too.
            if (!chart.Flooded)
            {
                if (!chart.Flooding)
                {
                    // Decision 201 (a): arriving, her approach point judged again now her chart's cells are probed (ClearApproach).
                    if (chartArriving) approachCheckedAt = float.NegativeInfinity;
                    chartGoal = chartArriving ? NavigationDestination() : water.ChartGoal(p, NavigationDestination());
                    FloodChart();
                }
                var slice = System.Diagnostics.Stopwatch.StartNew();
                bool flooded = chart.FloodStep(ChartBudgetMs);
                floodMs += slice.Elapsed.TotalMilliseconds; floodFrames++;
                if (!flooded) { Status.Message = "Charting her harbour (her way " + (chartArriving ? "in" : "out") + ")."; return false; }
            }
            chartDone = true;
            int wet = 0;
            for (int i = 0; i < chart.Count; i++) if (chart.Water(i)) wet++;
            var way = chart.Descent(p, 1e7);
            var centre = chart.Centre(chart.Count / 2 + chart.Width / 2);
            string exit = way.Count == 0 ? (chartArriving ? "nowhere from here (not in its water yet)" : "none from here") : chartArriving ? RoutePoint.Distance(p, chartGoal).ToString("F0") + " m to her approach point, " +
                chart.DistanceFrom(p).ToString("F0") + " m by water from here" : NavigationMath.Heading(way[way.Count - 1].x - centre.x, way[way.Count - 1].z - centre.z).ToString("F0") +
                " degrees from the island's centre, " + chart.DistanceFrom(p).ToString("F0") + " m to go from here by water and on";
            Log.Info("Sailing: " + Name + " has charted " + (chartArriving ? "her destination's harbour" : "her harbour") + ": " + wet + " of " + chart.Count + " cells water for her hull (" +
                chartShallow + " her hull fits too shallow for her keel at the sea now, decision 198 g: her keel " + water.KeelDepth.ToString("F2") + " m, " + NavigationWater.KeelMargin.ToString("F2") +
                " m under it at least, her line wanting " + chart.Depth.Want.ToString("F2") + " m); her way " + (chartArriving ? "in: " : "out leaves the chart ") + exit +
                (NavigationMath.Finite(chart.WindFrom) ? ", the wind from " + chart.WindFrom.ToString("F0") + " counted (decision 163)" : "") +
                " (" + chartMs.ToString("F0") + " ms over " + chartFrames + " frames, the flood " + floodMs.ToString("F0") + " ms over " + floodFrames + " frames; the heap " +
                Megabytes(chartHeap) + " before, " + Megabytes(GC.GetTotalMemory(false)) + " after; the longest frame while charting " + chartLongest.ToString("F0") + " ms).");
            // Her way closed from where she is: for want of water (the tide), or for good (decision 198 g, TideCheck).
            if (way.Count == 0 && !chartArriving) WayClosed(p, "her harbour is charted");
            return true;
        }

        // Decision 163 (the user, 2026-10-11): the flood counts the wind: a step she would beat costs WaterField.BeatFactor times its
        // length, in water under ChartNarrowLengths of her lengths wide WaterField.NarrowBeatFactor; her beat is her close-hauled off
        // the true wind with her best sails (decision 153's least limit turned to the true wind at ChartBeatSpeed). Flooded again on a
        // true wind shift over ChartWindShift while she is in it (Chaotic Wind's opposite winds favour opposite exits).
        // Decision 174: too narrow to tack in is also under ChartNarrowLeast metres, whatever her length (a small hull's 3 lengths are
        // under her turning circle: run 20261011-191709 on EQ, the Dhow sent up Dragon Cliffs' north channel, 30-45 m of water, to beat
        // into a westerly, stalled in its middle for 30 minutes).
        internal const double ChartNarrowLengths = 3, ChartNarrowLeast = 60, ChartWindShift = 45, ChartBeatSpeed = 2;
        private RoutePoint chartGoal;
        private double chartBeat = 89;
        private void FloodChart()
        {
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            double mainSails = NavigationMath.Finite(rigMinimum) ? rigMinimum : EscapeDeadZone;
            double beat = NavigationMath.Clamp(UpwindLimits.TrueAngle(NavigationMath.Clamp(mainSails, 10, 89), wind.magnitude, ChartBeatSpeed) + Margin, 10, 89);
            chartBeat = beat;
            if (wind.sqrMagnitude > 0.01f) chart.SetWind(WindFrom(), beat, Math.Max(ChartNarrowLengths * water.Length, ChartNarrowLeast) / 2, NarrowBeatFor(water.Length));
            var goal = chartGoal;
            // Decision 198 (j): begun here, a slice a frame after (ChartStep for her first, LineStep for a reflood); her chart's old
            // distances serve until it is done. Decision 198 (g): the depth judged at the sea as it stands now. Decision 198 (h): her
            // way in from her approach point.
            var depth = DepthNow();
            double want = Math.Max(ChartWant, ChartWantLengths * water.Length);
            if (chartArriving) chart.FloodToBegin(goal, want, ChartWeight, depth);
            else chart.FloodBegin(c => RoutePoint.Distance(c, goal), want, ChartWeight, depth);
            tideCheckedSea = depth.Sea; tideCheckedKeel = depth.Keel; arrivalReckoned = false;
            floodMs = 0; floodFrames = 0; floodHeap = GC.GetTotalMemory(false); floodLongest = 0; floodFrameAt = -1;
            // Decision 201 (b): her way in checked again on her destination's chart as flooded now.
            if (chartArriving) { wayInPlanner = null; wayInChecked = false; }
        }

        // Decision 198 (g) (the user's: "under 1 m of water beneath her keel, more for her keel's dip in the sea she has, costed"): the
        // depth her chart judges by now: the sea's surface as it stands (the mod measures it; Simple Tides moves it), her keel's depth,
        // the hard limit under it (NavigationWater.KeelMargin, decision 158), and DepthWant and her keel's dip wanted under it, less
        // costing up to DepthWeight more (no slowing over shallow water: the user's 198 choice).
        internal const double DepthWant = 1, DepthWeight = 2;
        private ChartDepth DepthNow() =>
            new ChartDepth(NavigationWater.RealSea, water.KeelDepth, NavigationWater.KeelMargin, DepthWant + water.KeelDip, DepthWeight);

        // Decision 198 (g): the tide watched. Every TideEvery, once the sea has moved TideStep (or her keel KeelStep) since her chart was
        // last judged: the cells whose verdict it changes counted (WaterField.Changes); her chart flooded again when a cell closes by her
        // way ahead (her line, else her chart's way down from her), or opens while she waits for the tide or has no line.
        internal const double TideStep = 0.25, KeelStep = 0.1;
        private const float TideEvery = 2;
        private double tideCheckedSea = double.NaN, tideCheckedKeel = double.NaN;
        private float tideCheckAt;
        private void TideTick(RoutePoint p)
        {
            if (!chartDone || chart == null || chartLeft || !chart.Flooded || chart.Flooding || chart.Needing || Time.time < tideCheckAt) return;
            tideCheckAt = Time.time + TideEvery;
            var was = chart.Depth;
            if (!was.Known) return;
            var now = DepthNow();
            if (Math.Abs(now.Sea - tideCheckedSea) < TideStep && Math.Abs(now.Keel - tideCheckedKeel) < KeelStep) return;
            tideCheckedSea = now.Sea; tideCheckedKeel = now.Keel;
            int changes = chart.Changes(now, out int closing);
            if (changes == 0) return;
            double length = Math.Max(10, water.Length);
            var ahead = new List<RoutePoint>();
            if (line != null) for (int k = lineAt; k < line.Count; k += 2) ahead.Add(line[k].Point);
            else ahead.AddRange(chart.Descent(p, ChartReach));
            double reach = 2 * Math.Max(0.5, water.Radius) + Math.Max(LinePlanner.TurnSpareLeast, LinePlanner.TurnSpareLengths * length) + ChartCell;
            bool closesHers = closing > 0 && chart.ClosesNear(now, ahead, reach);
            bool opens = changes > closing && (tideWait || line == null);
            if (!closesHers && !opens) return;
            FloodChart();
            refloodWhy = "decision 198 g: the sea " + (now.Sea - was.Sea).ToString("+0.00;-0.00") + " m and her keel " + (now.Keel - was.Keel).ToString("+0.00;-0.00") +
                " m since her chart was flooded, " + closing + " cells too shallow now and " + (changes - closing) + " deep enough" + (closesHers ? ", by her way ahead" : "");
            if (closesHers) ReplanLine("the tide: her way ahead is shallower (decision 198 g)");
            Log.Info("Sailing: " + Name + "'s chart is flooded again for the tide (" + refloodWhy + "), a slice a frame (decision 198 j).");
        }

        // Decision 198 (g) (112): her way closed from where she lies (her chart's flood reaches no water near her). For want of water (a
        // way leads on once the sea rises: WaterField.NeedBegin, reckoned a slice a frame in TideHolds) she waits for the tide; for good,
        // her way out as before (her origin's chart left: her route planned afresh).
        private void WayClosed(RoutePoint p, string when)
        {
            if (tideGaveUp || chart == null || !chart.Depth.Known) { if (!chartArriving) chartLeft = true; return; }
            tideClosedWhen = when;
            chart.NeedBegin(DepthNow());
        }

        // Decision 198 (g): waiting for the tide (the user, 2026-10-12: "no wait for the tide before setting out, a hold until the water is
        // back where her way closes for want of it"): until the sea stands TideMargin over what her way needs, at most TideWaitMost in all
        // since the first wait of this chart (Simple Tides: high to low in about 12.5 real minutes), then her chart flooded again. Her sails
        // furled and her helm amidships meanwhile (HoldNavigation), moored when she hasn't left her berth.
        internal const double TideMargin = 0.1;
        private const float TideWaitMost = 900;
        private bool tideWait, tideGaveUp, tideRisen;
        private double tideNeed = double.NaN;
        private float tideWaitSince = -1, tideNoteAt;
        private string tideClosedWhen;

        // True while she waits for the tide or reckons whether she must (her navigation held).
        private bool TideHolds(float dt, RoutePoint p)
        {
            // Not in the push off the dock, nor in her final approach: those stages hold her themselves.
            if (chart == null || Job.unberthing || Job.dockingPhase > 0) return false;
            if (chart.Needing)
            {
                if (!chart.NeedStep(ChartBudgetMs)) { HoldNavigation(dt, 0); Status.Message = "Reckoning her way for the tide."; return true; }
                TideReckoned(p);
            }
            if (!tideWait) return false;
            double sea = NavigationWater.RealSea;
            bool risen = sea >= tideNeed, tooLong = Time.realtimeSinceStartup - tideWaitSince > TideWaitMost;
            if ((risen || tooLong) && !tideRisen)
            {
                tideRisen = true; tideGaveUp |= tooLong && !risen;
                FloodChart();
                refloodWhy = risen ? "decision 198 g: the sea has risen to " + sea.ToString("F2") + ", her way needing " + (tideNeed - TideMargin).ToString("F2")
                    : "decision 198 g: no more waiting for the tide after " + TideWaitMost.ToString("F0") + " s (the sea " + sea.ToString("F2") + ", her way needing " + (tideNeed - TideMargin).ToString("F2") + ")";
                Log.Info("Sailing: " + Name + (risen ? " has the water back: " : " waits no longer for the tide: ") + refloodWhy + "; her chart flooded again.");
            }
            // Her chart flooded again for the sea she has: she sails on (decision 112).
            if (tideRisen && !chart.Flooding)
            {
                tideWait = tideRisen = false; tideNeed = double.NaN;
                progressWatchdog = new ProgressWatchdog();
                ReplanLine("the tide has risen (decision 198 g)");
                harbourGuideAt = 0;
                if (!NavigationMath.Finite(chart.DistanceFrom(p))) WayClosed(p, "the tide rose");
                return chart.Needing;
            }
            HoldNavigation(dt, 0);
            Status.Message = "Waiting for the tide: her way " + (chartArriving ? "in" : "out") + " needs the sea " + Math.Max(0, tideNeed - sea).ToString("F2") + " m higher.";
            if (Time.time >= tideNoteAt)
            {
                tideNoteAt = Time.time + 60;
                Log.Info("Sailing: " + Name + " waits for the tide (decision 198 g): the sea at " + sea.ToString("F2") + ", her way needing " + (tideNeed - TideMargin).ToString("F2") +
                    " (" + (Time.realtimeSinceStartup - tideWaitSince).ToString("F0") + " s so far).");
            }
            return true;
        }

        // The sea her way needs, reckoned: over the sea she has, she waits for it; else her way is closed for good (as before).
        private void TideReckoned(RoutePoint p)
        {
            // Before she leaves her berth, from the water her push off the dock takes her to (her length and her beam).
            int reach = !Job.departureComplete ? (int)Math.Ceiling((Math.Max(10, water.Length) + 2 * water.Radius) / ChartCell) + 1 : 3;
            double need = chart.NeedFrom(p, reach), sea = NavigationWater.RealSea;
            if (NavigationMath.Finite(need) && need > sea - 1e-3)
            {
                if (!tideWait && tideWaitSince < 0) tideWaitSince = Time.realtimeSinceStartup;
                tideWait = true; tideRisen = false; tideNeed = need + TideMargin; tideNoteAt = Time.time + 60;
                Log.Info("Sailing: " + Name + "'s way " + (chartArriving ? "in" : "out") + " closes for want of water (" + tideClosedWhen + "): it needs the sea at " + need.ToString("F2") +
                    ", " + (need - sea).ToString("F2") + " m higher than now; she waits for the tide (decisions 198 g, 112)" + (Job.departureComplete ? "" : ", moored") + ".");
                return;
            }
            Log.Info("Sailing: " + Name + "'s way " + (chartArriving ? "in" : "out") + " is closed from here (" + tideClosedWhen + "), not for want of water" +
                (double.IsPositiveInfinity(need) ? " (no way at any tide)" : "") + "; " + (chartArriving ? "her route takes her in." : "her route as before (decision 157)."));
            if (chartArriving) { inArrivalChart = false; line = null; linePlanner = null; }
            else if (Job.departureComplete) LeaveChart(p, WindFrom());
            else chartLeft = true;
        }

        // Out of her origin's chart (decision 157): her route planned afresh from here.
        private void LeaveChart(RoutePoint p, double windFrom)
        {
            chartLeft = true; line = null; linePlanner = null;
            var route = RoutePlanner.Plan(p, water.ChartGoal(p, NavigationDestination()), water.Clear, windFrom, NoGo);
            Log.Info("Sailing: " + Name + " is out of her harbour's chart (decision 157); " + (route.Length >= 2 ? "her route from here is planned afresh." : "no route from here: she keeps hers."));
            if (route.Length >= 2) ReplaceRoute(route);
        }

        // In her chart (decision 157): her guide, every 2 s, the farthest point down the water distance within ChartReach she can
        // reach in a straight, clear line (her hull's sweep and the seabed); the cells beyond it are her way on (WayClear). Out of
        // it (within ChartBeyond of its edge, or no water near her in it), her route planned afresh from here. True while in it.
        private bool UpdateHarbourGuide(RoutePoint p, double windFrom)
        {
            // Decision 198 (h): arriving, in her destination's chart only once she is in its water (her route takes her there).
            if (chartArriving && !ArrivalInChart(p)) return false;
            if (!InHarbourChart) return false;
            // Decision 198 (g): her way reckoned for the tide, or waited for: she stays in her chart.
            if (chart.Needing || tideWait) return true;
            if (!chart.Inside(p, ChartBeyond) || !NavigationMath.Finite(chart.DistanceFrom(p)))
            {
                // Decision 198 (g): her chart's water closed round her (the tide fell): first whether it is for want of water.
                if (chart.Inside(p, ChartBeyond) && !tideGaveUp && chart.Depth.Known) { WayClosed(p, "the water round her closed"); return true; }
                LeaveChart(p, windFrom);
                return false;
            }
            // Decision 198 (c): on her line, her guide is a point on it.
            if (line != null && FollowLine(p, windFrom)) return true;
            if (Time.time < harbourGuideAt) return true;
            harbourGuideAt = Time.time + 2;
            // Decision 163: the wind shifted over ChartWindShift since the flood: her way out reckoned again for it.
            if (!chart.Flooding && NavigationMath.Finite(chart.WindFrom) && Math.Abs(NavigationMath.Delta(chart.WindFrom, windFrom)) > ChartWindShift)
            {
                double was = chart.WindFrom;
                FloodChart();
                refloodWhy = "decision 163: the wind from " + was.ToString("F0") + " now from " + windFrom.ToString("F0");
                Log.Info("Sailing: " + Name + " reckons her way " + (chartArriving ? "in" : "out") + " again (" + refloodWhy + "); her chart flooded again a slice a frame (decision 198 j).");
            }
            var path = chart.Descent(p, ChartReach);
            if (path.Count == 0) return true;
            int pick = -1;
            bool wide = true;
            for (int k = path.Count - 1; k >= 1 && pick < 0; k -= 3)
                if (water.ClearWide(p, path[k], water.Radius + (float)ChartSpare, true)) pick = k;
            if (pick < 0)
            {
                wide = false;
                for (int k = path.Count - 1; k >= 1; k -= 3)
                    if (water.Clear(p, path[k])) { pick = k; break; }
            }
            if (pick < 0) pick = Math.Min(path.Count - 1, 2);
            harbourGuide = path[pick];
            harbourOnward.Clear();
            // Every cell on (her way on is checked cell to cell, decision 162; every third cut the corners of a bending gap).
            for (int k = pick + 1; k < path.Count; k++) harbourOnward.Add(path[k]);
            if (path.Count - 1 > pick && (harbourOnward.Count == 0 || RoutePoint.Distance(harbourOnward[harbourOnward.Count - 1], path[path.Count - 1]) > 0.5)) harbourOnward.Add(path[path.Count - 1]);
            if (Time.time >= harbourLogAt && RoutePoint.Distance(harbourGuide, harbourLogged) > 20)
            {
                harbourLogAt = Time.time + 10; harbourLogged = harbourGuide;
                Log.Info("Sailing: " + Name + "'s harbour guide " + RoutePoint.Distance(p, harbourGuide).ToString("F0") + " m off at " +
                    NavigationMath.Heading(harbourGuide.x - p.x, harbourGuide.z - p.z).ToString("F0") + " (decision 157; " + chart.DistanceFrom(p).ToString("F0") + " m to go by water and on" +
                    (pick < path.Count - 1 || path.Count < 2 ? "" : ", as far down as she looks") + (wide ? "" : ", no line " + ChartSpare.ToString("F0") + " m wider than her hull") + ").");
            }
            return true;
        }

        // Decision 195 (the user, 2026-10-12: "bigger ship should still consider northen exit, if it think it can turn around by itself
        // without towing"; the Brig, pushed off Dragon Cliffs' dock, failed her 105-degree turn into the northern channel three times
        // in seconds and took the backup tow, run 20261012-123333 on FN): hemmed in near her berth, the way she failed by is closed
        // in her chart (its cells from one to ChartFailLengths of her lengths down her way, each within her length) and the chart
        // flooded again: her next try goes by another way. True when her way out changed. (195 (a), closing a start she couldn't turn
        // into, went with decision 198: her line judges her start by her own circle.)
        internal const double ChartFailLengths = 4;
        private bool CloseFailedWay(RoutePoint p)
        {
            if (!InHarbourChart) return false;
            double length = Math.Max(10, water.Length);
            int closed = chart.CloseWay(p, length, ChartFailLengths * length, length);
            if (closed == 0) return false;
            FloodChart();
            refloodWhy = "decision 195: her way out failed, " + closed + " cells of it closed";
            harbourGuideAt = 0;
            ReplanLine("her way out failed here (decision 195)");
            Log.Info("Sailing: " + Name + "'s way out failed here: " + closed + " cells of it closed in her chart and her way reckoned again (decision 195), a slice a frame (decision 198 j).");
            return true;
        }

        // Decision 198 (a, b, c): her line out of her harbour, planned on her chart with her own turning circle (LinePlanner, TurnCircle),
        // a slice a frame (LineBudgetMs), once she is off the dock; her guide a point on it LineLookLengths of her length ahead (or
        // LineLookSeconds of her speed when longer); planned again when she is LineOffShare of her spare off it, steers LineHeadingOff
        // off its way for LineHeadingSeconds, the true wind shifts LineWindShift, or her way out fails (195 b), at most every
        // LineReplanSeconds. No line found: her chart's guide as before (157), the search tried again after LineRetrySeconds; where her
        // own way then fails, the backup tow (198 f, TowWhole).
        internal const double LineLookLengths = 1, LineLookSeconds = 2.5, LineOffShare = 0.5, LineHeadingOff = 15, LineWindShift = 15, LineGuideTolerance = 25;
        private const double LineBudgetMs = 3;
        private const float LineHeadingSeconds = 5, LineReplanSeconds = 10, LineRetrySeconds = 30;
        private TurnCircle turnCircle;
        private LinePlanner linePlanner;
        private List<LinePoint> line;
        private int lineAt;
        private double lineWind = double.NaN;
        private string lineReplanWhy;
        private float lineRetryAt, lineReplanAt, lineHeadingOffSince = -1, lineLogAt;
        internal TurnCircle Circle => turnCircle ?? (turnCircle = TurnCircle.For(RigDescriptor(boat)));
        // Decision 201 (d): her hull's length without her bowsprit, for her circle until it is measured: her stored length
        // (Storage.LengthOf, kept in her booking), else her length as measured.
        private double HullWithoutBowsprit => Record.lengthM > 0 ? Record.lengthM : Math.Max(10, water.Length);
        // Her line as planned (the QA reads it).
        internal IReadOnlyList<LinePoint> Line => line;

        // Every frame (NavigateTick): her line searched a slice at a time once her chart is ready and she is off the dock.
        private void LineStep()
        {
            // Decision 198 (j): a reflood a slice a frame; her line waits for it.
            if (chart != null && chartDone && chart.Flooding)
            {
                floodLongest = Gap(ref floodFrameAt, floodLongest);
                var slice = System.Diagnostics.Stopwatch.StartNew();
                bool flooded = chart.FloodStep(LineBudgetMs);
                floodMs += slice.Elapsed.TotalMilliseconds; floodFrames++;
                if (!flooded) return;
                var p = Point(boat.StablePosition);
                var again = chart.Descent(p, 1e7);
                var middle = chart.Centre(chart.Count / 2 + chart.Width / 2);
                Log.Info("Sailing: " + Name + "'s chart flooded again (" + (refloodWhy ?? "her way reckoned again") + "; " + floodMs.ToString("F0") + " ms over " + floodFrames +
                    " frames, the heap " + Megabytes(floodHeap) + " before, " + Megabytes(GC.GetTotalMemory(false)) + " after, the longest frame " + floodLongest.ToString("F0") + " ms): " +
                    (chartArriving ? "her way in " + (again.Count == 0 ? "doesn't reach her here" : chart.DistanceFrom(p).ToString("F0") + " m by water to her approach point") :
                    "her way out leaves it " + (again.Count == 0 ? "nowhere from here" : NavigationMath.Heading(again[again.Count - 1].x - middle.x, again[again.Count - 1].z - middle.z).ToString("F0") +
                    " degrees from the island's centre, " + chart.DistanceFrom(p).ToString("F0") + " m by water and on")) + ".");
                refloodWhy = null;
            }
            // Decision 198 (g, h): no line while her way is reckoned for the tide or waited for, nor in her final approach; arriving, not
            // until she is within ArrivalLineFrom of her approach point by water.
            if (!InHarbourChart || water == null || !Job.departureComplete || Job.unberthing || backing || Job.dockingPhase > 0 || chart.Needing || tideWait) return;
            if (chartArriving && linePlanner == null && line == null && chart.DistanceFrom(Point(boat.StablePosition)) > ArrivalLineFrom) return;
            if (linePlanner == null)
            {
                if (line != null && lineReplanWhy == null) return;
                if (line == null && lineReplanWhy == null && Time.time < lineRetryAt) return;
                StartLine(lineReplanWhy ?? (lineRetryAt > 0 ? "tried again" : "her chart is ready"));
                return;
            }
            if (!linePlanner.Step(LineBudgetMs)) return;
            var done = linePlanner; linePlanner = null; lineReplanWhy = null;
            if (done.Failed)
            {
                lineRetryAt = Time.time + LineRetrySeconds;
                Log.Info("Sailing: " + Name + " finds no line " + (done.Goal != null ? "in" : "out") + " (decision 198 b: " + done.Why + ", " + done.Milliseconds.ToString("F0") + " ms over the frames)" +
                    (line != null ? "; she keeps the line she has." : "; her chart's guide as before (decision 157), tried again in " + LineRetrySeconds.ToString("F0") + " s."));
                return;
            }
            line = done.Line; lineAt = 0; lineHeadingOffSince = -1; harbourGuideAt = 0;
            // A held cut was measured along the old line (198 d): read again on this one.
            narrowHoldWanted = double.NaN; narrowHoldAlong = double.NaN; narrowAt = 0;
            int turns = 0; double least = double.PositiveInfinity;
            for (int k = 1; k < line.Count; k++) { if (line[k].Turn != 0 && line[k - 1].Turn == 0) turns++; least = Math.Min(least, line[k].Clearance); }
            var last = line[line.Count - 1];
            int lastCell = chart.IndexOf(last.Point);
            Log.Info("Sailing: " + Name + " has her line " + (done.Goal != null ? "in" : "out") + " (decision 198): " + last.Along.ToString("F0") + " m to " +
                (done.Goal != null ? "her approach point (" + RoutePoint.Distance(last.Point, done.Goal.At).ToString("F0") + " m off it, heading " + last.Heading.ToString("F0") + ", her berth's way " + done.Goal.Heading.ToString("F0") + ")" :
                lastCell < 0 || chart.OnEdge(lastCell) ? "her chart's edge" : "open water") +
                ", " + turns + " turns, the least clearance on it " + least.ToString("F0") + " m (her spare " + done.Spare.ToString("F1") + " m), first heading " +
                line[Math.Min(line.Count - 1, 3)].Heading.ToString("F0") + " (" + done.States + " states, " + done.Milliseconds.ToString("F0") + " ms over the frames).");
        }

        private void StartLine(string why)
        {
            var p = Point(boat.StablePosition);
            double length = Math.Max(10, water.Length), half = Math.Max(0.5, water.Radius);
            var c = Circle;
            // The wind she has now, her no-go off it as the flood counted hers.
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            double windNow = wind.sqrMagnitude > 0.01f ? WindFrom() : double.NaN;
            lineWind = windNow;
            // Decision 198 (h): her line in ends at her approach point (her chart's flood goal), heading along her berth.
            var goal = chartArriving && chart.HasGoal ? new LineGoal(chart.FloodGoal, Job.dockHeading, Math.Max(ArrivalGoalLeast, length / 2), ArrivalGoalTolerance) : null;
            linePlanner = new LinePlanner(chart, p, boat.Root.eulerAngles.y, length, half, c.Plan(true, length, HullWithoutBowsprit), c.Plan(false, length, HullWithoutBowsprit),
                windNow, NavigationMath.Finite(windNow) ? chartBeat : double.NaN, goal);
            Log.Info("Sailing: " + Name + " plans her line " + (goal != null ? "in" : "out") + " (decision 198, " + why + "): her circle " + c.Describe(length, HullWithoutBowsprit) + ", her spare " + linePlanner.Spare.ToString("F1") + " m each side.");
        }

        // Her line planned again from where she is (her old line kept meanwhile).
        private void ReplanLine(string why)
        {
            if (!InHarbourChart || Time.time < lineReplanAt) return;
            lineReplanAt = Time.time + LineReplanSeconds;
            lineReplanWhy = why; linePlanner = null;
        }

        // On her line: her nearest point, her guide ahead of it and her way on beyond, and the reasons to plan it again. False past its
        // end (her chart's guide takes her on).
        private bool FollowLine(RoutePoint p, double windFrom)
        {
            int best = lineAt; double off = double.MaxValue;
            for (int k = Math.Max(0, lineAt - 10); k < Math.Min(line.Count, lineAt + 150); k++)
            {
                double d = RoutePoint.Distance(p, line[k].Point);
                if (d < off) { off = d; best = k; }
            }
            lineAt = best;
            var at = line[best];
            if (best >= line.Count - 2)
            {
                line = null; harbourGuideAt = 0;
                Log.Info("Sailing: " + Name + " is at the end of her line (decision 198); her chart's guide takes her on.");
                return false;
            }
            double length = Math.Max(10, water.Length), spare = Math.Max(LinePlanner.TurnSpareLeast, LinePlanner.TurnSpareLengths * length);
            float sog = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            double ahead = Math.Max(LineLookLengths * length, LineLookSeconds * sog);
            int g = best;
            while (g < line.Count - 1 && line[g].Along - at.Along < ahead) g++;
            harbourGuide = line[g].Point;
            harbourOnward.Clear();
            for (int k = g + 4; k < line.Count && line[k].Along - line[g].Along <= ChartReach; k += 4) harbourOnward.Add(line[k].Point);
            // Why plan it again (decision 198 b): off it, steering off its way, or the wind shifted. Not off it on a beat (she tacks across
            // its way), nor while she tacks, wears, gathers way or waits to tack.
            bool beatLeg = NavigationMath.Finite(lineWind) && Math.Abs(NavigationMath.Delta(windFrom, at.Heading)) < chartBeat;
            bool turning = navigator != null && (navigator.State == Manoeuvre.Tacking || navigator.State == Manoeuvre.Wearing || navigator.Gathering || navigator.WaitingToTack);
            double toGuide = NavigationMath.Heading(harbourGuide.x - p.x, harbourGuide.z - p.z), headingOff = Math.Abs(NavigationMath.Delta(boat.Root.eulerAngles.y, toGuide));
            if (headingOff <= LineHeadingOff || beatLeg || turning || sog < 1) lineHeadingOffSince = -1;
            else if (lineHeadingOffSince < 0) lineHeadingOffSince = Time.time;
            string why = null;
            if (!beatLeg && !turning && off > LineOffShare * spare) why = "she is " + off.ToString("F0") + " m off her line";
            else if (lineHeadingOffSince >= 0 && Time.time - lineHeadingOffSince > LineHeadingSeconds) why = "she steers " + headingOff.ToString("F0") + " degrees off her line's way";
            else if (NavigationMath.Finite(lineWind) && Math.Abs(NavigationMath.Delta(lineWind, windFrom)) > LineWindShift) why = "the wind shifted " + Math.Abs(NavigationMath.Delta(lineWind, windFrom)).ToString("F0") + " degrees";
            // Decision 198 (f): a commit point her stopping distance and a length before narrow water on her line (196's: under
            // NarrowLengths of her lengths clear). She goes in on her line (LineOffShare of her spare), her heading within LineHeadingOff
            // of its way, and nothing lying in it (decision 109); else her line is planned again from where she is (the next way at
            // once). Her speed is the held cut's (198 d): a speed she can't shed would only plan the same line again.
            if (why == null && !turning && !beatLeg)
            {
                double before = Math.Max(length, sog * StopSeconds) + length;
                for (int k = best + 1; k < line.Count && line[k].Along - at.Along <= before; k++)
                {
                    if (line[k].Clearance >= ArrivalSail.NarrowLengths * length) continue;
                    if (line[best].Clearance < ArrivalSail.NarrowLengths * length) break;
                    double wayOff = Math.Abs(NavigationMath.Delta(boat.Root.eulerAngles.y, at.Heading));
                    string not = off > LineOffShare * spare ? off.ToString("F0") + " m off her line" :
                        wayOff > LineHeadingOff ? "her heading " + wayOff.ToString("F0") + " degrees off its way" :
                        !LinePartClear(k, 2 * length) ? "something lies in it (" + water.Obstruction + ")" : null;
                    if (not != null) why = "not ready for the narrow water " + (line[k].Along - at.Along).ToString("F0") + " m ahead: " + not;
                    break;
                }
            }
            if (why != null && lineReplanWhy == null && linePlanner == null)
            {
                // A shift past the flood's own (decision 163): her chart flooded again for it first.
                if (!chart.Flooding && NavigationMath.Finite(chart.WindFrom) && Math.Abs(NavigationMath.Delta(chart.WindFrom, windFrom)) > ChartWindShift)
                { FloodChart(); refloodWhy = "decision 163: the wind from " + chart.WindFrom.ToString("F0") + ", on her line"; }
                ReplanLine(why);
            }
            if (Time.time >= lineLogAt)
            {
                lineLogAt = Time.time + 10;
                Log.Info("Sailing: " + Name + " on her line (decision 198): " + at.Along.ToString("F0") + " of " + line[line.Count - 1].Along.ToString("F0") + " m, " + off.ToString("F1") +
                    " m off it, its way " + at.Heading.ToString("F0") + ", her guide " + RoutePoint.Distance(p, harbourGuide).ToString("F0") + " m ahead at " + toGuide.ToString("F0") +
                    ", the water there " + at.Clearance.ToString("F0") + " m clear" + (why != null ? "; planned again: " + why : "") + ".");
            }
            return true;
        }

        // Her line from point `from` on for `metres` clear of what lies there (her hull swept, the seabed, a boat lying still: decision
        // 109), for the commit point (198 f).
        private bool LinePartClear(int from, double metres)
        {
            var a = line[from];
            for (int k = from + 4; k < line.Count; k += 4)
            {
                if (line[k].Along - line[from].Along > metres) break;
                if (!water.Clear(a.Point, line[k].Point)) return false;
                a = line[k];
            }
            return true;
        }

        // Her line's next `metres` clear (her beam swept and the seabed, as her chart judged its cells): her way while she is on it,
        // whatever her heading (decision 198 e).
        private bool LineAheadClear(float metres)
        {
            if (line == null) return false;
            var from = line[lineAt];
            for (int k = lineAt + 4; k < line.Count; k += 4)
            {
                if (line[k].Along - line[lineAt].Along > metres) break;
                if (!water.ClearBeam(from.Point, line[k].Point)) return false;
                from = line[k];
            }
            return true;
        }

        // Decision 198 (i) (the user, 2026-10-12: "push to the middle of waterway only apply when push one ship length is not
        // possible"): how far she is pushed off the dock: her own length (decision 192) where her hull pushed that far straight out is
        // clear at her bow, middle and stern; else to the middle of the waterway off the dock: along her push, out to PushMiddleReach
        // or until her hull would meet what it doesn't fit, where her chart's clearance at her bow, middle and stern is most (measured
        // 2026-10-12: Kicia Bay's berth lies in an 18 m slip; the town quay at Chronos had 60-70 m for the Leopard's 59). Her own
        // length without a chart.
        internal const double PushMiddleReach = 120, PushLeast = 2;
        private double PushDistance(RoutePoint p, double heading, double pushBearing, out string how)
        {
            double full = Unberthing.PushOff(water.Length), end = Math.Max(0, water.Length / 2 - water.Radius);
            how = "her length";
            var along = new[] { 0.0, end, -end };
            bool clear = true;
            foreach (double a in along)
            {
                var from = Ahead(p, heading, a);
                if (!water.ClearHull(from, Ahead(from, pushBearing, full))) { clear = false; break; }
            }
            if (clear || chart == null || !chart.Flooded) return full;
            double best = double.NegativeInfinity, bestAt = Math.Min(full, PushLeast);
            for (double d = PushLeast; d <= PushMiddleReach; d += 2)
            {
                double least = double.PositiveInfinity;
                foreach (double a in along)
                {
                    int cell = chart.IndexOf(Ahead(Ahead(p, heading, a), pushBearing, d));
                    least = Math.Min(least, cell < 0 ? PushMiddleReach : chart.Water(cell) ? chart.ClearanceAt(cell) : -1);
                }
                if (least < 0) break;
                if (least > best) { best = least; bestAt = d; }
            }
            how = "the middle of the waterway, her length not clear (decision 198 i)";
            return bestAt;
        }

        // Her turning circle for a check (decision 198 a): measured, the larger way's tightest. Decision 209 (b): until measured, decision
        // 201 (d)'s, twice her hull's length without her bowsprit, at any speed: never 169's rule (her speed over 5 degrees a second),
        // which made a slow ship's circle look small (the Brig turns on 54-61 m from 0.8 to 4 m/s, the Sanbuq wider when slow).
        private double MyTurnRadius()
        {
            double length = Math.Max(10, water.Length);
            var c = Circle;
            return c.Measured(true) || c.Measured(false) ? c.PlanEither(length, HullWithoutBowsprit) * TurnCircle.ReserveRate : TurnCircle.DefaultLengths * HullWithoutBowsprit;
        }

        // Decision 196 (ArrivalSail.NarrowSpeed, TurnAhead): her sail capped in narrow water only for a turn she knows is coming (the
        // user: "make it so the speed limit only apply to turns, if there is no turns it can keep high speed. Docking also should apply
        // this"): on her way within her look-ahead (NarrowAheadLengths of her lengths, or NarrowLookSeconds of her speed when longer),
        // the clearance at and before the turn sets the speed she should have. Her way: leaving, her harbour chart's way down the water
        // (its clearance); arriving (docking, or within RouteHarbour of her destination berth), her route to it (the water probed round
        // the turn). None while she waits to tack, tacks or gathers way (a tack needs its speed). Read every NarrowEvery seconds.
        // Decision 209 (a): NarrowLookSeconds 30 (was 20), so she sheds her speed before the turn, not at it (Claude's number).
        internal const double NarrowAheadLengths = 3, NarrowLookSeconds = 30;
        private const float NarrowEvery = 1;
        private float narrowAt, narrowShare = 1, narrowNoteAt;
        // Narrow water along her way (NarrowSailLimit: under NarrowLengths of her lengths of clearance within her look-ahead, turn or
        // none): she keeps her helm (Voyage.UpdateHelmHold), never lets it go to the game's rudder (decision 132). Run 20261012-125559:
        // let go mid-turn, the rudder eased from 0.43 to 0.31 and her turn died 13 m short of Cube_005; run 20261012-130913, a turn only
        // kept it: let go before a 20-degree bend in Dragon Cliffs' 26 m eastern gap, she drifted onto Cube_005 at 2.6 m/s.
        private bool narrowTurn;
        // The turn lead (ArrivalSail.TurnLead): her way's heading round the turn once she is within its lead; NaN when none.
        private double narrowLead = double.NaN;
        private float narrowLeadNoteAt, narrowLeadAt;
        private float NarrowSailLimit()
        {
            if (boat?.Body == null || water == null) return 1;
            if (navigator != null && (navigator.WaitingToTack || navigator.State == Manoeuvre.Tacking || navigator.Gathering)) return 1;
            if (Time.time < narrowAt) return narrowShare;
            narrowAt = Time.time + NarrowEvery;
            narrowShare = 1; narrowTurn = false; narrowLead = double.NaN;
            var p = Point(boat.StablePosition);
            double length = Math.Max(10, water.Length);
            float sog = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            double look = Math.Max(NarrowAheadLengths * length, sog * NarrowLookSeconds);
            var way = new List<RoutePoint> { p };
            bool charted = InHarbourChart;
            // Decision 198 (d): on her line, its turns.
            if (charted && line != null)
            {
                for (int k = lineAt + 1; k < line.Count && line[k].Along - line[lineAt].Along <= look + ArrivalSail.TurnWindowChords * length; k++) way.Add(line[k].Point);
            }
            else if (charted) way.AddRange(chart.Descent(p, look + ArrivalSail.TurnWindowChords * length));
            else if (Job.dockingPhase > 0 || Job.departureComplete && DistanceToTarget() < RouteHarbour)
            {
                for (int k = Math.Max(0, Job.waypoint); k < Job.route.Length; k++) way.Add(Job.route[k]);
                way.Add(NavigationDestination());
            }
            else return Held(sog);
            double alongClear = double.PositiveInfinity;
            if (charted)
            {
                foreach (var c in way)
                {
                    if (RoutePoint.Distance(p, c) > look) break;
                    alongClear = Math.Min(alongClear, chart.ClearanceAt(chart.Start(c)));
                }
            }
            else alongClear = ProbedClearance(p, ArrivalSail.NarrowLengths * length);
            narrowTurn = alongClear < ArrivalSail.NarrowLengths * length;
            // Decision 209 (a): every turn, whatever its angle (ArrivalSail.AnyTurnAngle). Her heading off her way counts as a turn now
            // only off a beat (her chart's beat, decision 193): on a beat she tacks up her way, never turns onto it.
            double angle = ArrivalSail.AnyTurnAngle, headingNow = boat.Root.eulerAngles.y;
            int first = way.FindIndex(c => RoutePoint.Distance(p, c) >= length / 2);
            if (first >= 0 && Math.Abs(NavigationMath.Delta(WindFrom(), NavigationMath.Heading(way[first].x - p.x, way[first].z - p.z))) < chartBeat)
                headingNow = double.NaN;
            if (!ArrivalSail.TurnAhead(way, headingNow, length, look, angle, out double along, out RoutePoint at, out double after)) return Held(sog);
            double clear = double.PositiveInfinity;
            if (charted)
            {
                foreach (var c in way)
                {
                    if (RoutePoint.Distance(p, c) > along + 2 * length) break;
                    clear = Math.Min(clear, chart.ClearanceAt(chart.Start(c)));
                }
            }
            else clear = ProbedClearance(at, 2 * length);
            if (double.IsNaN(clear)) return Held(sog);
            double wanted = ArrivalSail.NarrowSpeed(clear, length);
            if (!double.IsPositiveInfinity(wanted) && NavigationMath.Finite(after))
            {
                double turn = NavigationMath.Delta(boat.Root.eulerAngles.y, after);
                double lead = ArrivalSail.TurnLead(turn, MyTurnRadius(), length);
                if (along <= lead && Math.Abs(turn) >= angle)
                {
                    narrowLead = after; narrowLeadAt = Time.time;
                    if (Time.time >= narrowLeadNoteAt)
                    {
                        narrowLeadNoteAt = Time.time + 10;
                        Log.Info("Sailing: " + Name + " starts her turn to " + after.ToString("F0") + " (" + Math.Abs(turn).ToString("F0") + " degrees) " + along.ToString("F0") +
                            " m before it in narrow water, her lead " + lead.ToString("F0") + " m at " + sog.ToString("F1") + " m/s (decision 196).");
                    }
                }
            }
            if (double.IsPositiveInfinity(wanted)) return Held(sog);
            // Decision 198 (d): held until she is round it (the turn's end, TurnWindowChords of her length past where it begins).
            double end = along + ArrivalSail.TurnWindowChords * length;
            bool holding = NavigationMath.Finite(narrowHoldWanted);
            narrowHoldWanted = holding ? Math.Min(narrowHoldWanted, wanted) : wanted;
            if (line != null) narrowHoldAlong = Math.Max(holding && NavigationMath.Finite(narrowHoldAlong) ? narrowHoldAlong : 0, line[lineAt].Along + end);
            else { narrowHoldAlong = double.NaN; narrowHoldUntil = Mathf.Max(holding ? narrowHoldUntil : 0, Time.time + (float)Math.Max(15, end / Math.Max(1, sog))); }
            narrowShare = (float)(narrowCut = ArrivalSail.NextCut(narrowCut, sog, narrowHoldWanted));
            if (narrowShare < 1 && Time.time >= narrowNoteAt)
            {
                narrowNoteAt = Time.time + 20;
                Log.Info("Sailing: " + Name + " slows for a turn " + along.ToString("F0") + " m ahead in narrow water (decisions 196, 209): " + clear.ToString("F0") + " m of clearance (her length " +
                    length.ToString("F0") + " m) at " + sog.ToString("F1") + " m/s, wanted " + wanted.ToString("F1") + "; her sail at most " + narrowShare.ToString("P0") + ".");
            }
            return narrowShare;
        }

        // Decision 198 (d): her cut held until she is round the turn it was for (on her line by its distance, else for the time the turn
        // takes), however the turn reads meanwhile (run 20261012-133446: the cut came and went every few seconds and her sails were full
        // for the last 16 s before Dragon Cliffs' gap). 1 when nothing is held.
        private double narrowHoldWanted = double.NaN, narrowHoldAlong = double.NaN;
        private float narrowHoldUntil;
        private float Held(float sog)
        {
            bool holding = NavigationMath.Finite(narrowHoldWanted) &&
                (line != null && NavigationMath.Finite(narrowHoldAlong) ? line[lineAt].Along < narrowHoldAlong : Time.time < narrowHoldUntil);
            if (!holding) { narrowHoldWanted = double.NaN; narrowHoldAlong = double.NaN; narrowCut = 1; return 1; }
            narrowTurn = true;
            narrowShare = (float)(narrowCut = ArrivalSail.NextCut(narrowCut, sog, narrowHoldWanted));
            return narrowShare;
        }
        // Decision 209 (a): the cut held now, stepped each NarrowEvery toward the speed wanted (ArrivalSail.NextCut); 1 when none.
        private double narrowCut = 1;

        // The clearance round `at` without a chart: in 8 directions, the nearest her hull meets something, every half of her radius out
        // to `most`.
        private double ProbedClearance(RoutePoint at, double most)
        {
            double step = Math.Max(4, water.Radius), nearest = most;
            for (int d = 0; d < 8; d++)
                for (double r = step; r <= nearest; r += step)
                    if (!water.ClearHull(at, Ahead(at, d * 45, (float)r))) { nearest = Math.Min(nearest, r - step); break; }
            return nearest >= most ? double.PositiveInfinity : nearest;
        }

        // Her guide point: in her harbour's chart its guide, else her route's.
        private RoutePoint GuidePoint() => InHarbourChart ? harbourGuide : Job.route[Job.waypoint];
    }

    // ==== VoyageDocking ====

    // Adapted from the Ship Transport Prototype's TransportDocking (ChatGPT "Astra"'s work for the user, handed over 2026-10-06;
    // decisions 92-93). Berths are the ports' service berths (decision 129: the user's custom berths), found by Shipwright's
    // shipyard names; the origin mooring after a booking is new.

    // The service berth of a port (decision 129): the port's custom berth, alongside its two cleats (CustomBerths; since decision 217
    // the shipyard's own cleats beside its release spot at the four vanilla yards, the shipyard closed while she waits, 218), where she
    // lies by her own beam; where those cleats aren't found, the game's recovery berth there (RecoveryPort: where its rescue moors the
    // player's ship, decision 100), with its boat place and its two cleats. Read live from the cleats, so ScrambledSeas moves it with
    // its island.
    internal sealed class ServiceBerth
    {
        internal readonly string Port;
        private readonly CustomBerthSpec spec;
        private readonly GPButtonDockMooring stern, bow, extra;
        private readonly RecoveryPort recovery;

        // `extra`: a big ship's third cleat (CustomBerths.Long), beyond her bow or stern cleat; null for every other hull.
        internal ServiceBerth(string port, CustomBerthSpec spec, GPButtonDockMooring stern, GPButtonDockMooring bow, GPButtonDockMooring extra = null)
        {
            Port = port; this.spec = spec; this.stern = stern; this.bow = bow; this.extra = extra;
        }

        internal ServiceBerth(string port, RecoveryPort recovery) { Port = port; this.recovery = recovery; }

        internal bool Custom => spec != null;
        // The shipyard's own cleats beside its release spot (decision 217).
        internal bool AtShipyard => Custom && spec.AtShipyard;
        internal bool Long => Custom && extra;
        internal bool Valid => Custom ? stern && bow && stern.spring && bow.spring && (!extra || extra.spring) : recovery && recovery.boatPos;
        public static implicit operator bool(ServiceBerth berth) => berth != null && berth.Valid;

        // The berth's two ends: its stern and bow cleats, or for a big ship the extra cleat at the end it lies beyond.
        private GPButtonDockMooring SternEnd => Long && !spec.ExtraBeyondBow ? extra : stern;
        private GPButtonDockMooring BowEnd => Long && spec.ExtraBeyondBow ? extra : bow;

        // Her heading there, in degrees.
        internal float Heading => Custom ? (float)CustomBerths.Heading(Flat(SternEnd.transform.position), Flat(BowEnd.transform.position)) : recovery.boatPos.eulerAngles.y;
        internal Quaternion Rotation => Quaternion.Euler(0f, Heading, 0f);

        // Where her middle lies there, in the game's shifting coordinates, for a hull of this half-beam (the recovery berth has
        // one place for every hull): abreast of the middle of the berth's two ends. At the sea's surface.
        internal Vector3 Place(float halfBeam)
        {
            if (!Custom) return recovery.boatPos.position;
            var p = CustomBerths.Place(Flat(SternEnd.transform.position), Flat(BowEnd.transform.position), spec.WaterSide, halfBeam);
            return new Vector3(p.x, 0f, p.z);
        }

        internal Vector3 StablePlace(float halfBeam) => FloatingOriginManager.instance.ShiftingPosToRealPos(Place(halfBeam));

        // Its mooring cleats (those there), the bow's first; a big ship's third with them.
        internal GPButtonDockMooring[] Cleats =>
            Custom ? new[] { bow, stern, extra }.Where(d => d && d.spring).ToArray()
                : new[] { recovery.mooringFront, recovery.mooringBack }.Where(t => t).Select(t => t.GetComponent<GPButtonDockMooring>()).Where(d => d && d.spring).ToArray();

        internal string Describe() => Custom ? (AtShipyard ? "the shipyard's cleats at " : "the custom berth at ") + Port + " (" + spec.SternCleat + " and " + spec.BowCleat +
            (Long ? ", and " + spec.ExtraCleat + " beyond her " + (spec.ExtraBeyondBow ? "bow" : "stern") + " for a big ship" : "") + ")" : "the recovery berth at " + Port;

        private static RoutePoint Flat(Vector3 v) => new RoutePoint(v.x, v.z);
    }

    internal static class Berths
    {
        // Where no hull is measured: about a Dhow's.
        internal const float DefaultHalfBeam = 2f;

        // The loaded shipyard Shipwright knows by this name (its island is loaded), or null.
        internal static Shipyard Yard(string name)
        {
            foreach (var yard in Shipyards.Loaded())
                if (yard && Shipyards.InfoOf(yard)?.Name == name) return yard;
            return null;
        }

        // The ports whose picked cleats weren't found, logged once each.
        private static readonly HashSet<string> fellBack = new HashSet<string>(), noExtra = new HashSet<string>();

        // The service berth of a shipyard's port, or null when it has none. The custom berths' cleats are in the world scene
        // (level24, under their islands, which stay in it while their scenery loads and unloads), found by their path under
        // `_shifting world` (the FloatingOriginManager's object); Transform.Find finds them while their island is switched off.
        // `lengthM`: the ship's stored length (StoredShip/TransportRecord.lengthM); a big ship's (CustomBerths.Long) takes the berth's
        // third cleat too, where it is found (else the two, logged once).
        internal static ServiceBerth Service(string shipyard, float lengthM = 0f)
        {
            var spec = CustomBerths.Of(shipyard);
            var world = FloatingOriginManager.instance ? FloatingOriginManager.instance.transform : null;
            if (spec != null && world)
            {
                var stern = Cleat(world, spec.Island, spec.SternCleat);
                var bow = Cleat(world, spec.Island, spec.BowCleat);
                GPButtonDockMooring extra = null;
                if (stern && bow && spec.ExtraCleat != null && CustomBerths.Long(lengthM))
                {
                    extra = Cleat(world, spec.Island, spec.ExtraCleat);
                    if (!extra && noExtra.Add(shipyard))
                        Log.Warn("Sailing: the big ships' third cleat at " + shipyard + " (" + spec.ExtraCleat + ") isn't in the game; they lie on its two.");
                }
                if (stern && bow) { fellBack.Remove(shipyard); return new ServiceBerth(shipyard, spec, stern, bow, extra); }
                if (fellBack.Add(shipyard))
                    Log.Warn("Sailing: the custom berth at " + shipyard + " isn't in the game (" + spec.Island + ": " + (stern ? "" : spec.SternCleat + " missing ") +
                        (bow ? "" : spec.BowCleat + " missing") + "); its recovery berth is used (decision 129).");
            }
            var recovery = RecoveryBerth(shipyard);
            return recovery ? new ServiceBerth(shipyard, recovery) : null;
        }

        private static GPButtonDockMooring Cleat(Transform world, string island, string name)
        {
            var t = world.Find(island + "/" + name);
            var cleat = t ? t.GetComponent<GPButtonDockMooring>() : null;
            return cleat && cleat.spring ? cleat : null;
        }

        // The game's recovery berth at the port, registered at its start (RecoveryPort.Start); each of the four shipyards' ports
        // has one (review N7, from the game's scene data).
        private static RecoveryPort RecoveryBerth(string shipyard)
        {
            var port = Ports.ByName(shipyard);
            if (!port) return null;
            RecoveryPort berth;
            // Recovery.ports is null until the first berth registers.
            try { berth = Recovery.GetPort(port.portIndex); }
            catch (Exception) { return null; }
            return berth && berth.boatPos ? berth : null;
        }

        // As the game's rescue moors a ship at its berth (Recovery.RecoverBoat): the coiled line nearest each cleat is laid on it.
        internal static void MoorAtService(BoatMooringRopes ropes, ServiceBerth berth)
        {
            if (berth) foreach (var cleat in berth.Cleats) ropes.MoorClosestRope(cleat.transform);
        }

        // How far her hull reaches from her origin, ahead, astern and to each side (NavigationWater's measured envelope).
        internal struct Footprint
        {
            internal float Fore, Aft, Side;
            internal Footprint(NavigationWater water) { Fore = water.ForeExtent; Aft = water.AftExtent; Side = water.Radius; }
        }

        internal const float FootprintEndMargin = 3f, FootprintSideMargin = 1.5f, FootprintBelow = 3f, FootprintAbove = 4f;

        // The box the berth must keep clear, in the berth's axes from its boat place: her hull plus 3 m at each end and 1.5 m
        // each side, from 3 m under the place to 4 m over it.
        internal static void FootprintBox(Footprint her, out Vector3 centre, out Vector3 half)
        {
            centre = new Vector3(0f, (FootprintAbove - FootprintBelow) / 2f, (her.Fore - her.Aft) / 2f);
            half = new Vector3(her.Side + FootprintSideMargin, (FootprintAbove + FootprintBelow) / 2f, (her.Fore + her.Aft) / 2f + FootprintEndMargin);
        }

        private static readonly Collider[] footprintHits = new Collider[256];

        // Another boat at a berth (D2, D3; decision 107, review N2): any boat whose own colliders reach into her footprint there
        // (FootprintBox), wherever its centre lies, but `except` (her own) and houses. Docks, items and lines don't count. By the
        // distance of a boat's centre (review M3's max(15 m, 1.5 lengths)) the ships for sale beside Gold Rock City's and Fort
        // Aestrin's berths took them for good, though neither lies on them. `place` is in shifting coordinates.
        internal static PurchasableBoat Occupant(Vector3 place, float yaw, Footprint her, Transform except)
        {
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            FootprintBox(her, out var centre, out var half);
            int count = Physics.OverlapBoxNonAlloc(place + rotation * centre, half, footprintHits, rotation, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var boat = footprintHits[i] ? footprintHits[i].GetComponentInParent<PurchasableBoat>() : null;
                if (!boat || boat.isHouse || !boat.gameObject.activeInHierarchy || (except && boat.transform == except)) continue;
                return boat;
            }
            return null;
        }
    }

    // Decision 218 as the user changed it (2026-11-04: "as long as ship don't occupied shipyard anymore you should be able to do all
    // these, not 250m"): whether her hull still lies in a shipyard. The shipyard is the game's own area, the trigger box on the Shipyard
    // whose boats it counts (Shipyard.OnTriggerEnter, boatsInTrigger; SailingPatches.ShipyardAreaPrefix keeps her out of that list, so
    // her hull is measured against the box), and its release spot, where the game puts every ship it lets out (Shipyard.DischargeShip;
    // Dragon Cliffs' spot lies 6 m beyond its box, the scene files read 2026-11-04). Flat boxes on the water.
    internal static class ShipyardArea
    {
        // The water a ship let out at the release spot may take: the Baghlah's, the longest vanilla hull (36.3 m with her bowsprit,
        // half-beam 2.85 m; the charting run's hulls.txt), centred on the spot, with the berth footprint's margins.
        internal const float ReleaseHalfLength = 36.3f / 2 + Berths.FootprintEndMargin, ReleaseHalfBeam = 2.85f + Berths.FootprintSideMargin;

        // A box on the water: its middle (x, z), its heading (degrees, the game's yaw) and its half-sizes along and across that heading.
        internal struct Box
        {
            internal double X, Z, Heading, HalfAlong, HalfAcross;
            internal Box(double x, double z, double heading, double halfAlong, double halfAcross)
            { X = x; Z = z; Heading = heading; HalfAlong = halfAlong; HalfAcross = halfAcross; }
        }

        // Her hull, from her origin at (x, z) with her heading: Aft astern to Fore ahead, Side each side (Berths.Footprint).
        internal static Box Hull(double x, double z, double heading, Berths.Footprint her)
        {
            double r = heading * Math.PI / 180, shift = (her.Fore - her.Aft) / 2;
            return new Box(x + Math.Sin(r) * shift, z + Math.Cos(r) * shift, heading, (her.Fore + her.Aft) / 2, her.Side);
        }

        // Whether two boxes overlap (no axis of either separates them).
        internal static bool Overlap(Box a, Box b)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z;
            foreach (var axis in new[] { a.Heading, a.Heading + 90, b.Heading, b.Heading + 90 })
            {
                double r = axis * Math.PI / 180, ax = Math.Sin(r), az = Math.Cos(r);
                if (Math.Abs(dx * ax + dz * az) > Reach(a, ax, az) + Reach(b, ax, az)) return false;
            }
            return true;
        }

        private static double Reach(Box box, double ax, double az)
        {
            double r = box.Heading * Math.PI / 180, alongX = Math.Sin(r), alongZ = Math.Cos(r);
            return box.HalfAlong * Math.Abs(alongX * ax + alongZ * az) + box.HalfAcross * Math.Abs(alongZ * ax - alongX * az);
        }

        // The release spot's water (ReleaseHalfLength, ReleaseHalfBeam), its middle at (x, z) along its heading.
        internal static Box ReleaseSpot(double x, double z, double heading) => new Box(x, z, heading, ReleaseHalfLength, ReleaseHalfBeam);

        // Whether her hull (in the game's shifting coordinates) lies in this shipyard's area or on its release spot.
        internal static bool Holds(Shipyard yard, Box hull)
        {
            if (!yard) return false;
            foreach (var box in yard.GetComponents<BoxCollider>())
            {
                if (!box || !box.enabled || !box.isTrigger) continue;
                var t = box.transform;
                var middle = t.TransformPoint(box.center);
                var size = Vector3.Scale(box.size, t.lossyScale);
                if (Overlap(hull, new Box(middle.x, middle.z, t.eulerAngles.y, Math.Abs(size.z) / 2, Math.Abs(size.x) / 2))) return true;
            }
            var spot = yard.shipReleasePosition;
            return spot && Overlap(hull, ReleaseSpot(spot.position.x, spot.position.z, spot.eulerAngles.y));
        }
    }

    // What her hull touches while the harbour's tow has her (the user, 2026-10-10, run 20261010-020912: the Cog held at 0.03 m/s
    // under 19-27 kN, nothing under her keel nor in her hull's box below the waterline): every collider touching her body, with
    // its layer and parents, the last contact point in her frame (right, up, ahead), its normal, the strongest impulse.
    internal sealed class TowContacts : MonoBehaviour
    {
        private sealed class Touch { internal string What; internal float Last, Impulse, Speed; internal Vector3 Point, Normal; internal int Count; }
        private readonly Dictionary<Collider, Touch> touched = new Dictionary<Collider, Touch>();

        private void OnCollisionEnter(Collision c) => Note(c);
        private void OnCollisionStay(Collision c) => Note(c);

        private void Note(Collision c)
        {
            if (!c.collider || c.collider.GetComponentInParent<ShipItem>()) return;
            if (!touched.TryGetValue(c.collider, out var t))
            {
                var p = c.collider.transform.parent;
                touched[c.collider] = t = new Touch { What = c.collider.name + " (" + c.collider.GetType().Name + ", layer " + LayerMask.LayerToName(c.collider.gameObject.layer) +
                    (p ? ", under " + p.name + (p.parent ? " < " + p.parent.name : "") : "") + ")" };
            }
            t.Last = Time.time; t.Count++;
            t.Impulse = Mathf.Max(t.Impulse, c.impulse.magnitude);
            t.Speed = Mathf.Max(t.Speed, c.relativeVelocity.magnitude);
            if (c.contactCount > 0)
            {
                var contact = c.GetContact(0);
                t.Point = transform.InverseTransformPoint(contact.point);
                t.Normal = transform.InverseTransformDirection(contact.normal);
            }
        }

        // What touched her in the last `seconds`, the most pressing first.
        internal string Report(float seconds)
        {
            var recent = touched.Where(p => p.Key && Time.time - p.Value.Last <= seconds).Select(p => p.Value).OrderByDescending(t => t.Impulse).Take(6).ToArray();
            return recent.Length == 0 ? "nothing in the last " + seconds.ToString("F0") + " s" : string.Join("; ", recent.Select(t => t.What + " at " + t.Point.ToString("F1") +
                " in her frame (right, up, ahead), normal " + t.Normal.ToString("F2") + ", impulse up to " + t.Impulse.ToString("F0") + " N·s, " + t.Speed.ToString("F2") +
                " m/s, " + t.Count + " frames").ToArray());
        }
    }

    internal sealed partial class Voyage
    {
        private float arrivalSailNoteAt, approachBlockedSince = -1;
        private const float ApproachBlockedFor = 20;
        // Her half-beam, for where she lies at a custom berth (decision 129): her hull's measured clearance half-width.
        private float HalfBeam => water != null ? water.Radius : Berths.DefaultHalfBeam;
        // Her origin's service berth, looked up at most every OriginBerthSeconds (the review of 2026-10-11 night): Berths.Service finds
        // the cleats by their paths each time, and the tow in asked every physics step, "in port" every half second. The berth's place
        // is still read live from its cleats (ServiceBerth.Place).
        private ServiceBerth originBerth;
        private float originBerthAt = float.NegativeInfinity;
        private const float OriginBerthSeconds = 2f;
        private ServiceBerth OriginBerth()
        {
            if (Time.time >= originBerthAt || originBerth != null && !originBerth.Valid)
            { originBerth = Berths.Service(Record.from, Record.lengthM); originBerthAt = Time.time + OriginBerthSeconds; }
            return originBerth;
        }
        // Decision 203: her destination shipyard's release spot, read live from the shipyard (ReadArrivalSpot), and Kicia Bay's tow start
        // (ReleaseSpots); `noArrivalSpot` where the shipyard has none (none in the game).
        private RoutePoint arrivalSpot;
        private double arrivalYaw;
        private bool arrivalSpotKnown, noArrivalSpot;
        private TowStart towStart;
        private float dockScanAt, dockSettledAt = -1, dockStartedAt = -1, berthScanAt, heldSince = -1;
        private double occupiedSince = -1, heldHeading = double.NaN;
        private bool berthStayedTaken;
        private static readonly System.Reflection.FieldInfo RopeSpring = AccessTools.Field(typeof(PickupableBoatMooringRope), "mooredToSpring");

        internal static bool MooringBindingsReady => RopeSpring != null;
        private static readonly System.Reflection.FieldInfo RopeLengthSquared = AccessTools.Field(typeof(PickupableBoatMooringRope), "currentRopeLengthSquared");

        // Decision 113 (the user, 2026-10-07): tied at the berth, her lines get 2.5 m of slack. The game fixes a line at the distance
        // when it is tied (MoorTo) and again when a frozen ship turns physical; that short, they swung the Dhow onto Dragon Cliffs'
        // pier and laid her over until her yard lay on its boardwalk (launch 5, runs 20261007-080048 to -083924). Kept: a line the
        // game shortens again gets its slack back.
        internal const float LineSlack = 2.5f;
        private readonly Dictionary<PickupableBoatMooringRope, float> slackLengths = new Dictionary<PickupableBoatMooringRope, float>();

        // Springs given their slack the moment the booking tied them (SlackenAtTie): the waiting tick keeps that length.
        private static readonly HashSet<SpringJoint> slackenedAtTie = new HashSet<SpringJoint>();

        // At the booking, in the same frame as her lines are laid (candidate AH, run 20261007-173004: tied at their full length,
        // "3.7 of 3.7 m", until the waiting tick slackened them, they pulled her toward cleats up on the boardwalk; she struck it
        // at 1.9 m/s and rolled to 86 degrees within 2 s, her sail furled).
        internal static void SlackenAtTie(BoatMooringRopes ropes)
        {
            if (!ropes || RopeLengthSquared == null || RopeSpring == null) return;
            slackenedAtTie.RemoveWhere(s => !s);
            foreach (var rope in ropes.ropes)
            {
                var spring = rope && rope.IsMoored() ? RopeSpring.GetValue(rope) as SpringJoint : null;
                if (!spring || slackenedAtTie.Contains(spring)) continue;
                float length = spring.maxDistance + LineSlack;
                RopeLengthSquared.SetValue(rope, length * length); spring.maxDistance = length;
                slackenedAtTie.Add(spring);
            }
        }

        private void SlackenLines()
        {
            if (RopeLengthSquared == null || boat.Body.isKinematic) return;
            foreach (var rope in boat.Moorings.ropes)
            {
                if (!rope) continue;
                var spring = rope.IsMoored() ? RopeSpring.GetValue(rope) as SpringJoint : null;
                if (!spring) { slackLengths.Remove(rope); continue; }
                if (!slackLengths.TryGetValue(rope, out float length))
                {
                    length = spring.maxDistance + (slackenedAtTie.Remove(spring) ? 0f : LineSlack); slackLengths[rope] = length;
                    Log.Info("Sailing: " + Name + "'s line " + rope.name + " gets " + LineSlack + " m of slack (" + spring.maxDistance.ToString("F1") + " to " + length.ToString("F1") + " m).");
                }
                if (spring.maxDistance < length - 0.05f) { RopeLengthSquared.SetValue(rope, length * length); spring.maxDistance = length; }
            }
        }

        // ---- Arriving: the destination shipyard's release spot (decision 203) ----

        // The destination's shipyard loaded (its island is): her target is its release spot (where the game puts every ship leaving the
        // shipyard), or at Kicia Bay her tow start outside its channel (ReleaseSpots), learned once and read live again while she comes in
        // (ResolveArrivalSpot). The service berth is for departures only. A shipyard without a release spot (none in the game): her
        // route's end, and storage from where she is (NavigateDocking).
        private bool TryResolveDestinationBerth()
        {
            if (Job.berthKnown) return true;
            if (Time.time < berthScanAt) return false;
            berthScanAt = Time.time + 1;
            if (!ReadArrivalSpot()) return false;
            if (noArrivalSpot) { Job.destination = Job.route[Job.route.Length - 1]; Job.dockHeading = 0; }
            else { Job.destination = ArrivalTarget; Job.dockHeading = (float)ArrivalTargetHeading; }
            // Decision 222: her anchor spot, chosen once (of Gold Rock City's two, the nearer her now).
            if (!noArrivalSpot && !Job.anchorKnown)
            {
                var anchor = ReleaseSpots.AnchorFor(Record.to, arrivalSpot, arrivalYaw, Point(boat.StablePosition));
                if (anchor.HasValue) { Job.anchor = anchor.Value; Job.anchorKnown = true; }
            }
            Job.berthKnown = true; ++Job.revision;
            Log.Info("Sailing: " + Name + ": her arrival at " + Record.to + " is known (decision 203): " + DescribeArrival() +
                (Job.anchorKnown ? "; first she anchors at (" + At(Job.anchor) + "), " + RoutePoint.Distance(Job.anchor, arrivalSpot).ToString("F0") + " m from the release spot, until her harbour is charted (decision 222)" : "") + ".");
            return true;
        }

        private string DescribeArrival() => noArrivalSpot ? "its shipyard has no release spot; she goes into storage at her route's end" :
            towStart == null ? "the shipyard's release spot at (" + At(arrivalSpot) + "), its heading " + arrivalYaw.ToString("F0") + " (she may lie either way round, decision 143)" :
            "Kicia Bay's tow start, " + towStart.Name + ", at (" + At(ArrivalTarget) + "), her bow " + ArrivalTargetHeading.ToString("F0") + " there; from it the tow takes her to the " +
            "shipyard's release spot at (" + At(arrivalSpot) + ")";

        private static string At(RoutePoint p) => p.x.ToString("F0") + ", " + p.z.ToString("F0");

        // The destination's release spot from its live shipyard (stable coordinates; ScrambledSeas moves islands, so never from a table);
        // false while the shipyard isn't loaded.
        private bool ReadArrivalSpot()
        {
            var yard = Berths.Yard(Record.to);
            if (!yard || !FloatingOriginManager.instance) return false;
            var release = yard.shipReleasePosition;
            noArrivalSpot = !release; towStart = null;
            if (noArrivalSpot) return true;
            arrivalSpot = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(release.position));
            arrivalYaw = NavigationMath.Normalize(release.eulerAngles.y);
            towStart = ReleaseSpots.TowStartFor(Record.to, Record.lengthM);
            return true;
        }

        // The origin shipyard's release spot from its live shipyard (stable coordinates), as ReadArrivalSpot reads the destination's;
        // false while the shipyard isn't loaded or has none.
        private bool ReadOriginSpot(out RoutePoint spot, out double yaw)
        {
            spot = default(RoutePoint); yaw = 0;
            var yard = Berths.Yard(Record.from);
            var release = yard ? yard.shipReleasePosition : null;
            if (!release || !FloatingOriginManager.instance) return false;
            spot = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(release.position));
            yaw = NavigationMath.Normalize(release.eulerAngles.y);
            return true;
        }

        // Her target on her way in: the release spot, or Kicia Bay's tow start, with the heading she lies at there.
        private RoutePoint ArrivalTarget => towStart == null ? arrivalSpot : ReleaseSpots.InAxes(arrivalSpot, arrivalYaw, towStart.X, towStart.Z);
        private double ArrivalTargetHeading => towStart == null ? arrivalYaw : NavigationMath.Normalize(arrivalYaw + towStart.Heading);
        // Kicia Bay's tow start is taken one way round only, her bow toward the channel the tow takes her into (decision 143 doesn't turn
        // it); known from the table, without the shipyard.
        private bool ArrivalOneWay => Job.dockingPhase < 2 && ReleaseSpots.TowStartFor(Record.to, Record.lengthM) != null;

        // Read live every second while she comes in. Her target moved from where she learned it (a reloaded map; a save of an older build,
        // made while she came in to her service berth): the live one wins, and she keeps the way round she chose (decision 143), but at a
        // tow start. Moved by more than HeldWithin while her final approach or her mooring was under way (the older build's berth): a fresh
        // approach, her lines let go.
        private bool ResolveArrivalSpot()
        {
            if (Time.time < dockScanAt) return arrivalSpotKnown || noArrivalSpot;
            dockScanAt = Time.time + 1;
            if (!ReadArrivalSpot()) { arrivalSpotKnown = false; return false; }
            arrivalSpotKnown = !noArrivalSpot;
            if (noArrivalSpot) return true;
            bool toTowStart = towStart != null && Job.dockingPhase < 2;
            var want = toTowStart ? ArrivalTarget : arrivalSpot;
            double moved = RoutePoint.Distance(want, Job.destination);
            if (moved > 0.5)
            {
                double heading = toTowStart ? ArrivalTargetHeading : ReleaseSpots.LieHeading(arrivalYaw, Job.dockHeading);
                if (moved > ReleaseSpots.HeldWithin && Job.dockingPhase > 0)
                {
                    Log.Info("Sailing: " + Name + "'s arrival lies " + moved.ToString("F0") + " m from where she was coming in to: a fresh approach to " +
                        DescribeArrival() + " (decision 203).");
                    if (boat.Moorings.AnyRopeMoored()) boat.Moorings.UnmoorAllRopes();
                    Job.dockingPhase = 0; dockStartedAt = dockSettledAt = heldSince = -1; heldHeading = double.NaN;
                }
                Job.destination = want; Job.dockHeading = (float)heading; ++Job.revision;
            }
            return true;
        }

        // D3 at the release spot (decision 203, the user's 3b): another boat's hull in her footprint there (decision 107); heading for
        // Kicia Bay's tow start, in her footprint there too.
        private PurchasableBoat SpotOccupant()
        {
            var her = new Berths.Footprint(water);
            var origin = FloatingOriginManager.instance;
            double lie = towStart != null ? ReleaseSpots.LieHeading(arrivalYaw, ArrivalTargetHeading) : Job.dockHeading;
            var at = Berths.Occupant(origin.RealPosToShiftingPos(Vector(arrivalSpot)), (float)lie, her, boat.Root);
            if (!at && towStart != null && Job.dockingPhase < 2) at = Berths.Occupant(origin.RealPosToShiftingPos(Vector(Job.destination)), Job.dockHeading, her, boat.Root);
            return at;
        }

        // Decision 106 (every port since decision 203): she holds off while the player is in her destination's shipyard, whose exit puts
        // the ship there on the release spot; the shipyard admits, takes out and books none while she comes in or lies there (Voyages).
        private bool ShipyardInUse() => GameState.currentShipyard && GameState.currentShipyard == Berths.Yard(Record.to);

        // Docking phases: 1 her final approach to her target (the release spot, or Kicia Bay's tow start); 3 Kicia Bay's tow in to the
        // spot (VoyageTowIn.cs); 2 held at the spot (the user's 1a) until settled, then moored (phase 2 of the booking).
        private bool NavigateDocking(float dt)
        {
            if (dockStartedAt < 0) dockStartedAt = Time.time;
            if (Job.dockingPhase == 1 && Time.time - dockStartedAt > 1200) { Hold("Automatic arrival exceeded its approach budget."); dockStartedAt = -1; Job.dockingPhase = 0; return false; }
            if (!ResolveArrivalSpot())
            { HoldNavigation(dt, 0); Status.Message = "Waiting for the destination shipyard to load."; return false; }
            if (noArrivalSpot)
            {
                // A shipyard without a release spot: she goes into storage from where she is, as when the spot stays taken (D3, D5).
                if (!berthStayedTaken) { berthStayedTaken = true; Log.Info("Sailing: " + Name + ": " + Record.to + "'s shipyard has no release spot; she goes into storage from where she is."); }
                Voyages.BerthStayedTaken(this);
                if (boat == null) return false;
                HoldNavigation(dt, 0); Status.Message = "No release spot at the destination; storage waits."; return false;
            }
            if (boat.Body.isKinematic || !boat.Items.SafeToEnableBoatPhysics())
            { HoldNavigation(dt, 0); Status.Message = "Waiting for physical cargo vessel before arriving."; return false; }
            if (Job.dockingPhase == 3) { HoldNavigation(dt, 0); ArrivalTowTick(dt); return false; }
            if (Job.dockingPhase == 2) return HeldTick(dt);
            float distance = DistanceToTarget();
            float error = Mathf.Abs(Mathf.DeltaAngle(boat.Root.eulerAngles.y, Job.dockHeading));
            float speed = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            if (ShipyardInUse())
            { HoldNavigation(dt, 0); Status.Message = "The shipyard there is in use; arrival waiting."; return false; }
            var occupant = SpotOccupant();
            if (occupant)
            {
                // D3 (the user's 3b): she holds off a taken spot; ReleaseSpots.TakenHours later she goes into storage from where she holds.
                if (occupiedSince < 0)
                {
                    occupiedSince = GameClock.Hours;
                    Log.Info("Sailing: " + Name + ": the release spot at " + Record.to + (towStart != null && Job.dockingPhase < 2 ? " or her tow start" : "") + " is taken by " + occupant.name +
                        " (its hull in her footprint there, decisions 107, 203); she holds off, for at most " + ReleaseSpots.TakenHours.ToString("0.#") + " h of the game.");
                }
                if (GameClock.Hours - occupiedSince >= ReleaseSpots.TakenHours)
                {
                    if (!berthStayedTaken) { berthStayedTaken = true; Log.Info("Sailing: " + Name + ": the release spot stayed taken for " + ReleaseSpots.TakenHours.ToString("0.#") + " h (D3)."); }
                    Voyages.BerthStayedTaken(this);
                    // Stored; or she holds off until the player is off her deck (D5).
                    if (boat == null) return false;
                }
                HoldNavigation(dt, 0); Status.Message = "The release spot is taken; arrival waiting."; return false;
            }
            occupiedSince = -1;
            var stern = Ahead(Job.destination, Job.dockHeading + 180, Math.Max(0, water.AftExtent - water.Radius));
            var bow = Ahead(Job.destination, Job.dockHeading, Math.Max(0, water.ForeExtent - water.Radius));
            if (!water.ClearHull(stern, bow))
            { HoldNavigation(dt, 0); Status.Message = "The hull does not have a clear pose at her target (" + water.Obstruction + ")."; return false; }
            bool atTarget = distance <= 10 && error <= 20 && speed <= .6f && heel.State != HeelState.Urgent && rig.Sails.All(s => s.currentUnroll <= .02f);
            if (atTarget)
            {
                if (towStart != null) BeginArrivalTow();
                else BeginHeld("at the shipyard's release spot after her final approach");
                HoldNavigation(dt, 0); return false;
            }

            var here = Point(boat.StablePosition);
            var forward = Quaternion.Euler(0, Job.dockHeading, 0) * Vector3.forward;
            float along = Vector3.Dot(Vector(Job.destination) - boat.StablePosition, forward);
            // Overshooting is a failed approach, not permission to move the hull. Replan a fresh approach with the same job.
            if (along < -Math.Max(10, water.Length * .5f) || distance > Math.Max(250, water.Length * 10))
            {
                var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                var route = RoutePlanner.Plan(here, NavigationDestination(), water.Clear, NavigationMath.Heading(-wind.x, -wind.z), NoGo, 6000, 8);
                if (route.Length < 2) { HoldNavigation(dt, 0); Status.Message = "No clear route for another approach."; return false; }
                ReplaceRoute(route); Job.dockingPhase = 0; navigationClock = 1; dockStartedAt = -1;
                return false;
            }
            if (!water.TrafficClear(here, Job.destination))
            {
                // Pressed against the dock (the user, 2026-10-11: the arrival lab's Dhow came in too fast, struck the dock 38 m short
                // of her berth and lay there, furled, "Mooring approach obstructed", for good): after ApproachBlockedFor she makes a
                // fresh approach, her route to the approach point planned again, as after an overshoot.
                if (approachBlockedSince < 0) approachBlockedSince = Time.time;
                if (Time.time - approachBlockedSince >= ApproachBlockedFor)
                {
                    approachBlockedSince = -1;
                    var windNow = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                    var again = RoutePlanner.Plan(here, NavigationDestination(), water.Clear, NavigationMath.Heading(-windNow.x, -windNow.z), NoGo, 6000, 8);
                    Log.Info("Sailing: " + Name + "'s final approach stays obstructed (" + water.Obstruction + ", " + distance.ToString("F0") + " m from her target): " +
                        (again.Length >= 2 ? "a fresh approach." : "no route back out to the approach point; she holds."));
                    if (again.Length >= 2) { ReplaceRoute(again); Job.dockingPhase = 0; ++Job.revision; navigationClock = 1; dockStartedAt = -1; return false; }
                    Hold("Final approach obstructed: " + water.Obstruction); return false;
                }
                HoldNavigation(dt, 0); Status.Message = "Final approach obstructed (" + water.Obstruction + "); holding sail demand."; return false;
            }
            approachBlockedSince = -1;
            // Look along the axis, using native rudder and sail physics to remove cross-track error. No hull pose, velocity or
            // propulsion writes.
            steeringTarget = Vector(Job.destination) + forward * Math.Max(20, water.Length);
            navMessage = "Automatic final approach: " + distance.ToString("F1") + "m, " + speed.ToString("F2") + "m/s";
            Status.Message = navMessage;
            return true;
        }

        // Held at the spot (the user's 1a): the booking's tow draws her bow and stern onto their places there (DrawToSpot), heading
        // heldHeading (decision 143: the way round she came in). Settled for ReleaseSpots.HeldSettleSeconds (within HeldAcross, HeldAlong,
        // HeldAlign, under HeldSpeed, sails furled): moored (Voyages.Moored), her half hour begun; the draw holds her until her storage
        // (TickMoored). Not settled within HeldSettleBudget: she counts as arrived where she lies, logged.
        private const float HeldSettleBudget = 300;

        private void BeginHeld(string how)
        {
            heldHeading = ReleaseSpots.LieHeading(arrivalYaw, boat.Root.eulerAngles.y);
            float off = HorizontalDistance(boat.StablePosition, Vector(arrivalSpot)), speed = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            Job.destination = arrivalSpot; Job.dockHeading = (float)heldHeading; Job.dockingPhase = 2; ++Job.revision;
            heldSince = Time.time; dockSettledAt = -1; towDrawFore = towDrawAft = Unberthing.TowStrengthStart;
            Log.Info("Sailing: " + Name + " is " + how + ": " + off.ToString("F1") + " m off it at " + speed.ToString("F2") + " m/s; held there by the tow's draw, her bow " +
                heldHeading.ToString("F0") + " (" + WayRound(heldHeading) + "; decision 203).");
        }

        private bool HeldTick(float dt)
        {
            HoldNavigation(dt, 0);
            if (double.IsNaN(heldHeading)) heldHeading = ReleaseSpots.LieHeading(arrivalYaw, Job.dockHeading);
            if (heldSince < 0) heldSince = Time.time;
            DrawToSpot();
            var p = Point(boat.StablePosition);
            float speed = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            bool settled = ReleaseSpots.Held(p, arrivalSpot, heldHeading, boat.Root.eulerAngles.y) && speed <= ReleaseSpots.HeldSpeed && rig.Sails.All(s => s.currentUnroll <= .02f);
            if (!settled) dockSettledAt = -1;
            else if (dockSettledAt < 0) dockSettledAt = Time.time;
            Status.Message = "Held at the shipyard's release spot; settling.";
            if (dockSettledAt >= 0 && Time.time - dockSettledAt >= ReleaseSpots.HeldSettleSeconds)
                CompleteVoyage("arrived: held at the shipyard's release spot (" + RoutePoint.Distance(p, arrivalSpot).ToString("F1") + " m off it), sails furled, settled for " +
                    ReleaseSpots.HeldSettleSeconds.ToString("F0") + " s (decision 203).");
            else if (Time.time - heldSince >= HeldSettleBudget)
                CompleteVoyage("arrived: held at the shipyard's release spot " + RoutePoint.Distance(p, arrivalSpot).ToString("F1") + " m off it, " + speed.ToString("F2") +
                    " m/s, not settled within " + HeldSettleBudget.ToString("F0") + " s; she counts as arrived where she lies (decision 203).");
            return false;
        }

        // The booking's draw (VoyageTowIn's DrawEnd): her bow and stern each drawn to their places, her middle on the release spot.
        private void DrawToSpot()
        {
            float fore = water.ForeExtent - water.Radius, aft = water.AftExtent - water.Radius;
            DrawEnd(fore, Ahead(arrivalSpot, heldHeading, fore), ref towDrawFore);
            DrawEnd(-aft, Ahead(arrivalSpot, heldHeading + 180, aft), ref towDrawAft);
        }

        // Moored at the destination (the booking's phase 2, VoyageMode.Moored): held at the release spot by the draw until her storage
        // (the user's 1a), while she is physical near the player and lies within HeldWithin of the spot, her lines and anchor free (a ship
        // anchored where Kicia Bay's tow left her is left as she lies).
        private float mooredScanAt;

        private void TickMoored(float dt)
        {
            if (Record.phase != 2 || Job == null || Job.onPaper || boat.Body.isKinematic || boat.ObserverDistance > 1000 || !boat.Items.SafeToEnableBoatPhysics()) return;
            if (boat.Anchor.IsSet() || boat.Moorings.AnyRopeMoored()) return;
            if (Time.time >= mooredScanAt) { mooredScanAt = Time.time + 1; arrivalSpotKnown = ReadArrivalSpot() && !noArrivalSpot; }
            if (!arrivalSpotKnown) return;
            if (RoutePoint.Distance(Point(boat.StablePosition), arrivalSpot) > ReleaseSpots.HeldWithin) return;
            if (water == null) water = new NavigationWater(boat, IsOwnVesselCollider);
            if (double.IsNaN(heldHeading)) heldHeading = ReleaseSpots.LieHeading(arrivalYaw, boat.Root.eulerAngles.y);
            DrawToSpot();
        }

        // Kicia Bay's tow given up (its time out): her anchor let go where she is through its winch; once it holds (or is all out) she
        // counts as arrived there (decision 203).
        private void AnchorWhereSheIs(float dt)
        {
            var line = boat.Moorings.GetAnchorController();
            if (!line || !anchorWinch) { CompleteVoyage("arrived: where the tow in left her, with no anchor winch (decision 203)."); return; }
            WinchDrive.Apply(line, anchorWinch, 1, dt);
            Status.Message = "Letting her anchor go where the tow left her.";
            if (boat.Anchor.IsSet() || line.currentLength >= .999f)
            {
                GameBindings.WinchInput.SetValue(anchorWinch, 0f);
                CompleteVoyage("arrived: at anchor where the tow in left her, " + HorizontalDistance(boat.StablePosition, Vector(arrivalSpot)).ToString("F0") + " m from the release spot (decision 203).");
            }
        }

        private float DockingSailLimit()
        {
            if (Job == null) return 1;
            // On her way to the approach point (the user, 2026-10-11): she slows down gradually, reefing as she closes in (ArrivalSail),
            // so she comes to the final approach at its speed instead of furling all at once there.
            if (Job.dockingPhase == 0)
            {
                if (!Job.berthKnown || boat?.Body == null) return 1;
                // A tack needs its speed (decision 161: enough to carry her across the wind): while she waits to tack, tacks or gathers way, all her sail (run
                // 20261011-024626: 40-55 m off Fort Aestrin's berth, held to a third of her sail at 1.3 m/s, she never had the speed
                // to tack, wore into the dock and stalled by the ship for sale).
                if (navigator != null && (navigator.WaitingToTack || navigator.State == Manoeuvre.Tacking || navigator.Gathering)) return 1;
                // Decision 222: on her way to her anchor spot she slows into it, not into her berth beyond it.
                float toBerth = AnchorLegOpen ? HorizontalDistance(boat.StablePosition, Vector(Job.anchor)) : DistanceToTarget();
                float sog = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
                float cap = (float)ArrivalSail.Limit(toBerth, sog);
                if (cap < 1 && Time.time >= arrivalSailNoteAt)
                {
                    arrivalSailNoteAt = Time.time + 30;
                    Log.Info("Sailing: " + Name + " slows into " + Record.to + ": " + toBerth.ToString("F0") + " m from her target at " + sog.ToString("F1") + " m/s (wanted " +
                        ArrivalSail.Speed(toBerth).ToString("F1") + "), her sail at most " + cap.ToString("P0") + ".");
                }
                return cap;
            }
            // Held at the spot, or towed in to it: furled.
            if (Job.dockingPhase >= 2) return 0;
            float distance = DistanceToTarget(), speed = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up).magnitude;
            float reefSeconds = 0;
            foreach (var binding in rig.Reefs)
            {
                var winch = rig.Winches[binding.Rope];
                float target = binding.Reversed ? 1 : 0;
                double input = WinchMath.MaximumInput(target < binding.Rope.currentLength, binding.Rope.currentResistance, binding.Rope.CanPull());
                double rate = WinchMath.LengthPerSecond(input, winch.rotationSpeed, winch.gearRatio);
                reefSeconds = Mathf.Max(reefSeconds, rate > 0 ? (float)(Math.Abs(target - binding.Rope.currentLength) / rate) : 60);
            }
            float stopping = 10 + speed * reefSeconds + speed * speed / .05f;
            float desired = Mathf.Clamp(distance / 100, .35f, 1.2f);
            if (distance <= 10 || speed > desired || distance < stopping && speed > .35f) return 0;
            return distance > 100 ? .2f : .08f;
        }

        private void ResetDocking()
        {
            arrivalSpotKnown = noArrivalSpot = false; towStart = null; arrivalTowGivenUp = false;
            dockScanAt = mooredScanAt = 0; dockStartedAt = dockSettledAt = heldSince = approachBlockedSince = -1; occupiedSince = -1; heldHeading = double.NaN; berthStayedTaken = false;
            if (Job != null && Job.dockingPhase == 3) towInStage = 0;
        }

        // ---- Waiting at the origin berth (phase 0) ----

        private float waitingScanAt;
        private int mooringTries;
        private GPButtonRopeWinch waitingAnchorWinch;
        internal bool MooredAtOrigin { get; private set; }

        // Moored in the port for a day (the user's flow) at its service berth (decision 129): her lines laid on its two cleats,
        // as the game's rescue moors a ship there (the booking did that already when it brought her). When they don't hold, or
        // she isn't at the berth, her anchor goes down instead. Only near the player, with her cargo aboard and physical.
        private const int MooringTries = 5;

        private bool yardsHome;

        private void TickWaiting(float dt)
        {
            // Her yards on her centre line once she waits at the berth (after a load too): her controls are locked, so they stay.
            if (!yardsHome) { yardsHome = true; try { SailControlAdapters.FurlAtOnce(rig); SailControlAdapters.HoldOnCentreLine(rig); } catch (Exception e) { Log.Warn("Sailing: " + Name + "'s yards: " + e.Message); } }
            // Decisions 114, 117: from the release spot the tow brings her to her berth first (VoyageTowIn.cs).
            if (Job.toBerth) { TowInTick(dt); return; }
            if (MooredAtOrigin)
            {
                SlackenLines();
                // (Candidate Z let her lines go when she heeled over 15 degrees: free on an onshore wind she drifted onto Dragon
                // Cliffs' pier and capsized, run 20261007-123048. Her lines stay; decision 113 gives them slack.)
                return;
            }
            // Two lines, as the game's rescue ties a ship there (review N6: one line alone would let her swing for a day); one
            // line tied gets the tries below, then her anchor too.
            int lines = boat.Moorings.ropes.Count(r => r && r.IsMoored());
            if (boat.Anchor.IsSet() || lines >= 2)
            {
                MooredAtOrigin = true;
                Log.Info("Sailing: " + Name + " lies " + (boat.Anchor.IsSet() ? "at anchor" : "moored with " + lines + " lines") + " at the service berth of " + Record.from + ".");
                return;
            }
            if (mooringTries >= MooringTries)
            {
                var line = boat.Moorings.GetAnchorController();
                if (!waitingAnchorWinch) waitingAnchorWinch = boat.Root.GetComponentsInChildren<GPButtonRopeWinch>(true).FirstOrDefault(w => w.rope == line);
                if (!line || !waitingAnchorWinch) { MooredAtOrigin = true; Log.Warn("Sailing: " + Name + " can't be moored or anchored at her berth."); return; }
                WinchDrive.Apply(line, waitingAnchorWinch, 1, dt);
                if (boat.Anchor.IsSet() || line.currentLength >= .999f)
                {
                    GameBindings.WinchInput.SetValue(waitingAnchorWinch, 0f);
                    MooredAtOrigin = true;
                    Log.Info("Sailing: " + Name + " lies at anchor at her berth (no two cleats within her lines' reach).");
                }
                return;
            }
            if (Time.time < waitingScanAt) return;
            waitingScanAt = Time.time + 2;
            if (boat.Body.isKinematic || !boat.Items.SafeToEnableBoatPhysics() || boat.ObserverDistance > 1000) return;
            var berth = OriginBerth();
            string why = !berth ? "the port has no service berth" : berth.Cleats.Length < 2 ? "the berth has no two cleats"
                : HorizontalDistance(boat.StablePosition, berth.StablePlace(HalfBeam)) > 15 ? "she isn't at the berth" : null;
            if (why == null)
            {
                mooringTries++;
                try { Berths.MoorAtService(boat.Moorings, berth); SlackenAtTie(boat.Moorings); }
                catch (Exception e) { why = "her lines can't be laid on its cleats (" + e.Message + ")"; }
            }
            if (why == null) return;
            // Her anchor goes down instead (the tries run out at once).
            mooringTries = MooringTries;
            Log.Info("Sailing: " + Name + ": " + why + "; her anchor goes down.");
        }
    }

    // ==== VoyageTowIn ====

    // Decisions 114 and 117 (the user, 2026-10-07): at the booking the shipyard releases her the game's own way (Voyages.Board:
    // AdmitShip, its slide, DischargeShip), and the harbour's tow brings her to her berth, where she is moored and waits: "cancel
    // sailing out idea, just tow it to the berth. But arrival to destination need to be sail in" (the arrival's docking under sail,
    // VoyageDocking, is unchanged). Since decision 203 the same tow takes her in at Kicia Bay, from her tow start outside its channel to the
    // shipyard's release spot (ArrivalTowTick).

    internal sealed partial class Voyage
    {
        // 0: not begun; 1: towed along a planned way to a point off her berth; 2: bow and stern drawn alongside.
        private int towInStage;
        private float towInSeconds;
        // Drawn alongside, each end's tow strength (Unberthing.TowStrength).
        private double towDrawFore = Unberthing.TowStrengthStart, towDrawAft = Unberthing.TowStrengthStart;
        private RoutePoint[] towInPath;
        private int towInLeg;
        private RoutePoint towInBerth, towInApproach;
        private double towInHeading;

        internal const float TowInBudget = 360f;      // seconds at least, then she stays where the tow left her (moored if in reach, else anchored)
        internal const float TowInAlongsideBudget = 180f;
        private float towInBudget = TowInBudget, towInNoteAt;
        internal const double TowInSpeed = 1.0, TowInAlongside = 0.5;

        // Every waiting tick until she lies at her berth. Only near the player, physical, her cargo aboard.
        private void TowInTick(float dt)
        {
            if (boat.Body.isKinematic || !boat.Items.SafeToEnableBoatPhysics() || boat.ObserverDistance > 1000) return;
            var berth = OriginBerth();
            if (!berth) { EndTowIn("the port has no berth"); return; }
            if (water == null) water = new NavigationWater(boat, IsOwnVesselCollider);
            if ((towInSeconds += dt) > towInBudget) { EndTowIn("the tow's " + towInBudget.ToString("F0") + " s are out"); return; }
            var p = Point(boat.StablePosition);
            if (towInStage == 0)
            {
                towInBerth = Point(berth.StablePlace(HalfBeam)); towInHeading = berth.Heading;
                var cleats = berth.Cleats.Select(c => Point(FloatingOriginManager.instance.ShiftingPosToRealPos(c.transform.position))).ToArray();
                int side = Unberthing.DockSide(towInHeading, towInBerth, cleats);
                // Off the berth on its open side (the dock on her starboard side: to port), a beam and 3 m. At the shipyard's own cleats
                // (decision 217) that point lies across the release spot by the far row of cleats: there she is towed along the spot's line
                // to abreast of her berth, then drawn across to it.
                double outward = towInHeading + (side >= 0 ? -90 : 90);
                if (berth.AtShipyard && ReadOriginSpot(out var spot, out double spotYaw))
                {
                    double r = spotYaw * Math.PI / 180;
                    towInApproach = Ahead(spot, spotYaw, (towInBerth.x - spot.x) * Math.Sin(r) + (towInBerth.z - spot.z) * Math.Cos(r));
                }
                else towInApproach = Ahead(towInBerth, outward, water.Radius * 2 + 3);
                towInPath = PlanTowIn(p, towInApproach); towInLeg = 0; towInStage = 1; towInSeconds = 0; towInNoteAt = 0; ResetTowStrength(); WatchTowContacts();
                // The limit grows with the way (run 20261010-093953: Fort Aestrin's 321 m tow ran out of its flat 360 s at 0.84-0.99
                // m/s, and she was anchored 38 m off her berth): half again the way at the tow's speed, and 3 minutes alongside.
                double way = towInPath != null ? RouteMath.Length(towInPath) : RoutePoint.Distance(p, towInApproach);
                towInBudget = Math.Max(TowInBudget, (float)(1.5 * way / TowInSpeed) + TowInAlongsideBudget);
                Log.Info("Sailing: " + Name + " (stored length " + Record.lengthM.ToString("F1") + " m) is towed from the release spot to " + berth.Describe() + " ("
                    + RoutePoint.Distance(p, towInBerth).ToString("F0") + " m; the dock on her " + (side >= 0 ? "starboard" : "port") + " side there): "
                    + (towInPath != null ? (towInPath.Length - 1) + " legs, " + way.ToString("F0") + " m" : "no clear way planned, straight to a point off it")
                    + "; " + towInBudget.ToString("F0") + " s at most.");
            }
            TowInNote(p, "the point off her berth", "drawn alongside", "her berth");
            if (TowInMove(p, "lies off her berth", "drawn alongside")) EndTowIn(null);
        }

        // Against a wind the tow's strength rises until she makes its speed (Pull, Unberthing.TowStrength; run 20261007-201058:
        // 0.23 m/s against a beam wind with the old gentle pull, the whole of it across her). Every 30 s of the tow, where it has her.
        private void TowInNote(RoutePoint p, string approach, string drawn, string place)
        {
            if (towInSeconds < towInNoteAt) return;
            towInNoteAt = towInSeconds + 30;
            Log.Info("Sailing: " + Name + ": the tow in, " + towInSeconds.ToString("F0") + " s of " + towInBudget.ToString("F0") + ": " +
                (towInStage == 1 ? "leg " + (towInLeg + 1) + " of " + (towInPath != null ? towInPath.Length - 1 : 0) + ", " + RoutePoint.Distance(p, towInApproach).ToString("F1") + " m to " + approach
                    : drawn + ", " + RoutePoint.Distance(p, towInBerth).ToString("F1") + " m from " + place) +
                ", " + boat.Body.velocity.magnitude.ToString("F2") + " m/s, heading " + boat.Root.eulerAngles.y.ToString("F0") + "; " + TowContactReport() +
                // What holds her when the pull is high (run 20261010-102301: drawn alongside at Fort Aestrin, 13.9 kN for 0.02 m/s).
                (Math.Max(towStrength, Math.Max(towDrawFore, towDrawAft)) >= 8 ? "; " + StuckReport() : "") + ".");
        }

        // The tow's stages 1 and 2 (the booking's tow to her berth and, decision 203, Kicia Bay's tow in to the release spot): along its
        // planned way to towInApproach, then her bow and stern drawn to their places at towInBerth along towInHeading. True once she lies
        // there.
        private bool TowInMove(RoutePoint p, string reached, string drawn)
        {
            if (towInStage == 1)
            {
                double toApproach = RoutePoint.Distance(p, towInApproach);
                if (towInPath != null && !Unberthing.Pursue(towInPath, ref towInLeg, p, Math.Max(15, water.Length), out var nearest, out var target, out var offWay))
                {
                    Pull(NavigationMath.Heading(target.x - p.x, target.z - p.z), true, TowInSpeed);
                    HoldOnWay(p, nearest, offWay);
                }
                // Near the point off her berth she is pulled bodily (run 20261010-100003: at Gold Rock City a 155 m tow ran out of its
                // 412 s and never reached it; bow first, with no pull from 60 degrees off, she circles a point she overshoots).
                else if (toApproach > 4)
                    Pull(NavigationMath.Heading(towInApproach.x - p.x, towInApproach.z - p.z), toApproach >= Math.Max(20, 2 * water.Length), TowInSpeed * 0.6);
                else
                {
                    towInStage = 2; towDrawFore = towDrawAft = Unberthing.TowStrengthStart;
                    Log.Info("Sailing: " + Name + " " + reached + " after " + towInSeconds.ToString("F0") + " s; " + drawn + ".");
                }
                return false;
            }
            // Bow and stern each drawn to their places: alongside, and turned to its heading, at most 0.5 m/s.
            float fore = water.ForeExtent - water.Radius, aft = water.AftExtent - water.Radius;
            double end = Math.Max(DrawEnd(fore, Ahead(towInBerth, towInHeading, fore), ref towDrawFore), DrawEnd(-aft, Ahead(towInBerth, towInHeading + 180, aft), ref towDrawAft));
            return (end < 1.5 || AlongsideInReach(p)) && boat.Body.velocity.magnitude < 0.3f;
        }

        // Decision 203 (the user, 2026-10-13: "tow in from channel mouth"; "can it be closer? like 50m west then start towing?"): at Kicia
        // Bay she sails to her tow start outside its channel (ReleaseSpots: the near one, or the far one for the hulls the harbour check
        // sends there) and stops there (her final approach, NavigateDocking); then the tow takes her in (docking phase 3), as the booking's
        // tow takes her out the other way: along a planned wide way (D17), bow first, sideways as needed (decision 124), the seabed checked
        // where it can be (decision 140), to the release spot; there her bow and stern are drawn to their places and she is held (the
        // user's 1a). Its time as the booking's tow's; out: her anchor goes down where she is and she counts as arrived there.
        private bool arrivalTowGivenUp;
        private float towInRetryAt;
        private const float TowInRetrySeconds = 10;

        private void BeginArrivalTow()
        {
            double lie = ReleaseSpots.LieHeading(arrivalYaw, boat.Root.eulerAngles.y);
            Log.Info("Sailing: " + Name + " lies at Kicia Bay's tow start (" + (towStart != null ? towStart.Name : "?") + ", " + DistanceToTarget().ToString("F1") + " m off it, her bow " +
                boat.Root.eulerAngles.y.ToString("F0") + "): the tow takes her in through its channel to the shipyard's release spot, " +
                HorizontalDistance(boat.StablePosition, Vector(arrivalSpot)).ToString("F0") + " m off (decision 203).");
            Job.destination = arrivalSpot; Job.dockHeading = (float)lie; Job.dockingPhase = 3; ++Job.revision;
            towInStage = 0; towInSeconds = 0; towInNoteAt = 0; towInBudget = TowInBudget; arrivalTowGivenUp = false; towInRetryAt = 0; occupiedSince = -1;
        }

        private void ArrivalTowTick(float dt)
        {
            if (arrivalTowGivenUp) { AnchorWhereSheIs(dt); return; }
            if ((towInSeconds += dt) > towInBudget) { EndArrivalTow("the tow's " + towInBudget.ToString("F0") + " s are out"); return; }
            var p = Point(boat.StablePosition);
            if (towInStage == 0)
            {
                // Her middle onto the spot, the way round she is towed in (decision 143).
                towInBerth = towInApproach = arrivalSpot; towInHeading = ReleaseSpots.LieHeading(arrivalYaw, boat.Root.eulerAngles.y);
                if (Time.time < towInRetryAt) { towInSeconds = 0; return; }
                towInPath = PlanTowIn(p, towInApproach, true);
                if (towInPath == null)
                {
                    // No way for the tow (a boat in the channel; the user's 4a with 3b's wait): she waits at her tow start, planning again every
                    // TowInRetrySeconds; still none ReleaseSpots.TakenHours on, she goes into storage from there (D3, D5).
                    towInSeconds = 0; towInRetryAt = Time.time + TowInRetrySeconds;
                    if (occupiedSince < 0)
                    {
                        occupiedSince = GameClock.Hours;
                        Log.Info("Sailing: " + Name + ": no way for the tow in to the release spot (" + water.Obstruction + "); she waits at her tow start, for at most " +
                            ReleaseSpots.TakenHours.ToString("0.#") + " h of the game (decision 203).");
                    }
                    if (GameClock.Hours - occupiedSince >= ReleaseSpots.TakenHours)
                    {
                        if (!berthStayedTaken) { berthStayedTaken = true; Log.Info("Sailing: " + Name + ": still no way for the tow after " + ReleaseSpots.TakenHours.ToString("0.#") + " h: storage from where she lies (D3)."); }
                        Voyages.BerthStayedTaken(this);
                    }
                    return;
                }
                occupiedSince = -1;
                // Decision 225 (9): its time counted afresh with its stage, as its budget is set for the way left (after a save her sailor
                // comes back at stage 0 with the time used before kept, and the tow gave up early).
                towInLeg = 0; towInStage = 1; towInSeconds = 0; towInNoteAt = 0; ResetTowStrength(); WatchTowContacts();
                double way = towInPath != null ? RouteMath.Length(towInPath) : RoutePoint.Distance(p, towInApproach);
                towInBudget = Math.Max(TowInBudget, (float)(1.5 * way / TowInSpeed) + TowInAlongsideBudget);
                Log.Info("Sailing: " + Name + " is towed in to the release spot at " + Record.to + " (" + RoutePoint.Distance(p, towInBerth).ToString("F0") + " m, to lie with her bow " +
                    towInHeading.ToString("F0") + "): " + (towInPath != null ? (towInPath.Length - 1) + " legs, " + way.ToString("F0") + " m (" + towInWay + ")" : "no clear way planned, straight to it") +
                    "; " + towInBudget.ToString("F0") + " s at most (decision 203).");
            }
            TowInNote(p, "the release spot", "drawn onto the release spot", "it");
            if (TowInMove(p, "lies by the release spot", "drawn onto it")) EndArrivalTow(null);
        }

        private void EndArrivalTow(string why)
        {
            towInStage = 0; towStuck = 0;
            if (why == null)
            {
                EndTowContacts();
                BeginHeld("towed in to the shipyard's release spot (" + towInSeconds.ToString("F0") + " s)");
                return;
            }
            Log.Info("Sailing: " + Name + ": the tow in to the release spot ends: " + why + "; " + StuckReport() + "; " + TowContactReport() +
                "; her anchor goes down where she is, and she counts as arrived there (decision 203).");
            EndTowContacts();
            arrivalTowGivenUp = true;
        }

        // Decision 220 (the user, 2026-11-03, asked how a ship booked from Kicia Bay gets out of its shipyard channel: "Tow her out
        // (Recommended)"): the arrival's tow in reverse takes her from the shipyard's cleats (217) down the channel to its tow start
        // (ReleaseSpots.TowOutFor), where her sailor sets sail. Claude's calls (decision 228): its way planned as the arrival's is, from the
        // tow start to where she lies (the narrow end, near the goal, on her own clearance), then turned round; bow first at the arrival's
        // speeds (TowInMove); let go where the way ends, no draw (she isn't held there). No way: she waits where she lies and it is planned
        // again every TowInRetrySeconds, with no limit (206 counts this wait as her voyage going on). Its time out: planned again from where she lies. Once done
        // (Job.towedOut, saved) never again for this voyage: a hem-in after it takes the usual stage.
        private bool TowOutPending
        {
            get
            {
                if (Job.dockingPhase != 0 || Job.towedOut || ReleaseSpots.TowOutFor(Record.from) == null) return false;
                var berth = OriginBerth();
                return berth && berth.AtShipyard;
            }
        }

        private bool towOutWaitNoted;

        private void BeginTowOut(string because)
        {
            unberthStage = UnberthStage.TowOutChannel;
            if (!Job.unberthing) { Job.unberthing = true; ++Job.revision; }
            towInStage = 0; towInSeconds = 0; towInNoteAt = 0; towInBudget = TowInBudget; towInRetryAt = 0; towOutWaitNoted = false;
            Log.Info("Sailing: " + Name + " is to be towed out of Kicia Bay's channel to its tow start (" + because + "; decision 220).");
        }

        private void TowOutTick(float dt)
        {
            HoldNavigation(dt, 0);
            if ((towInSeconds += dt) > towInBudget)
            {
                Log.Info("Sailing: " + Name + ": the tow out's " + towInBudget.ToString("F0") + " s are out (" + StuckReport() + "; " + TowContactReport() +
                    "); it is planned again from where she lies (decision 220).");
                EndTowContacts(); towInStage = 0; towInSeconds = 0; towInBudget = TowInBudget;
            }
            var p = Point(boat.StablePosition);
            if (towInStage == 0)
            {
                var start = ReleaseSpots.TowOutFor(Record.from);
                if (start == null || !ReadOriginSpot(out var spot, out double yaw)) { towInSeconds = 0; Status.Message = "Waiting for the shipyard to load for the tow out."; return; }
                towInBerth = towInApproach = ReleaseSpots.InAxes(spot, yaw, start.X, start.Z);
                towInHeading = NavigationMath.Normalize(yaw + ReleaseSpots.TowOutHeading(start));
                if (Time.time < towInRetryAt) { towInSeconds = 0; return; }
                var way = PlanTowIn(towInApproach, p, true);
                if (way == null)
                {
                    towInSeconds = 0; towInRetryAt = Time.time + TowInRetrySeconds;
                    Status.Message = "Waiting for a clear way for the tow out.";
                    if (!towOutWaitNoted)
                    {
                        towOutWaitNoted = true;
                        Log.Info("Sailing: " + Name + ": no way for the tow out to Kicia Bay's tow start (" + water.Obstruction + "); she waits where she lies, planned again every " +
                            TowInRetrySeconds.ToString("F0") + " s (decision 220).");
                    }
                    return;
                }
                towOutWaitNoted = false;
                towInPath = way.Reverse().ToArray();
                towInLeg = 0; towInStage = 1; towInSeconds = 0; towInNoteAt = 0; ResetTowStrength(); WatchTowContacts();
                double length = RouteMath.Length(towInPath);
                towInBudget = Math.Max(TowInBudget, (float)(1.5 * length / TowInSpeed) + TowInAlongsideBudget);
                Log.Info("Sailing: " + Name + " is towed out to Kicia Bay's tow start, " + start.Name + " (" + RoutePoint.Distance(p, towInApproach).ToString("F0") + " m off): " +
                    (towInPath.Length - 1) + " legs, " + length.ToString("F0") + " m (" + towInWay + "); " + towInBudget.ToString("F0") + " s at most (decision 220).");
            }
            Status.Message = "Towed out of the channel: " + RoutePoint.Distance(p, towInApproach).ToString("F0") + " m to the tow start.";
            TowInNote(p, "the tow start", "let go", "it");
            TowInMove(p, "reached Kicia Bay's tow start", "the tow lets her go");
            if (towInStage == 2) EndTowOut();
        }

        private void EndTowOut()
        {
            towInStage = 0; Job.towedOut = true; ++Job.revision;
            EndTowContacts();
            Log.Info("Sailing: " + Name + " lies at Kicia Bay's tow start after " + towInSeconds.ToString("F0") + " s of the tow out; her sailor sets sail (decision 220). " + StuckReport());
            FinishUnberthing(true);
        }

        // Decision 182 (the user, 2026-10-11 night: "the way you tow the dhow into berth is weird, after dhow already moor you're still
        // towing"): alongside, she is tied up once she lies within TowInAcross of the berth's line, within TowInAlong of her place along
        // it and within TowInAlign of its heading; her lines reach that far (they are tied with 2.5 m of slack). Before, both her ends
        // had to come within 1.5 m of their places: run 20261011-205735, the Dhow lay alongside 3.1 m short and was hauled along the
        // dock at 0.18 m/s for 36 s.
        internal const double TowInAcross = 1.5, TowInAlong = 4, TowInAlign = 10;
        private bool AlongsideInReach(RoutePoint p)
        {
            double dx = p.x - towInBerth.x, dz = p.z - towInBerth.z, r = towInHeading * Math.PI / 180;
            double along = dx * Math.Sin(r) + dz * Math.Cos(r), across = dx * Math.Cos(r) - dz * Math.Sin(r);
            return Math.Abs(across) <= TowInAcross && Math.Abs(along) <= TowInAlong && Math.Abs(NavigationMath.Delta(boat.Root.eulerAngles.y, towInHeading)) <= TowInAlign;
        }

        // One end of her (`along` metres from her origin on her centre line) drawn toward `place` with half her mass, at her centre
        // of mass's height (no heel); its distance from there.
        private double DrawEnd(float along, RoutePoint place, ref double strength)
        {
            var world = boat.Root.TransformPoint(new Vector3(0, 0, along));
            var at = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(world));
            double dx = place.x - at.x, dz = place.z - at.z, distance = Math.Sqrt(dx * dx + dz * dz);
            double speed = Math.Min(TowInAlongside, distance * 0.25);
            double tx = distance > 1e-6 ? dx / distance * speed : 0, tz = distance > 1e-6 ? dz / distance * speed : 0;
            var v = boat.Body.GetPointVelocity(world);
            // Each end's own strength, by how fast that end closes on its place (Unberthing.TowStrength).
            double closing = speed > 1e-6 ? (v.x * tx + v.z * tz) / speed : 0;
            AdaptTow(ref strength, closing, speed, "with an end closing on its place");
            Unberthing.Tow(v.x, v.z, tx, tz, out var ax, out var az, strength);
            world.y = boat.Body.worldCenterOfMass.y;
            var force = new Vector3((float)ax, 0, (float)az) * boat.Body.mass * 0.5f;
            boat.Body.AddForceAtPosition(force, world);
            NoteTowForce(force);
            return distance;
        }

        // From the release spot to the point off her berth: wide (D17) away from both ends, her own clearance within 20 m of the
        // point off the berth (the dock beside it) and for the first 15 m; her own clearance throughout when no wide way is found.
        // `seabed` (decision 203, Kicia Bay's tow in from open water): first the wide way with the seabed under it too (decision 140, as
        // the tow out checks it), then as the booking's tow. towInWay: which was found.
        private string towInWay;

        private RoutePoint[] PlanTowIn(RoutePoint start, RoutePoint goal, bool seabed = false)
        {
            Func<RoutePoint, RoutePoint, bool> Wide(Func<RoutePoint, RoutePoint, bool> far) => (a, b) => RoutePoint.Distance(a, goal) < 20 || RoutePoint.Distance(b, goal) < 20
                ? water.ClearHull(a, b) : Unberthing.TowLegClear(start, a, b, water.ClearHull, far);
            var ways = new System.Collections.Generic.List<KeyValuePair<string, Func<RoutePoint, RoutePoint, bool>>>();
            if (seabed) ways.Add(new KeyValuePair<string, Func<RoutePoint, RoutePoint, bool>>("wide, the seabed checked", Wide(TowWideSeabed)));
            ways.Add(new KeyValuePair<string, Func<RoutePoint, RoutePoint, bool>>("wide", Wide(TowWideHull)));
            ways.Add(new KeyValuePair<string, Func<RoutePoint, RoutePoint, bool>>("her own clearance", water.ClearHull));
            // The arrival's tow plans on an 8 m grid (as her approach does under 600 m): Kicia Bay's channel is 14-17 m from dock to dock,
            // which the 20 m grid's points and legs never fit, and with no way planned the tow pulls her straight at the spot, across the
            // land between. The booking's tow keeps its own (the user, 2026-10-12: its route stays as it is).
            foreach (var way in ways)
            {
                var path = RoutePlanner.Plan(start, goal, way.Value, 0, 0, 1500, seabed ? 8 : 20);
                if (path.Length >= 2) { towInWay = way.Key; return path; }
            }
            towInWay = "none";
            return null;
        }

        private bool TowWideSeabed(RoutePoint a, RoutePoint b) => water.ClearWide(a, b, (float)Unberthing.TowClearance(water.Radius, water.Length), true);

        // The tow done (or given up, `why`): the waiting tick moors her at the berth when her lines reach, else her anchor goes down.
        private void EndTowIn(string why)
        {
            Job.toBerth = false; ++Job.revision; towInStage = 0; towStuck = 0;
            Log.Info("Sailing: " + Name + (why == null ? " lies at her berth after the tow (" + towInSeconds.ToString("F0") + " s)." : ": the tow in ends: " + why + "; " + StuckReport() + "; " + TowContactReport() + "."));
            EndTowContacts();
        }

        // ---- The tow's pull, for the "no headway under tow for 30 s" line ----

        // Since the tow began (ResetTowStrength) or the last report: the largest pull, and its largest part across her (+ to starboard).
        private float towForceMax, towForceAcross;

        private void NoteTowForce(Vector3 force)
        {
            towForceMax = Mathf.Max(towForceMax, force.magnitude);
            float across = Vector3.Dot(force, boat.Root.right);
            if (Mathf.Abs(across) > Mathf.Abs(towForceAcross)) towForceAcross = across;
        }

        private string TowForceReport()
        {
            string report = towForceMax <= 0 ? "no tow" : "the tow's pull up to " + towForceMax.ToString("F0") + " N, across her up to " + towForceAcross.ToString("F0") + " N (+ to starboard)";
            towForceMax = towForceAcross = 0;
            return report;
        }
    }

    // ==== VoyageArrival ====

    // Decision 198 (h) (the user, 2026-10-12: "make this universal, both big boat and small boat can sail according to list, and not limit
    // to only dragoncliff"): arriving, she plans her way in as she plans her way out. From ArrivalChartFrom off her berth her destination's
    // harbour is charted (the island's envelope round her berth, as her origin's at her departure, decision 157; a slice a frame while her
    // route takes her in) and flooded from her approach point (decision 146); once she is in its water, her line in is planned on it with
    // her own turning circle (LinePlanner, LineGoal) and she sails it as her line out (decision 198 b-f); near its end her final approach
    // takes her to her berth (NavigateDocking). Her way in follows her approach point when it moves (decision 143's other end, 146's other
    // distance). In port still from RouteHarbour of her berth (decision 177): this chart doesn't put her in port (InOriginChart).

    internal sealed partial class Voyage
    {
        // Charted from this far off her berth (decision 201 g, the user, 2026-10-12 night: "Earlier, slow if late"; it was 2 km, the user's
        // "about 2 km out"); her line in ends within ArrivalGoalLeast or half her length of her approach point, her heading within
        // ArrivalGoalTolerance of her berth's way; her chart flooded again when her approach point moves ArrivalRetargetMetres or more. Her
        // line in is planned once she is ArrivalLineFrom or less by water from her approach point, her chart's guide taking her there
        // (decision 157): a search from her chart's edge, 3 km by water at Dragon Cliffs, ran out of states even with longer moves in open
        // water (the host test's harbour check, 2026-10-12: from 1,200 m the Brig had a line in in 6 of 16 winds, from the edge in none).
        internal const double ArrivalChartFrom = 3000, ArrivalGoalLeast = 10, ArrivalGoalTolerance = 45, ArrivalRetargetMetres = 10, ArrivalLineFrom = 1200;
        private const float ArrivalRetargetEvery = 2;
        private bool chartArriving, inArrivalChart, arrivalReckoned;
        private float arrivalRetargetAt;

        // Decision 201 (b) (the user, 2026-10-12 night; the harbour check: the Junk's and the Baghlah's charts at Dragon Cliffs led them
        // into its northern channel, whose bend their circles can't make, with no room to turn back): her way in decided before she is
        // committed. Each time her destination's chart has been flooded while her own line in isn't begun, her line in is searched, a slice
        // a frame, from her entry: down her chart's way from where she comes in (from her when she is in its water, else from the water on
        // its edge nearest her) to ArrivalLineFrom by water from her approach point (WaterField.EntryDown). Found: her chart's way stands.
        // None: her chart's way is closed where the search got stuck (CloseWayIn) and her chart flooded again, at most WayInCloses times in
        // a chart, so her chart's guide leads her to a way she can make (as decision 195 b does leaving). Her own line in is still planned
        // from where she is once she is ArrivalLineFrom or less by water from her approach point.
        internal const int WayInCloses = 3;
        // Claude's: the cells within WayInKeepLengths of her lengths of her approach point are never closed (her chart's flood starts
        // there; closed, no way in would be left at all).
        internal const double WayInKeepLengths = 2;
        private LinePlanner wayInPlanner;
        private bool wayInChecked;
        private int wayInClosed;

        // Decision 201 (b): her chart's way in closed where her search got stuck: from the state it reached nearest her approach point by
        // water, one to ChartFailLengths of her lengths down her way, each cell within her length (decision 195 b's closing), none within
        // WayInKeepLengths of her lengths of her approach point (the chart's flood goal). How many cells; the harbour check runs it too.
        internal static int CloseWayIn(WaterField chart, RoutePoint stuck, double length) =>
            chart.CloseWay(stuck, length, ChartFailLengths * length, length, chart.FloodGoal, WayInKeepLengths * length);

        // Every frame (NavigateTick): within ArrivalChartFrom of her known berth, her destination charted a slice a frame (her origin's
        // chart behind her is let go), then kept until she is moored.
        private void ArrivalChartStep(RoutePoint p)
        {
            if (chartArriving)
            {
                if (!chartDone && !chartNone) ChartStep(Job.destination, p);
                else WayInStep(p);
                return;
            }
            if (!Job.departureComplete || Job.unberthing || Job.dockingPhase > 0 || !Job.berthKnown || !Physical || water == null) return;
            // Decision 222 (then the user, 2026-11-04: "no if it can be charted on her way in, just go in"): with an anchor spot ahead, once
            // her destination island's scene is loaded and no scene loads (ChartStep allows for the island standing off its true height),
            // on her way in from ArrivalChartFrom off her berth, or at anchor where it wasn't charted by then. After a load past her anchor
            // stage, or at a port without an anchor spot, from ArrivalChartFrom off her berth as before. Decision 238 (the user, 2026-11-04:
            // "Yes, build it"): in any case only with the island's seabed on, so with the player aboard from about its load distance.
            if (AnchorLegOpen && (GameState.loadingScenes > 0 || !BerthIslandLoaded())) return;
            if (!(AnchorLegOpen && Job.anchorStage == 1) && (DistanceToTarget() > ArrivalChartFrom || NearOriginHarbour())) return;
            if (!ChartSeabedOn(BerthIsland())) return;
            chartArriving = true; chart = null; chartDone = chartLeft = chartNone = false; inArrivalChart = arrivalReckoned = false;
            line = null; linePlanner = null; lineReplanWhy = null; refloodWhy = null; lineRetryAt = 0; lineAt = 0;
            tideWait = tideRisen = tideGaveUp = false; tideWaitSince = -1; tideNeed = double.NaN;
            narrowHoldWanted = double.NaN; narrowHoldAlong = double.NaN;
            wayInPlanner = null; wayInChecked = false; wayInClosed = 0;
            Log.Info("Sailing: " + Name + " is " + DistanceToTarget().ToString("F0") + " m from her berth at " + Record.to + ": she charts her destination's harbour, her way in to be planned " +
                "with her turning circle to her approach point (decisions 198 h, 201 g" + (AnchorLegOpen ? ", 222: " + (Job.anchorStage == 1 ? "at anchor" : "on her way in") + ", the island " + ChartIslandDrop().ToString("F2") +
                " m below its height now" : "") + ").");
        }

        // ---- Decision 222: the wait at anchor before her harbour ----

        // Her destination's anchor leg: chosen (TryResolveDestinationBerth), not yet weighed, not docking, not arrived on paper.
        private bool AnchorLegOpen => Job.berthKnown && Job.anchorKnown && Job.anchorStage < 2 && Job.dockingPhase == 0 && !Job.onPaper;

        // Where her route ends: her anchor spot while her anchor leg is open, else her approach point.
        private RoutePoint RouteEnd() => AnchorLegOpen ? Job.anchor : NavigationDestination();

        // Decision 222 (the user, 2026-11-03: "C. Wait, but wait at the part that can be anchored and make it anchor there. And only wait
        // until the chart is usable, not when island fully raised"): at her destination's anchor spot she lets go the game's own anchor
        // (225 (1): her lock leaves it able to set) and waits there, her sails off, until her harbour's chart is made (ArrivalChartStep:
        // begun on her way in, or at anchor, once the island's scene is loaded, seconds), then weighs anchor and sails in (203); charted
        // before she gets there, she doesn't anchor (SkipAnchor; the user, 2026-11-04). The wait is no fault: her
        // progress watch is paused at anchor (TickSailing). The anchor weighed within AnchorWeighSeconds, else a hold (as at her departure).
        private float anchorWeighSeconds;
        internal const float AnchorWeighSeconds = 120;
        private bool AnchorTick(float dt)
        {
            HoldNavigation(dt, 0);
            var rope = boat.Moorings.GetAnchorController();
            bool canAnchor = rope && anchorWinch;
            if (Job.anchorStage == 0)
            {
                Job.anchorStage = 1; ++Job.revision; anchorWeighSeconds = 0;
                Log.Info("Sailing: " + Name + " " + (canAnchor ? "lets go her anchor" : "stops, with no anchor winch,") + " at her anchor spot off " + Record.to + " (" +
                    RoutePoint.Distance(Point(boat.StablePosition), Job.anchor).ToString("F0") + " m off it) and waits there until her harbour is charted (decision 222).");
            }
            if (!(chartArriving && (chartDone || chartNone)))
            {
                if (canAnchor && !boat.Anchor.IsSet() && rope.currentLength < .999f) WinchDrive.Apply(rope, anchorWinch, 1, dt);
                else if (canAnchor) GameBindings.WinchInput.SetValue(anchorWinch, 0f);
                Status.Message = "At anchor off " + Record.to + ": " + (!ChartSeabedOn(BerthIsland()) ? "waiting until the player is near enough to chart her harbour." : chartArriving ? "charting her harbour." : "waiting for its island to load.");
                return false;
            }
            if (canAnchor && (rope.currentLength > 0.005f || boat.Anchor.IsSet()))
            {
                if ((anchorWeighSeconds += dt) > AnchorWeighSeconds) { Hold("Anchor could not be recovered at her anchor spot."); return false; }
                WinchDrive.Apply(rope, anchorWinch, 0, dt);
                Status.Message = "Weighing anchor off " + Record.to + " (" + rope.currentLength.ToString("P0") + ").";
                return false;
            }
            if (canAnchor) GameBindings.WinchInput.SetValue(anchorWinch, 0f);
            Job.anchorStage = 2; ++Job.revision; approachPlanAt = 0;
            sails.Plan((float)heel.Deployment, false);
            Log.Info("Sailing: " + Name + " weighs anchor off " + Record.to + ": her harbour is charted" + (chartNone ? " (no chart there)" : "") + "; she sails in (decision 222).");
            return false;
        }

        // Decision 222 (the user, 2026-11-04: "no if it can be charted on her way in, just go in"): her harbour charted before she
        // reached her anchor spot, she doesn't anchor; her route goes on to her approach point.
        private void SkipAnchor()
        {
            Job.anchorStage = 2; ++Job.revision; approachPlanAt = 0;
            Log.Info("Sailing: " + Name + "'s harbour at " + Record.to + " is charted on her way in" + (chartNone ? " (no chart there)" : "") + ", " +
                RoutePoint.Distance(Point(boat.StablePosition), Job.anchor).ToString("F0") + " m before her anchor spot: she goes straight in, without anchoring (decision 222).");
        }

        // The island whose load distance holds this point (real metres), as EnsureVoyageScenes keeps it, or null.
        private IslandHorizon IslandHolding(Vector3 real)
        {
            var tracker = IslandDistanceTracker.instance;
            if (!tracker || !FloatingOriginManager.instance) return null;
            IslandHorizon best = null; float bestDistance = float.PositiveInfinity;
            foreach (var island in tracker.islands)
            {
                if (!island || island.islandIndex <= 0) continue;
                float d = HorizontalDistance(real, FloatingOriginManager.instance.ShiftingPosToRealPos(island.GetPosition()));
                if (d < island.islandLoadDistance && d < bestDistance) { best = island; bestDistance = d; }
            }
            return best;
        }

        // Her destination berth's island, or null.
        private IslandHorizon BerthIsland() => IslandHolding(Vector(Job.destination));

        // The island her chart lies round: her destination's, or on her way out her origin's (decision 236), or null.
        private IslandHorizon ChartIsland()
        {
            if (chartArriving) return BerthIsland();
            var berth = OriginBerth();
            return berth ? IslandHolding(berth.StablePlace(HalfBeam)) : null;
        }

        // Its scene loaded (the scene's own isLoaded: IslandHorizon.SceneLoaded is true from the load's start).
        private bool BerthIslandLoaded()
        {
            var island = BerthIsland();
            return island && SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded;
        }

        // How far her chart's island stands below its true height now (+ low; IslandHeight.Drop), 0 where unknown.
        private float ChartIslandDrop()
        {
            var island = ChartIsland();
            var eye = global::Refs.ovrCameraRig;
            return island && eye ? (float)IslandHeight.Drop(island.currentDistance, eye.position.y, island.fullyEmergedDistance, island.maxDrop) : 0f;
        }

        // Decision 238 (the user, 2026-11-04: "Yes, build it"; run 20261104-210717): whether a chart of this island can see its seabed now (no
        // island known: as before). `chartPaused`: her chart is held back for it this frame; `seabedWait`: she holds for it (no fault).
        private static bool ChartSeabedOn(IslandHorizon island) => !island || IslandHeight.SeabedOn(island.currentDistance, island.islandLoadDistance);
        private bool chartPaused, seabedWait;

        // Decision 201 (b): her way in checked from her entry once her destination's chart is flooded (above); a slice a frame.
        private void WayInStep(RoutePoint p)
        {
            if (wayInChecked || chart == null || chartNone || chartLeft || !chart.Flooded || chart.Flooding || chart.Needing || tideWait || !chart.HasGoal || Job.dockingPhase > 0) return;
            // Her own line in begun: the check has nothing to add.
            if (line != null || linePlanner != null) { wayInPlanner = null; wayInChecked = true; return; }
            double length = Math.Max(10, water.Length);
            if (wayInPlanner == null)
            {
                bool fromHer = chart.Inside(p) && NavigationMath.Finite(chart.DistanceFrom(p));
                int edge = fromHer ? -1 : chart.EdgeNearest(p);
                RoutePoint? entry = null;
                double heading = double.NaN;
                if (fromHer || edge >= 0) entry = chart.EntryDown(fromHer ? p : chart.Centre(edge), ArrivalLineFrom, out heading);
                if (!entry.HasValue)
                {
                    wayInChecked = true;
                    Log.Info("Sailing: " + Name + "'s way in can't be checked (decision 201 b): her chart's flood reaches " + (fromHer ? "no way down from her" : "no water on its edge") + ".");
                    return;
                }
                if (!NavigationMath.Finite(heading)) heading = Job.dockHeading;
                var c = Circle;
                var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
                double windNow = wind.sqrMagnitude > 0.01f ? WindFrom() : double.NaN;
                var goal = new LineGoal(chart.FloodGoal, Job.dockHeading, Math.Max(ArrivalGoalLeast, length / 2), ArrivalGoalTolerance);
                wayInPlanner = new LinePlanner(chart, entry.Value, heading, length, Math.Max(0.5, water.Radius), c.Plan(true, length, HullWithoutBowsprit), c.Plan(false, length, HullWithoutBowsprit),
                    windNow, NavigationMath.Finite(windNow) ? chartBeat : double.NaN, goal);
                Log.Info("Sailing: " + Name + " checks her way in (decision 201 b): her line in searched from (" + entry.Value.x.ToString("F0") + ", " + entry.Value.z.ToString("F0") + "), " +
                    chart.DistanceFrom(entry.Value).ToString("F0") + " m by water from her approach point, heading " + heading.ToString("F0") + ", down her chart's way from " +
                    (fromHer ? "her" : "the water on its edge nearest her") + ".");
                return;
            }
            if (!wayInPlanner.Step(LineBudgetMs)) return;
            var done = wayInPlanner; wayInPlanner = null;
            if (!done.Failed)
            {
                wayInChecked = true;
                Log.Info("Sailing: " + Name + "'s way in is one her circle makes (decision 201 b): a line from her entry " + done.Line[done.Line.Count - 1].Along.ToString("F0") +
                    " m to her approach point (" + done.States + " states, " + done.Milliseconds.ToString("F0") + " ms over the frames).");
                return;
            }
            var stuck = done.Stuck();
            int closed = wayInClosed < WayInCloses && stuck.HasValue ? CloseWayIn(chart, stuck.Value, length) : 0;
            if (closed == 0)
            {
                wayInChecked = true;
                Log.Info("Sailing: " + Name + " finds no line in from her entry (decision 201 b: " + done.Why + ", " + done.Milliseconds.ToString("F0") + " ms over the frames); " +
                    (wayInClosed >= WayInCloses ? "her way already closed " + WayInCloses + " times" : stuck.HasValue ? "nothing left to close where it got stuck" : "it reached nowhere") +
                    ": her chart's guide as before (decision 157).");
                return;
            }
            wayInClosed++;
            FloodChart();
            refloodWhy = "decision 201 b: no line in from her entry (" + done.Why + "), " + closed + " cells of her way closed where her search got stuck, at (" +
                stuck.Value.x.ToString("F0") + ", " + stuck.Value.z.ToString("F0") + "), " + wayInClosed + " of " + WayInCloses;
            Log.Info("Sailing: " + Name + "'s way in is reckoned again (" + refloodWhy + "); her chart flooded again a slice a frame (decision 198 j).");
        }

        // Decision 201 (g): within RouteHarbour of her berth before her destination is charted (her chart probed and flooded), her sail
        // at steerage until it is (the user: "Earlier, slow if late").
        private bool arrivalLateLogged;
        private float ArrivalChartSailLimit()
        {
            if (!chartArriving || chartDone || chartNone || Job.dockingPhase > 0 || DistanceToTarget() >= RouteHarbour) { arrivalLateLogged = false; return 1; }
            if (!arrivalLateLogged)
            {
                arrivalLateLogged = true;
                Log.Info("Sailing: " + Name + " is " + DistanceToTarget().ToString("F0") + " m from her berth, her destination not charted yet (" +
                    (chart == null ? "not begun" : chartNext < chart.Count ? (100.0 * chartNext / chart.Count).ToString("F0") + "% of its cells" : "its flood") +
                    "): her sail at steerage until it is (decision 201 g).");
            }
            return (float)EmergencySailPolicy.SteerageDeployment;
        }

        // Whether she is in her destination's chart's water (her way in reaches her); the first time it doesn't though she is in the
        // chart, whether the tide closed it (WayClosed). Her approach point followed every ArrivalRetargetEvery.
        private bool ArrivalInChart(RoutePoint p)
        {
            if (!chartDone || chart == null || chartNone || chartLeft) return inArrivalChart = false;
            if (chart.Needing || tideWait) return inArrivalChart;
            if (Time.time >= arrivalRetargetAt) { arrivalRetargetAt = Time.time + ArrivalRetargetEvery; ArrivalRetarget(); }
            bool inside = chart.Inside(p, ChartBeyond), reached = inside && NavigationMath.Finite(chart.DistanceFrom(p));
            if (inside && !reached && !arrivalReckoned && !tideGaveUp && chart.Depth.Known && !chart.Flooding)
            {
                arrivalReckoned = true;
                WayClosed(p, "she is in her destination's chart, its way in not reaching her");
                return inArrivalChart = false;
            }
            if (reached && !inArrivalChart)
                Log.Info("Sailing: " + Name + " is in her destination's chart's water: " + chart.DistanceFrom(p).ToString("F0") + " m by water to her approach point, " +
                    RoutePoint.Distance(p, Job.destination).ToString("F0") + " m from her berth (decision 198 h).");
            return inArrivalChart = reached;
        }

        // Her approach point moved since her chart was flooded from it: flooded again from where it is now, and her line planned again.
        private void ArrivalRetarget()
        {
            if (chart == null || !chart.Flooded || chart.Flooding || !chart.HasGoal || !Job.berthKnown) return;
            var now = NavigationDestination();
            double moved = RoutePoint.Distance(now, chart.FloodGoal);
            if (moved < ArrivalRetargetMetres) return;
            chartGoal = now;
            FloodChart();
            refloodWhy = "decision 198 h: her approach point moved " + moved.ToString("F0") + " m (" + WayRound(Job.dockHeading) + ")";
            ReplanLine("her approach point moved (decision 198 h)");
            Log.Info("Sailing: " + Name + "'s way in is reckoned again from her approach point (" + refloodWhy + "), a slice a frame (decision 198 j).");
        }

        // Near her approach point in her destination's chart (as near as her route's last point counts reached): her final approach.
        private bool ArrivalApproachReached(RoutePoint p) =>
            chartArriving && InHarbourChart && Job.berthKnown && RoutePoint.Distance(p, NavigationDestination()) < Math.Max(40, water.Length * 1.5f);
    }

    // ==== VoyageBacking ====

    // Decision 155: backing her square sail to turn where she lies (Navigation\Backing.cs holds the rules, host-tested). Started from
    // NavigateTick where she can't tack and her wear's way is blocked, or where she is hemmed in (before the old way: the short tow near
    // her berth, else a hold); run every half second while it lasts (BackingTick), her helm by Steer (reversed with her sternway), her
    // sails by SetSailTurn (SailControlAdapters.Astern: square sails braced for the most push astern, the others for her turn).

    internal sealed partial class Voyage
    {
        private bool backing, backThroughWind;
        private int backSide;
        private double backTarget;
        private RoutePoint backFrom;
        private float backSince, backSail = 1f, backProgressAt;
        private double backBestRemaining;
        private readonly List<float> backFails = new List<float>();

        // Her way astern `metres` past her stern (her beam and the seabed, NavigationWater.ClearBeam), measured from her stern, not her
        // middle (decision 164).
        private bool SternClear(float metres)
        {
            var stern = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(boat.Root.TransformPoint(new Vector3(0, 0, -(water.AftExtent - water.Radius)))));
            return water.ClearBeam(stern, Ahead(stern, boat.Root.eulerAngles.y + 180, metres));
        }

        // A rig that can back: one with a square sail (decision 155).
        private bool CanBack => rig != null && rig.Sails.Any(s => s && s.squareSail);

        // Starts backing when she can: a square sail, the apparent wind forward of her beam, her way astern clear for her length, and a
        // clear, sailable heading to turn to from a little astern of where she lies; fewer than Backing.MostFails failed backings in
        // the last Backing.FailWindow. True when she backs. `throughWind` (decision 160, instead of a wear in her harbour): her bow turns
        // toward the wind and across it onto her new tack (she luffs with the way she has, the backed sail drives her astern as she
        // stalls, her helm reversed with her sternway carries her bow over); else away from it (decision 155's box-haul; DU's runs
        // 20261011-141743 and -141758: with 2.3 m/s ahead the box-haul wore her forward 40-70 degrees down the wind, away from the exit).
        private bool TryBacking(string why, RoutePoint p, double bearing, double apparentFrom, double deadZone, float look, bool throughWind = false)
        {
            if (backing || !CanBack || Job.unberthing || Job.dockingPhase > 0 || boat.Body.isKinematic) return false;
            backFails.RemoveAll(t => Time.time - t > Backing.FailWindow);
            if (backFails.Count >= Backing.MostFails) return false;
            double h = boat.Root.eulerAngles.y;
            if (!Backing.WindAhead(h, apparentFrom)) return false;
            if (!SternClear(water.Length))
            { Log.Info("Sailing: " + Name + " can't back (decision 155): her way astern is blocked (" + water.Obstruction + ")."); return false; }
            int side = throughWind ? -Backing.TurnSide(h, apparentFrom) : Backing.TurnSide(h, apparentFrom);
            // She turns where she lies, half her length astern of it: her hull's turn there (in two halves, each the way she turns) and
            // her way on from there clear.
            var pivot = Ahead(p, h + 180, water.Length * 0.5);
            var guide = GuidePoint();
            double guideFromPivot = NavigationMath.Heading(guide.x - pivot.x, guide.z - pivot.z);
            // Across the wind (decision 160): a heading on her new side only (run 20261011-152234 on DZ: 338 from 348, still on her
            // old side, was "her heading" at once, three times).
            int oldSide = TackNavigator.Side(apparentFrom, h);
            double target = Backing.Target(h, side, bearing, apparentFrom, deadZone, c =>
            {
                if (throughWind && TackNavigator.Side(apparentFrom, c) == oldSide) return false;
                double mid = NavigationMath.Normalize(h + side * Backing.Remaining(h, c, side) / 2);
                return water.TurnClear(pivot, h, mid) && water.TurnClear(pivot, mid, c) && WayClear(pivot, c, look, guideFromPivot, guide);
            }, Margin);
            if (double.IsNaN(target))
            { Log.Info("Sailing: " + Name + " can't back (decision 155): no clear, sailable heading to turn to from where she lies."); return false; }
            backing = true; backThroughWind = throughWind; backSide = side; backTarget = target; backFrom = p; backSince = Time.time; backSail = 1f;
            backBestRemaining = Backing.Remaining(h, target, side); backProgressAt = Time.time;
            // Bearing away or in irons before: their sail settings end here (SetSailTurn takes the backing's).
            easing = false; weatherHelmSeconds = 0f; ironsSeconds = 0f; ironsSide = 0;
            float ahead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            Log.Info("Sailing: " + Name + " backs her square sail to go astern (decision 155: " + why + "): the apparent wind " +
                NavigationMath.Delta(h, apparentFrom).ToString("F0") + " degrees off her bow, " + ahead.ToString("F2") + " m/s ahead; her bow to turn to " +
                (side > 0 ? "starboard" : "port") + (throughWind ? ", across the wind" : ", away from the wind") + ", round to " + target.ToString("F0") + " (" + Backing.Remaining(h, target, side).ToString("F0") + " degrees).");
            steeringTarget = Vector(Ahead(p, Backing.Steer(h, target, side), 120));
            SetSailTurn(); TraceFor(90);
            return true;
        }

        // Every half second while backing: done on her heading (within Backing.DoneWithin) or with the wind abaft her beam (her bow
        // has fallen off); given up after Backing.MostSeconds, Backing.MostLengths of her lengths astern, or her way astern closing.
        private bool BackingTick(RoutePoint p)
        {
            double h = boat.Root.eulerAngles.y, apparentFrom = ApparentFrom(), remaining = Backing.Remaining(h, backTarget, backSide);
            float ahead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            double astern = RoutePoint.Distance(backFrom, p);
            // Across the wind (decision 160), on her heading only with her sails able to draw on her new side (run 20261011-152531 on EA:
            // done 20 degrees short of her heading, the wind 17 degrees off her bow, she lay in irons going astern onto Cube).
            bool drawing = !backThroughWind || Math.Abs(NavigationMath.Delta(h, apparentFrom)) >= TackNavigator.BeatOff(DeadZone, Margin);
            if ((remaining <= Backing.DoneWithin || remaining >= 360 - Backing.DoneWithin) && drawing) { EndBacking("she is on her heading", false, p); return true; }
            if (Math.Abs(NavigationMath.Delta(h, apparentFrom)) > Backing.Abaft) { EndBacking("the wind is abaft her beam", false, p); return true; }
            if (remaining < backBestRemaining - Backing.ProgressDegrees) { backBestRemaining = remaining; backProgressAt = Time.time; }
            if (Backing.OutOfTime(Time.time - backSince, Time.time - backProgressAt))
            { EndBacking((Time.time - backSince).ToString("F0") + " s backing, " + (Time.time - backProgressAt).ToString("F0") + " s since her bow last closed " + Backing.ProgressDegrees.ToString("F0") + " degrees on its heading", true, p); return true; }
            if (astern > Backing.MostLengths * water.Length) { EndBacking(astern.ToString("F0") + " m astern", true, p); return true; }
            // From her stern, 8 s of her sternway (decision 164's reach; run 20261011-163355 on EF: half her length from her middle saw under
            // a metre past her stern, and she backed onto east_dock_002 at 0.95 m/s).
            if (ahead < -0.3f && !SternClear(Mathf.Max(DriftLeast, -ahead * DriftSeconds)))
            { EndBacking("her way astern closes (" + water.Obstruction + ")", true, p); return true; }
            backSail = (float)Backing.SailLimit(-ahead, backSail);
            steeringTarget = Vector(Ahead(p, Backing.Steer(h, backTarget, backSide), 120));
            Status.Message = "Backing her sail: " + astern.ToString("F0") + " m astern, " + remaining.ToString("F0") + " degrees to turn to " + backTarget.ToString("F0") + ".";
            return true;
        }

        private void EndBacking(string how, bool failed, RoutePoint p)
        {
            backing = false;
            if (failed) backFails.Add(Time.time);
            double h = boat.Root.eulerAngles.y, apparentFrom = ApparentFrom();
            Log.Info("Sailing: " + Name + " fills her sails again (decision 155: " + how + "): heading " + h.ToString("F0") + " (she was to turn to " +
                backTarget.ToString("F0") + "), " + RoutePoint.Distance(backFrom, p).ToString("F0") + " m astern in " + (Time.time - backSince).ToString("F0") + " s" +
                (failed ? "; a backing that failed (" + backFails.Count + " in " + (Backing.FailWindow / 60).ToString("F0") + " minutes)" : "") + ".");
            if (navigator != null)
            {
                navigator.Restore(TackNavigator.Side(apparentFrom, h), h, apparentFrom, DeadZone);
                navigator.Rechoose("she has backed round");
            }
            SetSailTurn(); TraceFor(30);
        }
    }

    // ==== VoyageDrift ====

    // Decision 164 (the user, 2026-10-11: "drift and going astern when not backing should check astern"): her look-ahead checks
    // where her bow points; with little way on she often moves elsewhere: astern (a backing ended with sternway, in irons with her
    // sail aback) or sideways (her leeway after a stalled tack). Run 20261011-152531 on EA went astern onto Cube; run
    // 20261011-155747 on ED drifted sideways onto east_dock (8) at 1.1 m/s. While she moves over DriftSpeed more than DriftOffBow off
    // her bow (not backing: its own check runs then), her beam and the seabed are swept from her bow, middle and stern along her
    // actual motion for DriftSeconds of it (at least DriftLeast): blocked there, her sail is no longer set aback while she goes astern
    // and her course is chosen again; blocked within DriftNearSeconds (at least DriftNearLeast), she is hemmed in (decision 123's
    // short tow near her berth, else a hold).

    internal sealed partial class Voyage
    {
        internal const float DriftSpeed = 0.3f, DriftOffBow = 45f, DriftSeconds = 8f, DriftLeast = 5f, DriftNearSeconds = 3f, DriftNearLeast = 3f;
        private const float DriftEvery = 0.25f, DriftLogEvery = 5f, DriftHold = 2f;
        private float driftCheckAt, driftLoggedAt, driftAsternUntil, driftRechooseAt;
        private string driftNear;

        // Every DriftEvery: null while her motion is clear (or along her bow), else the reason she is hemmed in.
        private string DriftCheck()
        {
            if (backing || boat.Body.isKinematic || Time.time < driftCheckAt) return driftNear;
            driftCheckAt = Time.time + DriftEvery; driftNear = null;
            var v = Vector3.ProjectOnPlane(boat.Body.velocity, Vector3.up);
            float speed = v.magnitude;
            var forward = Vector3.ProjectOnPlane(boat.Root.forward, Vector3.up);
            if (speed < DriftSpeed || forward.sqrMagnitude < 1e-6f || Vector3.Angle(v, forward) <= DriftOffBow) return null;
            var way = v / speed;
            float far = Mathf.Max(DriftLeast, speed * DriftSeconds), near = Mathf.Max(DriftNearLeast, speed * DriftNearSeconds);
            float ahead = Vector3.Dot(boat.Body.velocity, boat.Root.forward);
            float across = Vector3.Dot(boat.Body.velocity, boat.Root.right);
            string motion = -ahead >= Math.Abs(across) ? "astern" : "sideways to " + (across > 0 ? "starboard" : "port");
            float blockedAt = float.PositiveInfinity;
            string what = null;
            foreach (float along in new[] { water.ForeExtent - water.Radius, 0f, -(water.AftExtent - water.Radius) })
            {
                var from = Point(FloatingOriginManager.instance.ShiftingPosToRealPos(boat.Root.TransformPoint(new Vector3(0, 0, along))));
                // The nearest blocked distance of DriftNear and far, from each of her bow, middle and stern.
                foreach (float reach in new[] { near, far })
                {
                    var to = new RoutePoint(from.x + way.x * reach, from.z + way.z * reach);
                    if (water.ClearBeam(from, to)) continue;
                    if (reach < blockedAt) { blockedAt = reach; what = water.Obstruction; }
                    break;
                }
            }
            if (float.IsPositiveInfinity(blockedAt)) return null;
            if (motion == "astern") driftAsternUntil = Time.time + DriftHold;
            if (Time.time >= driftLoggedAt)
            {
                driftLoggedAt = Time.time + DriftLogEvery;
                Log.Info("Sailing: " + Name + " moves " + motion + " at " + speed.ToString("F2") + " m/s toward " + what + " within " + blockedAt.ToString("F0") +
                    " m (decision 164: her bow " + Vector3.Angle(v, forward).ToString("F0") + " degrees off her motion)" + (blockedAt <= near ? "; hemmed in." : "; her course is chosen again."));
            }
            if (blockedAt <= near) return driftNear = "Moving " + motion + " onto " + what + " within " + blockedAt.ToString("F0") + " m";
            if (Time.time >= driftRechooseAt) { driftRechooseAt = Time.time + DriftHold; navigator?.Rechoose("she moves " + motion + " toward " + what); }
            return null;
        }
    }

    // ==== VoyageSafety ====

    // Adapted from the Ship Transport Prototype's TransportSafety and TransportVoyage (ChatGPT "Astra"'s work for the user,
    // handed over 2026-10-06; decisions 92-93). Islands are kept loaded only while she sails in physics, near the player (D4).

    internal sealed partial class Voyage
    {
        private float visualRetryAt, sailLimit = 1;
        // Her sails' plans this minute (UpdateSailSafety, for the minute line): how many, the longest and the total, in milliseconds.
        private static readonly System.Diagnostics.Stopwatch sailClock = new System.Diagnostics.Stopwatch();
        private int sailPlans;
        private double sailMostMs, sailTotalMs;

        private void TryCreateHelmsman()
        {
            if (helmsman != null || Time.realtimeSinceStartup < visualRetryAt) return;
            visualRetryAt = Time.realtimeSinceStartup + 10;
            try
            {
                var template = Voyages.HelmsmanTemplate();
                if (!template) return;
                helmsman = StaticHelmsman.Create(boat, template, Voyages.HelmsmanOffset, Voyages.HelmsmanYaw);
            }
            catch (Exception e) { Log.Warn("Sailing: helmsman visual deferred; sailing continues: " + e.Message); }
        }

        private void UpdateSailSafety(float dt, float limit = 1)
        {
            float raw = (float)CourseSafety.HeelDegrees(boat.Root.right.y, boat.Root.up.y);
            var previous = heel.State;
            TakingWater = waterIngress.Sample(dt);
            heel.Tick(raw, dt, TakingWater);
            tickClock += dt;
            if (previous != heel.State || tickClock >= .5f || SailLimitChanged(sailLimit, limit) || sails.TurnChanged)
            {
                tickClock = 0; sailLimit = limit;
                // Overflow always owns the retained-sail policy, including while
                // awaiting geometry or recovering an anchor. Use native winches.
                // Timed for the minute line (the review of 2026-10-13: her sails' plans, the trim's search over every yard angle and decision
                // 199's mix, run outside NavigateTick's clock, so no line measured them).
                sailClock.Restart();
                sails.PlanLimited((float)heel.Deployment, heel.State == HeelState.Urgent, limit);
                sailClock.Stop();
                double planMs = sailClock.Elapsed.TotalMilliseconds;
                sailPlans++; sailTotalMs += planMs; if (planMs > sailMostMs) sailMostMs = planMs;
                if (previous != heel.State) Log.Info("Sailing: " + Name + ": heel response changed to " + heel.State + "; deck depth=" + DeckOverflowDepth + "m.");
                // Decision 199: a sail taken in or set, or her sails chosen anew.
                string reefNote = sails.TakeReefNote();
                if (reefNote != null) Log.Info("Sailing: " + Name + " " + reefNote + ".");
            }
            sails.Apply(dt);
        }

        // Decision 225 (6): her sail plan made again for a change of her cap of PlanCapStep or more, or to or from none: near her destination
        // her cap changes every physics step, and an exact compare planned her sails about 25 times as often as meant.
        internal const float PlanCapStep = 0.02f;
        internal static bool SailLimitChanged(float was, float now) => Math.Abs(now - was) >= PlanCapStep || (was <= 0f) != (now <= 0f);

        // Decision 225 (12): a tide wait or an obstructed approach starts her helm's watches afresh (decision 184 (d)), and her helm's
        // controller from the helm it leaves amidships: a weather helm or a spin noted before reefed her on resuming before her bow turned.
        private void HoldNavigation(float dt, float deploymentLimit)
        {
            helm.Demand = 0; helm.Apply(); ResetHelmWatch(); heading.Reset();
            if (!boat.Body.isKinematic && (background == null || background.Mode == SimulationMode.Physical))
                UpdateSailSafety(dt, deploymentLimit);
            navigationClock = 1;
        }

        // ---- Islands near her while she sails in physics ----

        private readonly HashSet<IslandHorizon> voyageIslands = new HashSet<IslandHorizon>();
        private float islandLeaseAt;
        private bool voyageDestinationPinned;
        private static readonly System.Reflection.MethodInfo LoadIsland = AccessTools.Method(typeof(IslandHorizon), "LoadIslandScene");
        private static readonly System.Reflection.FieldInfo IslandUnloading = AccessTools.Field(typeof(IslandHorizon), "unloading");
        internal bool KeepsIsland(IslandHorizon island) => sails != null && voyageIslands.Contains(island);

        private void EnsureVoyageScenes()
        {
            if (Time.realtimeSinceStartup < islandLeaseAt) return;
            islandLeaseAt = Time.realtimeSinceStartup + 2;
            voyageIslands.Clear();
            if (!Physical) return;
            var tracker = IslandDistanceTracker.instance;
            if (!tracker || LoadIsland == null || IslandUnloading == null) throw new NotSupportedException("Native voyage island loading contract unavailable.");
            var here = boat.StablePosition;
            foreach (var island in tracker.islands)
            {
                if (!island || island.islandIndex <= 0) continue;
                var centre = FloatingOriginManager.instance.ShiftingPosToRealPos(island.GetPosition());
                bool destinationApproach = water.DestinationIsland == island &&
                    (voyageDestinationPinned || HorizontalDistance(here, Vector(Job.route[Job.route.Length - 1])) < 3000);
                if (destinationApproach) voyageDestinationPinned = true;
                // Decision 201 (g): the island her destination berth lies on held loaded from ArrivalChartFrom off the berth, where her
                // destination is charted (beyond its load distance and 2 km from its centre the rule below would let it go).
                bool berthIsland = Job.berthKnown && HorizontalDistance(here, Vector(Job.destination)) < ArrivalChartFrom &&
                    HorizontalDistance(Vector(Job.destination), centre) < island.islandLoadDistance;
                if (!destinationApproach && !berthIsland && HorizontalDistance(here, centre) > island.islandLoadDistance + 2000) continue;
                voyageIslands.Add(island);
                if (!island.SceneLoaded() && !SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded && !(bool)IslandUnloading.GetValue(island))
                    LoadIsland.Invoke(island, null);
            }
        }
    }

    // ==== VoyageStatus ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public enum TransportPhase { Idle, Preparing, Sailing, Suspended, Finished }

    public sealed class TransportStatus
    {
        public TransportPhase Phase { get; internal set; }
        public string Message { get; internal set; } = "No active test.";
        public override string ToString() => Phase + ": " + Message;
    }

    // ==== VoyageJob ====

    // Adapted from the Ship Transport Prototype's TransportJob (ChatGPT "Astra"'s work for the user, handed over 2026-10-06;
    // decisions 92-93), with the clock of far travel (decision 95, D7).

    // A sailing booking's voyage while the game runs. Its saved form is SailingJob (Core\SaveData.cs).
    internal sealed class VoyageJob
    {
        internal string jobId, vesselName, rig;
        internal int vesselScene;
        internal RoutePoint[] route;
        internal int waypoint = 1, tack, revision;
        internal bool departureComplete;
        internal bool unberthing;
        internal bool toBerth;
        internal double[] progressState;
        internal float deployment = 1, savedSpeed, referenceWind;
        internal BodyFlag[] physics;
        internal RoutePoint destination;
        internal float dockHeading;
        internal bool berthKnown;
        internal int dockingPhase;
        internal double done, clockHour, speed, length;
        internal bool onPaper;
        internal bool leftOrigin, towedOut;
        internal bool anchorKnown;
        internal RoutePoint anchor;
        internal int anchorStage;

        internal static VoyageJob From(SailingJob saved, int vesselScene)
        {
            if (saved == null) throw new ArgumentNullException(nameof(saved));
            if (saved.routeX == null || saved.routeZ == null || saved.routeX.Length != saved.routeZ.Length)
                throw new FormatException("Missing or mismatched route coordinates.");
            if ((saved.bodyPaths == null) != (saved.bodyKinematic == null) || saved.bodyPaths != null && saved.bodyPaths.Length != saved.bodyKinematic.Length)
                throw new FormatException("Missing or mismatched body manifest.");
            var job = new VoyageJob
            {
                jobId = saved.jobId, vesselName = saved.vesselName, rig = saved.rig, vesselScene = vesselScene,
                route = saved.routeX.Select((x, i) => new RoutePoint(x, saved.routeZ[i])).ToArray(),
                waypoint = saved.waypoint, tack = saved.tack, revision = saved.revision, departureComplete = saved.departureComplete,
                unberthing = saved.unberthing && saved.departureComplete, toBerth = saved.toBerth && !saved.departureComplete, progressState = saved.progressState == null || saved.progressState.Length == 0 ? null : (double[])saved.progressState.Clone(),
                deployment = saved.deployment, savedSpeed = saved.savedSpeed, referenceWind = saved.referenceWind,
                physics = saved.bodyPaths?.Select((p, i) => new BodyFlag { path = p, kinematic = saved.bodyKinematic[i] }).ToArray(),
                destination = new RoutePoint(saved.destinationX, saved.destinationZ), dockHeading = saved.dockHeading,
                berthKnown = saved.berthKnown, dockingPhase = saved.dockingPhase,
                done = saved.done, clockHour = saved.clockHour, speed = saved.speed, length = saved.length, onPaper = saved.onPaper,
                leftOrigin = saved.leftOrigin, towedOut = saved.towedOut,
                anchorKnown = saved.anchorKnown, anchor = new RoutePoint(saved.anchorX, saved.anchorZ), anchorStage = saved.anchorStage,
            };
            string error = job.Validate();
            if (error != null) throw new FormatException(error);
            return job;
        }

        internal SailingJob ToRecord() => new SailingJob
        {
            jobId = jobId ?? "", vesselName = vesselName ?? "", rig = rig ?? "",
            routeX = route.Select(p => p.x).ToArray(), routeZ = route.Select(p => p.z).ToArray(),
            waypoint = waypoint, tack = tack, revision = revision, departureComplete = departureComplete, unberthing = unberthing, toBerth = toBerth,
            progressState = progressState == null ? null : (double[])progressState.Clone(),
            deployment = deployment, savedSpeed = savedSpeed, referenceWind = referenceWind,
            bodyPaths = physics?.Select(p => p.path).ToArray(), bodyKinematic = physics?.Select(p => p.kinematic).ToArray(),
            destinationX = destination.x, destinationZ = destination.z, dockHeading = dockHeading, berthKnown = berthKnown,
            dockingPhase = dockingPhase, done = done, clockHour = clockHour, speed = speed, length = length, onPaper = onPaper,
            leftOrigin = leftOrigin, towedOut = towedOut,
            anchorKnown = anchorKnown, anchorX = anchor.x, anchorZ = anchor.z, anchorStage = anchorStage,
        };

        internal string Validate()
        {
            if (!Guid.TryParse(jobId, out _) || vesselScene <= 0 || string.IsNullOrEmpty(vesselName) || string.IsNullOrEmpty(rig)) return "Incomplete vessel/job identity.";
            if (route == null || route.Length < 2 || route.Length > 256 || waypoint < 1 || waypoint >= route.Length) return "Invalid route progress.";
            if (tack < -1 || tack > 1 || revision < 0 || !NavigationMath.Finite(deployment) || deployment < 0 || deployment > 1) return "Invalid control snapshot.";
            if (!NavigationMath.Finite(savedSpeed) || savedSpeed < 0 || savedSpeed > 12 || !NavigationMath.Finite(referenceWind) || referenceWind < 0 || referenceWind > 1000) return "Invalid saved motion sample.";
            if (ProgressWatchdog.ValidateSnapshot(progressState) != null) return "Invalid saved progress state.";
            if (!RoutePlanner.Finite(destination) || !NavigationMath.Finite(dockHeading) || dockingPhase < 0 || dockingPhase > 3 || dockingPhase > 0 && !berthKnown)
                return "Invalid voyage destination/docking state.";
            if (anchorStage < 0 || anchorStage > 2 || anchorKnown && !RoutePlanner.Finite(anchor)) return "Invalid anchor stage.";
            if (!NavigationMath.Finite(done) || !NavigationMath.Finite(clockHour) || !NavigationMath.Finite(speed) || !NavigationMath.Finite(length) ||
                done < 0 || speed < 0 || length <= 0 || done > length + 1) return "Invalid voyage clock.";
            if (physics != null)
            {
                if (physics.Length > 128) return "Invalid body manifest size.";
                var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var body in physics) if (body == null || string.IsNullOrEmpty(body.path) || !keys.Add(body.path)) return "Duplicate/missing body identity.";
            }
            for (int i = 0; i < route.Length; ++i)
            {
                if (!RoutePlanner.Finite(route[i])) return "Invalid stable waypoint.";
                if (i > 0 && RoutePoint.Distance(route[i - 1], route[i]) < 0.01) return "Duplicate route waypoint.";
            }
            if (RouteMath.Length(route) > RoutePlanner.MaximumVoyageDistance * 4) return "Saved route exceeds supported length.";
            return null;
        }
    }

    [Serializable] internal sealed class BodyFlag { public string path; public bool kinematic; }

    // The route as a line for the clock of far travel (D7): its length, the point and heading at a distance along it, and the
    // distance of the point on it nearest a place.
    internal static class RouteMath
    {
        internal static double Length(RoutePoint[] route)
        {
            double total = 0;
            for (int i = 1; i < route.Length; i++) total += RoutePoint.Distance(route[i - 1], route[i]);
            return total;
        }

        // The point at `distance` metres along the route, and the heading of its leg; past either end, that end.
        internal static RoutePoint PointAt(RoutePoint[] route, double distance, out double heading)
        {
            heading = route.Length > 1 ? NavigationMath.Heading(route[1].x - route[0].x, route[1].z - route[0].z) : 0;
            if (distance <= 0) return route[0];
            for (int i = 1; i < route.Length; i++)
            {
                var a = route[i - 1]; var b = route[i];
                double leg = RoutePoint.Distance(a, b);
                heading = NavigationMath.Heading(b.x - a.x, b.z - a.z);
                if (distance <= leg)
                {
                    double t = leg < 1e-9 ? 1 : distance / leg;
                    return new RoutePoint((float)(a.x + (b.x - a.x) * t), (float)(a.z + (b.z - a.z) * t));
                }
                distance -= leg;
            }
            return route[route.Length - 1];
        }

        // The distance along the route of the point on its legs from `fromLeg` on that lies nearest `p`.
        internal static double Project(RoutePoint[] route, RoutePoint p, int fromLeg = 1)
        {
            double before = 0, best = double.PositiveInfinity, bestAlong = 0;
            fromLeg = Math.Max(1, Math.Min(fromLeg, route.Length - 1));
            for (int i = 1; i < route.Length; i++)
            {
                var a = route[i - 1]; var b = route[i];
                double dx = b.x - a.x, dz = b.z - a.z, leg2 = dx * dx + dz * dz;
                double t = leg2 < 1e-12 ? 0 : NavigationMath.Clamp(((p.x - a.x) * dx + (p.z - a.z) * dz) / leg2, 0, 1);
                double x = a.x + dx * t - p.x, z = a.z + dz * t - p.z, gap = x * x + z * z;
                double leg = Math.Sqrt(leg2);
                if (i >= fromLeg && gap < best) { best = gap; bestAlong = before + leg * t; }
                before += leg;
            }
            return bestAlong;
        }

        // The clock of far travel (decision 101): `elapsed` game hours on from `done` metres at game hour `hour`. Before the quoted
        // hour she covers what is left of her route by that hour, wherever her sailing left her; once it has passed, she goes on
        // at the paper speed and arrives late. `reachedAt` is the hour she reaches the route's end in this stretch, else NaN.
        internal static double ClockAdvance(double length, double done, double hour, double elapsed, double arriveHour, double paperSpeed, out double reachedAt)
        {
            reachedAt = double.NaN;
            if (!(elapsed > 0) || done >= length) return Math.Min(length, done);
            double left = length - done, toGo = arriveHour - hour;
            if (toGo > 1e-9)
            {
                if (elapsed >= toGo) { reachedAt = arriveHour; return length; }
                return done + left * (elapsed / toGo);
            }
            if (!(paperSpeed > 0)) { reachedAt = hour; return length; }
            double next = done + paperSpeed * elapsed;
            if (next < length) return next;
            reachedAt = hour + left / paperSpeed;
            return length;
        }

        // The leg (index of its end point) that holds `distance`.
        internal static int LegAt(RoutePoint[] route, double distance)
        {
            for (int i = 1; i < route.Length; i++)
            {
                double leg = RoutePoint.Distance(route[i - 1], route[i]);
                if (distance <= leg) return i;
                distance -= leg;
            }
            return route.Length - 1;
        }
    }

    // ==== SailingPatches ====

    // Adapted from the Ship Transport Prototype's TransportPatches (ChatGPT "Astra"'s work for the user, handed over 2026-10-06;
    // decisions 92-93): the same hooks, now asking every voyage in service (Voyages). The save hooks are merged into
    // Shipwright's own (Core\SaveHooks.cs).

    internal static class SailingPatches
    {
        private static FieldInfo pointedAt;

        // All or nothing: a hook that no longer fits the game leaves the sailing transport off (D1: paperwork).
        internal static bool Install(Harmony harmony)
        {
            try
            {
                GameBindings.Verify();
                GameBindings.WaterPatchReady = false;
                pointedAt = GameBindings.PointedButton;
                Patch(harmony, AccessTools.Method(typeof(PlayerEmbarkerNew), "PlayerEmbark", Type.EmptyTypes), prefix: nameof(PlayerEmbarkPrefix), priority: Priority.First);
                Patch(harmony, AccessTools.Method(typeof(BoatHorizon), "Update"), prefix: nameof(HorizonPrefix), priority: Priority.First);
                Patch(harmony, AccessTools.Method(typeof(IslandHorizon), "UnloadIslandScene"), prefix: nameof(IslandUnloadPrefix));
                foreach (var type in new[] { typeof(Sail), typeof(Rudder), typeof(Anchor) })
                    Patch(harmony, AccessTools.Method(type, type == typeof(Anchor) ? "ExtraFixedUpdate" : "FixedUpdate"), prefix: nameof(PhysicsPrefix), priority: Priority.First);
                Patch(harmony, AccessTools.Method(typeof(GoPointer), "LateUpdate"), prefix: nameof(GoPointerPrefix));
                Patch(harmony, AccessTools.Method(typeof(GoPointer), "PickUpItem"), prefix: nameof(PickUpPrefix));
                Patch(harmony, AccessTools.Method(typeof(GoPointerButton), "Click"), prefix: nameof(ButtonActionPrefix));
                Patch(harmony, AccessTools.Method(typeof(GoPointerButton), "StickyClick"), prefix: nameof(ButtonActionPrefix));
                PatchButtonActivationOverrides(harmony);
                Patch(harmony, AccessTools.Method(typeof(GPButtonRopeWinch), "Update"), prefix: nameof(WinchUpdatePrefix), priority: Priority.First);
                Patch(harmony, AccessTools.Method(typeof(TouchRotateHandle), "Update"), prefix: nameof(HandleUpdatePrefix));
                var grabbableType = AccessTools.TypeByName("OVRGrabbable");
                var grabBegins = grabbableType == null ? Array.Empty<MethodInfo>() : grabbableType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "GrabBegin").ToArray();
                if (grabBegins.Length == 0) throw new NotSupportedException("OVRGrabbable.GrabBegin was not found; cannot guard sailing handle grabs.");
                foreach (var grabBegin in grabBegins) Patch(harmony, grabBegin, prefix: nameof(GrabBeginPrefix));
                // Decision 94 as changed by the user (2026-10-10): only water is kept out of a ship in service (the deck's
                // overflow, the intake through her hull). Daily wear (BoatDamage.DailyDamage) and collisions (BoatDamage.Impact)
                // damage her as any ship.
                Patch(harmony, AccessTools.Method(typeof(BoatDamage), nameof(BoatDamage.Overflow)), prefix: nameof(OverflowPrefix), postfix: nameof(OverflowPostfix), priority: Priority.First);
                Patch(harmony, AccessTools.Method(typeof(BoatDamage), "UpdateWaterAndDrag"), transpiler: nameof(WaterIntakeTranspiler));
                Patch(harmony, AccessTools.Method(typeof(SaveLoadManager), "SaveGame", new[] { typeof(bool) }), prefix: nameof(SavePrefix));
                Patch(harmony, AccessTools.Method(typeof(Shipyard), "OnTriggerEnter", new[] { typeof(Collider) }), prefix: nameof(ShipyardAreaPrefix));
                GameBindings.WaterPatchReady = true;
                return true;
            }
            catch (Exception e)
            {
                GameBindings.WaterPatchReady = false;
                Log.Error("Sailing transport is off (every transport goes on paper): a game hook no longer fits: " + e.Message);
                return false;
            }
        }

        private static void PatchButtonActivationOverrides(Harmony harmony)
        {
            var names = new[] { "OnActivate", "OnActivateHit", "OnAltActivate", "OnUnactivate", "OnAltHeld" };
            Type[] types;
            try { types = typeof(GoPointerButton).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !typeof(GoPointerButton).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (names.Contains(method.Name)) Patch(harmony, method, prefix: nameof(ButtonActionPrefix));
                var itemClick = type.GetMethod("OnItemClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (itemClick != null) Patch(harmony, itemClick, prefix: nameof(ItemClickPrefix));
            }
        }

        private static void Patch(Harmony harmony, MethodBase original, string prefix = null, string transpiler = null, int priority = Priority.Normal, string postfix = null)
        {
            if (original == null) throw new NotSupportedException("A required game patch target is missing.");
            harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SailingPatches), prefix)) { priority = priority },
                postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SailingPatches), postfix)),
                transpiler: transpiler == null ? null : new HarmonyMethod(AccessTools.Method(typeof(SailingPatches), transpiler)));
        }

        private static void SavePrefix() => Voyages.BeforeSave();

        // A ship in service never counts in a shipyard's area (Voyage.Attach takes her off the lists she is on already), so no
        // shipyard admits her; while she waits there or comes in, the shipyard refuses every ship (decisions 106, 218:
        // Voyages.ReleaseRefusal). It kept the shipyard free while she waited (decision 99, deleted by 218).
        public static bool ShipyardAreaPrefix(Collider other) => !other || !Voyages.InService(other);

        public static void GoPointerPrefix(GoPointer __instance)
        {
            if (!__instance || pointedAt == null) return;
            var current = pointedAt.GetValue(__instance) as Component;
            if (current && Voyages.IsProtected(current)) pointedAt.SetValue(__instance, null);
        }

        public static bool PickUpPrefix(PickupableItem item) => !item || !Voyages.IsProtected(item);
        public static bool ButtonActionPrefix(GoPointerButton __instance) => !__instance || !Voyages.IsProtected(__instance);

        public static bool ItemClickPrefix(GoPointerButton __instance, ref bool __result)
        {
            if (!__instance || !Voyages.IsProtected(__instance)) return true;
            __result = false;
            return false;
        }

        public static bool HandleUpdatePrefix(TouchRotateHandle __instance) => !__instance || !Voyages.IsProtected(__instance);
        public static bool GrabBeginPrefix(Component __instance) => !__instance || !Voyages.IsProtected(__instance);

        public static bool WinchUpdatePrefix(GPButtonRopeWinch __instance)
        {
            if (!__instance || !Voyages.IsProtected(__instance)) return true;
            // VirtualCrew's winch prefix runs after this one whatever it returns (HarmonyX runs every prefix): its orders for
            // this winch go first (D8).
            Compat.VirtualCrew.DropOrders(__instance);
            // Vanilla assumes its small audio pool always has room. Concurrent AI winches can
            // exhaust it; wait for a free voice without changing another vessel's audio/input.
            var current = GameBindings.WinchCurrentAudio.GetValue(__instance) as AudioSource;
            bool moving = (float)GameBindings.WinchInput.GetValue(__instance) != 0;
            if (current || !moving || (WinchAudioPlayer.instance && WinchAudioPlayer.instance.GetFreeAudio()))
                GameBindings.WinchAudio.Invoke(__instance, null);
            return false;
        }

        public static bool OverflowPrefix(BoatDamage __instance, float heightDiff, out float __state)
        {
            __state = __instance ? __instance.waterLevel : 0f;
            // Observe the native intake signal before the existing transport
            // protection suppresses its water-level mutation.
            if (__instance && GameState.playing && !GameState.justStarted && !GameState.justWokeUp && heightDiff > 0)
                Voyages.ObserveOverflow(__instance, heightDiff);
            return !__instance || !Voyages.IsProtectedDamage(__instance);
        }

        // Decision 94 (no water, no sinking) against other mods' Overflow prefixes, which HarmonyX runs whatever this one
        // returns: RadFixes' adds the overflow's water itself (launch 5, candidates K and L: the Dhow sank at her berth while
        // waiting). Her water before the call (this prefix runs first) is put back after it.
        public static void OverflowPostfix(BoatDamage __instance, float __state)
        {
            if (__instance && __instance.waterLevel > __state && Voyages.IsProtectedDamage(__instance)) __instance.waterLevel = __state;
        }

        public static bool HorizonPrefix(BoatHorizon __instance) => Voyages.AllowHorizonUpdate(__instance);
        public static bool IslandUnloadPrefix(IslandHorizon __instance) => !Voyages.KeepsIsland(__instance);

        // Decision 225 (22): Custom Island API (Shattered Seas' islands; 1.0.2 decompiled 2026-11-03) unloads its own islands in its own
        // prefix on IslandHorizon.UnloadIslandScene, which starts its unload whatever IslandUnloadPrefix returns (HarmonyX runs every
        // prefix): its method itself is held off for an island a voyage keeps. Looked for once every plugin is loaded
        // (Compat.Mods.ApplyLatePatches); without it nothing is patched.
        internal const string CustomIslandPatchType = "CustomIslandAPI.MethodPatcher+Patch_IslandHorizon", CustomIslandUnload = "UnloadIslandScenePatch";

        internal static void HoldCustomIslandUnload(Harmony harmony)
        {
            if (!Voyages.Enabled) return;
            var patchType = AccessTools.TypeByName(CustomIslandPatchType);
            if (patchType == null) return;
            var method = CustomIslandUnloadMethod(patchType);
            if (method == null) { Log.Warn("Sailing: Custom Island API's island unload isn't as expected; its islands may unload under a ship in service."); return; }
            try { harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(SailingPatches), nameof(CustomIslandUnloadPrefix)))); }
            catch (Exception e) { Log.Warn("Sailing: Custom Island API's island unload couldn't be held off: " + e.Message); }
        }

        // Its unload prefix as this patch expects it: static, returning whether the game's unload runs, the island its first argument.
        internal static MethodInfo CustomIslandUnloadMethod(Type patchType)
        {
            var method = patchType == null ? null : AccessTools.Method(patchType, CustomIslandUnload);
            if (method == null || !method.IsStatic || method.ReturnType != typeof(bool)) return null;
            var parameters = method.GetParameters();
            return parameters.Length > 0 && parameters[0].ParameterType == typeof(IslandHorizon) ? method : null;
        }

        // On its UnloadIslandScenePatch(IslandHorizon __instance, ...): the island is its first argument (`__0`; its own name is Harmony's
        // word for an instance, and the method is static). Kept: neither its unload nor the game's runs.
        public static bool CustomIslandUnloadPrefix(IslandHorizon __0, ref bool __result)
        {
            if (!__0 || !Voyages.KeepsIsland(__0)) return true;
            __result = false;
            return false;
        }
        public static bool PhysicsPrefix(Component __instance) => Voyages.AllowPhysicsStep(__instance);

        public static bool PlayerEmbarkPrefix(PlayerEmbarkerNew __instance, EmbarkBoat ___currentBoat)
        {
            if (___currentBoat != null && Voyages.IsBoardingBlocked(___currentBoat.worldBoat))
            {
                BoardingRetry.Wait(__instance, ___currentBoat.worldBoat);
                return false;
            }
            BoardingRetry.Clear(__instance);
            return true;
        }

        private static bool AllowIntake(bool original, BoatDamage damage) => original && !Voyages.IsProtectedDamage(damage);

        public static IEnumerable<CodeInstruction> WaterIntakeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var lastBoat = AccessTools.Field(typeof(GameState), "lastBoat");
            var transformGetter = AccessTools.PropertyGetter(typeof(Component), "transform");
            var equality = AccessTools.Method(typeof(UnityEngine.Object), "op_Equality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) });
            if (lastBoat == null || transformGetter == null || equality == null)
                throw new NotSupportedException("Could not resolve the exact vanilla water-intake comparison.");
            var matches = new List<int>();
            for (var i = 4; i < code.Count; ++i)
            {
                if ((code[i].opcode == OpCodes.Brfalse || code[i].opcode == OpCodes.Brfalse_S) &&
                    code[i - 1].Calls(equality) && code[i - 2].Calls(transformGetter) &&
                    code[i - 3].opcode == OpCodes.Ldarg_0 && code[i - 4].opcode == OpCodes.Ldsfld &&
                    Equals(code[i - 4].operand, lastBoat)) matches.Add(i);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException("Expected exactly one GameState.lastBoat == boat.transform gate in BoatDamage.UpdateWaterAndDrag; found " + matches.Count + ".");
            var allowIntake = AccessTools.Method(typeof(SailingPatches), nameof(AllowIntake));
            var insertion = matches[0]; // insert while bool is on stack, directly before its branch
            var damageArg = new CodeInstruction(OpCodes.Ldarg_0);
            code[insertion].MoveLabelsTo(damageArg);
            code.Insert(insertion++, damageArg);
            code.Insert(insertion, new CodeInstruction(OpCodes.Call, allowIntake));
            return code;
        }
    }

    // ==== BackgroundTravel ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).
    // Far travel follows the route by the game clock (decision 95, D7) instead of the prototype's estimated-speed stepper; the
    // handover (caching, restoring, the body manifest, cargo readiness) is the prototype's.

    // Owns distance/physics policy only for the selected job. Vanilla owns item records and spawning.
    internal sealed class BackgroundTravel : IDisposable
    {
        private static readonly FieldInfo ItemsLoaded = AccessTools.Field(typeof(BoatLocalItems), "itemsLoaded");
        private static readonly FieldInfo HorizonCooldown = AccessTools.Field(typeof(BoatHorizon), "updateCooldown");
        private readonly BoatContext boat;
        private readonly VoyageJob job;
        private readonly DistanceHandover handover = new DistanceHandover();
        private readonly Dictionary<Rigidbody, bool> bodyFlags = new Dictionary<Rigidbody, bool>();
        private int[] expectedCargoIds = Array.Empty<int>();
        private readonly List<Transform> externalRoots = new List<Transform>();
        private SaveablePrefab[] retiringCargo = Array.Empty<SaveablePrefab>();
        private Quaternion cargoRotation;
        private float entrySpeed, transitionSeconds;
        private Vector3 entryVelocity, previousObserver;
        private bool releaseCargoCompleted;
        private bool wasDisposed;
        internal SimulationMode Mode => handover.Mode;
        internal bool ReturnedToPhysics { get; private set; }
        // The clock brought her to the route's end; ReachedHour is the game hour it did.
        internal bool Reached => job.done >= job.length - 0.01;
        internal double ReachedHour { get; private set; } = double.NaN;
        internal float Speed => Mode == SimulationMode.Physical ? Mathf.Max(0,Vector3.Dot(boat.Body.velocity,boat.Root.forward)) :
            Mode == SimulationMode.Caching ? entrySpeed : ClockSpeedPerSecond;
        // Her paper speed in metres a real second at the game's normal pace (a game hour lasts 1 / 0.008 s = 125 s): the speed
        // she is given back in physics. Not the live Sun.initialTimescale, which anything speeding the clock (the QA's fast
        // clock) multiplies: she came back at 12 m/s then (review M11).
        private float ClockSpeedPerSecond => Mathf.Min(12f, (float)(job.speed * Miles.DefaultTimescale));
        // The quoted arrival hour: the clock brings her in then (decision 101).
        private readonly double arriveHour;
        internal string Message { get; private set; } = "Physical sailing";
        internal BodyFlag[] CaptureBodyFlags() => bodyFlags.Where(p=>p.Key).Select(p=>new BodyFlag {path=BodyKey(p.Key),kinematic=p.Value}).ToArray();

        // `underWay`: she had left her berth before; her clock then goes on from the hour it last moved her, so the game hours
        // between a save (the sailor lets go) and this resume count (N13: launch 5's S6 saw 0 m in 2.26 h). At a departure by
        // the clock Voyages counts from her departure hour instead.
        internal void ResumeCached(VoyageJob saved, bool underWay)
        {
            double since = job.clockHour;
            // Also near (N13): a ship the game holds frozen with her cargo packed (after a save let her far travel go, the player came
            // back to 388 m, inside the sailor's range but outside the game's unpacking one) resumes here, and the next tick restores
            // her (the handover asks for it within SailAgainWithin, decision 221; RefreshHorizon has the game unpack her).
            if (boat.PlayerAboard || !boat.Items.HasLocalItems() || (bool)ItemsLoaded.GetValue(boat.Items))
                throw new InvalidOperationException("Distant resume requires a completed vanilla cargo cache.");
            if (saved.physics==null || saved.physics.Length==0)
                throw new InvalidOperationException("Saved physics manifest does not match vessel: no saved bodies; loaded="+string.Join(";",bodyFlags.Keys.Select(BodyKey)));
            var manifest=new Dictionary<string,bool>(StringComparer.Ordinal);
            foreach (var p in saved.physics) manifest[p.path]=p.kinematic;
            if (!manifest.ContainsKey(BodyKey(boat.Body)))
                throw new InvalidOperationException("Saved physics manifest does not match vessel: her hull's body is not in it. Saved="+string.Join(";",saved.physics.Select(p=>p.path))+"; loaded="+string.Join(";",bodyFlags.Keys.Select(BodyKey)));
            // Each body rebound by its path; one the saved list hasn't keeps its own flag, and a saved one that is gone is passed
            // over, both logged (run 20261010-102301: the sail's collider bodies, col_parent and its cols, were there at the far
            // start but not when her list was taken at the berth, and every far departure was refused, "does not match vessel").
            var added=new List<string>();
            foreach (var body in bodyFlags.Keys.ToArray())
            {
                if (manifest.TryGetValue(BodyKey(body),out bool flag)) bodyFlags[body]=flag;
                else added.Add(BodyKey(body));
            }
            var loadedKeys=new HashSet<string>(bodyFlags.Keys.Select(BodyKey),StringComparer.Ordinal);
            var gone=manifest.Keys.Where(k=>!loadedKeys.Contains(k)).ToArray();
            if (added.Count>0 || gone.Length>0)
                Log.Info("Sailing: her bodies differ from the list saved at the berth: new, kept as they are: "+(added.Count>0?string.Join(", ",added.ToArray()):"none")+
                    "; gone: "+(gone.Length>0?string.Join(", ",gone):"none")+".");
            var cached=boat.Items.GetCachedItems();
            expectedCargoIds=ExpectedCargo(cached);
            if (expectedCargoIds.Distinct().Count()!=expectedCargoIds.Length || SaveLoadManager.instance.GetCurrentPrefabs().Any(p=>p && expectedCargoIds.Contains(p.instanceId)))
                throw new InvalidOperationException("Cargo cache is not exclusive during resume.");
            entrySpeed=saved.savedSpeed; cargoRotation=boat.Root.rotation;
            entryVelocity=boat.Root.forward*entrySpeed;
            handover.Observe(Math.Max(boat.ObserverDistance,DistanceHandover.SailWithin+1),false,2);
            if (!handover.CommitCached(handover.Generation)) throw new InvalidOperationException("Resume handover did not acquire background state.");
            EnterClock();
            if (underWay && NavigationMath.Finite(since) && since <= job.clockHour) job.clockHour = since;
            Freeze(); RefreshHorizon(); Message="Resumed far travel by the clock from the saved place";
        }
        private string BodyKey(Rigidbody body) => BodyKey(boat, body);

        private static string BodyKey(BoatContext boat, Rigidbody body)
        {
            Transform root=boat.Root; string prefix="boat/";
            if (body.transform==boat.Anchor.transform || body.transform.IsChildOf(boat.Anchor.transform)) {root=boat.Anchor.transform; prefix="anchor/";}
            else for(int i=0;i<boat.Moorings.ropes.Length;++i)
            {
                var rope=boat.Moorings.ropes[i];
                if (rope && (body.transform==rope.transform || body.transform.IsChildOf(rope.transform))) {root=rope.transform; prefix="mooring"+i+"/"; break;}
            }
            // The rig's path (Voyage.Path): names and same-name counts, so other children coming or going don't change it.
            return prefix+Voyage.Path(body.transform, root);
        }

        // Called as she starts going out of the player's range (BeginCaching): the voyage lets go of her berth if she
        // hadn't left it yet.
        internal Action LeavingPhysics;
        // Let go for a game save (Voyage.CaptureForSave): Dispose bridges the 2 s until her sailor takes her back (decision 221).
        internal bool ForSave;
        internal const float SaveBridgeSeconds = 10f;

        internal BackgroundTravel(BoatContext boat, VoyageJob job, double arriveHour)
        {
            this.boat = boat; this.job = job; this.arriveHour = arriveHour;
            if (ItemsLoaded == null || HorizonCooldown == null) throw new NotSupportedException("Cargo/horizon bindings missing.");
            if (boat.Items.debugDisableAutoCache) throw new NotSupportedException("Vanilla automatic cargo caching must be enabled.");
            if (!SaveLoadManager.instance || !global::Refs.observerMirror) throw new InvalidOperationException("World references not ready.");
            CollectBodies(boat, bodyFlags, externalRoots);
            if (!boat.Root.GetComponentsInChildren<BoatEmbarkCollider>(true).Any(e => e.GetComponents<Collider>().Any(c => c.enabled)))
                throw new NotSupportedException("No active cargo/boarding triggers found.");
            previousObserver = ObserverPosition();
        }

        // Her bodies (hull, sails, anchor, lines) with their kinematic flags, and the roots outside her hierarchy (anchor,
        // lines). The game holds a ship far from the player kinematic (BoatHorizon.UpdateKinematic) through her root body
        // only; a ship physical under sail never is.
        private static void CollectBodies(BoatContext boat, Dictionary<Rigidbody, bool> flags, List<Transform> externals)
        {
            foreach (var body in boat.Root.GetComponentsInChildren<Rigidbody>(true))
                if (!IsPlayerBody(body) && !body.GetComponentInParent<ShipyardSailColChecker>() && !body.GetComponentInParent<SaveablePrefab>() && !body.GetComponent<ItemRigidbody>()) flags[body] = body.isKinematic;
            IncludeExternal(boat, boat.Anchor.transform, flags, externals);
            foreach (var rope in boat.Moorings.ropes)
                if (rope && rope.GetBoatRigidbody() == boat.Body) IncludeExternal(boat, rope.transform, flags, externals);
            if (boat.Body) flags[boat.Body] = false;
        }

        private static void IncludeExternal(BoatContext boat, Transform transform, Dictionary<Rigidbody, bool> flags, List<Transform> externals)
        {
            if (transform.IsChildOf(boat.Root) || transform == boat.Root) return;
            externals?.Add(transform);
            foreach (var body in transform.GetComponentsInChildren<Rigidbody>(true)) if(!IsPlayerBody(body)) flags[body] = body.isKinematic;
        }

        // The body manifest of a ship as she lies now (physical), for a later far resume (ResumeCached).
        internal static BodyFlag[] Manifest(BoatContext boat)
        {
            var flags = new Dictionary<Rigidbody, bool>();
            CollectBodies(boat, flags, null);
            return flags.Where(p => p.Key).Select(p => new BodyFlag { path = BodyKey(boat, p.Key), kinematic = p.Value }).ToArray();
        }
        private static bool IsPlayerBody(Rigidbody body)
        {
            var observer=global::Refs.observerMirror;
            var player=global::Refs.charController;
            return observer && (body.transform==observer.transform || body.transform.IsChildOf(observer.transform)) ||
                player && (body.transform==player.transform || body.transform.IsChildOf(player.transform));
        }

        internal bool Owns(BoatHorizon horizon) => !wasDisposed && horizon == boat.Horizon;

        internal bool BlocksBoarding(Transform model) => !wasDisposed && Mode != SimulationMode.Physical &&
            model && (model == boat.Root || model.IsChildOf(boat.Root));

        // Called instead of this boat's vanilla horizon Update. Never replaces another boat's loop.
        internal void RefreshHorizon()
        {
            if (wasDisposed || !boat.Valid) return;
            // While the game is to cache or spawn her cargo she is kept just within her horizon, so the game keeps her switched on
            // and its own BoatLocalItems.Update does it (run 20261010-132249's S9: the player moved 88 km off at once; switched off
            // beyond her horizon before her cargo was cached, she waited "for vanilla cargo cache" for good).
            // Decision 221: shown while she sails, packs or unpacks (physical too: switched off by the game while physical she didn't
            // move, run 20261010-111618); hidden while the clock carries her beyond SailAgainWithin, also a hull the game would show farther
            // (modded hulls shown to 10 or 30 km; Claude's call, decision 229, as the user capped the tow's pickup at 3,000 m). Decision 234:
            // within it she stays shown on the clock too: a load near the player resumes her by the clock for the tick before her restore.
            float distance = (float)boat.ObserverDistance, sunk = boat.Horizon.fullySunkDistance;
            if (sunk > 1f)
            {
                bool shown = Mode != SimulationMode.Background || distance <= DistanceHandover.SailAgainWithin;
                distance = shown ? Mathf.Min(distance, sunk - 1f) : Mathf.Max(distance, sunk);
            }
            boat.Horizon.distanceToPlayer = distance;
            // Fix A: also shut while a shift is under way and her cache hasn't spawned (this runs every frame, Tick may not).
            boat.Horizon.closeToPlayer = Mode == SimulationMode.Physical || Mode == SimulationMode.Restoring && !spawnHeld &&
                !(CachePending() && (FloatingOriginManager.ShiftingThisFrame || GameState.waitingForShift || IsShifting()));
            HorizonCooldown.SetValue(boat.Horizon, 0f);
            if (FloatingOriginManager.ShiftingThisFrame || IsShifting()) return;
            if (Mode != SimulationMode.Physical) Freeze();
            else if (Time.timeScale > 0) // BoatPhysicsSwitcher owns pause momentum.
                boat.Body.isKinematic = !GameState.playing || GameState.currentlyLoading || GameState.justStarted ||
                    GameState.recovering || GameState.changingStartRegion ||
                    (GameState.currentShipyard && GameState.currentShipyard.GetCurrentBoat()==boat.Root.gameObject) ||
                    Debugger.debugForceKinematicBoat || !boat.Items.SafeToEnableBoatPhysics() && !CargoLeftCached;
        }

        // True means the physical controller must NOT advance this tick.
        internal bool Tick(float dt)
        {
            ReturnedToPhysics = false;
            if (wasDisposed) return true;
            WatchShifts(dt);
            if (IsShifting() || FloatingOriginManager.ShiftingThisFrame || Time.timeScale == 0) return true;
            if (!NavigationMath.Finite(boat.ObserverDistance)) throw new InvalidOperationException("Observer unavailable.");
            var before = Mode;
            handover.Observe(boat.ObserverDistance, boat.PlayerAboard, dt);
            if (before != Mode && Mode == SimulationMode.Caching) BeginCaching();
            if (before != Mode && Mode == SimulationMode.Restoring) BeginRestoring();
            if (Mode == SimulationMode.Restoring) HoldSpawnForShifts(dt); else spawnHeld = false;
            RefreshHorizon();
            if (Mode == SimulationMode.Physical) { previousObserver = ObserverPosition(); return false; }
            // A cache held back for a coming shift is not a stuck handover: its wait has its own limit (SpawnHoldLimit).
            if (!spawnHeld) transitionSeconds += dt;
            if ((Mode == SimulationMode.Caching || Mode == SimulationMode.Restoring) && transitionSeconds > 20)
            {
                // Which condition held her (the user, 2026-10-09: runs 20261009-194114 and -201141 timed out in Restoring with
                // "cargo cached 0, cargo safe True" and named nothing else).
                try
                {
                    // Once per state: a held voyage retries, and run 20261009-211324 logged the same lines 3519 times.
                    string why = "Sailing: the cargo handover timed out in " + Mode + ": items loaded " + ItemsLoaded.GetValue(boat.Items) + ", cargo in her cache " +
                        boat.Items.HasLocalItems() + ", safe to enable physics " + boat.Items.SafeToEnableBoatPhysics() + ", the game loading boat items " +
                        GameState.loadingBoatLocalItems + ", debug kinematic " + Debugger.debugForceKinematicBoat + ", retiring cargo left " +
                        (retiringCargo == null ? 0 : retiringCargo.Count(p => p)) + "; cargo: " +
                        (expectedCargoIds == null ? "no expected list" : CargoReadiness.Explain(expectedCargoIds, CargoSamples())) + ".";
                    if (why != lastTimeout)
                    {
                        lastTimeout = why; Log.Warn(why);
                    }
                }
                catch (Exception e) { Log.Warn("Sailing: the cargo handover timed out; reading why failed: " + e.Message); }
                // Coming back (the user, 2026-10-10): she gets her physics back with her cargo as it stands, an item that didn't come
                // back logged above. Before, this threw every physics step ahead of the readiness check below: she stayed frozen,
                // could not be boarded, and each throw pushed her hold's retry back, until a save or a load let her go (the review
                // of 2026-10-10: the handover has no way from Restoring but its commit).
                if (Mode == SimulationMode.Restoring)
                {
                    RestoreBodies(true);
                    handover.CommitPhysical(handover.Generation);
                    ReturnedToPhysics = true;
                    Message = "Full physics restored after the cargo handover timed out";
                    CargoLeftCached = true;
                    Log.Warn("Sailing: her physics are back though her cargo handover timed out; her cargo stays as it stands.");
                    previousObserver = ObserverPosition();
                    return false;
                }
                // Going away the game's cache keeps its time: turned back before every retiring item is gone, its cargo would come
                // back twice (DistanceHandover: caching completes before any respawn). She stays frozen far from the player and the
                // caching below goes on until the game finishes; the wait is logged once above, not thrown each step.
            }
            if (Mode == SimulationMode.Caching)
            {
                job.clockHour = GameClock.Hours;
                if (boat.Items.HasLocalItems() && !(bool)ItemsLoaded.GetValue(boat.Items) && retiringCargo.All(p => !p))
                {
                    expectedCargoIds = ExpectedCargo(boat.Items.GetCachedItems());
                    // What she carries, for the restore's log (the user, 2026-10-09: a crate and 2 items of hers were gone after
                    // a restore with Dizzy Sailwind Fixes installed; which ones?).
                    try
                    {
                        var directory = PrefabsDirectory.instance ? PrefabsDirectory.instance.directory : null;
                        Log.Info("Sailing: her cargo cached by the game (" + expectedCargoIds.Length + "): " + string.Join(", ", boat.Items.GetCachedItems().Where(p => p != null).Select(p =>
                            p.instanceId + " " + (directory != null && p.prefabIndex >= 0 && p.prefabIndex < directory.Length && directory[p.prefabIndex] ? directory[p.prefabIndex].name : "prefab " + p.prefabIndex) +
                            (p.crateId > 0 ? " (in crate " + p.crateId + ")" : "")).ToArray()) + ".");
                    }
                    catch (Exception e) { Log.Warn("Sailing: listing her cached cargo failed: " + e.Message); }
                    handover.CommitCached(handover.Generation);
                    EnterClock();
                    Message = "Far travel by the clock; cargo cached by vanilla";
                    // A quick reversal cannot respawn before every retiring object is gone.
                    if (boat.PlayerAboard || boat.ObserverDistance <= DistanceHandover.SailAgainWithin) { handover.RequestRestore(); BeginRestoring(); }
                }
            }
            else if (Mode == SimulationMode.Background)
            {
                // Her cache unpacked while the clock carries her (run 20261009-224014: her cargo came back 511 m from her, as if
                // spawned where she had been, then left her): logged once, with where she is.
                if (!unpackedFarLogged && ((bool)ItemsLoaded.GetValue(boat.Items) || !boat.Items.HasLocalItems()))
                {
                    unpackedFarLogged = true;
                    Log.Info("Sailing: her cache is unpacked while the clock carries her: items loaded " + ItemsLoaded.GetValue(boat.Items) + ", cargo in her cache " +
                        boat.Items.HasLocalItems() + ", horizon close " + boat.Horizon.closeToPlayer + ", the player " + boat.ObserverDistance.ToString("F0") + " m from her, at " + boat.StablePosition.ToString("F0") + ".");
                }
                // Check observer motion even on ticks too short to move her.
                var here = boat.StablePosition;
                if (!CanAdvance(here.x, here.z, here.x, here.z))
                {
                    handover.RequestRestore(); BeginRestoring();
                    previousObserver = ObserverPosition();
                    return true;
                }
                AdvanceByClock();
                if (Mode == SimulationMode.Background)
                    Message = Reached ? "Far travel by the clock: at the route's end" : "Far travel by the clock";
            }
            else if (Mode == SimulationMode.Restoring)
            {
                job.clockHour = GameClock.Hours;
                bool spawned = (bool)ItemsLoaded.GetValue(boat.Items) && !boat.Items.HasLocalItems() && !GameState.loadingBoatLocalItems;
                // Fix A's evidence: a shift within 3 s of her cache spawning is what fix A is meant to prevent.
                if ((bool)ItemsLoaded.GetValue(boat.Items) && spawnedAt < 0) { spawnedAt = Time.realtimeSinceStartup; spawnShifts = shiftCount; }
                if (spawnedAt >= 0 && !spawnShiftLogged && shiftCount != spawnShifts && Time.realtimeSinceStartup - spawnedAt < 3f)
                {
                    spawnShiftLogged = true;
                    Log.Warn("Sailing: the world shifted " + (Time.realtimeSinceStartup - spawnedAt).ToString("F2") + " s after her cargo spawned; items not yet on her deck may be left behind.");
                }
                // Dizzy Sailwind Fixes (the user, 2026-10-09: with it installed, 5 items of one crate stayed out of it after her cargo
                // came back, runs 20261009-194114 to -204550): 3 s after her cargo is back, an item still held for a crate that is
                // in the world goes into it the game's own way, as its load would have put it (ShipItem's load coroutine).
                if (spawned && transitionSeconds > 3 && !relinked) { relinked = true; RelinkCrateItems(); }
                if (spawned && boat.Items.SafeToEnableBoatPhysics() && !Debugger.debugForceKinematicBoat && CargoReady())
                {
                    RestoreBodies(true);
                    handover.CommitPhysical(handover.Generation);
                    ReturnedToPhysics = true;
                    Message = "Full physics restored; vanilla cargo ready";
                    previousObserver = ObserverPosition();
                    return false;
                }
            }
            previousObserver = ObserverPosition();
            return true;
        }

        // Far away the clock brings her in at the quoted hour from wherever she is, or at the paper speed once that hour has
        // passed (decision 101, RouteMath.ClockAdvance): sleeping and time skips move her the same way.
        private void EnterClock()
        {
            job.clockHour = GameClock.Hours;
            var here = boat.StablePosition;
            // From the leg her progress has reached, not her guide point (decision 127: a guide point ahead of her mustn't move her
            // on by the corner she would have cut).
            job.done = Math.Max(0, Math.Min(job.length, RouteMath.Project(job.route, new RoutePoint(here.x, here.z), RouteMath.LegAt(job.route, job.done))));
            ReachedHour = Reached ? job.clockHour : double.NaN;
        }

        private void AdvanceByClock()
        {
            double hours = GameClock.Hours;
            double elapsed = hours - job.clockHour;
            job.clockHour = hours;
            if (!NavigationMath.Finite(elapsed) || elapsed <= 0 || Reached) return;
            double next = RouteMath.ClockAdvance(job.length, job.done, hours - elapsed, elapsed, arriveHour, job.speed, out double reachedAt);
            var point = RouteMath.PointAt(job.route, next, out double heading);
            var current = boat.StablePosition;
            if (!CanAdvance(current.x, current.z, point.x, point.z)) { job.clockHour = hours - elapsed; handover.RequestRestore(); BeginRestoring(); return; }
            job.done = next;
            job.waypoint = Math.Max(job.waypoint, Math.Min(job.route.Length - 1, RouteMath.LegAt(job.route, next)));
            if (Reached && double.IsNaN(ReachedHour)) ReachedHour = double.IsNaN(reachedAt) ? hours : reachedAt;
            MoveRepresentation(point, heading);
        }

        // Physical with her cargo still in the game's cache (decision 121's timeout): the game spawns cached items only into a
        // frozen ship, so her cargo stays cached, and her steering must not wait for it (Voyage.TickSailing).
        internal bool CargoLeftCached { get; private set; }

        private void BeginCaching()
        {
            CargoLeftCached = false;
            if (boat.PlayerAboard) throw new InvalidOperationException("Cannot cache an occupied ship.");
            LeavingPhysics?.Invoke();
            transitionSeconds = 0;
            entryVelocity = boat.Body.velocity;
            entrySpeed = Mathf.Clamp(Vector3.Dot(entryVelocity, boat.Root.forward), 0, 12);
            cargoRotation = boat.Root.rotation;
            releaseCargoCompleted = false;
            retiringCargo = SaveLoadManager.instance.GetCurrentPrefabs().Where(p => p && p.GetParentObject() == boat.Saveable.sceneIndex).ToArray();
            if (retiringCargo.Any(p => !p.GetComponent<ShipItem>()))
                throw new NotSupportedException("Selected ship contains cargo without a supported vanilla despawn path.");
            // Record current flags at each handover, not potentially stale flags from job start.
            foreach (var body in bodyFlags.Keys.ToArray()) if (body) bodyFlags[body] = body == boat.Body ? false : body.isKinematic;
            // EmbarkCol also attaches ShipItems. Keep it active through both directions of handover.
            Freeze();
            Message = "Waiting for vanilla cargo cache and despawn";
        }

        private void BeginRestoring()
        {
            transitionSeconds = 0;
            relinked = false;
            // Her cache unpacks now: each crate's items into their crate as they load (CrateTakeOut), as at her release and
            // arrival, so they don't come back full-size and shove the crate off her deck (run 20261009-213901: a ScrollShelf with
            // a model ship and a wool bale came back off her, then were gone; the shove of the 0.1.3 take-outs).
            CrateTakeOut.TakingOut(boat.Saveable.sceneIndex);
            RotateCachedCargo();
            spawnHeldFor = 0; spawnHoldWhy = null; spawnHoldGivenUp = false; spawnedAt = -1f; spawnShiftLogged = false;
            HoldSpawnForShifts(0f);
            boat.Horizon.closeToPlayer = !spawnHeld;
            Freeze();
            Message = "Prewarming: waiting for cargo and physics readiness";
        }

        private void RotateCachedCargo()
        {
            var cached = boat.Items.GetCachedItems();
            if (cached == null) return;
            var delta = boat.Root.rotation * Quaternion.Inverse(cargoRotation);
            foreach (var item in cached) if (item != null) item.rotation = delta * (Quaternion)item.rotation;
            cargoRotation = boat.Root.rotation;
        }

        // Never move her into SailAgainWithin of the player (decision 221): there she sails in physics. Relative swept motion
        // includes both the ship and the observer since the previous tick.
        private bool CanAdvance(double ax, double az, double bx, double bz)
        {
            var observer = ObserverPosition();
            return !NavigationMath.SegmentEntersRadius(ax - previousObserver.x, az - previousObserver.z,
                bx - observer.x, bz - observer.z, 0, 0, DistanceHandover.SailAgainWithin);
        }

        // Her no-go angle (Voyage: 75 degrees with a square sail, else 60).
        internal float NoGo = 60f;
        // Her way when the clock gives her back to physics, m/s.
        internal const float RestoreSpeed = 2.5f;

        // A leg into the wind is sailed close-hauled: her heading the nearer of the wind ± (no-go + 10), so her physics come back
        // with way on (run 20261010-115231: the last leg into Dragon Cliffs ran straight upwind; she came back head to wind,
        // in irons, and held again and again, "Manoeuvre timed out without steerage", the wind 2-5 degrees off her bow).
        private double SailableHeading(double heading)
        {
            var wind = Vector3.ProjectOnPlane(Wind.currentWind, Vector3.up);
            if (wind.sqrMagnitude < 0.25f) return heading;
            double windFrom = NavigationMath.Heading(-wind.x, -wind.z), off = NavigationMath.Delta(windFrom, heading);
            // Off the wind by her no-go + 30 (a beam reach for a lateen): close-hauled, at the clock's crawl, the wind on her sails
            // rounded her up into it before she gathered way (BL, run 20261010-124111: 33.5 m/s, sails set, head to wind).
            if (Math.Abs(off) >= NoGo + 30) return heading;
            return NavigationMath.Normalize(windFrom + (off >= 0 ? 1 : -1) * (NoGo + 30));
        }

        private void MoveRepresentation(RoutePoint point, double heading)
        {
            heading = SailableHeading(heading);
            if (Mode != SimulationMode.Background || boat.PlayerAboard) throw new InvalidOperationException("Background authority lost.");
            var oldPosition = boat.Root.position; var oldRotation = boat.Root.rotation;
            var newPosition = FloatingOriginManager.instance.RealPosToShiftingPos(new Vector3(point.x, boat.StablePosition.y, point.z));
            var newRotation = Quaternion.Euler(0, (float)heading, 0);
            var delta = newRotation * Quaternion.Inverse(oldRotation);
            foreach (var external in externalRoots)
                if (external) { external.position = newPosition + delta * (external.position - oldPosition); external.rotation = delta * external.rotation; }
            boat.Root.SetPositionAndRotation(newPosition, newRotation);
        }

        private Vector3 ObserverPosition() => FloatingOriginManager.instance.ShiftingPosToRealPos(global::Refs.observerMirror.transform.position);
        // A body's flag counts only while the world is in fact shifting: the game clears it in RestoreMomentum, which skips a body
        // whose ship is switched off (beyond the horizon), so a shift while she was hidden left it set for good, and her far clock
        // stopped (run 20261010-092446, S10: the Dhow stood 12 km out while the game ran 200 hours on).
        private bool IsShifting() => WorldShifting() && bodyFlags.Keys.Any(b => b && b.GetComponent<ShiftingRigidbody>() && b.GetComponent<ShiftingRigidbody>().IsShifting());

        private static bool WorldShifting() => GameAccess.WorldShifting();

        // Back on and no shift under way: a flag left from a shift while she was hidden is cleared the game's own way
        // (GameAccess.ClearStaleShift, shared with the take-out), before her own velocity is set.
        private void ClearStaleShifts()
        {
            foreach (var body in bodyFlags.Keys) if (body) GameAccess.ClearStaleShift(body);
        }
        private void Freeze() { foreach (var body in bodyFlags.Keys) if (body) body.isKinematic = true; }

        private bool CargoReady() => CargoReadiness.IsReady(expectedCargoIds, CargoSamples());

        private bool relinked;
        private static string lastTimeout;
        private bool unpackedFarLogged;

        // Fix A (S6, run 20261009-230439; the user, 2026-10-09). An item fresh from her cache sits at the scene root, outside
        // the shifting world, until it settles on her deck a few physics frames later; a world shift in between moved her 512 m
        // and left her wool, shelf and model ship behind ("left a ship", save parent -1; the game's clean-up took two). The game's
        // own horizon has the same gap, and Dizzy Sailwind Fixes' SkipSmoothOriginShift makes every shift instant. So her horizon
        // opens (the game spawns her cache its own way) only when no shift can come while her items settle: not during a rescue
        // or a load, no shift for 0.5 s, and the player farther from the next shift line than 1.5 s at their speed + 5 m.
        private const float SpawnShiftQuiet = 0.5f, SpawnLookAhead = 1.5f, SpawnMargin = 5f, SpawnHoldLimit = 60f;
        private bool spawnHeld, spawnHoldGivenUp, spawnShiftLogged, offsetKnown, shifterKnown;
        private string spawnHoldWhy;
        private float spawnHeldFor, spawnedAt = -1f, lastShiftAt = -1000f, shifterSpeed, lastShifterTime;
        private int shiftCount, spawnShifts;
        private Vector3 lastOffset, lastShifterReal;

        private bool CachePending() => boat.Items.HasLocalItems() && !(bool)ItemsLoaded.GetValue(boat.Items);

        // Every tick, shifting or not: counts the world's shifts and measures how fast the shift's anchor (the player) moves.
        private void WatchShifts(float dt)
        {
            var origin = FloatingOriginManager.instance;
            if (!origin) return;
            if (origin.outCurrentOffset != lastOffset)
            {
                if (offsetKnown) { shiftCount++; lastShiftAt = Time.realtimeSinceStartup; }
                lastOffset = origin.outCurrentOffset; offsetKnown = true;
            }
            if (FloatingOriginManager.ShiftingThisFrame) lastShiftAt = Time.realtimeSinceStartup;
            var shifter = origin.shifterObject;
            if (!shifter) return;
            var real = shifter.position - origin.outCurrentOffset; real.y = 0f;
            float since = Time.time - lastShifterTime;
            if (shifterKnown && since > 1e-4f) shifterSpeed = (real - lastShifterReal).magnitude / since;
            lastShifterReal = real; lastShifterTime = Time.time; shifterKnown = true;
        }

        // Why a shift could come while her cache settles, or null.
        private string SpawnShiftRisk()
        {
            if (GameState.recovering) return "the player is being moved (rescue)";
            if (GameState.currentlyLoading) return "the game is loading";
            if (GameState.waitingForShift || FloatingOriginManager.ShiftingThisFrame || IsShifting()) return "a world shift is under way";
            float quiet = Time.realtimeSinceStartup - lastShiftAt;
            if (quiet < SpawnShiftQuiet) return "the world shifted " + quiet.ToString("F2") + " s ago";
            var origin = FloatingOriginManager.instance;
            var shifter = origin ? origin.shifterObject : null;
            if (!shifter) return null;
            var p = shifter.position;
            float margin = origin.shiftDistance - Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z));
            float need = SpawnMargin + shifterSpeed * SpawnLookAhead;
            return margin < need ? "the player is " + margin.ToString("F0") + " m from the next shift line at " + shifterSpeed.ToString("F1") +
                " m/s (needs " + need.ToString("F0") + " m)" : null;
        }

        private void HoldSpawnForShifts(float dt)
        {
            string why = CachePending() ? SpawnShiftRisk() : null;
            if (why != null && spawnHeldFor < SpawnHoldLimit)
            {
                if (spawnHoldWhy == null) Log.Info("Sailing: her cargo waits to come back: " + why + ".");
                spawnHeld = true; spawnHoldWhy = why; spawnHeldFor += dt;
                return;
            }
            if (why != null && !spawnHoldGivenUp)
            {
                spawnHoldGivenUp = true;
                Log.Warn("Sailing: her cargo waited " + SpawnHoldLimit.ToString("F0") + " s for the world's shifts (" + why + "); it comes back now.");
            }
            else if (why == null && spawnHeld) Log.Info("Sailing: her cargo comes back after waiting " + spawnHeldFor.ToString("F1") + " s (last: " + spawnHoldWhy + ").");
            spawnHeld = false;
        }

        // The cargo her restore waits for: loose items and items whose crate is in her cache too. An item held in a crate that
        // is not aboard her (run 20261009-224014: five starter items "in crate 584901118", 224 km from her, saved as hers) never
        // comes aboard; the game holds it for its crate wherever that is.
        private static int[] ExpectedCargo(List<SavePrefabData> cached)
        {
            var items = (cached ?? new List<SavePrefabData>()).Where(p => p != null).ToList();
            var ids = new HashSet<int>(items.Select(p => p.instanceId));
            var away = items.Where(p => p.crateId > 0 && !ids.Contains(p.crateId)).ToList();
            if (away.Count > 0)
                Log.Info("Sailing: " + away.Count + " cached item(s) of hers lie in a crate that is not aboard her (" +
                    string.Join(", ", away.Select(p => p.instanceId + " in crate " + p.crateId).ToArray()) + "); her restore doesn't wait for them.");
            return items.Where(p => p.crateId <= 0 || ids.Contains(p.crateId)).Select(p => p.instanceId).ToArray();
        }

        // Her items still held for a crate in the world (attached, colliders off: the game's crate-load hold) go into it by the
        // crate's own CrateInventory.InsertItem. Only her expected cargo, only into a crate that is in the world.
        private void RelinkCrateItems()
        {
            if (expectedCargoIds == null || expectedCargoIds.Length == 0) return;
            var expected = new HashSet<int>(expectedCargoIds);
            var prefabs = SaveLoadManager.instance.GetCurrentPrefabs();
            var byId = new Dictionary<int, SaveablePrefab>();
            foreach (var p in prefabs) if (p && !byId.ContainsKey(p.instanceId)) byId.Add(p.instanceId, p);
            int inserted = 0, missing = 0;
            foreach (var prefab in prefabs.ToArray())
            {
                if (!prefab || prefab.currentCrateId <= 0 || !expected.Contains(prefab.instanceId)) continue;
                var item = prefab.GetComponent<ShipItem>();
                if (!item) continue;
                if (!byId.TryGetValue(prefab.currentCrateId, out var crate) || !crate) { missing++; continue; }
                var inventory = crate.GetComponent<CrateInventory>();
                if (!inventory || inventory.containedItems.Contains(item)) continue;
                inventory.InsertItem(item); inserted++;
            }
            if (inserted > 0 || missing > 0)
                Log.Info("Sailing: her cargo back: " + inserted + " item(s) put into their crates the game's way" + (missing > 0 ? "; " + missing + " whose crate is not in the world" : "") + ".");
        }

        private List<CargoAttachment> CargoSamples()
        {
            var samples = new List<CargoAttachment>();
            foreach (var prefab in SaveLoadManager.instance.GetCurrentPrefabs())
            {
                if (!prefab) continue;
                var item = prefab.GetComponent<ShipItem>();
                if (!item) continue;
                bool attached = item.currentWalkCol == boat.WalkCol && item.currentActualBoat &&
                    (item.currentActualBoat == boat.Root || item.currentActualBoat.IsChildOf(boat.Root)) &&
                    prefab.GetParentObject() == boat.Saveable.sceneIndex;
                int containerId = prefab.currentCrateId;
                bool linked = false;
                if (containerId > 0)
                {
                    var container = SaveLoadManager.instance.GetCurrentPrefabs().FirstOrDefault(p => p && p.instanceId == containerId);
                    var inventory = container ? container.GetComponent<CrateInventory>() : null;
                    linked = inventory && inventory.containedItems.Contains(item);
                }
                samples.Add(new CargoAttachment(prefab.instanceId, attached, containerId, linked));
            }
            return samples;
        }

        private void RestoreBodies(bool giveMomentum)
        {
            // She comes back moving ahead at the clock's speed, as she was far away: along her heading, which far away is her
            // route's leg or, on a leg into the wind, close-hauled (SailableHeading).
            var ahead = boat.Root.forward; ahead.y = 0;
            if (ahead.sqrMagnitude < 1e-6f) { RouteMath.PointAt(job.route, job.done, out double legHeading); ahead = Quaternion.Euler(0, (float)legHeading, 0) * Vector3.forward; }
            // At least steerage way (RestoreSpeed): at the clock's own speed (a voyage's average, ~0.1 m/s) her rudder had no
            // grip and the wind turned her head to it (BL). Never more than 12 m/s (review M11).
            var velocity = Mode == SimulationMode.Caching || job.speed <= 0 ? entryVelocity
                : ahead.normalized * (Reached ? 0f : Mathf.Max(ClockSpeedPerSecond, RestoreSpeed));
            velocity.y = 0;
            velocity = Vector3.ClampMagnitude(velocity, 12);
            ClearStaleShifts();
            foreach (var pair in bodyFlags)
            {
                if (!pair.Key) continue;
                pair.Key.isKinematic = pair.Value;
                if (giveMomentum && !pair.Value) { pair.Key.velocity = velocity; pair.Key.angularVelocity = Vector3.zero; }
            }
        }

        public void Dispose()
        {
            if (wasDisposed) return;
            wasDisposed = true;
            var mode = Mode; handover.Stop();
            try
            {
                if (mode == SimulationMode.Caching) CompleteMarkedDespawn();
                if (boat.Items) RotateCachedCargo();
            }
            finally
            {
                try { if (mode != SimulationMode.Physical) RestoreBodies(false); }
                finally
                {
                    if (boat.Horizon && boat.Body)
                    {
                        // Decision 221: let go for a game save while she sails in physics beyond the game's 1 km (her sailor takes her back
                        // 2 s after it, StartSailing), she stays "close" until then (the game's own check waits out SaveBridgeSeconds), so
                        // the game doesn't pack her cargo and the clock and the restore don't follow each save (Claude's call, decision 229).
                        bool bridge = ForSave && mode == SimulationMode.Physical && boat.Valid && !boat.PlayerAboard &&
                            boat.ObserverDistance > 1000 && boat.ObserverDistance <= DistanceHandover.SailWithin;
                        boat.Horizon.closeToPlayer = boat.Valid && (boat.PlayerAboard || boat.ObserverDistance <= 1000 || bridge);
                        // Let vanilla finish any in-flight cargo restore, keeping the hull frozen until ready.
                        boat.Body.isKinematic = !boat.Horizon.closeToPlayer || !boat.Items || !boat.Items.SafeToEnableBoatPhysics() ||
                            !GameState.playing || GameState.currentlyLoading || GameState.justStarted || GameState.changingStartRegion ||
                            GameState.recovering || GameState.currentShipyard || GameState.sleeping || Debugger.debugForceKinematicBoat || Time.timeScale == 0;
                        HorizonCooldown.SetValue(boat.Horizon, bridge ? SaveBridgeSeconds : 0f);
                    }
                }
            }
        }

        private void CompleteMarkedDespawn()
        {
            if (releaseCargoCompleted || !boat.Items) return;
            var cached = boat.Items.GetCachedItems();
            if (cached == null) return; // Caching had not started; vanilla still owns live objects.
            var ids = new HashSet<int>(cached.Where(p => p != null).Select(p => p.instanceId));
            var pending = retiringCargo.Where(p => p && p.GetParentObject() == -2).ToArray();
            // Validate the entire set before performing any irreversible vanilla despawns.
            foreach (var prefab in pending)
            {
                if (!ids.Contains(prefab.instanceId)) throw new InvalidOperationException("Retiring cargo has no vanilla cache record.");
                if (!prefab.GetComponent<ShipItem>()) throw new InvalidOperationException("Retiring cargo has no vanilla item component.");
            }
            foreach (var prefab in pending)
            {
                // Finish the same vanilla despawn path early only on cancellation. This unregisters
                // the old ID before vanilla can respawn the cache when its horizon loop resumes.
                prefab.GetComponent<ShipItem>().DestroyItem();
                prefab.gameObject.SetActive(false);
            }
            releaseCargoCompleted = true;
        }
    }

    // ==== BoardingRetry ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Retry only while still touching the walking deck. Replaying a cached enter event could board
    // a player who has already swum away. Cargo triggers and the escort boat are never disabled.
    internal sealed class BoardingRetry : MonoBehaviour
    {
        private PlayerEmbarkerNew owner;
        private Transform target;

        internal static void Wait(PlayerEmbarkerNew player, Transform boat)
        {
            var retry = player.GetComponent<BoardingRetry>() ?? player.gameObject.AddComponent<BoardingRetry>();
            retry.owner = player;
            retry.target = boat;
        }

        internal static void Clear(PlayerEmbarkerNew player)
        {
            if (!player) return;
            var retry = player.GetComponent<BoardingRetry>();
            if (retry) { retry.target = null; Destroy(retry); }
        }

        private void OnTriggerStay(Collider other)
        {
            if (!owner || !target) { Destroy(this); return; }
            var current = GameBindings.BoardingBoat.GetValue(owner) as EmbarkBoat;
            if (current == null || current.worldBoat != target) { Clear(owner); return; }
            if (Voyages.IsBoardingBlocked(target)) return;
            // Vanilla checks this is the current boat's walk collider and the player is not aboard.
            owner.OnTriggerEnter(other);
        }
    }

    // ==== BoatContext ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public sealed class BoatContext
    {
        public SaveableObject Saveable { get; private set; }
        public Transform Root => Saveable.transform;
        public Rigidbody Body { get; private set; }
        public BoatRefs Refs { get; private set; }
        public Transform WalkCol { get; private set; }
        private System.Collections.Generic.HashSet<Collider> playerHullColliders;
        internal bool OwnsPlayerHullCollider(Collider collider) => playerHullColliders != null && playerHullColliders.Contains(collider);
        public BoatDamage Damage { get; private set; }
        public BoatLocalItems Items { get; private set; }
        public BoatHorizon Horizon { get; private set; }
        public GPButtonSteeringWheel Wheel { get; private set; }
        public System.Collections.Generic.IReadOnlyList<GPButtonSteeringWheel> Wheels { get; private set; }
        public BoatMooringRopes Moorings { get; private set; }
        public Anchor Anchor { get; private set; }
        public bool Valid => Saveable && Body && Refs && Refs.boatModel && WalkCol &&
            Damage && Items && Wheel && Wheel.attachedRudder && Wheels != null && Wheels.Count > 0 && Horizon && Moorings && Anchor;

        // requireOwned false: a hull's original stands in for a copy not built yet (Voyages.WhyCarried, PlanJob).
        public static BoatContext Resolve(PurchasableBoat boat, bool requireOwned = true)
        {
            if (!boat || boat.isHouse || requireOwned && !boat.isPurchased()) throw new InvalidOperationException("Select an owned ship, not a house.");
            var root = boat.transform;
            var context = new BoatContext
            {
                Saveable = boat.GetComponent<SaveableObject>(), Body = boat.GetComponent<Rigidbody>(),
                Refs = boat.GetComponent<BoatRefs>(), Damage = boat.GetComponent<BoatDamage>(),
                Items = boat.GetComponent<BoatLocalItems>(), Moorings = boat.GetComponent<BoatMooringRopes>()
            };
            if (!context.Saveable || !context.Body || !context.Refs || !context.Damage || !context.Items)
                throw new InvalidOperationException("Ship is missing required vanilla components.");
            // Some vanilla rigs omit BoatRefs.walkCol. Boarding and cargo use
            // the embark trigger's serialized walking deck instead.
            var decks = root.GetComponentsInChildren<BoatEmbarkCollider>(true)
                .Where(c => c.transform.parent == context.Refs.boatModel && c.walkCollider)
                .Select(c => c.walkCollider).Distinct().ToArray();
            if (decks.Length != 1 || (context.Refs.walkCol && context.Refs.walkCol != decks[0]))
                throw new InvalidOperationException("Ship requires one consistent walking deck on its embark trigger.");
            context.WalkCol = decks[0];
            if (GameBindings.HullPlayerProxy == null) throw new NotSupportedException("Hull proxy ownership binding is unavailable.");
            // Some stock large hulls omit the optional cleaning backlink. Map
            // the detached player collider through its actual owning component.
            context.playerHullColliders = new System.Collections.Generic.HashSet<Collider>(root.GetComponentsInChildren<HullPlayerCollider>(true)
                .Select(h => GameBindings.HullPlayerProxy.GetValue(h) as Transform).Where(t => t)
                .Select(t => t.GetComponent<Collider>()).Where(c => c));
            context.Wheels = ResolveInstalledWheels(root, context.Refs.boatModel, context.Body);
            context.Wheel = context.Wheels[0];
            context.Horizon = root.GetComponentInChildren<BoatHorizon>(true);
            // The boat's mooring component owns this link. SaveableObject.anchor is populated
            // on the separately saved anchor object, not on the boat's SaveableObject.
            context.Anchor = context.Moorings ? context.Moorings.anchor : null;
            if (!context.Valid)
                throw new InvalidOperationException("Ship model, walking deck, horizon, anchor, or mooring component is missing.");
            return context;
        }

        private static GPButtonSteeringWheel[] ResolveInstalledWheels(Transform root, Transform model, Rigidbody body)
        {
            var wheels = root.GetComponentsInChildren<GPButtonSteeringWheel>(true)
                .Where(w => IsInstalledControlPart(w.transform, model)).ToArray();
            if (wheels.Length == 0 || wheels.Any(w => !w.attachedRudder || !w.attachedRudder.GetComponent<Rudder>() ||
                    w.attachedRudder.GetComponent<Rudder>().shipRigidbody != body) ||
                wheels.Select(w => w.attachedRudder).Distinct().Count() != 1)
                throw new InvalidOperationException("Prototype requires installed helms sharing exactly one supported rudder.");
            return wheels;
        }

        // Stock Brig has lower/upper helm variants. Ignore an uninstalled part,
        // while allowing the whole model to be hidden by native distance loading.
        private static bool IsInstalledControlPart(Transform control, Transform model)
        {
            if (!control || !model || !control.IsChildOf(model)) return false;
            for (var part = control; part != model; part = part.parent)
                if (!part.gameObject.activeSelf) return false;
            return true;
        }

        public bool Contains(Component component)
        {
            if (!component || !Saveable) return false;
            if (component is PickupableBoatMooringRope rope) return rope.GetBoatRigidbody() == Body;
            if (component is MooringRopeLengthAdjuster adjuster) return adjuster.mooringRope && adjuster.mooringRope.GetBoatRigidbody() == Body;
            return component.transform == Root || component.transform.IsChildOf(Root) || component == Anchor;
        }
        public bool PlayerAboard => GameState.currentBoat &&
            (GameState.currentBoat == Refs.boatModel || GameState.currentBoat == Root || GameState.currentBoat.IsChildOf(Root));
        public Vector3 StablePosition => FloatingOriginManager.instance.ShiftingPosToRealPos(Root.position);
        public float ObserverDistance => global::Refs.observerMirror ? Vector3.Distance(Root.position, global::Refs.observerMirror.transform.position) : float.PositiveInfinity;
    }

    // ==== CompatibilityGate ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).
    // VirtualCrew no longer refuses the transport (D8: its automation is kept off a ship in service, Compat\VirtualCrew), unless
    // its code changed so that it can't be; SailMaster, not installed on the build PC and so untested, still does (D1: paperwork).

    internal static class CompatibilityGate
    {
        private static readonly string[] ConflictingNames = { "SailMaster" };
        private static bool? sailMaster;

        // Looked for by its BepInEx plugin and its assembly, and kept only once every plugin is loaded (the main scene,
        // Compat.Mods.ApplyLatePatches): asked at Shipwright's Awake, a SailMaster that loads later went unseen (review M6).
        internal static bool SailMaster => sailMaster ?? Detect();

        internal static void Settle()
        {
            sailMaster = Detect();
            if (sailMaster.Value) Log.Info("SailMaster is installed: transports are carried, not sailed (D1).");
        }

        private static bool Detect()
        {
            bool Named(string text) => !string.IsNullOrEmpty(text) && ConflictingNames.Any(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
            if (Chainloader.PluginInfos.Values.Any(p => p?.Metadata != null && (Named(p.Metadata.GUID) || Named(p.Metadata.Name)))) return true;
            return AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            {
                try { return Named(a.GetName().Name); } catch { return false; }
            });
        }

        // Asked once the game runs (the board, a voyage's start): VirtualCrew is kept off at the main scene's load.
        internal static bool HasConflict => SailMaster || Compat.VirtualCrew.Unguarded;

        internal static string ConflictReason => SailMaster ? "SailMaster is installed; transports are carried instead."
            : "VirtualCrew can't be kept off a ship in service; transports are carried instead.";
    }

    // ==== ControlOwnership ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    /// <summary>Owns only controls and line objects belonging to one selected vessel.</summary>
    public sealed class ControlOwnership : IDisposable
    {
        private sealed class ButtonState
        {
            internal GoPointerButton Button;
            internal bool Unclickable;
        }

        private sealed class ColliderState
        {
            internal Collider Collider;
            internal bool Enabled;
        }

        private static readonly FieldInfo IsClicked = AccessTools.Field(typeof(GoPointerButton), "isClicked");
        private static readonly FieldInfo StickyClickedBy = AccessTools.Field(typeof(GoPointerButton), "stickyClickedBy");
        private static readonly FieldInfo ClickedBy = AccessTools.Field(typeof(GoPointerButton), "isClickedBy");
        private static readonly FieldInfo Grabbable = AccessTools.Field(typeof(TouchRotateHandle), "grabbable");
        private static readonly FieldInfo DebugGrabbed = AccessTools.Field(typeof(TouchRotateHandle), "debugGrabbed");
        private static readonly Type GrabType = AccessTools.TypeByName("OVRGrabbable");
        private static readonly PropertyInfo IsGrabbed = AccessTools.Property(GrabType, "isGrabbed");
        private static readonly FieldInfo HeldItem = AccessTools.Field(typeof(GoPointer), "heldItem");

        private readonly BoatContext boat;
        private readonly RigSnapshot rig;
        private readonly HashSet<Component> protectedObjects = new HashSet<Component>();
        private readonly List<ButtonState> buttonStates = new List<ButtonState>();
        private readonly List<ColliderState> colliderStates = new List<ColliderState>();
        private bool acquired;
        private bool disposed;

        public ControlOwnership(BoatContext boat, RigSnapshot rig)
        {
            this.boat = boat ?? throw new ArgumentNullException(nameof(boat));
            this.rig = rig ?? throw new ArgumentNullException(nameof(rig));
            if (!boat.Valid || !rig.Supported)
                throw new InvalidOperationException("Control ownership requires a valid boat and supported rig.");
            BuildControlSet();
        }

        public bool IsProtected(Component component)
        {
            if (!acquired || !component) return false;
            if (protectedObjects.Contains(component)) return true;
            var handle = component as TouchRotateHandle;
            if (!handle) handle = component.GetComponentInParent<TouchRotateHandle>();
            if (handle && protectedObjects.Contains(handle)) return true;
            if (GrabType != null && GrabType.IsInstanceOfType(component))
            {
                var parentHandle = component.GetComponent<TouchRotateHandle>();
                return parentHandle && protectedObjects.Contains(parentHandle);
            }
            return false;
        }

        public void Acquire()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ControlOwnership));
            if (acquired) return;
            if (IsGrabbedOrActivelyUsed())
                throw new InvalidOperationException("Release any active wheel, winch, or handle input before starting transport.");

            try
            {
                foreach (var button in protectedObjects.OfType<GoPointerButton>())
                {
                    buttonStates.Add(new ButtonState { Button = button, Unclickable = button.unclickable });
                    button.unclickable = true;
                }
                foreach (var handle in protectedObjects.OfType<TouchRotateHandle>())
                    SnapshotColliders(handle.GetComponentsInChildren<Collider>(true));
                foreach (var obj in protectedObjects)
                {
                    if (obj is GoPointerButton || obj is TouchRotateHandle) continue;
                    SnapshotColliders(obj.GetComponents<Collider>());
                }
                foreach (var state in colliderStates)
                    if (state.Collider) state.Collider.enabled = false;
                acquired = true;
            }
            catch
            {
                Restore();
                throw;
            }
        }

        private void BuildControlSet()
        {
            foreach (var wheel in boat.Wheels) AddButton(wheel);
            foreach (var winch in rig.Winches.Values) AddButton(winch);
            var anchorLine=boat.Moorings.GetAnchorController();
            foreach (var winch in boat.Root.GetComponentsInChildren<GPButtonRopeWinch>(true))
                if (winch.rope==anchorLine) AddButton(winch);

            // Anchors and mooring lines are sailing controls even though they are not rope winches.
            if (boat.Anchor) Add(boat.Anchor);
            if (boat.Moorings)
            {
                foreach (var rope in boat.Moorings.GetComponentsInChildren<PickupableBoatMooringRope>(true))
                    if (boat.Contains(rope)) Add(rope);
                foreach (var adjuster in boat.Moorings.GetComponentsInChildren<MooringRopeLengthAdjuster>(true))
                    if (boat.Contains(adjuster)) Add(adjuster);
            }
            // Some mooring objects are detached from the boat hierarchy while still bound to its rigidbody.
            foreach (var rope in Resources.FindObjectsOfTypeAll<PickupableBoatMooringRope>())
                if (rope && rope.gameObject.scene.IsValid() && boat.Contains(rope)) Add(rope);
            foreach (var adjuster in Resources.FindObjectsOfTypeAll<MooringRopeLengthAdjuster>())
                if (adjuster && adjuster.gameObject.scene.IsValid() && boat.Contains(adjuster)) Add(adjuster);

            foreach (var button in protectedObjects.OfType<GoPointerButton>().ToArray())
            {
                var handle = button.GetComponentInChildren<TouchRotateHandle>(true);
                if (handle) Add(handle);
            }
            var sailingButtons = protectedObjects.OfType<GoPointerButton>().ToArray();
            foreach (var handle in Resources.FindObjectsOfTypeAll<TouchRotateHandle>())
                if (handle && handle.gameObject.scene.IsValid() && sailingButtons.Any(b => b.transform == handle.rotatable)) Add(handle);
        }

        private void AddButton(GoPointerButton button)
        {
            if (!button || !boat.Contains(button)) throw new InvalidOperationException("A mapped sailing control is not on the selected vessel.");
            Add(button);
        }

        private void Add(Component component)
        {
            if (component) protectedObjects.Add(component);
        }

        private bool IsGrabbedOrActivelyUsed()
        {
            if (IsClicked == null || StickyClickedBy == null || ClickedBy == null || Grabbable == null ||
                DebugGrabbed == null || IsGrabbed == null || HeldItem == null)
                throw new NotSupportedException("Could not verify that sailing controls are free of player input.");

            foreach (var button in protectedObjects.OfType<GoPointerButton>())
            {
                if ((bool)IsClicked.GetValue(button) || StickyClickedBy.GetValue(button) != null || ClickedBy.GetValue(button) != null)
                    return true;
            }
            foreach (var handle in protectedObjects.OfType<TouchRotateHandle>())
            {
                if ((bool)DebugGrabbed.GetValue(handle)) return true;
                var grabbable = Grabbable.GetValue(handle);
                if (grabbable == null) throw new InvalidOperationException("Wait for the sailing handles to initialize.");
                if ((bool)IsGrabbed.GetValue(grabbable, null)) return true;
            }
            foreach (var pointer in Resources.FindObjectsOfTypeAll<GoPointer>())
            {
                if (!pointer || !pointer.gameObject.scene.IsValid()) continue;
                var held = HeldItem.GetValue(pointer) as Component;
                if (held && IsDirectlyProtected(held)) return true;
            }
            return false;
        }

        private bool IsDirectlyProtected(Component component)
        {
            if (protectedObjects.Contains(component)) return true;
            var adjuster = component as MooringRopeLengthAdjuster;
            return adjuster && adjuster.mooringRope && protectedObjects.Contains(adjuster.mooringRope);
        }

        private void SnapshotColliders(IEnumerable<Collider> colliders)
        {
            foreach (var collider in colliders)
                if (collider && !colliderStates.Any(s => s.Collider == collider))
                    colliderStates.Add(new ColliderState { Collider = collider, Enabled = collider.enabled });
        }

        private void Restore()
        {
            for (var i = colliderStates.Count - 1; i >= 0; --i)
                if (colliderStates[i].Collider) colliderStates[i].Collider.enabled = colliderStates[i].Enabled;
            for (var i = buttonStates.Count - 1; i >= 0; --i)
                if (buttonStates[i].Button) buttonStates[i].Button.unclickable = buttonStates[i].Unclickable;
            colliderStates.Clear();
            buttonStates.Clear();
            acquired = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            Restore();
            disposed = true;
        }
    }

    // ==== GameBindings ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    internal static class GameBindings
    {
        internal static readonly FieldInfo WheelLocked = AccessTools.Field(typeof(GPButtonSteeringWheel), "locked");
        internal static readonly FieldInfo WheelLimit = AccessTools.Field(typeof(GPButtonSteeringWheel), "rotationAngleLimit");
        internal static readonly FieldInfo WinchInput = AccessTools.Field(typeof(GPButtonRopeWinch), "currentInput");
        internal static readonly FieldInfo WinchLastAngle = AccessTools.Field(typeof(GPButtonRopeWinch), "lastAngle");
        internal static readonly MethodInfo WinchAudio = AccessTools.Method(typeof(GPButtonRopeWinch), "UpdateAudio");
        internal static readonly FieldInfo WinchCurrentAudio = AccessTools.Field(typeof(GPButtonRopeWinch), "currentAudio");
        internal static readonly FieldInfo PointedButton = AccessTools.Field(typeof(GoPointer), "pointedAtButton");
        internal static readonly FieldInfo Grabbed = AccessTools.Field(typeof(TouchRotateHandle), "grabbable");
        internal static readonly FieldInfo BoardingBoat = AccessTools.Field(typeof(PlayerEmbarkerNew), "currentBoat");
        internal static readonly FieldInfo HullPlayerProxy = AccessTools.Field(typeof(HullPlayerCollider), "hullCol");
        internal static bool WaterPatchReady;
        internal static readonly FieldInfo SaveBusy = AccessTools.Field(typeof(SaveLoadManager), "busy");

        internal static void Verify()
        {
            if (WheelLocked == null || WheelLimit == null || WinchInput == null || WinchLastAngle == null ||
                WinchAudio == null || WinchCurrentAudio == null || PointedButton == null || Grabbed == null || BoardingBoat == null || BoardingBoat.FieldType != typeof(EmbarkBoat) || SaveBusy == null || SaveBusy.FieldType != typeof(bool) ||
                HullPlayerProxy == null || HullPlayerProxy.FieldType != typeof(UnityEngine.Transform))
                throw new NotSupportedException("Required game bindings are missing; prototype controls are unavailable.");
        }
    }

    // ==== NavigationWater ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Geometry adapter. A missing island scene is an exclusion, never evidence of water.
    internal sealed class NavigationWater
    {
        private readonly BoatContext boat;
        private readonly Func<Collider,bool> own;
        private readonly Func<Vector3> velocity;
        private readonly RaycastHit[] hits=new RaycastHit[96];
        private readonly Collider[] overlaps=new Collider[96];
        private Rigidbody[] traffic=Array.Empty<Rigidbody>();
        private readonly Dictionary<Rigidbody, Behaviour> customTraffic = new Dictionary<Rigidbody, Behaviour>();
        private float trafficAt;
        private readonly Dictionary<int,IslandEnvelope> islands=new Dictionary<int,IslandEnvelope>();
        private float islandsAt;
        private sealed class IslandEnvelope { internal Vector3 Centre; internal float Radius; }
        internal float Radius { get; }
        // How deep the clearance sweep reaches under the sea's surface: her keel's depth and 0.15 m (the user, 2026-10-11, "reduce the keel clearance to keel + 0.15m to see if it can pass through north exit"; 0.3 m from 2026-10-09; it was her
        // hull's depth and 0.75 m, at least 2.5 m, which met Dragon Cliffs' seabed "Cube" meshes 1.0-1.6 m down under a Dhow
        // floating 0.83 m deep: runs 20261009-194114 to -204550). The keel's depth is measured while she floats, the deepest seen
        // (her bow and stern dip in a sea); before that, estimated from her hull.
        internal const float KeelMargin = 0.15f;
        internal float Draft => KeelDepth + KeelMargin;
        // Her keel's depth under the sea as she floats (her mean, measured; before that estimated from her hull).
        internal float KeelDepth => keelMeasured ? keelDepth : estimatedKeelDepth;
        // Decision 198 (g): how much deeper than its mean her keel dips in the sea she has (her bow, middle or stern at its deepest over
        // the last DipSeconds), for the water her line wants under her keel.
        internal float KeelDip => Mathf.Max(0f, DipPeak() - KeelDepth);
        // The sea's surface in stable coordinates (Simple Tides moves it: the mod reads it, never that mod).
        internal static float RealSea => FloatingOriginManager.instance ? FloatingOriginManager.instance.ShiftingPosToRealPos(new Vector3(0, SeaLevel(), 0)).y : SeaLevel();
        private readonly float estimatedKeelDepth;
        private float keelDepth;
        private bool keelMeasured;
        internal float Length { get; }
        // Decision 208: a hull piece wholly this far from her origin or more is left out of her measurement (Claude's number: the planner
        // takes hulls up to 65 m long, so no piece of a hull it can sail lies that far out).
        internal const float FarOutside = 100f;
        internal float ForeExtent { get; }
        internal float AftExtent { get; }
        // Her hull's lowest point and its length, in her own axes (decision 112: the tide probe under her keel).
        internal float KeelY { get; }
        internal float KeelFore { get; }
        internal float KeelAft { get; }
        internal string Obstruction { get; private set; }
        internal IslandHorizon DestinationIsland { get; private set; }
        // The layers of her physical hull, for the game's layer collision matrix (Obstacle).
        private readonly int[] hullLayers;
        // The layers her hull can touch by that matrix (the review of 2026-10-11 night): the queries ask for these alone, so a collider
        // she passes through is never returned to be sorted out, nor fills a query's fixed-size result array (the seabed probe's 16)
        // and pushes a real one out. Read again every HitMaskSeconds (the game sets the matrix at its start; a mod could change it).
        private int hitMask;
        private float hitMaskAt=float.NegativeInfinity;
        private const float HitMaskSeconds=10f;
        private int HitMask()
        {
            if (Time.time<hitMaskAt) return hitMask;
            hitMaskAt=Time.time+HitMaskSeconds;
            int mask=0;
            for (int layer=0;layer<32;layer++)
                foreach (int l in hullLayers) if (!Physics.GetIgnoreLayerCollision(l,layer)) { mask|=1<<layer; break; }
            return hitMask=mask;
        }
        private bool Touches(int layer) => (HitMask() & (1<<layer))!=0;
        // Her clearance checks under way (Check, TurnClear; not the harbour chart's cells, which its own line counts) since the voyage
        // last read them, for the minute line's cost of her navigation (the review of 2026-10-11 night).
        internal int Checks;
        private readonly HashSet<Collider> passedThrough=new HashSet<Collider>();
        private readonly HashSet<Collider> described=new HashSet<Collider>();
        internal NavigationWater(BoatContext boat,Func<Collider,bool> own,Func<Vector3> velocity=null)
        {
            this.boat=boat; this.own=own;
            this.velocity=velocity ?? (()=>boat.Body.velocity);
            // On within her own ship: a stored ship far away is switched off as a whole (RigScanner.On), and then Unity gives her
            // colliders no attached rigidbody; the nearest one up her hierarchy is the one they attach to when she is on.
            var hull=boat.Root.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && RigScanner.ActiveWithin(c.transform,boat.Root) && !c.isTrigger && BodyOf(c)==boat.Body && !c.GetComponentInParent<ShipItem>()).ToArray();
            var bounds=new Bounds(Vector3.zero,Vector3.zero);
            var leftOut=new List<string>();
            var inHull=new List<Collider>();
            foreach (var c in hull)
            {
                Bounds local;
                if (c is MeshCollider mesh && mesh.sharedMesh) local=mesh.sharedMesh.bounds;
                else if (c is BoxCollider box) local=new Bounds(box.center,box.size);
                else if (c is CapsuleCollider capsule)
                {
                    var size=Vector3.one*(capsule.radius*2);
                    size[capsule.direction]=Mathf.Max(capsule.height,capsule.radius*2);
                    local=new Bounds(capsule.center,size);
                }
                else if (c is SphereCollider sphere) local=new Bounds(sphere.center,Vector3.one*(sphere.radius*2));
                else throw new NotSupportedException("Unsupported physical hull collider: "+c.GetType().Name);
                var piece=new Bounds(boat.Root.InverseTransformPoint(c.transform.TransformPoint(local.center)),Vector3.zero);
                for (int i=0;i<8;++i)
                {
                    var p=local.center+Vector3.Scale(local.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                    piece.Encapsulate(boat.Root.InverseTransformPoint(c.transform.TransformPoint(p)));
                }
                // Decision 208 (the user, 2026-10-13: "ignore hull pieces far outside of ship"): a piece lying wholly FarOutside or more
                // from her origin is no part of her hull (the test games' ShipyardExpansion put two cabin colliders of its Jong parts about
                // 75 km off her, so she measured 61 by 44 km and was refused).
                if (piece.SqrDistance(Vector3.zero)>=FarOutside*FarOutside)
                {
                    leftOut.Add(Voyage.Path(c.transform,boat.Root)+" ("+piece.center.magnitude.ToString("F0")+" m off)");
                    continue;
                }
                inHull.Add(c);
                bounds.Encapsulate(piece.min); bounds.Encapsulate(piece.max);
            }
            if (leftOut.Count>0) Log.Info("Navigation envelope: hull pieces lying far outside her left out (decision 208): "+string.Join("; ",leftOut.ToArray())+".");
            hull=inHull.ToArray();
            if (hull.Length==0 || bounds.size.sqrMagnitude<0.01f)
                throw new NotSupportedException("No measurable physical hull geometry.");
            hullLayers=hull.Select(c => c.gameObject.layer).Distinct().ToArray();
            // Include off-centre fenders and keep20cm beyond the measured beam.
            // A larger fixed buffer can reject vanilla's close alongside berths.
            Radius=Mathf.Max(1.5f,bounds.extents.x+Mathf.Abs(bounds.center.x)+0.2f);
            Length=Mathf.Max(10,bounds.size.z);
            ForeExtent=Mathf.Max(0,bounds.max.z)+.2f;
            AftExtent=Mathf.Max(0,-bounds.min.z)+.2f;
            estimatedKeelDepth=Mathf.Max(0.5f,-bounds.min.y+0.75f);
            KeelY=bounds.min.y; KeelFore=bounds.max.z; KeelAft=bounds.min.z;
            // Her keel's depth kept from her last navigation (a resume after a hold, a restart): run 20261011-145413 on DX, each resume
            // took its first reading, 0.00-0.21 m, as her keel's depth and averaged up to 1.4 m over a minute, her sweep meanwhile 0.15-1.0 m
            // deep: she judged Dragon Cliffs' shoals passable and touched them 80 times.
            if (boat.Root && KeptKeel.TryGetValue(boat.Root.GetInstanceID(),out float kept)) { keelDepth=kept; keelMeasured=true; keelLogged=kept; }
            Log.Info($"Navigation envelope: local hull bounds={bounds}; radius={Radius}; draft={Draft}; length={Length}; position={boat.Root.position}.");
            if (Radius>15 || Length>65 || Draft>12) throw new NotSupportedException("Hull clearance envelope exceeds this local planner's limits.");
        }
        private static float SeaLevel() => Crest.OceanRenderer.Instance ? Crest.OceanRenderer.Instance.SeaLevel : 0f;

        // Her keel's depth under the sea's surface as she floats (never while she is held kinematic: the shipyard lifts her out of
        // the water). It was the deepest of her bow, middle and stern ever seen; with the sea grown (the QA, 2026-10-11) every trough
        // and heave pushed it deeper (run 20261011-011257: 0.64 to 1.73 m in a minute off Gold Rock City, her seabed limit 2.03 m),
        // so it is now her keel's mean depth (bow, middle and stern), averaged over KeelSeconds while she floats upright.
        private const float KeelSeconds=20f;
        private float keelSampledAt=-1, keelLogged=float.NaN;
        // Each hull's keel depth as last measured, by her root's instance (kept for the session).
        private static readonly Dictionary<int,float> KeptKeel=new Dictionary<int,float>();
        private void MeasureKeel()
        {
            if (!boat.Body || boat.Body.isKinematic || Vector3.Angle(boat.Root.up,Vector3.up)>10f) return;
            float sea=SeaLevel(), sum=0f, deepest=float.NegativeInfinity;
            foreach (float z in new[] { KeelFore, 0f, KeelAft })
            {
                float d=sea-boat.Root.TransformPoint(new Vector3(0,KeelY,z)).y;
                sum+=d; deepest=Mathf.Max(deepest,d);
            }
            float mean=sum/3f;
            if (!NavigationMath.Finite(mean) || mean<-2 || mean>12) return;
            float now=Time.time;
            NoteDip(deepest,now);
            if (!keelMeasured) keelDepth=Mathf.Max(0,mean);
            else if (now>keelSampledAt) keelDepth=Mathf.Max(0,keelDepth+(mean-keelDepth)*Mathf.Clamp01((now-keelSampledAt)/KeelSeconds));
            keelSampledAt=now; keelMeasured=true;
            KeptKeel[boat.Root.GetInstanceID()]=keelDepth;
            if (float.IsNaN(keelLogged) || Mathf.Abs(keelDepth-keelLogged)>0.1f)
            {
                keelLogged=keelDepth;
                Log.Info("Navigation: her keel floats " + keelDepth.ToString("F2") + " m deep (her mean, averaged); the clearance sweep reaches " + (keelDepth+KeelMargin).ToString("F2") + " m.");
            }
        }

        // Decision 198 (g): her keel's deepest moments (bow, middle or stern), kept in DipBuckets slots of DipSeconds / DipBuckets each.
        private const float DipSeconds=30f;
        private const int DipBuckets=6;
        private readonly float[] dipDeepest=new float[DipBuckets];
        private readonly int[] dipSlot={ int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private void NoteDip(float deepest,float now)
        {
            int slot=Mathf.FloorToInt(now/(DipSeconds/DipBuckets)), b=(slot%DipBuckets+DipBuckets)%DipBuckets;
            if (dipSlot[b]!=slot) { dipSlot[b]=slot; dipDeepest[b]=deepest; }
            else dipDeepest[b]=Mathf.Max(dipDeepest[b],deepest);
        }
        private float DipPeak()
        {
            int slot=Mathf.FloorToInt(Time.time/(DipSeconds/DipBuckets));
            float peak=float.NegativeInfinity;
            for (int b=0;b<DipBuckets;b++) if (dipSlot[b]!=int.MinValue && dipSlot[b]<=slot && slot-dipSlot[b]<DipBuckets) peak=Mathf.Max(peak,dipDeepest[b]);
            return peak;
        }

        private static Rigidbody BodyOf(Collider c)
        {
            if (c.attachedRigidbody) return c.attachedRigidbody;
            for (var t=c.transform;t;t=t.parent) { var body=t.GetComponent<Rigidbody>(); if (body) return body; }
            return null;
        }
        // Her way under sail: her hull swept along it, and the seabed under it (SeabedClear).
        internal bool Clear(RoutePoint from,RoutePoint to) => Check(from,to,Radius,true,true);
        // Her hull swept only, no seabed probe: the harbour's own moves beside the dock (docking, the tow, the probes off the dock),
        // where the custom berths lie over seabed meshes about as deep as her keel (Dragon Cliffs: 1.0-1.6 m). Her beam round her
        // middle, swept (not her hull's box, decision 149: these moves take her bodily, often sideways off the dock, where a box laid
        // along the move turns her across it; run 20261011-102410: the tow in planned no way out of Gold Rock City's shipyard for
        // the Brig, and pinned her bow on its dock).
        internal bool ClearHull(RoutePoint from,RoutePoint to) => Check(from,to,Radius,false,false);
        // Her beam swept and the seabed under her keel, as her harbour's chart judges its cells (decision 157): her way on beyond her
        // guide, cell to cell down the chart (decision 162; run 20261011-153651 on EC: straight lines every third cell, her whole
        // hull's box laid along each, clipped the shallow edges of Dragon Cliffs' bending north gap, so her way at her guide read
        // as blocked, she kept clear the wrong way and struck Cube at 3.5 m/s).
        internal bool ClearBeam(RoutePoint from,RoutePoint to) => Check(from,to,Radius,true,false);
        // The harbour's tow (D17): a wider swept clearance, for her length at an angle to her way and her leeway.
        // `seabed`: the tow out over open water checks the seabed on her centre line too (decision 140; run 20261011-013704: the long
        // tow at Fort Aestrin pulled her bow onto Cube_000 at 2.2 m/s); the tow in, which ends over the berth's shallow seabed, doesn't.
        internal bool ClearWide(RoutePoint from,RoutePoint to,float radius,bool seabed=false) => Check(from,to,Mathf.Max(Radius,radius),seabed);
        internal bool TrafficClear(RoutePoint from,RoutePoint to) => ClearHull(from,to) &&
            MovingTrafficClear(from,to,Vector3.ProjectOnPlane(velocity(),Vector3.up).magnitude);
        // Boats under way only (decisions 102, 109: traffic makes her give way, it never blocks her course).
        internal bool MovingClear(RoutePoint from,RoutePoint to) =>
            MovingTrafficClear(from,to,Vector3.ProjectOnPlane(velocity(),Vector3.up).magnitude);
        internal bool VelocityTrafficClear(RoutePoint from)
        {
            var current=velocity();
            return MovingTrafficClear(from,new RoutePoint(from.x+current.x*20,from.z+current.z*20),
                Vector3.ProjectOnPlane(current,Vector3.up).magnitude);
        }
        internal RoutePoint ChartGoal(RoutePoint from, RoutePoint destination)
        {
            var tracker=IslandDistanceTracker.instance;
            if (!tracker || tracker.islands==null || tracker.islands.Count==0) throw new InvalidOperationException("Island chart unavailable.");
            RefreshIslands(tracker); islandsAt=Time.time+5;
            var goal=destination;
            // Stop outside unknown coastal geometry. The voyage owns the final
            // destination separately and replans this approach after native loading.
            foreach (var island in tracker.islands)
            {
                if (!island || island.islandIndex<=0 || island.SceneLoaded() && SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded) continue;
                if (!islands.TryGetValue(island.islandIndex,out var envelope))
                    envelope=new IslandEnvelope { Centre=FloatingOriginManager.instance.ShiftingPosToRealPos(island.GetPosition()), Radius=island.islandLoadDistance };
                var centre=new RoutePoint(envelope.Centre.x,envelope.Centre.z);
                float radius=envelope.Radius+Radius+Length+300;
                if (RoutePoint.Distance(destination,centre)>radius) continue;
                double bearing=NavigationMath.Heading(from.x-centre.x,from.z-centre.z), best=double.PositiveInfinity;
                bool found=false;
                for (int i=0;i<32;i++)
                {
                    double angle=(bearing+i*11.25)*Math.PI/180;
                    var candidate=new RoutePoint(centre.x+(float)Math.Sin(angle)*radius,centre.z+(float)Math.Cos(angle)*radius);
                    double score=RoutePoint.Distance(from,candidate)+RoutePoint.Distance(candidate,destination);
                    if (score>=best || !Clear(candidate,candidate)) continue;
                    goal=candidate; best=score; found=true;
                }
                if (!found) throw new InvalidOperationException("No surveyed-water staging point outside "+island.name);
                DestinationIsland=island;
                break;
            }
            return goal;
        }
        private bool Check(RoutePoint from,RoutePoint to,float radius,bool seabed,bool box=false)
        {
            if (!RoutePlanner.Finite(from) || !RoutePlanner.Finite(to) || !FloatingOriginManager.instance) return false;
            var tracker=IslandDistanceTracker.instance;
            if (!tracker || tracker.islands==null || tracker.islands.Count==0) { Obstruction="Island registry unavailable"; return false; }
            Checks++;
            if (Time.time>=islandsAt) { RefreshIslandsIfChanged(tracker); islandsAt=Time.time+5; }
            foreach (var island in tracker.islands)
            {
                if (!island || island.islandIndex<=0) continue;
                if (island.SceneLoaded() && SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded) continue;
                IslandEnvelope envelope;
                if (!islands.TryGetValue(island.islandIndex,out envelope))
                    envelope=new IslandEnvelope {Centre=FloatingOriginManager.instance.ShiftingPosToRealPos(island.GetPosition()),Radius=island.islandLoadDistance};
                var centre=envelope.Centre;
                if (NavigationMath.SegmentEntersRadius(from.x,from.z,to.x,to.z,centre.x,centre.z,envelope.Radius+UnloadedReach))
                { Obstruction="Unloaded island region: "+island.name; return false; }
            }
            MeasureKeel();
            // At the sea's surface (Simple Tides moves it 1.3-4 m), which the sweep's depth is measured from.
            float sea=SeaLevel();
            var a=FloatingOriginManager.instance.RealPosToShiftingPos(new Vector3(from.x,0,from.z)); a.y=sea;
            var b=FloatingOriginManager.instance.RealPosToShiftingPos(new Vector3(to.x,0,to.z)); b.y=sea;
            // Her own radius: a capsule from her draft to 3 m over the water, swept along the way.
            if (radius>Radius+0.01f) return Boxed(a,b,radius) && (!seabed || SeabedClear(from,to,sea));
            return (box ? Swept(a,b) : SweptBeam(a,b)) && (!seabed || SeabedClear(from,to,sea));
        }

        // The seabed (the user, 2026-10-11: "obstacle detection should include sea bed depth"; "same logic as berth one ... keel
        // +0.3m"). The swept capsule meets the islands' terrain meshes only at its lowest point and missed a shoal ahead (the arrival
        // lab, run 20261011-002639: the Dhow sailed onto Dragon Cliffs' Cube_002). Along her way, every SeabedStep metres on her
        // centre line, a ray straight down from SeabedTop over the water to her Draft under it (her keel's depth and KeelMargin, as at the
        // berth): land or seabed met there blocks her way. Cached per cell in stable coordinates, probed again when the sea has moved
        // more than half a metre (the tide) or the loaded scenes change. Not under her own hull: she floats there.
        internal const float SeabedStep=3f, SeabedTop=3f, RockTop=60f;
        private struct SeabedCell { internal float Sea, Top; internal Collider What; }
        private readonly Dictionary<long,SeabedCell> seabedCells=new Dictionary<long,SeabedCell>();
        private readonly RaycastHit[] downHits=new RaycastHit[16];
        private int seabedScenes=int.MinValue;

        private bool SeabedClear(RoutePoint from,RoutePoint to,float sea)
        {
            var origin=FloatingOriginManager.instance;
            float realSea=origin.ShiftingPosToRealPos(new Vector3(0,sea,0)).y;
            var hullAt=origin.ShiftingPosToRealPos(boat.Root.position);
            var hull=new RoutePoint(hullAt.x,hullAt.z);
            double heading=boat.Root.eulerAngles.y;
            foreach (var key in Seabed.Cells(from,to,SeabedStep))
            {
                var centre=Seabed.Centre(key,SeabedStep);
                // Under her hull as she lies (a cell's half-diagonal of margin): she floats there.
                if (Seabed.UnderHull(centre,hull,heading,Radius+SeabedStep*0.71,ForeExtent+SeabedStep*0.71,AftExtent+SeabedStep*0.71)) continue;
                var cell=SeabedCellAt(key,realSea);
                if (cell.Top<=realSea-Draft) continue;
                float depth=realSea-cell.Top;
                if (cell.What && described.Add(cell.What))
                    Log.Info("Navigation seabed: "+cell.What.name+" (layer "+LayerMask.LayerToName(cell.What.gameObject.layer)+(cell.What.transform.parent ? ", under "+
                        cell.What.transform.parent.name : "")+") "+(depth>=0 ? depth.ToString("F1")+" m under the sea" : (-depth).ToString("F1")+" m over it")+
                        " at ("+centre.x.ToString("F0")+", "+centre.z.ToString("F0")+"); her keel needs "+Draft.ToString("F2")+" m.");
                Obstruction="Shallows: "+(cell.What ? cell.What.name : "the seabed")+" "+(depth>=0 ? depth.ToString("F1")+" m deep" : "above the sea")+
                    " (her keel needs "+Draft.ToString("F2")+" m)";
                return false;
            }
            return true;
        }

        // A seabed cell, probed or from the cache (probed again when the sea moved over half a metre or the loaded scenes changed).
        private SeabedCell SeabedCellAt(long key,float realSea)
        {
            int scenes=SceneStamp();
            if (scenes!=seabedScenes || seabedCells.Count>200000) { seabedCells.Clear(); seabedScenes=scenes; }
            if (!seabedCells.TryGetValue(key,out var cell) || Mathf.Abs(cell.Sea-realSea)>0.5f)
            {
                cell=Probe(Seabed.Centre(key,SeabedStep),realSea);
                seabedCells[key]=cell;
            }
            return cell;
        }

        // The highest surface her hull can touch under a point, from SeabedTop over the sea down past the deepest keel the planner
        // takes (12 m); boats (bodies) are left to the swept capsule.
        private SeabedCell Probe(RoutePoint at,float realSea)
        {
            var origin=FloatingOriginManager.instance;
            var start=origin.RealPosToShiftingPos(new Vector3(at.x,realSea+SeabedTop,at.z));
            int count=Physics.RaycastNonAlloc(start,Vector3.down,downHits,SeabedTop+12.5f,HitMask(),QueryTriggerInteraction.Ignore);
            var cell=new SeabedCell { Sea=realSea, Top=float.NegativeInfinity };
            for (int i=0;i<count;++i)
            {
                var c=downHits[i].collider;
                if (!c || c.attachedRigidbody || own(c) || c.GetComponentInParent<ShipItem>()) continue;
                if (!Touches(c.gameObject.layer)) continue;
                float top=origin.ShiftingPosToRealPos(downHits[i].point).y;
                if (top>cell.Top) { cell.Top=top; cell.What=c; }
            }
            // Inside a cliff (the user, 2026-10-11, on the harbour map: "your check point is wrong, you point boat through cliff"): a ray
            // from SeabedTop starts inside the rock and doesn't meet it, so the inside of a cliff read as deep water wherever its walls
            // have a gap. Rock over her (met from RockTop down to SeabedTop) with nothing met looking up from the water is her
            // inside a cliff: land. An arch she sails under has an underside facing the water, which the look up meets.
            if (cell.Top<realSea)
            {
                var high=origin.RealPosToShiftingPos(new Vector3(at.x,realSea+RockTop,at.z));
                int over=Physics.RaycastNonAlloc(high,Vector3.down,downHits,RockTop-SeabedTop,HitMask(),QueryTriggerInteraction.Ignore);
                Collider rock=null;
                for (int i=0;i<over && !rock;++i)
                {
                    var c=downHits[i].collider;
                    if (c && !c.attachedRigidbody && !own(c) && !c.GetComponentInParent<ShipItem>() && Touches(c.gameObject.layer)) rock=c;
                }
                if (rock)
                {
                    var low=origin.RealPosToShiftingPos(new Vector3(at.x,realSea+0.1f,at.z));
                    int under=Physics.RaycastNonAlloc(low,Vector3.up,downHits,RockTop,~0,QueryTriggerInteraction.Ignore);
                    bool roofed=false;
                    for (int i=0;i<under && !roofed;++i)
                    {
                        var c=downHits[i].collider;
                        roofed=c && !c.attachedRigidbody && !own(c) && !c.GetComponentInParent<ShipItem>();
                    }
                    if (!roofed) { cell.Top=realSea+SeabedTop; cell.What=rock; }
                }
            }
            return cell;
        }

        // Decision 157's harbour chart, one cell `size` metres square round `at`, as her terrain detection judges it (decision 198 g: the
        // user's "the chart keeps each cell's depth, the shallowest of its probes, more probes where the seabed lies within a few metres
        // of her keel"): its seabed's top (`top`, stable y; negative infinity where no probe met one), the highest of its probes (its
        // middle and four a quarter of its size off, and a 4 by 4 grid besides where that lies within DepthRefine of her keel's limit),
        // and whether her hull fits there above the water: no probe met land (above the sea, or the inside of a cliff), and her beam
        // round its middle, from SurfaceBand under the sea's surface to 3 m over it, clear of everything fixed (boats and items are left to
        // her live look-ahead: they move). Its depth is judged by each flood at the sea level then (WaterField), so the tide changes no
        // verdict of this (the beam reached her keel before: the seabed under her beam is now her probes' and her line's spare). Inside the
        // envelope of an island whose scene isn't loaded (`unknown`): never water, as her live checks judge it (Check).
        internal const float DepthRefine=3f, SurfaceBand=0.5f;
        internal struct ChartExclusion { internal float X, Z, Radius; }
        // Decision 225 (21): how far beyond an unloaded island's envelope she keeps off it, in her live checks (Check) and her chart alike:
        // her radius and her length (the chart kept only her radius, a band every live check refused, where her line could lead her in).
        internal float UnloadedReach => Radius+Length;
        internal static bool InsideUnloaded(float x,float z,ChartExclusion e,float reach) => (x-e.X)*(x-e.X)+(z-e.Z)*(z-e.Z)<(e.Radius+reach)*(e.Radius+reach);
        // Decision 222: `low`, how far the island stands below its true height (IslandHeight.Drop; + low): it is probed against a sea that
        // much lower (its rays reach that much deeper, its land and its overlap judged there), and its top kept true (raised by it).
        internal bool ChartCell(RoutePoint at,float size,IList<ChartExclusion> unknown,out float top,float low=0f)
        {
            top=float.NegativeInfinity;
            if (unknown!=null)
                foreach (var e in unknown)
                    if (InsideUnloaded(at.x,at.z,e,UnloadedReach)) return false;
            bool fits=ChartCellAt(at,size,low,ref top);
            if (top>float.NegativeInfinity) top+=low;
            return fits;
        }

        private bool ChartCellAt(RoutePoint at,float size,float low,ref float top)
        {
            var origin=FloatingOriginManager.instance;
            float sea=SeaLevel()-low, realSea=origin.ShiftingPosToRealPos(new Vector3(0,sea,0)).y, q=size/4f;
            if (!ChartProbe(at,realSea,ref top)) return false;
            for (int k=0;k<4;k++)
                if (!ChartProbe(new RoutePoint(at.x+((k&1)==0 ? -q : q),at.z+((k&2)==0 ? -q : q)),realSea,ref top)) return false;
            if (top>realSea-Draft-DepthRefine)
                for (int i=0;i<4;i++)
                    for (int j=0;j<4;j++)
                        if (!ChartProbe(new RoutePoint(at.x+(i-1.5f)*q,at.z+(j-1.5f)*q),realSea,ref top)) return false;
            var c=origin.RealPosToShiftingPos(new Vector3(at.x,0,at.z)); c.y=sea;
            var bottom=c+Vector3.up*(-SurfaceBand+Radius); var over=c+Vector3.up*Mathf.Max(-SurfaceBand+Radius,3);
            int count=Physics.OverlapCapsuleNonAlloc(bottom,over,Radius,overlaps,HitMask(),QueryTriggerInteraction.Ignore);
            if (count==overlaps.Length) return false;
            for (int i=0;i<count;++i)
            {
                var o=overlaps[i];
                if (!o || o.attachedRigidbody || own(o) || o.GetComponentInParent<ShipItem>()) continue;
                if (!Touches(o.gameObject.layer)) continue;
                return false;
            }
            return true;
        }

        // One probe of a chart cell: its seabed's top into `top` (the highest kept); false where it met land.
        private bool ChartProbe(RoutePoint at,float realSea,ref float top)
        {
            float met=Probe(at,realSea).Top;
            if (met>top) top=met;
            return !(met>realSea);
        }

        // The envelopes of the islands whose scenes aren't loaded within `reach` of `centre` (their geometry unknown), for a chart about
        // to be made there (ChartCell).
        internal List<ChartExclusion> UnloadedNear(RoutePoint centre,float reach)
        {
            var list=new List<ChartExclusion>();
            var tracker=IslandDistanceTracker.instance;
            if (!tracker || tracker.islands==null || !FloatingOriginManager.instance) return list;
            foreach (var island in tracker.islands)
            {
                if (!island || island.islandIndex<=0) continue;
                if (island.SceneLoaded() && SceneManager.GetSceneByBuildIndex(island.islandIndex).isLoaded) continue;
                if (!islands.TryGetValue(island.islandIndex,out var envelope))
                    envelope=new IslandEnvelope { Centre=FloatingOriginManager.instance.ShiftingPosToRealPos(island.GetPosition()), Radius=island.islandLoadDistance };
                if (new Vector2(envelope.Centre.x-centre.x,envelope.Centre.z-centre.z).magnitude>reach+envelope.Radius) continue;
                list.Add(new ChartExclusion { X=envelope.Centre.x, Z=envelope.Centre.z, Radius=envelope.Radius });
            }
            return list;
        }

        // The island whose envelope (its geometry and 100 m, RefreshIslands) holds `near`, as her sailor charts it.
        internal bool IslandAround(RoutePoint near,out RoutePoint centre,out float radius)
        {
            centre=default(RoutePoint); radius=0;
            var tracker=IslandDistanceTracker.instance;
            if (tracker && tracker.islands!=null && FloatingOriginManager.instance && Time.time>=islandsAt) { RefreshIslandsIfChanged(tracker); islandsAt=Time.time+5; }
            float best=float.MaxValue;
            foreach (var e in islands.Values)
            {
                float gap=new Vector2(e.Centre.x-near.x,e.Centre.z-near.z).magnitude;
                if (gap<e.Radius && gap<best) { best=gap; centre=new RoutePoint(e.Centre.x,e.Centre.z); radius=e.Radius; }
            }
            return best<float.MaxValue;
        }

        // Which island scenes are loaded: the seabed cache holds while it doesn't change.
        private static int SceneStamp()
        {
            int stamp=17;
            for (int i=0;i<SceneManager.sceneCount;i++)
            {
                var scene=SceneManager.GetSceneAt(i);
                if (scene.isLoaded) stamp=stamp*31+scene.buildIndex+1;
            }
            return stamp;
        }
        // Her way from a to b (the user, 2026-10-11: "if you only check in front of bow, you need to consider all hull width also"; the
        // Cog's departure struck Dragon Cliffs' shoal): her hull's footprint, a box her beam wide (Radius each side) and her length
        // long (ForeExtent ahead of her origin, AftExtent astern), from her draft to 3 m over the water, lying along the way, swept
        // from a to b and clear at b. At a, her beam round her (as before): she lies there as she lies, not turned onto the way (run
        // 20261011-102026: the box turned onto a way out at Gold Rock City's berth met the pier, and the Brig's booking found "No clear
        // way out of the harbour"); her own turns are TurnClear's. So what the box already overlaps at a (its sweep's hits at no
        // distance) is left out. A point (a = b) has no way to lie along: her beam round it. It was a capsule her beam wide at her
        // origin all along, which left out her bow and stern.
        private bool Swept(Vector3 a,Vector3 b)
        {
            var bottom=Vector3.up*(-Draft+Radius); var top=Vector3.up*Mathf.Max(-Draft+Radius,3);
            if (!PointClear(a+bottom,a+top)) return false;
            var delta=b-a; float distance=delta.magnitude;
            if (distance<=0.01f) return true;
            var rotation=Quaternion.LookRotation(delta/distance,Vector3.up);
            HullBox(rotation,out var offset,out var half);
            if (!BoxClear(b+offset,half,rotation)) return false;
            int count=Physics.BoxCastNonAlloc(a+offset,half,delta/distance,hits,rotation,distance,HitMask(),QueryTriggerInteraction.Ignore);
            if (count==hits.Length) { Obstruction="Clearance query overflow"; return false; }
            for (int i=0;i<count;++i)
            {
                if (hits[i].distance<=0f && hits[i].point==Vector3.zero) continue;
                if (Obstacle(hits[i].collider,hits[i].point)) return false;
            }
            return true;
        }

        // Her beam round her middle, swept from a to b: the harbour's own moves (ClearHull).
        private bool SweptBeam(Vector3 a,Vector3 b)
        {
            var bottom=Vector3.up*(-Draft+Radius); var top=Vector3.up*Mathf.Max(-Draft+Radius,3);
            if (!PointClear(a+bottom,a+top) || !PointClear(b+bottom,b+top)) return false;
            var delta=b-a; float distance=delta.magnitude;
            if (distance>0.01f)
            {
                int count=Physics.CapsuleCastNonAlloc(a+bottom,a+top,Radius,delta/distance,hits,distance,HitMask(),QueryTriggerInteraction.Ignore);
                if (count==hits.Length) { Obstruction="Clearance query overflow"; return false; }
                for (int i=0;i<count;++i) if (Obstacle(hits[i].collider,hits[i].point==Vector3.zero ? a+bottom : hits[i].point)) return false;
            }
            return true;
        }

        // Her hull's box lying along `rotation`: its centre from her origin at the sea's surface, and its half-size.
        private void HullBox(Quaternion rotation,out Vector3 offset,out Vector3 half)
        {
            half=new Vector3(Radius,(Draft+3)/2f,(ForeExtent+AftExtent)/2f);
            offset=Vector3.up*((3-Draft)/2f)+rotation*Vector3.forward*((ForeExtent-AftExtent)/2f);
        }

        // Her turn where she lies, from her heading to `to` (the user, 2026-10-11): her hull's box every TurnStep degrees of the turn
        // (the last at `to`) clear, and the seabed under her bow and stern there (her keel's depth and KeelMargin, as SeabedClear). Her
        // heading itself isn't checked: she lies there.
        internal const float TurnStep=15f;
        internal bool TurnClear(RoutePoint at,double from,double to)
        {
            if (!RoutePlanner.Finite(at) || !FloatingOriginManager.instance) return false;
            double turn=NavigationMath.Delta(from,to);
            int steps=(int)Math.Ceiling(Math.Abs(turn)/TurnStep);
            if (steps==0) return true;
            Checks++;
            float sea=SeaLevel();
            var p=FloatingOriginManager.instance.RealPosToShiftingPos(new Vector3(at.x,0,at.z)); p.y=sea;
            float realSea=FloatingOriginManager.instance.ShiftingPosToRealPos(new Vector3(0,sea,0)).y;
            for (int i=1;i<=steps;i++)
            {
                double heading=from+turn*i/steps;
                var rotation=Quaternion.Euler(0f,(float)heading,0f);
                HullBox(rotation,out var offset,out var half);
                if (!BoxClear(p+offset,half,rotation)) return false;
                foreach (float along in new[] { ForeExtent, -AftExtent })
                {
                    var tip=Voyage.Ahead(at,heading,along);
                    var cell=SeabedCellAt(Seabed.At(tip,SeabedStep),realSea);
                    if (cell.Top<=realSea-Draft) continue;
                    Obstruction="Shallows under her "+(along>0 ? "bow" : "stern")+" as she turns to "+NavigationMath.Normalize(heading).ToString("F0")+": "+
                        (cell.What ? cell.What.name : "the seabed")+" "+(realSea-cell.Top).ToString("F1")+" m deep (her keel needs "+Draft.ToString("F2")+" m)";
                    return false;
                }
            }
            return true;
        }
        // Wider (D17): a box `radius` to each side, from her draft to 3 m over the water, swept along the way and `radius` past
        // each end. A capsule that wide could reach her draft only at its middle (run 20261007-124359: the cliff's foot lay
        // 2.3 m under her bow, under the water).
        private bool Boxed(Vector3 a,Vector3 b,float radius)
        {
            var delta=b-a; float distance=delta.magnitude;
            var rotation=distance>0.01f ? Quaternion.LookRotation(delta/distance,Vector3.up) : Quaternion.identity;
            var half=new Vector3(radius,(Draft+3)/2f,radius); var lift=Vector3.up*((3-Draft)/2f);
            if (!BoxClear(a+lift,half,rotation) || !BoxClear(b+lift,half,rotation)) return false;
            if (distance>0.01f)
            {
                int count=Physics.BoxCastNonAlloc(a+lift,half,delta/distance,hits,rotation,distance,HitMask(),QueryTriggerInteraction.Ignore);
                if (count==hits.Length) { Obstruction="Clearance query overflow"; return false; }
                for (int i=0;i<count;++i) if (Obstacle(hits[i].collider,hits[i].point==Vector3.zero ? a+lift : hits[i].point)) return false;
            }
            return true;
        }
        private bool BoxClear(Vector3 centre,Vector3 half,Quaternion rotation)
        {
            int count=Physics.OverlapBoxNonAlloc(centre,half,overlaps,rotation,HitMask(),QueryTriggerInteraction.Ignore);
            if (count==overlaps.Length) { Obstruction="Clearance overlap overflow"; return false; }
            for (int i=0;i<count;++i) if (Obstacle(overlaps[i],centre)) return false;
            return true;
        }
        private bool MovingTrafficClear(RoutePoint from,RoutePoint to,float speed)
        {
            if (Time.time>=trafficAt)
            {
                var bodies=UnityEngine.Object.FindObjectsOfType<BoatRefs>().Select(b=>b.GetComponent<Rigidbody>())
                    .Concat(UnityEngine.Object.FindObjectsOfType<NPCBoatController>().Select(b=>b.GetComponent<Rigidbody>()))
                    .Where(b=>b).ToList();
                customTraffic.Clear();
                // Better NPC Boats replaces NPCBoatController; its pooled hulls
                // have no BoatRefs either. Resolve the optional mod once per scan.
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.raddude.betternpcboats", out var plugin))
                {
                    var type=plugin.Instance.GetType().Assembly.GetType("BetterNPCBoats.BetterNPCFishingBoat");
                    if (type==null || !typeof(Behaviour).IsAssignableFrom(type))
                        throw new NotSupportedException("Better NPC Boats traffic controller contract changed.");
                    foreach (Behaviour controller in UnityEngine.Object.FindObjectsOfType(type))
                    {
                        var body=controller.GetComponent<Rigidbody>();
                        if (!controller.isActiveAndEnabled || !body) continue;
                        bodies.Add(body); customTraffic[body]=controller;
                    }
                }
                traffic=bodies.Distinct().ToArray();
                trafficAt=Time.time+2;
            }
            foreach (var body in traffic)
            {
                if (!body || !body.gameObject.activeInHierarchy || body==boat.Body || body.isKinematic) continue;
                // Under way only (decision 109): a boat lying still is an obstacle Clear sees by its colliders.
                if (!TrafficPrediction.Moving(body.velocity.x,body.velocity.z)) continue;
                // A scene can return a boat to its pool between registry refreshes.
                if (customTraffic.TryGetValue(body,out var controller) && (!controller || !controller.isActiveAndEnabled)) continue;
                var start=FloatingOriginManager.instance.ShiftingPosToRealPos(body.position);
                if (TrafficPrediction.Intersects(from,to,speed,new RoutePoint(start.x,start.z),
                    body.velocity.x,body.velocity.z,Radius+12))
                { Obstruction="Predicted crossing traffic: "+body.name; return false; }
            }
            return true;
        }
        // The survey below (every collider in the scene and every island's renderers: up to 17.6 ms, 90 times in the 7.5 minutes of run
        // 20261011-203805, every 5 s) runs again only when the loaded scenes have changed (an island's geometry comes and goes with its
        // scene), or after IslandsAtMost (the review of 2026-10-11 night). Envelopes are kept in stable coordinates, so the world's
        // shifts need none.
        private const float IslandsAtMost=60f;
        private int islandsScenes=int.MinValue;
        private float islandsForcedAt;
        private void RefreshIslandsIfChanged(IslandDistanceTracker tracker)
        {
            int scenes=SceneStamp();
            if (scenes==islandsScenes && Time.time<islandsForcedAt) return;
            islandsScenes=scenes; islandsForcedAt=Time.time+IslandsAtMost;
            RefreshIslands(tracker);
        }

        private void RefreshIslands(IslandDistanceTracker tracker)
        {
            // Keep a conservative coastline envelope when a nearby island unloads. Horizon
            // meshes remain present; loaded shallow/static colliders expand, never shrink, it.
            var staticGeometry=UnityEngine.Object.FindObjectsOfType<Collider>().Where(c=>c && c.enabled && !c.isTrigger && !c.attachedRigidbody && !c.GetComponentInParent<ShipItem>() &&
                !c.GetComponent<CleanableObjectCollider>() && c.bounds.max.y>=-Draft).ToArray();
            foreach(var island in tracker.islands)
            {
                if (!island || island.islandIndex<=0) continue;
                var renderers=island.GetComponentsInChildren<MeshRenderer>(true).Where(r=>IslandGeometry(r,island)).ToArray();
                bool valid=false; Bounds bounds=default(Bounds);
                foreach(var renderer in renderers) { if(!valid) {bounds=renderer.bounds;valid=true;} else bounds.Encapsulate(renderer.bounds); }
                foreach(var c in staticGeometry.Where(c=>c.gameObject.scene.buildIndex==island.islandIndex))
                { if(!valid) {bounds=c.bounds;valid=true;} else bounds.Encapsulate(c.bounds); }
                if (!valid || !NavigationMath.Finite(bounds.size.sqrMagnitude) || Mathf.Max(bounds.size.x,bounds.size.z)<20) continue;
                var centre=FloatingOriginManager.instance.ShiftingPosToRealPos(bounds.center);
                float radius=new Vector2(bounds.extents.x,bounds.extents.z).magnitude+100;
                IslandEnvelope previous;
                if (islands.TryGetValue(island.islandIndex,out previous))
                {
                    // Union with earlier loaded geometry in stable coordinates after origin shifts.
                    var offset=Vector3.ProjectOnPlane(centre-previous.Centre,Vector3.up); float separation=offset.magnitude;
                    if(previous.Radius>=separation+radius) { centre=previous.Centre; radius=previous.Radius; }
                    else if(radius<separation+previous.Radius)
                    { float combined=(separation+radius+previous.Radius)*0.5f; centre=previous.Centre+offset*((combined-previous.Radius)/Mathf.Max(0.001f,separation)); radius=combined; }
                }
                else Log.Info($"Island exclusion {island.name}: stable centre={centre}, radius={radius:F0}m (horizon geometry + shallow static colliders + 100m margin).");
                islands[island.islandIndex]=new IslandEnvelope {Centre=centre,Radius=radius};
            }
        }
        private bool IslandGeometry(MeshRenderer r,IslandHorizon island)
        {
            // Horizon hierarchies retain disabled authoring copies, including
            // an8km water plane under Firefly Grotto. Those objects are not
            // coastline. Active meshes still count even when their renderer is
            // temporarily hidden; missing geometry retains the load-radius fallback.
            if (!r || !r.gameObject.activeInHierarchy || r.bounds.max.y-island.transform.position.y < -Draft) return false;
            // Inspected vanilla horizon hierarchies also contain kilometre-wide water
            // planes and Crest depth-input quads. They are render inputs, not land.
            var materials=r.sharedMaterials.Where(m=>m).ToArray();
            return materials.Length==0 || !materials.All(m=>m.name=="test water" ||
                m.name.StartsWith("Crest/Inputs/Depth/",StringComparison.Ordinal) ||
                m.shader && m.shader.name.StartsWith("Crest/Inputs/",StringComparison.Ordinal));
        }
        private bool PointClear(Vector3 bottom,Vector3 top)
        {
            int count=Physics.OverlapCapsuleNonAlloc(bottom,top,Radius,overlaps,HitMask(),QueryTriggerInteraction.Ignore);
            if (count==overlaps.Length) { Obstruction="Clearance overlap overflow"; return false; }
            for (int i=0;i<count;++i) if (Obstacle(overlaps[i],(bottom+top)*0.5f)) return false;
            return true;
        }
        private bool Obstacle(Collider c,Vector3 at)
        {
            if (!c) return false;
            // A collider her hull can't touch is no obstacle: the game's layer collision matrix lets her pass through it (candidate
            // AQ; run 20261009-201141 found the player's own collider, layer OnlyPlayerCol, among them; Dragon Cliffs' "Cube"
            // colliders, which held her tow, do collide with her hull's layers BoatCapsule and Ignore Raycast). The queries ask for her
            // layers alone (HitMask), so this is a guard; tested first, as the cheapest.
            if (!Touches(c.gameObject.layer))
            {
                if (passedThrough.Add(c))
                    Log.Info("Navigation: "+c.name+" (layer "+LayerMask.LayerToName(c.gameObject.layer)+") is no obstacle: her hull's layers ("+
                        string.Join(", ",hullLayers.Select(l => LayerMask.LayerToName(l)).ToArray())+") pass through it.");
                return false;
            }
            if (own(c) || c.GetComponentInParent<ShipItem>()) return false;
            // A boat under way (faster than TrafficPrediction.MovingSpeed) is traffic, not an obstacle (decisions 102, 109: she gives way
            // to it, her sails in to steerage and her course kept, MovingTrafficClear; she never changes course or holds for it). A boat
            // lying still stays an obstacle (the design review, 2026-10-11: every boat counted here, so traffic could hem her in).
            if (c.attachedRigidbody && TrafficPrediction.Moving(c.attachedRigidbody.velocity.x,c.attachedRigidbody.velocity.z)) return false;
            if (Obstruction==null) Log.Info("Navigation first obstruction: "+c.name+"; bounds="+c.bounds+"; closestToHull="+c.ClosestPoint(boat.Root.position));
            // What blocks her, once per collider (the user, 2026-10-09: what Dragon Cliffs' "Cube" colliders are and where they lie):
            // its kind and layer, where the query met it in her own frame, and how deep that is against her keel.
            if (described.Add(c))
            {
                float sea=Crest.OceanRenderer.Instance ? Crest.OceanRenderer.Instance.SeaLevel : 0f;
                var local=boat.Root.InverseTransformPoint(at);
                string kind=c is MeshCollider mesh ? (mesh.convex ? "convex mesh" : "mesh") : c is BoxCollider ? "box" : c is CapsuleCollider ? "capsule" : c is SphereCollider ? "sphere" : c.GetType().Name;
                float keel=boat.Root.TransformPoint(new Vector3(0,KeelY,0)).y-sea;
                Log.Info("Navigation obstruction: "+c.name+" ("+kind+", layer "+LayerMask.LayerToName(c.gameObject.layer)+(c.transform.parent ? ", under "+c.transform.parent.name : "")+
                    "): met at "+local.ToString("F1")+" in her frame (right, up, ahead), "+(at.y-sea).ToString("F1")+" m against the sea's surface; her keel at "+
                    keel.ToString("F1")+" m, the sweep down to "+(-Draft).ToString("F1")+" m; its scale "+c.transform.lossyScale.ToString("F1")+", bounds "+c.bounds+".");
            }
            // Whose collider it is (launch 5: "Cube" blocked her tow for 5 minutes; a name alone didn't tell what it was).
            string parents=""; var up=c.transform.parent;
            for (int i=0;i<3 && up;i++,up=up.parent) parents+=(i>0 ? " < " : "")+up.name;
            Obstruction=c.name+(parents.Length>0 ? " ("+parents+")" : "")+(c.attachedRigidbody ? " [a body]" : ""); return true;
        }
    }

    // ==== StaticHelmsman ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    /// <summary>Visual-only copy of a vanilla NPC, with all game behaviour stripped.</summary>
    public sealed class StaticHelmsman : IDisposable
    {
        private readonly GameObject instance;

        private StaticHelmsman(GameObject instance) { this.instance = instance; }

        public static GameObject CaptureTemplate(Transform persistentParent)
        {
            if (!persistentParent) throw new ArgumentNullException(nameof(persistentParent));
            var candidates = Resources.FindObjectsOfTypeAll<NPCAnimations>()
                .Where(a => a && a.gameObject.scene.IsValid()).ToArray();
            NPCAnimations source = candidates.FirstOrDefault(a => HasAncestorNamed(a.transform, "Modular NPC"));
            if (!source) source = candidates.FirstOrDefault();
            if (!source) return null;

            var visualRoot = FindAncestorNamed(source.transform, "Modular NPC");
            if (!visualRoot)
            {
                var npcPlayer = source.GetComponentInParent<NPCPlayerCol>();
                visualRoot = npcPlayer ? npcPlayer.transform : null;
            }
            if (!visualRoot || visualRoot.GetComponentsInChildren<Renderer>(true).Length == 0) return null;

            var staging = new GameObject("ShipTransportHelmsmanTemplateStaging");
            staging.SetActive(false);
            staging.transform.SetParent(persistentParent, false);
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(visualRoot.gameObject, staging.transform, false);
                clone.name = "ShipTransportHelmsmanTemplate";
                clone.SetActive(false);
                StripBehaviourAndPhysics(clone);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.SetParent(persistentParent, false);
                VerifyVisualOnly(clone);
                return clone;
            }
            catch
            {
                if (clone) UnityEngine.Object.Destroy(clone);
                throw;
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }
        }

        public static StaticHelmsman Create(BoatContext boat, GameObject template, Vector3 localOffset, float yaw)
        {
            if (boat == null || !boat.Valid) throw new ArgumentException("A valid vessel is required.", nameof(boat));
            if (!template) throw new NotSupportedException("No vanilla NPC visual template is available in this scene.");
            if (!NavigationMath.Finite(localOffset.sqrMagnitude) || !NavigationMath.Finite(yaw))
                throw new ArgumentException("NPC pose must be finite.");
            var model = boat.Refs.boatModel;
            var clone = UnityEngine.Object.Instantiate(template, model, false);
            try
            {
                clone.name = "ShipTransportStaticHelmsman";
                clone.SetActive(false);
                StripBehaviourAndPhysics(clone);
                clone.transform.localPosition = model.InverseTransformPoint(boat.Wheel.transform.position) + localOffset;
                clone.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                VerifyVisualOnly(clone);
                foreach (var skinned in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    skinned.updateWhenOffscreen = true;
                clone.SetActive(true);
                return new StaticHelmsman(clone);
            }
            catch
            {
                if (clone) UnityEngine.Object.Destroy(clone);
                throw;
            }
        }

        private static bool HasAncestorNamed(Transform transform, string exactName)
        {
            return FindAncestorNamed(transform, exactName);
        }

        private static Transform FindAncestorNamed(Transform transform, string exactName)
        {
            for (var current = transform; current; current = current.parent)
                if (string.Equals(current.name, exactName, StringComparison.OrdinalIgnoreCase)) return current;
            return null;
        }

        private static void StripBehaviourAndPhysics(GameObject root)
        {
            // Remove scripts first so no NPC logic, animation, or game registration survives the copy.
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour) UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator) UnityEngine.Object.DestroyImmediate(animator);
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                if (animation) UnityEngine.Object.DestroyImmediate(animation);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                if (collider) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var joint in root.GetComponentsInChildren<Joint>(true))
                if (joint) UnityEngine.Object.DestroyImmediate(joint);
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
                if (body) UnityEngine.Object.DestroyImmediate(body);
            foreach (var audio in root.GetComponentsInChildren<AudioSource>(true))
                if (audio) UnityEngine.Object.DestroyImmediate(audio);
            StripOptionalComponent(root, "UnityEngine.ParticleSystem");
            StripOptionalComponent(root, "UnityEngine.ParticleSystemRenderer");
        }

        private static void StripOptionalComponent(GameObject root, string fullName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
            if (type == null) return;
            foreach (var component in root.GetComponentsInChildren(type, true))
                if (component) UnityEngine.Object.DestroyImmediate(component);
        }

        private static void VerifyVisualOnly(GameObject root)
        {
            if (root.GetComponentsInChildren<MonoBehaviour>(true).Length != 0 ||
                root.GetComponentsInChildren<Animator>(true).Length != 0 ||
                root.GetComponentsInChildren<Animation>(true).Length != 0 ||
                root.GetComponentsInChildren<Joint>(true).Length != 0 ||
                root.GetComponentsInChildren<Rigidbody>(true).Length != 0 ||
                root.GetComponentsInChildren<Collider>(true).Length != 0 ||
                root.GetComponentsInChildren<AudioSource>(true).Length != 0 ||
                root.GetComponentsInChildren<Renderer>(true).Length == 0)
                throw new NotSupportedException("The NPC copy could not be reduced to a visual-only model.");
        }

        public void Dispose()
        {
            if (instance) { instance.SetActive(false); UnityEngine.Object.Destroy(instance); }
        }
    }

    // ==== WinchDrive ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    internal static class WinchDrive
    {
        internal static void Apply(RopeController rope,GPButtonRopeWinch winch,float target,float dt)
        {
            if (!rope || !winch || winch.rope!=rope || !NavigationMath.Finite(target) || !NavigationMath.Finite(rope.currentLength) || !NavigationMath.Finite(dt) || dt<=0)
                throw new InvalidOperationException("Invalid winch/rope command.");
            GameBindings.WinchInput.SetValue(winch,0f);
            float old=rope.currentLength;
            if (Mathf.Abs(target-old)<0.000001f) return;
            float input=(float)WinchMath.MaximumInput(target<old,rope.currentResistance,rope.CanPull());
            float rate=(float)WinchMath.LengthPerSecond(input,winch.rotationSpeed,winch.gearRatio);
            float next=Mathf.MoveTowards(old,Mathf.Clamp01(target),rate*dt);
            rope.currentLength=next; rope.changed=true;
            winch.transform.Rotate(Vector3.forward,-(next-old)*360*winch.gearRatio,Space.Self);
            GameBindings.WinchLastAngle.SetValue(winch,Mathf.DeltaAngle(0,winch.transform.localEulerAngles.z));
            GameBindings.WinchInput.SetValue(winch,next==old?0f:input);
        }

        // Decision 231: a brace no winch holds (RigScanner.UnwinchedBrace) set at the rate of its mast's brace winch on that side, as
        // that winch would pull it; the winch itself is left to the brace it holds.
        internal static void ApplyUnwinched(RopeController rope,GPButtonRopeWinch rate,float target,float dt)
        {
            if (!rope || !rate || !NavigationMath.Finite(target) || !NavigationMath.Finite(rope.currentLength) || !NavigationMath.Finite(dt) || dt<=0)
                throw new InvalidOperationException("Invalid brace command.");
            float old=rope.currentLength;
            if (Mathf.Abs(target-old)<0.000001f) return;
            float input=(float)WinchMath.MaximumInput(target<old,rope.currentResistance,rope.CanPull());
            float speed=(float)WinchMath.LengthPerSecond(input,rate.rotationSpeed,rate.gearRatio);
            rope.currentLength=Mathf.MoveTowards(old,Mathf.Clamp01(target),speed*dt); rope.changed=true;
        }
    }

    // ==== ArrivalSail ====

    // Slowing down into the destination (the user, 2026-10-11: "vessel should slow down gradually when getting close to destination,
    // so it should start reefing and furled when closing in"), engine-free and host-tested. It caps her sail (Voyage.DockingSailLimit)
    // on her way to the berth; the final approach (the docking) keeps its own caps and furls her at the berth.

    public static class ArrivalSail
    {
        // From SlowFrom metres off the berth her sail eases from all of it to NearShare at Near metres, and the speed she should have
        // from CruiseSpeed to NearSpeed; inside Near the speed wanted is the final approach's own (VoyageDocking: her distance / 100
        // m/s, between DockSlowest and NearSpeed). Measured from the berth, not her approach point (run 20261011-032545, Fort Aestrin: the approach
        // point moved in to 29-50 m, and slowing to it brought her there at 2 m/s, which the docking furled to a 0.5 m/s it couldn't
        // make, and she ran past). Faster than wanted by Over she reefs to half the share, by FarOver to Steerage; slower, her sail
        // comes back.
        public const double SlowFrom = 600, Near = 120, NearShare = 0.25, CruiseSpeed = 5, NearSpeed = 1.2, DockSlowest = 0.35,
            Over = 1.2, FarOver = 1.6, Steerage = 0.1;

        private static void Check(double toBerth)
        {
            if (!(toBerth >= 0)) throw new ArgumentException("Invalid arrival distance.");
        }

        // The speed she should have this far off the berth.
        public static double Speed(double toBerth)
        {
            Check(toBerth);
            if (toBerth <= Near) return Math.Max(DockSlowest, Math.Min(NearSpeed, toBerth / 100));
            if (toBerth >= SlowFrom) return CruiseSpeed;
            return NearSpeed + (CruiseSpeed - NearSpeed) * (toBerth - Near) / (SlowFrom - Near);
        }

        // Decision 196 (the user, 2026-10-12: "use a speed limit for big ship in narrow water"; the Brig ran down Dragon Cliffs' eastern
        // way at 3-4 m/s with all her sail and was hemmed in by its shallows in a gap twice her width, run 20261012-122603): within
        // NarrowLengths of her lengths of what her hull doesn't fit, the speed she should have is NarrowSpeedLeast and NarrowSpeedPerLength
        // for each of her lengths of clearance beyond half of one (infinity when wider: no limit); her sail share is cut to (wanted /
        // speed) squared above it (her speed goes as the square root of the sail drawing, decision 153), never under NarrowShareLeast.
        // NarrowSpeedLeast 2.5 (was 1.5, run 20261012-125559 on FS: the Brig cut to 46% at 2.2 m/s in a light air turned 1-1.8 degrees a
        // second with her helm at 0.7-0.9 in Dragon Cliffs' eastern dogleg and struck Cube_005 at 2.1 m/s; at 4-5 m/s she made the same
        // bends, run -125640): a heavy hull needs way to answer her helm.
        // NarrowShareLeast 0.1 (was 0.2: with 13-14 m/s astern a fifth of the Brig's sail still drove her at 3.5-4 m/s into Dragon Cliffs'
        // eastern gap, runs 20261012-130953 and -132246).
        public const double NarrowLengths = 2, NarrowSpeedLeast = 2.5, NarrowSpeedPerLength = 1, NarrowShareLeast = 0.1;
        // Her turn lead (decision 196): she starts a turn coming in narrow water this far before it, her turning radius times the tangent
        // of half the turn and LeadLengths of her length, so her arc rounds the bend instead of starting at it.
        public const double LeadLengths = 0.5;
        public static double TurnLead(double turn, double radius, double length)
        {
            if (!(radius > 0) || !(length > 0) || double.IsNaN(turn)) throw new ArgumentException("Invalid turn lead sample.");
            double half = Math.Min(Math.Abs(turn), 150) / 2 * Math.PI / 180;
            return radius * Math.Tan(half) + LeadLengths * length;
        }
        public static double NarrowSpeed(double clearance, double length)
        {
            if (!(length > 0) || double.IsNaN(clearance)) throw new ArgumentException("Invalid narrow-water sample.");
            if (clearance >= NarrowLengths * length) return double.PositiveInfinity;
            return Math.Max(NarrowSpeedLeast, NarrowSpeedLeast + NarrowSpeedPerLength * (Math.Max(0, clearance) / length - 0.5));
        }
        // An incoming turn (the user: "speed limit should apply if the ship know there is going to be a turn"): along her way, resampled
        // every `chord` metres (her length), a net change of heading of `angle` degrees or more within TurnWindowChords chords, or her
        // way's first chord that far off her `heading`; the first within `look` metres (the chord completing it). `along`: how far ahead
        // the turn begins. Decision 209 (a) (the user, 2026-10-13: "i notice you didn't slow down when approaching the turn so ship
        // cannot make it"): every turn, whatever its angle; 196's 45 degrees (and 20 in water tighter than her length) are gone (the big
        // hulls' Dragon Cliffs failures of 2026-10-13 began at 16-26 degree turns at 3-4 m/s). AnyTurnAngle only keeps a chart's
        // cell-to-cell steps from reading as turns (Claude's number).
        public const double AnyTurnAngle = 10;
        public const int TurnWindowChords = 3;
        public static bool TurnAhead(IList<RoutePoint> way, double heading, double chord, double look, out double along, out RoutePoint at) =>
            TurnAhead(way, heading, chord, look, out along, out at, out _);
        // The same, and `after`: her way's heading once round the turn (the chord that completes it).
        public static bool TurnAhead(IList<RoutePoint> way, double heading, double chord, double look, out double along, out RoutePoint at, out double after) =>
            TurnAhead(way, heading, chord, look, AnyTurnAngle, out along, out at, out after);
        public static bool TurnAhead(IList<RoutePoint> way, double heading, double chord, double look, double angle, out double along, out RoutePoint at, out double after)
        {
            along = double.NaN; at = default(RoutePoint); after = double.NaN;
            if (way == null || way.Count < 2 || !(chord > 0)) return false;
            var pts = new List<RoutePoint> { way[0] };
            var last = way[0];
            for (int k = 1; k < way.Count; k++)
            {
                var next = way[k];
                double d = RoutePoint.Distance(last, next);
                while (d >= chord)
                {
                    double t = chord / d;
                    last = new RoutePoint((float)(last.x + (next.x - last.x) * t), (float)(last.z + (next.z - last.z) * t));
                    pts.Add(last);
                    d = RoutePoint.Distance(last, next);
                }
            }
            if (RoutePoint.Distance(last, way[way.Count - 1]) > chord * 0.5) pts.Add(way[way.Count - 1]);
            if (pts.Count < 2) return false;
            var hs = new List<double>();
            for (int k = 0; k + 1 < pts.Count; k++) hs.Add(NavigationMath.Heading(pts[k + 1].x - pts[k].x, pts[k + 1].z - pts[k].z));
            if (NavigationMath.Finite(heading) && Math.Abs(NavigationMath.Delta(heading, hs[0])) >= angle) { along = 0; at = pts[0]; after = hs[0]; return true; }
            // The turn is met at the chord that completes it, counted when that chord begins within her look-ahead.
            for (int k = 0; k < hs.Count && k * chord <= look; k++)
                for (int m = 1; m <= TurnWindowChords && k + m < hs.Count && (k + m) * chord <= look; m++)
                    if (Math.Abs(NavigationMath.Delta(hs[k], hs[k + m])) >= angle) { along = (k + m) * chord; at = pts[k + m]; after = hs[k + m]; return true; }
            return false;
        }

        public static double NarrowShare(double speed, double wanted)
        {
            if (double.IsNaN(speed) || double.IsNaN(wanted)) throw new ArgumentException("Invalid narrow-water sample.");
            if (!(speed > wanted)) return 1;
            double r = wanted / speed;
            return Math.Max(NarrowShareLeast, r * r);
        }

        // Decision 209 (a): her sail taken in until she is at the speed wanted, not one cut by NarrowShare's formula (the Brig, run
        // 20261013-060421, cut to 56-65% for 2.6 m/s, kept 3.2-3.4 m/s running before the wind). Each step (a second): from NarrowShare's
        // cut when none is held (`cut` 1 or more), then CutStep less while she is NarrowOver of the speed wanted or faster, never under
        // NarrowShareLeast; CutStep back (a share over 1 is all of it) while she is under NarrowUnder of it. Claude's numbers.
        public const double NarrowOver = 1.05, NarrowUnder = 0.9, CutStep = 0.2;
        public static double NextCut(double cut, double speed, double wanted)
        {
            if (double.IsNaN(cut) || double.IsNaN(speed) || double.IsNaN(wanted)) throw new ArgumentException("Invalid narrow-water sample.");
            if (cut >= 1) return NarrowShare(speed, wanted);
            if (speed >= wanted * NarrowOver) return Math.Max(NarrowShareLeast, cut * (1 - CutStep));
            if (speed < wanted * NarrowUnder) return Math.Min(1, cut / (1 - CutStep));
            return cut;
        }

        // Her sail share's cap this far off the berth at this speed (1 beyond SlowFrom).
        public static double Limit(double toBerth, double speed)
        {
            Check(toBerth);
            if (toBerth >= SlowFrom) return 1;
            double share = toBerth <= Near ? NearShare : NearShare + (1 - NearShare) * (toBerth - Near) / (SlowFrom - Near);
            double wanted = Speed(toBerth);
            if (speed > wanted * FarOver) return Math.Min(share, Steerage);
            if (speed > wanted * Over) return share * 0.5;
            // Slower than wanted, she gets her sail back as she loses way (all of it stopped), so she keeps steerage and can work
            // to windward (the arrival lab, run 20261011-010840: 86 m off at 1.1 m/s, a quarter of her sail).
            if (speed < wanted) return share + (1 - share) * (1 - Math.Max(0, speed) / wanted);
            return share;
        }
    }

    // ==== Backing ====

    // Decision 155 (the user, 2026-10-11, watching the Cog fail Dragon Cliffs' departures: "for ship with square sail, it should be able
    // to utilzed back up manuver to get into more favorable position, if normal tack/wearing is too hard to do"): a ship with a square
    // sail that can't tack (too slow) and has no room to wear, or that is hemmed in, backs her square sail: the game's own wind force on
    // it drives her astern, her helm reversed with her sternway so her bow falls off away from the wind, and she turns where she lies
    // (a box-haul: a wear made going astern) onto a clear, sailable heading, then fills her sails and gathers way. Engine-free, so it is
    // host-tested; VoyageNavigation runs it (TryBacking, BackingTick). The numbers are Claude's, for the user to tune.

    internal static class Backing
    {
        // The apparent wind must be at most MostAbeam off her bow for a backed sail to drive her astern; she stops backing once it is
        // Abaft off it (her bow has fallen off: the wind no longer drives her astern).
        internal const double MostAbeam = 90, Abaft = 110;
        // She is done within DoneWithin degrees of the heading she turns to; she gives up after MostSeconds or MostLengths of her
        // lengths astern. Past MostSeconds she goes on while her bow still closes on that heading (ProgressDegrees within the last
        // ProgressSeconds), up to TurningMostSeconds (run 20261011-143739 on DW: backing across the wind she turned faster as her sternway
        // grew, 1.4-1.7 degrees a second, and was stopped at 60 s 8 degrees short of the wind's eye).
        internal const double DoneWithin = 20, MostSeconds = 60, MostLengths = 2;
        internal const double ProgressSeconds = 20, ProgressDegrees = 5, TurningMostSeconds = 120;

        // Whether a backing `elapsed` seconds in, its heading last closed ProgressDegrees on its target `sinceProgress` seconds ago, is
        // out of time.
        internal static bool OutOfTime(double elapsed, double sinceProgress) =>
            elapsed > TurningMostSeconds || elapsed > MostSeconds && sinceProgress > ProgressSeconds;
        // Her sternway at most MostSternway (m/s): faster, her sail eased to EasedSail until under SternwayEased.
        internal const double MostSternway = 1.5, SternwayEased = 1.2, EasedSail = 0.5;
        // Her helm led at most Lead degrees ahead of her heading in the way she turns; the headings she may turn to every Step degrees
        // up to MostTurn from where her bow is.
        internal const double Lead = 60, Step = 10, MostTurn = 270;
        // Two backings that end without reaching her heading within FailWindow seconds: the old way (decision 123's short tow near her
        // berth, else a hold).
        internal const double FailWindow = 300;
        internal const int MostFails = 2;

        // A backed sail drives her astern only with the apparent wind forward of her beam.
        internal static bool WindAhead(double heading, double apparentFrom) => Math.Abs(NavigationMath.Delta(heading, apparentFrom)) <= MostAbeam;

        // The side her bow falls off to: away from the apparent wind (+1 to starboard, as SailControlAdapters.TurnSide).
        internal static int TurnSide(double heading, double apparentFrom) => NavigationMath.Delta(heading, apparentFrom) > 0 ? -1 : 1;

        // The degrees still to turn from `heading` to `target` going `side` (0 to 360).
        internal static double Remaining(double heading, double target, int side)
        {
            double d = NavigationMath.Delta(heading, target) * side;
            return d < 0 ? d + 360 : d;
        }

        // The heading she turns to: of the headings every Step degrees turning `side` from `heading` (up to MostTurn), sailable
        // (outside close-hauled of the apparent wind, decision 137) and clear (`clear`), the one with the most way made good toward her
        // guide point (`bearing`; TackNavigator.Vmg); NaN when none makes way. Only turns beyond DoneWithin: a nearer target ended
        // the backing as it began, "on her heading", and she was blocked and backed again (run 20261011-233600 on FE: five backings in
        // 5 s, each 1 m astern, the Cog's targets 19 degrees off); a turn that small her helm makes going ahead.
        internal static double Target(double heading, int side, double bearing, double apparentFrom, double noGo, Func<double, bool> clear, double margin = TackNavigator.CloseMargin)
        {
            if (side != 1 && side != -1) throw new ArgumentException("She turns to one side.", nameof(side));
            double best = double.NaN, bestWay = TackNavigator.LeastWay;
            for (double t = DoneWithin + Step; t <= MostTurn + 1e-9; t += Step)
            {
                double h = NavigationMath.Normalize(heading + side * t);
                if (Math.Abs(NavigationMath.Delta(apparentFrom, h)) < TackNavigator.BeatOff(noGo, margin) || !clear(h)) continue;
                double way = TackNavigator.Vmg(h, bearing, apparentFrom, noGo);
                if (way > bestWay + 1e-9) { best = h; bestWay = way; }
            }
            return best;
        }

        // Where her helm is led: at most Lead degrees on from her heading, the way she turns.
        internal static double Steer(double heading, double target, int side) =>
            NavigationMath.Normalize(heading + side * Math.Min(Lead, Remaining(heading, target, side)));

        // Her sail's share while backing: eased when her sternway is over MostSternway, all of it again under SternwayEased.
        internal static double SailLimit(double sternway, double was) => sternway > MostSternway ? EasedSail : sternway < SternwayEased ? 1 : was;
    }

    // ==== BerthApproach ====

    // Her approach to the berth on arrival (decisions 143 and 146), engine-free and host-tested: how far out along the berth's axis
    // her approach point lies, and from which end of the berth she comes in (Voyage.ChooseApproach).

    public static class BerthApproach
    {
        // Her usual approach points: FarLengths of her lengths out (at least LeastFar metres), 75 m, 50 m, 3 and 2 of her lengths.
        public const double LeastFar = 100, FarLengths = 5;
        // Decision 146: when none of those is clear, she looks further out, this much beyond the farthest.
        public static readonly double[] FurtherBy = { 50, 100, 200 };
        // Coming in with the wind more than this many degrees off her bow, she runs in before it (harder to stop at the berth).
        public const double RunFrom = 150;
        // Her way in shorter by more than this (metres) makes the other end the better one, all else equal.
        public const double WayInMargin = 30;
        // Inside this distance of the berth she is slowing into it (ArrivalSail.SlowFrom): Turn's `near`.
        public const double NearFrom = ArrivalSail.SlowFrom;

        // The usual approach distances, farthest first (decision 146: it was list order, so a hull longer than 25 m, whose 3 lengths
        // lie beyond 75 m, could get a point closer than 2 of her lengths while a farther one was clear). Each once, none beyond the
        // farthest.
        public static double[] Distances(double length)
        {
            if (!(length > 0) || double.IsInfinity(length)) throw new ArgumentException("A hull needs a length.");
            double far = Math.Max(LeastFar, length * FarLengths);
            var result = new List<double>();
            foreach (double d in new[] { far, 75, 50, length * 3, length * 2 }.Where(d => d <= far).OrderByDescending(d => d))
                if (result.Count == 0 || result[result.Count - 1] - d > 0.5) result.Add(d);
            return result.ToArray();
        }

        // Decision 146: the points beyond the usual ones, nearest first, looked at only when none of those is clear.
        public static double[] Further(double length) => FurtherBy.Select(x => Distances(length)[0] + x).ToArray();

        // How the wind suits coming in to the berth along this heading: 0 across it (a reach), 1 before it (a run: more than RunFrom
        // off the wind), 2 inside close-hauled (she can't sail in along it).
        public static int WindRank(double heading, double windFrom, double closeHauled)
        {
            double off = Math.Abs(NavigationMath.Delta(windFrom, heading));
            if (off < closeHauled) return 2;
            return off > RunFrom ? 1 : 0;
        }

        // One end of the berth as she would come in from it: a clear approach point there, how the wind suits it (WindRank), and her
        // way in (metres from her to that approach point).
        public struct End
        {
            public bool Clear;
            public int Wind;
            public double WayIn;
            public End(bool clear, int wind, double wayIn) { Clear = clear; Wind = wind; WayIn = wayIn; }
        }

        // Decision 143: whether she turns to the berth's other end. The other end must have a clear approach point; over hers without
        // one it wins at once. Both clear: the wind that suits it better, then her way in shorter by more than WayInMargin. `near`
        // (she is slowing into her berth, ArrivalSail.SlowFrom): she keeps her end unless it has no clear approach point or she can't
        // sail in from it and can from the other, so a wind shift close in doesn't send her round again. A tie keeps hers.
        public static bool Turn(End other, End hers, bool near)
        {
            if (!other.Clear) return false;
            if (!hers.Clear) return true;
            if (hers.Wind == 2 && other.Wind < 2) return true;
            if (near) return false;
            if (other.Wind != hers.Wind) return other.Wind < hers.Wind;
            return other.WayIn < hers.WayIn - WayInMargin;
        }
    }

    // ==== CourseSafety ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // M2 can correct a nearby heading; committed tacks and large turns belong to M4.
    public static class CourseSafety
    {
        public static bool CanFollow(double heading, double target, double windFrom, double noGo)
        {
            if (!NavigationMath.Finite(heading) || !NavigationMath.Finite(target) ||
                !NavigationMath.Finite(windFrom) || !NavigationMath.Finite(noGo) || noGo <= 0 || noGo >= 90)
                return false;
            if (Math.Abs(NavigationMath.Delta(heading, target)) > 45) return false;
            if (Math.Abs(NavigationMath.Delta(windFrom, heading)) < noGo ||
                Math.Abs(NavigationMath.Delta(windFrom, target)) < noGo) return false;
            double turn = NavigationMath.Delta(heading, target);
            double toWind = NavigationMath.Delta(heading, windFrom);
            return !(Math.Sign(turn) == Math.Sign(toWind) && Math.Abs(toWind) <= Math.Abs(turn));
        }

        // The game's own line: it caches a ship's cargo, the clock's start, only beyond 1000 m (BoatHorizon.DistanceCheck); at
        // 950 m nothing started her between the two (review M7).
        public static bool OutsideObserverRange(bool aboard, double distance) =>
            !aboard && (!NavigationMath.Finite(distance) || distance > 1000);

        public static double HeelDegrees(double rightY, double upY)
        {
            if (!NavigationMath.Finite(rightY) || !NavigationMath.Finite(upY))
                throw new ArgumentException("Invalid vessel orientation.");
            return Math.Abs(Math.Atan2(-rightY, upY) * 180 / Math.PI);
        }
    }

    // ==== CustomBerths ====

    // The custom berths (decision 129, D14; SAILING_TRANSPORT_DRAFT.md "The custom berths"): the user's picked cleats at each
    // shipyard's port and where a ship lies alongside them. Engine-free, so the table and the geometry are host-tested; Berths
    // (VoyageDocking.cs) finds the cleats in the game.

    // One port's berth: its two cleats, found by name (or path) under their island (the world scene's `_shifting world/<island>`), the one
    // at her stern and the one at her bow, and the side of her the open water lies on (-1 port, +1 starboard). ExtraCleat: the next
    // cleat along the same dock, beyond her bow (ExtraBeyondBow) or her stern, which a big ship's berth takes too (CustomBerths.Long).
    // AtShipyard: the shipyard's own cleats beside its release spot (decision 217), where the tow draws her across from the spot and the
    // push takes her back onto it (219).
    internal sealed class CustomBerthSpec
    {
        internal readonly string Port, Island, SternCleat, BowCleat, ExtraCleat;
        internal readonly int WaterSide;
        internal readonly bool ExtraBeyondBow, AtShipyard;

        internal CustomBerthSpec(string port, string island, string sternCleat, string bowCleat, int waterSide, string extraCleat = null, bool extraBeyondBow = true, bool atShipyard = false)
        {
            Port = port; Island = island; SternCleat = sternCleat; BowCleat = bowCleat; WaterSide = waterSide;
            ExtraCleat = extraCleat; ExtraBeyondBow = extraBeyondBow; AtShipyard = atShipyard;
        }
    }

    internal static class CustomBerths
    {
        // Decision 217 (the user, 2026-11-03: "Change the waiting berth to shipyard berth so it don't conflict with Economic overhaul's
        // visible trader"; "there is cleat beside shipyard, tow the ship toward cleat and moor it."; of Claude's picks, "your proposed cleat
        // is okay"): at the four vanilla yards the shipyard's own cleats beside its release spot, where the spot lies between two rows of
        // cleats 16-18 m apart; her bow toward the way out, as the spot's heading points; a third cleat for big ships (decision 150). Named
        // from the game's main scene (level24 of Sailwind 0.40, every GPButtonDockMooring read by work\modtools\scene_values, 2026-11-03;
        // ..\outputs\shipwright_sailing_move\cleats-by-spot.txt), each a child of its island; in the spot's axes (x to starboard, z
        // ahead, metres): Dragon Cliffs (heading 328) E (15) at (8.1, -2.5) and E (16) at (8.3, -9.9), E (17) at (8.8, -22.6) astern;
        // Gold Rock City (145) (5) at (10.1, -3.2) and (11) at (9.7, -13.0), (4) at (9.9, 8.8) ahead; Fort Aestrin (114) M (16) at
        // (9.4, 14.6) and M (15) at (9.0, -1.4), M (14) at (8.6, -15.9) astern (decision 232, the user, 2026-11-04: "Yes, move the berth":
        // the pair M (15) and M (14) started a Cog abreast of a chart cell's corner she had no way past); Kicia Bay (292) the port row, E (12) at (-5.2, 4.2) and
        // E (13) at (-5.0, -4.1), E (14) at (-4.6, -12.6) astern. The open water (the spot) lies on her port side but at Kicia Bay, on her
        // starboard. They replace the custom berths along the towns' waterfronts (decision 129, D14; the user's picks of 2026-10-09 to
        // 10-12, decision 202 at Kicia Bay), which lay 7-8 m from EconomyOverhaul's trader berths.
        // Chronos (decision 198, 2026-10-12, kept by 217: the user's five cleats on the south-west pier of Old Chronian's fort, on its own
        // islet south of the town in open sea, and Claude's pick among them; the wide port survey, run 20261012-175751): 4S3 at her stern,
        // 3S8 at her bow, 11.3 m apart, her bow 138 toward the open sea, the open water on her starboard side; 4S5 7 m beyond her stern for
        // big ships. Each cleat named by its path under the island: Old Chronian names 4S1, 4S3, 4S5 and 4S6 on two docks.
        internal static readonly CustomBerthSpec[] Table =
        {
            new CustomBerthSpec("Dragon Cliffs", "island 9 E (dragon cliffs)", "dock_mooring E (16)", "dock_mooring E (15)", -1, "dock_mooring E (17)", false, true),
            new CustomBerthSpec("Gold Rock City", "island 1 A (gold rock)", "dock_spring_attachment (11)", "dock_spring_attachment (5)", -1, "dock_spring_attachment (4)", true, true),
            new CustomBerthSpec("Fort Aestrin", "island 15 M (Fort)", "dock_mooring M (15)", "dock_mooring M (16)", -1, "dock_mooring M (14)", false, true),
            new CustomBerthSpec("Kicia Bay", "island 27 Lagoon Shipyard", "dock_mooring E (13)", "dock_mooring E (12)", 1, "dock_mooring E (14)", false, true),
            new CustomBerthSpec("Chronos", "island 25 (chronos)", "JUNIPER(Clone)/DOCKS/FORT_dock4/FORT_dock_mooring_4S3", "JUNIPER(Clone)/DOCKS/FORT_dock_mooring_3S8", 1,
                "JUNIPER(Clone)/DOCKS/FORT_dock4/FORT_dock_mooring_4S5", false),
        };

        // A big ship (her stored length, Storage.LengthOf, this long or more: the Brig; not the Dhow, 11.6 m) lies along all three
        // cleats: her place moves to the middle of the outer two, half the extra cleat's spacing along the dock.
        internal const double LongHull = 18;
        internal static bool Long(double lengthM) => lengthM >= LongHull;

        internal static CustomBerthSpec Of(string port)
        {
            foreach (var spec in Table)
                if (spec.Port == port) return spec;
            return null;
        }

        // From the line of the cleats to her side: she lies a little off the dock's edge, her lines slack (decision 113).
        internal const double HullOffCleats = 1.5;

        // Her heading: along the cleats' line, from the stern cleat toward the bow cleat.
        internal static double Heading(RoutePoint stern, RoutePoint bow) => NavigationMath.Heading(bow.x - stern.x, bow.z - stern.z);

        // Her middle: abreast of the point midway between the cleats, her half-beam and HullOffCleats out from their line on the
        // water's side.
        internal static RoutePoint Place(RoutePoint stern, RoutePoint bow, int waterSide, double halfBeam)
        {
            if (waterSide != 1 && waterSide != -1) throw new ArgumentException("The water lies to port (-1) or starboard (+1).", nameof(waterSide));
            if (!NavigationMath.Finite(halfBeam) || halfBeam <= 0) throw new ArgumentException("A hull needs a beam.", nameof(halfBeam));
            double dx = bow.x - stern.x, dz = bow.z - stern.z, length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 0.5) throw new ArgumentException("The cleats lie on one spot.");
            // Starboard of a heading (dx, dz) is (dz, -dx).
            double sx = dz / length * waterSide, sz = -dx / length * waterSide, off = halfBeam + HullOffCleats;
            return new RoutePoint((float)((stern.x + bow.x) / 2 + sx * off), (float)((stern.z + bow.z) / 2 + sz * off));
        }
    }

    // ==== HelmHold ====

    // Decision 132 (the user, 2026-10-10 night: "once the direction is right, let go of helm, vanilla's rudder push back will
    // automatically make ship stay in course ... only resume helm control if you stray away from your direction too much"): when she
    // lets go of the helm and when she takes it again. Engine-free, so the rule is host-tested; Voyage.UpdateHelmHold runs it.

    internal enum HelmChange { None, LetGo, Take }

    internal static class HelmHold
    {
        // She lets go within LetGoWithin degrees of her course, turning under LetGoYawRate degrees a second, with more than LetGoSpeed
        // m/s ahead (the game's push back grows with her speed); she takes the helm again beyond TakeHelmBeyond, or off a plain
        // course (a turn, bearing away, in irons, giving way, the harbour's stages), or under LetGoSpeed. Claude's numbers, to tune.
        internal const double LetGoWithin = 5, TakeHelmBeyond = 15, LetGoSpeed = 1, LetGoYawRate = 2;
        // Let go, she takes the helm again at once once she turns faster than this, whatever her course's error (run 20261013-020142,
        // GA's arrival lab: back from the clock 2.7 km off Dragon Cliffs in a grown sea, the Dhow let go at once, on her course, her turn
        // zeroed by the return; she slewed round at 25 degrees a second with her helm centred, and the helm came back only 15 degrees
        // off, too late: she was round 85 degrees in 2 s, lost her way and lay in irons for 10 minutes).
        internal const double TakeHelmYawRate = 6;
        // Decision 134: she lets go only of a light helm (under LightHelm of full); a heavier one on her course is a weather helm the
        // game's rudder wouldn't hold, which reefs instead (the balance, decision 128).
        internal const double LightHelm = 0.5;

        // `plain`: on a plain course (sailing at her guide point or close-hauled, gathering way included); `helm`: her helm now, a
        // share of full.
        internal static HelmChange Next(bool released, bool plain, double error, double yawRate, double forward, double helm)
        {
            if (!NavigationMath.Finite(error) || !NavigationMath.Finite(yawRate) || !NavigationMath.Finite(forward) || !NavigationMath.Finite(helm))
                throw new ArgumentException("Non-finite helm sample.");
            bool free = plain && forward > LetGoSpeed;
            if (released) return !free || Math.Abs(error) > TakeHelmBeyond || Math.Abs(yawRate) > TakeHelmYawRate ? HelmChange.Take : HelmChange.None;
            return free && Math.Abs(error) < LetGoWithin && Math.Abs(yawRate) < LetGoYawRate && Math.Abs(helm) < LightHelm ? HelmChange.LetGo : HelmChange.None;
        }
    }

    // ==== LinePlanner ====

    // Decision 198 (b) (the user, 2026-10-12: "it should know that there is a turn coming and she need to judge if she can make that turn,
    // if not either change course to be able to make the turn, or select further but easier route"): her line out of her harbour,
    // searched over position and heading on her harbour's chart (WaterField, decision 157), never turning tighter than her own circle
    // (TurnCircle). A bend she can make only from wide is set up wide; one she can't make at all is left for a longer way. Engine-free,
    // so it is host-tested; searched a slice at a time (Step) so no frame waits for it (198 j). Her way in (198 h) is searched the same,
    // on her destination's chart flooded from her approach point, to it (LineGoal).

    // A point of her line, every SampleStep metres: where, her heading there, how far along, the chart's clearance there (m), and the
    // way her line turns there (-1 to port, 0 straight, +1 to starboard).
    internal struct LinePoint
    {
        internal float X, Z;
        internal double Heading, Along, Clearance;
        internal int Turn;
        internal RoutePoint Point => new RoutePoint(X, Z);
    }

    // Decision 198 (h): where her line in ends: within Radius of At (her approach point, decision 146), her heading within Tolerance of
    // Heading there (her way in along her berth; NaN: any heading).
    internal sealed class LineGoal
    {
        internal readonly RoutePoint At;
        internal readonly double Heading, Radius, Tolerance;
        internal LineGoal(RoutePoint at, double heading, double radius, double tolerance)
        {
            if (!RoutePlanner.Finite(at) || !(radius > 0) || !(tolerance > 0)) throw new ArgumentException("Invalid line goal.");
            At = at; Heading = double.IsNaN(heading) ? double.NaN : NavigationMath.Normalize(heading); Radius = radius; Tolerance = tolerance;
        }
        internal bool Reached(double x, double z, double heading) =>
            RoutePoint.Distance(new RoutePoint((float)x, (float)z), At) <= Radius && (double.IsNaN(Heading) || Math.Abs(NavigationMath.Delta(heading, Heading)) <= Tolerance);
    }

    internal sealed class LinePlanner
    {
        // Each move of the search: StepLength metres straight, or on an arc of her planned circle, or of twice it. States are told
        // apart by PositionCell metres and HeadingBin degrees, the cheaper kept. HeadingBin 2.5 (was 5, 2026-10-12, building 198 h): an
        // 80 m circle turns 4.3 degrees a step, so a 5-degree bin merged her tightest arc into a less turned state of the same cost and a
        // turn at the very edge of her circle (into a narrow arm from a basin, onto her berth's way at her approach point) was never found.
        internal const double StepLength = 6, PositionCell = 4, HeadingBin = 2.5, SampleStep = 2;
        // A move is longer where the water round her is open: a quarter of her chart's clearance where she is, StepLength to StepMost
        // (it never reaches what lies beyond that quarter). 2026-10-12, building 198 h and k: her line in from her chart's edge, 3 km by
        // water at Dragon Cliffs, ran out of states in open water at 6 m a move; begun nearer, it was already committed to the way her
        // chart's flood chose, whose bend the Brig's circle can't make.
        internal const double StepMost = 30, StepShare = 0.25;
        // Her spare each side (198 b): TurnSpareLengths of her length, at least TurnSpareLeast metres, on every move. A cell her hull
        // doesn't fit has something within her half-beam of its middle, so each point of her hull's centre line keeps twice her
        // half-beam and her spare from the middle of every such cell. Measured on Dragon Cliffs' chart (2026-10-12, an offline copy of
        // this search): so the Brig's line goes south of Cube_005, and the Dhow's, Cog's and Kakam's keep the north gap they passed by;
        // with half the spare on straight runs the Brig's went back through the gap. Her first length off her start needs only her
        // middle in water (she lies there already, maybe close to the dock or in a slip); then the room she needs grows from the room
        // she has where she starts to all of it over StartRampLengths of her length (2026-10-12, building 198 k: from her berth at
        // Dragon Cliffs the Brig's line needed all of it one length off the dock, out of reach of a hull alongside it, and her search
        // ended in 42 states; a line begun in open water has all of it at once).
        internal const double TurnSpareLengths = 0.25, TurnSpareLeast = 4, StartRampLengths = 2;
        // Her spare, the room her line wants at each of her hull's points (twice her half-beam and her spare), and those points along her
        // centre line (her middle, a quarter of her length each way, and her ends less her half-beam, the round of a hull's end): shared
        // with her approach point's judge (decision 201 a, WaterField.HullRoom).
        internal static double SpareFor(double length) => Math.Max(TurnSpareLeast, TurnSpareLengths * length);
        internal static double FullRoom(double length, double halfBeam) => 2 * halfBeam + SpareFor(length);
        internal static double[] Axis(double length, double halfBeam)
        {
            double end = Math.Max(0, length / 2 - halfBeam), quarter = length / 4;
            return end > quarter ? new[] { 0, quarter, -quarter, end, -end } : new[] { 0, end, -end };
        }
        // A turn costs ArcCost more a metre than a straight; a tack through the wind's eye TackLengths of her length more.
        internal const double ArcCost = 0.05, TackLengths = 2;
        // Done at the chart's edge (the open sea it was flooded from) or in water OpenClearance clear.
        internal const double OpenClearance = 300;
        internal const int MostStates = 250000;
        // Decision 201 (c) (the user, 2026-10-12 night: a faster line search): her chart's water distance to go (the flood's, its costs
        // as her line's) counted Weight times against her cost so far, so the search reaches deeper toward her goal before it widens:
        // a beat's near-equal zigzags no longer use up her states. Her line costs at most Weight times her best, as her chart counts
        // cost. Claude's number, from the harbour check's runs at several weights (the draft's decision 201, "As built").
        internal const double Weight = 1.5;
        // The weight in use: the harbour check's --weight <w> sets another to compare (the host test only; the mod never changes it).
        internal static double WeightUsed = Weight;

        private struct Node { internal double X, Z, H, G, Travelled, Step; internal int Parent; internal int Turn; }

        // The length of a move from (x, z).
        private double StepAt(double x, double z)
        {
            int cell = chart.IndexOf(new RoutePoint((float)x, (float)z));
            double clear = cell < 0 ? (goal == null ? OpenClearance : 0) : chart.ClearanceAt(cell);
            return NavigationMath.Clamp(StepShare * clear, StepLength, StepMost);
        }

        private readonly WaterField chart;
        private readonly double length, halfBeam, radiusToward, radiusAway, spare;
        private readonly double windFrom, beat, tackRoom;
        private readonly bool windKnown;
        // Decision 198 (h): her way in ends at her goal, within her chart (its edge is no way out); null: her way out to the open sea.
        private readonly LineGoal goal;
        internal LineGoal Goal => goal;
        // Her heading bins: HeadingBin, or half the turn of her tightest arc in one StepLength where that is more (whole bins round the
        // compass). Decision 201 (d)'s circle of her length turns 14 degrees a step for the Junk: in bins of 2.5 degrees each of her arcs
        // spread over six of them, and her search out of Dragon Cliffs ran out of its states in 15 winds of 16 (the harbour check,
        // 2026-10-12 night); half a step's turn still tells her tightest arc from the next (HeadingBin's own reason).
        private readonly double headingBin;
        private readonly long headingBins;
        private readonly List<Node> nodes = new List<Node>();
        private readonly Dictionary<long, int> closed = new Dictionary<long, int>();
        // Decision 201 (c): each state expanded once. Weighted, a state reached first by a dearer way and expanded is often reached again
        // more cheaply; expanded again each time, the Brig's search at Dragon Cliffs took 10.7 s for the 1.1 s of its 77,000 states
        // unweighted (the host test, 2026-10-12 night). Her line still costs at most its weight times her best.
        private readonly HashSet<long> expanded = new HashSet<long>();
        // The harbour check's --reopen expands a state again when it is reached more cheaply (the search before 201 c), to compare (the
        // host test only; the mod never sets it).
        internal static bool ReopenUsed;
        private readonly Heap open = new Heap();
        private readonly double[] axis;

        internal bool Done { get; private set; }
        internal bool Failed { get; private set; }
        internal string Why { get; private set; }
        internal int States => closed.Count;
        internal double Milliseconds { get; private set; }
        internal List<LinePoint> Line { get; private set; }
        // Her line's cost as her chart counts it (the search's cost so far at its end): the harbour check compares weights by it (201 c).
        internal double Cost { get; private set; } = double.NaN;
        internal double Spare => spare;

        // `radiusToward`, `radiusAway`: her planned circle turning toward the wind and away from it (TurnCircle.Plan). `windFromNow`,
        // `beatNow`: the true wind she has and her beat off it, when the chart was flooded for another (else the chart's). `goal`: her
        // way in (decision 198 h), on a chart flooded from it (WaterField.FloodToBegin).
        internal LinePlanner(WaterField chart, RoutePoint start, double heading, double length, double halfBeam, double radiusToward, double radiusAway,
            double windFromNow = double.NaN, double beatNow = double.NaN, LineGoal goal = null)
        {
            if (chart == null || !chart.Flooded) throw new ArgumentException("Her line needs her flooded chart.", nameof(chart));
            if (!(length > 0) || !(halfBeam > 0) || !(radiusToward > 0) || !(radiusAway > 0) || !RoutePlanner.Finite(start) || !NavigationMath.Finite(heading))
                throw new ArgumentException("Invalid line search.");
            if (goal != null && !chart.HasGoal) throw new ArgumentException("Her line in needs her chart flooded from her goal.", nameof(goal));
            this.chart = chart; this.length = length; this.halfBeam = halfBeam; this.radiusToward = radiusToward; this.radiusAway = radiusAway; this.goal = goal;
            spare = SpareFor(length);
            double tightestTurn = StepLength / Math.Min(radiusToward, radiusAway) * 180 / Math.PI;
            headingBins = Math.Max(1, (long)Math.Floor(360 / Math.Max(HeadingBin, tightestTurn / 2)));
            headingBin = 360.0 / headingBins;
            bool now = NavigationMath.Finite(windFromNow) && NavigationMath.Finite(beatNow);
            windFrom = now ? NavigationMath.Normalize(windFromNow) : chart.WindFrom; windKnown = NavigationMath.Finite(windFrom);
            beat = !windKnown ? 0 : now ? beatNow : chart.WindBeat;
            // Too narrow to tack in (decision 174): under 3 of her lengths or 60 m wide, as the chart counted it.
            tackRoom = !windKnown ? 0 : chart.NarrowClearance > 0 ? chart.NarrowClearance : Math.Max(3 * length, 60) / 2;
            axis = Axis(length, halfBeam);
            int startCell = chart.IndexOf(start);
            if (startCell < 0 || !NavigationMath.Finite(chart.DistanceAt(chart.Start(start)))) { Fail("she is not in her chart's water"); return; }
            // The room she has where she starts: the least, over her hull's points, of their distance from the middle of a cell her hull
            // doesn't fit (bounded below by their cells' clearance, OctileMost aside, less their offset from its middle).
            double h0 = NavigationMath.Normalize(heading) * Math.PI / 180, have = double.PositiveInfinity;
            foreach (double a in axis)
            {
                var p = new RoutePoint((float)(start.x + a * Math.Sin(h0)), (float)(start.z + a * Math.Cos(h0)));
                int cell = chart.IndexOf(p);
                if (cell < 0) continue;
                have = Math.Min(have, chart.Water(cell) ? Math.Max(0, chart.ClearanceAt(cell) / OctileMost - RoutePoint.Distance(p, chart.Centre(cell))) : 0);
            }
            startHave = have;
            Add(new Node { X = start.x, Z = start.z, H = NavigationMath.Normalize(heading), G = 0, Travelled = 0, Parent = -1 });
        }
        private readonly double startHave;

        private void Fail(string why) { Failed = true; Done = true; Why = why; }

        // Searches for up to `budgetMs`; true once done (her line found, or none: Failed).
        internal bool Step(double budgetMs)
        {
            if (Done) return true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (open.Count > 0)
            {
                if (clock.Elapsed.TotalMilliseconds >= budgetMs) { Milliseconds += clock.Elapsed.TotalMilliseconds; return false; }
                open.Pop(out _, out int index);
                var n = nodes[index];
                long key = Key(n.X, n.Z, n.H);
                if (closed.TryGetValue(key, out int seen) && seen != index) continue;
                if (!expanded.Add(key) && !ReopenUsed) continue;
                int cell = chart.IndexOf(new RoutePoint((float)n.X, (float)n.Z));
                bool done = goal != null ? goal.Reached(n.X, n.Z, n.H) : n.Travelled >= 2 * length && (cell < 0 || chart.OnEdge(cell) || chart.ClearanceAt(cell) >= OpenClearance);
                if (done) { Finish(index); Milliseconds += clock.Elapsed.TotalMilliseconds; return true; }
                if (closed.Count >= MostStates) { Fail("no line within " + MostStates + " states"); Milliseconds += clock.Elapsed.TotalMilliseconds; return true; }
                Expand(n, index);
            }
            Milliseconds += clock.Elapsed.TotalMilliseconds;
            Fail("no line her circle and spare allow (" + closed.Count + " states)");
            return true;
        }

        private void Expand(Node n, int index)
        {
            // Straight; her circle to either side; twice her circle to either side.
            for (int move = 0; move < 5; move++)
            {
                int turn = move == 0 ? 0 : move % 2 == 1 ? 1 : -1;
                bool tight = move == 1 || move == 2;
                double radius = 0;
                if (turn != 0)
                {
                    // Toward the wind: turning to the side the wind comes from.
                    double rel = windKnown ? NavigationMath.Delta(n.H, windFrom) : 0;
                    bool toward = windKnown && Math.Sign(rel) == turn;
                    radius = (toward ? radiusToward : radiusAway) * (tight ? 1 : 2);
                }
                if (!Move(n, turn, radius, spare, out var next, out bool tack)) continue;
                int cell = chart.IndexOf(new RoutePoint((float)next.X, (float)next.Z));
                double factor = cell < 0 ? 1 : chart.StepFactor(cell, next.H);
                next.G = n.G + next.Step * factor * (turn == 0 ? 1 : 1 + ArcCost) + (tack ? TackLengths * length : 0);
                next.Parent = index; next.Turn = turn;
                Add(next);
            }
        }

        // One move from `n`: its end, when every point her hull passes is in water (her spare `need` in a turn), and whether it takes
        // her through the wind's eye (a tack: only where the water is wide enough to tack in). `refused`: told why not (ExplainStart).
        private bool Move(Node n, int turn, double radius, double need, out Node next, out bool tack, Action<string> refused = null)
        {
            next = n; tack = false;
            double h0 = n.H * Math.PI / 180, k = turn == 0 ? 0 : turn / radius, step = StepAt(n.X, n.Z);
            next.Step = step;
            for (int s = 1; s <= 3; s++)
            {
                double d = step * s / 3, x, z, h;
                if (turn == 0) { x = n.X + d * Math.Sin(h0); z = n.Z + d * Math.Cos(h0); h = h0; }
                else { h = h0 + k * d; x = n.X + (Math.Cos(h0) - Math.Cos(h)) / k; z = n.Z + (Math.Sin(h) - Math.Sin(h0)) / k; }
                double travelled = n.Travelled + d;
                // Her first length off her start: only her middle in water; then her room grows from what she had to all of it.
                if (travelled < length)
                {
                    if (!Water(x, z, 0, refused)) { refused?.Invoke("her middle " + d.ToString("F1") + " m on (her first length)"); return false; }
                }
                else
                {
                    double full = 2 * halfBeam + need, want = Math.Min(full, startHave + (full - startHave) * Math.Min(1, (travelled - length) / (StartRampLengths * length)));
                    foreach (double a in axis)
                        if (!Water(x + a * Math.Sin(h), z + a * Math.Cos(h), want, refused))
                        { refused?.Invoke("her point " + a.ToString("F1") + " m along her, " + d.ToString("F1") + " m on, wanting " + want.ToString("F1") + " m of room"); return false; }
                }
                next.X = x; next.Z = z; next.H = NavigationMath.Normalize(h * 180 / Math.PI); next.Travelled = travelled;
            }
            if (!windKnown) return true;
            // Her no-go: a heading in it only where the water is wide enough to tack in (she beats there, decision 163); through the
            // wind's eye, a tack, in such water only. Bearing away within it, out of it, anywhere (2026-10-12, building 198 k: lying at
            // her berth with her bow in her no-go, every move was barred and her line never left her start; a ship there falls off).
            double before = NavigationMath.Delta(windFrom, n.H), after = NavigationMath.Delta(windFrom, next.H);
            bool inNoGo = Math.Abs(after) < beat;
            tack = Math.Abs(before) < 90 && Math.Abs(after) < 90 && Math.Sign(before) != Math.Sign(after) && before != 0;
            if (!inNoGo && !tack) return true;
            if (!tack && turn != 0 && Math.Abs(after) > Math.Abs(before)) return true;
            int cell = chart.IndexOf(new RoutePoint((float)next.X, (float)next.Z));
            if (cell < 0 || chart.ClearanceAt(cell) >= tackRoom) return true;
            refused?.Invoke((tack ? "a tack" : "a heading in her no-go (" + Math.Abs(after).ToString("F0") + " degrees off the wind, her beat " + beat.ToString("F0") + ")") +
                " where the water is " + chart.ClearanceAt(cell).ToString("F0") + " m clear, under the " + tackRoom.ToString("F0") + " m she needs to tack in");
            return false;
        }

        // The state nearest her goal (or the open sea) by her chart's water distance, and that distance; -1 before any.
        private int NearestByWater(out double least)
        {
            int nearest = -1; least = double.PositiveInfinity;
            for (int i = 0; i < nodes.Count; i++)
            {
                var p = new RoutePoint((float)nodes[i].X, (float)nodes[i].Z);
                int cell = chart.IndexOf(p);
                double d = cell < 0 ? (goal == null ? 0 : double.PositiveInfinity) : chart.DistanceAt(chart.Start(p));
                if (d < least) { least = d; nearest = i; }
            }
            return nearest;
        }

        // Decision 201 (b): where her search got stuck, once it has failed: the state it reached nearest her goal by water; null when it
        // reached none (or found her line).
        internal RoutePoint? Stuck()
        {
            if (!Failed) return null;
            int i = NearestByWater(out double least);
            return i < 0 || !NavigationMath.Finite(least) ? (RoutePoint?)null : new RoutePoint((float)nodes[i].X, (float)nodes[i].Z);
        }

        // Host diagnostics (the harbour check's --why): where the search got, after it ran: the state nearest her goal by water (her
        // chart's distance), and the states within 30 m of her goal (her way in) with the headings they reached.
        internal string ExplainReach()
        {
            if (nodes.Count == 0) return "no states";
            int nearest = NearestByWater(out double least);
            int close = 0; double lowH = double.PositiveInfinity, highH = double.NegativeInfinity, bestOff = double.PositiveInfinity;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var p = new RoutePoint((float)n.X, (float)n.Z);
                if (goal != null && RoutePoint.Distance(p, goal.At) <= 30)
                {
                    close++;
                    double off = double.IsNaN(goal.Heading) ? 0 : NavigationMath.Delta(goal.Heading, n.H);
                    lowH = Math.Min(lowH, off); highH = Math.Max(highH, off);
                    if (Math.Abs(off) < Math.Abs(bestOff)) bestOff = off;
                }
            }
            var m = nodes[nearest];
            // Her whole hull at her goal, lying along its way, with the room her line wants there (all of it: twice her half-beam and
            // her spare at each of her hull's points).
            string fit = "";
            if (goal != null && !double.IsNaN(goal.Heading))
            {
                double hg = goal.Heading * Math.PI / 180, full = 2 * halfBeam + spare;
                var refusals = new List<string>();
                foreach (double a in axis)
                    Water(goal.At.x + a * Math.Sin(hg), goal.At.z + a * Math.Cos(hg), full, w => refusals.Add("her point " + a.ToString("F1") + " m along her: " + w));
                fit = "; her hull at her goal along its way, " + full.ToString("F1") + " m of room at each point: " + (refusals.Count == 0 ? "fits" : "doesn't fit (" + string.Join("; ", refusals.ToArray()) + ")");
            }
            return nodes.Count + " states; nearest her goal by water (" + m.X.ToString("F0") + ", " + m.Z.ToString("F0") + ") heading " + m.H.ToString("F0") + ", " +
                least.ToString("F0") + " m by water from it, " + m.Travelled.ToString("F0") + " m sailed" +
                (goal == null ? "" : "; " + close + " states within 30 m of her goal" + (close > 0 ? ", their headings " + lowH.ToString("F0") + " to " + highH.ToString("F0") +
                " off her goal's way (the nearest " + bestOff.ToString("F0") + ", " + goal.Tolerance.ToString("F0") + " allowed)" : "")) + fit;
        }

        // Host diagnostics (the harbour check's --why): her start, and each of her five first moves and why it was refused if it was.
        internal List<string> ExplainStart()
        {
            var report = new List<string>();
            if (nodes.Count == 0) { report.Add("no start: " + Why); return report; }
            var n = nodes[0];
            int cell = chart.IndexOf(new RoutePoint((float)n.X, (float)n.Z));
            report.Add("her start (" + n.X.ToString("F1") + ", " + n.Z.ToString("F1") + ") heading " + n.H.ToString("F0") + ": its cell " +
                (cell < 0 ? "off her chart" : (chart.Water(cell) ? "water" : "one her hull doesn't fit") + ", " + chart.ClearanceAt(cell).ToString("F0") + " m clear, " +
                chart.DistanceAt(cell).ToString("F0") + " m by water to the open sea") + "; her room there " + startHave.ToString("F1") + " m; the wind from " +
                (windKnown ? windFrom.ToString("F0") + ", her beat " + beat.ToString("F0") + ", her heading " + Math.Abs(NavigationMath.Delta(windFrom, n.H)).ToString("F0") +
                " off it, " + tackRoom.ToString("F0") + " m of water to tack in" : "not counted") + "; her circle " + radiusToward.ToString("F0") + " m toward the wind, " +
                radiusAway.ToString("F0") + " away; her spare " + spare.ToString("F1") + " m");
            string[] names = { "straight", "her circle to starboard", "her circle to port", "twice her circle to starboard", "twice her circle to port" };
            for (int move = 0; move < 5; move++)
            {
                int turn = move == 0 ? 0 : move % 2 == 1 ? 1 : -1;
                double radius = 0;
                if (turn != 0)
                {
                    double rel = windKnown ? NavigationMath.Delta(n.H, windFrom) : 0;
                    radius = (windKnown && Math.Sign(rel) == turn ? radiusToward : radiusAway) * (move == 1 || move == 2 ? 1 : 2);
                }
                var why = new List<string>();
                if (!Move(n, turn, radius, spare, out var next, out _, w => why.Add(w))) { report.Add(names[move] + ": refused, " + string.Join("; ", why.ToArray())); continue; }
                int to = chart.IndexOf(new RoutePoint((float)next.X, (float)next.Z));
                double distance = to < 0 ? 0 : chart.DistanceAt(chart.Start(new RoutePoint((float)next.X, (float)next.Z)));
                report.Add(names[move] + ": " + (NavigationMath.Finite(distance) ? "taken, to (" + next.X.ToString("F1") + ", " + next.Z.ToString("F1") + ") heading " + next.H.ToString("F0")
                    : "dropped: her chart's flood doesn't reach where it ends"));
            }
            return report;
        }

        // Water for a point of her hull's centre line at (x, z) with `want` metres of room (her beam twice and her spare; none: its cell
        // alone): its chart cell fits her beam, and the middle of every cell that doesn't lies `want` from it. Beyond the chart's edge,
        // the open sea (her way out). The cell's own clearance bounds the distance (less or more the point's offset from the cell's
        // middle); only between the bounds are the cells round it measured.
        // A clearance is measured along 8-neighbour steps (WaterField's flood), which read up to OctileMost times the straight distance
        // (22.5 degrees off the grid's lines): divided by it, a true lower bound (Claude's review, 2026-10-12: taken as one, it let her
        // line pass up to 8% of her room, about a metre for the Brig, inside her spare).
        private static readonly double OctileMost = Math.Sqrt(4 - 2 * Math.Sqrt(2));
        private bool Water(double x, double z, double want, Action<string> refused = null)
        {
            var p = new RoutePoint((float)x, (float)z);
            int cell = chart.IndexOf(p);
            if (cell < 0) { if (goal != null) refused?.Invoke("off her chart on her way in"); return goal == null; }
            if (!chart.Water(cell)) { refused?.Invoke("at (" + x.ToString("F1") + ", " + z.ToString("F1") + ") a cell her hull doesn't fit"); return false; }
            if (want <= 0) return true;
            double clearance = chart.ClearanceAt(cell), off = RoutePoint.Distance(p, chart.Centre(cell));
            if (clearance / OctileMost - off >= want) return true;
            if (clearance + off < want) { refused?.Invoke("at (" + x.ToString("F1") + ", " + z.ToString("F1") + ") only " + (clearance + off).ToString("F1") + " m clear"); return false; }
            int reach = (int)Math.Ceiling(want / chart.Cell) + 1, x0 = cell % chart.Width, z0 = cell / chart.Width;
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int cx = x0 + dx, cz = z0 + dz;
                    if (cx < 0 || cz < 0 || cx >= chart.Width || cz >= chart.Height) continue;
                    int j = cz * chart.Width + cx;
                    if (!chart.Water(j) && RoutePoint.Distance(p, chart.Centre(j)) < want)
                    {
                        refused?.Invoke("at (" + x.ToString("F1") + ", " + z.ToString("F1") + ") a cell her hull doesn't fit " + RoutePoint.Distance(p, chart.Centre(j)).ToString("F1") + " m off");
                        return false;
                    }
                }
            return true;
        }

        private void Add(Node n)
        {
            long key = Key(n.X, n.Z, n.H);
            if (expanded.Contains(key) && !ReopenUsed || closed.TryGetValue(key, out int seen) && nodes[seen].G <= n.G) return;
            int cell = chart.IndexOf(new RoutePoint((float)n.X, (float)n.Z));
            double h = cell < 0 ? 0 : chart.DistanceAt(chart.Start(new RoutePoint((float)n.X, (float)n.Z)));
            if (!NavigationMath.Finite(h)) return;
            nodes.Add(n);
            closed[key] = nodes.Count - 1;
            // Decision 201 (c): the distance to go weighted.
            open.Add(n.G + WeightUsed * h, nodes.Count - 1);
        }

        private long Key(double x, double z, double h)
        {
            long ix = (long)Math.Floor((x - chart.OriginX) / PositionCell) + 4096, iz = (long)Math.Floor((z - chart.OriginZ) / PositionCell) + 4096;
            long ih = (long)Math.Floor(NavigationMath.Normalize(h) / headingBin) % headingBins;
            return (ih * 16384 + iz) * 16384 + ix;
        }

        // Her line from her start to the node reached: every SampleStep metres along the moves.
        private void Finish(int index)
        {
            var chain = new List<Node>();
            for (int i = index; i >= 0; i = nodes[i].Parent) chain.Add(nodes[i]);
            chain.Reverse();
            var line = new List<LinePoint>();
            var first = chain[0];
            line.Add(Point(first.X, first.Z, first.H, 0, 0));
            for (int c = 1; c < chain.Count; c++)
            {
                var a = chain[c - 1]; var b = chain[c];
                int samples = Math.Max(1, (int)Math.Round(b.Step / SampleStep));
                double h0 = a.H * Math.PI / 180;
                double radius = b.Turn == 0 ? 0 : Math.Abs(b.Step / (NavigationMath.Delta(a.H, b.H) * Math.PI / 180));
                for (int s = 1; s <= samples; s++)
                {
                    double d = b.Step * s / samples, x, z, h;
                    if (b.Turn == 0 || !(radius > 0) || double.IsInfinity(radius)) { x = a.X + d * Math.Sin(h0); z = a.Z + d * Math.Cos(h0); h = h0; }
                    else { double k = b.Turn / radius; h = h0 + k * d; x = a.X + (Math.Cos(h0) - Math.Cos(h)) / k; z = a.Z + (Math.Sin(h) - Math.Sin(h0)) / k; }
                    line.Add(Point(x, z, h * 180 / Math.PI, a.Travelled + d, b.Turn));
                }
            }
            Line = line;
            Cost = nodes[index].G;
            Done = true;
        }

        private LinePoint Point(double x, double z, double h, double along, int turn)
        {
            int cell = chart.IndexOf(new RoutePoint((float)x, (float)z));
            return new LinePoint { X = (float)x, Z = (float)z, Heading = NavigationMath.Normalize(h), Along = along, Clearance = cell < 0 ? double.PositiveInfinity : chart.ClearanceAt(cell), Turn = turn };
        }

        // The open states, least first (key, then node on a tie).
        private sealed class Heap
        {
            private double[] keys = new double[1024]; private int[] items = new int[1024]; private int count;
            internal int Count => count;
            private bool Less(int a, int b) => keys[a] < keys[b] || (keys[a] == keys[b] && items[a] < items[b]);
            private void Swap(int a, int b) { double k = keys[a]; keys[a] = keys[b]; keys[b] = k; int t = items[a]; items[a] = items[b]; items[b] = t; }
            internal void Add(double key, int item)
            {
                if (count == keys.Length) { Array.Resize(ref keys, count * 2); Array.Resize(ref items, count * 2); }
                keys[count] = key; items[count] = item;
                for (int i = count++; i > 0;) { int up = (i - 1) / 2; if (!Less(i, up)) break; Swap(i, up); i = up; }
            }
            internal void Pop(out double key, out int item)
            {
                key = keys[0]; item = items[0];
                if (--count == 0) return;
                keys[0] = keys[count]; items[0] = items[count];
                for (int i = 0; ;)
                {
                    int l = 2 * i + 1, r = l + 1, least = i;
                    if (l < count && Less(l, least)) least = l;
                    if (r < count && Less(r, least)) least = r;
                    if (least == i) break;
                    Swap(i, least); i = least;
                }
            }
        }
    }

    // ==== NavigationMath ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Engine-independent arithmetic shared with the executable tests.
    public static class NavigationMath
    {
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
        public static double Normalize(double angle) => (angle % 360 + 360) % 360;
        public static double Delta(double from, double to)
        {
            var d = Normalize(to - from);
            return d > 180 ? d - 360 : d; // deterministic starboard choice at exactly 180
        }
        public static double Heading(double x, double z) => Normalize(Math.Atan2(x, z) * 180 / Math.PI);
        public static double Move(double from, double to, double step) => from + Clamp(to - from, -step, step);
        public static double Vmg(double vx, double vz, double dx, double dz)
        {
            var distance = Math.Sqrt(dx * dx + dz * dz);
            return distance < 0.001 ? 0 : (vx * dx + vz * dz) / distance;
        }
        public static double ReefLength(double deployment, bool reversed)
        {
            if (!Finite(deployment)) throw new ArgumentException("Deployment must be finite.", nameof(deployment));
            return reversed ? 1 - Clamp(deployment, 0, 1) : Clamp(deployment, 0, 1);
        }
        public static double InverseLerp(double min, double max, double value)
        {
            if (!Finite(min) || !Finite(max) || !Finite(value)) throw new ArgumentException("InverseLerp inputs must be finite.");
            if (Math.Abs(max - min) < 0.001) throw new ArgumentException("Degenerate sail-control limits.");
            return Clamp((value - min) / (max - min), 0, 1);
        }
        // Whether the segment from a to b passes within `radius` of p (the prototype's sweep, kept from its background stepper).
        public static bool SegmentEntersRadius(double ax, double az, double bx, double bz, double px, double pz, double radius)
        {
            double dx = bx - ax, dz = bz - az, length2 = dx * dx + dz * dz;
            double t = length2 < 1e-12 ? 0 : Clamp(((px - ax) * dx + (pz - az) * dz) / length2, 0, 1);
            double x = ax + dx * t - px, z = az + dz * t - pz;
            return x * x + z * z <= radius * radius;
        }
    }

    // Decision 179 (the user, 2026-10-11 night: "the way you control rudder have problem, you only steer full left or full right, there
    // is no middle ground"): she is steered by her turn rate. The rate she wants is TurnRateGain a degree of her heading's error, at
    // most `mostRate` (TurnRateMost; more while she keeps clear of something); her helm is RateGain for each degree a second she lacks
    // of it (the helm eases as she turns, and checks a turn running past it), and a slow hold (HoldGain, within HoldBand of her course,
    // at most HoldMost) carries a steady weather helm without a standing error. It slews at Slew a second. (It was -0.025 a degree of
    // error and 0.08 a degree a second of turn: hard over beyond 40 degrees off or 12.5 degrees a second, and keeping clear forced
    // it hard over.)
    public sealed class HeadingController
    {
        public const double TurnRateGain = 0.4, TurnRateMost = 6, RateGain = 0.15, HoldGain = 0.004, HoldBand = 15, HoldMost = 0.5, HoldDecay = 0.1, Slew = 0.5;
        // Decision 184 (a): "the helm hard over" for decision 178's reef against a weather helm (Voyage.Steer): the helm this law gives
        // while her bow turns under 1 degree a second toward a course TurnRateMost / TurnRateGain degrees or more off, 0.75. With her bow
        // standing still it gives 0.9: the old law's hard over, read as 0.95, never came, and the reef never fired.
        public const double HardOver = RateGain * (TurnRateMost - 1);
        // The hold builds only while her heading is steady (under HoldSteadyRate degrees a second: a weather helm shows only then), and
        // a hold pushing her away from her course unwinds at HoldUnwind a second besides (run 20261012-001522 on FG: the Kakam's turn
        // up to close-hauled at 3-7 degrees a second built a hold of about +0.13 within HoldBand; she overshot 3 degrees into her
        // dead zone and that hold cancelled most of the helm bearing her off (-0.05 for 0.18), and she stalled from 2.1 to 0.8 m/s).
        public const double HoldSteadyRate = 1, HoldUnwind = 0.2;
        private double last, hold;
        public void Reset() { last = 0; hold = 0; FullRateSide = 0; }
        // Decision 225 (5): taken back from where her wheel lies (`helm`, -1 to 1 in this controller's sense), not from the middle,
        // which left her almost no helm for up to 2 s at its slew.
        public void Reset(double helm) { Reset(); if (NavigationMath.Finite(helm)) last = NavigationMath.Clamp(helm, -1, 1); }
        // Decision 225 (10): the side (+1 starboard, -1 port) she was last asked to turn at the most rate she may (mostRate), else 0:
        // her turning circle is sampled then (TurnCircle), not by the helm's size.
        public int FullRateSide { get; private set; }
        // Vanilla Rudder applies negative yaw for positive rudder angle while moving forward: a negative demand turns her to starboard.
        public double Tick(double heading, double target, double yawDegreesPerSecond, double dt, double mostRate = TurnRateMost)
        {
            if (!NavigationMath.Finite(heading) || !NavigationMath.Finite(target) ||
                !NavigationMath.Finite(yawDegreesPerSecond) || !NavigationMath.Finite(dt) || dt <= 0 || !NavigationMath.Finite(mostRate) || mostRate <= 0)
                throw new ArgumentException("Invalid heading sample.");
            double error = NavigationMath.Delta(heading, target);
            double wanted = NavigationMath.Clamp(TurnRateGain * error, -mostRate, mostRate);
            FullRateSide = Math.Abs(TurnRateGain * error) >= mostRate ? Math.Sign(error) : 0;
            if (Math.Abs(error) >= HoldBand) hold = NavigationMath.Move(hold, 0, HoldDecay * dt);
            else
            {
                if (Math.Abs(yawDegreesPerSecond) < HoldSteadyRate) hold = NavigationMath.Clamp(hold - HoldGain * error * dt, -HoldMost, HoldMost);
                // Her correction is -error's way: a hold of error's sign pushes her off her course.
                if (hold * error > 0) hold = NavigationMath.Move(hold, 0, HoldUnwind * dt);
            }
            var demand = NavigationMath.Clamp(-RateGain * (wanted - yawDegreesPerSecond) + hold, -1, 1);
            last = NavigationMath.Move(last, demand, Slew * dt);
            return last;
        }
    }

    // ==== ReleaseSpots ====

    // Decision 203 (the user, 2026-10-13: "but all arrive should arrive at shipyard position, only departure is at berth"; then "release
    // spot, wait for half hour and player, tow in from channel mouth"): arriving, she sails to her destination shipyard's release spot
    // (Shipyard.shipReleasePosition, where the game puts every ship leaving the shipyard), is held there, waits half a game hour and until
    // the player is off her deck (D5), then goes into storage. The service berth is for departures only. Kicia Bay's release spot lies in its
    // channel (8-16 m wide on the 8 m chart, not joined to the sea there): she sails to a tow start outside it and is towed in. Engine-free,
    // so the table and the geometry are host-tested; Voyage (VoyageDocking.cs) reads the spot from the live shipyard.

    // Where Kicia Bay's tow takes her from: a fixed point in the release spot's axes (x to starboard, z ahead, as the port surveys
    // give them), and the heading she lies at there, in degrees from the release spot's own (180: her bow back toward the spot).
    internal sealed class TowStart
    {
        internal readonly string Name;
        internal readonly double X, Z, Heading;
        internal TowStart(string name, double x, double z, double heading) { Name = name; X = x; Z = z; Heading = heading; }
    }

    internal static class ReleaseSpots
    {
        // Her wait at the release spot (the user's half hour): from when she lies held there, settled, to her storage; arrived on paper
        // far from the player (D4), the same (the user, 2026-10-13: "6 ok").
        internal const double StoreAfterHours = 0.5;
        // D3 at the release spot (the user's 3b, 2026-10-13): another boat on it (decision 107's footprint), she holds off; still taken
        // this long after, she goes into storage from where she holds, the player off her deck (D5). It was an hour at the berth.
        internal const double TakenHours = 0.5;
        // Held at the spot (the user's 1a): her final approach ends within the docking's 10 m and 20 degrees, then the booking's tow draws
        // her bow and stern onto their places there (VoyageTowIn's draw) and holds her until her storage. Settled: within HeldAcross of
        // the spot's line, HeldAlong of her place along it and HeldAlign of its heading, under HeldSpeed, for HeldSettleSeconds.
        internal const double HeldAcross = 1.5, HeldAlong = 4, HeldAlign = 10, HeldSpeed = 0.3, HeldSettleSeconds = 30;
        // Held only while she lies within this of the spot (at her destination; a ship moored farther off is left alone).
        internal const double HeldWithin = 20;

        // Kicia Bay's tow start (the user, 2026-10-13: "can it be closer? like 50m west then start towing?"; "run check for both points"),
        // chosen on the 8 m charts of run 20261012-203136 by the host test's scan (--tow-scan: every water cell within 130 m of the spot, her
        // bow 30 to 105, all seven hulls, 16 winds at high and low water; her way in found to her approach point, her hull with her half-beam
        // and 1.5 m of room where she stops and on her straight final run there). The two points first checked: the pocket where the
        // channel opens out, 50 m along it from the spot, bow toward the channel (112), and the deep water at the south-west entrance, about
        // 150 m out, bow 69: the small hulls in 8-13 and 14-16 winds of 16, no big hull at either (the pocket too small for a big hull to
        // stop in; head winds up the inlet's narrow head). No point within 110 m served every hull; the nearest that did (every hull, every
        // wind, both waters): the waterway west of the town where it opens to the inlet, about 123 m from the spot, bow 60 (east-north-east);
        // the tow takes her north up the waterway, through the pocket and along the channel, about 170 m.
        internal const string KiciaBay = "Kicia Bay";
        internal static readonly TowStart KiciaTowStart = new TowStart("the waterway west of the town where it opens to the inlet, about 123 m out", -118.6, 31.9, 128);
        // The user's 50 m point: the pocket where the channel opens out, on the spot's own axis.
        internal static readonly TowStart KiciaPocket = new TowStart("the pocket at the channel's mouth, 50 m along the channel", 0, 50, 180);

        // Decision 207 (the user, 2026-10-13: "do actual sailing test before we decide, try for sailing into shipyard release spot
        // directly, if that cannot be achieve, than fall back to 30m -> 50m ->100m towing"): Kicia Bay's arrival is decided by sailing it
        // in game. KiciaArrival: "sail" (straight in to the spot under sail, no tow start: her approach point on the spot's axis, at the
        // pocket where the channel opens, and her final approach up the channel), "30" (a tow start in the channel 30 m out, on the
        // spot's axis), "50" (the pocket), "100" (Kicia100, in the waterway), or "123" (KiciaTowStart, the host check's pick, decision 203
        // (4)), until the user decides; the in-game test sets it (the QA's -shipwrightKicia). Claude's points for 30 and 100: 30 on the
        // spot's axis as the pocket is; 100 the best cell 95-112 m from the spot in the host test's scan (--tow-scan --step 8 --within 112
        // --winds 4, 2026-10-13: her bow toward the pocket; the Cog 3, the Dhow, the Junk and the Kakam 4, the Brig 2 of 4 winds, the
        // Baghlah and the Sanbuq none).
        internal static string KiciaArrival = "123";
        internal static readonly TowStart Kicia30 = new TowStart("the channel, 30 m from the spot", 0, 30, 180);
        internal static readonly TowStart Kicia100 = new TowStart("the waterway west of the pocket, about 105 m from the spot", -91.6, 51.9, 91);

        // Decision 220 (the user, 2026-11-03, asked how a ship booked from Kicia Bay gets out of its shipyard channel: "Tow her out
        // (Recommended)"): the arrival's tow (213) in reverse, from the shipyard's cleats (217) down the channel to its tow start, where her
        // sailor sets sail. Always the tow start 123 m out, whatever 207's arrival ladder says; there her bow points the other way from
        // the arrival's lie, away from the channel. Null at every other port.
        internal static TowStart TowOutFor(string port) => port == KiciaBay ? KiciaTowStart : null;

        // Decision 222 (the user, 2026-11-03: "C. Wait, but wait at the part that can be anchored and make it anchor there. And only wait
        // until the chart is usable"; the picks "* Kicia bay: 1 * DC: only 1 * GRC: 2 and 4 * FA: 3 * Chronos: 1"): arriving, she anchors at
        // her destination's anchor spot until her harbour's chart is made. The spots (real metres, SAILING_MOVE_REVIEW.md "Picked": the
        // anchor's bottom 10-28 m below mean sea, each within 1,500 m of its island's centre) in each release spot's axes (x to starboard, z
        // ahead; from the -Part Depths runs' spots), read from the live spot as her arrival is (ScrambledSeas moves islands). Gold Rock
        // City has two, her mouth's side and the other; she takes the one nearer her (Claude's reading of "the one nearer her along her
        // route"). Null at a port without one.
        internal static readonly (string Port, double X, double Z)[] AnchorSpots =
        {
            ("Kicia Bay", -232.7, 236.3), ("Dragon Cliffs", 625.2, 683.0), ("Gold Rock City", 129.4, 1227.0), ("Gold Rock City", 669.7, -1104.6),
            ("Fort Aestrin", -193.6, 350.8), ("Chronos", -201.4, -119.9),
        };

        internal static RoutePoint? AnchorFor(string port, RoutePoint spot, double yaw, RoutePoint from)
        {
            RoutePoint? best = null;
            foreach (var a in AnchorSpots)
            {
                if (a.Port != port) continue;
                var at = InAxes(spot, yaw, a.X, a.Z);
                if (best == null || RoutePoint.Distance(at, from) < RoutePoint.Distance(best.Value, from)) best = at;
            }
            return best;
        }
        internal static double TowOutHeading(TowStart start) => NavigationMath.Normalize(start.Heading + 180);

        // Her tow start at this port, or null where she sails all the way to the spot. Every hull from the same one (the scan: all seven
        // hulls there in every wind); `storedLength` kept for a split by size, should a test call for one.
        internal static TowStart TowStartFor(string port, double storedLength) => port != KiciaBay || KiciaArrival == "sail" ? null :
            KiciaArrival == "30" ? Kicia30 : KiciaArrival == "50" ? KiciaPocket : KiciaArrival == "100" ? Kicia100 : KiciaTowStart;

        // A point given in a release spot's axes (x to starboard, z ahead of its heading `yaw`).
        internal static RoutePoint InAxes(RoutePoint spot, double yaw, double x, double z)
        {
            double r = yaw * Math.PI / 180, fx = Math.Sin(r), fz = Math.Cos(r);
            return new RoutePoint((float)(spot.x + x * fz + z * fx), (float)(spot.z - x * fx + z * fz));
        }

        // How she lies held at the spot (decision 143: either way round): its heading or the opposite one, whichever lies nearer hers.
        internal static double LieHeading(double yaw, double heading) =>
            Math.Abs(NavigationMath.Delta(heading, yaw)) <= 90 ? NavigationMath.Normalize(yaw) : NavigationMath.Normalize(yaw + 180);

        // Whether she lies held there (settled, the draw's reach): `p` her middle, `place` hers at the spot, `lie` its heading.
        internal static bool Held(RoutePoint p, RoutePoint place, double lie, double heading)
        {
            double dx = p.x - place.x, dz = p.z - place.z, r = lie * Math.PI / 180;
            double along = dx * Math.Sin(r) + dz * Math.Cos(r), across = dx * Math.Cos(r) - dz * Math.Sin(r);
            return Math.Abs(across) <= HeldAcross && Math.Abs(along) <= HeldAlong && Math.Abs(NavigationMath.Delta(heading, lie)) <= HeldAlign;
        }
    }

    // Decision 222: the game's island height (IslandHorizon.ApplyNewHorizon, read in the 0.40 decompile; measured to the centimetre at
    // 16 points off Gold Rock City, SAILING_MOVE_REVIEW.md "The island's rise, measured"): how far an island stands below its true
    // height (+ low, - high) for the player `distance` metres from its centre (IslandHorizon.currentDistance), the player's eye `eyeY`
    // (Refs.ovrCameraRig's world height), its fullyEmergedDistance `emerged` and maxDrop (the game's -20: raised at most 20 m).
    internal static class IslandHeight
    {
        internal const double PlanetRadius = 515662;   // the game's
        internal static double Drop(double distance, double eyeY, double emerged, double maxDrop = -20)
        {
            if (!NavigationMath.Finite(distance) || !NavigationMath.Finite(eyeY) || !(emerged > 0)) return 0;
            double s = NavigationMath.Clamp((distance - emerged) / (1.25 * emerged), 0, 1);
            return Math.Max(s * (distance * distance / (2 * PlanetRadius) - 2 * eyeY), maxDrop);
        }

        // Decision 238: the island's low seabed is on for her chart: the game switches it off at its load distance (IslandPerformanceSwitcher.Update,
        // these same two values). The margin: on for some frames by then with the player coming in (Claude's number; a ray in the very frame it comes on isn't known to see it).
        internal const double SeabedOnMargin = 20;
        internal static bool SeabedOn(double distance, double load) => distance < load - SeabedOnMargin;
    }

    // ==== RigIdentity ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public static class RigIdentity
    {
        // How far a sail's area, angle limits and scale may differ from her booking's and still be the same rig: 0.1%. A reload
        // recomputes sail area from Unity transforms, and float rounding through her hierarchy's scales moved the Cog's jib 1.4
        // parts per million (run 20261010-020912: 30.9344082 booked, 30.9344521 after the load), past the old one part per million,
        // so her voyage was never taken in service again (the review of 2026-10-10). 0.1% is far above that noise and well under
        // any real change of sail or scale.
        public const double RelativeTolerance = 0.001;

        public static bool Matches(string saved, string current)
        {
            if (string.IsNullOrEmpty(saved) || string.IsNullOrEmpty(current)) return false;
            var a=saved.Split('|'); var b=current.Split('|');
            if(a.Length!=b.Length) return false;
            for(int i=0;i<a.Length;i++)
            {
                var left=a[i].Split(':'); var right=b[i].Split(':');
                if(left.Length<6 || left.Length!=right.Length) return false;
                int numeric=left.Length-5;
                // Hierarchy identity and square/fore-and-aft type remain exact.
                for(int p=0;p<numeric;p++) if(left[p]!=right[p]) return false;
                if(left[numeric+3]!=right[numeric+3]) return false;
                for(int n=0;n<3;n++) if(!Near(left[numeric+n],right[numeric+n])) return false;
                var ls=left[numeric+4].Split(','); var rs=right[numeric+4].Split(',');
                if(ls.Length!=3 || rs.Length!=3) return false;
                for(int n=0;n<3;n++) if(!Near(ls[n],rs[n])) return false;
            }
            return true;
        }
        private static bool Near(string a,string b)
        {
            double x,y;
            if(!double.TryParse(a,NumberStyles.Float,CultureInfo.InvariantCulture,out x) ||
                !double.TryParse(b,NumberStyles.Float,CultureInfo.InvariantCulture,out y) ||
                !NavigationMath.Finite(x) || !NavigationMath.Finite(y)) return false;
            return Math.Abs(x-y)<=Math.Max(0.00001,Math.Max(Math.Abs(x),Math.Abs(y))*RelativeTolerance);
        }
    }

    // ==== RoutePlanner ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    [Serializable]
    public struct RoutePoint
    {
        public float x, z;
        public RoutePoint(float x, float z) { this.x = x; this.z = z; }
        public static double Distance(RoutePoint a, RoutePoint b) => Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.z-b.z)*(a.z-b.z));
    }

    // Bounded A*: the caller supplies swept clearance, including depth and unknown map areas.
    // Coordinates are stable world coordinates, never render/origin-shift coordinates.
    public static class RoutePlanner
    {
        public const double MaximumVoyageDistance=500000;
        public static RoutePoint[] PlanDeparture(RoutePoint start, RoutePoint goal, RoutePoint bowLead,
            Func<RoutePoint, RoutePoint, bool> clear, double windFrom, double noGo)
        {
            var direct = Plan(start, goal, clear, windFrom, noGo);
            if (direct.Length >= 2) return direct;
            // A narrow berth can have a clear bow exit between the eight grid
            // directions. Seed the open-water search with that checked exit.
            if (!Finite(bowLead) || RoutePoint.Distance(start, bowLead) < 1 ||
                RoutePoint.Distance(bowLead, goal) > MaximumVoyageDistance || !clear(start, bowLead)) return direct;
            var onward = Plan(bowLead, goal, clear, windFrom, noGo);
            if (onward.Length < 2) return direct;
            var route = new RoutePoint[onward.Length + 1]; route[0] = start;
            Array.Copy(onward, 0, route, 1, onward.Length);
            return route;
        }
        // Review M4: Plan's grid step is a 65th of the way (~500 m from Dragon Cliffs to Kicia Bay), so a harbour whose way out
        // turns within one step has none on it. Then the way out is planned on a fine grid to an offing `offing` metres off
        // (toward the goal first, then up to 90 degrees either side), and the open sea from there; the two are joined.
        public static RoutePoint[] PlanDepartureStaged(RoutePoint start, RoutePoint goal, RoutePoint bowLead,
            Func<RoutePoint, RoutePoint, bool> clear, double windFrom, double noGo, double offing = 1200)
        {
            var direct = PlanDeparture(start, goal, bowLead, clear, windFrom, noGo);
            if (direct.Length >= 2 || !Finite(start) || !Finite(goal) || RoutePoint.Distance(start, goal) <= offing * 1.5) return direct;
            double toGoal = NavigationMath.Heading(goal.x - start.x, goal.z - start.z);
            foreach (double turn in new double[] { 0, 30, -30, 60, -60, 90, -90 })
            {
                double r = (toGoal + turn) * Math.PI / 180;
                var outside = new RoutePoint((float)(start.x + Math.Sin(r) * offing), (float)(start.z + Math.Cos(r) * offing));
                if (!clear(outside, outside)) continue;
                var harbour = PlanDeparture(start, outside, bowLead, clear, windFrom, noGo);
                if (harbour.Length < 2) continue;
                var sea = Plan(outside, goal, clear, windFrom, noGo);
                if (sea.Length < 2) continue;
                var joined = new List<RoutePoint>(harbour);
                for (int i = 1; i < sea.Length; i++)
                    if (RoutePoint.Distance(joined[joined.Count - 1], sea[i]) >= 0.01) joined.Add(sea[i]);
                if (joined.Count > 256) continue;
                return joined.ToArray();
            }
            return direct;
        }

        // minStep: the grid's least step (20 m; the approach to a berth plans on a finer one, 8 m: run 20261011-035040 at Gold Rock
        // City, "no route to her approach point 199 m off", the water by the berth narrower than a 20 m grid sees).
        public static RoutePoint[] Plan(RoutePoint start, RoutePoint goal, Func<RoutePoint, RoutePoint, bool> clear,
            double windFrom, double noGo, int budget = 6000, double minStep = 20)
        {
            if (!Finite(start) || !Finite(goal) || clear == null || !NavigationMath.Finite(windFrom) ||
                !NavigationMath.Finite(noGo) || noGo < 0 || noGo >= 90 || budget < 1 || !(minStep > 0))
                throw new ArgumentException("Invalid route request.");
            if (RoutePoint.Distance(start, goal) > MaximumVoyageDistance) throw new ArgumentException("Route exceeds the 500 km voyage limit.");
            if (clear(start, goal)) return new[] { start, goal };
            double step = Math.Max(minStep, RoutePoint.Distance(start, goal) / 65);
            int minX = (int)Math.Floor(Math.Min(0, (goal.x-start.x)/step))-12;
            int minZ = (int)Math.Floor(Math.Min(0, (goal.z-start.z)/step))-12;
            int maxX = (int)Math.Ceiling(Math.Max(0, (goal.x-start.x)/step))+12;
            int maxZ = (int)Math.Ceiling(Math.Max(0, (goal.z-start.z)/step))+12;
            int width = maxX-minX+1, height = maxZ-minZ+1;
            Func<int, RoutePoint> point = i => new RoutePoint((float)(start.x+(i%width+minX)*step), (float)(start.z+(i/width+minZ)*step));
            int initial = -minZ*width-minX;
            var costs = new double[width*height]; var previous = new int[costs.Length]; var closed = new bool[costs.Length];
            for (int i=0; i<costs.Length; ++i) { costs[i]=double.PositiveInfinity; previous[i]=-1; }
            var open = new SortedSet<Entry>(Comparer<Entry>.Create((a,b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Id.CompareTo(b.Id)));
            costs[initial]=0; open.Add(new Entry(initial, RoutePoint.Distance(start,goal)));
            int found=-1, visited=0;
            while (open.Count>0 && visited++<budget)
            {
                var next=open.Min; open.Remove(next); int current=next.Id;
                if (closed[current]) continue;
                closed[current]=true; var p=point(current);
                if (RoutePoint.Distance(p,goal)<=step*1.5 && clear(p,goal)) { found=current; break; }
                for (int dz=-1; dz<=1; ++dz) for (int dx=-1; dx<=1; ++dx)
                {
                    if (dx==0 && dz==0) continue;
                    int x=current%width+dx, z=current/width+dz;
                    if (x<0 || x>=width || z<0 || z>=height) continue;
                    int id=z*width+x;
                    if (closed[id]) continue;
                    var q=point(id); double gap=Math.Abs(NavigationMath.Delta(windFrom,NavigationMath.Heading(q.x-p.x,q.z-p.z)));
                    double cost=costs[current]+RoutePoint.Distance(p,q)*(gap<noGo ? 2.5 : 1);
                    if (cost>=costs[id] || !clear(p,q)) continue;
                    costs[id]=cost; previous[id]=current;
                    open.Add(new Entry(id,cost+RoutePoint.Distance(q,goal)));
                }
            }
            if (found<0) return Array.Empty<RoutePoint>();
            var path=new List<RoutePoint> { goal };
            for (int i=found; i>=0; i=previous[i])
                if (RoutePoint.Distance(path[path.Count-1],point(i))>=0.01) path.Add(point(i));
            path.Reverse();
            // Visibility smoothing keeps the exact tested segments, rather than cutting grid corners.
            var smooth=new List<RoutePoint> { start }; int index=0;
            while (index<path.Count-1)
            {
                int end=path.Count-1;
                while (end>index+1 && !clear(path[index],path[end])) --end;
                smooth.Add(path[end]); index=end;
            }
            return smooth.ToArray();
        }
        // Decision 125: a route out through an offing (planned to it, then on to the goal), for the routes she chooses between.
        public static RoutePoint[] PlanVia(RoutePoint start, RoutePoint offing, RoutePoint goal, Func<RoutePoint, RoutePoint, bool> clear,
            double windFrom, double noGo)
        {
            if (!clear(offing, offing)) return Array.Empty<RoutePoint>();
            var harbour = Plan(start, offing, clear, windFrom, noGo);
            if (harbour.Length < 2) return harbour;
            var sea = Plan(offing, goal, clear, windFrom, noGo);
            if (sea.Length < 2) return sea;
            var joined = new List<RoutePoint>(harbour);
            for (int i = 1; i < sea.Length; i++)
                if (RoutePoint.Distance(joined[joined.Count - 1], sea[i]) >= 0.01) joined.Add(sea[i]);
            return joined.Count > 256 ? Array.Empty<RoutePoint>() : joined.ToArray();
        }

        // Decision 125: how long a route would take with the wind now, in seconds at a reach's speed of 1 m/s (comparable between
        // routes, not a forecast). Over its first `near` metres each leg at the speed its angle to the wind gives
        // (TackNavigator.Polar); a leg within her no-go sailed as tacks (close-hauled, its way made good, and each tack's loss over
        // 300 m of beating); beyond, at a reach's speed (the wind out there will change).
        public static double SailingTime(RoutePoint[] route, double windFrom, double noGo, double near = 3000)
        {
            if (route == null || route.Length < 2 || !NavigationMath.Finite(windFrom) || !NavigationMath.Finite(noGo) || !(near >= 0))
                throw new ArgumentException("Invalid route time request.");
            double close = noGo + TackNavigator.CloseMargin, time = 0, done = 0;
            for (int i = 1; i < route.Length; i++)
            {
                double leg = RoutePoint.Distance(route[i - 1], route[i]);
                double scored = Math.Min(leg, Math.Max(0, near - done));
                time += leg - scored;
                if (scored > 0)
                {
                    double off = Math.Abs(NavigationMath.Delta(windFrom, NavigationMath.Heading(route[i].x - route[i - 1].x, route[i].z - route[i - 1].z)));
                    double speed = off >= close ? TackNavigator.Polar(off, noGo)
                        : TackNavigator.Polar(close, noGo) * Math.Cos((close - off) * Math.PI / 180);
                    time += scored / Math.Max(0.05, speed);
                    if (off < close) time += scored / 300 * 30;
                }
                done += leg;
            }
            return time;
        }

        public static bool Finite(RoutePoint p) => NavigationMath.Finite(p.x) && NavigationMath.Finite(p.z) && Math.Abs(p.x)<=2000000 && Math.Abs(p.z)<=2000000;
        private struct Entry { public int Id; public double Score; public Entry(int id,double score) { Id=id; Score=score; } }
    }

    // ==== Seabed ====

    // The seabed probe's cells (NavigationWater.SeabedClear; the user, 2026-10-11: "obstacle detection should include sea bed depth"),
    // engine-free and host-tested: a square grid `step` metres on a side in stable coordinates, and the cells a way passes over.

    public static class Seabed
    {
        public static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        public static RoutePoint Centre(long key, float step)
        {
            int x = (int)(key >> 32), z = unchecked((int)(uint)key);
            return new RoutePoint((x + 0.5f) * step, (z + 0.5f) * step);
        }

        public static double Distance(float ax, float az, float bx, float bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        // The cell a point lies in.
        public static long At(RoutePoint p, float step)
        {
            if (!(step > 0) || !RoutePlanner.Finite(p)) throw new ArgumentException("Invalid seabed point.");
            return Key((int)Math.Floor(p.x / step), (int)Math.Floor(p.z / step));
        }

        // Whether a point lies under her hull as she lies (her origin, heading, half-beam, and her length ahead of and astern of her
        // origin): she floats there, so the seabed there isn't probed (the user, 2026-10-11: it was a circle her length round her
        // middle, which also left out the water her bow swings over as she turns; the Cog's bow struck Dragon Cliffs' shoal).
        public static bool UnderHull(RoutePoint p, RoutePoint origin, double heading, double halfBeam, double fore, double aft)
        {
            double rad = heading * Math.PI / 180, fx = Math.Sin(rad), fz = Math.Cos(rad);
            double dx = p.x - origin.x, dz = p.z - origin.z;
            double ahead = dx * fx + dz * fz, side = dx * fz - dz * fx;
            return Math.Abs(side) <= halfBeam && ahead <= fore && ahead >= -aft;
        }

        // The cells under a way from `from` to `to`, sampled every half step along it (a diagonal way may step from a cell to the
        // one at its corner), each once, in order from `from`; both ends included.
        public static IEnumerable<long> Cells(RoutePoint from, RoutePoint to, float step)
        {
            if (!(step > 0) || !RoutePlanner.Finite(from) || !RoutePlanner.Finite(to)) throw new ArgumentException("Invalid seabed way.");
            double length = RoutePoint.Distance(from, to);
            int samples = Math.Max(1, (int)Math.Ceiling(length / (step * 0.5)));
            long last = long.MinValue;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                int x = (int)Math.Floor((from.x + (to.x - from.x) * t) / step), z = (int)Math.Floor((from.z + (to.z - from.z) * t) / step);
                long key = Key(x, z);
                if (key == last) continue;
                last = key;
                yield return key;
            }
        }
    }

    // ==== TackNavigator ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).
    // Reworked for decisions 126, 127 and 131 (the user, 2026-10-10; SAILING_TRANSPORT_DRAFT.md, "Sailing by the wind"): her route is a
    // guide, not a track. Her course is chosen by the wind she has toward a guide point on it, and chosen again only when the wind
    // has shifted or the choice no longer holds; any rig tacks once she has way on, and a wear turns by her actual heading.

    public enum Manoeuvre { Direct, CloseHauled, Tacking, Wearing, Blocked, Avoiding }

    public sealed class TackNavigator
    {
        // Her course is chosen again when the wind has shifted more than this since the last choice (the user, 2026-10-10: "only
        // reevaluate when wind change for more than 15 degree", a guideline to tune from the runs).
        public const double WindShift = 15;
        // Under this she sails lower to build speed (FootingMargin) and gathers way (decision 131).
        public const double MinTackSpeed = 2.0;
        // Decision 133, a tack's requirements (the user: "set up tacking requirement also, so you don't stuck in iron while
        // tacking"): she starts one with her sails drawing (SailsDrawing, set by the voyage) and with the speed to carry her across
        // the wind (CanTack); short of it she keeps her side and gathers way, and wears after TackWaitMost. Under TackAbortSpeed
        // (1.0 m/s, 2 knots) before her bow crosses the wind she bears away back onto her old tack and tries again; after
        // MaxTackFailures she wears. Under it once across, she bears away on her new side at once. Decision 168 (the user,
        // 2026-10-11 night: a tack needs no waiting time once her speed is enough): no 10 s on a steady heading first. SteadyRate
        // still tells a steady heading for decision 159's wind shift.
        public const double TackAbortSpeed = 1.03, SteadyRate = 2, TackWaitMost = 60;
        // Decision 161 (the user, 2026-10-11: "don't set minimum speed for tacking anymore, if you judge the speed can finish the
        // tack, you do it"; the fixed 2.6 m/s of decision 133 dropped): she tacks when her speed, less what she loses turning
        // (LossPerDegree for each degree of her turn to TackPastEye past the wind's eye), stays at TackAbortSpeed or more. The loss
        // starts at TackLossStart (run 20261011-140252 on DT: the Cog turned 127 degrees from 3.03 to 1.41 m/s) and is learned from
        // each of her tacks, at the wind's eye or where the tack is lost, within TackLossLeast and TackLossMost.
        public const double TackPastEye = 10, TackLossStart = 0.013, TackLossLeast = 0.004, TackLossMost = 0.05;
        public double LossPerDegree { get; private set; } = TackLossStart;
        private double tackStartSpeed, tackStartOff;
        private bool tackCrossed;

        // Her turn from `heading` across the wind's eye to TackPastEye beyond it.
        public static double TurnToCross(double heading, double windFrom) => Math.Abs(NavigationMath.Delta(windFrom, heading)) + TackPastEye;
        // The speed she needs on `heading` to finish a tack (decision 161).
        public double SpeedToTack(double heading, double windFrom) => TackAbortSpeed + LossPerDegree * TurnToCross(heading, windFrom);
        public bool CanTack(double speed, double heading, double windFrom) => speed >= SpeedToTack(heading, windFrom);

        // Her loss per degree learned from a tack: `turned` degrees (at least 5: a tack lost with hardly a turn counts as a steep
        // loss) from `startSpeed` to `speed` (half the old, half the new).
        private void LearnLoss(double startSpeed, double speed, double turned)
        {
            if (!NavigationMath.Finite(startSpeed) || !NavigationMath.Finite(speed) || !NavigationMath.Finite(turned)) return;
            turned = Math.Max(5, turned);
            double loss = Math.Max(0, startSpeed - speed) / turned;
            LossPerDegree = NavigationMath.Clamp(0.5 * LossPerDegree + 0.5 * loss, TackLossLeast, TackLossMost);
            LossLearned = loss.ToString("F3") + " m/s a degree over " + turned.ToString("F0") + " degrees (" + startSpeed.ToString("F1") + " to " +
                speed.ToString("F1") + " m/s); now " + LossPerDegree.ToString("F3");
        }
        // For the tick her loss was learned (the voyage logs it).
        public string LossLearned { get; private set; }
        // Decision 225 (2): what an earlier navigator of hers learned (a save, a hold, a return from the clock make a new one), kept.
        public void KeepLoss(double loss)
        {
            if (NavigationMath.Finite(loss)) LossPerDegree = NavigationMath.Clamp(loss, TackLossLeast, TackLossMost);
        }
        public const int MaxTackFailures = 2;
        public bool SailsDrawing { get; set; } = true;
        // Decision 134: no gathering way while she bears away against a weather helm (set by the voyage): a round-up that slowed her
        // isn't a heading to hold.
        public bool BearingAway { get; set; }
        // Decision 137 (the user, 2026-10-10 night: "dead wind angle + 7"): close-hauled is her no-go and CloseMargin (BeatOff); no
        // course closer to the wind than that is straight at a guide point, nor a heading she gathers way on. Nearing a stop (Footing, decision 166) she
        // sails up to FootingMargin further off the wind, so she builds speed before she points high (run 20261010-160852: back in
        // physics at 2.5 m/s, the Dhow pointed up at once and her lateen pinned her head to wind at 0.7 m/s).
        // Decision 177 (the user, 2026-10-11 night): at sea close-hauled is her dead zone and CloseMargin (was 7); in port, where her dead
        // zone is the angle at which her sails catch 0.5 of the wind, close-hauled is her dead zone itself (PortCloseMargin). Each
        // ship's own Margin, set by her voyage.
        public const double CloseMargin = 5, PortCloseMargin = 0, FootingMargin = 15;
        public double Margin { get; set; } = CloseMargin;
        // Decision 166 (the user, 2026-10-11: "could've sail more close to the wind while still be able to go forward"): she sails
        // FootingMargin further off only as she nears a stop, under FootingSpeed (full at none), not under MinTackSpeed (run
        // 20261011-163940 on EF: at 1.6-1.8 m/s she sailed 5 degrees lower than close-hauled, and her let-go helm 6-7 more).
        public const double FootingSpeed = 1.0;
        public static double Footing(double speed) => FootingMargin * NavigationMath.Clamp((FootingSpeed - speed) / FootingSpeed, 0, 1);
        // A course on the other side of the wind costs this (a share of a reach's speed): a turn loses way; a tack, across the wind's
        // eye, loses the most (decision 131: "you should tack as less as possible, because tacking is speed loss").
        public const double TurnCost = 0.15, TackCost = 0.30;
        // Decision 162 (a) as the user answered it (2026-10-11 night: "i never set this time limit (120s), as long as speed built on
        // enough for tacking, it can tack without waiting time"): no hold on her side after a turn; a tack's cost (TackCost) and her
        // speed to finish it (decision 161) decide.
        // Decision 167: a change of side whose turn round her stern is over this (across the wind at most 60 degrees more) is a tack
        // or a wear (BeginTurn), never a plain turn round her stern.
        public const double SternTurnMost = 150;
        // Decision 131, gathering way: under GatherSpeed with her sail drawing on her heading (the wind outside her no-go and
        // CloseMargin) and her way ahead clear, she holds that heading while her course waits; she turns to it once she has
        // GatherSpeed, her speed has not risen 0.1 m/s for GatherPlateau, or after GatherMost. She gathers way again only when, having
        // had RegatherSpeed since, she falls under it (a turn that slowed her). Not when her course is within GatherAlign of her heading.
        public const double GatherSpeed = MinTackSpeed, RegatherSpeed = 1.0, GatherPlateau = 15, GatherMost = 90, GatherAlign = 20;
        // Decision 131: she comes up toward the wind gently, the helm led at most this far ahead of her heading.
        public const double LuffLead = 15;
        // Her way made good toward the guide point below this share of a reach's speed is no way.
        public const double LeastWay = 0.02;
        // The user, 2026-10-11 ("you should not reef when you detect obstacle, instead change course to avoid it"): when a course's
        // way ahead is blocked, or her guide point lies out of clear water, headings fanned out from her guide point AvoidStep apart
        // are courses too, each sailable (no closer to the wind than close-hauled) with its way ahead clear; the one with the most
        // way made good is taken, and she keeps clear on it (Manoeuvre.Avoiding). Every AvoidRecheck she looks again, so she comes
        // back to her guide point once past; the heading she holds is kept unless another makes AvoidKeep more way.
        public const double AvoidStep = 10, AvoidRecheck = 5, AvoidKeep = 0.05;
        // Decision 180 (the user, 2026-10-11 night: "there is multiple occasion if the rudder just keep straight, you can sail out the
        // water way because the wind is usable, but you kept steering away"): in port (KeepHeading, set by her voyage), a course that
        // would turn her more than HoldTurn gives way to her heading as it is, sailable and clear, while it makes HoldShare of its way.
        public const double HoldTurn = 30, HoldShare = 0.5;
        public bool KeepHeading { get; set; }
        // Decision 209 (d): she sails her planned line (decision 198), set by her voyage each tick; straight at her guide on it comes first.
        public bool OnLine { get; set; }
        private bool holdingHeading;
        // Her sails refilled (after losing way, decision 126) also on a reach this far off the wind with this much way.
        public const double RefillReach = 80, RefillReachSpeed = 2.0;

        public Manoeuvre State { get; private set; }
        // The side of the wind she sails on: +1 her heading clockwise of it, -1 anticlockwise.
        public int Tack { get; private set; }
        public int TackCount { get; private set; }
        public double Demand { get; private set; }
        // The heading of her course as chosen (Demand may lead toward it, decision 131): the voyage checks its way (decision 140).
        public double CourseHeading { get; private set; }
        public string Reason { get; private set; } = "Awaiting course.";
        // The last choice: why it was made and each course's way made good toward the guide point, for the log.
        public string Choice { get; private set; } = "";
        // True for the tick that made a choice (the voyage logs it).
        public bool Chose { get; private set; }
        // Blocked because no clear, sailable course makes way toward her guide point (the voyage plans her routes again first).
        public bool NoWay { get; private set; }
        // Decision 131: she holds GatherHeading while she gathers way; for the tick it began or ended (the voyage logs it).
        public bool Gathering { get; private set; }
        public double GatherHeading { get; private set; }
        public bool GatherBegan { get; private set; }
        public string GatherEnded { get; private set; }
        // Waiting for a tack's speed this tick (decision 133): the voyage's slowing into a port gives her sail back meanwhile.
        public bool WaitingToTack { get; private set; }
        // For the tick a committed wear began (not a refill): inside her harbour's chart a square rig backs round instead (decision 160).
        public bool WearBegan { get; private set; }

        private bool planned, direct;
        // Keeping clear (the user, 2026-10-11): the heading she holds, how long since her course was chosen, and why it is chosen
        // again when the voyage asks (Rechoose, Interrupt).
        private bool avoiding;
        private double avoidHeading, sinceChoice;
        private string rechooseWhy, lastChoice;
        private bool gatherArmed = true, hadWay = true;
        private double gatherTime, gatherBest, gatherFlat;
        private int planSide;
        private double windAtChoice, sinceTurn = double.PositiveInfinity;
        // Decision 159 (the user, 2026-10-11, on Claude's design review: "Apparent, skip own turns"): the WindShift that chooses her
        // course again is measured on the apparent wind (decision 136), but only while she holds a steady heading: while she turns
        // (her heading moving SteadyRate or more a second) the wind it is measured from is taken afresh, so her own turns, which swing
        // the apparent wind, never count as a shift (run 20261011-112407 on DL: the Cog chose her course again every few seconds on
        // "the wind shifted 15-17 degrees" while turning, her helm hard over one way, then the other; DM had measured it on the true
        // wind instead).
        private double turning, turnBestError, turnNoProgress, lastHeading;
        private int previousSide;
        private bool refill, countOnCompletion, refilled;
        // Seconds to gather way before a stall counts; how long she has had no way.
        private double grace, stalled;
        // Decision 133: how long her heading has turned under SteadyRate; how long she has waited to tack; her failed tacks in a row.
        private double steady, lastSample = double.NaN, tackWait;
        private int tackFailures;
        // For the tick a tack was given up before the wind's eye (the voyage logs it).
        public string TackAborted { get; private set; }

        public void GatherWay(double seconds) { grace = Math.Max(grace, seconds); stalled = 0; }

        // A fresh start (a resume, a hold's retry, the end of the unberthing stage): her course is chosen on the next tick.
        public void Restore(int tack)
        {
            Tack = tack; State = Manoeuvre.Direct; planned = false; refill = countOnCompletion = NoWay = false;
            avoiding = holdingHeading = false; rechooseWhy = lastChoice = null;
            turning = turnNoProgress = stalled = 0; Reason = "Choosing her course.";
            Gathering = false; gatherArmed = hadWay = true;
            steady = tackWait = 0; tackFailures = 0; lastSample = double.NaN;
            resideNext = wearGivenUp = tackGivenUp = false;
        }

        // The same with how she lies: her side is where her bow is now (a save in the middle of a turn keeps no half-done turn;
        // she chooses her course again from the wind she has).
        public void Restore(int tack, double heading, double windFrom, double noGo)
        {
            foreach (var value in new[] { heading, windFrom, noGo })
                if (!NavigationMath.Finite(value)) throw new ArgumentException("Non-finite restored manoeuvre geometry.");
            if (noGo < 10 || noGo >= 90) throw new ArgumentException("Invalid restored manoeuvre limits.");
            double off = NavigationMath.Delta(windFrom, heading);
            Restore(off < 0 ? -1 : off > 0 ? 1 : tack != 0 ? tack : 1);
        }

        // Her course chosen again on the next tick, her manoeuvre and side kept (a route planned again: no fresh start, which
        // reset her turn and gathering every 8 s in the arrival lab, run 20261011-002639).
        public void Rechoose(string why)
        {
            planned = false; rechooseWhy = why;
        }

        // A tack or wear whose way is blocked (the user, 2026-10-11: a turn into what lies ahead is no way out): given up where her
        // bow is, and her course chosen again, keeping clear (the voyage held her instead: "Downwind recovery turn is obstructed").
        // Decision 170: her side is taken afresh from where her bow lies on the next tick (the turn had set it to the side it was
        // going to), and a wear given up whose turn is wanted again within WearRetry is no way at all: blocked.
        public void Interrupt(string why)
        {
            if (State != Manoeuvre.Tacking && State != Manoeuvre.Wearing) { Rechoose(why); return; }
            if (State == Manoeuvre.Wearing && !refill) wearGivenUp = true;
            if (State == Manoeuvre.Tacking) tackGivenUp = true;
            State = PlainState; turning = turnNoProgress = 0; refill = countOnCompletion = false; sinceTurn = 0;
            resideNext = true;
            Rechoose(why);
        }
        // Decision 170 (Claude, 2026-10-11 night, a bug against decisions 126 and 133): run 20261011-173154 on EK, three times a wear
        // given up (its way blocked) left her side on the wear's new side; her next course across the wind's eye was no turn to the
        // navigator, and the helm's guard wore her round her stern anyway, down the wind onto Dragon Cliffs' east docks.
        public const double WearRetry = 10;
        // Decision 173: her bow fell across the wind's eye with no turn under way (the helm's guard saw it): her side taken from her
        // bow on the next tick, and her course chosen again.
        public void FellAcross(string why)
        {
            if (State == Manoeuvre.Tacking || State == Manoeuvre.Wearing) return;
            resideNext = true;
            Rechoose(why);
        }
        // A tack given up (its way blocked) isn't begun again within WearRetry either (run 20261012-072254 on FJ: the Brig's tack, its arc
        // blocked by Cube, was begun and given up every tick for 30 s, each start leading her helm up toward the wind, and she crept
        // onto Cube at 2.2 m/s); meanwhile she keeps her side as when short of a tack's speed.
        private bool resideNext, wearGivenUp, tackGivenUp;

        private Manoeuvre PlainState => avoiding ? Manoeuvre.Avoiding : direct ? Manoeuvre.Direct : Manoeuvre.CloseHauled;
        // A wear she chose (not a refill, bearing away to fill her sails after losing way): given up at any speed when its arc is
        // blocked (decision 225 (16), Voyage.GivesUpTurn).
        public bool CommittedWear => State == Manoeuvre.Wearing && !refill;

        // One sample: her heading, the bearing to her guide point, the wind, her no-go, her speed ahead; `directClear`: the line to
        // the guide point is clear water; `clear`: her way ahead on a heading is clear (her look-ahead). The voyage passes the
        // apparent wind and her rig's dead-zone angle (decision 136); the engine-free code reads any wind and its no-go alike.
        public double Tick(double heading, double bearing, double windFrom, double noGo, double speed, double dt,
            bool directClear, Func<double, bool> clear)
        {
            foreach (var v in new[] { heading, bearing, windFrom, noGo, speed, dt })
                if (!NavigationMath.Finite(v)) throw new ArgumentException("Non-finite manoeuvre sample.");
            if (dt < 0 || dt > 1 || noGo < 10 || noGo >= 90 || clear == null) throw new ArgumentException("Invalid manoeuvre limits.");
            Chose = false; GatherBegan = false; GatherEnded = null; TackAborted = null; WaitingToTack = false; WearBegan = false; LossLearned = null; sinceTurn += dt; sinceChoice += dt;
            // Decision 133: how long her heading has held steady.
            if (!double.IsNaN(lastSample) && dt > 0) steady = Math.Abs(NavigationMath.Delta(lastSample, heading)) / dt < SteadyRate ? steady + dt : 0;
            lastSample = heading;
            // Decision 170: after a turn given up, her side is where her bow lies.
            if (resideNext) { resideNext = false; Tack = Side(windFrom, heading); }
            if (wearGivenUp && sinceTurn >= WearRetry) wearGivenUp = false;
            if (tackGivenUp && sinceTurn >= WearRetry) tackGivenUp = false;
            // Decision 159: turning, the wind her shift is measured from is taken afresh.
            if (steady <= 0) windAtChoice = windFrom;
            // Armed again once she has had RegatherSpeed since her last gathering and lost it (a turn that slowed her).
            if (speed >= RegatherSpeed) hadWay = true;
            else if (hadWay) gatherArmed = true;
            if (State == Manoeuvre.Blocked) { StopGathering("she is held"); return Demand; }
            if (State == Manoeuvre.Tacking || State == Manoeuvre.Wearing) { StopGathering("she turns"); return Turn(heading, bearing, windFrom, noGo, speed, dt); }
            // Decision 225 (15): with no tack or wear under way, her side is her bow's once her bow lies outside close-hauled (it swung
            // across with her helm at nought: a wait, a hold): a course on her old side is then a change of side, a tack or a wear, not a
            // turn round her stern the long way (Voyage.Steer). Inside close-hauled her side stays (decision 173 there).
            if (Math.Abs(NavigationMath.Delta(windFrom, heading)) >= noGo + Margin) Tack = Side(windFrom, heading);
            // A grace to gather way (back in physics at the clock's speed, her sails still setting): no stall counts until it is
            // out (run 20261010-122604).
            if (grace > 0) { grace -= dt; stalled = 0; }
            stalled = speed < 0.25 ? stalled + dt : 0;
            if (stalled > 12 && Tack != 0)
            {
                StopGathering("no way on her heading");
                StartTurn(heading, Tack, true, "Lost forward steerage; bearing away to refill sails.");
                return Demand = NavigationMath.Normalize(windFrom + Tack * 120);
            }
            if (Reconsider(bearing, windFrom, noGo, directClear, clear, out string why))
                Choose(heading, bearing, windFrom, noGo, directClear, clear, why);
            if (State == Manoeuvre.Blocked) { StopGathering("she is held"); return Demand; }
            double course = Course(bearing, windFrom, noGo, speed);
            CourseHeading = course;
            State = PlainState;
            // Decision 131: with little way she holds the heading her sail draws on; her course waits.
            if (Gather(heading, course, windFrom, noGo, speed, dt, clear))
            {
                Tack = Side(windFrom, GatherHeading);
                Reason = "Gathering way on her heading before she turns to her course.";
                return Demand = GatherHeading;
            }
            int side = Side(windFrom, course);
            // On the other side of the wind: across the wind's eye a tack (a wear when slow); round her stern a plain turn.
            // Decision 167: or a change of side over SternTurnMost, which read as round her stern (run 20261011-164839 on EH: from 205
            // to 26 the short way, 179 degrees, missed the wind's eye; she bore away downwind unasked, ran at 3.4 m/s and struck Terrain).
            if (side != Tack && (CrossesEye(heading, course, windFrom) || Math.Abs(NavigationMath.Delta(heading, course)) > SternTurnMost))
                return BeginTurn(side, heading, bearing, windFrom, noGo, speed, dt, clear);
            Tack = side; tackWait = 0;
            Reason = holdingHeading ? "Holding her heading: clear and making way (decision 180)." : avoiding ? "Keeping clear of what lies on her way to her guide point." : direct ? "Sailing at her guide point." : "Beating toward her guide point.";
            // Her heading's way blocked (decision 140): straight to her course, not led up 15 degrees at a time.
            return Demand = clear(heading) ? Luff(heading, course, windFrom) : course;
        }

        // Decision 131, gathering way (the constants above). True while she holds GatherHeading.
        private bool Gather(double heading, double course, double windFrom, double noGo, double speed, double dt, Func<double, bool> clear)
        {
            double close = noGo + Margin;
            if (BearingAway) { StopGathering("she bears away against her weather helm"); return false; }
            if (Gathering)
            {
                gatherTime += dt;
                if (speed > gatherBest + 0.1) { gatherBest = speed; gatherFlat = 0; }
                else gatherFlat += dt;
                string end = speed >= GatherSpeed ? "she has way (" + speed.ToString("F1") + " m/s)"
                    : gatherFlat >= GatherPlateau ? "her speed stopped rising (" + speed.ToString("F1") + " m/s)"
                    : gatherTime >= GatherMost ? GatherMost.ToString("F0") + " s on it (" + speed.ToString("F1") + " m/s)"
                    : Math.Abs(NavigationMath.Delta(windFrom, GatherHeading)) < close ? "the wind came onto her bow"
                    : !clear(GatherHeading) ? "her way ahead closes"
                    : Math.Abs(NavigationMath.Delta(GatherHeading, course)) <= GatherAlign ? "her course came round to her heading"
                    : null;
                if (end == null) return true;
                StopGathering(end);
                gatherArmed = hadWay = false;
                return false;
            }
            if (!gatherArmed || speed >= GatherSpeed || Math.Abs(NavigationMath.Delta(windFrom, heading)) < close ||
                Math.Abs(NavigationMath.Delta(heading, course)) <= GatherAlign || !clear(heading)) return false;
            Gathering = GatherBegan = true; GatherHeading = NavigationMath.Normalize(heading);
            gatherTime = gatherFlat = 0; gatherBest = speed;
            return true;
        }

        private void StopGathering(string why)
        {
            if (!Gathering) return;
            Gathering = false; GatherEnded = why;
        }

        // Decision 131: up toward the wind gently. A course closer to the wind than her heading, on her side, is led LuffLead ahead
        // of her bow; she bears away, or turns round her stern, at once.
        public static double Luff(double heading, double course, double windFrom)
        {
            double h = NavigationMath.Delta(windFrom, heading), c = NavigationMath.Delta(windFrom, course);
            if (h < 0 != c < 0 || Math.Abs(c) >= Math.Abs(h) - LuffLead) return course;
            return NavigationMath.Normalize(windFrom + (h < 0 ? -1 : 1) * (Math.Abs(h) - LuffLead));
        }

        // When the choice no longer holds, or the wind has shifted more than WindShift since it was made. Nothing else chooses
        // again: a course whose way made good is below the best is kept (decision 127).
        private bool Reconsider(double bearing, double windFrom, double noGo, bool directClear, Func<double, bool> clear, out string why)
        {
            why = null;
            if (!planned) { why = rechooseWhy ?? "a fresh start"; rechooseWhy = null; return true; }
            double shift = Math.Abs(NavigationMath.Delta(windAtChoice, windFrom));
            if (steady > 0 && shift > WindShift) { why = "the wind shifted " + shift.ToString("F0") + " degrees"; return true; }
            double gap = NavigationMath.Delta(windFrom, bearing), close = noGo + Margin;
            if (avoiding)
            {
                if (!clear(Demand)) { why = "her way ahead closes"; return true; }
                if (sinceChoice >= AvoidRecheck) { why = "she looks again past what she keeps clear of"; return true; }
                return false;
            }
            if (direct)
            {
                if (!directClear) { why = "her guide point is no longer in clear water"; return true; }
                if (Math.Abs(gap) < close) { why = "her guide point lies in her no-go"; return true; }
            }
            else
            {
                if (directClear && planSide * gap >= BeatOff(noGo, Margin) + 3 && clear(bearing)) { why = "she can lay her guide point"; return true; }
                if (-planSide * gap >= BeatOff(noGo, Margin) + 3) { why = "the other tack lays her guide point"; return true; }
            }
            if (!clear(Demand)) { why = "her way ahead closes"; return true; }
            return false;
        }

        // Of the courses whose way ahead is clear (straight at the guide point when it lies outside her no-go and in clear water;
        // close-hauled on either side), the one with the most way made good toward the guide point; the other side of the wind
        // costs TurnCost, across the wind's eye TackCost.
        private void Choose(double heading, double bearing, double windFrom, double noGo, bool directClear, Func<double, bool> clear, string why)
        {
            int current = Tack != 0 ? Tack : Side(windFrom, heading);
            double close = noGo + Margin, gap = NavigationMath.Delta(windFrom, bearing);
            var text = new StringBuilder(why).Append(":");
            var courses = new System.Collections.Generic.List<Candidate>
            {
                new Candidate("straight at it", true, gap < 0 ? -1 : 1, bearing, directClear && Math.Abs(gap) >= close),
                new Candidate("close-hauled, the wind to port", false, 1, windFrom + BeatOff(noGo, Margin), true),
                new Candidate("close-hauled, the wind to starboard", false, -1, windFrom - BeatOff(noGo, Margin), true),
            };
            foreach (var c in courses) c.Usable = c.Possible && clear(c.Heading);
            // Something in her way (a course's way blocked, or her guide point out of clear water outside her no-go): the fan of
            // headings around her guide point, sailable and clear, keeping clear of it (the user, 2026-10-11).
            bool obstructed = courses.Exists(c => c.Possible && !c.Usable) || !directClear && Math.Abs(gap) >= close;
            int fanned = 0;
            if (obstructed)
                for (int k = 1; k * AvoidStep < 180; k++)
                    foreach (int s in new[] { 1, -1 })
                    {
                        double h = NavigationMath.Normalize(bearing + s * k * AvoidStep);
                        if (Math.Abs(NavigationMath.Delta(windFrom, h)) < close || !clear(h)) continue;
                        courses.Add(new Candidate("keeping clear at " + h.ToString("F0"), false, Side(windFrom, h), h, true) { Avoid = true, Usable = true });
                        fanned++;
                    }
            foreach (var c in courses)
            {
                c.Way = Vmg(c.Heading, bearing, windFrom, noGo);
                c.Score = c.Way - (c.Side == current ? 0 : CrossesEye(heading, c.Heading, windFrom) ? TackCost : TurnCost);
                // The heading she keeps clear on is kept unless another makes AvoidKeep more way (no swing from side to side).
                if (avoiding && c.Avoid && Math.Abs(NavigationMath.Delta(c.Heading, avoidHeading)) < AvoidStep / 2) c.Score += AvoidKeep;
            }
            Candidate best = null;
            foreach (var c in courses)
            {
                if (c.Avoid) continue;
                text.Append(' ').Append(c.Name).Append(' ');
                if (!c.Possible) { text.Append("(not in clear water outside her no-go);"); continue; }
                if (!c.Usable) { text.Append("(her way ahead is blocked);"); continue; }
                text.Append(c.Way.ToString("F2")).Append(';');
            }
            // Decision 209 (d): on her planned line (OnLine) its way is her course: straight at her guide on it whenever that is clear and
            // makes way, whatever side of the wind it puts her on; no side change's cost and no heading she keeps clear on outscores it
            // (run 20261013-060421: the Brig running before the wind on her line, the side change cost "straight at it" and she kept clear
            // on her side, 7-10 m off her line, where no line her circle allows was found).
            var straight = courses[0];
            if (OnLine && straight.Usable && straight.Way > LeastWay) best = straight;
            else
                foreach (var c in courses)
                {
                    if (!c.Usable || c.Way <= LeastWay) continue;
                    if (best == null || c.Score > best.Score) best = c;
                }
            if (obstructed) text.Append(' ').Append(fanned).Append(" clear headings around her guide point;");
            if (KeepHeading && !OnLine && best != null && Math.Abs(NavigationMath.Delta(heading, best.Heading)) > HoldTurn && Math.Abs(NavigationMath.Delta(windFrom, heading)) >= close && clear(heading))
            {
                double way = Vmg(heading, bearing, windFrom, noGo);
                if (way > LeastWay && way >= HoldShare * best.Way)
                {
                    text.Append(" her heading as it is (").Append(NavigationMath.Normalize(heading).ToString("F0")).Append(") ").Append(way.ToString("F2")).Append(", instead of a turn to ").Append(best.Name).Append(" (decision 180);");
                    best = new Candidate("her heading as it is (" + NavigationMath.Normalize(heading).ToString("F0") + ")", false, Side(windFrom, heading), heading, true) { Avoid = true, Usable = true, Hold = true, Way = way };
                }
            }
            bool noWay = false;
            // None makes way: keeping clear on the clear, sailable heading that loses least is still better than holding (the user,
            // 2026-10-11); the voyage chooses her routes again meanwhile (NoWay).
            if (best == null)
                foreach (var c in courses)
                    if (c.Avoid && (best == null || c.Score > best.Score)) { best = c; noWay = true; }
            bool same = avoiding && best != null && best.Avoid && Math.Abs(NavigationMath.Delta(best.Heading, avoidHeading)) < AvoidStep / 2;
            windAtChoice = windFrom; sinceChoice = 0;
            // Looking again and keeping the same heading, or the same course taken again for the same reason (run 20261011-005455:
            // "the other tack lays her guide point" every tick while she keeps her new side), isn't a new choice (the voyage logs each).
            string key = why + "|" + (best == null ? "none" : best.Avoid ? "clear " + Math.Round(best.Heading / AvoidStep) : best.Name);
            Chose = !(same && why.StartsWith("she looks again", StringComparison.Ordinal)) && key != lastChoice;
            lastChoice = key;
            if (best == null)
            {
                planned = false; NoWay = true; avoiding = holdingHeading = false;
                Choice = text.Append(" none makes way").ToString();
                Block("No clear, sailable course makes way toward her guide point.", heading);
                return;
            }
            planned = true; NoWay = noWay; direct = best.Direct; planSide = best.Side;
            avoiding = best.Avoid; avoidHeading = best.Heading; holdingHeading = best.Hold;
            Choice = text.Append(noWay ? " none makes way; taken: " : " taken: ").Append(best.Name).ToString();
        }

        private sealed class Candidate
        {
            internal readonly string Name; internal readonly bool Direct, Possible; internal readonly int Side; internal readonly double Heading;
            internal bool Usable, Avoid, Hold; internal double Way, Score;
            internal Candidate(string name, bool isDirect, int side, double heading, bool possible)
            { Name = name; Direct = isDirect; Side = side; Heading = NavigationMath.Normalize(heading); Possible = possible; }
        }

        // The heading she steers on her course: at the guide point, or close-hauled on her side; nearing a stop (Footing) up to
        // FootingMargin further off the wind (never closer than close-hauled), so she builds speed before she points high.
        private double Course(double bearing, double windFrom, double noGo, double speed)
        {
            double least = BeatOff(noGo, Margin) + Footing(speed);
            if (direct || avoiding)
            {
                double aim = avoiding ? avoidHeading : bearing, gap = NavigationMath.Delta(windFrom, aim);
                return Math.Abs(gap) >= least ? NavigationMath.Normalize(aim) : NavigationMath.Normalize(windFrom + (gap < 0 ? -1 : 1) * least);
            }
            return NavigationMath.Normalize(windFrom + planSide * least);
        }

        // Across the wind's eye (decisions 126, 133): a tack, whatever her rig, once she meets a tack's requirements (her speed to
        // finish it, decision 161; her sails drawing; decision 168: no steady heading first); short of them she keeps her side close-hauled and gathers way. A
        // wear after TackWaitMost of that, after MaxTackFailures failed tacks, or when her side's way is blocked.
        // Decision 190 (the user, 2026-10-11 night, the Cog's wear blocked by Dragon Cliffs' dock: "probally don't wear at all in
        // port"): in port (NoWear, set by her voyage) no wear while she can wait: short of a tack she keeps her side and builds her
        // speed (decision 191: footing off where there is room), whatever her failed tacks (each failure raises the speed she waits
        // for, LossPerDegree); a wear only with no room to wait or no speed after TackWaitMost, where its turn is clear (decision 191).
        // A tack lost across the wind still bears away on her new side to fill her sails.
        public bool NoWear { get; set; }

        private double BeginTurn(int side, double heading, double bearing, double windFrom, double noGo, double speed, double dt, Func<double, bool> clear)
        {
            if (NoWear && Tack == 0) Tack = Side(windFrom, heading);
            double hold = NavigationMath.Normalize(windFrom + Tack * (BeatOff(noGo, Margin) + Footing(speed)));
            bool canTack = CanTack(speed, heading, windFrom);
            // Decision 168: no waiting for a steady heading; her speed to finish it (decision 161) and her sails drawing decide.
            if (canTack && SailsDrawing && tackFailures < MaxTackFailures && !tackGivenUp)
            {
                tackWait = 0;
                StartTurn(heading, Tack != 0 ? Tack : -side, false, "Committed tack (" + speed.ToString("F1") + " m/s, " + SpeedToTack(heading, windFrom).ToString("F1") + " needed to cross the wind).");
                Tack = side; State = Manoeuvre.Tacking;
                tackStartSpeed = speed; tackStartOff = Math.Abs(NavigationMath.Delta(windFrom, heading)); tackCrossed = false;
                return Demand = NavigationMath.Normalize(windFrom + side * TackPastEye);
            }
            tackWait += dt;
            if (NoWear)
            {
                // Decision 191 (the user, 2026-10-12, asked how the Kakam, 1.1-1.2 m/s close-hauled in a light air short of the 1.4 her tack
                // needed, should get out of Dragon Cliffs' basin with no wear: "foot off to build speed"): short of a tack's speed she
                // sails FootingMargin further off the wind than close-hauled while that heading is clear, else close-hauled, and tacks
                // once she has the speed (run 20261012-002938 on FH: blocked three times at 1.1-1.2 m/s, the backup tow).
                // And (the user: "if it still don't work, allow wearing only where there is room"): with no room to foot off nor to hold
                // close-hauled, or no tack's speed after TackWaitMost, a wear, its turn checked clear by her voyage (WearArcClear: given
                // up when it isn't, then blocked, decision 170).
                double footed = NavigationMath.Normalize(windFrom + Tack * (BeatOff(noGo, Margin) + Math.Max(Footing(speed), canTack ? 0 : FootingMargin)));
                bool footing = !canTack && clear(footed);
                if ((footing || clear(hold)) && tackWait < TackWaitMost)
                {
                    if (tackFailures >= MaxTackFailures) tackFailures = 0;
                    WaitingToTack = true;
                    Reason = "Waiting to tack, no wear in port (decision 190): " + (!canTack ? speed.ToString("F1") + " of " + SpeedToTack(heading, windFrom).ToString("F1") + " m/s to cross the wind" +
                        (footing ? ", footing off to build it (decision 191)" : "") : "her sails not drawing") + ".";
                    return Demand = Luff(heading, footing ? footed : hold, windFrom);
                }
            }
            string wear = Tack == 0 ? "no side yet" : tackFailures >= MaxTackFailures ? tackFailures + " tacks failed" : !clear(hold) ? "her side's way is blocked"
                : tackWait >= TackWaitMost ? "no tacking speed in " + TackWaitMost.ToString("F0") + " s" : null;
            if (wear != null && wearGivenUp)
            {
                wearGivenUp = false;
                return Block("Her wear's way is blocked again and she can't tack (" + speed.ToString("F1") + " m/s; decision 170).", heading);
            }
            if (wear != null)
            {
                tackWait = 0; tackFailures = 0;
                StartTurn(heading, Tack != 0 ? Tack : -side, false, "Committed wear: " + wear + " (" + speed.ToString("F1") + " m/s" + (NoWear ? "; in port only where its turn is clear, decision 191" : "") + ").");
                Tack = side; State = Manoeuvre.Wearing; WearBegan = true;
                return Demand = NavigationMath.Normalize(heading + previousSide * 60);
            }
            WaitingToTack = true;
            Reason = "Waiting to tack: " + (!canTack ? speed.ToString("F1") + " of " + SpeedToTack(heading, windFrom).ToString("F1") + " m/s to cross the wind" : "her sails not drawing") + ".";
            return Demand = Luff(heading, hold, windFrom);
        }

        private void StartTurn(double heading, int fromSide, bool refillTurn, string reason)
        {
            previousSide = fromSide; refill = refillTurn; countOnCompletion = !refillTurn; refilled = false;
            turning = turnNoProgress = 0; turnBestError = 180; lastHeading = heading; sinceTurn = 0;
            if (refillTurn) { Tack = fromSide; State = Manoeuvre.Wearing; }
            Reason = reason;
        }

        // A turn under way. A tack holds her new course as its demand; one that loses her way (no speed, or no turning, in the
        // wind's eye) becomes a wear from where her bow is. A wear turns away from the wind on her old side, the helm led 60
        // degrees ahead of her heading, until her stern has passed the wind (her bow 25 degrees onto the new side), then she comes
        // up to her course: its marks are where her heading actually is, no fixed checkpoints (decision 126). It fails when her
        // heading stops moving her way for 30 s (in irons her sail is backed meanwhile), or after 100 s.
        private double Turn(double heading, double bearing, double windFrom, double noGo, double speed, double dt)
        {
            turning += dt;
            double target = Course(bearing, windFrom, noGo, speed);
            if (State == Manoeuvre.Tacking)
            {
                double error = Math.Abs(NavigationMath.Delta(heading, target));
                // Decision 161: her loss per degree learned where her bow crosses the wind's eye.
                if (!tackCrossed && Side(windFrom, heading) != previousSide)
                {
                    tackCrossed = true;
                    LearnLoss(tackStartSpeed, speed, tackStartOff + Math.Abs(NavigationMath.Delta(windFrom, heading)));
                }
                if (error < turnBestError - 1) { turnBestError = error; turnNoProgress = 0; }
                else turnNoProgress += dt;
                if (turning > 100) return Block("Manoeuvre timed out without steerage.", heading);
                if (error < 12 && speed > 0.35) { tackFailures = 0; return Complete(target, "Tack done with forward steerage."); }
                // Decision 133: lost under TackAbortSpeed (after 2 s of it), with no turn toward her new course for 10 s, or after
                // 75 s.
                bool lost = turning > 2 && speed < TackAbortSpeed || error > 18 && turnNoProgress > 10 || turning > 75;
                // Decision 167: her helm led to TackPastEye across the wind's eye first, then to her course: a course near 180 degrees
                // off is reached across the wind, never round her stern.
                if (!lost) return Demand = tackCrossed ? target : NavigationMath.Normalize(windFrom + Tack * TackPastEye);
                if (Side(windFrom, heading) != Tack)
                {
                    // Before the wind's eye: back onto her old tack, where she gathers way and tries again (MaxTackFailures, then
                    // a wear), never left in irons. Decision 161: her loss learned from the turn she made.
                    LearnLoss(tackStartSpeed, speed, tackStartOff - Math.Abs(NavigationMath.Delta(windFrom, heading)));
                    ++tackFailures;
                    Tack = previousSide; State = PlainState;
                    turning = 0; sinceTurn = 0; steady = 0; countOnCompletion = false;
                    gatherArmed = hadWay = true;
                    TackAborted = speed.ToString("F1") + " m/s before the wind's eye (" + tackFailures + " of " + MaxTackFailures + ")";
                    Reason = "Tack given up before the wind's eye; back on her old tack to gather way.";
                    return Demand = NavigationMath.Normalize(windFrom + previousSide * (BeatOff(noGo, Margin) + Footing(speed)));
                }
                // Across the wind: she bears away on her new side at once to fill her sails.
                double spent = turning;
                StartTurn(heading, Tack, true, "Tack lost way across the wind; bearing away on her new side to fill her sails.");
                turning = spent;
                State = Manoeuvre.Wearing;
                countOnCompletion = true;
            }
            double off = NavigationMath.Delta(windFrom, heading);
            // Her turn's way, away from the wind on the side she turns from (a refill: her own side).
            double step = NavigationMath.Delta(lastHeading, heading) * previousSide;
            lastHeading = heading;
            turnNoProgress = step > 0.2 * Math.Max(dt, 0.02) ? 0 : turnNoProgress + dt;
            if (turning > 100) return Block("Manoeuvre timed out without steerage.", heading);
            if (refill)
            {
                // Bear away on her side until the wind is 110 degrees off and she has way again, then back to her course.
                // Or a reach with way on (run 20261011-022857 at Gold Rock City: out of irons she sailed 92-108 degrees off the wind at
                // 2.3-2.9 m/s, never 110, and the turn timed out after 100 s).
                // Once filled, filled for the rest of the turn: else, turning back up toward a course closer to the wind she read as unfilled
                // again and bore away again (a refill finished only when her course lay that far off the wind).
                bool filled = refilled || (Math.Abs(off) >= 110 && speed >= 1.0 || Math.Abs(off) >= RefillReach && speed >= RefillReachSpeed) && Side(windFrom, heading) == previousSide;
                refilled = filled;
                if (!filled)
                {
                    if (turnNoProgress > 30 && Math.Abs(off) < 110) return Block("Bearing away stalls: her bow isn't falling off.", heading);
                    return Demand = NavigationMath.Normalize(windFrom + previousSide * 120);
                }
                if (Math.Abs(NavigationMath.Delta(heading, target)) < 12 && speed > 0.35) return Complete(target, "Sails refilled; back on her course.");
                return Demand = target;
            }
            bool sternThrough = Side(windFrom, heading) != previousSide && Math.Abs(off) <= 155;
            if (!sternThrough)
            {
                if (turnNoProgress > 30) return Block("The wear stalls: her heading isn't moving.", heading);
                return Demand = NavigationMath.Normalize(heading + previousSide * 60);
            }
            if (Math.Abs(NavigationMath.Delta(heading, target)) < 12 && speed > 0.35) return Complete(target, "Wear done with forward steerage.");
            return Demand = target;
        }

        private double Complete(double target, string reason)
        {
            State = PlainState; turning = stalled = 0; sinceTurn = 0;
            if (countOnCompletion) ++TackCount;
            countOnCompletion = refill = false; Reason = reason;
            return Demand = target;
        }

        private double Block(string reason, double heading) { State = Manoeuvre.Blocked; Reason = reason; return Demand = heading; }

        // The side of the wind a heading lies on: +1 clockwise of it, -1 anticlockwise.
        public static int Side(double windFrom, double heading) => NavigationMath.Delta(windFrom, heading) < 0 ? -1 : 1;

        // The short turn from `heading` to `course` passes through the wind's eye (else round her stern, or no change of side).
        public static bool CrossesEye(double heading, double course, double windFrom)
        {
            double a = NavigationMath.Delta(windFrom, heading), b = NavigationMath.Delta(windFrom, course);
            return (a < 0) != (b < 0) && Math.Abs(a) + Math.Abs(b) < 180;
        }

        // A speed estimate for an angle off the wind, as a share of her best: nothing in her no-go, then decision 210's measured share
        // by the angle beyond its edge (UpwindLimits.SpeedShare: 0.63 at the edge, 0.75 at 25 beyond, all of it from 80, the same dead
        // downwind), the curve her choice of sails ranks by. Decision 216 (the user, 2026-10-13 night: "use measured curve"): her course
        // choice, her heading off the dock, her backing's heading and her routes' timing rank by it too; it was Claude's assumed curve
        // (0.55 at the edge, all of it from 30 beyond, fore-and-aft rigs a quarter slower dead downwind).
        public static double Polar(double offWind, double noGo) => UpwindLimits.SpeedShare(offWind, noGo);

        // Decision 137: her close-hauled angle, her no-go and CloseMargin. Near the player (decision 136) her wind is the apparent
        // wind and her no-go her rig's dead-zone angle (decision 133), so a lateen (19) beats 26 degrees off the apparent wind. It
        // replaces CE's best way made good by the Polar guess, which put a lateen 37 degrees off the true wind, pinching.
        public static double BeatOff(double noGo, double margin = CloseMargin) => Math.Min(noGo + margin, 90);

        // Way made good toward a bearing on a heading: the Polar speed times the cosine of the angle off the bearing.
        public static double Vmg(double heading, double bearing, double windFrom, double noGo) =>
            Polar(NavigationMath.Delta(windFrom, heading), noGo) * Math.Cos(NavigationMath.Delta(heading, bearing) * Math.PI / 180);
    }

    // ==== TrafficPrediction ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public static class TrafficPrediction
    {
        // Decision 109 (review N4): traffic is a boat under way, faster than 1 m/s (about 2 knots) over the water. A boat lying
        // still (moored, anchored, drifting) is an obstacle her course and clearance checks keep her clear of; as traffic it
        // slowed her to steerage whenever she passed within ~12 m of it.
        public const double MovingSpeed = 1.0;

        public static bool Moving(double vx, double vz) => NavigationMath.Finite(vx) && NavigationMath.Finite(vz) && vx * vx + vz * vz > MovingSpeed * MovingSpeed;

        // Both vessels advance on the same clock. At the planned endpoint,
        // continue checking arriving traffic for the remaining prediction time.
        public static bool Intersects(RoutePoint from, RoutePoint to, double speed,
            RoutePoint other, double otherVx, double otherVz, double radius, double seconds = 20)
        {
            if (!RoutePlanner.Finite(from) || !RoutePlanner.Finite(to) || !RoutePlanner.Finite(other) ||
                !NavigationMath.Finite(speed) || speed < 0 || !NavigationMath.Finite(otherVx) ||
                !NavigationMath.Finite(otherVz) || !NavigationMath.Finite(radius) || radius < 0 ||
                !NavigationMath.Finite(seconds) || seconds < 0)
                throw new ArgumentException("Traffic prediction requires finite positions, velocities and nonnegative bounds.");
            double dx = to.x - from.x, dz = to.z - from.z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            double travelTime = speed > 0 ? Math.Min(seconds, distance / speed) : 0;
            double fraction = distance > 0 ? Math.Min(1, speed * travelTime / distance) : 0;
            double ax = from.x - other.x, az = from.z - other.z;
            double bx = ax + dx * fraction - otherVx * travelTime;
            double bz = az + dz * fraction - otherVz * travelTime;
            return NavigationMath.SegmentEntersRadius(ax, az, bx, bz, 0, 0, radius) ||
                NavigationMath.SegmentEntersRadius(bx, bz,
                    bx - otherVx * (seconds - travelTime), bz - otherVz * (seconds - travelTime), 0, 0, radius);
        }
    }

    // ==== TurnCircle ====

    // Decision 198 (a) (the user, 2026-10-12: "it should know that there is a turn coming and she need to judge if she can make that
    // turn"): her turning circle as she turns, not assumed. Measured from her own turns at her helm's full turn rate (her speed ahead over her
    // turn rate), toward the wind and away from it apart (the Brig's traces, 2026-10-12: 47 m toward the wind, 55 m away at the tightest,
    // 52-61 m as she usually turns; decision 169's 5 degrees a second gave her 29 m at 2.5 m/s). Kept per hull type, and remembered in
    // Shipwright's save (TurnCircleRecord): a hull measured once plans with her own circle in every later game of that save.
    // Engine-free, so it is host-tested.

    internal sealed class TurnCircle
    {
        // A sample counts while her helm's controller asks its full turn rate (HeadingController.FullRateSide; decision 225 (10): by the
        // helm's size, 0.75 and more, the turn-rate helm gave it only while she turned under about 1 degree a second, circles of 86-172 m),
        // with this much way on and this much turn (a steady heading says nothing of her circle).
        internal const double LeastSpeed = 1.5, LeastRate = 0.5;
        // Her circle is the tightest steady one she makes: the KeepFraction-th of the last Kept samples, once there are Least of them
        // (a sample a second at most, Watch: Kept is her last 40 seconds of hard-helmed turns, Least 5 of them).
        internal const int Kept = 40, Least = 5;
        internal const double KeepFraction = 0.25;
        // Until measured: DefaultLengths of her length. Planned at ReserveRate of her best turn rate (her helm kept in reserve).
        // Decision 201 (d) as the user changed it (2026-10-12 late night: "change to 2 hull length for circle without bowsprit", once her
        // length had cost lines the harbour check found with twice it): twice her hull's length without her bowsprit, her stored length
        // (Storage.LengthOf: the Brig 25.9 m, her length with her bowsprit, NavigationWater.Length, 29.0 m); twice that one until FY.
        internal const double DefaultLengths = 2, ReserveRate = 0.75;

        private readonly List<double> toward = new List<double>(), away = new List<double>();

        // One moment of her sailing: her speed ahead (m/s), her turn rate (degrees a second, + to starboard), the side her helm's
        // controller asks its full rate to (`asked`: +1 starboard, -1 port, 0 not at its full rate), and whether she turns toward the wind
        // (luffing up) or away from it (bearing away). True when it counted. Only a turn her helm asks: a turn the other way (a spin
        // against her helm, decisions 176 and 178, or her swing being checked) is the wind's or her own momentum's, and its circle, often
        // tighter than her helm can turn her, isn't hers (Claude's review, 2026-10-12).
        internal bool Sample(double speedAhead, double turnRate, int asked, bool towardWind)
        {
            if (!(speedAhead >= LeastSpeed) || asked == 0 || !(Math.Abs(turnRate) >= LeastRate) || double.IsNaN(turnRate)) return false;
            if (Math.Sign(turnRate) != Math.Sign(asked)) return false;
            double radius = speedAhead / (Math.Abs(turnRate) * Math.PI / 180);
            if (!(radius > 0) || radius > 2000) return false;
            var list = towardWind ? toward : away;
            list.Add(radius);
            if (list.Count > Kept) list.RemoveAt(0);
            return true;
        }

        internal int Samples(bool towardWind) => (towardWind ? toward : away).Count;
        internal bool Measured(bool towardWind) => Samples(towardWind) >= Least;

        // Her tightest steady circle that way (m), or NaN while unmeasured.
        internal double Tightest(bool towardWind)
        {
            var list = towardWind ? toward : away;
            if (list.Count < Least) return double.NaN;
            var sorted = new List<double>(list);
            sorted.Sort();
            return sorted[(int)Math.Floor(KeepFraction * (sorted.Count - 1))];
        }

        // The circle her line is planned with that way: her tightest at ReserveRate of her best turn rate (so her circle over
        // ReserveRate, at least half her length); unmeasured, the other way's when it is measured, else DefaultLengths of `hull`, her
        // hull's length without her bowsprit (decision 201 d; her length where that isn't known).
        internal double Plan(bool towardWind, double length, double hull = double.NaN)
        {
            if (!(length > 0)) throw new ArgumentException("A hull needs a length.", nameof(length));
            double r = Tightest(towardWind);
            if (double.IsNaN(r)) r = Tightest(!towardWind);
            return double.IsNaN(r) ? DefaultLengths * (hull > 0 ? hull : length) : Math.Max(length * 0.5, r / ReserveRate);
        }

        // The larger of the two ways' planned circles: for a check that doesn't know which way she turns.
        internal double PlanEither(double length, double hull = double.NaN) => Math.Max(Plan(true, length, hull), Plan(false, length, hull));

        // When a voyage samples her circle (Claude's review, 2026-10-12): once her helm's controller has asked its full rate the same way
        // for SteadySeconds (her swing built up: a turn's first seconds read a wider circle than she makes), and at most every SampleEvery
        // seconds. Her helm is steered every physics step: a sample each filled her Kept in under a second and made her measured in a
        // tenth, from whatever that moment held. One per voyage (her circle is her hull type's, which two ships of a type may share).
        internal const double SteadySeconds = 2, SampleEvery = 1;
        internal sealed class Watch
        {
            private int side;
            private double since = double.NegativeInfinity, next = double.NegativeInfinity;
            // `asked` the side her helm's controller asks its full rate to (0: not), `now` the game's seconds: true when a sample is due.
            internal bool Due(int asked, double now)
            {
                int hard = Math.Sign(asked);
                if (hard != side) { side = hard; since = now; }
                if (hard == 0 || now - since < SteadySeconds || now < next) return false;
                next = now + SampleEvery;
                return true;
            }
        }

        internal string Describe(double length, double hull = double.NaN)
        {
            string Way(bool t) => Measured(t) ? Tightest(t).ToString("F0") + " m (" + Samples(t) + " turns)" : "unmeasured";
            return "toward the wind " + Way(true) + ", away " + Way(false) + "; planned " + Plan(true, length, hull).ToString("F0") + " and " + Plan(false, length, hull).ToString("F0") + " m";
        }

        // Kept per hull type (her rig descriptor): a new voyage of the same hull starts from what the last one learned.
        private static readonly Dictionary<string, TurnCircle> ByHull = new Dictionary<string, TurnCircle>();
        // Decision 225 (20): her rig's text drifts in its last digits after a load (RigIdentity), so a known hull type within its tolerance
        // is hers: an exact key lost her learned circle.
        internal static TurnCircle For(string hullType)
        {
            if (string.IsNullOrEmpty(hullType)) return new TurnCircle();
            if (ByHull.TryGetValue(hullType, out var circle)) return circle;
            foreach (var known in ByHull) if (RigIdentity.Matches(known.Key, hullType)) return known.Value;
            circle = new TurnCircle(); ByHull[hullType] = circle;
            return circle;
        }

        // Decision 198 (a): what every hull type has learned, for Shipwright's save (its samples each way, to a tenth of a metre), in
        // the order of the hull types' names so the same knowledge always saves the same.
        internal static List<TurnCircleRecord> Capture()
        {
            var records = new List<TurnCircleRecord>();
            var hulls = new List<string>(ByHull.Keys);
            hulls.Sort(StringComparer.Ordinal);
            foreach (string hull in hulls)
            {
                var circle = ByHull[hull];
                if (circle.toward.Count + circle.away.Count == 0) continue;
                records.Add(new TurnCircleRecord { hull = hull, toward = Rounded(circle.toward), away = Rounded(circle.away) });
            }
            return records;
        }

        private static float[] Rounded(List<double> samples)
        {
            var kept = new float[samples.Count];
            for (int i = 0; i < kept.Length; i++) kept[i] = (float)(Math.Round(samples[i] * 10) / 10);
            return kept;
        }

        // A save's knowledge in place of the game before it (a load), every hull type it doesn't know unmeasured. A sample that
        // couldn't be a circle (not over 0, over 2 km) is left out, and each way keeps its last Kept.
        internal static void Restore(IEnumerable<TurnCircleRecord> records)
        {
            ByHull.Clear();
            if (records == null) return;
            foreach (var record in records)
            {
                if (record == null || string.IsNullOrEmpty(record.hull)) continue;
                var circle = new TurnCircle();
                Load(circle.toward, record.toward);
                Load(circle.away, record.away);
                ByHull[record.hull] = circle;
            }
        }

        private static void Load(List<double> list, float[] samples)
        {
            if (samples == null) return;
            foreach (float r in samples)
            {
                if (!(r > 0) || r > 2000) continue;
                list.Add(r);
                if (list.Count > Kept) list.RemoveAt(0);
            }
        }

        // A new game: nothing learned yet.
        internal static void Reset() => ByHull.Clear();
    }

    // ==== Unberthing ====

    // Leaving the berth (decisions 110-111, SAILING_TRANSPORT_DRAFT.md "Leaving the berth"): how she comes off a dock when her
    // straight way out is blocked. Engine-free, so its arithmetic is host-tested; Voyage (VoyageNavigation.cs) runs it.

    internal static class Unberthing
    {
        internal const double MinimumWind = 1;            // below it no wind pins her to the dock (m/s, the game's wind)
        internal const double OnshoreAngle = 120;         // wind beyond 120 degrees of it (from open water): onshore
        internal const double FirstTurn = 30, LastTurn = 150, TurnStep = 15;
        internal const double TowBudget = 300;            // seconds of towing, then a hold (D6)
        internal const double ContactSpeed = 0.3;         // closing on the dock faster than this: her own way stops, the tow
        // A stronger tow since candidate AR (the user, 2026-10-09, after runs 20261009-194114 and -201141: towed 63-76 m in 10
        // minutes against a 15 m/s crosswind): gain 1.5 (was 0.5, which kept the pull at 0.35 m/s² for the 0.7 m/s she lacked,
        // under even its cap) and cap 1.2 m/s² (was 0.4: 362 N on the Dhow).
        internal const double TowSpeed = 0.7;
        // No fixed strength (the user, 2026-10-10, after runs 20261009-234905 and 20261010-005136: against an onshore 14-52 m/s the
        // capped pull, 1.2 m/s² unless she was nearly stopped, held her at 0.85 m/s, and her 300-400 m way out outlasted the tow's 5
        // minutes): her speed is the standard. Each physics step the strength (m/s² of pull) rises while she is slower along the
        // tow than its speed and falls while she is faster; it starts at the old 1.2. TowStrengthLimit is a safety ceiling, not a
        // working strength: a ship pinned on a dock can't wind it up without end and be thrown off when she frees.
        internal const double TowStrengthStart = 1.2, TowRise = 0.5, TowFall = 1.0, TowSpeedBand = 0.05, TowStrengthFloor = 0.2, TowStrengthLimit = 20;
        internal static double TowStrength(double strength, double speedAlong, double target, double dt)
        {
            if (!NavigationMath.Finite(strength) || !NavigationMath.Finite(speedAlong) || !NavigationMath.Finite(target) || !(dt >= 0))
                throw new ArgumentException("Invalid tow strength sample.");
            if (speedAlong < target - TowSpeedBand) return Math.Min(TowStrengthLimit, strength + TowRise * dt);
            if (speedAlong > target + TowSpeedBand) return Math.Max(TowStrengthFloor, strength - TowFall * dt);
            return strength;
        }
        // Bow first (the user, 2026-10-10, after run 20261010-014335: almost all of the tow's pull was across her, 14.5 kN for
        // 0.95 m/s against her keel, and she struck east_dock (72) at 1.08 m/s): out of the harbour and in to her berth she is
        // turned onto the tow's line about her middle (no pull across her) and pulled along her length only, the more the
        // better she points along the line: all of the tow's speed on it, none from 60 degrees off it.
        internal const double BowFirstAngle = 60;
        internal static double BowFirstShare(double offDegrees)
        {
            double cos = Math.Cos(Math.Abs(offDegrees) * Math.PI / 180), edge = Math.Cos(BowFirstAngle * Math.PI / 180);
            return Math.Max(0, (cos - edge) / (1 - edge));
        }
        // The turn onto the line, led by her rate of turn: 0.4 degrees a second for each degree off it, at most 6 a second; the
        // turn's acceleration twice the rate it lacks, within a safety limit (degrees a second, per second).
        // The limit was 30 until the review of 2026-10-10: her hull's yaw drag and the wind took about 10 degrees a second² for
        // each degree a second of turn (the Dhow turned 1 a second on 10 of push, runs 20261010-083701 and -083709), so 6 a
        // second needs about 60. Her rate stays within 6 a second whatever the push.
        internal const double TurnGain = 0.4, TurnRateLimit = 6, TurnResponse = 2, TurnAccelerationLimit = 60;

        // The turn with what it still lacks built up (the user, 2026-10-10): the push on the rate she lacked alone fell short against her
        // hull's yaw drag and a steady wind, so she turned about 1 degree a second instead of 6, and beyond BowFirstAngle off the
        // tow's line, with no pull, she stalled and drifted. Here the rate she lacks also builds a steady push (TurnBuildRate a
        // second for each degree a second lacking), which then holds her at the rate she wants; it never builds past the safety
        // limit, nor while the push is already at the limit that way (no wind-up). One per voyage, reset when a tow begins.
        internal const double TurnBuildRate = 2;

        internal sealed class TurnController
        {
            private double built;
            internal double Built => built;
            internal void Reset() => built = 0;

            internal double Tick(double offDegrees, double rateDegrees, double dt)
            {
                if (!NavigationMath.Finite(offDegrees) || !NavigationMath.Finite(rateDegrees) || !NavigationMath.Finite(dt) || dt < 0)
                    throw new ArgumentException("Invalid turn sample.");
                double want = NavigationMath.Clamp(offDegrees * TurnGain, -TurnRateLimit, TurnRateLimit);
                double lack = want - rateDegrees;
                double proportional = lack * TurnResponse;
                double total = proportional + built;
                bool atLimit = Math.Abs(total) >= TurnAccelerationLimit && Math.Sign(total) == Math.Sign(lack);
                if (!atLimit) built = NavigationMath.Clamp(built + lack * TurnBuildRate * dt, -TurnAccelerationLimit, TurnAccelerationLimit);
                return NavigationMath.Clamp(proportional + built, -TurnAccelerationLimit, TurnAccelerationLimit);
            }
        }
        // The pull along her length toward `target` m/s (TowStrength's strength): the whole strength from rest, easing near it;
        // backward when she goes faster.
        internal static double TowAlong(double along, double target, double strength)
        {
            if (!NavigationMath.Finite(along) || !NavigationMath.Finite(target) || !(strength > 0)) throw new ArgumentException("Invalid tow sample.");
            return NavigationMath.Clamp(strength * (target - along) / Math.Max(0.5, target), -strength, strength);
        }

        // Held by the docks she touches (launch 5, candidate T: her hull against east_dock_base on both sides, the tow made 0.02
        // m/s for 5 minutes): after 20 s under 0.1 m/s her way out is planned again from where she is (TowHeadway).
        internal const double StuckSpeed = 0.1, FreeSpeed = 0.3, StuckSeconds = 20;
        internal static double StuckTime(double stuckSeconds, double speed, double dt) =>
            speed < StuckSpeed ? stuckSeconds + dt : speed > FreeSpeed ? 0 : stuckSeconds;
        // Out by the bow along a planned way (launch 5, candidate O: a straight heading kept edging along the harbour's "Cube"
        // for 5 minutes): to an offing 300 m off (out of the 300 m round her berth where a fault sends her back to this stage;
        // candidate P's 200 m, ended once her route's next leg looked clear, left her to tack back onto the docks), at 1.5 m/s.
        internal const double TowOffing = 300, TowOutSpeed = 1.5;

        // D17 (run 20261007-124359: the tow's way, swept by her half-beam alone, led her bow onto the foot of Dragon Cliffs'
        // cliff): the tow's way keeps half her length + 3 m clear (her hull at an angle to it, her leeway), except in the first
        // 15 m from where she starts (a dock beside her). Followed as a line (Pursue), not point to point: turned toward each
        // next point a length short of it, she cut across the inside of the bend, onto what the bend went round.
        internal const double TowMargin = 3, TowNear = 15;
        internal static double TowClearance(double radius, double length) => Math.Max(radius, length / 2) + TowMargin;

        // Following the way as a line (candidate AG, run 20261007-171636: steered from where she was straight at its next point,
        // set off the way by the wind, that line crossed the cliff, the way was "blocked", planned again narrower, and she ended
        // on the cliff's foot): her nearest point on the way from leg `leg` on (never back), and the point `lookahead` metres
        // further along it, which she is towed toward; how far off the way she is; true at its end (within `lookahead` of it).
        internal static bool Pursue(RoutePoint[] path, ref int leg, RoutePoint at, double lookahead, out RoutePoint nearest, out RoutePoint target, out double offWay)
        {
            if (path == null || path.Length < 2 || !(lookahead > 0)) throw new ArgumentException("Invalid way.");
            leg = Math.Max(0, Math.Min(leg, path.Length - 2));
            int best = leg; double bestT = 0, bestD = double.PositiveInfinity;
            for (int i = leg; i < path.Length - 1; i++)
            {
                double t = Project(path[i], path[i + 1], at), d = RoutePoint.Distance(at, Along(path[i], path[i + 1], t));
                if (d < bestD - 1e-6) { bestD = d; best = i; bestT = t; }
            }
            leg = best; nearest = Along(path[best], path[best + 1], bestT); offWay = bestD;
            double left = lookahead; var from = nearest; int j = best;
            while (true)
            {
                double rest = RoutePoint.Distance(from, path[j + 1]);
                if (rest >= left) { target = Along(from, path[j + 1], rest > 1e-9 ? left / rest : 0); break; }
                left -= rest; from = path[j + 1]; j++;
                if (j >= path.Length - 1) { target = path[path.Length - 1]; break; }
            }
            double toEnd = RoutePoint.Distance(nearest, path[best + 1]);
            for (int i = best + 1; i < path.Length - 1; i++) toEnd += RoutePoint.Distance(path[i], path[i + 1]);
            return toEnd < lookahead;
        }

        private static double Project(RoutePoint a, RoutePoint b, RoutePoint p)
        {
            double lx = b.x - a.x, lz = b.z - a.z, l2 = lx * lx + lz * lz;
            return l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((p.x - a.x) * lx + (p.z - a.z) * lz) / l2));
        }

        private static RoutePoint Along(RoutePoint a, RoutePoint b, double t) => new RoutePoint((float)(a.x + (b.x - a.x) * t), (float)(a.z + (b.z - a.z) * t));

        // A way's leg from where the tow starts (`start`): her own clearance for its first 15 m, the wide one beyond; any other
        // leg the wide one.
        internal static bool TowLegClear(RoutePoint start, RoutePoint a, RoutePoint b, Func<RoutePoint, RoutePoint, bool> clear,
            Func<RoutePoint, RoutePoint, bool> wideClear)
        {
            if (RoutePoint.Distance(a, start) > 0.5) return wideClear(a, b);
            double d = RoutePoint.Distance(a, b);
            if (d <= TowNear) return clear(a, b);
            double t = TowNear / d;
            var near = new RoutePoint((float)(a.x + (b.x - a.x) * t), (float)(a.z + (b.z - a.z) * t));
            return clear(a, near) && wideClear(near, b);
        }

        // The angle between where the wind comes from and the dock (0: from the dock, offshore; 180: from open water, onshore).
        // dockSide: +1 the dock is on her starboard side, -1 on her port side.
        internal static double WindToDock(double heading, int dockSide, double windFrom)
        {
            if (dockSide != 1 && dockSide != -1) throw new ArgumentException("The dock is on one side.", nameof(dockSide));
            return Math.Abs(NavigationMath.Delta(heading + dockSide * 90, windFrom));
        }

        // Decision 181 (the user: "when unberth still tow her away from pier like 5m (like how a player will push the boat away from
        // pier)"): how far she is pushed off the dock before her sailor takes her. Decision 185 (the user, 2026-10-11 night, asked
        // whether 5 m is room enough with an onshore wind: "further away, make it 10 meter") made it 10 m in an onshore wind; decision
        // 189 (the user, 2026-10-11 night: "i thought unberth push become 10m instead of 5m"; asked, "10 m always") made it 10 m whatever
        // the wind. Decision 192 (the user, 2026-10-12, the 29 m Brig unable to turn in Dragon Cliffs' basin after 10 m: "change all to
        // push 1 ship size length for unberth"): her own hull length, every ship (PushOffLeast without a measured length).
        internal const double PushOffLeast = 10;
        internal static double PushOff(double hullLength) => NavigationMath.Finite(hullLength) && hullLength > 1 ? hullLength : PushOffLeast;

        // Decision 219 (the user, 2026-11-03: "the push should push that much since the release spot is not big, just push back to
        // release spot"): at the shipyard's own cleats (217) she is pushed off only until her middle `p` lies on the release spot's line
        // (through `spot` along its heading `spotYaw`), along `pushBearing`, at most her own length (PushOff). 0 when the push doesn't
        // take her toward that line (she lies on it or beyond it, or the push runs nearly along it).
        internal static double PushToSpot(RoutePoint p, double pushBearing, RoutePoint spot, double spotYaw, double hullLength)
        {
            double r = spotYaw * Math.PI / 180, b = pushBearing * Math.PI / 180;
            // Starboard of the spot's heading, and how far she lies that way; how much of the push goes that way.
            double nx = Math.Cos(r), nz = -Math.Sin(r);
            double across = (p.x - spot.x) * nx + (p.z - spot.z) * nz, toward = Math.Sin(b) * nx + Math.Cos(b) * nz;
            if (Math.Abs(toward) < 0.2) return 0;
            return NavigationMath.Clamp(-across / toward, 0, PushOff(hullLength));
        }

        // The first heading turned away from the dock (30 degrees, then by 15 up to 150) that `usable` accepts; NaN when none.
        internal static double EscapeHeading(double heading, int dockSide, Func<double, bool> usable)
        {
            if (dockSide != 1 && dockSide != -1) throw new ArgumentException("The dock is on one side.", nameof(dockSide));
            for (double turn = FirstTurn; turn <= LastTurn; turn += TurnStep)
            {
                double candidate = NavigationMath.Normalize(heading - dockSide * turn);
                if (usable(candidate)) return candidate;
            }
            return double.NaN;
        }

        // A heading she can sail: no closer to the wind than close-hauled (her no-go and TackNavigator.CloseMargin, decision 137).
        internal static bool Sailable(double candidate, double windFrom, double noGo, double margin = TackNavigator.CloseMargin) =>
            Math.Abs(NavigationMath.Delta(candidate, windFrom)) >= noGo + margin;

        // How far off the dock (the water between her hull and it) before she turns away: one beam and 3 m, and room for her
        // stern to swing in as her bow turns `turn` degrees out about her middle.
        internal static double OffGap(double radius, double aftExtent, double turn)
        {
            if (!NavigationMath.Finite(radius) || !NavigationMath.Finite(aftExtent) || radius <= 0 || aftExtent < 0) throw new ArgumentException("Invalid hull envelope.");
            double swing = aftExtent * Math.Sin(NavigationMath.Clamp(Math.Abs(turn), 0, 90) * Math.PI / 180) + 2;
            return Math.Max(2 * radius + 3, swing);
        }

        // Which side of her centre line the berth's cleats lie on (+1 starboard, -1 port); 0 when they don't tell.
        internal static int DockSide(double heading, RoutePoint centre, RoutePoint[] cleats, double reach = 40)
        {
            double sum = 0; int count = 0;
            double r = heading * Math.PI / 180;
            foreach (var cleat in cleats ?? Array.Empty<RoutePoint>())
            {
                double dx = cleat.x - centre.x, dz = cleat.z - centre.z;
                if (dx * dx + dz * dz > reach * reach) continue;
                sum += dx * Math.Cos(r) - dz * Math.Sin(r); count++;
            }
            if (count == 0 || Math.Abs(sum / count) < 0.5) return 0;
            return sum > 0 ? 1 : -1;
        }

        // Aground: the ground within 0.3 m under her keel at any of the probes (NaN: no ground found under it). For now only
        // reported (StuckReport): launch 5's runs P, R and S had her unmoved by sail and tow, and the low tide guessed then is
        // doubted by the user (2026-10-07: no shipyard port dries at low tide); decision 112 (wait for the tide) applies only
        // to a grounding shown so.
        internal const double AgroundGap = 0.3;
        internal static bool Aground(double[] gapsUnderKeel)
        {
            if (gapsUnderKeel == null) return false;
            foreach (double gap in gapsUnderKeel) if (NavigationMath.Finite(gap) && gap <= AgroundGap) return true;
            return false;
        }

        // The tow's offings, in the order tried: toward her next waypoint, then turned by 30 degrees either way up to astern.
        internal static readonly double[] OffingTurns = { 0, 30, -30, 60, -60, 90, -90, 120, -120, 150, -150, 180 };

        // The harbour tow (decision 111): an acceleration toward the tow's velocity, at most `strength` (m/s², TowStrength). From
        // rest it is the whole strength; it eases as she nears the tow's velocity, and checks her drift across it the same way.
        // Below 0.5 m/s of tow speed the gain stays that of 0.5 m/s, so drawing alongside doesn't jitter.
        internal static void Tow(double vx, double vz, double targetX, double targetZ, out double ax, out double az, double strength = TowStrengthStart)
        {
            if (!NavigationMath.Finite(vx) || !NavigationMath.Finite(vz) || !NavigationMath.Finite(targetX) || !NavigationMath.Finite(targetZ) || !(strength > 0))
                throw new ArgumentException("Invalid tow sample.");
            double gain = strength / Math.Max(0.5, Math.Sqrt(targetX * targetX + targetZ * targetZ));
            ax = gain * (targetX - vx); az = gain * (targetZ - vz);
            double size = Math.Sqrt(ax * ax + az * az);
            if (size > strength) { ax *= strength / size; az *= strength / size; }
        }
    }

    // ==== UpwindLimits ====

    // Decision 133 (the user, 2026-10-10 night: "set a minimal angle between ship and apparent wind direction and never turn there
    // unless you're going to tack"; "pick a wind amount, set for every sail, and use vanilla upwind efficiency to see which angle is
    // that for every sail"; the share 0.5 since decision 214): her least angle to the apparent wind, sail by sail and for her rig, and the
    // turns between the apparent and the true wind. Engine-free, so it is host-tested; Voyage reads each sail's data
    // (SailControlAdapters.UpwindLimit).

    internal static class UpwindLimits
    {
        // The share of the wind each sail must receive at her closest angle, at sea and in port. Decision 214 (the user, 2026-10-13
        // evening: "make other sail to 0.5 also, so code is cleaner"; it was 0.6 at sea, 133, and 0.5 in port, 177; 212 had lowered it
        // for square sails alone, after the timing lab's Cog made 3.2 m/s with every sail at 61 degrees apparent, inside her square
        // sail's 70 at 0.6): one least angle per sail. What stays of 177 is her close-hauled: in port right at her dead zone
        // (TackNavigator.PortCloseMargin), at sea CloseMargin outside it.
        internal const double WindShare = 0.5;
        // In port (decision 177): in her harbour's chart, docking, or within `harbour` metres of her origin or destination berth.
        // Decision 184 (c): once in port, out of it only PortBand further out, so a beat across that line doesn't flip her in and
        // out of port, each flip choosing her course again.
        internal const double PortBand = 100;
        internal static bool InPort(bool wasInPort, bool inChart, bool docking, double fromOrigin, double fromDestination, double harbour)
        {
            double reach = harbour + (wasInPort ? PortBand : 0);
            return inChart || docking || fromOrigin <= reach || fromDestination <= reach;
        }
        // The game's law (Sail.UpdateWindForceOnSail, SailForceMath.Captured): a sail receives lerp(upwindEfficiency, 1, attack/90)
        // of the wind, and nothing under 13 degrees of attack, all of it from FullDraw.
        internal const double FullDraw = 16;
        // Sails holding at least this share of her sail area set her rig's limit (a small jib doesn't hold back her mainsail).
        internal const double MainArea = 0.25;

        // A sail's least angle between her bow and the apparent wind: its closest sheeting angle off her centre line, and the
        // angle of attack at which it receives `share` of the wind (at least FullDraw).
        internal static double SailMinimum(double efficiency, double closestOff, double share = WindShare)
        {
            if (!NavigationMath.Finite(efficiency) || !NavigationMath.Finite(closestOff) || efficiency < 0 || efficiency > 1 || closestOff < 0 || closestOff > 90)
                throw new ArgumentException("Invalid sail data for its upwind limit.");
            double attack = efficiency >= share ? 0 : 90 * (share - efficiency) / (1 - efficiency);
            return Math.Min(179, closestOff + Math.Max(FullDraw, Math.Min(90, attack)));
        }

        // Her rig's: the widest among the sails holding MainArea of her sail area or more (all her sails when none does).
        internal static double RigMinimum(IList<KeyValuePair<double, double>> areaAndMinimum)
        {
            if (areaAndMinimum == null || areaAndMinimum.Count == 0) throw new ArgumentException("A rig needs sails for its upwind limit.");
            double total = areaAndMinimum.Sum(s => Math.Max(0, s.Key));
            var main = areaAndMinimum.Where(s => total <= 0 || s.Key >= MainArea * total).ToList();
            return (main.Count > 0 ? main : areaAndMinimum.ToList()).Max(s => s.Value);
        }

        // Decision 153 (the user, 2026-10-11): "for square sail rig, if it's other staysail can sail better angle, furl it's square sail
        // and only use square sail when the angle of the wind is good enough for it to use"; "you need to check VMG, if sailing at
        // bigger angle using all square is faster than only using staysail to sail closer to wind angle, still use square"; "this mod
        // should scan the ship it will sail first, read all it's sail and angle it can reach, then decide how to manage the sail for
        // it, because there will be custom sail plan from player and you need to adapt to it". Every sail's least angle (her set-up
        // scan, SailControlAdapters.UpwindLimit) is a limit she may sail at: at limit m the sails whose own is m or less draw, the
        // others can't. She takes the limit with the most way made good toward her guide point (ChooseLimit), and each sail outside
        // it is set only while the wind is outside its own close-hauled (Draws).
        internal const double ModeHysteresis = 0.1, SetAgain = 5;

        internal struct SailLimit
        {
            internal double Area, Minimum;
            internal SailLimit(double area, double minimum) { Area = area; Minimum = minimum; }
        }

        // The sail area drawing at limit `limit`: every sail whose own least angle is that or less.
        internal static double AreaWithin(IList<SailLimit> sails, double limit) => sails.Where(s => s.Minimum <= limit + 1e-6).Sum(s => Math.Max(0, s.Area));

        // Decision 210 (the user, 2026-10-13: "Time cog/brig sailing to check, don't make up formula, use actual data to make formula"):
        // her speed with a sail set, from the timing lab's in-game runs (the Cog and the Brig held at sea at each angle off the wind with
        // each of their sail sets, runs 20261013-134942, -140427 and -151535, the draft's 210): the area drawing to the power AreaPower (the
        // sets' speeds paired at like angles and winds, scaled by the wind to the power 0.3: 0.23-0.56 by pair, their median 0.36; it was the square
        // root), times SpeedShare of her apparent angle beyond the set's own least angle (linear between the measured points; full from
        // 80 degrees beyond, the same dead downwind for fore-and-aft sails; it was TackNavigator.Polar's assumed 0.55 at the edge, full
        // from 30 beyond, a quarter less downwind). Claude's fit. Fitted again for decision 214 (every set's least angle at 0.5 of the wind,
        // 12-15 degrees lower: the Cog's jib 16, her square sail 57; the Brig's jib 14, gaff 16, square sails 72 and 77): 0.63 at it, 0.67 at
        // 10 beyond, 0.75 at 25, 0.79 at 45, 0.86 at 60, all from 80 (at 0.6 it was 0.65, 0.72, 0.82, 0.88, 0.93); the area power, from
        // speeds paired at like angles, is the same.
        internal const double AreaPower = 0.35;
        private static readonly double[] BeyondAt = { 0, 10, 25, 45, 60, 80 }, ShareAt = { 0.63, 0.67, 0.75, 0.79, 0.86, 1.0 };
        internal static double SpeedShare(double off, double minimum)
        {
            double beyond = Math.Abs(off) - minimum;
            if (beyond < 0) return 0;
            for (int i = 1; i < BeyondAt.Length; i++)
                if (beyond <= BeyondAt[i]) return ShareAt[i - 1] + (ShareAt[i] - ShareAt[i - 1]) * (beyond - BeyondAt[i - 1]) / (BeyondAt[i] - BeyondAt[i - 1]);
            return ShareAt[ShareAt.Length - 1];
        }
        internal static double SetSpeed(double area, double off, double minimum) => Math.Pow(Math.Max(0, area), AreaPower) * SpeedShare(off, minimum);

        // Her way made good toward her guide point (`guideOff`: its bearing's angle off the apparent wind) at limit `minimum` with
        // `area` drawing: straight at it when she can, else close-hauled (TackNavigator.BeatOff), at SetSpeed there.
        internal static double ModeVmg(double guideOff, double minimum, double area, double margin = TackNavigator.CloseMargin)
        {
            if (!NavigationMath.Finite(guideOff) || !NavigationMath.Finite(minimum) || !NavigationMath.Finite(area)) throw new ArgumentException("Invalid sail mode data.");
            double g = Math.Min(180, Math.Abs(guideOff)), off = Math.Max(g, TackNavigator.BeatOff(minimum, margin));
            return SetSpeed(area, off, minimum) * Math.Cos((off - g) * Math.PI / 180);
        }

        // Her best way made good toward her guide point at limit `minimum` with `area` drawing, among the headings she can sail
        // there whose way is clear (the user, 2026-10-11: "you cannot pick best VMG inside port unless you consider the terrain
        // also"): straight at it, and every TerrainStep degrees from close-hauled round to dead before the wind on either side;
        // `clear` (null: open water) takes a heading as its angle off the apparent wind (+ to starboard, as `guideOff`). The best
        // first, the first clear one taken; none clear: no way at all (negative infinity). `side` (+1 the wind on her starboard side
        // of the bow's angle, as `guideOff`'s sign; -1 port; 0 either): only headings on that side of the wind.
        internal const double TerrainStep = 10;

        internal static double BestVmg(double guideOff, double minimum, double area, Func<double, bool> clear, int side = 0, double margin = TackNavigator.CloseMargin) =>
            BestVmg(guideOff, minimum, area, clear, out _, side, margin);
        // The same, and `heading`: the clear heading it is made on (its angle off the apparent wind; NaN when none).
        internal static double BestVmg(double guideOff, double minimum, double area, Func<double, bool> clear, out double heading, int side = 0, double margin = TackNavigator.CloseMargin)
        {
            heading = double.NaN;
            if (!NavigationMath.Finite(guideOff) || !NavigationMath.Finite(minimum) || !NavigationMath.Finite(area)) throw new ArgumentException("Invalid sail mode data.");
            double close = TackNavigator.BeatOff(minimum, margin), g = NavigationMath.Delta(0, guideOff);
            var headings = new List<double>();
            if (Math.Abs(g) >= close) headings.Add(g);
            foreach (int s in new[] { 1, -1 })
                for (double a = close; a <= 180 + 1e-9; a += TerrainStep) headings.Add(s * Math.Min(a, 180));
            foreach (var off in headings.Where(h => side == 0 || h * side > 0).Select(h => new { h, v = SetSpeed(area, Math.Abs(h), minimum) * Math.Cos(NavigationMath.Delta(h, g) * Math.PI / 180) })
                .OrderByDescending(x => x.v))
                if (clear == null || clear(off.h)) { heading = off.h; return off.v; }
            return double.NegativeInfinity;
        }

        // Whether at limit `minimum` she can sail straight at her guide point (`guideOff` off the apparent wind) and its way is clear.
        internal static bool Lays(double guideOff, double minimum, Func<double, bool> clear, double margin)
        {
            if (!NavigationMath.Finite(guideOff) || !NavigationMath.Finite(minimum)) return false;
            double g = NavigationMath.Delta(0, guideOff);
            return Math.Abs(g) >= TackNavigator.BeatOff(minimum, margin) && (clear == null || clear(g));
        }

        // The limit to sail at: of her sails' least angles, the one with the most way made good among clear headings; `current`
        // (NaN: none yet) kept unless another makes ModeHysteresis more, or it has no clear heading at all.
        // `side` (her bow's side of the apparent wind, as BestVmg's; 0 either): another limit is judged only by headings on her
        // side, so a change never asks for a tack or a wear (decision 131: "you should tack as less as possible"; run
        // 20261011-114147: just after her tack out of Dragon Cliffs the Cog took every sail, whose course lay across the wind on her
        // other side, and had no room to wear). Her current limit is judged on either side (she keeps it); left because it has no
        // clear heading, or with no limit clear on her side, the choice is made afresh on either side.
        // Decision 190 (the user, 2026-10-11 night, the Cog boxed in Dragon Cliffs' dock basin on every sail: "don't have to force use
        // square, if only staysail can get you out, you should use that, espicially in tight space"): in port (`inPort`) the sails
        // that let her sail straight at her guide point, its way clear, come first (the most way made good among them); when none
        // can, the closest-winded limit's best clear heading (on her side, else either), with the most sail that can sail it: on a beat
        // her closest-winded sails alone, pointing highest in the least room. At sea as above.
        internal static double ChooseLimit(IList<SailLimit> sails, double guideOff, double current, Func<double, bool> clear = null, int side = 0, double margin = TackNavigator.CloseMargin, bool inPort = false)
        {
            if (sails == null || sails.Count == 0) throw new ArgumentException("A rig needs sails for its upwind limit.");
            var limits = sails.Select(s => s.Minimum).Distinct().OrderBy(m => m).ToList();
            if (inPort)
            {
                double g = NavigationMath.Delta(0, guideOff);
                var laying = limits.Where(m => Lays(g, m, clear, margin)).ToList();
                if (laying.Count > 0)
                {
                    double pick = double.NaN, pickVmg = double.NegativeInfinity;
                    foreach (double m in laying)
                    {
                        double v = ModeVmg(g, m, AreaWithin(sails, m), margin);
                        if (NavigationMath.Finite(current) && Math.Abs(m - current) < 1e-6) v *= 1 + ModeHysteresis;
                        if (v > pickVmg + 1e-9) { pick = m; pickVmg = v; }
                    }
                    return pick;
                }
                // None lays it: the closest-winded limit's best clear heading, sailed with the most sail that can sail it (her square
                // sails too where that heading is outside their close-hauled).
                foreach (int s in side != 0 ? new[] { side, 0 } : new[] { 0 })
                    foreach (double m in limits)
                    {
                        if (double.IsNegativeInfinity(BestVmg(guideOff, m, AreaWithin(sails, m), clear, out double h, s, margin))) continue;
                        return limits.Where(w => TackNavigator.BeatOff(w, margin) <= Math.Abs(h) + 1e-6).Max();
                    }
                return limits[limits.Count - 1];
            }
            double best = double.NaN, bestVmg = double.NegativeInfinity;
            bool hasCurrent = NavigationMath.Finite(current) && limits.Any(m => Math.Abs(m - current) < 1e-6);
            if (hasCurrent)
            {
                double v = BestVmg(guideOff, current, AreaWithin(sails, current), clear, 0, margin);
                if (!double.IsNegativeInfinity(v)) { best = current; bestVmg = v >= 0 ? v * (1 + ModeHysteresis) : v / (1 + ModeHysteresis); }
                else side = 0;
            }
            foreach (double m in limits)
            {
                if (NavigationMath.Finite(best) && Math.Abs(m - best) < 1e-6) continue;
                double v = BestVmg(guideOff, m, AreaWithin(sails, m), clear, side, margin);
                if (v > bestVmg + 1e-9) { best = m; bestVmg = v; }
            }
            if (!NavigationMath.Finite(best) && side != 0) return ChooseLimit(sails, guideOff, double.NaN, clear, 0, margin);
            // No limit has a clear heading: the widest, with every sail.
            return NavigationMath.Finite(best) ? best : limits[limits.Count - 1];
        }
        // Whether a sail whose own least angle is `minimum` is set with the apparent wind `apparentOff` off her bow, sailing at
        // `limit`: always within the limit; else while the wind is outside its close-hauled (TackNavigator.BeatOff), SetAgain
        // further before it is set again once furled.
        internal static bool Draws(double apparentOff, double minimum, double limit, bool setNow, double margin = TackNavigator.CloseMargin) =>
            minimum <= limit + 1e-6 || Math.Abs(apparentOff) >= TackNavigator.BeatOff(minimum, margin) + (setNow ? 0 : SetAgain);
        // The apparent wind's angle off her bow for the true wind's, her moving `speed` along her heading in a `wind` (m/s).
        internal static double ApparentAngle(double trueAngle, double wind, double speed)
        {
            double a = trueAngle * Math.PI / 180;
            return Math.Abs(Math.Atan2(wind * Math.Sin(a), wind * Math.Cos(a) + Math.Max(0, speed)) * 180 / Math.PI);
        }

        // The true wind's angle off her bow at which the apparent wind is `apparent` off it (w sin(true - apparent) = v sin(apparent)).
        internal static double TrueAngle(double apparent, double wind, double speed)
        {
            if (!NavigationMath.Finite(apparent) || !NavigationMath.Finite(wind) || !NavigationMath.Finite(speed)) throw new ArgumentException("Non-finite wind sample.");
            if (wind < 0.1) return apparent;
            double a = apparent * Math.PI / 180, s = Math.Max(0, speed) * Math.Sin(a) / wind;
            return Math.Min(179, apparent + (s >= 1 ? 90 : Math.Asin(s) * 180 / Math.PI));
        }

        // The steering's guard: a demanded heading closer to the apparent wind than `minimum` is put on the edge of that zone, on
        // the side her bow is on.
        internal static double Guard(double demand, double heading, double apparentFrom, double minimum)
        {
            double off = NavigationMath.Delta(apparentFrom, demand);
            if (Math.Abs(off) >= minimum) return NavigationMath.Normalize(demand);
            int side = NavigationMath.Delta(apparentFrom, heading) < 0 ? -1 : 1;
            return NavigationMath.Normalize(apparentFrom + side * minimum);
        }
    }

    // ==== WaterField ====

    // Decision 157 (the user, 2026-10-11, watching the Cog leave Dragon Cliffs toward the wrong side of its island: "can't you not just
    // use terrain detection system to sail out to the exit, instead of mapping checkpoint like this?"; on the proposal: "Build it"):
    // the harbour charted by her own terrain detection (each cell's seabed and her hull's clearance, NavigationWater.WaterCell) and
    // flooded from the open sea at its edge, each edge cell starting at its straight distance on to her destination: every water cell
    // then knows how far she has to go by water, through whichever way out serves her destination best. No checkpoints in the harbour:
    // her guide is the farthest point down that water distance she can reach in a straight, clear line (VoyageNavigation's harbour
    // chart). Engine-free, so it is host-tested.
    // Decision 198 (g) (depth) and (h) (her way in): each cell keeps two things her chart judges by: whether her hull fits there above the
    // water (nothing fixed in her beam at the sea's surface, no land), which no tide changes, and its seabed's top, the shallowest of its
    // probes. Each flood judges the depth at the sea level it is given (ChartDepth): the tide changes only that, and a flood again. A flood
    // starts from the open sea at the chart's edge (her way out), or from her goal (her way in to her approach point, 146).

    // Decision 198 (g): the depth a flood judges her chart by: the sea's surface (stable y), her keel's depth under it, the least water
    // under her keel (the hard limit: her keel and NavigationWater.KeelMargin, decision 158), and the water wanted under it, under which a
    // step costs more, up to Weight more at the hard limit (the user, 2026-10-12: "a no-go margin as a cost, never a bar").
    internal struct ChartDepth
    {
        internal readonly double Sea, Keel, Least, Want, Weight;
        internal readonly bool Known;
        internal ChartDepth(double sea, double keel, double least, double want, double weight)
        {
            if (!NavigationMath.Finite(sea) || !NavigationMath.Finite(keel) || keel < 0 || !(least >= 0) || !(want >= least) || !(weight >= 0))
                throw new ArgumentException("Invalid chart depth.");
            Sea = sea; Keel = keel; Least = least; Want = want; Weight = weight; Known = true;
        }
        // The highest a seabed may lie for her to pass over it (stable y).
        internal double Floor => Sea - Keel - Least;
        // The water under her keel over a seabed whose top is `top`.
        internal double Under(double top) => Sea - Keel - top;
        // A seabed whose top is `top` deep enough for her (unknown depth, or none met: deep enough).
        internal bool Deep(double top) => !Known || !(top > Floor);
        // The sea level at which a seabed whose top is `top` is deep enough for her.
        internal double SeaFor(double top) => top + Keel + Least;
    }

    internal sealed class WaterField
    {
        internal readonly double OriginX, OriginZ, Cell;
        internal readonly int Width, Height;
        // Her hull fits there above the water (decision 198 g: no tide changes it); the seabed's top there (stable y, the shallowest of its
        // probes; negative infinity where none was met); and whether the cell is water for her at the depth the flood judged by (fits and
        // deep enough), which her line and guide go by.
        private readonly bool[] fits;
        private readonly float[] top;
        private bool[] water;
        // Decision 201 (e) (4 m cells, four times as many: Gold Rock City's chart about 75 MB with doubles and a fresh set each flood): her
        // water distances and clearances held as float (a distance is at most about 100 km of costed water, a float's step there under 0.01
        // m; each step costs a cell or more), and every buffer of the flood kept for the next (a reflood allocates nothing: the game's Mono
        // collector stops the world, and rarely gives memory back).
        private float[] distance;
        internal bool Flooded { get; private set; }

        internal WaterField(double centreX, double centreZ, double half, double cell)
        {
            if (!NavigationMath.Finite(centreX) || !NavigationMath.Finite(centreZ) || !(half > 0) || !(cell > 0) || half / cell > 1000)
                throw new ArgumentException("Invalid water field.");
            Cell = cell;
            Width = Height = (int)Math.Ceiling(2 * half / cell);
            OriginX = centreX - Width * cell / 2; OriginZ = centreZ - Height * cell / 2;
            water = new bool[Width * Height]; fits = new bool[Width * Height]; top = new float[Width * Height]; distance = new float[Width * Height];
            for (int i = 0; i < distance.Length; i++) { distance[i] = float.PositiveInfinity; top[i] = float.NegativeInfinity; }
        }

        // Decision 201 (e): a reach the chart's cells were counted in (8 m cells), now in metres: so many cells at this chart's size.
        internal int Cells(double metres) => Math.Max(1, (int)Math.Ceiling(metres / Cell - 1e-9));

        internal int Count => Width * Height;
        internal RoutePoint Centre(int i) => new RoutePoint((float)(OriginX + (i % Width + 0.5) * Cell), (float)(OriginZ + (i / Width + 0.5) * Cell));
        // A cell her hull fits (or not) whatever its depth; it is water for her once a flood judges its depth.
        internal void SetWater(int i, bool isWater) { water[i] = fits[i] = isWater; }
        // Decision 198 (g): a charted cell: whether her hull fits there above the water, and its seabed's top (stable y).
        internal void SetCell(int i, bool hullFits, float seabedTop)
        {
            fits[i] = hullFits; top[i] = float.IsNaN(seabedTop) ? float.NegativeInfinity : seabedTop;
            water[i] = hullFits && depth.Deep(top[i]);
        }
        internal bool Fits(int i) => fits[i];
        internal float Top(int i) => top[i];
        // Decision 195: the water cells within `radius` of `p` closed (a way she failed by); how many. Decision 201 (b): none within
        // `keepRadius` of `keep` (her approach point stays open).
        internal int CloseAround(RoutePoint p, double radius, RoutePoint keep = default(RoutePoint), double keepRadius = 0)
        {
            int closed = 0, reach = (int)Math.Ceiling(radius / Cell) + 1, x0 = (int)Math.Floor((p.x - OriginX) / Cell), z0 = (int)Math.Floor((p.z - OriginZ) / Cell);
            for (int z = Math.Max(0, z0 - reach); z <= Math.Min(Height - 1, z0 + reach); z++)
                for (int x = Math.Max(0, x0 - reach); x <= Math.Min(Width - 1, x0 + reach); x++)
                {
                    int i = z * Width + x;
                    if (!water[i] || RoutePoint.Distance(p, Centre(i)) > radius) continue;
                    if (keepRadius > 0 && RoutePoint.Distance(keep, Centre(i)) <= keepRadius) continue;
                    water[i] = fits[i] = false; closed++;
                }
            return closed;
        }

        // Decision 195 (b) leaving, and 201 (b) arriving: her chart's way down from `from` closed: the water cells within `radius` of each
        // of its cells lying `nearest` to `farthest` metres from `from` (none within `keepRadius` of `keep`); how many.
        internal int CloseWay(RoutePoint from, double nearest, double farthest, double radius, RoutePoint keep = default(RoutePoint), double keepRadius = 0)
        {
            int closed = 0;
            foreach (var c in Descent(from, farthest + Cell))
            {
                double d = RoutePoint.Distance(from, c);
                if (d >= nearest && d <= farthest) closed += CloseAround(c, radius, keep, keepRadius);
            }
            return closed;
        }
        internal bool Water(int i) => water[i];
        internal bool OnEdge(int i) { int x = i % Width, z = i / Width; return x == 0 || z == 0 || x == Width - 1 || z == Height - 1; }

        internal int IndexOf(RoutePoint p)
        {
            int x = (int)Math.Floor((p.x - OriginX) / Cell), z = (int)Math.Floor((p.z - OriginZ) / Cell);
            return x < 0 || z < 0 || x >= Width || z >= Height ? -1 : z * Width + x;
        }

        internal bool Inside(RoutePoint p, double margin = 0) =>
            p.x >= OriginX + margin && p.z >= OriginZ + margin && p.x <= OriginX + Width * Cell - margin && p.z <= OriginZ + Height * Cell - margin;

        // The flood's open cells, least first: the key, then the cell's index on a tie (the order the SortedSet these replaced kept,
        // so the cells settle in the same order). A cell can be in it more than once; a stale entry pops with a key above the cell's
        // distance and is skipped. An array heap: the SortedSet's tree allocated a node per entry, and the flood over Dragon Cliffs'
        // 432 by 432 cells took 336 ms in one frame (run 20261011-223323's set-out, the navigation's longest step 383 ms). Decision 201 (e):
        // float keys (a cell's distance as stored, so a stale entry is told by an exact comparison), and kept from flood to flood (Clear).
        private sealed class CellHeap
        {
            private float[] keys; private int[] cells; private int count;
            internal CellHeap(int capacity) { keys = new float[Math.Max(16, capacity)]; cells = new int[keys.Length]; }
            internal int Count => count;
            internal void Clear() => count = 0;
            private bool Less(int a, int b) => keys[a] < keys[b] || (keys[a] == keys[b] && cells[a] < cells[b]);
            private void Swap(int a, int b) { float k = keys[a]; keys[a] = keys[b]; keys[b] = k; int c = cells[a]; cells[a] = cells[b]; cells[b] = c; }
            internal void Add(float key, int cell)
            {
                if (count == keys.Length) { Array.Resize(ref keys, count * 2); Array.Resize(ref cells, count * 2); }
                keys[count] = key; cells[count] = cell;
                for (int i = count++; i > 0;) { int up = (i - 1) / 2; if (!Less(i, up)) break; Swap(i, up); i = up; }
            }
            internal void Pop(out float key, out int cell)
            {
                key = keys[0]; cell = cells[0];
                if (--count == 0) return;
                keys[0] = keys[count]; cells[0] = cells[count];
                for (int i = 0; ;)
                {
                    int l = 2 * i + 1, r = l + 1, least = i;
                    if (l < count && Less(l, least)) least = l;
                    if (r < count && Less(r, least)) least = r;
                    if (least == i) break;
                    Swap(i, least); i = least;
                }
            }
        }

        // Each water cell's clearance: its distance to the nearest cell her hull doesn't fit (beyond the chart's edge counts as water).
        private float[] clearance;
        internal double ClearanceAt(int i) => clearance == null || i < 0 ? double.PositiveInfinity : clearance[i];

        // From every water cell on the edge, starting at `onward(its centre)` (its straight distance on to her destination), through
        // water cells, 8 neighbours (a diagonal only where both cells beside it are water: no corner cut through a cliff).
        // The middle of the waterway (the user, 2026-10-11: "favor the one in the middle of waterway(stay furthur away from terrain) so
        // if wind is strong, it won't hit into terrain"): a step into a cell nearer than `want` metres to what her hull doesn't fit
        // costs up to `weight` times more (at the wall), falling to nothing at `want`.
        private double floodWant, floodWeight;
        // Decision 163 (the user, 2026-10-11: "depend on wind, boat choose to go through one of it"; "Count it 3x as far"): a step she
        // would sail within `windBeat` degrees of the true wind (`windFrom`) is a beat and costs BeatFactor times its length, or
        // NarrowBeatFactor where the water's clearance is under `narrowClearance` (a channel under about 3 of her lengths wide, too
        // narrow to tack in); reaching and running cost their length. NaN: no wind counted.
        internal const double BeatFactor = 1.4, NarrowBeatFactor = 3;
        private double windFrom = double.NaN, windBeat, narrowClearance;
        // Decision 193 (amended 2026-10-12): `narrowFactor`, a beat's cost where too narrow to tack in (NarrowBeatFactor when 0).
        private double narrowBeatFactor = NarrowBeatFactor;
        internal void SetWind(double from, double beat, double narrow, double narrowFactor = 0)
        {
            if (!NavigationMath.Finite(from) || !NavigationMath.Finite(beat) || !NavigationMath.Finite(narrow) || beat <= 0 || beat >= 90 || narrow < 0)
                throw new ArgumentException("Invalid chart wind.");
            windFrom = NavigationMath.Normalize(from); windBeat = beat; narrowClearance = narrow;
            narrowBeatFactor = narrowFactor > 0 ? narrowFactor : NarrowBeatFactor;
        }
        internal double WindFrom => windFrom;
        // The wind's factor for a step she sails on `heading` from a cell of clearance `clear`.
        internal double WindFactor(double heading, double clear) => WindFactor(Beats(heading), clear);
        private double WindFactor(bool beat, double clear) => !beat ? 1 : clear < narrowClearance ? narrowBeatFactor : BeatFactor;
        private bool Beats(double heading) => !double.IsNaN(windFrom) && Math.Abs(NavigationMath.Delta(windFrom, heading)) < windBeat;
        // A step's cost from a cell, as the flood counts it: its length, more near what her hull doesn't fit, more where the water under
        // her keel is shallow (decision 198 g), more into the wind (decision 163; `heading`: the way she sails it, from `from` toward the
        // sea or her goal).
        private double StepCost(int from, bool diagonal, double heading) => StepCost(from, diagonal, Beats(heading));
        private double StepCost(int from, bool diagonal, bool beat) => StepCost(clearance, floodWant, floodWeight, shallowOn ? shallowFactor : null, from, diagonal, beat);
        private double StepCost(float[] clear, double want, double weight, float[] shallow, int from, bool diagonal, bool beat)
        {
            double step = diagonal ? Cell * Math.Sqrt(2) : Cell;
            if (want > 0 && weight > 0) step *= 1 + weight * Math.Max(0, want - clear[from]) / want;
            if (shallow != null) step *= shallow[from];
            return step * WindFactor(beat, clear[from]);
        }

        // Decision 198 (g): the depth the last flood judged by (none: every cell her hull fits is water), and each cell's cost for the
        // water under her keel there (1 with her depth wanted or more; 1 + Weight at the hard limit; null: none counted).
        private ChartDepth depth;
        private float[] shallowFactor;
        private bool shallowOn;
        internal ChartDepth Depth => depth;
        // Into `factors` (made once, decision 201 e); false when no depth is counted.
        private static bool ShallowFactors(float[] top, ChartDepth d, ref float[] factors)
        {
            if (!d.Known || !(d.Weight > 0) || !(d.Want > 0)) return false;
            if (factors == null) factors = new float[top.Length];
            for (int i = 0; i < top.Length; i++)
            {
                double under = d.Under(top[i]);
                factors[i] = (float)(under >= d.Want ? 1 : 1 + d.Weight * Math.Min(1, (d.Want - Math.Max(0, under)) / d.Want));
            }
            return true;
        }

        // Decision 198 (j) (a flood took 73-155 ms in one frame, with a reflood 215 ms): the flood a slice at a time, FloodBegin and then
        // FloodStep until it is done; its water, clearances and distances in arrays of their own, put in place at the end, so her chart's
        // own serve her until then. Flood runs it whole. First which cells are water at its depth, then the clearances (from the cells
        // that aren't), then the distances (from the water on the edge, or from her goal). Decision 201 (e): at the end the two sets swap,
        // the old one kept for the next flood.
        private float[] nextClearance, nextDistance;
        private bool[] nextWater;
        private float[] nextShallow;
        private bool nextShallowOn;
        private ChartDepth nextDepth;
        private CellHeap work;
        private int floodPhase;
        private Func<RoutePoint, double> floodOnward;
        private double nextWant, nextWeight;
        // Decision 198 (h): a flood from her goal (her approach point) instead of the open sea; the cells within GoalReach metres of it (2
        // cells of 8 m before decision 201 e) start at their straight distance to it. FloodGoal: the last flood's goal, when it had one.
        internal const double GoalReach = 16;
        private bool nextHasGoal, hasGoal;
        private RoutePoint nextGoal, goal;
        internal bool HasGoal => hasGoal;
        internal RoutePoint FloodGoal => goal;
        // Whether each of the 8 steps (by dx, dz) is a beat: only 8 headings, so not reckoned for every cell's every step.
        private readonly bool[] stepBeats = new bool[9];
        internal bool Flooding => floodPhase != 0;

        internal void Flood(Func<RoutePoint, double> onward, double want = 0, double weight = 0, ChartDepth judged = default)
        {
            FloodBegin(onward, want, weight, judged);
            while (!FloodStep(double.PositiveInfinity)) { }
        }

        // Her way out: from the open sea at the edge, each edge cell at `onward(its centre)`.
        internal void FloodBegin(Func<RoutePoint, double> onward, double want = 0, double weight = 0, ChartDepth judged = default)
        {
            floodOnward = onward ?? throw new ArgumentNullException(nameof(onward));
            nextHasGoal = false;
            Begin(want, weight, judged);
        }

        // Decision 198 (h): her way in, from her goal.
        internal void FloodToBegin(RoutePoint to, double want = 0, double weight = 0, ChartDepth judged = default)
        {
            if (!RoutePlanner.Finite(to)) throw new ArgumentException("Invalid flood goal.", nameof(to));
            floodOnward = c => RoutePoint.Distance(c, to);
            nextHasGoal = true; nextGoal = to;
            Begin(want, weight, judged);
        }

        internal void FloodTo(RoutePoint to, double want = 0, double weight = 0, ChartDepth judged = default)
        {
            FloodToBegin(to, want, weight, judged);
            while (!FloodStep(double.PositiveInfinity)) { }
        }

        private void Begin(double want, double weight, ChartDepth judged)
        {
            nextWant = want; nextWeight = weight; nextDepth = judged;
            if (nextWater == null) nextWater = new bool[Count];
            for (int i = 0; i < Count; i++) nextWater[i] = fits[i] && judged.Deep(top[i]);
            nextShallowOn = ShallowFactors(top, judged, ref nextShallow);
            if (nextClearance == null) nextClearance = new float[Count];
            if (work == null) work = new CellHeap(Count); else work.Clear();
            for (int i = 0; i < Count; i++)
            {
                nextClearance[i] = nextWater[i] ? float.PositiveInfinity : 0f;
                if (!nextWater[i]) work.Add(0f, i);
            }
            floodPhase = 1;
        }

        private static void Swap<T>(ref T a, ref T b) { T t = a; a = b; b = t; }

        // Up to `budgetMs` of it; true once done (its distances in place).
        internal bool FloodStep(double budgetMs)
        {
            if (floodPhase == 0) return true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int n = 1; ; n++)
            {
                if ((n & 1023) == 0 && clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
                if (work.Count == 0)
                {
                    if (floodPhase == 1) { StartDistances(); continue; }
                    // Decision 201 (e): the flood's set in place, her chart's old one kept for the next flood.
                    Swap(ref water, ref nextWater); Swap(ref clearance, ref nextClearance); Swap(ref distance, ref nextDistance); Swap(ref shallowFactor, ref nextShallow);
                    shallowOn = nextShallowOn; floodWant = nextWant; floodWeight = nextWeight; depth = nextDepth; hasGoal = nextHasGoal; goal = nextGoal;
                    floodOnward = null; floodPhase = 0;
                    Flooded = true;
                    return true;
                }
                work.Pop(out float d, out int i);
                int x = i % Width, z = i / Width;
                if (floodPhase == 1)
                {
                    if (d > nextClearance[i]) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= Width || nz >= Height) continue;
                            int j = nz * Width + nx;
                            float nd = (float)(d + Cell * (dx != 0 && dz != 0 ? Math.Sqrt(2) : 1));
                            if (nd < nextClearance[j]) { nextClearance[j] = nd; work.Add(nd, j); }
                        }
                }
                else
                {
                    if (d > nextDistance[i]) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= Width || nz >= Height) continue;
                            int j = nz * Width + nx;
                            if (!nextWater[j]) continue;
                            if (dx != 0 && dz != 0 && (!nextWater[z * Width + nx] || !nextWater[nz * Width + x])) continue;
                            // She sails it from j toward i (the sea, or her goal).
                            float nd = (float)(d + StepCost(nextClearance, nextWant, nextWeight, nextShallowOn ? nextShallow : null, j, dx != 0 && dz != 0, stepBeats[(dz + 1) * 3 + dx + 1]));
                            if (nd >= nextDistance[j]) continue;
                            nextDistance[j] = nd; work.Add(nd, j);
                        }
                }
            }
        }

        private void StartDistances()
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    stepBeats[(dz + 1) * 3 + dx + 1] = (dx != 0 || dz != 0) && Beats(NavigationMath.Heading(-dx, -dz));
            if (nextDistance == null) nextDistance = new float[Count];
            work.Clear();
            for (int i = 0; i < Count; i++) nextDistance[i] = float.PositiveInfinity;
            foreach (int i in Seeds(nextHasGoal, nextGoal, nextWater))
            {
                double d = Math.Max(0, floodOnward(Centre(i)));
                if (!NavigationMath.Finite(d)) continue;
                nextDistance[i] = (float)d; work.Add(nextDistance[i], i);
            }
            floodPhase = 2;
        }

        // Where a flood starts: the water on the edge (the open sea), or the water within GoalReach metres of its goal (the cells round
        // the goal's own, so many each way).
        private IEnumerable<int> Seeds(bool toGoal, RoutePoint at, bool[] wet)
        {
            if (!toGoal)
            {
                for (int i = 0; i < Count; i++) if (wet[i] && OnEdge(i)) yield return i;
                yield break;
            }
            int x0 = (int)Math.Floor((at.x - OriginX) / Cell), z0 = (int)Math.Floor((at.z - OriginZ) / Cell), reach = Cells(GoalReach);
            for (int z = Math.Max(0, z0 - reach); z <= Math.Min(Height - 1, z0 + reach); z++)
                for (int x = Math.Max(0, x0 - reach); x <= Math.Min(Width - 1, x0 + reach); x++)
                    if (wet[z * Width + x]) yield return z * Width + x;
        }

        // Decision 198 (g): whether a cell is water for her at a depth.
        internal bool WaterAt(int i, ChartDepth judged) => fits[i] && judged.Deep(top[i]);

        // How many cells her hull fits would change their verdict at `judged` from the last flood's, and how many of them would close.
        internal int Changes(ChartDepth judged, out int closing)
        {
            int changes = 0; closing = 0;
            for (int i = 0; i < Count; i++)
            {
                if (!fits[i]) continue;
                bool was = depth.Deep(top[i]), now = judged.Deep(top[i]);
                if (was == now) continue;
                changes++;
                if (was) closing++;
            }
            return changes;
        }

        // Whether any cell within `reach` metres of these points would close at `judged` (her way: what a falling tide takes from it).
        internal bool ClosesNear(ChartDepth judged, IEnumerable<RoutePoint> way, double reach)
        {
            int r = (int)Math.Ceiling(reach / Cell);
            foreach (var p in way)
            {
                int x0 = (int)Math.Floor((p.x - OriginX) / Cell), z0 = (int)Math.Floor((p.z - OriginZ) / Cell);
                for (int z = Math.Max(0, z0 - r); z <= Math.Min(Height - 1, z0 + r); z++)
                    for (int x = Math.Max(0, x0 - r); x <= Math.Min(Width - 1, x0 + r); x++)
                    {
                        int i = z * Width + x;
                        if (fits[i] && depth.Deep(top[i]) && !judged.Deep(top[i])) return true;
                    }
            }
            return false;
        }

        // The water under her keel at cell `i` at the last flood's depth (infinity where no seabed was met, or no depth judged).
        internal double UnderKeel(int i) => !depth.Known || float.IsNegativeInfinity(top[i]) ? double.PositiveInfinity : depth.Under(top[i]);

        // Decision 198 (g) (the user, 2026-10-12: "no wait for the tide before setting out, a hold until the water is back where her way
        // closes for want of it"): the sea level she needs to leave each cell by the last flood's way (to the open sea, or to its goal)
        // through cells her hull fits: a way needs its shallowest cell's (ChartDepth.SeaFor), and a cell the least of its ways' (her beam
        // and turning circle aside: that is her line's). Its own slices, begun when her way has closed: NeedBegin, then NeedStep. Decision 201
        // (e): float, its buffers kept as the flood's (a sea level is a few metres either side of the game's own).
        private float[] need, nextNeed;
        private CellHeap needWork;
        private bool needing;
        private ChartDepth needDepth;
        internal bool Needing => needing;

        internal void NeedBegin(ChartDepth judged)
        {
            if (!judged.Known) throw new ArgumentException("Her need of the sea needs her keel.", nameof(judged));
            needDepth = judged;
            if (nextNeed == null) nextNeed = new float[Count];
            if (needWork == null) needWork = new CellHeap(Count); else needWork.Clear();
            for (int i = 0; i < Count; i++) nextNeed[i] = float.PositiveInfinity;
            foreach (int i in Seeds(hasGoal, goal, fits))
            {
                nextNeed[i] = CellNeed(i);
                needWork.Add(nextNeed[i], i);
            }
            needing = true;
        }

        private float CellNeed(int i) => float.IsNegativeInfinity(top[i]) ? float.NegativeInfinity : (float)needDepth.SeaFor(top[i]);

        internal bool NeedStep(double budgetMs)
        {
            if (!needing) return true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int n = 1; ; n++)
            {
                if ((n & 1023) == 0 && clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
                if (needWork.Count == 0) { Swap(ref need, ref nextNeed); needing = false; return true; }
                needWork.Pop(out float d, out int i);
                if (d > nextNeed[i]) continue;
                int x = i % Width, z = i / Width;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= Width || nz >= Height) continue;
                        int j = nz * Width + nx;
                        if (!fits[j]) continue;
                        if (dx != 0 && dz != 0 && (!fits[z * Width + nx] || !fits[nz * Width + x])) continue;
                        float nd = Math.Max(d, CellNeed(j));
                        if (nd >= nextNeed[j]) continue;
                        nextNeed[j] = nd; needWork.Add(nd, j);
                    }
            }
        }

        internal void Need(ChartDepth judged)
        {
            NeedBegin(judged);
            while (!NeedStep(double.PositiveInfinity)) { }
        }

        // The sea level she needs to leave from `p`: the least of the cells within `reach` cells of it (infinity: no way at any sea
        // level; NaN: not reckoned).
        internal double NeedFrom(RoutePoint p, int reach)
        {
            if (need == null) return double.NaN;
            int x0 = (int)Math.Floor((p.x - OriginX) / Cell), z0 = (int)Math.Floor((p.z - OriginZ) / Cell);
            double least = double.PositiveInfinity;
            for (int z = Math.Max(0, z0 - reach); z <= Math.Min(Height - 1, z0 + reach); z++)
                for (int x = Math.Max(0, x0 - reach); x <= Math.Min(Width - 1, x0 + reach); x++)
                    least = Math.Min(least, need[z * Width + x]);
            return least;
        }

        internal double DistanceAt(int i) => i < 0 ? double.PositiveInfinity : distance[i];

        // Decision 198 (b): her line's search counts as the flood does: a step's cost per metre from cell `i` sailed on `heading`, and
        // the wind the flood counted (her beat off it, and the clearance under which a beat is too narrow to tack in).
        internal double StepFactor(int i, double heading) => StepCost(i, false, heading) / Cell;
        internal double WindBeat => windBeat;
        internal double NarrowClearance => narrowClearance;

        // The cell to start from at `p`: its own when it is water and reached by the flood, else the nearest such within `reach`
        // cells, by default StartReach metres (she lies alongside a dock, her own cell taken by it; 3 cells of 8 m before decision 201 e);
        // -1 when none.
        internal const double StartReach = 24;
        internal int Start(RoutePoint p, int reach = 0)
        {
            int i = IndexOf(p);
            if (i >= 0 && NavigationMath.Finite(distance[i])) return i;
            if (reach <= 0) reach = Cells(StartReach);
            int x0 = (int)Math.Floor((p.x - OriginX) / Cell), z0 = (int)Math.Floor((p.z - OriginZ) / Cell), best = -1;
            double bestGap = double.PositiveInfinity;
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int x = x0 + dx, z = z0 + dz;
                    if (x < 0 || z < 0 || x >= Width || z >= Height) continue;
                    int j = z * Width + x;
                    if (!NavigationMath.Finite(distance[j])) continue;
                    double gap = RoutePoint.Distance(p, Centre(j));
                    if (gap < bestGap) { bestGap = gap; best = j; }
                }
            return best;
        }

        // Her distance to go by water from `p` (the nearest flooded cell's, plus the gap to it); infinity when none is near.
        internal double DistanceFrom(RoutePoint p)
        {
            int i = Start(p);
            return i < 0 ? double.PositiveInfinity : distance[i] + RoutePoint.Distance(p, Centre(i));
        }

        // Down the water distance from `p`: each step to the neighbour whose distance and the step's cost (StepCost: the flood's own)
        // are least, among those nearer the sea (or her goal), until `length` metres, the edge (her way out) or her goal (her way in:
        // no neighbour nearer). The cells' centres, nearest first; empty when `p` has no flooded cell near.
        internal List<RoutePoint> Descent(RoutePoint p, double length)
        {
            var path = new List<RoutePoint>();
            int i = Start(p);
            if (i < 0) return path;
            double along = 0;
            var last = p;
            while (along < length)
            {
                path.Add(Centre(i));
                along += RoutePoint.Distance(last, Centre(i)); last = Centre(i);
                if (OnEdge(i) && !hasGoal) break;
                int x = i % Width, z = i / Width, best = -1;
                double bestScore = double.PositiveInfinity;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= Width || nz >= Height) continue;
                        int j = nz * Width + nx;
                        if (!(distance[j] < distance[i] - 1e-9)) continue;
                        if (dx != 0 && dz != 0 && (!water[z * Width + nx] || !water[nz * Width + x])) continue;
                        double score = distance[j] + StepCost(i, dx != 0 && dz != 0, NavigationMath.Heading(dx, dz));
                        if (score < bestScore) { bestScore = score; best = j; }
                    }
                if (best < 0) break;
                i = best;
            }
            return path;
        }

        // Decision 201 (a): a point of her hull's centre line at (x, z) with `want` metres of room at the depth `judged`, as her line's search
        // judges one (LinePlanner's Water): its cell water for her, and the middle of every cell that isn't `want` or more from it. Off the
        // chart: no room (her way in ends inside it).
        internal bool Room(double x, double z, double want, ChartDepth judged)
        {
            var p = new RoutePoint((float)x, (float)z);
            int cell = IndexOf(p);
            if (cell < 0 || !WaterAt(cell, judged)) return false;
            int reach = (int)Math.Ceiling(want / Cell) + 1, x0 = cell % Width, z0 = cell / Width;
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int cx = x0 + dx, cz = z0 + dz;
                    if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) continue;
                    int j = cz * Width + cx;
                    if (!WaterAt(j, judged) && RoutePoint.Distance(p, Centre(j)) < want) return false;
                }
            return true;
        }

        // Decision 201 (a) (the user, 2026-10-12 night): her whole hull lying along `heading` with its middle at `at` (her centre line's
        // points `axis` metres along her, LinePlanner.Axis), and on a straight run-in `runIn` metres long astern of it, every RunInStep
        // metres: each point with `want` metres of room (Room). `why`: where it isn't.
        internal const double RunInStep = 2;
        internal bool HullRoom(RoutePoint at, double heading, double[] axis, double want, double runIn, ChartDepth judged, out string why)
        {
            double h = heading * Math.PI / 180, sx = Math.Sin(h), sz = Math.Cos(h);
            int steps = Math.Max(0, (int)Math.Ceiling(runIn / RunInStep - 1e-9));
            for (int s = 0; s <= steps; s++)
            {
                double back = Math.Min(runIn, s * RunInStep), cx = at.x - back * sx, cz = at.z - back * sz;
                foreach (double a in axis)
                    if (!Room(cx + a * sx, cz + a * sz, want, judged))
                    {
                        why = (back > 0 ? back.ToString("F0") + " m back on her run-in, " : "") + "her point " + a.ToString("F1") + " m along her at (" +
                            (cx + a * sx).ToString("F0") + ", " + (cz + a * sz).ToString("F0") + ") has under " + want.ToString("F1") + " m of room";
                        return false;
                    }
            }
            why = null;
            return true;
        }

        // Decision 201 (b): where her line in begins: down her chart's way from `from` (her way in, flooded from her approach point), its
        // first point `within` metres or less by water from her goal, the chart's last if none; `heading` down the way there (toward the
        // point EntryLook metres on; NaN where the way ends there). Null where the flood doesn't reach `from`.
        internal const double EntryLook = 24;
        internal RoutePoint? EntryDown(RoutePoint from, double within, out double heading)
        {
            heading = double.NaN;
            var down = Descent(from, 1e7);
            if (down.Count == 0) return null;
            int at = down.FindIndex(q => DistanceFrom(q) <= within);
            if (at < 0) at = down.Count - 1;
            int next = at;
            double along = 0;
            while (next < down.Count - 1 && along < EntryLook) { along += RoutePoint.Distance(down[next], down[next + 1]); next++; }
            if (RoutePoint.Distance(down[at], down[next]) > 1) heading = NavigationMath.Heading(down[next].x - down[at].x, down[next].z - down[at].z);
            return down[at];
        }

        // Decision 201 (b): the water on the chart's edge her chart's flood reaches nearest `p` (where she comes in from the sea); -1 when
        // none.
        internal int EdgeNearest(RoutePoint p)
        {
            int best = -1; double least = double.PositiveInfinity;
            for (int i = 0; i < Count; i++)
            {
                if (!OnEdge(i) || !NavigationMath.Finite(distance[i])) continue;
                double d = RoutePoint.Distance(p, Centre(i));
                if (d < least) { least = d; best = i; }
            }
            return best;
        }
    }

    // ==== ControlMath ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public static class ControlMath
    {
        // A single symmetric sheet can haul toward its closed interval, but it
        // cannot pull across that interval. Easing thereafter needs torque in
        // the travel direction at every intermediate angle, not just the target.
        public static bool CanReachSingleSheetAngle(double current,double target,double closedMin,double closedMax,Func<double,double> torque)
        {
            if(!NavigationMath.Finite(current) || !NavigationMath.Finite(target) || !NavigationMath.Finite(closedMin) ||
                !NavigationMath.Finite(closedMax) || closedMin>closedMax || torque==null)
                throw new ArgumentException("Invalid single-sheet travel inputs.");
            double start,direction;
            if(target>closedMax) {start=Math.Min(target,Math.Max(current,closedMin));direction=1;}
            else if(target<closedMin) {start=Math.Max(target,Math.Min(current,closedMax));direction=-1;}
            else return true;
            int steps=(int)Math.Ceiling(Math.Abs(target-start));
            for(int i=0;i<=steps;i++)
            {
                double at=steps==0 ? target : start+(target-start)*i/steps;
                double moment=torque(at);
                if(!NavigationMath.Finite(moment)) throw new ArgumentException("Nonfinite single-sheet torque.");
                if(direction*moment<-.001) return false;
            }
            return true;
        }

        // A tensioned sheet can oppose torque at an interval boundary, but cannot
        // push a free sail against torque toward the other boundary.
        public static bool CanHoldAngle(double angle, double minimum, double maximum, double torque)
        {
            if (!NavigationMath.Finite(angle) || !NavigationMath.Finite(minimum) ||
                !NavigationMath.Finite(maximum) || !NavigationMath.Finite(torque))
                throw new ArgumentException("Holding inputs must be finite.");
            const double angleTolerance = .01;
            if (minimum > maximum + angleTolerance || angle < minimum - angleTolerance || angle > maximum + angleTolerance) return false;
            if (torque > .001) return angle >= maximum - angleTolerance;
            if (torque < -.001) return angle <= minimum + angleTolerance;
            return true;
        }

        // Smallest rope length whose interpolated angular interval contains the target.
        // Both endpoints must open outwards as length increases.
        public static double IntervalLength(double target, double closedMin, double closedMax, double openMin, double openMax)
        {
            if (!NavigationMath.Finite(target) || !NavigationMath.Finite(closedMin) || !NavigationMath.Finite(closedMax) ||
                !NavigationMath.Finite(openMin) || !NavigationMath.Finite(openMax))
                throw new ArgumentException("Sheet interval inputs must be finite.");
            if (closedMin > closedMax) throw new ArgumentException("Closed sheet interval is inverted.");
            if (openMin > closedMin || openMax < closedMax || openMax <= openMin)
                throw new ArgumentException("Unrecognized sheet interval geometry.");
            target = NavigationMath.Clamp(target, openMin, openMax);
            double lower = target < closedMin ? NavigationMath.InverseLerp(closedMin, openMin, target) : 0;
            double upper = target > closedMax ? NavigationMath.InverseLerp(closedMax, openMax, target) : 0;
            return Math.Max(lower, upper);
        }
    }

    // ==== RigScanner ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public sealed class RigSnapshot
    {
        internal readonly List<Sail> Sails = new List<Sail>();
        internal readonly List<ReefBinding> Reefs = new List<ReefBinding>();
        internal readonly List<SailGroup> Groups = new List<SailGroup>();
        internal readonly Dictionary<RopeController, GPButtonRopeWinch> Winches = new Dictionary<RopeController, GPButtonRopeWinch>();
        // Decision 231: a brace no winch holds (RigScanner.UnwinchedBrace), with the winch whose rate her sailor sets it at.
        internal readonly Dictionary<RopeController, GPButtonRopeWinch> Unwinched = new Dictionary<RopeController, GPButtonRopeWinch>();
        public readonly List<string> Errors = new List<string>();
        public string Description { get; internal set; }
        internal string Signature;
        public bool Supported => Errors.Count == 0 && Sails.Count > 0;
    }

    internal sealed class ReefBinding
    {
        internal Sail Sail;
        internal RopeControllerSailReef Rope;
        internal bool Reversed;
    }

    internal sealed class SailGroup
    {
        internal Sail Primary;
        internal HingeJoint Hinge;
        internal readonly List<Sail> Members = new List<Sail>();
        internal readonly List<RopeController> Angles = new List<RopeController>();
        internal float TargetAngle;
        internal float SheetLimitBias;
        internal readonly TrimPolicy Trim = new TrimPolicy();
    }

    public static class RigScanner
    {
        // A part of hers is on: enabled, and switched on within her own ship. A ship stored far out on the parking field is
        // switched off as a whole (launch 5's first run, 2026-10-06: every booking was carried, "inactive reef controller or
        // winch"); the booking's checks judge her as if she were on. For a ship that is on, this is isActiveAndEnabled.
        internal static bool On(Behaviour part, Transform root) => part && part.enabled && ActiveWithin(part.transform, root);

        internal static bool ActiveWithin(Transform part, Transform root)
        {
            for (var t = part; t; t = t.parent)
            {
                if (t == root) return true;
                if (!t.gameObject.activeSelf) return false;
            }
            return part && part.gameObject.activeInHierarchy;
        }

        public static RigSnapshot Scan(BoatContext boat)
        {
            var result = new RigSnapshot();
            var sails = boat.Root.GetComponentsInChildren<Sail>(true)
                // Vanilla still equips active legacy sails (obsolete=true) on
                // fresh Emerald ships. Validate their real controls normally.
                .Where(s => s.IsInstalled() && s.shipRigidbody == boat.Body).ToArray();
            var groups = new Dictionary<Sail, SailGroup>();
            var owners = new Dictionary<RopeController, Sail>();
            foreach (var winch in boat.Root.GetComponentsInChildren<GPButtonRopeWinch>(true))
            {
                if (!winch.rope) continue;
                if (result.Winches.ContainsKey(winch.rope)) result.Errors.Add("Multiple winches share a controller: " + winch.name);
                else result.Winches.Add(winch.rope, winch);
            }
            foreach (var sail in sails)
            {
                result.Sails.Add(sail);
                var connections = sail.GetComponent<SailConnections>();
                var reef = connections ? connections.reefController as RopeControllerSailReef : null;
                if (!reef || reef.sail != sail || !result.Winches.ContainsKey(reef))
                    result.Errors.Add(sail.name + ": missing or unsupported reef control.");
                else
                {
                    result.Reefs.Add(new ReefBinding { Sail = sail, Rope = reef, Reversed = reef.reverseReefing });
                    if (!On(reef, boat.Root) || !On(result.Winches[reef], boat.Root))
                        result.Errors.Add(sail.name + ": inactive reef controller or winch.");
                }
                if (!On(sail, boat.Root) || !sail.windcenter || !NavigationMath.Finite(sail.minAngle) ||
                    !NavigationMath.Finite(sail.maxAngle) || !NavigationMath.Finite(sail.sailArea) || sail.sailArea <= 0 ||
                    sail.minAngle >= sail.maxAngle || sail.maxAngle - sail.minAngle > 360 || !sail.cloth || !sail.cloth.transform.parent)
                    result.Errors.Add(sail.name + ": inactive sail or invalid geometry.");
                var primary = sail;
                var visited = new HashSet<Sail>();
                while (primary)
                {
                    if (!visited.Add(primary)) { result.Errors.Add(sail.name + ": linked-yard cycle."); primary = null; break; }
                    var mirror = primary.GetComponent<SquareTopsailAngleMirror>();
                    if (!mirror) break;
                    primary = mirror.sailBelow ? mirror.sailBelow.GetComponent<Sail>() : null;
                }
                if (!primary || !sails.Contains(primary)) { result.Errors.Add(sail.name + ": linked sail is not on this ship."); continue; }
                if (!groups.TryGetValue(primary, out var group))
                {
                    group = new SailGroup { Primary = primary, Hinge = primary.GetComponent<HingeJoint>() };
                    groups.Add(primary, group);
                    result.Groups.Add(group);
                    var c = primary.GetComponent<SailConnections>();
                    if (!c || !group.Hinge) { result.Errors.Add(primary.name + ": missing connections/hinge."); continue; }
                    var controls = new[] { c.angleControllerMid, c.angleControllerLeft, c.angleControllerRight }.Where(r => r).Distinct();
                    foreach (var rope in controls)
                    {
                        if (owners.TryGetValue(rope, out var other) && other != primary)
                            result.Errors.Add(primary.name + ": control shared by unrelated yards.");
                        owners[rope] = primary;
                        if (!result.Winches.ContainsKey(rope))
                        {
                            var stand = UnwinchedBrace(primary, rope, result);
                            if (stand) result.Unwinched[rope] = stand;
                            else result.Errors.Add(primary.name + ": sheet has no winch.");
                        }
                        if (rope is RopeControllerSailAngle a)
                        {
                            if (a.sailHinge != group.Hinge) result.Errors.Add(primary.name + ": sheet hinge mismatch.");
                        }
                        else if (rope is RopeControllerSailAngleJib j)
                        {
                            if (!j.jibAngleMaster || j.jibAngleMaster.sailHinge != group.Hinge)
                                result.Errors.Add(primary.name + ": jib linkage mismatch.");
                        }
                        else if (rope is RopeControllerSailAngleSquare square)
                        {
                            if (!square.squareAngleMaster || square.squareAngleMaster.GetHinge() != group.Hinge)
                                result.Errors.Add(primary.name + ": square brace linkage mismatch.");
                        }
                        else result.Errors.Add(primary.name + ": unknown sheet controller " + rope.GetType().Name);
                        group.Angles.Add(rope);
                    }
                    if (group.Angles.Count == 0) result.Errors.Add(primary.name + ": no angle controls.");
                    if (primary.squareSail && group.Angles.Count != 2) result.Errors.Add(primary.name + ": square yard requires both braces.");
                    ValidateGroup(group, result, boat.Root);
                }
                group.Members.Add(sail);
            }
            if (sails.Length == 0) result.Errors.Add("No installed sails were found.");
            result.Signature = Signature(boat);
            result.Description = sails.Length + " sails, " + groups.Count + " independently trimmed groups; " +
                (result.Supported ? "mapping supported (handling unvalidated)" : string.Join(" | ", result.Errors));
            return result;
        }

        // Decision 231: Topsail Yards (1.0.7) skips a sail marked obsolete when it links a mast's square sails (the game links each to the
        // square sail below it, SquareTopsailAngleMirror), so on the stock Brig her course (prefab 114) is left with no mirror and its
        // braces with no winch, the topsail above holding the mast's brace winches. Only in that pattern is such a brace set by her sailor
        // directly, at the rate of the winch: a square sail with no mirror whose brace no winch holds, while the same side's brace winch
        // of its own mast holds that side's brace of another square sail with no mirror on that mast. That winch, else null.
        internal static GPButtonRopeWinch UnwinchedBrace(Sail primary, RopeController rope, RigSnapshot result)
        {
            if (!primary || !primary.squareSail || primary.GetComponent<SquareTopsailAngleMirror>() || !(rope is RopeControllerSailAngleSquare brace)) return null;
            var mast = primary.GetComponentInParent<Mast>();
            if (!mast) return null;
            var winches = brace.side == RopeControllerSailAngleSquare.WinchSide.left ? mast.leftAngleWinch : mast.rightAngleWinch;
            var winch = winches != null && winches.Length > 0 ? winches[0] : null;
            if (!winch || !(winch.rope is RopeControllerSailAngleSquare held) || held == brace || held.side != brace.side || !result.Winches.ContainsKey(held)) return null;
            var other = held.squareAngleMaster ? held.squareAngleMaster.GetComponent<Sail>() : null;
            if (!other || other == primary || !other.squareSail || other.GetComponent<SquareTopsailAngleMirror>() || other.GetComponentInParent<Mast>() != mast) return null;
            return winch;
        }

        private static void ValidateGroup(SailGroup group, RigSnapshot result, Transform root)
        {
            try
            {
                var s = group.Primary;
                var axis = group.Hinge.axis;
                if (!NavigationMath.Finite(axis.sqrMagnitude) || axis.sqrMagnitude < 0.01f)
                    throw new ArgumentException("invalid hinge axis");
                var ordinary = group.Angles.OfType<RopeControllerSailAngle>().ToArray();
                var jibs = group.Angles.OfType<RopeControllerSailAngleJib>().ToArray();
                var squares = group.Angles.OfType<RopeControllerSailAngleSquare>().ToArray();
                if (squares.Length > 0)
                {
                    if (ordinary.Length != 0 || jibs.Length != 0 || squares.Length != 2 ||
                        squares[0].side == squares[1].side || !squares[0].squareAngleMaster ||
                        squares[0].squareAngleMaster != squares[1].squareAngleMaster)
                        throw new ArgumentException("square yard requires one opposing brace pair");
                    // The master is the yard's own (the game's SquareAngleMaster.Awake takes the hinge and sail on its object), and the yard's
                    // limits are the sail's. Not the master's GetMinAngle/GetMaxAngle: it copies them from the sail only in its Update, so on a
                    // ship that hasn't run a frame since her sails were set (lying far off, switched off) they are stale. The Brig's scan
                    // failed on them in 2 of 5 charting launches ("square brace master has inconsistent geometry", runs 20261012-202827 and
                    // 20261013-020131), and so could a Brig's booking (carried on paper, D1).
                    var master = squares[0].squareAngleMaster;
                    if (!On(master, root) || master.GetComponent<HingeJoint>() != group.Hinge || master.GetComponent<Sail>() != s ||
                        !NavigationMath.Finite(s.minAngle) || !NavigationMath.Finite(s.maxAngle))
                        throw new ArgumentException("square brace master has inconsistent geometry");
                }
                if (ordinary.Length > 0)
                {
                    if (jibs.Length != 0 || (ordinary.Length == 1 && !ordinary[0].limitBoth) ||
                        ordinary.Length > 2 || (ordinary.Length == 2 && (ordinary.Any(a => a.limitBoth) || ordinary[0].limitMax == ordinary[1].limitMax)))
                        throw new ArgumentException("unsupported or incomplete sheet pair");
                    foreach (var a in ordinary.Where(a => a.limitBoth))
                        ControlMath.IntervalLength(0, a.limitBothClosestAngle - a.closestAnglePadding,
                            a.limitBothClosestAngle + a.closestAnglePadding, s.minAngle, s.maxAngle);
                }
                if (jibs.Length > 0)
                {
                    if (jibs.Length != 2 || jibs[0].side == jibs[1].side)
                        throw new ArgumentException("jib requires opposing sheets");
                    if (group.Hinge.useSpring || group.Hinge.useMotor)
                        throw new ArgumentException("jib spring or motor torque is unsupported");
                    foreach (var j in jibs)
                        ControlMath.IntervalLength(0, j.closestAngle - j.closestAnglePadding,
                            j.closestAngle + j.closestAnglePadding, s.minAngle, s.maxAngle);
                }
                foreach (var rope in group.Angles)
                    if (!On(rope, root) || !(result.Winches.TryGetValue(rope, out var winch) || result.Unwinched.TryGetValue(rope, out winch)) || !On(winch, root))
                        throw new ArgumentException("inactive sheet or winch");
            }
            catch (Exception ex) { result.Errors.Add(group.Primary.name + ": " + ex.Message); }
        }

        // What changed between two fingerprints (the user, 2026-10-10: runs 20261010-005136 and -020912 held on "Vessel or rig
        // changed." and named nothing): per sail (keyed by its instance ID, `name` names it), each field that differs, old -> new.
        // Sails only in one of them are listed as gone or new.
        private static readonly string[] SignatureFields = { "id", "active", "enabled", "area", "min angle", "max angle", "square", "reverse reef", "topsail below", "attachment place", "attachment scale" };

        internal static string Difference(string before, string after, Func<string, string> name)
        {
            Func<string, Dictionary<string, string[]>> sails = s => (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':')).Where(f => f.Length > 0).GroupBy(f => f[0]).ToDictionary(g => g.Key, g => g.First());
            var a = sails(before); var b = sails(after);
            var parts = new List<string>();
            foreach (var id in a.Keys.Union(b.Keys))
            {
                string who = (name?.Invoke(id) ?? "sail") + " [" + id + "]";
                if (!b.ContainsKey(id)) { parts.Add(who + " gone"); continue; }
                if (!a.ContainsKey(id)) { parts.Add(who + " new"); continue; }
                var x = a[id]; var y = b[id];
                var fields = new List<string>();
                for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
                {
                    string l = i < x.Length ? x[i] : "-", r = i < y.Length ? y[i] : "-";
                    if (l != r) fields.Add((i < SignatureFields.Length ? SignatureFields[i] : "field " + i) + " " + l + " -> " + r);
                }
                if (fields.Count > 0) parts.Add(who + ": " + string.Join(", ", fields.ToArray()));
            }
            return parts.Count == 0 ? "no sail differs (the same text?)" : string.Join("; ", parts.ToArray());
        }

        internal static string Signature(BoatContext boat)
        {
            // Session-only fingerprint: names intentionally excluded. Persisted descriptors arrive in M3.
            // Each sail, rope and winch judged as if her ship were on (ActiveWithin, On), never by activeInHierarchy or
            // isActiveAndEnabled: the game hides a ship beyond the horizon by switching her root off (BoatHorizonPerformanceSwitcher),
            // and far from the player every sail then read off and she held, "Vessel or rig changed." (run 20261010-024308; the
            // review of 2026-10-10).
            var root = boat.Root;
            var sb = new StringBuilder();
            foreach (var s in boat.Root.GetComponentsInChildren<Sail>(true).OrderBy(s => s.GetInstanceID()))
            {
                if (s.shipRigidbody != boat.Body || !s.IsInstalled()) continue;
                var c = s.GetComponent<SailConnections>();
                sb.Append(s.GetInstanceID()).Append(':').Append(ActiveWithin(s.transform, root)).Append(':')
                    .Append(s.enabled).Append(':')
                    .Append(s.sailArea).Append(':').Append(s.minAngle).Append(':').Append(s.maxAngle).Append(':')
                    .Append(s.squareSail).Append(':');
                var reef = c ? c.reefController as RopeControllerSailReef : null;
                sb.Append(reef ? reef.reverseReefing : false).Append(':');
                var mirror = s.GetComponent<SquareTopsailAngleMirror>();
                sb.Append(mirror && mirror.sailBelow ? mirror.sailBelow.GetInstanceID() : 0).Append(':');
                var attachment = s.transform.parent;
                if (attachment)
                    sb.Append(attachment.localPosition).Append(':').Append(attachment.localScale);
                if (s.cloth && s.cloth.transform.parent)
                    sb.Append(':').Append(s.cloth.transform.parent.localScale.y).Append(':').Append(s.cloth.transform.parent.localScale.z);
                if (c) foreach (var rope in new[] { c.reefController, c.angleControllerMid, c.angleControllerLeft, c.angleControllerRight })
                {
                    sb.Append(':').Append(rope ? rope.GetInstanceID() : 0);
                    if (rope) sb.Append(':').Append(On(rope, root));
                    if (rope is RopeControllerSailAngle a)
                        sb.Append(':').Append(a.limitMax).Append(':').Append(a.limitBoth).Append(':').Append(a.limitBothClosestAngle).Append(':').Append(a.closestAnglePadding);
                    if (rope is RopeControllerSailAngleJib j)
                        sb.Append(':').Append(j.side).Append(':').Append(j.closestAngle).Append(':').Append(j.closestAnglePadding);
                    if (rope is RopeControllerSailAngleSquare square)
                        sb.Append(':').Append(square.side).Append(':').Append(square.squareAngleMaster ? square.squareAngleMaster.GetInstanceID() : 0);
                }
                sb.Append(';');
            }
            foreach (var w in boat.Root.GetComponentsInChildren<GPButtonRopeWinch>(true).OrderBy(w => w.GetInstanceID()))
                sb.Append('|').Append(w.GetInstanceID()).Append(':').Append(w.rope ? w.rope.GetInstanceID() : 0).Append(':').Append(On(w, root));
            return sb.ToString();
        }
    }

    // ==== SailControlAdapters ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    internal sealed class SailControlAdapters
    {
        private readonly RigSnapshot rig;
        private readonly BoatContext boat;
        private readonly Dictionary<RopeController, float> targets = new Dictionary<RopeController, float>();
        internal bool Emergency;
        private float plannedDeployment;
        private float deploymentLimit=1;
        private Sail emergencySail;
        private readonly List<TrimSample> trimSamples = new List<TrimSample>(360);

        // Decision 126 through the sail (the user, 2026-10-10: a backed sail, not a push): +1 sets every sail for the turning
        // moment to starboard, -1 to port, 0 for drive. TurnOnly (in irons, backing; Voyage.SetSailTurn sets both together): the
        // moment alone, backed if need be (decision 178 took out decision 134's eased sheet for bearing away). TurnMoment: the
        // moment her way that the last plan's angles give (N·m, summed over her sail groups), for the log.
        internal int TurnSide;
        internal bool TurnOnly;
        // Decision 155, backing: every sail set (none furled for decision 153's limit), each square sail braced for the most push astern
        // the game's force on it gives (its Forward most negative, aback), the others for the turning moment toward TurnSide.
        internal bool Astern;
        private bool plannedAstern;
        internal double TurnMoment { get; private set; }
        private int plannedTurnSide;
        private bool plannedTurnOnly;
        internal bool TurnChanged => TurnSide != plannedTurnSide || TurnOnly != plannedTurnOnly || Astern != plannedAstern;

        internal SailControlAdapters(BoatContext boat, RigSnapshot rig) { this.boat = boat; this.rig = rig; }

        // Decision 133: her least angle to the apparent wind (UpwindLimits), from each sail group's own data: its upwindEfficiency,
        // its sail area, and how close to her centre line its controls hold it: a lateen's or gaff's single sheet to its closest
        // angle (RopeControllerSailAngle.limitBothClosestAngle, 0 in the vanilla sails), a jib's sheets to theirs (16), a square
        // sail's braces to its hinge's limits (its yard square across at 0, so 90 less the furthest it is braced). `report`: each
        // sail's numbers, for the log.
        // Decision 214: one least angle per sail, at UpwindLimits.WindShare, at sea and in port.
        internal double UpwindLimit(out string report)
        {
            limits.Clear(); furledGroups.Clear(); Limit = double.NaN;
            var sails = new List<KeyValuePair<double, double>>();
            var text = new System.Text.StringBuilder();
            foreach (var group in rig.Groups)
            {
                var sail = group.Primary;
                if (!sail) continue;
                double area = group.Members.Where(m => m).Sum(m => (double)m.sailArea);
                double closest;
                if (sail.squareSail)
                {
                    float min = group.Hinge ? group.Hinge.limits.min : sail.minAngle, max = group.Hinge ? group.Hinge.limits.max : sail.maxAngle;
                    closest = NavigationMath.Clamp(90 - Math.Max(Math.Abs(min), Math.Abs(max)), 0, 90);
                }
                else
                {
                    var single = group.Angles.OfType<RopeControllerSailAngle>().FirstOrDefault(a => a && a.limitBoth);
                    var jibs = group.Angles.OfType<RopeControllerSailAngleJib>().Where(j => j).ToList();
                    closest = single ? Math.Abs(single.limitBothClosestAngle) : jibs.Count > 0 ? jibs.Min(j => Math.Abs(j.closestAngle)) : 0;
                    closest = NavigationMath.Clamp(closest, 0, 90);
                }
                double efficiency = NavigationMath.Clamp(sail.upwindEfficiency, 0, 1);
                double analytic = UpwindLimits.SailMinimum(efficiency, closest);
                // By the sail's own geometry where it can be read (any rig, a square sail's braces included); else the rule.
                double geometric = double.NaN;
                try { geometric = GeometricMinimum(group, UpwindLimits.WindShare); }
                catch (Exception e) { Log.Info("Sailing: " + sail.name + "'s upwind limit by its geometry couldn't be read (" + e.Message + ")."); }
                double minimum = double.IsNaN(geometric) ? analytic : geometric;
                sails.Add(new KeyValuePair<double, double>(area, minimum));
                limits[group] = new UpwindLimits.SailLimit(area, minimum);
                text.Append(text.Length > 0 ? "; " : "").Append(sail.name).Append(": efficiency ").Append(sail.upwindEfficiency.ToString("F2"))
                    .Append(", area ").Append(area.ToString("F0")).Append(", ").Append(minimum.ToString("F0"))
                    .Append(double.IsNaN(geometric) ? " by the rule (closest " + closest.ToString("F0") + ")" : " by its geometry (the rule: " + analytic.ToString("F0") + ")")
                    // The angles its controls can hold it at (the user, 2026-10-11: the reading must fit what the sail can turn to): a
                    // square sail's braces from its minAngle to its maxAngle (RopeControllerSailAngleSquare), else its sheet's range.
                    .Append(", yard range ").Append(sail.minAngle.ToString("F0")).Append(" to ").Append(sail.maxAngle.ToString("F0"));
            }
            if (sails.Count == 0) { report = "no sails"; return 75; }
            report = text.ToString();
            return UpwindLimits.RigMinimum(sails);
        }

        // Decision 153: each sail's least angle from her set-up scan (UpwindLimit), the limit she sails at (ChooseLimit, by her way made
        // good), and the sails furled for it (UpdateSet: outside the limit, while the wind is inside their own close-hauled).
        // Decision 177: at sea or in port (InPort, set by her voyage): Margin her close-hauled's beyond a sail's least angle (214: the
        // same least angles in both).
        private readonly Dictionary<SailGroup, UpwindLimits.SailLimit> limits = new Dictionary<SailGroup, UpwindLimits.SailLimit>();
        internal bool InPort { get; private set; }
        internal double Margin => InPort ? TackNavigator.PortCloseMargin : TackNavigator.CloseMargin;
        // In port or out of it. True when it changed.
        internal bool SetInPort(bool inPort)
        {
            if (inPort == InPort) return false;
            InPort = inPort;
            return true;
        }
        private readonly HashSet<SailGroup> furledGroups = new HashSet<SailGroup>();
        internal double Limit { get; private set; } = double.NaN;
        internal bool HasLimits => limits.Count > 0;
        // The least of her sail sets' limits (the closest she can sail to the apparent wind with some sail), NaN without a scan.
        internal double LeastLimit => limits.Count > 0 ? limits.Values.Min(v => v.Minimum) : double.NaN;

        // The limit with the most way made good toward her guide point (`guideOff`: its bearing off the apparent wind), or null when
        // unchanged; else what changed, for the log.
        // `clear`: whether a heading, as its angle off the apparent wind (+ to starboard), is clear where she is (the terrain).
        // `held`: her limit is kept unless it has no clear heading at all (the user, 2026-10-11, run 20261011-111556: the Cog changed
        // between her jib alone and every sail every few seconds, each change a new course on the other side of the wind, and turned
        // in circles onto Dragon Cliffs' rock). `side`: her bow's side of the apparent wind (UpwindLimits.ChooseLimit).
        // `clearFar` (decision 162, in her harbour's chart): her headings judged clear further on, used when any limit has such a heading.
        internal string ChooseLimit(double guideOff, Func<double, bool> clear = null, bool held = false, int side = 0, Func<double, bool> clearFar = null)
        {
            if (limits.Count == 0) return null;
            if (clearFar != null)
            {
                var all = limits.Values.ToList();
                if (all.Select(s => s.Minimum).Distinct().Any(m => !double.IsNegativeInfinity(UpwindLimits.BestVmg(guideOff, m, UpwindLimits.AreaWithin(all, m), clearFar, 0, Margin))))
                    clear = clearFar;
            }
            double was = Limit;
            // Decision 190: in port a held limit gives way to one that lays her guide point when it can't itself.
            bool laysOrNone = !InPort || UpwindLimits.Lays(guideOff, was, clear, Margin) || !limits.Values.Any(v => UpwindLimits.Lays(guideOff, v.Minimum, clear, Margin));
            if (held && NavigationMath.Finite(was) && laysOrNone && !double.IsNegativeInfinity(UpwindLimits.BestVmg(guideOff, was, UpwindLimits.AreaWithin(limits.Values.ToList(), was), clear, 0, Margin)))
                return null;
            double now = UpwindLimits.ChooseLimit(limits.Values.ToList(), guideOff, was, clear, side, Margin, InPort);
            if (NavigationMath.Finite(was) && Math.Abs(now - was) < 1e-6) return null;
            Limit = now;
            var within = limits.Where(g => g.Value.Minimum <= now + 1e-6).Select(g => g.Key.Primary ? g.Key.Primary.name : "?").ToArray();
            return "sails at " + now.ToString("F0") + " degrees off the apparent wind with " + string.Join(", ", within) + " (" +
                UpwindLimits.AreaWithin(limits.Values.ToList(), now).ToString("F0") + " of " + limits.Values.Sum(s => s.Area).ToString("F0") +
                " of her sail area; her guide point " + Math.Abs(guideOff).ToString("F0") + " degrees off the apparent wind" +
                (NavigationMath.Finite(was) ? "; it was " + was.ToString("F0") : "") + ")";
        }

        // Each sail outside her limit set or furled for the apparent wind `apparentOff` off her bow (UpwindLimits.Draws); what
        // changed, or null.
        internal string UpdateSet(double apparentOff)
        {
            if (limits.Count == 0 || !NavigationMath.Finite(Limit)) return null;
            var changed = new List<string>();
            foreach (var g in limits)
            {
                bool setNow = !furledGroups.Contains(g.Key);
                bool draws = UpwindLimits.Draws(apparentOff, g.Value.Minimum, Limit, setNow, Margin);
                if (draws == setNow) continue;
                if (draws) furledGroups.Remove(g.Key); else furledGroups.Add(g.Key);
                changed.Add((g.Key.Primary ? g.Key.Primary.name : "?") + (draws ? " set" : " furled") + " (its close-hauled " + TackNavigator.BeatOff(g.Value.Minimum, Margin).ToString("F0") + ")");
            }
            return changed.Count > 0 ? string.Join(", ", changed.ToArray()) + ", the apparent wind " + Math.Abs(apparentOff).ToString("F0") + " degrees off her bow" : null;
        }

        private bool FurledForLimit(Sail sail) => !Astern && furledGroups.Count > 0 && furledGroups.Any(g => g.Members.Contains(sail));

        internal void Plan(float deployment, bool urgent) => PlanLimited(deployment,urgent,1);

        internal void PlanLimited(float deployment, bool urgent, float maximumDeployment)
        {
            // No sail asked for (towed, drifting off, waiting, holding): every sail furled, no steerage sail even when her heel
            // is urgent (the user, 2026-10-07: "Let the ship towed fully furled"; launch 5, candidates AE-AH: towed heeled over,
            // the urgent heel response kept 15% of her sail set, which heeled her more).
            if (maximumDeployment <= 0f)
            {
                emergencySail = null; Emergency = false; plannedDeployment = 0f; deploymentLimit = 0f;
                plannedTurnSide = TurnSide; plannedTurnOnly = TurnOnly; plannedAstern = Astern; TurnMoment = 0;
                ResetReefPlan();
                targets.Clear();
                foreach (var binding in rig.Reefs)
                {
                    if (!binding.Sail || !binding.Rope || binding.Rope.sail != binding.Sail ||
                        binding.Sail.shipRigidbody != boat.Body || binding.Reversed != binding.Rope.reverseReefing)
                        throw new InvalidOperationException("Reef linkage changed during control.");
                    Set(binding.Rope, (float)NavigationMath.ReefLength(0, binding.Reversed));
                }
                return;
            }
            if (urgent && !emergencySail) SelectSteerageSail();
            if (!urgent && deployment >= .999f) emergencySail = null;
            Emergency = urgent;
            plannedDeployment = deployment;
            deploymentLimit=urgent ? 1 : Mathf.Clamp01(maximumDeployment);
            plannedTurnSide = TurnSide; plannedTurnOnly = TurnOnly; plannedAstern = Astern; TurnMoment = 0;
            targets.Clear();
            UpdateReefPlan(urgent);
            foreach (var binding in rig.Reefs)
            {
                if (!binding.Sail || !binding.Rope || binding.Rope.sail != binding.Sail ||
                    binding.Sail.shipRigidbody != boat.Body || binding.Reversed != binding.Rope.reverseReefing)
                    throw new InvalidOperationException("Reef linkage changed during control.");
                Set(binding.Rope, (float)NavigationMath.ReefLength(DeploymentFor(binding.Sail), binding.Reversed));
            }
            foreach (var group in rig.Groups)
            {
                // Keep trimming the retained sail for drive; do not spend winch
                // input moving completely furled yards during the emergency.
                if (urgent && !group.Members.Contains(emergencySail)) continue;
                float target = BestAngle(group);
                group.TargetAngle = target;
                float command = SheetCommandAngle(group, target);
                foreach (var rope in group.Angles) Set(rope, SheetLength(group, rope, target, command));
            }
        }

        // The length a sheet or brace is set to for its sail's target angle (`command`: SheetCommandAngle's, for a single sheet).
        private static float SheetLength(SailGroup group, RopeController rope, float target, float command)
        {
            var s = group.Primary;
            if (rope is RopeControllerSailAngle a)
            {
                if (a.limitBoth)
                    return (float)ControlMath.IntervalLength(command, a.limitBothClosestAngle - a.closestAnglePadding,
                        a.limitBothClosestAngle + a.closestAnglePadding, s.minAngle, s.maxAngle);
                if (a.limitMax) return (float)NavigationMath.InverseLerp(s.minAngle, s.maxAngle, Mathf.Min(s.maxAngle, target + 3));
                return (float)NavigationMath.InverseLerp(s.maxAngle, s.minAngle, Mathf.Max(s.minAngle, target - 3));
            }
            if (rope is RopeControllerSailAngleSquare square)
                // The left brace sets the upper limit; the right sets the
                // lower limit. Leave the same small free band as paired sheets.
                return square.side == RopeControllerSailAngleSquare.WinchSide.left
                    ? (float)NavigationMath.InverseLerp(s.minAngle, s.maxAngle, Mathf.Min(s.maxAngle, target + 3))
                    : (float)NavigationMath.InverseLerp(s.maxAngle, s.minAngle, Mathf.Max(s.minAngle, target - 3));
            var j = (RopeControllerSailAngleJib)rope;
            return (float)ControlMath.IntervalLength(target, j.closestAngle - j.closestAnglePadding,
                j.closestAngle + j.closestAnglePadding, s.minAngle, s.maxAngle);
        }

        // Decision 138 (the user, 2026-10-10 night, on CI's trace, run 20261010-203125: "Trim at once"): back from the clock her
        // sails are as her sailor far away would have had them, before her first physics step: each unrolled to its planned share
        // (Plan first) and each yard turned at once to the angle her trim wants for the wind on her heading at her speed, its
        // sheets or braces at that angle's length. The Dhow's lateen had come back furled and on her centre line (held there at the
        // berth, VoyageDocking) on a broad reach at 2.9 m/s; she swung toward the wind at 8.9 degrees a second before her winch
        // had eased it, and never bore away again. The game's own rope code applies each length at once, as a frame would (each
        // sheet's Update, the reef's unroll). A yard is turned about its hinge as the trim's predictions turn it (BestAngleFrom);
        // one that doesn't read its new angle afterwards is turned back and left to its sheet. Returns what was set.
        internal string TrimAtOnce()
        {
            var report = new List<string>();
            foreach (var binding in rig.Reefs)
            {
                if (!binding.Sail || !binding.Rope || binding.Rope.sail != binding.Sail || binding.Sail.shipRigidbody != boat.Body) continue;
                binding.Rope.currentLength = Mathf.Clamp01((float)NavigationMath.ReefLength(DeploymentFor(binding.Sail), binding.Reversed));
                binding.Rope.changed = true;
                foreach (var step in new[] { "UpdateSailUnroll", "UpdateClothScale", "UpdateWindCloth" }) CallGame(binding.Rope, step);
            }
            foreach (var group in rig.Groups)
            {
                var sail = group.Primary;
                if (!sail || !group.Hinge || sail.shipRigidbody != boat.Body || group.Angles.Count == 0) continue;
                float before = CurrentAngle(group), target = BestAngle(group);
                group.TargetAngle = target;
                foreach (var rope in group.Angles) { rope.currentLength = Mathf.Clamp01(SheetLength(group, rope, target, target)); rope.changed = true; }
                var axis = group.Hinge.transform.TransformDirection(group.Hinge.axis).normalized;
                var pivot = group.Hinge.transform.TransformPoint(group.Hinge.anchor);
                float turn = Mathf.DeltaAngle(before, target);
                group.Hinge.transform.RotateAround(pivot, axis, turn);
                foreach (var rope in group.Angles) CallGame(rope, "Update");
                float after = CurrentAngle(group);
                string how = "at " + after.ToString("F0");
                if (Mathf.Abs(Mathf.DeltaAngle(after, target)) > 3f)
                {
                    group.Hinge.transform.RotateAround(pivot, axis, -turn);
                    foreach (var rope in group.Angles) CallGame(rope, "Update");
                    how = "its yard read " + after.ToString("F0") + " once turned, so it was turned back to " + CurrentAngle(group).ToString("F0") + " and left to its sheet";
                }
                var body = group.Hinge.GetComponent<Rigidbody>();
                if (body && !body.isKinematic) { body.angularVelocity = boat.Body.angularVelocity; body.velocity = boat.Body.GetPointVelocity(body.worldCenterOfMass); }
                report.Add(sail.name + " from " + before.ToString("F0") + " to its trim's " + target.ToString("F0") + " (" + how + ", unrolled " + sail.currentUnroll.ToString("F2") + ")");
            }
            return report.Count > 0 ? string.Join("; ", report.ToArray()) : "no sail to set";
        }

        // A game method the rope runs itself every frame (private in the game), run once more now.
        private static void CallGame(object rope, string method)
        {
            var call = AccessTools.Method(rope.GetType(), method);
            if (call == null) throw new MissingMethodException(rope.GetType().Name, method);
            call.Invoke(rope, null);
        }

        // Each sail's unrolled share as the game has it (the trace: "sails" is the planned deployment).
        internal string UnrollReport() => string.Join(", ", rig.Reefs.Where(b => b.Sail).Select(b => b.Sail).Distinct().Select(s => s.currentUnroll.ToString("F2")).ToArray());

        // Every sail's sheets set to hold it on her centre line, at once (no winch, no sailor): moored alongside, a yard swung out
        // reaches over the dock (the user's pictures, 2026-10-07: the Dhow's lateen yard lay on Dragon Cliffs' boardwalk the moment
        // the booking placed her, and laid her over). The same geometry as PlanLimited's, for a target of 0 degrees.
        // Every sail furled at once (no winch, no sailor), for a ship the booking brings to a berth (D16): run 20261007-125753
        // showed her tilted 110 degrees seconds after her placement, a sail along her yard on the water; a sail set and held on
        // her centre line in a beam wind is the most heeling trim there is.
        internal static void FurlAtOnce(RigSnapshot rig)
        {
            foreach (var binding in rig.Reefs)
            {
                if (!binding.Sail) continue;
                binding.Sail.currentUnroll = 0f;
                if (!binding.Rope || binding.Rope.sail != binding.Sail) continue;
                binding.Rope.currentLength = binding.Reversed ? 1f : 0f;
                binding.Rope.changed = true;
            }
        }

        internal static void HoldOnCentreLine(RigSnapshot rig)
        {
            foreach (var group in rig.Groups)
            {
                var s = group.Primary;
                if (!s || s.maxAngle - s.minAngle < 0.01f) continue;
                foreach (var rope in group.Angles)
                {
                    if (!rope) continue;
                    float length;
                    if (rope is RopeControllerSailAngle a && !a.limitBoth)
                        length = a.limitMax ? (float)NavigationMath.InverseLerp(s.minAngle, s.maxAngle, Mathf.Clamp(3f, s.minAngle, s.maxAngle))
                            : (float)NavigationMath.InverseLerp(s.maxAngle, s.minAngle, Mathf.Clamp(-3f, s.minAngle, s.maxAngle));
                    else if (rope is RopeControllerSailAngleSquare square)
                        length = square.side == RopeControllerSailAngleSquare.WinchSide.left
                            ? (float)NavigationMath.InverseLerp(s.minAngle, s.maxAngle, Mathf.Clamp(3f, s.minAngle, s.maxAngle))
                            : (float)NavigationMath.InverseLerp(s.maxAngle, s.minAngle, Mathf.Clamp(-3f, s.minAngle, s.maxAngle));
                    else length = 0f; // a single sheet limiting both ways, or a jib's: closed
                    rope.currentLength = Mathf.Clamp01(length); rope.changed = true;
                }
            }
        }

        // A sail furled for her limit (decision 153) draws nothing, but the emergency's steerage sail. Decision 199: her reef plan's share
        // for the sail where it plans (UpdateReefPlan), else every sail at the share.
        private float DeploymentFor(Sail sail) => sail != emergencySail && FurledForLimit(sail) ? 0f
            : planShare.TryGetValue(sail, out var planned) ? planned
            : Mathf.Min(Mathf.Min(deploymentLimit, BalanceOf(sail)), emergencySail
                ? (float)EmergencySailPolicy.Deployment(sail == emergencySail, plannedDeployment)
                : plannedDeployment);

        // Decision 199 (Sails\ReefPlan.cs): her sail taken in and set in whole sails, chosen by the game's own forces on her. The plan
        // stands aside (every sail at the share, as before) in the emergency and its recovery, while she backs or lies in irons (126,
        // 155: every sail for the turn), with fewer than two sails, or with her rudder unread. `Rudder`: set by her voyage.
        internal RudderModel Rudder;
        private readonly ReefPlan reefPlan = new ReefPlan();
        private readonly Dictionary<Sail, float> planShare = new Dictionary<Sail, float>();
        private Sail[] planSails;
        private double[] planLast = new double[0];
        private int[] planKind = new int[0];
        private Vector2 windView;
        private bool windViewSet;
        private string reefNote;

        // What her plan changed (a sail taken in or set, another mix, her mix chosen anew), for her voyage's log; once.
        internal string TakeReefNote() { var note = reefNote; reefNote = null; return note; }

        // Her mix chosen anew at the next plan: back from the clock, after her sails were all furled.
        internal void ResetReefPlan()
        {
            reefPlan.Reset(); planShare.Clear(); planLast = new double[0]; planKind = new int[0]; windViewSet = false;
        }

        // Decision 199 (e): the apparent wind on her bow, averaged over SailBalance.Smoothing (8 s), as a direction.
        private void WatchWind(float dt)
        {
            if (!(dt > 0) || !boat.Body) return;
            var local = boat.Body.transform.InverseTransformDirection(Wind.currentWind - boat.Body.velocity);
            var flat = new Vector2(local.x, local.z);
            if (flat.sqrMagnitude < 0.01f) return;
            flat.Normalize();
            windView = windViewSet ? Vector2.Lerp(windView, flat, 1f - Mathf.Exp(-dt / (float)SailBalance.Smoothing)) : flat;
            windViewSet = true;
        }

        private double WindOnBow => windViewSet && windView.sqrMagnitude > 1e-6f ? Mathf.Atan2(windView.x, windView.y) * Mathf.Rad2Deg : double.NaN;

        // Decision 199 (a): her sail power in a sail (ReefPlan.SailPower) on the game's own numbers: its area (Sail.GetRealSailPower),
        // the height of its centre of effort as the game reads it, its type.
        private static double SailPowerOf(Sail sail) => ReefPlan.SailPower(sail.GetRealSailPower(), sail.windcenter.position.y,
            sail.category == SailCategory.junk ? 0.75 : sail.category == SailCategory.gaff ? 0.85 : 1);

        private void UpdateReefPlan(bool urgent)
        {
            if (planSails == null)
                planSails = rig.Reefs.Where(b => b.Sail && b.Sail.shipRigidbody == boat.Body).Select(b => b.Sail).Distinct().ToArray();
            // Her sails have reached the last plan (ReefPlan.Settle): the next swap may come.
            bool settled = true;
            for (int i = 0; i < planLast.Length && i < planSails.Length; i++)
                if (planSails[i] && Math.Abs(planSails[i].currentUnroll - planLast[i]) > ReefPlan.Settle) settled = false;
            planShare.Clear();
            if (urgent || emergencySail || TurnOnly || Astern || Rudder == null || planSails.Length < 2 || planSails.Any(s => !s || !s.windcenter))
            {
                if (planLast.Length > 0) { reefPlan.Reset(); planLast = new double[0]; planKind = new int[0]; }
                return;
            }
            var dragPoint = boat.Body.position;
            var forward = boat.Body.transform.forward;
            var right = boat.Body.transform.right;
            var input = new ReefSail[planSails.Length];
            for (int i = 0; i < planSails.Length; i++)
            {
                var sail = planSails[i];
                // Its push as the game gives it now, set full and without the wind's shadow, and its turn on her about the point the
                // water holds her sideways at (Crest's BoatProbes puts its side drag at her body's position).
                var force = VanillaSailForces.Predict(sail, Quaternion.identity, sail.windcenter.position, 1f, false);
                var push = forward * force.Forward + right * force.Sideways;
                input[i] = new ReefSail
                {
                    Power = SailPowerOf(sail),
                    Drive = force.Forward,
                    Moment = Vector3.Cross(sail.windcenter.position - dragPoint, push).y,
                    Cap = FurledForLimit(sail) ? 0 : BalanceOf(sail),
                };
            }
            float speed = Vector3.Dot(boat.Body.velocity, forward);
            double demand = Mathf.Min(plannedDeployment, deploymentLimit);
            var shares = reefPlan.Plan(input, demand, Rudder.HelmFor(1, speed), Rudder.DragFor(1, speed), WindOnBow, settled);
            for (int i = 0; i < planSails.Length; i++) planShare[planSails[i]] = (float)shares[i];
            NoteReefPlan(input, shares, demand, speed);
            planLast = shares;
        }

        // Decision 199: what her plan changed, for the log (TakeReefNote): a sail taken in (from full) or set (from furled), another mix,
        // her mix chosen anew; with her sail power carried, the helm her rudder needs to hold her against her sails' turn, and her sails'
        // drive less her rudder's drag.
        private void NoteReefPlan(ReefSail[] input, double[] shares, double demand, float speed)
        {
            var kind = new int[shares.Length];
            for (int i = 0; i < shares.Length; i++) kind[i] = shares[i] <= 0 ? 0 : shares[i] >= input[i].Cap ? 2 : 1;
            bool first = planKind.Length != kind.Length;
            var takenIn = new List<string>();
            var set = new List<string>();
            if (!first)
                for (int i = 0; i < kind.Length; i++)
                {
                    if (planKind[i] == 2 && kind[i] < 2) takenIn.Add(SailLabel(planSails[i]));
                    if (planKind[i] == 0 && kind[i] > 0) set.Add(SailLabel(planSails[i]));
                }
            planKind = kind;
            string change = reefPlan.Change;
            if (change == null && takenIn.Count == 0 && set.Count == 0) return;
            ReefPlan.Totals(input, shares, out double drive, out double moment);
            double all = 0, carried = 0;
            for (int i = 0; i < input.Length; i++) { all += input[i].Power; carried += shares[i] * input[i].Power; }
            var text = new System.Text.StringBuilder();
            if (change == "fresh" || first)
            {
                text.Append("sets her sails by decision 199: ");
                for (int i = 0; i < kind.Length; i++)
                    text.Append(i > 0 ? "; " : "").Append(SailLabel(planSails[i])).Append(' ')
                        .Append(input[i].Cap <= 0 ? "not drawing (decision 153)" : kind[i] == 2 ? "full" : kind[i] == 0 ? "furled" : "at " + shares[i].ToString("P0"))
                        .Append(" (power ").Append(input[i].Power.ToString("F0")).Append(')');
            }
            else
            {
                text.Append(change == "swap" ? "changes her sails over (decision 199, " + reefPlan.SwapWhy + ")" : "takes in or sets a sail (decision 199)");
                if (set.Count > 0) text.Append(": sets ").Append(string.Join(", ", set.ToArray()));
                if (takenIn.Count > 0) text.Append(set.Count > 0 ? ", takes in " : ": takes in ").Append(string.Join(", ", takenIn.ToArray()));
            }
            text.Append("; ").Append((all > 0 ? carried / all : 0).ToString("P0")).Append(" of her sail power (her share ").Append(demand.ToString("P0"))
                .Append("), her helm to hold her against her sails' turn ").Append(Rudder.HelmFor(moment, speed).ToString("F2")).Append(" of full (")
                .Append(ReefPlan.HelmLimit.ToString("F2")).Append(" at most), her sails' drive ").Append(drive.ToString("F0")).Append(" N less her rudder's drag ")
                .Append(Rudder.DragFor(moment, speed).ToString("F0")).Append(" N at ").Append(speed.ToString("F1")).Append(" m/s");
            reefNote = text.ToString();
        }

        // A sail by its name and place (two of a rig's sails can share a name: the Brig's square sails): its centre of effort's height
        // and how far ahead of her middle (her body's position) it lies.
        private string SailLabel(Sail sail)
        {
            if (!sail) return "a sail gone";
            var c = sail.windcenter ? sail.windcenter.position : sail.transform.position;
            float ahead = Vector3.Dot(c - boat.Body.position, boat.Body.transform.forward);
            return sail.name + " (" + c.y.ToString("F0") + " m up, " + (ahead >= 0 ? ahead.ToString("F0") + " m ahead" : (-ahead).ToString("F0") + " m aft") + ")";
        }

        // Decision 128 (SailBalance): each sail's balance reef caps its deployment (1: full). Balance(+1), weather helm: her aftmost
        // sail still above BalanceFloor is reefed a step (a mizzen first); Balance(-1), lee helm: her foremost (a jib first); a
        // single sail is the one; Balance(0): her most reefed sail is let out a step. What changed, or null.
        internal const float BalanceStep = 0.1f, BalanceFloor = 0.3f;
        private readonly Dictionary<Sail, float> balanceReef = new Dictionary<Sail, float>();
        internal float BalanceOf(Sail sail) => sail && balanceReef.TryGetValue(sail, out var cap) ? cap : 1f;

        internal string Balance(int helm)
        {
            var own = rig.Reefs.Where(b => b.Sail && b.Sail.shipRigidbody == boat.Body).Select(b => b.Sail).Distinct().ToList();
            if (own.Count == 0) return null;
            if (helm == 0)
            {
                // Decision 225 (19): her most reefed sail that is drawing (as the reef side's filter below); one furled for her limit or taken
                // in by her plan was let out for nothing, and the sail that needed it waited.
                int at = LetOutChoice(own.Select(s => BalanceOf(s)).ToList(), own.Select(s => Drawing(s)).ToList());
                if (at < 0) return null;
                var reefed = own[at];
                balanceReef[reefed] = Mathf.Min(1f, BalanceOf(reefed) + BalanceStep);
                return reefed.name + " let out to " + BalanceOf(reefed).ToString("P0");
            }
            var mass = boat.Body.worldCenterOfMass;
            var forward = boat.Body.transform.forward;
            // A sail furled for her limit (decisions 153, 190) draws nothing: reefing it would change nothing (the Cog on her jib alone,
            // her square furled aft of it, would have spent seven steps of a weather helm's reef on the square). Nor one her reef plan has
            // taken in (decision 199).
            var sail = own.Where(Drawing)
                .OrderBy(s => helm * Vector3.Dot(s.windcenter.position - mass, forward)).FirstOrDefault(s => BalanceOf(s) > BalanceFloor + 0.001f);
            if (!sail) return null;
            balanceReef[sail] = Mathf.Max(BalanceFloor, BalanceOf(sail) - BalanceStep);
            return sail.name + " reefed to " + BalanceOf(sail).ToString("P0") + (own.Count > 1 ? " (her " + (helm > 0 ? "aftmost" : "foremost") + " sail not yet at " + BalanceFloor.ToString("P0") + ")" : "");
        }

        // A sail drawing: not furled for her limit (153, 190), not taken in by her reef plan (199).
        private bool Drawing(Sail s) => !FurledForLimit(s) && !(planShare.TryGetValue(s, out var planned) && planned <= 0f);

        // Decision 225 (19): the sail a let-out goes to: the most reefed (the least cap under full) of those drawing, the first of equals;
        // -1 for none.
        internal static int LetOutChoice(IList<float> caps, IList<bool> drawing)
        {
            int best = -1;
            for (int i = 0; i < caps.Count; i++)
                if (drawing[i] && caps[i] < 1f && (best < 0 || caps[i] < caps[best])) best = i;
            return best;
        }

        private void SelectSteerageSail()
        {
            Sail fallback = null;
            float bestScore = float.NegativeInfinity, fallbackPower = float.NegativeInfinity;
            foreach (var binding in rig.Reefs)
            {
                var sail = binding.Sail;
                if (!sail || sail.shipRigidbody != boat.Body || !sail.IsInstalled()) continue;
                float height = Mathf.Max(1, sail.windcenter.position.y - boat.Body.worldCenterOfMass.y);
                float power = sail.GetRealSailPower() / height;
                if (power > fallbackPower) { fallbackPower = power; fallback = sail; }
                var force = VanillaSailForces.Predict(sail, Quaternion.identity, sail.windcenter.position,
                    (float)EmergencySailPolicy.SteerageDeployment);
                if (force.Forward <= .01f) continue;
                float score = force.Forward / (1 + Mathf.Abs(force.Sideways) * height);
                if (score > bestScore) { bestScore = score; emergencySail = sail; }
            }
            if (!emergencySail) emergencySail = fallback;
            if (!emergencySail) throw new InvalidOperationException("No usable sail remains for emergency steerage.");
            Log.Info("Emergency steerage sail: " + emergencySail.name + "; retained at15%; other sails reef through maximum-input winches. Selection held through recovery.");
        }

        private float SheetCommandAngle(SailGroup group, float desired)
        {
            if (group.Angles.Count != 1 || !(group.Angles[0] is RopeControllerSailAngle line) || !line.limitBoth)
                return desired;
            float current = Mathf.DeltaAngle(0, line.currentAngle);
            var axis = group.Hinge.transform.TransformDirection(group.Hinge.axis).normalized;
            var pivot = group.Hinge.transform.TransformPoint(group.Hinge.anchor);
            var body = group.Hinge.GetComponent<Rigidbody>();
            float torque = Vector3.Dot(Vector3.Cross(group.Primary.windcenter.position - pivot,
                (Wind.currentWind - boat.Body.velocity) * .22f * group.Primary.GetCurrentShadowMult()), axis);
            if (body.useGravity)
                torque += Vector3.Dot(Vector3.Cross(body.worldCenterOfMass - pivot, Physics.gravity * body.mass), axis);
            float closed = line.limitBothClosestAngle;
            // Easing a single sheet cannot move a backwinded yard across its
            // centre. Pull it in first when wind still holds it on the old side.
            if ((current - closed) * (desired - closed) < 0 &&
                Mathf.Abs(current - closed) > line.closestAnglePadding + 1 && (desired - closed) * torque < -.001f)
                return closed;

            // Real stock yards settle slightly beyond the reported joint limit.
            // Learn only a small, steady, tensioned offset, then command the
            // limit that achieves the scored physical angle. This matters at
            // the sharp luffing boundary; no sail/body rotation is imposed.
            if (Mathf.Abs(torque) > .01f)
            {
                float boundary = torque > 0 ? group.Hinge.limits.max : group.Hinge.limits.min;
                float bias = current - boundary;
                float rate = Vector3.Dot(body.angularVelocity - boat.Body.angularVelocity, axis);
                if (Mathf.Abs(bias) <= 3 && Mathf.Abs(rate) < .08f && Mathf.Abs(bias - group.SheetLimitBias) > .05f)
                    group.SheetLimitBias = Mathf.Lerp(group.SheetLimitBias, bias, .25f);
            }
            return desired - group.SheetLimitBias;
        }

        private void Set(RopeController rope, float target)
        {
            if (!NavigationMath.Finite(target)) throw new InvalidOperationException("Non-finite sail target.");
            if (targets.TryGetValue(rope, out var previous) && Mathf.Abs(previous - target) > 0.001f)
                throw new InvalidOperationException("Conflicting targets for a shared rope.");
            targets[rope] = Mathf.Clamp01(target);
        }

        private float BestAngle(SailGroup group)
        {
            var sail = group.Primary;
            var wind = Wind.currentWind - boat.Body.velocity;
            var axis = group.Hinge.transform.TransformDirection(group.Hinge.axis).normalized;
            float current = CurrentAngle(group);
            if (wind.sqrMagnitude < 0.01f) return current;
            return BestAngleFrom(group, sail, wind, axis, current);
        }

        // Decision 133, by the sail's own geometry: the least angle between her bow and an apparent wind (on the side it lies worse)
        // at which some angle the sail's controls can hold it at gets `wanted` of the wind (UpwindLimits.WindShare) by the game's law
        // (Sail.GetWindAngle, Sail.UpdateWindForceOnSail: as VanillaSailForces.Predict measures it), a degree at a time; NaN when
        // no angle of the wind gives it that share. A single sheet holds the sail only on the side the wind pushes it to, no
        // closer than its closest angle; a jib's sheets no closer than theirs (less their slack); braces anywhere in the hinge's range.
        private double GeometricMinimum(SailGroup group, double wanted)
        {
            var sail = group.Primary;
            if (!group.Hinge || !sail.windcenter) return double.NaN;
            var axis = group.Hinge.transform.TransformDirection(group.Hinge.axis).normalized;
            var pivot = group.Hinge.transform.TransformPoint(group.Hinge.anchor);
            float current = CurrentAngle(group);
            var single = group.Angles.OfType<RopeControllerSailAngle>().FirstOrDefault(a => a && a.limitBoth);
            var jibs = group.Angles.OfType<RopeControllerSailAngleJib>().Where(j => j).ToList();
            float jibClosest = jibs.Count > 0 ? jibs.Min(j => Mathf.Abs(j.closestAngle) - j.closestAnglePadding) : 0f;
            double efficiency = NavigationMath.Clamp(sail.upwindEfficiency, 0, 1);
            var forward = Vector3.ProjectOnPlane(boat.Body.transform.forward, Vector3.up).normalized;
            double worst = double.NaN;
            foreach (int side in new[] { 1, -1 })
            {
                double least = double.NaN;
                for (int off = 0; off <= 179 && double.IsNaN(least); off++)
                {
                    // The apparent wind from `off` degrees off her bow on this side, blowing toward the other.
                    var wind = -(Quaternion.AngleAxis(side * off, Vector3.up) * forward);
                    for (float candidate = sail.minAngle + 1; candidate < sail.maxAngle; candidate += 1)
                    {
                        if (single && (candidate - single.limitBothClosestAngle) * Vector3.Dot(Vector3.Cross(Quaternion.AngleAxis(candidate - current, axis) * (sail.windcenter.position - pivot), wind), axis) < -0.001f) continue;
                        if (jibs.Count > 0 && Mathf.Abs(candidate) < jibClosest) continue;
                        var rotation = Quaternion.AngleAxis(candidate - current, axis);
                        var windAxis = rotation * sail.windcenter.TransformDirection(sail.squareSail ? Vector3.up : Vector3.right);
                        float incidence = Vector3.Angle(wind, windAxis);
                        if (incidence > 90) incidence = 180 - incidence;
                        float vertical = Vector3.Angle(wind, rotation * sail.windcenter.forward);
                        if (vertical > 90) vertical = 180 - vertical;
                        incidence *= (vertical - (90 - vertical) * 0.44f) / 90;
                        // Aback is no drive (run 20261011-103242: the Cog and the Brig read 0 degrees, their square sails braced into a
                        // head wind catching it on their fronts): the game pushes along the sail's face (its right for a square sail,
                        // else its up), the side the wind blows toward, and that push more than 90 degrees from her bow drives her
                        // backwards (Sail.UpdateWindForceOnSail: pushingBoatBackwards).
                        var face = rotation * sail.transform.TransformDirection(sail.squareSail ? Vector3.right : Vector3.up);
                        var push = Vector3.Angle(wind, face) < Vector3.Angle(wind, -face) ? face : -face;
                        if (Vector3.Angle(push, boat.Body.transform.forward) > 90f) continue;
                        double share = SailForceMath.Captured(1, incidence, efficiency, 1, 1, 1);
                        if (share >= wanted - 1e-6) { least = off; break; }
                    }
                }
                if (double.IsNaN(least)) return double.NaN;
                worst = double.IsNaN(worst) ? least : Math.Max(worst, least);
            }
            return worst;
        }

        // Each sail's angle as its controls measure it and the angle set for it (its sheet's limit), for the minute line
        // (decision 134: how eased her sheets are).
        internal string AngleReport() => string.Join(", ", rig.Groups.Where(g => g != null && g.Primary && g.Hinge).Select(g =>
            g.Primary.name + " at " + CurrentAngle(g).ToString("F0") + " (set " + g.TargetAngle.ToString("F0") + ")").ToArray());

        // The sail's angle as its controls measure it.
        private static float CurrentAngle(SailGroup group)
        {
            var sail = group.Primary;
            float current = group.Hinge.angle;
            // Ordinary sheets measure the sail relative to its installed local rest angle.
            // HingeJoint.angle can retain a different reference after hull turns; using it
            // rotates the scored force curve away from the angle the rope actually controls.
            foreach (var rope in group.Angles)
                if (rope is RopeControllerSailAngle ordinary)
                { current=Mathf.DeltaAngle(0,ordinary.currentAngle); break; }
                else if (rope is RopeControllerSailAngleSquare square)
                { current=square.squareAngleMaster.GetCurrentAngle(); break; }
            var jib = sail.GetComponent<JibAngleMaster>();
            if (jib)
            {
                // Vanilla ResetHingeRestingRot establishes local identity after
                // installation. HingeJoint.angle drifts with hull turns in this
                // player build, while initialLocalRot may still be the prefab's
                // pre-installation 90-degree rotation. Read the actual local
                // twist around the installed hinge axis instead of either value.
                var local = group.Hinge.transform.localRotation;
                float twist = Vector3.Dot(new Vector3(local.x, local.y, local.z), group.Hinge.axis.normalized);
                current = Mathf.DeltaAngle(0, 2 * Mathf.Atan2(twist, local.w) * Mathf.Rad2Deg);
            }
            return current;
        }

        private float BestAngleFrom(SailGroup group, Sail sail, Vector3 wind, Vector3 axis, float current)
        {
            var jib = sail.GetComponent<JibAngleMaster>();
            var pivot = group.Hinge.transform.TransformPoint(group.Hinge.anchor);
            var jibBody = jib ? group.Hinge.GetComponent<Rigidbody>() : null;
            var singleSheet=group.Angles.Count==1 ? group.Angles[0] as RopeControllerSailAngle : null;
            Func<double,double> sheetTorque=null;
            if(singleSheet && singleSheet.limitBoth)
            {
                var body=group.Hinge.GetComponent<Rigidbody>();
                var windLever=sail.windcenter.position-pivot;
                var gravityLever=body.worldCenterOfMass-pivot;
                var sailWind=wind*.22f*sail.GetCurrentShadowMult();
                sheetTorque=angle=> {
                    var turn=Quaternion.AngleAxis((float)angle-current,axis);
                    float moment=Vector3.Dot(Vector3.Cross(turn*windLever,sailWind),axis);
                    if(body.useGravity) moment+=Vector3.Dot(Vector3.Cross(turn*gravityLever,Physics.gravity*body.mass),axis);
                    return moment;
                };
            }
            var samples = trimSamples; samples.Clear();
            // Set to turn her (TurnSide): each angle's yaw about her centre of mass from the game's own forces on her, each at its
            // sail's centre of effort (Sail.ApplyForce: the forward force along her bow, the sideways one along her beam).
            var shipForward = boat.Body.transform.forward;
            var shipRight = boat.Body.transform.right;
            var massCentre = boat.Body.worldCenterOfMass;
            // Each member's deployment once: it doesn't change with the candidate angle, and it was asked again at every one (the review
            // of 2026-10-13: a closure and three lookups each, about 180 candidates a sail every half second). The same values.
            var members = group.Members;
            var deployed = new float[members.Count];
            for (int k = 0; k < deployed.Length; k++) deployed[k] = DeploymentFor(members[k]);
            for (float candidate = sail.minAngle + 1; candidate < sail.maxAngle; candidate += 1)
            {
                var rotation = Quaternion.AngleAxis(candidate - current, axis);
                // One sheet limits a free-swinging yard; it cannot hold the yard on
                // the opposite side from the torque produced by the actual wind.
                if (group.Angles.Count == 1 && group.Angles[0] is RopeControllerSailAngle sheet && sheet.limitBoth)
                {
                    var lever = rotation * (sail.windcenter.position - pivot);
                    float torque = Vector3.Dot(Vector3.Cross(lever, wind), axis);
                    if ((candidate - sheet.limitBothClosestAngle) * torque < -0.001f) continue;
                    if(!ControlMath.CanReachSingleSheetAngle(current,candidate,
                        sheet.limitBothClosestAngle-sheet.closestAnglePadding,sheet.limitBothClosestAngle+sheet.closestAnglePadding,sheetTorque)) continue;
                }
                if (jibBody)
                {
                    // Inclined jibs feel both the game's wind force and gravity.
                    // Score only angles that the two actual sheet intervals can
                    // hold against that torque; neither rope can push the sail.
                    float torque = Vector3.Dot(Vector3.Cross(rotation * (sail.windcenter.position - pivot),
                        wind * .22f * sail.GetCurrentShadowMult()), axis);
                    if (jibBody.useGravity)
                        torque += Vector3.Dot(Vector3.Cross(rotation * (jibBody.worldCenterOfMass - pivot), Physics.gravity * jibBody.mass), axis);
                    float minimum = sail.minAngle, maximum = sail.maxAngle;
                    foreach (var rope in group.Angles)
                    {
                        var line = (RopeControllerSailAngleJib)rope;
                        float length = (float)ControlMath.IntervalLength(candidate, line.closestAngle - line.closestAnglePadding,
                            line.closestAngle + line.closestAnglePadding, sail.minAngle, sail.maxAngle);
                        minimum = Mathf.Max(minimum, Mathf.Lerp(line.closestAngle - line.closestAnglePadding, sail.minAngle, length));
                        maximum = Mathf.Min(maximum, Mathf.Lerp(line.closestAngle + line.closestAnglePadding, sail.maxAngle, length));
                    }
                    if (!ControlMath.CanHoldAngle(candidate, minimum, maximum, torque)) continue;
                    if (!JibReachableBySheets(group, jibBody, candidate, current, pivot, axis, wind)) continue;
                }
                float forward = 0, side = 0, moment = 0, incidence = 90;
                for (int k = 0; k < members.Count; k++)
                {
                    var member = members[k];
                    var force = VanillaSailForces.Predict(member, rotation, pivot, deployed[k]);
                    forward += force.Forward; side += Mathf.Abs(force.Sideways); incidence = Mathf.Min(incidence, force.Incidence);
                    if (TurnSide != 0)
                    {
                        var centre = pivot + rotation * (member.windcenter.position - pivot);
                        moment += Vector3.Cross(centre - massCentre, shipForward * force.Forward + shipRight * force.Sideways).y;
                    }
                }
                if (!NavigationMath.Finite(forward) || !NavigationMath.Finite(side) || !NavigationMath.Finite(moment)) throw new InvalidOperationException("Invalid sail force prediction.");
                samples.Add(new TrimSample { Angle = candidate, Forward = forward, Sideways = side, Moment = moment, Drawing = incidence >= UpwindLimits.FullDraw });
            }
            // Decision 155: backing, a square sail braced for the most push astern (on a tie, the most turn her way).
            if (Astern && sail.squareSail && samples.Count > 0)
            {
                var back = samples[0];
                foreach (var s in samples)
                    if (s.Forward < back.Forward - 0.001 || Math.Abs(s.Forward - back.Forward) <= 0.001 && TurnSide * s.Moment > TurnSide * back.Moment) back = s;
                TurnMoment += TurnSide * back.Moment;
                return (float)back.Angle;
            }
            if (TurnSide != 0 && samples.Count > 0)
            {
                // Decision 126 through the sail (in irons, and backing's sails other than square ones): the angle with the most
                // turning moment her way, whatever its drive (a sail held aback). Decision 178 took out decision 134's eased sheet.
                TrimSample turn = default;
                bool found = false;
                foreach (var s in samples)
                    if (!found || TurnSide * s.Moment > TurnSide * turn.Moment) { turn = s; found = true; }
                TurnMoment += TurnSide * turn.Moment;
                return (float)turn.Angle;
            }
            double closed = double.NaN;
            if (!sail.squareSail)
            {
                double jibCentre = 0; int jibLines = 0;
                foreach (var rope in group.Angles)
                    if (rope is RopeControllerSailAngle a && a.limitBoth) { closed = a.limitBothClosestAngle; break; }
                    else if (rope is RopeControllerSailAngleJib j) { jibCentre += j.closestAngle; jibLines++; }
                // Opposing jib sheets pull around their common centre, not
                // whichever sheet happened to be enumerated first.
                if (jibLines > 0) closed = jibCentre / jibLines;
            }
            return (float)group.Trim.Choose(samples, current, closed, Time.time);
        }

        private static bool JibReachableBySheets(SailGroup group, Rigidbody body, float candidate,
            float current, Vector3 pivot, Vector3 axis, Vector3 wind)
        {
            float pullMin = float.PositiveInfinity, pullMax = float.NegativeInfinity;
            foreach (RopeControllerSailAngleJib line in group.Angles)
            {
                pullMin = Mathf.Min(pullMin, line.closestAngle + line.closestAnglePadding);
                pullMax = Mathf.Max(pullMax, line.closestAngle - line.closestAnglePadding);
            }
            float start, direction;
            if (candidate > pullMax) { start = Mathf.Max(pullMax, Mathf.Min(current, candidate)); direction = 1; }
            else if (candidate < pullMin) { start = Mathf.Min(pullMin, Mathf.Max(current, candidate)); direction = -1; }
            else return true;
            // Beyond the span the sheets can pull across, easing needs torque
            // along the entire path. An apparently stable distant angle beyond
            // a gravity equilibrium cannot be reached by paying out rope.
            for (float angle = start; (candidate - angle) * direction > .001f; angle += direction)
            {
                var rotation = Quaternion.AngleAxis(angle - current, axis);
                float torque = Vector3.Dot(Vector3.Cross(rotation * (group.Primary.windcenter.position - pivot),
                    wind * .22f * group.Primary.GetCurrentShadowMult()), axis);
                if (body.useGravity)
                    torque += Vector3.Dot(Vector3.Cross(rotation * (body.worldCenterOfMass - pivot), Physics.gravity * body.mass), axis);
                if (direction * torque < -.001f) return false;
            }
            return true;
        }

        internal void Apply(float dt)
        {
            WatchWind(dt);
            foreach (var winch in rig.Winches.Values)
                if (winch) GameBindings.WinchInput.SetValue(winch, 0f);
            foreach (var pair in targets)
            {
                var rope = pair.Key;
                if (!rope) throw new InvalidOperationException("Rig changed during control.");
                if (rig.Winches.TryGetValue(rope,out var winch) && winch) WinchDrive.Apply(rope,winch,pair.Value,dt);
                else if (rig.Unwinched.TryGetValue(rope,out var stand) && stand) WinchDrive.ApplyUnwinched(rope,stand,pair.Value,dt);
                else throw new InvalidOperationException("Mapped winch disappeared.");
            }
        }

        internal void StopInputs()
        {
            targets.Clear();
            foreach (var winch in rig.Winches.Values)
                if (winch) GameBindings.WinchInput.SetValue(winch, 0f);
        }
        // (The prototype's completion-reef snapshot, for voyages that ended offshore, isn't needed: every Shipwright voyage docks.)

        // Normal completion uses the same bounded native winch drive as sailing.
        // No sheet movement or direct sail deployment writes are needed to furl.
        internal bool FurlAtNativeRate(float dt)
        {
            StopInputs(); Emergency = false;
            bool furled = true;
            foreach (var binding in rig.Reefs)
            {
                if (!binding.Sail || !binding.Rope || binding.Sail.shipRigidbody != boat.Body ||
                    binding.Rope.sail != binding.Sail || binding.Reversed != binding.Rope.reverseReefing ||
                    !rig.Winches.TryGetValue(binding.Rope, out var winch) || !winch ||
                    !NavigationMath.Finite(binding.Sail.currentUnroll))
                    throw new InvalidOperationException("Reef linkage/sample changed during completion.");
                float target = binding.Reversed ? 1 : 0;
                WinchDrive.Apply(binding.Rope, winch, target, dt);
                if (Mathf.Abs(binding.Rope.currentLength - target) > .000001f || binding.Sail.currentUnroll > .02f)
                    furled = false;
            }
            return furled;
        }

        internal void EmergencyFurlImmediate()
        {
            targets.Clear();
            Emergency = true;
            // Use the acquisition snapshot. A changed rig must not redirect cleanup to another vessel's line.
            foreach (var binding in rig.Reefs)
            {
                try
                {
                    if (!binding.Sail || binding.Sail.shipRigidbody != boat.Body) continue;
                    binding.Sail.currentUnroll = 0f;
                    var reef = binding.Rope;
                    if (!reef || reef.sail != binding.Sail) continue;
                    reef.currentLength = binding.Reversed ? 1f : 0f;
                    reef.changed = true;
                    if (rig.Winches.TryGetValue(reef, out var winch) && winch)
                    {
                        GameBindings.WinchInput.SetValue(winch, 0f);
                        GameBindings.WinchLastAngle.SetValue(winch, Mathf.DeltaAngle(0, winch.transform.localEulerAngles.z));
                    }
                }
                catch (Exception e) { Log.Warn("Emergency furl failed for one sail: " + e.Message); }
            }
            foreach (var winch in rig.Winches.Values)
                if (winch) GameBindings.WinchInput.SetValue(winch, 0f);
        }
    }

    internal sealed class HelmAdapter : IDisposable
    {
        private readonly GPButtonSteeringWheel[] wheels;
        private readonly bool[] wasLocked;
        private readonly float[] limits;
        internal float Demand;
        internal HelmAdapter(GPButtonSteeringWheel wheel) : this(new[] { wheel }) { }
        internal HelmAdapter(IEnumerable<GPButtonSteeringWheel> controls)
        {
            wheels = controls.ToArray();
            if (wheels.Length == 0 || wheels.Any(w => !w || !w.attachedRudder) || wheels.Select(w => w.attachedRudder).Distinct().Count() != 1)
                throw new InvalidOperationException("Helm controls must share one rudder.");
            wasLocked = wheels.Select(w => (bool)GameBindings.WheelLocked.GetValue(w)).ToArray();
            limits = new float[wheels.Length];
        }
        // Decision 132 (the user: "once the direction is right, let go of helm, vanilla's rudder push back will automatically make
        // ship stay in course"): the wheels unlocked and left alone. Unheld and unlocked, the game's wheel follows its rudder
        // (GPButtonSteeringWheel.ExtraLateUpdate), and the rudder's own push back turns it against any turn of the ship, the more
        // the faster she goes (Rudder.ApplyCenteringForce). Apply takes the helm again.
        internal bool Released { get; private set; }

        internal void Release()
        {
            if (Released) return;
            Released = true;
            for (int i = 0; i < wheels.Length; i++)
                if (wheels[i]) GameBindings.WheelLocked.SetValue(wheels[i], false);
        }

        // Where her rudder stands, as a share of full helm in Demand's sense: the first wheel's input over its limit (a free wheel
        // follows the rudder).
        internal float Current
        {
            get
            {
                var wheel = wheels[0];
                float limit = wheel ? (float)GameBindings.WheelLimit.GetValue(wheel) : 0f;
                return wheel && limit > 0 ? Mathf.Clamp(wheel.currentInput / limit, -1f, 1f) : 0f;
            }
        }

        // Decision 199 (c): her rudder by the game's law for her reef plan (Rudder: rudderPower, dragPower; full helm at its hinge's
        // limit, as the wheel sets it), or null where it can't be read.
        internal RudderModel Model()
        {
            var hinge = wheels.Length > 0 && wheels[0] ? wheels[0].attachedRudder : null;
            var rudder = hinge ? hinge.GetComponent<Rudder>() : null;
            if (!rudder) return null;
            try { return new RudderModel(rudder.rudderPower, rudder.dragPower, hinge.limits.max); }
            catch (ArgumentException) { return null; }
        }

        internal void Apply()
        {
            Released = false;
            for (int i = 0; i < wheels.Length; i++)
            {
                limits[i] = wheels[i] ? (float)GameBindings.WheelLimit.GetValue(wheels[i]) : float.NaN;
                if (!NavigationMath.Finite(limits[i]) || limits[i] <= 0) throw new InvalidOperationException("Unsupported wheel limits.");
            }
            // Every installed helm can write the shared native spring, including
            // an unattended locked helm. Give all of them the same normalized
            // demand through their own gearing so update order cannot oppose it.
            for (int i = 0; i < wheels.Length; i++)
            {
                GameBindings.WheelLocked.SetValue(wheels[i], true);
                wheels[i].currentInput = Mathf.Clamp(Demand, -1, 1) * limits[i];
            }
        }
        public void Dispose()
        {
            Demand = 0;
            for (int i = 0; i < wheels.Length; i++)
            {
                var wheel = wheels[i];
                if (!wheel) continue;
                wheel.currentInput = 0;
                if (wheel.attachedRudder)
                {
                    var spring = wheel.attachedRudder.spring;
                    spring.targetPosition = 0;
                    wheel.attachedRudder.spring = spring;
                }
                GameBindings.WheelLocked.SetValue(wheel, wasLocked[i]);
            }
        }
    }

    // ==== VanillaSailForces ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Incidence: the angle the apparent wind meets the sail at (Sail.GetWindAngle), for the trim's full-draw check (decision 134).
    internal struct SailForceSample { internal float Forward, Sideways, Incidence; }
    internal static class VanillaSailForces
    {
        // Rotation is a hypothetical world-space change. No transforms or bodies
        // are changed during scoring. Existing shadow is held at its measured value; `shadow` false leaves it out (decision 199: the
        // user, "leave wind shadow out" of her reef plan).
        internal static SailForceSample Predict(Sail sail, Quaternion rotation, Vector3 pivot, float deployment, bool shadow = true)
        {
            var wind = Wind.currentWind - sail.shipRigidbody.velocity;
            var windAxis = rotation * sail.windcenter.TransformDirection(sail.squareSail ? Vector3.up : Vector3.right);
            float incidence = Vector3.Angle(wind, windAxis);
            if (incidence > 90) incidence = 180 - incidence;
            float vertical = Vector3.Angle(wind, rotation * sail.windcenter.forward);
            if (vertical > 90) vertical = 180 - vertical;
            incidence *= (vertical - (90 - vertical) * 0.44f) / 90;
            var normal = rotation * sail.transform.TransformDirection(sail.squareSail ? Vector3.right : Vector3.up);
            if (Vector3.Dot(normal, wind) <= 0) normal = -normal;
            float angle = Vector3.Angle(normal, sail.shipRigidbody.transform.forward);
            float y = (pivot + rotation * (sail.windcenter.position - pivot)).y;
            float height = Mathf.Lerp(0.8f, 1.4f, Mathf.InverseLerp(0, 22, y));
            double captured = SailForceMath.Captured(wind.magnitude, incidence, sail.upwindEfficiency, height, shadow ? sail.GetCurrentShadowMult() : 1f, Mathf.Max(Debugger.instance.debugYardMult, deployment));
            float power = sail.GetRealSailPower() * 50;
            if (sail.category == SailCategory.junk) power *= 0.75f;
            if (sail.category == SailCategory.gaff) power *= 0.85f;
            return new SailForceSample {
                Incidence = incidence,
                Forward = (float)SailForceMath.Forward(captured, angle, sail.upwindEfficiency) * power,
                Sideways = (float)SailForceMath.Sideways(captured, angle, Vector3.Dot(normal, sail.shipRigidbody.transform.right) >= 0) * power
            };
        }
    }

    // ==== WinchMath ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Maximum keyboard/mouse input, including Run quick-release, from vanilla GPButtonRopeWinch.
    public static class WinchMath
    {
        public static double MaximumInput(bool pulling,double resistance,bool canPull)
        {
            if (!NavigationMath.Finite(resistance)) throw new ArgumentException("Invalid rope resistance.");
            if (!pulling) return -100;
            return canPull ? Math.Max(9.44,Math.Min(50,50-resistance)) : 0;
        }
        public static double LengthPerSecond(double input,double rotationSpeed,double gearRatio)
        {
            if (!NavigationMath.Finite(input) || !NavigationMath.Finite(rotationSpeed) || !NavigationMath.Finite(gearRatio) || rotationSpeed<=0 || gearRatio<=0)
                throw new ArgumentException("Invalid winch gearing.");
            return Math.Abs(input)*1.4*rotationSpeed*0.75/360/gearRatio;
        }
    }

    // ==== EmergencySailPolicy ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public static class EmergencySailPolicy
    {
        public const double SteerageDeployment = 0.15;

        // One retained sail keeps steerage. Other independently reefed sails
        // reopen progressively after the low-heel recovery dwell.
        public static double Deployment(bool retained, double demand)
        {
            if (!NavigationMath.Finite(demand)) throw new ArgumentException("Invalid emergency deployment.");
            demand = NavigationMath.Clamp(demand, SteerageDeployment, 1);
            return retained ? demand : (demand - SteerageDeployment) / (1 - SteerageDeployment);
        }
    }

    // ==== HeelGovernor ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public enum HeelState { Normal, Caution, Urgent }

    public sealed class HeelGovernor
    {
        private double filtered, cautionTime, urgentRecovery, calmTime, reefTime;
        private double previous;
        private bool initialized;
        private bool recoveringFromHeel;
        public HeelState State { get; private set; }
        public double Deployment { get; private set; }

        public HeelGovernor(double initialDeployment = 1)
        {
            if (!NavigationMath.Finite(initialDeployment)) throw new ArgumentException("Invalid initial deployment.");
            Deployment = NavigationMath.Clamp(initialDeployment, 0, 1);
        }

        public void Tick(double rawHeel, double dt, bool takingWater = false)
        {
            if (!NavigationMath.Finite(rawHeel) || !NavigationMath.Finite(dt) || dt <= 0)
                throw new ArgumentException("Invalid heel sample.");
            double heel = Math.Abs(rawHeel);
            if (!initialized) { filtered = heel; previous = heel; initialized = true; }
            filtered += (heel - filtered) * (1 - Math.Exp(-dt / 0.5));
            var riseRate = Math.Max(0, (heel - previous) / dt);
            previous = heel;

            // Emergency reefing follows this hull's actual deck overflow, not
            // a universal heel angle. Different hulls immerse their deck at
            // different angles and wave heights.
            if (takingWater)
            {
                State = HeelState.Urgent;
                recoveringFromHeel = true;
                Deployment = EmergencySailPolicy.SteerageDeployment;
                urgentRecovery = calmTime = cautionTime = reefTime = 0;
                return;
            }
            if (State == HeelState.Urgent)
            {
                urgentRecovery += dt;
                if (urgentRecovery < 5) return;
                State = HeelState.Caution;
                urgentRecovery = 0;
            }
            cautionTime = filtered >= 20 ? cautionTime + dt : 0;
            if (cautionTime >= 2)
            {
                State = HeelState.Caution;
                recoveringFromHeel = true;
                reefTime += dt;
                if (reefTime >= 2) { Deployment = Math.Max(EmergencySailPolicy.SteerageDeployment, Deployment - 0.1); reefTime = 0; }
            }
            else reefTime = 0;

            // After depowering, restore enough sail for steerage in bounded
            // steps. Five percent per fifteen seconds left a recovered upright
            // hull almost powerless for minutes, especially while it was turning.
            // Decision 225 (18): 15 degrees whenever she is under full sail (a governor made new after a hold or back from the clock
            // starts reefed without the recovery flag: at a steady 12 degrees she stayed at about half sail for good).
            double lowHeel = recoveringFromHeel || Deployment < 1 ? 15 : 10;
            // Small wave rolls can rise faster than three degrees/second while
            // staying safely upright. Only veto recovery when that rise could
            // reach the recovery boundary within the next half second.
            double projectedHeel = heel + riseRate * 0.5;
            calmTime = heel < lowHeel && filtered < lowHeel && projectedHeel < lowHeel ? calmTime + dt : 0;
            double recoveryInterval = recoveringFromHeel && Deployment >= 0.3 ? 10 : 5;
            if (calmTime >= recoveryInterval)
            {
                Deployment = Math.Min(1, Deployment + 0.1);
                State = HeelState.Normal;
                calmTime = 0;
                if (Deployment >= 1) recoveringFromHeel = false;
            }
        }
    }

    // ==== ReefPlan ====

    // Decision 199 (the user, 2026-10-12 night: "now when you reefing, you reef all the sail together, which looks rather unnatural ...
    // calculate how many you need to reef, then reef individual sail fully"; "exactly leftover"; "judge the balance of the sail when you see
    // the sails ... other player's custom sail plan"; "take height multiplier into consideration"; "leave wind shadow out"; her helm
    // "reduce it to 0.33"): her sail taken in and set in whole sails, one at a time. Engine-free, host-tested; SailControlAdapters gives it
    // each sail's numbers from the game's own forces on her (VanillaSailForces) and her rudder's (RudderModel).

    // A sail as her reef plan sees it.
    internal struct ReefSail
    {
        // Her sail power in it (ReefPlan.SailPower): what her share of her sail is counted in.
        internal double Power;
        // Its push ahead (N) and its turn on her (N·m, about the point the water holds her sideways at), set full, now.
        internal double Drive, Moment;
        // The most of it she may carry (decision 128's balance reef, else 1); 0: not to be set (decision 153: outside her limit).
        internal double Cap;
    }

    // Her rudder by the game's law (Rudder.FixedUpdate): its turn on her is (rudderPower × her speed + 3.1) N·m for each degree of it,
    // and it drags her back by dragPower × rudderPower × her speed for each degree; full helm is its hinge's limit
    // (GPButtonSteeringWheel sets the rudder at limits.max × the helm's share).
    internal sealed class RudderModel
    {
        internal readonly double Power, Drag, FullAngle;

        internal RudderModel(double power, double drag, double fullAngle)
        {
            if (!NavigationMath.Finite(power) || !NavigationMath.Finite(drag) || !NavigationMath.Finite(fullAngle) || power < 0 || drag < 0 || fullAngle <= 0)
                throw new ArgumentException("Invalid rudder.");
            Power = power; Drag = drag; FullAngle = fullAngle;
        }

        // The share of full helm that holds her against a turn of `moment` (N·m) at `speed` (m/s ahead).
        internal double HelmFor(double moment, double speed) => Math.Abs(moment) / ((Power * Math.Abs(speed) + 3.1) * FullAngle);

        // Its drag (N) holding her against that turn.
        internal double DragFor(double moment, double speed)
        {
            double turning = Power * Math.Abs(speed);
            return Drag * turning * Math.Abs(moment) / (turning + 3.1);
        }
    }

    internal sealed class ReefPlan
    {
        // The user (2026-10-12 night): a mix her rudder holds with at most 0.33 of full helm. Claude's: a swap's clear gain (5%) and the
        // change of her point of sailing that looks again (the apparent wind on her bow, averaged over 8 s, 20 degrees from where it lay).
        internal const double HelmLimit = 0.33, SwapGain = 0.05, SwapShift = 20;
        // Her sails have reached their plan when each is within this share of it (the next swap may come then).
        internal const double Settle = 0.05;
        // Every mix is tried for up to this many sails that may draw (4,096 sets of whole sails); beyond, taken in one at a time from all.
        internal const int Tried = 12;
        private const double Tiny = 1e-9;

        // Decision 199 (a): her sail power in a sail, its area times the game's height multiplier at its centre of effort
        // (Sail.GetWindHeightMult: 0.8 at y 0 to 1.4 from y 22) and its type's factor (Sail.ApplyForce: a junk sail 0.75, a gaff 0.85).
        internal static double SailPower(double area, double centreY, double typeFactor) =>
            area * (0.8 + 0.6 * NavigationMath.Clamp(centreY / 22, 0, 1)) * typeFactor;

        // Her sails' push ahead and turn on her with these shares.
        internal static void Totals(ReefSail[] sails, double[] shares, out double drive, out double moment)
        {
            drive = moment = 0;
            for (int i = 0; i < sails.Length; i++) { drive += shares[i] * sails[i].Drive; moment += shares[i] * sails[i].Moment; }
        }

        private double[] share = new double[0], caps = new double[0];
        private bool held;
        private double chosenWind = double.NaN, judgedTarget = double.NaN, judgedHelm = double.NaN;

        // What the last Plan did beyond moving her sails toward her share: "fresh" (her mix chosen anew) or "swap" (another mix at
        // the same power, SwapWhy saying why); else null.
        internal string Change { get; private set; }
        internal string SwapWhy { get; private set; }

        // Her mix chosen anew at the next Plan (back from the clock, after her sails were all furled, a new rig).
        internal void Reset()
        {
            held = false; share = new double[0]; caps = new double[0]; chosenWind = judgedTarget = judgedHelm = double.NaN;
        }

        // Each sail's share (0 to its cap) for `demand`, her share of her sail (0 to 1): together they carry the sail power the same
        // share on every sail would (each sail's least of the share and its cap), every sail full or furled but one (decision 199 b).
        // `helmPerMoment` and `dragPerMoment`: her rudder at her speed for 1 N·m of turn (RudderModel). `windOnBow`: the apparent wind on
        // her bow, averaged (degrees; NaN: not known). `settled`: her sails have reached the last plan's shares (Settle).
        internal double[] Plan(ReefSail[] sails, double demand, double helmPerMoment, double dragPerMoment, double windOnBow, bool settled)
        {
            if (sails == null) throw new ArgumentNullException(nameof(sails));
            if (!NavigationMath.Finite(demand) || !NavigationMath.Finite(helmPerMoment) || !NavigationMath.Finite(dragPerMoment) || helmPerMoment < 0 || dragPerMoment < 0)
                throw new ArgumentException("Invalid reef plan input.");
            foreach (var s in sails)
                if (!NavigationMath.Finite(s.Power) || !NavigationMath.Finite(s.Drive) || !NavigationMath.Finite(s.Moment) || !NavigationMath.Finite(s.Cap) || s.Power < 0)
                    throw new ArgumentException("Invalid sail for the reef plan.");
            Change = null; SwapWhy = null;
            int n = sails.Length;
            demand = NavigationMath.Clamp(demand, 0, 1);
            var cap = new double[n];
            double all = 0, target = 0;
            for (int i = 0; i < n; i++)
            {
                cap[i] = sails[i].Power > Tiny ? NavigationMath.Clamp(sails[i].Cap, 0, 1) : 0;
                all += cap[i] * sails[i].Power;
                target += Math.Min(demand, cap[i]) * sails[i].Power;
            }
            double eps = Math.Max(Tiny, all * 1e-9);
            double[] next;
            if (!held || share.Length != n)
            {
                next = Best(sails, cap, target, eps, helmPerMoment, dragPerMoment, null);
                held = true; chosenWind = windOnBow; Change = "fresh";
            }
            else
            {
                // Her shares as held: a sail that was full stays full at its cap (decision 128 let it out or reefed it), one that may no
                // longer draw is out, none above its cap.
                next = new double[n];
                for (int i = 0; i < n; i++)
                    next[i] = cap[i] <= 0 ? 0 : caps[i] > 0 && share[i] >= caps[i] - Tiny ? cap[i] : Math.Min(share[i], cap[i]);
                Step(sails, cap, next, target, eps, helmPerMoment, dragPerMoment);
                // Decision 199 (e): another mix at the same power only on a change of her point of sailing or a heavy helm, for a clear gain,
                // once her sails have reached the last plan.
                if (!NavigationMath.Finite(chosenWind)) chosenWind = windOnBow;
                double shift = NavigationMath.Finite(chosenWind) && NavigationMath.Finite(windOnBow) ? Math.Abs(NavigationMath.Delta(chosenWind, windOnBow)) : 0;
                var now = Judge(sails, next, helmPerMoment, dragPerMoment);
                bool heavy = now.Helm > HelmLimit;
                // A heavy helm looked at again only once something changed (her share, or her helm by SwapGain): with no mix held (slow,
                // her rudder weak) the search would run every plan for nothing.
                bool unchanged = shift < SwapShift && Math.Abs(target - judgedTarget) <= eps && Math.Abs(now.Helm - judgedHelm) <= SwapGain * judgedHelm;
                if (settled && (shift >= SwapShift || heavy) && !unchanged)
                {
                    judgedTarget = target; judgedHelm = now.Helm;
                    var best = Best(sails, cap, target, eps, helmPerMoment, dragPerMoment, next);
                    var mix = Judge(sails, best, helmPerMoment, dragPerMoment);
                    bool gain = mix.Helm <= HelmLimit
                        ? heavy || mix.Net > now.Net + SwapGain * Math.Abs(now.Net)
                        : heavy && mix.Helm < now.Helm * (1 - SwapGain);
                    if (gain && Differs(best, next, cap))
                    {
                        next = best; Change = "swap";
                        SwapWhy = heavy ? "her helm would be " + now.Helm.ToString("F2") + " of full, over " + HelmLimit.ToString("F2")
                            : "the apparent wind on her bow moved " + shift.ToString("F0") + " degrees since her sails were chosen";
                    }
                    chosenWind = windOnBow;
                }
            }
            // A sail within a millionth of furled or of its cap is furled or full.
            for (int i = 0; i < n; i++)
            {
                if (next[i] < 1e-6) next[i] = 0;
                else if (next[i] > cap[i] - 1e-6) next[i] = cap[i];
            }
            share = next; caps = cap;
            return (double[])next.Clone();
        }

        // A mix: its drive less her rudder's drag, the helm it needs, and whether the sail partly set in it keeps her held within the
        // limit all the way in and all the way out (its turn on her is linear in its share, so held at both ends is held between).
        private struct Mix { internal double Net, Helm; internal bool EndsHeld; }

        private static Mix Make(double drive, double moment, double helmPer, double dragPer, bool endsHeld = true) =>
            new Mix { Helm = Math.Abs(moment) * helmPer, Net = drive - Math.Abs(moment) * dragPer, EndsHeld = endsHeld };

        private static Mix Judge(ReefSail[] sails, double[] shares, double helmPer, double dragPer)
        {
            Totals(sails, shares, out double drive, out double moment);
            return Make(drive, moment, helmPer, dragPer);
        }

        private static bool Held(double moment, double helmPer) => Math.Abs(moment) * helmPer <= HelmLimit + 1e-12;

        // Decision 199 (c): a mix held within the helm limit beats one that isn't; of two held, one whose partly set sail keeps her held
        // all its way in and out (else her share moving on would force a swap: 3 in a steady wind in the host test before it), then the
        // more drive less rudder drag; of two not held, the lighter helm.
        private static bool Better(Mix a, Mix b)
        {
            bool ah = a.Helm <= HelmLimit + 1e-12, bh = b.Helm <= HelmLimit + 1e-12;
            if (ah != bh) return ah;
            if (!ah) return a.Helm < b.Helm - 1e-12;
            if (a.EndsHeld != b.EndsHeld) return a.EndsHeld;
            return a.Net > b.Net + 1e-9 * (1 + Math.Abs(b.Net));
        }

        // A sail's state: 0 furled, 1 partly set, 2 full.
        private static int Kind(double share, double cap) => share <= 1e-6 ? 0 : share >= cap - 1e-6 ? 2 : 1;

        private static bool Differs(double[] a, double[] b, double[] cap)
        {
            for (int i = 0; i < a.Length; i++) if (cap[i] > 0 && Kind(a[i], cap[i]) != Kind(b[i], cap[i])) return true;
            return false;
        }

        // Decision 199 (b): from the held shares toward `target`, never against it: the sails partly set first, each moving the same
        // share of its room; then one more sail taken in (falling) or set (rising), the best mix that leaves (Better).
        private static void Step(ReefSail[] sails, double[] cap, double[] x, double target, double eps, double helmPer, double dragPer)
        {
            int n = sails.Length;
            for (int guard = 0; guard < 2 * n + 4; guard++)
            {
                double now = 0;
                for (int i = 0; i < n; i++) now += x[i] * sails[i].Power;
                double gap = target - now;
                if (Math.Abs(gap) <= eps) return;
                double room = 0;
                for (int i = 0; i < n; i++)
                    if (cap[i] > 0 && Kind(x[i], cap[i]) == 1) room += gap < 0 ? x[i] * sails[i].Power : (cap[i] - x[i]) * sails[i].Power;
                if (room > Tiny)
                {
                    double f = Math.Min(room, Math.Abs(gap)) / room;
                    for (int i = 0; i < n; i++)
                        if (cap[i] > 0 && Kind(x[i], cap[i]) == 1) x[i] += gap < 0 ? -x[i] * f : (cap[i] - x[i]) * f;
                    continue;
                }
                Totals(sails, x, out double drive, out double moment);
                int pick = -1; double pickTo = 0; Mix best = default;
                for (int i = 0; i < n; i++)
                {
                    if (cap[i] <= 0 || Kind(x[i], cap[i]) != (gap < 0 ? 2 : 0)) continue;
                    double to = gap < 0 ? Math.Max(0, cap[i] + gap / sails[i].Power) : Math.Min(cap[i], gap / sails[i].Power);
                    // Where it is headed as her share moves on: all the way in (falling) or out (rising).
                    double end = gap < 0 ? 0 : cap[i];
                    bool endsHeld = Held(moment + (end - x[i]) * sails[i].Moment, helmPer);
                    var mix = Make(drive + (to - x[i]) * sails[i].Drive, moment + (to - x[i]) * sails[i].Moment, helmPer, dragPer, endsHeld);
                    if (pick < 0 || Better(mix, best)) { pick = i; pickTo = to; best = mix; }
                }
                if (pick < 0) return;
                x[pick] = pickTo;
            }
        }

        // Decision 199 (c, d): the best mix for `target` (Better; of two alike, the one changing fewer sails from `current`): every set of
        // full sails with one sail partly set (or none), for up to Tried sails that may draw; two partly set only where no such mix is held
        // within the limit, one either side of her balance. Beyond Tried sails, taken in one at a time from all set (Step).
        private static double[] Best(ReefSail[] sails, double[] cap, double target, double eps, double helmPer, double dragPer, double[] current)
        {
            int n = sails.Length;
            var x = new double[n];
            var on = new List<int>();
            for (int i = 0; i < n; i++) if (cap[i] > 0) on.Add(i);
            int m = on.Count;
            if (m == 0) return x;
            if (m > Tried)
            {
                foreach (int i in on) x[i] = cap[i];
                Step(sails, cap, x, target, eps, helmPer, dragPer);
                return x;
            }
            int masks = 1 << m;
            var power = new double[masks];
            var drive = new double[masks];
            var moment = new double[masks];
            for (int mask = 1; mask < masks; mask++)
            {
                int low = mask & -mask, rest = mask ^ low, i = on[Bit(low)];
                power[mask] = power[rest] + cap[i] * sails[i].Power;
                drive[mask] = drive[rest] + cap[i] * sails[i].Drive;
                moment[mask] = moment[rest] + cap[i] * sails[i].Moment;
            }
            var kinds = new int[m];
            if (current != null) for (int b = 0; b < m; b++) kinds[b] = Kind(current[on[b]], cap[on[b]]);
            int bestMask = -1, bestJ = -1, bestK = -1, bestChanges = int.MaxValue;
            double bestXj = 0, bestXk = 0;
            Mix best = default;

            // One mix: the full sails `mask`, sail `j` (a bit, -1: none) at `xj` of its power, `k` likewise.
            void Consider(int mask, int j, double xj, int k, double xk)
            {
                double d = drive[mask], t = moment[mask];
                if (j >= 0) { d += xj * sails[on[j]].Drive; t += xj * sails[on[j]].Moment; }
                if (k >= 0) { d += xk * sails[on[k]].Drive; t += xk * sails[on[k]].Moment; }
                // One sail partly set: held with it all the way in and all the way out; two (decision 199 d): as they stand.
                bool endsHeld = k >= 0 || j < 0 || Held(moment[mask], helmPer) && Held(moment[mask] + cap[on[j]] * sails[on[j]].Moment, helmPer);
                var mix = Make(d, t, helmPer, dragPer, endsHeld);
                int changes = 0;
                if (current != null)
                    for (int b = 0; b < m; b++)
                    {
                        int kind = (mask & (1 << b)) != 0 ? 2 : b == j ? Kind(xj, cap[on[b]]) : b == k ? Kind(xk, cap[on[b]]) : 0;
                        if (kind != kinds[b]) changes++;
                    }
                if (bestMask >= 0 && !Better(mix, best) && (Better(best, mix) || changes >= bestChanges)) return;
                bestMask = mask; bestJ = j; bestXj = xj; bestK = k; bestXk = xk; best = mix; bestChanges = changes;
            }

            for (int mask = 0; mask < masks; mask++)
            {
                double left = target - power[mask];
                if (left < -eps) continue;
                if (left <= eps) { Consider(mask, -1, 0, -1, 0); continue; }
                for (int b = 0; b < m; b++)
                {
                    if ((mask & (1 << b)) != 0) continue;
                    var s = sails[on[b]];
                    if (cap[on[b]] * s.Power < left - eps) continue;
                    Consider(mask, b, Math.Min(cap[on[b]], left / s.Power), -1, 0);
                }
            }
            if (bestMask < 0 || best.Helm > HelmLimit)
                for (int mask = 0; mask < masks; mask++)
                {
                    double left = target - power[mask];
                    if (left <= eps) continue;
                    for (int b = 0; b < m; b++)
                    {
                        if ((mask & (1 << b)) != 0) continue;
                        for (int c = b + 1; c < m; c++)
                        {
                            if ((mask & (1 << c)) != 0) continue;
                            ReefSail sj = sails[on[b]], sk = sails[on[c]];
                            double fj = cap[on[b]] * sj.Power, fk = cap[on[c]] * sk.Power;
                            if (fj + fk < left - eps) continue;
                            // `a` of the power from j, the rest from k: her turn moment[mask] + a·mj + (left - a)·mk, nought where it can be.
                            double lo = Math.Max(0, left - fk), hi = Math.Min(fj, left);
                            double mj = sj.Moment / sj.Power, mk = sk.Moment / sk.Power;
                            double a = Math.Abs(mj - mk) > Tiny ? -(moment[mask] + left * mk) / (mj - mk) : lo;
                            a = NavigationMath.Clamp(a, lo, hi);
                            Consider(mask, b, a / sj.Power, c, (left - a) / sk.Power);
                        }
                    }
                }
            if (bestMask < 0)
            {
                // Nothing reaches her share (it is more than all her sail): every sail full.
                foreach (int i in on) x[i] = cap[i];
                return x;
            }
            for (int b = 0; b < m; b++) if ((bestMask & (1 << b)) != 0) x[on[b]] = cap[on[b]];
            if (bestJ >= 0) x[on[bestJ]] = bestXj;
            if (bestK >= 0) x[on[bestK]] = bestXk;
            return x;
        }

        private static int Bit(int single)
        {
            int b = 0;
            while ((single >>= 1) != 0) b++;
            return b;
        }
    }

    // ==== SailBalance ====

    // Decision 128 (the user, 2026-10-10): her sails balanced against her helm. "If you met situation like this, you should reef the
    // jib/mizzen sail to retain balance, but if the rudder can handle the unbalance and it will be faster if you let rudder handle it
    // and fully unfurl the sail, you should do it. If sail force is too unbalance that rudder cause too many drag, reef the sail."
    // Engine-free, so it is host-tested; Voyage.BalanceSails feeds it and SailControlAdapters.Balance reefs or lets out the sail.

    public enum BalanceAction { None, ReefAft, ReefFore, LetOut }

    public sealed class SailBalance
    {
        // The helm she needs on a steady course is averaged (Smoothing seconds); every Window seconds, above Heavy (a share of full
        // helm: the game's rudder drag grows with its angle and her speed) the sail making it is reefed a step: weather helm (her
        // bow wanting into the wind) an aft sail, lee helm a fore sail; below Light (her rudder holds her easily) a reefed sail is
        // let out a step, so she carries full sail whenever her rudder can hold her.
        public const double Window = 20, Heavy = 0.6, Light = 0.3, Smoothing = 8;
        private double average, clock;

        // Decision 183 (c) (the user: "add reef fix"), with 186 (the user, 2026-10-11 night, asked whether a reef let out while slow and
        // a weather helm's reef could take turns every few seconds: "sure", to let it out only while her helm is light): under SlowSpeed,
        // with nothing overpowering her helm (`overpowered`: reefing against a weather helm or one building, spinning, backing) and her
        // helm under HelmHold.LightHelm of full, a reef is let out (Voyage.Steer, a step every 2 s).
        public const double SlowSpeed = 1;
        internal static bool SlowLetOut(double forward, bool overpowered, double helm) =>
            forward < SlowSpeed && !overpowered && Math.Abs(helm) < HelmHold.LightHelm;

        // The averaged helm: + weather helm, - lee helm.
        public double WeatherHelm => average;

        public void Reset() { average = 0; clock = 0; }

        public BalanceAction Sample(double weatherHelm, double dt)
        {
            if (!NavigationMath.Finite(weatherHelm) || !NavigationMath.Finite(dt) || dt <= 0) throw new ArgumentException("Invalid helm sample.");
            average += (NavigationMath.Clamp(weatherHelm, -1, 1) - average) * (1 - Math.Exp(-dt / Smoothing));
            if ((clock += dt) < Window) return BalanceAction.None;
            clock = 0;
            if (Math.Abs(average) > Heavy) return average > 0 ? BalanceAction.ReefAft : BalanceAction.ReefFore;
            return Math.Abs(average) < Light ? BalanceAction.LetOut : BalanceAction.None;
        }
    }

    // ==== SailForceMath ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    // Inspected Sailwind 0.39/0.39.1/0.39.2 force law. Inputs are geometry samples,
    // not an alternative force application or a physical aerodynamics model.
    public static class SailForceMath
    {
        public static double Captured(double wind, double incidence, double efficiency, double height, double shadow, double deployment)
        {
            double t = NavigationMath.Clamp(incidence / 90, 0, 1);
            double luff = NavigationMath.Clamp((incidence - 13) / 3, 0, 1);
            return wind * height * shadow * (efficiency + (1 - efficiency) * t) * deployment * luff;
        }
        public static double Forward(double captured, double forceToBow, double efficiency)
        {
            double sign = forceToBow > 90 ? -0.33 : 1;
            double angle = forceToBow > 90 ? 180 - forceToBow : forceToBow;
            return sign * captured * ((90 - angle) / 90 * (1 - efficiency) + efficiency);
        }
        public static double Sideways(double captured, double forceToBow, bool starboard)
        {
            double angle = forceToBow > 90 ? 180 - forceToBow : forceToBow;
            return (starboard ? 1 : -1) * captured * angle / 90 * 1.5;
        }
    }

    // ==== TrimPolicy ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    internal struct TrimSample
    {
        // Moment: the yaw the game's forces on the sail give her about her centre of mass at this angle (N·m, + to starboard),
        // scored only when her sails are set to turn her (SailControlAdapters.TurnSide).
        internal double Angle, Forward, Sideways, Moment;
        // Decision 134: the apparent wind meets every sail of the group at the game's full-draw angle or more (16 degrees; under 13
        // a sail luffs and gives nothing).
        internal bool Drawing;
    }

    // Per acquired sail group. A target is a sheet limit, not an applied sail rotation.
    // Decision 134 (the user: "making biggest forward momemtum while keep lateral force as small as possible"): of the angles
    // giving DriveBand of the most forward drive, the one with the least sideways force, so the sheet is as eased as the drive
    // allows (it was 0.98, and a single sheet hauled 2 degrees tighter toward its closed angle: tight on a beam reach).
    internal sealed class TrimPolicy
    {
        internal const double DriveBand = 0.90;
        internal const double Deadband = 3;
        internal const double RetrimSeconds = 5;
        private bool hasTarget;
        private double target, lastChange;
        private bool pendingRetrim;
        private double pendingAngle, pendingSince;

        internal double Choose(IReadOnlyList<TrimSample> samples, double current, double closedAngle, double now)
        {
            double peak = 0;
            foreach (var sample in samples) peak = Math.Max(peak, sample.Forward);
            if (peak <= 0.001) { pendingRetrim = false; return hasTarget ? target : current; }

            // Never eased past full draw (decision 134): where any angle in the band draws fully, only those count, so a small shift
            // can't tip the sail into luffing.
            bool anyDrawing = false;
            foreach (var sample in samples) if (sample.Forward >= peak * DriveBand && sample.Drawing) anyDrawing = true;
            TrimSample best = default;
            double leastSide = double.PositiveInfinity;
            foreach (var sample in samples)
                if (sample.Forward >= peak * DriveBand && (!anyDrawing || sample.Drawing) && (sample.Sideways < leastSide - 0.001 ||
                    Math.Abs(sample.Sideways - leastSide) <= 0.001 && (sample.Forward > best.Forward + 0.001 ||
                    Math.Abs(sample.Forward - best.Forward) <= 0.001 && Math.Abs(sample.Angle - current) < Math.Abs(best.Angle - current))))
                { best = sample; leastSide = sample.Sideways; }

            if (hasTarget)
                foreach (var held in samples)
                {
                    // All chosen targets lie on the sampled angle grid. Absence
                    // means a changed wind made that target physically infeasible.
                    if (Math.Abs(held.Angle - target) > 0.01) continue;
                    bool good = held.Forward >= peak * DriveBand && held.Sideways <= leastSide + peak * 0.05;
                    bool acceptable = held.Forward >= peak * (DriveBand - 0.05) && held.Sideways <= leastSide + peak * 0.20;
                    // Downwind either wing can produce almost the same drive.
                    // Do not haul a loaded yard across the centre for that tiny
                    // gain: the crossing itself loses much more force. A real
                    // tack still retrims immediately when the held wing is no
                    // longer feasible or no longer in the good force band.
                    bool oppositeWing = NavigationMath.Finite(closedAngle) &&
                        (best.Angle - closedAngle) * (target - closedAngle) < 0;
                    bool stableWing = oppositeWing && held.Forward >= peak * DriveBand &&
                        held.Sideways <= leastSide + peak * .20;
                    if ((good && Math.Abs(best.Angle - target) <= Deadband) || stableWing)
                    { pendingRetrim = false; return target; }
                    if (acceptable)
                    {
                        // Require a persistent improvement, not just an old
                        // target. Otherwise each expiry can ease a jib toward a
                        // brief torque equilibrium and immediately haul it back
                        // when the gust reverses. Unsafe/unreachable held angles
                        // still bypass this dwell below.
                        if (!pendingRetrim || Math.Abs(best.Angle - pendingAngle) > Deadband ||
                            (best.Angle - target) * (pendingAngle - target) <= 0)
                        { pendingRetrim = true; pendingAngle = best.Angle; pendingSince = now; }
                        if (now - pendingSince < RetrimSeconds || now - lastChange < RetrimSeconds) return target;
                    }
                    break;
                }
            pendingRetrim = false;
            if (!hasTarget || Math.Abs(best.Angle - target) > 0.01) lastChange = now;
            target = best.Angle; hasTarget = true;
            return target;
        }
    }

    // ==== WaterIngressSensor ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    internal sealed class WaterIngressSensor
    {
        private readonly BoatContext boat;
        private readonly WaveSplashZone[] zones;
        private readonly SampleHeightHelper[] samples;
        private float overflowObservedAt = float.NegativeInfinity;
        internal float OverflowDepth { get; private set; }

        internal WaterIngressSensor(BoatContext boat)
        {
            this.boat = boat;
            zones = boat.Root.GetComponentsInChildren<WaveSplashZone>(true)
                .Where(z => z.transform.parent && z.transform.parent.GetComponent<BoatDamage>() == boat.Damage).ToArray();
            samples = zones.Select(z => new SampleHeightHelper()).ToArray();
            Log.Info("Water-intake sensor: " + zones.Length + " native deck-overflow points on scene" + boat.Saveable.sceneIndex + ".");
        }

        internal void ObserveOverflow(BoatDamage damage, float depth)
        {
            if (damage == boat.Damage && depth > 0 && NavigationMath.Finite(depth)) overflowObservedAt = Time.time;
        }

        internal bool Sample(float dt)
        {
            OverflowDepth = 0;
            // Match WaveSplashZone's own ocean height/vertical-offset condition,
            // without its last-player-boat restriction. An unoccupied physical
            // transport needs the same early warning as a passenger's vessel.
            for (int i=0;i<zones.Length;i++)
            {
                var zone=zones[i];
                if (!zone || !zone.isActiveAndEnabled) continue;
                var position = zone.transform.position;
                float depth = OceanHeight.GetHeight(samples[i], position) + zone.verticalOffset - position.y;
                if (!NavigationMath.Finite(depth)) throw new InvalidOperationException("Invalid deck-overflow sample.");
                OverflowDepth = Mathf.Max(OverflowDepth, depth);
            }
            return OverflowDepth > 0 || Time.time - overflowObservedAt <= dt + .05f;
        }
    }

    // ==== CargoReadiness ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public sealed class CargoAttachment
    {
        public readonly int Id, ContainerId;
        public readonly bool AttachedToBoat, LinkedToContainer;
        public CargoAttachment(int id, bool attachedToBoat, int containerId = 0, bool linkedToContainer = false)
        { Id = id; AttachedToBoat = attachedToBoat; ContainerId = containerId; LinkedToContainer = linkedToContainer; }
    }

    // Spawned records alone do not establish that cargo has entered the boat's walking-physics space.
    public static class CargoReadiness
    {
        public static bool IsReady(IEnumerable<int> expectedIds, IEnumerable<CargoAttachment> live)
        {
            var expected = new HashSet<int>();
            foreach (int id in expectedIds) if (id <= 0 || !expected.Add(id)) return false;
            var byId = new Dictionary<int, CargoAttachment>();
            var duplicates = new HashSet<int>();
            foreach (var item in live)
            {
                if (byId.ContainsKey(item.Id)) duplicates.Add(item.Id);
                else byId.Add(item.Id, item);
            }
            foreach (int id in expected)
            {
                var visited = new HashSet<int>();
                int current = id;
                while (true)
                {
                    if (!expected.Contains(current) || duplicates.Contains(current) || !visited.Add(current) ||
                        !byId.TryGetValue(current, out var item)) return false;
                    if (item.ContainerId > 0)
                    {
                        if (!item.LinkedToContainer) return false;
                        current = item.ContainerId;
                    }
                    else if (item.AttachedToBoat) break;
                    else return false;
                }
            }
            return true;
        }

        // Why IsReady says no, item by item along the same rules (the user, 2026-10-09: the cargo handover's timeout named no item).
        public static string Explain(IEnumerable<int> expectedIds, IEnumerable<CargoAttachment> live)
        {
            var reasons = new List<string>();
            var expected = new HashSet<int>();
            foreach (int id in expectedIds)
                if (id <= 0) reasons.Add(id + ": not a valid id");
                else if (!expected.Add(id)) reasons.Add(id + ": expected twice");
            var byId = new Dictionary<int, CargoAttachment>();
            var duplicates = new HashSet<int>();
            foreach (var item in live)
            {
                if (byId.ContainsKey(item.Id)) duplicates.Add(item.Id);
                else byId.Add(item.Id, item);
            }
            foreach (int id in expected)
            {
                var visited = new HashSet<int>();
                int current = id;
                while (true)
                {
                    string at = current == id ? id.ToString() : id + " (in crate " + current + ")";
                    if (!expected.Contains(current)) { reasons.Add(at + ": its crate is not her cargo"); break; }
                    if (duplicates.Contains(current)) { reasons.Add(at + ": in the world twice"); break; }
                    if (!visited.Add(current)) { reasons.Add(at + ": crates inside each other in a loop"); break; }
                    if (!byId.TryGetValue(current, out var item)) { reasons.Add(at + ": not in the world"); break; }
                    if (item.ContainerId > 0)
                    {
                        if (!item.LinkedToContainer)
                        {
                            reasons.Add(at + (byId.ContainsKey(item.ContainerId) ? ": not inside its crate " : ": its crate is not in the world, ") + item.ContainerId);
                            break;
                        }
                        current = item.ContainerId;
                    }
                    else { if (!item.AttachedToBoat) reasons.Add(at + ": not aboard her"); break; }
                }
            }
            return reasons.Count == 0 ? "every expected item ready (" + expected.Count + ")" : reasons.Count + " of " + expected.Count + " not ready: " + string.Join("; ", reasons);
        }
    }

    // ==== Headway ====

    // Decision 233 (Claude's, the user's "Yes, keep it"; it widens 206): sailing or holding near the player, a voyage goes on only while she gets
    // somewhere: GainMetres farther from where the watch began than she has been, or nearer her destination than she has been. Her holds
    // start her afresh and her replans change her route, so a watch on either never ends her loop near her yard (hemmed in: the push, her
    // sailor, the backup tow, a hold, again) nor a wait that never ends (no way for Kicia Bay's tow out, an island that never loads, an
    // approach obstructed). Kept in her voyage only: a load begins the watch afresh.
    public sealed class Headway
    {
        public const double GainMetres = 100;
        private double startX, startZ, farthest, nearest;
        public double Hour { get; private set; } = double.NaN;

        // The hour she last got somewhere, given the hour and where she lies now.
        public double Observe(double hour, double x, double z, double toEnd)
        {
            double fromStart = Math.Sqrt((x - startX) * (x - startX) + (z - startZ) * (z - startZ));
            if (double.IsNaN(Hour)) { startX = x; startZ = z; farthest = 0; nearest = toEnd; Hour = hour; }
            else if (fromStart >= farthest + GainMetres || toEnd <= nearest - GainMetres)
            {
                farthest = Math.Max(farthest, fromStart); nearest = Math.Min(nearest, toEnd); Hour = hour;
            }
            return Hour;
        }

        public void Reset() { Hour = double.NaN; }
    }

    // ==== ProgressWatchdog ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    /// <summary>Only a measured improvement towards the current route point earns more time.</summary>
    public sealed class ProgressWatchdog
    {
        private double bestDistance, targetX, targetZ;
        private bool initialized;
        public double StalledSeconds { get; private set; }

        public ProgressWatchdog(double[] snapshot = null)
        {
            string error = ValidateSnapshot(snapshot);
            if (error != null) throw new ArgumentException(error);
            if (snapshot == null) return;
            StalledSeconds = snapshot[0]; bestDistance = snapshot[1];
            targetX = snapshot[2]; targetZ = snapshot[3]; initialized = snapshot[4] == 1;
        }

        // Primitive arrays survive Unity's late-loaded JSON serializer. Preserve
        // the approach baseline as well as elapsed time: saving is not movement.
        public double[] Capture() => new[] { StalledSeconds, bestDistance, targetX, targetZ, initialized ? 1d : 0d };

        public static string ValidateSnapshot(double[] snapshot)
        {
            if (snapshot == null) return null; // Legacy records start a new budget.
            if (snapshot.Length != 5) return "Incomplete progress snapshot.";
            foreach (double value in snapshot)
                if (!NavigationMath.Finite(value)) return "Non-finite progress snapshot.";
            if (snapshot[0] < 0 || snapshot[1] < 0 || Math.Abs(snapshot[2]) > 2000000 || Math.Abs(snapshot[3]) > 2000000 ||
                snapshot[4] != 0 && snapshot[4] != 1) return "Invalid progress snapshot.";
            if (snapshot[4] == 0 && (snapshot[0] != 0 || snapshot[1] != 0 || snapshot[2] != 0 || snapshot[3] != 0))
                return "Uninitialized progress snapshot carries state.";
            return null;
        }

        public void Observe(double dt, double distance, double x, double z)
        {
            if (!NavigationMath.Finite(dt) || dt < 0 || !NavigationMath.Finite(distance) || distance < 0 ||
                !NavigationMath.Finite(x) || !NavigationMath.Finite(z))
                throw new ArgumentException("Invalid progress sample.");
            if (dt == 0) return; // A paused sample cannot earn or consume progress time.
            StalledSeconds += dt;
            if (!initialized || x != targetX || z != targetZ)
            {
                // Replanning, waypoint changes and simulation handovers are not movement.
                initialized = true; targetX = x; targetZ = z; bestDistance = distance;
            }
            else if (distance <= bestDistance - 1.5)
            {
                bestDistance = distance;
                StalledSeconds = 0;
            }
        }
    }

    // ==== SimulationMode ====

    // Adapted from the Ship Transport Prototype (ChatGPT "Astra"'s work for the user, handed over 2026-10-06; decisions 92-93).

    public enum SimulationMode { Physical, Caching, Background, Restoring, Stopped }

    public sealed class DistanceHandover
    {
        // Decision 221 (the user, 2026-11-04: "hide the ship at 3.2 km, show again at 3 km, like how economic overhaul's visible trader",
        // its TraderHide and TraderShow): she sails in physics out to SailWithin of the player; beyond it (for 2 s) the game packs her cargo,
        // then she is hidden (BackgroundTravel.RefreshHorizon) and the clock carries her. She is shown and sails again only once back within
        // SailAgainWithin; between the two she stays as she was, so she doesn't flip at a line (each flip packs and unpacks her cargo). They
        // replace the game's own 1 km, the 1.3 km when closing and the prewarm beyond 1 km (the prototype's), and the arrival's band of
        // 2.7-2.95 km.
        public const double SailWithin = 3200, SailAgainWithin = 3000;
        public SimulationMode Mode { get; private set; } = SimulationMode.Physical;
        public long Generation { get; private set; }
        private double farSeconds;

        public void Observe(double distance, bool aboard, double dt)
        {
            if (!NavigationMath.Finite(distance) || distance < 0 || !NavigationMath.Finite(dt) || dt < 0)
                throw new ArgumentException("Invalid handover observation.");
            if (Mode == SimulationMode.Stopped) return;
            if (Mode == SimulationMode.Physical)
            {
                farSeconds = !aboard && distance > SailWithin ? farSeconds + dt : 0;
                if (farSeconds >= 2) Change(SimulationMode.Caching);
            }
            else if (Mode == SimulationMode.Background && (aboard || distance <= SailAgainWithin))
                Change(SimulationMode.Restoring);
            // Caching must complete before respawning, even if the observer reverses direction.
        }

        public bool CommitCached(long generation)
        {
            if (generation != Generation || Mode != SimulationMode.Caching) return false;
            Change(SimulationMode.Background);
            return true;
        }

        public void RequestRestore()
        {
            if (Mode == SimulationMode.Background) Change(SimulationMode.Restoring);
        }

        public bool CommitPhysical(long generation)
        {
            if (generation != Generation || Mode != SimulationMode.Restoring) return false;
            Change(SimulationMode.Physical);
            return true;
        }

        public void Stop() { Change(SimulationMode.Stopped); }
        private void Change(SimulationMode next) { Mode = next; Generation++; farSeconds = 0; }
    }
}

namespace ShipwrightAndTransport.Compat
{
    // ==== VirtualCrew (D8) ====

    // D8 (SAILING_TRANSPORT_DRAFT.md): VirtualCrew 0.2.2's automation is kept off a ship in service, so her sailor and her
    // locks (decision 97) are all that move her helm, winches, anchor and lines. VirtualCrew works the player's current (or
    // last) boat:
    //   - its pilot (PilotingWindow.Update) writes the wheel it found every frame, after the sailor's physics step and before
    //     the wheel's LateUpdate hands it to the rudder: what it writes to a wheel a voyage locks is put back as it was;
    //   - its crew's winch work (trims, standing orders, sail orders) goes through its crewWinchInstructions, applied by its
    //     GPButtonRopeWinch.Update prefix, which HarmonyX runs even after Shipwright's prefix has skipped the winch (every
    //     prefix runs; a false only skips the original): a locked winch's orders are dropped first (DropOrders, from
    //     SailingPatches.WinchUpdatePrefix, Priority.First);
    //   - while the boat it works on is in service, its first officer queues no trims or standing orders, and its crew neither
    //     tie nor untie her lines nor row her.
    // All or nothing: if VirtualCrew no longer matches, transports are carried (Sailing.CompatibilityGate), as with SailMaster.
    public static partial class VirtualCrew
    {
        private static bool guarded, dropFailed;
        private static ServiceTargets targets;
        private static Func<Transform> crewBoat;

        // VirtualCrew is installed but not kept off ships in service: transports are carried.
        internal static bool Unguarded => Mods.Installed(Guid) && !guarded;

        internal sealed class ServiceTargets
        {
            internal MethodInfo PilotUpdate, AutoTrim, StandingOrders, Moor, Unmoor, Scull, CrewBoat, Manager;
            internal FieldInfo PilotWheel, CrewOrders, OldOrders;
        }

        // The members D8 needs, read from VirtualCrew 0.2.2's code; null, with why, when one is missing or changed.
        internal static ServiceTargets FindServiceTargets(Func<string, Type> typeByName, out string why)
        {
            try { return Find(typeByName, out why); }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return null; }
        }

        private static ServiceTargets Find(Func<string, Type> typeByName, out string why)
        {
            var pilot = typeByName("SailwindVirtualCrew.PilotingWindow");
            var manager = typeByName("SailwindVirtualCrew.VirtualCrewManager");
            var resolver = typeByName("SailwindVirtualCrew.CrewBoatContextResolver");
            var skulling = typeByName("SailwindVirtualCrew.SkullingRequest");
            var found = new ServiceTargets
            {
                PilotUpdate = Method(pilot, "Update", Type.EmptyTypes),
                PilotWheel = pilot != null ? AccessTools.Field(pilot, "steeringWheel") : null,
                AutoTrim = Method(manager, "QueueAutoTrimAllSails", Type.EmptyTypes),
                StandingOrders = Method(manager, "TickFirstOfficerStandingOrders", new[] { typeof(float) }),
                Moor = Method(manager, "AddMooringRequests", null),
                Unmoor = Method(manager, "AddUnmooringRequests", null),
                Scull = Method(skulling, "TryCreate", null),
                CrewBoat = Method(resolver, "GetActiveWorldBoat", Type.EmptyTypes),
                Manager = manager != null ? AccessTools.PropertyGetter(manager, "Instance") : null,
                CrewOrders = manager != null ? AccessTools.Field(manager, "crewWinchInstructions") : null,
                // Its older whole-ship orders (set nowhere in 0.2.2, read by the same prefix): dropped too while there.
                OldOrders = manager != null ? AccessTools.Field(manager, "winchInstructions") : null,
            };
            why = Mismatch(found, manager);
            return why == null ? found : null;
        }

        private static MethodInfo Method(Type type, string name, Type[] parameters) =>
            type == null ? null : parameters == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, parameters);

        private static string Mismatch(ServiceTargets t, Type manager)
        {
            if (t.PilotUpdate == null || t.PilotWheel == null || t.PilotWheel.FieldType != typeof(GPButtonSteeringWheel)) return "its pilot";
            if (t.AutoTrim == null || t.StandingOrders == null) return "its first officer";
            if (t.Moor == null || t.Unmoor == null || t.Moor.GetParameters().Length != 1 || t.Unmoor.GetParameters().Length != 1) return "its mooring";
            if (t.Scull == null || !t.Scull.IsStatic || t.Scull.ReturnType != typeof(bool) ||
                !t.Scull.GetParameters().Any(p => p.Name == "reason" && p.ParameterType == typeof(string).MakeByRefType())) return "its rowing";
            if (t.CrewBoat == null || !t.CrewBoat.IsStatic || t.CrewBoat.ReturnType != typeof(Transform)) return "the boat it works on";
            if (t.Manager == null || !t.Manager.IsStatic || t.Manager.ReturnType != manager) return "its crew manager";
            if (!IsOrderBook(t.CrewOrders)) return "its winch orders";
            if (t.OldOrders != null && !IsOrderBook(t.OldOrders)) t.OldOrders = null;
            return null;
        }

        // A dictionary keyed by the game's winches.
        private static bool IsOrderBook(FieldInfo field) =>
            field != null && !field.IsStatic && typeof(IDictionary).IsAssignableFrom(field.FieldType) && field.FieldType.IsGenericType &&
            field.FieldType.GetGenericArguments()[0] == typeof(GPButtonRopeWinch);

        internal static void KeepOffShipsInService(Harmony harmony)
        {
            if (!Mods.Installed(Guid)) return;
            var found = FindServiceTargets(AccessTools.TypeByName, out string why);
            if (found == null)
            {
                Log.Warn("VirtualCrew changed (" + why + "): it can't be kept off a ship in service, so transports are carried.");
                return;
            }
            var applied = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                crewBoat = AccessTools.MethodDelegate<Func<Transform>>(found.CrewBoat);
                targets = found;
                Patch(harmony, applied, found.PilotUpdate, nameof(PilotPrefix), nameof(PilotPostfix));
                foreach (var method in new[] { found.AutoTrim, found.StandingOrders, found.Moor, found.Unmoor })
                    Patch(harmony, applied, method, nameof(UnlessInService), null);
                Patch(harmony, applied, found.Scull, nameof(RowingPrefix), null);
                guarded = true;
                Log.Info("VirtualCrew found: its pilot, first officer and crew keep off a ship in service.");
            }
            catch (Exception e)
            {
                foreach (var patch in applied)
                {
                    try { harmony.Unpatch(patch.Key, patch.Value); } catch { }
                }
                Log.Warn("VirtualCrew: keeping it off a ship in service failed (" + e.Message + "), so transports are carried.");
            }
        }

        private static void Patch(Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> applied, MethodBase original, string prefix, string postfix)
        {
            var pre = prefix != null ? AccessTools.Method(typeof(VirtualCrew), prefix) : null;
            var post = postfix != null ? AccessTools.Method(typeof(VirtualCrew), postfix) : null;
            harmony.Patch(original, prefix: pre != null ? new HarmonyMethod(pre) : null, postfix: post != null ? new HarmonyMethod(post) : null);
            if (pre != null) applied.Add(new KeyValuePair<MethodBase, MethodInfo>(original, pre));
            if (post != null) applied.Add(new KeyValuePair<MethodBase, MethodInfo>(original, post));
        }

        // The boat VirtualCrew works on is in service.
        private static bool InService()
        {
            try
            {
                var boat = crewBoat?.Invoke();
                return boat && Sailing.Voyages.InService(boat);
            }
            catch { return false; }
        }

        public static bool UnlessInService() => !InService();

        public static bool RowingPrefix(ref bool __result, ref string reason)
        {
            if (!InService()) return true;
            __result = false;
            reason = "She is in a shipwright's service.";
            return false;
        }

        // The wheel as it was before VirtualCrew's pilot ran, when a voyage locks it.
        public struct HeldWheel
        {
            internal GPButtonSteeringWheel Wheel;
            internal float Input;
            internal bool Locked;
            internal float RudderTarget;
        }

        public static void PilotPrefix(MonoBehaviour __instance, out HeldWheel __state)
        {
            __state = default;
            var wheel = targets?.PilotWheel.GetValue(__instance) as GPButtonSteeringWheel;
            if (!wheel || !Sailing.Voyages.IsProtected(wheel)) return;
            __state = new HeldWheel { Wheel = wheel, Input = wheel.currentInput, Locked = (bool)Sailing.GameBindings.WheelLocked.GetValue(wheel),
                RudderTarget = wheel.attachedRudder ? wheel.attachedRudder.spring.targetPosition : 0f };
        }

        public static void PilotPostfix(HeldWheel __state)
        {
            if (!__state.Wheel) return;
            __state.Wheel.currentInput = __state.Input;
            Sailing.GameBindings.WheelLocked.SetValue(__state.Wheel, __state.Locked);
            // Decision 132: a wheel the voyage has let go keeps its rudder where the game's push back put it, not where the pilot did.
            if (__state.Wheel.attachedRudder) { var spring = __state.Wheel.attachedRudder.spring; spring.targetPosition = __state.RudderTarget; __state.Wheel.attachedRudder.spring = spring; }
        }

        // SailingPatches.WinchUpdatePrefix, for a winch a voyage locks: VirtualCrew's orders for it go before its own prefix
        // reads them (they are read with TryGetValue and ContainsKey, so a dropped order is simply none).
        internal static void DropOrders(GPButtonRopeWinch winch)
        {
            if (!guarded) return;
            try
            {
                var manager = targets.Manager.Invoke(null, null);
                if (manager == null) return;
                (targets.CrewOrders.GetValue(manager) as IDictionary)?.Remove(winch);
                if (targets.OldOrders != null) (targets.OldOrders.GetValue(manager) as IDictionary)?.Remove(winch);
            }
            catch (Exception e)
            {
                if (dropFailed) return;
                dropFailed = true;
                Log.Warn("VirtualCrew: dropping its orders for a ship in service failed: " + e.Message);
            }
        }
    }
}
