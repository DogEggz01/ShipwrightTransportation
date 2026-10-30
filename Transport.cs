using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipwrightAndTransport
{
    // ==== Transport ====

    // Transport on paper (§6): no ship moves. The ship leaves a day after booking and arrives after miles ÷ knots; then
    // her storage record changes shipyard, and she stays in her parking slot.
    internal static class Transport
    {
        internal const double FindingSailorsHours = 24.0;

        internal static TransportRecord RecordOf(int index) => State.Save.transports.Find(t => t.saveIndex == index);

        // A tow has left with her once the tug has picked her up.
        internal static bool HasLeft(TransportRecord record) => record.tow ? Tow.PickedUp(record) : GameClock.Hours >= record.departHour;

        internal sealed class Quote
        {
            // From: where she is stored and leaves from; OrderedAt: the shipyard whose documents booked her.
            internal ShipyardInfo From, To, OrderedAt;
            internal int Miles, Currency, StorageCurrency, TransportFee, StorageFee;
            internal float Knots;
            internal double Book, Depart, Arrive;
            internal bool FromOtherYard => From != OrderedAt;
            internal bool OneCurrency => Currency == StorageCurrency;
            internal int Total => (int)Math.Min(int.MaxValue, (long)TransportFee + StorageFee);
        }

        // The fee is the transport (with 20% of her cargo's value) plus the storage yard's fee through the departure day: the 24
        // hours of finding sailors always cross one midnight, so one day more than at booking. Booked at the yard she is stored
        // at, 0.6 a metre a mile; booked at another yard, 0.8, the transport in the ordering yard's currency and the storage in
        // her storage yard's ("charging in player's ordering shipyard currency", "Origin's storage paid at booking in storage yard
        // currency", SPEC.md §6). The miles count from where she leaves.
        internal static Quote QuoteFor(StoredShip record, ShipyardInfo orderedAt, ShipyardInfo to, int currency)
        {
            var from = Shipyards.ByName(record.shipyard) ?? orderedAt;
            var quote = new Quote { From = from, To = to, OrderedAt = orderedAt, Currency = currency, StorageCurrency = from == orderedAt ? currency : from.Currency };
            quote.Miles = Math.Max(0, ShipwrightAndTransport.Miles.BetweenShipyards(from.Name, to.Name));
            quote.Knots = Fees.TransportKnots(record.lengthM, record.massLb, record.cargoLb);
            quote.Book = GameClock.Hours;
            quote.Depart = quote.Book + FindingSailorsHours;
            quote.Arrive = quote.Depart + Fees.SeaHours(quote.Miles, quote.Knots);
            float perMetre = quote.FromOtherYard ? Fees.OtherYardPerMetre : Fees.NormalPerMetre;
            quote.TransportFee = Fees.TransportFee(record.lengthM, quote.Miles, record.cargoUnits, Money.Rate(currency), perMetre);
            quote.StorageFee = Fees.StorageDue(Fees.StorageDays(GameClock.Day + 1, record.dayStored), Storage.DailyFee(record, quote.StorageCurrency));
            return quote;
        }

        internal static bool Book(StoredShip record, Quote quote)
        {
            if (RecordOf(record.saveIndex) != null) return false;
            if (!PayBoth(quote)) return false;
            State.Save.transports.Add(new TransportRecord
            {
                saveIndex = record.saveIndex, from = quote.From.Name, to = quote.To.Name, orderedAt = quote.OrderedAt.Name,
                bookedHour = quote.Book, departHour = quote.Depart, arriveHour = quote.Arrive,
            });
            Log.Info("Booked " + Ships.NameOf(record.saveIndex) + " from " + quote.From.Name + " to " + quote.To.Name +
                (quote.FromOtherYard ? " at " + quote.OrderedAt.Name : "") +
                ": leaves on day " + GameClock.DayOf(quote.Depart) + ", arrives on day " + GameClock.DayOf(quote.Arrive) + ".");
            return true;
        }

        // In one currency, one payment; in two, both are checked before either is taken.
        private static bool PayBoth(Quote quote)
        {
            if (quote.OneCurrency) return Money.Pay(quote.Currency, quote.Total);
            var coins = PlayerGold.currency;
            if (coins == null || coins[quote.Currency] < quote.TransportFee || coins[quote.StorageCurrency] < quote.StorageFee)
            {
                Notify.Show("Not enough money.");
                return false;
            }
            return Money.Pay(quote.Currency, quote.TransportFee) && Money.Pay(quote.StorageCurrency, quote.StorageFee);
        }

        private static float nextCheck;

        // Every few seconds and after loading, wherever the player is; several may arrive at once. A tow before its pickup is
        // checked only in the regular checks: right after a load the game hasn't placed the player and the ships for good.
        internal static void Tick(bool now = false)
        {
            if (State.Off || (!now && Time.time < nextCheck)) return;
            nextCheck = Time.time + 2f;
            if (!Sun.sun) return;
            double hours = GameClock.Hours;
            foreach (var transport in State.Save.transports.ToArray())
            {
                if (transport.tow)
                {
                    if (!Tow.PickedUp(transport))
                    {
                        if (!now) Tow.Check(transport, hours);
                        continue;
                    }
                    Tow.TellCrates(transport, hours, false);
                }
                if (hours < transport.arriveHour) continue;
                State.Save.transports.Remove(transport);
                var record = Storage.RecordOf(transport.saveIndex);
                if (record == null) continue;
                record.shipyard = transport.to;
                record.dayStored = GameClock.DayOf(transport.arriveHour);
                string name = Ships.NameOf(transport.saveIndex);
                Log.Info(name + " arrived in " + transport.to + ".");
                if (transport.tow) Tow.TellCrates(transport, hours, true);
                Notify.Later(Arrived(name, transport.to));
            }
        }

        internal static string Arrived(string name, string shipyard) => name + " arrived in\n" + shipyard + " shipyard.";
    }

    // ==== Tow ====

    // The tow of a ship not stored (SPEC.md §6), in the background ("all these only background", the user): a tug
    // leaves the ordering yard a day after booking, sails to her at 8 knots, waits a day at her, takes her once nobody is near,
    // and tows her to the destination at 8 knots, where she is stored. Until the pickup she is an ordinary ship in the world;
    // from the pickup she is on paper, parked on the storage field like a stored ship, her record already the destination's.
    internal static class Tow
    {
        // "A. 8 knots"; "before depart and when arrive at target ship, towing ship will wait for a day each" (the user).
        internal const float Knots = 8f;
        internal const double WaitHours = 24.0;
        // B: farther than this from her place at booking, measured flat, the tow is cancelled (Claude's suggestion, the user's
        // "OK"). More than she swings at anchor (the code's default line is 50 m) or on her ropes, much less than a sail away.
        internal const float BufferMetres = 150f;

        internal const string Booked = "It is booked for a tow.";
        internal const string Sank = "A sunk ship can't be towed.";
        // The user's handoff's text (2026-10-30), on three lines of the scroll.
        internal const string CratesTaken = "The tug crew has taken\nthe items in your crates\nfor themselves";

        internal static string Reached(string name) => "The tug reached\n" + name + ".";
        internal static string Cancelled(string name) => "The tug found no\n" + name + ": the towing\nfee is paid back.";

        internal static bool PickedUp(TransportRecord tow) => tow.pickupHour >= 0;

        internal struct Clock { internal double Book, Depart, Reach, PickupDue, Arrive; }

        // The hours as planned at booking: the pickup may come later (C), and the tow then counts from it.
        internal static Clock Plan(double book, int tugMiles, int towMiles, float towKnots)
        {
            var clock = new Clock { Book = book, Depart = book + WaitHours };
            clock.Reach = clock.Depart + Fees.SeaHours(tugMiles, Knots);
            clock.PickupDue = clock.Reach + WaitHours;
            clock.Arrive = clock.PickupDue + Fees.SeaHours(towMiles, towKnots);
            return clock;
        }

        // Her weight slows the tow leg only, the tug's run to her stays at 8 knots ("For tugboat it only affect the towing leg,
        // not tugboat to ship leg", the user, 2026-10-30; Fees.LoadFactor).
        internal static float TowKnots(float massLb, float cargoLb) => Knots * Fees.LoadFactor(massLb, cargoLb);

        // Flat, so the sea's rise and fall doesn't count (Simple Tides moves it 1.3-4 m).
        internal static bool Beyond(double atX, double atZ, double x, double z)
        {
            double dx = x - atX, dz = z - atZ;
            return dx * dx + dz * dz > (double)BufferMetres * BufferMetres;
        }

        // Where the tug is: 0 at the ordering yard, 1 at her, 2 at the destination, between them the share of the leg sailed.
        // Until the pickup (pickup < 0) it stays at her once there.
        internal static double TugProgress(double hours, double depart, double reach, double pickup, double arrive)
        {
            if (pickup < 0) return hours <= depart ? 0 : hours >= reach || reach <= depart ? 1 : (hours - depart) / (reach - depart);
            return 1 + (hours >= arrive || arrive <= pickup ? 1 : hours <= pickup ? 0 : (hours - pickup) / (arrive - pickup));
        }

        // A world position with the floating origin's shift taken out (FloatingOriginManager.ShiftingPosToRealPos), as the record
        // keeps her place: the world shifts in 512 m steps around the player.
        internal static Vector3 Real(Vector3 position) => position - Parking.Offset;

        // Game miles, flat, from a shipyard's port to a place in the world.
        internal static int MilesFrom(string shipyard, Vector3 position)
        {
            var port = Ports.ByName(shipyard);
            if (!port) return 0;
            Vector3 d = port.transform.position - position;
            d.y = 0f;
            return Miles.Round(d.magnitude, Miles.Timescale);
        }

        // Why she can't be booked for a tow now, or null: what Store ship refuses (a crate holding items, cargo loading, the
        // Leopard's rowboat, a pledged ship, another boat tied to her, the field full), and a sunk or booked ship ("F. Refuse.").
        // A rented ship isn't on the list (Ships.Owned).
        internal static string Refusal(PurchasableBoat boat)
        {
            if (Transport.RecordOf(Ships.IndexOf(boat)) != null) return Booked;
            var damage = boat.GetComponent<BoatDamage>();
            if (damage && damage.sunk) return Sank;
            return Storage.RefusalToStore(boat, true);
        }

        internal sealed class Quote
        {
            internal ShipyardInfo OrderedAt, To;
            internal Vector3 At;
            internal int Currency, TugMiles, TowMiles, TugFee, TowFee, CargoUnits;
            internal float LengthM, MassLb, CargoLb, TowKnots;
            internal Clock Hours;
            internal int Total => (int)Math.Min(int.MaxValue, (long)TugFee + TowFee);
        }

        // Both legs in the ordering yard's currency at 1 a metre a mile, the tow leg with 20% of her cargo, nothing for the two
        // days' wait (the user). Her length as Store ship measures it; her cargo as it would pack, the loose items and her cache,
        // its value for the fee and its weight for the tow leg's speed (TowKnots).
        internal static Quote QuoteFor(PurchasableBoat boat, ShipyardInfo orderedAt, ShipyardInfo to, int currency)
        {
            int index = Ships.IndexOf(boat);
            var quote = new Quote { OrderedAt = orderedAt, To = to, Currency = currency, At = Real(boat.transform.position) };
            quote.LengthM = Storage.LengthOf(boat, Ships.Describe(index, boat).Hull);
            var items = boat.GetComponent<BoatLocalItems>();
            quote.CargoUnits = (int)Math.Min(int.MaxValue, (long)Storage.CargoValue(index) + Storage.CachedCargoValue(items));
            quote.MassLb = Weights.ShipMass(boat);
            quote.CargoLb = Weights.CargoPounds(index, items);
            quote.TowKnots = TowKnots(quote.MassLb, quote.CargoLb);
            quote.TugMiles = MilesFrom(orderedAt.Name, boat.transform.position);
            quote.TowMiles = MilesFrom(to.Name, boat.transform.position);
            quote.Hours = Plan(GameClock.Hours, quote.TugMiles, quote.TowMiles, quote.TowKnots);
            float rate = Money.Rate(currency);
            quote.TugFee = Fees.TugFee(quote.LengthM, quote.TugMiles, rate);
            quote.TowFee = Fees.TransportFee(quote.LengthM, quote.TowMiles, quote.CargoUnits, rate, Fees.TowPerMetre);
            return quote;
        }

        internal static bool Book(PurchasableBoat boat, Quote quote)
        {
            int index = Ships.IndexOf(boat);
            if (index <= 0 || Storage.RecordOf(index) != null) return false;
            string refusal = Refusal(boat);
            if (refusal != null) { Notify.Show(refusal); return false; }
            if (!Money.Pay(quote.Currency, quote.Total)) return false;
            var clock = quote.Hours;
            State.Save.transports.Add(new TransportRecord
            {
                saveIndex = index, from = quote.OrderedAt.Name, to = quote.To.Name, orderedAt = quote.OrderedAt.Name, tow = true,
                currency = quote.Currency, tugMiles = quote.TugMiles, towMiles = quote.TowMiles, tugFee = quote.TugFee, towFee = quote.TowFee,
                cargoUnits = quote.CargoUnits, lengthM = quote.LengthM, atX = quote.At.x, atZ = quote.At.z,
                massLb = quote.MassLb, cargoLb = quote.CargoLb,
                bookedHour = clock.Book, departHour = clock.Depart, reachHour = clock.Reach, arriveHour = clock.Arrive,
            });
            Log.Info("Booked a tow of " + Ships.NameOf(index) + " from " + quote.OrderedAt.Name + " to " + quote.To.Name + ": the tug's " + quote.TugMiles +
                " miles " + quote.TugFee + ", the tow's " + quote.TowMiles + " miles " + quote.TowFee + " (cargo worth " + quote.CargoUnits + " units; " +
                Weights.Text(quote.MassLb, quote.CargoLb) + ", towed at " + quote.TowKnots.ToString("0.0", CultureInfo.InvariantCulture) + " knots), " +
                Money.Short[quote.Currency] + "; she lies at (" + quote.At.x.ToString("F0", CultureInfo.InvariantCulture) + ", " +
                quote.At.z.ToString("F0", CultureInfo.InvariantCulture) + "). The tug leaves on day " + GameClock.DayOf(clock.Depart) + ", reaches her on day " +
                GameClock.DayOf(clock.Reach) + "; she arrives on day " + GameClock.DayOf(clock.Arrive) + " at the earliest.");
            return true;
        }

        // Checks in a row that found her past the buffer (Lost), by save index. Not saved.
        private static readonly Dictionary<int, int> farChecks = new Dictionary<int, int>();
        private static readonly HashSet<int> fullLogged = new HashSet<int>();

        // Before the pickup, at each check: the cancel (B), the tug reaching her, and the pickup once the day at her is over and
        // nobody is near (C). The tug's wait for that tells nothing (the user: four notifications only).
        internal static void Check(TransportRecord tow, double hours)
        {
            if (GameState.justStarted || GameState.recovering) return;
            var boat = Ships.BoatOf(tow.saveIndex);
            string lost = Lost(tow, boat);
            if (lost != null) { Cancel(tow, lost); return; }
            if (!tow.reachedTold && hours >= tow.reachHour)
            {
                tow.reachedTold = true;
                string name = Ships.NameOf(tow.saveIndex);
                Log.Info("The tug reached " + name + ".");
                Notify.Later(Reached(name));
            }
            double due = tow.reachHour + WaitHours;
            if (hours < due) return;
            if (!OutOfSight(boat)) { tow.nearHour = hours; return; }
            if (Parking.FreeSlot(State.Save.stored) < 0)
            {
                if (fullLogged.Add(tow.saveIndex)) Log.Warn("The tug waits at " + Ships.NameOf(tow.saveIndex) + ": the storage field is full.");
                return;
            }
            // From when she was last seen near, if that was after the day's wait: the checks run every 2 s.
            PickUp(tow, boat, Math.Max(due, tow.nearHour));
        }

        // B, and what else ends a tow before the pickup (SPEC.md §6): she sank, is no longer the player's (EconomyOverhaul's
        // seizure), or lies past the buffer from her place at booking (the game's rescue, a sail, a drift). Null while she may
        // still be taken.
        private static string Lost(TransportRecord tow, PurchasableBoat boat)
        {
            if (!boat) return "she is no longer in the game";
            var saveable = boat.GetComponent<SaveableObject>();
            if (!saveable || !saveable.extraSetting || Compat.EconomyOverhaul.IsRented(tow.saveIndex)) return "she is no longer the player's";
            var damage = boat.GetComponent<BoatDamage>();
            if (damage && damage.sunk) return "she sank";
            // While the world shifts the offset moves first and the ships a physics step later (FloatingOriginManager.NewShift,
            // inside GameState.waitingForShift), so a place measured then is 512 m off: no measure then, and only two checks in a
            // row past the buffer cancel.
            if (GameState.waitingForShift || FloatingOriginManager.ShiftingThisFrame) return null;
            Vector3 at = Real(boat.transform.position);
            if (!Beyond(tow.atX, tow.atZ, at.x, at.z)) { farChecks.Remove(tow.saveIndex); return null; }
            farChecks.TryGetValue(tow.saveIndex, out int count);
            farChecks[tow.saveIndex] = ++count;
            if (count < 2) return null;
            float metres = new Vector2(at.x - tow.atX, at.z - tow.atZ).magnitude;
            return "she lies " + Mathf.RoundToInt(metres) + " m from her place at booking";
        }

        // The tug leg is kept, the tow leg paid back in the coins paid ("Still pay for tug leg, return the towing fee").
        private static void Cancel(TransportRecord tow, string why)
        {
            State.Save.transports.Remove(tow);
            farChecks.Remove(tow.saveIndex);
            Money.Refund(tow.currency, tow.towFee);
            string name = Ships.NameOf(tow.saveIndex);
            Log.Info("The tow of " + name + " to " + tow.to + " is cancelled: " + why + ". The tow leg's " + tow.towFee + " " + Money.Short[tow.currency] +
                " are paid back; the tug leg's " + tow.tugFee + " are kept.");
            Notify.Later(Cancelled(name));
        }

        // C, read as: the game has her switched off for the player (BoatHorizonPerformanceSwitcher: her root inactive beyond her
        // own fullySunkDistance, 3,000 m on the vanilla ships, 10,000 m on some modded hulls), so she is never seen vanishing and
        // nobody is aboard. Measured here too as BoatHorizon.DistanceCheck measures, should the switcher not hold her.
        // A modded hull may be shown much farther, Shattered Seas' Shroud Large (the full mod list's new game's "Clipper") to
        // 30,000 m (run 20261029-190504): the distance is capped at the vanilla ships' 3,000 m (the user, 2026-10-29: "should cap
        // it like other vanilla boat, make it 3000m").
        internal const float PickupDistanceCap = 3000f;

        internal static bool OutOfSight(PurchasableBoat boat)
        {
            var horizon = boat.GetComponentInChildren<BoatHorizon>(true);
            var root = horizon ? horizon.transform.parent : null;
            if (root && !root.gameObject.activeSelf) return true;
            var player = Refs.observerMirror;
            return horizon && player && Vector3.Distance(horizon.transform.position, player.transform.position) >= PickupDistance(horizon.fullySunkDistance);
        }

        internal static float PickupDistance(float fullySunkDistance) => Math.Min(fullySunkDistance, PickupDistanceCap);

        // The pickup (SPEC.md §6.3): Store ship's steps without the shipyard's, as the probe ran
        // them (the QA's Mod.PickUp, runs 20261029-182831 and -183130): the items in her crates taken (D), her cargo packed, the game's
        // references to her dropped, a storage record at the destination, she moved to a parking slot. Her cargo counts at the
        // booking's value: far off, the game has packed her already, and PackCargo measures only items still loose (the probe: 0).
        private static void PickUp(TransportRecord tow, PurchasableBoat boat, double pickupHour)
        {
            int index = tow.saveIndex;
            int slot = Parking.FreeSlot(State.Save.stored);
            // Switched on, as the game switches her on when the player comes near: the move's steps are the store's, made on a ship
            // switched on (the game switches her off again out there, as after a store).
            if (!boat.gameObject.activeSelf) boat.gameObject.SetActive(true);
            var items = boat.GetComponent<BoatLocalItems>();
            // cratesTaken holds the items taken from 0.1.2 (the crates before; only > 0 is read). The name stays: it is saved.
            tow.cratesTaken = TakeCrateContents(index, items);
            GameAccess.PackCargo(items, out _);
            Storage.LogCrates("tug pickup", index, items);
            Storage.ForgetBoat(boat.transform);
            tow.pickupHour = pickupHour;
            tow.arriveHour = pickupHour + Fees.SeaHours(tow.towMiles, TowKnots(tow.massLb, tow.cargoLb));
            State.Save.stored.Add(new StoredShip
            {
                saveIndex = index, shipyard = tow.to, slot = slot, dayStored = GameClock.DayOf(tow.arriveHour),
                lengthM = tow.lengthM, feeUnits = Fees.DailyUnits(boat.price), cargoUnits = tow.cargoUnits,
                massLb = tow.massLb, cargoLb = tow.cargoLb,
            });
            farChecks.Remove(index);
            fullLogged.Remove(index);
            Log.Info("The tug took " + Ships.NameOf(index) + " (save index " + index + ") on hour " + pickupHour.ToString("F1", CultureInfo.InvariantCulture) +
                " to slot " + slot + (tow.cratesTaken > 0 ? ", " + tow.cratesTaken + " item(s) in crates taken" : "") + "; she arrives in " + tow.to +
                " on day " + GameClock.DayOf(tow.arriveHour) + ".");
            Plugin.Instance.StartCoroutine(Teleporter.Move(boat, Parking.World(State.Save.parkingField, slot), Parking.Heading, false));
        }

        // D: the items in her crates at the pickup are taken, the crates and Kemy's storage furniture stay aboard, empty (the
        // user, 2026-10-30; until 0.1.2 the opened crate went too, "Destroy it"): loose as the game destroys an item
        // (ShipItem.DestroyItem), or packed in her cache by dropping its data (the live copies are gone, ShipItem.ProcessSaveable).
        // Packing them with her was prone to bugs (the "tried and failed" file, "Opened crates"). Her crates holding items are
        // gathered from her items and her cache first (Storage.FilledCrates: a crate packed far off can leave its goods in the
        // world, the tow run 20261029-191154), then every item in one goes, wherever it is. Returns how many items.
        private static int TakeCrateContents(int index, BoatLocalItems items)
        {
            var crates = Storage.FilledCrates(index, items);
            if (crates.Count == 0) return 0;
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            var byId = new Dictionary<int, SaveablePrefab>();
            var doomed = new List<SaveablePrefab>();
            if (prefabs != null)
                foreach (var prefab in prefabs)
                {
                    if (!prefab) continue;
                    byId[prefab.instanceId] = prefab;
                    if (CrateRule.InCrate(new CrateRule.Item(prefab.instanceId, 0, prefab.currentCrateId), crates)) doomed.Add(prefab);
                }
            int count = 0;
            foreach (var prefab in doomed)
            {
                var item = prefab ? prefab.GetComponent<ShipItem>() : null;
                if (!item) continue;
                // Out of its crate first: the crate stays, and CrateInventory.LateUpdate moves every item it holds each frame.
                var inventory = byId.TryGetValue(prefab.currentCrateId, out var crate) && crate ? crate.GetComponent<CrateInventory>() : null;
                if (inventory) inventory.WithdrawItem(item);
                item.DestroyItem();
                count++;
            }
            var cached = items ? items.GetCachedItems() : null;
            if (cached != null) count += cached.RemoveAll(d => d != null && CrateRule.InCrate(new CrateRule.Item(d.instanceId, 0, d.crateId), crates));
            return count;
        }

        // D's notification, a day after the tow leg started ("make this show up after start tugging leg for 1 days"); a tow
        // shorter than a day tells it at the arrival, just before the arrival's (Claude's suggestion).
        internal static void TellCrates(TransportRecord tow, double hours, bool arriving)
        {
            if (tow.cratesTaken <= 0 || tow.cratesTold || (!arriving && hours < tow.pickupHour + WaitHours)) return;
            tow.cratesTold = true;
            Notify.Later(CratesTaken);
        }
    }

    // ==== Weights ====

    // Her own mass and her cargo's weight, in the game's mass units, the pounds of the mission board (MissionDetailsUI), for
    // the transport's and the tow leg's speed (Fees.LoadFactor). Both as the game adds them up for her physics.
    internal static class Weights
    {
        private static readonly FieldInfo SelfMass = AccessTools.Field(typeof(BoatMass), "selfMass");
        private static readonly FieldInfo PartsMass = AccessTools.Field(typeof(BoatMass), "partsMass");
        private static readonly MethodInfo SailMass = AccessTools.Method(typeof(BoatMass), "GetSailMass");

        // BoatMass.UpdateMass without items or the player: the hull's own body mass (selfMass, from Awake), partsMass, and
        // again each part's active option with its masts' sails (the game counts the parts twice; as it does, so the factor
        // matches her physics). Her body's mass if BoatMass can't be read.
        internal static float ShipMass(PurchasableBoat boat)
        {
            var body = boat ? boat.GetComponent<Rigidbody>() : null;
            var mass = boat ? boat.GetComponent<BoatMass>() : null;
            if (!mass || SelfMass == null) return body ? body.mass : 0f;
            try
            {
                float sum = (float)SelfMass.GetValue(mass) + (PartsMass != null ? (float)PartsMass.GetValue(mass) : 0f);
                var parts = boat.GetComponent<BoatCustomParts>();
                if (parts && parts.availableParts != null)
                    foreach (var part in parts.availableParts)
                    {
                        var option = part != null ? part.GetActiveOption() : null;
                        if (!option) continue;
                        sum += option.mass;
                        var mast = option.GetComponent<Mast>();
                        if (!mast || mast.sails == null || SailMass == null) continue;
                        foreach (var sail in mast.sails)
                        {
                            var s = sail ? sail.GetComponent<Sail>() : null;
                            if (s) sum += (float)SailMass.Invoke(mass, new object[] { s });
                        }
                    }
                return sum;
            }
            catch (Exception e)
            {
                Log.Warn("Reading " + boat.name + "'s mass failed: " + e.Message);
                return body ? body.mass : 0f;
            }
        }

        // Her cargo: the items aboard her (her parent, as Storage.CargoValue) and those in her cache (null: not read).
        internal static float CargoPounds(int index, BoatLocalItems cache)
        {
            double sum = 0;
            var prefabs = SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null;
            if (index > 0 && prefabs != null)
                foreach (var prefab in prefabs)
                {
                    var item = prefab && prefab.GetParentObject() == index ? prefab.GetComponent<ShipItem>() : null;
                    if (item) sum += ItemPounds(item, item.amount, item.health, item.GetComponent<ShipItemSoup>());
                }
            var cached = cache ? cache.GetCachedItems() : null;
            if (cached != null)
                foreach (var data in cached)
                {
                    var prefab = data != null ? Storage.PrefabOf(data.prefabIndex) : null;
                    var item = prefab ? prefab.GetComponent<ShipItem>() : null;
                    // shortcut: a packed soup pot counts without its soup (kept in extraValues); replace if pots go as cargo.
                    if (item) sum += ItemPounds(item, data.itemAmount, data.itemHealth, null);
                }
            return (float)sum;
        }

        // An item's weight as ItemRigidbody.UpdateMass sets its body's mass: its own, a crate's goods by its amount (none in an
        // opened crate or Kemy's storage furniture, whose amount is 0 and which hold no goods prefab), a bottle's drink, tea and
        // salt by the amount, a pot's soup.
        private static float ItemPounds(ShipItem item, float amount, float health, ShipItemSoup soup)
        {
            float pounds = item.mass;
            var crate = item as ShipItemCrate;
            var goods = crate ? crate.GetContainedPrefab() : null;
            var goodsItem = goods ? goods.GetComponent<ShipItem>() : null;
            if (goodsItem) pounds += goodsItem.mass * amount;
            if (item.GetComponent<ShipItemBottle>()) pounds += health;
            if (item.GetComponent<ShipItemTea>() || item.GetComponent<ShipItemSalt>()) pounds += amount * 0.1f;
            if (soup) pounds += soup.currentWater + soup.currentEnergy / 20f + soup.currentUncookedEnergy / 20f;
            return Math.Max(0f, pounds);
        }

        internal static string Text(float massLb, float cargoLb) =>
            "ship " + Mathf.RoundToInt(massLb) + " lb, cargo " + Mathf.RoundToInt(cargoLb) + " lb";
    }

    // ==== Miles ====

    // Game miles, as the mission board measures them (§4.6): a game mile is what 1 knot covers in a game hour,
    // 0.514444 m/s ÷ Sun.initialTimescale, which is 64.3055 m at the game's 0.008.
    internal static class Miles
    {
        internal const float DefaultTimescale = 0.008f;

        internal static float MetresPerMile(float initialTimescale) =>
            0.514444f / (initialTimescale > 0f ? initialTimescale : DefaultTimescale);

        // Mission.GetDistance (metres / 100) and MissionDetailsUI.GetDistanceText (× 100 / metres per mile), rounded as
        // the board rounds.
        internal static int Round(float metres, float initialTimescale)
        {
            float distance = metres / 100f;
            return Fees.Round(distance * 100f / MetresPerMile(initialTimescale));
        }

        internal static float Exact(float metres, float initialTimescale) => metres / MetresPerMile(initialTimescale);

        // "40 miles", "1 mile".
        internal static string Text(float miles)
        {
            int rounded = Fees.Round(miles);
            return rounded + (rounded == 1 ? " mile" : " miles");
        }

        // Appendix B, for a port missing at run time.
        private struct Route
        {
            internal string A, B; internal int Miles;
            internal Route(string a, string b, int miles) { A = a; B = b; Miles = miles; }
        }

        private static readonly Route[] Table =
        {
            new Route("Dragon Cliffs", "Kicia Bay", 523),
            new Route("Gold Rock City", "Kicia Bay", 1162),
            new Route("Gold Rock City", "Dragon Cliffs", 1347),
            new Route("Fort Aestrin", "Dragon Cliffs", 1385),
            new Route("Gold Rock City", "Fort Aestrin", 1503),
            new Route("Fort Aestrin", "Kicia Bay", 1740),
            new Route("Dragon Cliffs", "Chronos", 2660),
            new Route("Fort Aestrin", "Chronos", 3062),
            new Route("Kicia Bay", "Chronos", 3089),
            new Route("Gold Rock City", "Chronos", 3951),
        };

        // -1 when the route is not in the table.
        internal static int FromTable(string a, string b)
        {
            foreach (var route in Table)
                if ((route.A == a && route.B == b) || (route.A == b && route.B == a)) return route.Miles;
            return -1;
        }

        // In game.
        internal static float Timescale => Sun.sun ? Sun.sun.initialTimescale : DefaultTimescale;

        internal static float ExactBetween(Vector3 a, Vector3 b) => Exact(Vector3.Distance(a, b), Timescale);

        internal static int BetweenShipyards(string a, string b)
        {
            var portA = Ports.ByName(a); var portB = Ports.ByName(b);
            return portA && portB ? Round(Vector3.Distance(portA.transform.position, portB.transform.position), Timescale) : FromTable(a, b);
        }
    }

    // ==== BoardShips (transport pages) ====

    // The board's transport pages (§6): a ship's trip, the destinations and the booking.
    internal sealed partial class Board
    {
        // A ship waiting for sailors here, or on her way here: the route and the leaving or arrival day, no buttons.
        private void TripPage(TransportRecord transport, bool arriving)
        {
            page.Block(BoardPage.Left, 0.64f, "Origin port:", transport.from);
            page.Block(BoardPage.Left, 0.41f, "Destination:", transport.to);
            if (arriving)
                page.Block(BoardPage.Left, 0.18f, "Arrives:", "day " + GameClock.DayOf(transport.arriveHour), Fees.AtSea(transport.arriveHour - transport.departHour));
            else
                page.Block(BoardPage.Left, 0.18f, "Leaves:", "day " + GameClock.DayOf(transport.departHour));
        }

        // The list becomes the destinations, nearest first; back returns to the ships. A destination's page reads like a
        // mission, and Send turns into Confirm on the first click, since a booked transport can't be cancelled.
        // A ship stored at another yard lists the destinations from there (her storage yard is left out, this yard is not).
        private void ShowDestinations(int index, string to, bool confirm)
        {
            var record = Storage.RecordOf(index);
            if (record == null || Transport.RecordOf(index) != null) { ShowShips(index); return; }
            var from = Shipyards.ByName(record.shipyard) ?? info;
            var destinations = new List<KeyValuePair<ShipyardInfo, int>>();
            foreach (var other in Shipyards.Table)
                if (other != from && Shipyards.Present(other))
                    destinations.Add(new KeyValuePair<ShipyardInfo, int>(other, Miles.BetweenShipyards(from.Name, other.Name)));
            destinations.Sort((a, b) => a.Value.CompareTo(b.Value));
            items.Clear();
            int selected = 0;
            for (int i = 0; i < destinations.Count; i++)
            {
                string name = destinations[i].Key.Name;
                items.Add(new BoardItem { Text = "to " + name, Clicked = () => ShowDestinations(index, name, false) });
                if (name == to) selected = i;
            }
            ShowList(BoardList.Destinations, destinations.Count > 0 ? selected : -1, false, "back", () => ShowShips(index));
            if (destinations.Count == 0) { BeginPage(() => ShowShips(index)); page.Title("No other shipyard", false); return; }
            TransportPage(index, record, destinations[selected].Key, confirm);
        }

        private void TransportPage(int index, StoredShip record, ShipyardInfo destination, bool confirm)
        {
            var quote = Transport.QuoteFor(record, info, destination, yard.region);
            BeginPage(() => ShowDestinations(index, destination.Name, confirm));
            page.Title(Ships.NameOf(index) + " to " + destination.Name, false);
            page.Block(BoardPage.Left, 0.70f, "Origin port:", quote.From.Name);
            page.Block(BoardPage.Left, 0.50f, "Destination:", destination.Name);
            page.Block(BoardPage.Left, 0.30f, "Leaves:", "day " + GameClock.DayOf(quote.Depart), "a day to find sailors");
            page.Block(BoardPage.Left, 0.04f, "Arrives:", "day " + GameClock.DayOf(quote.Arrive), Fees.AtSea(quote.Arrive - quote.Depart));
            page.Line(0, "Distance: " + quote.Miles + " miles");
            page.Line(1, "Speed: " + quote.Knots.ToString("0.0", CultureInfo.InvariantCulture) + " knots");
            // Two currencies (stored at another yard): the transport here, her storage there, each named.
            string storage = Money.Amount(quote.StorageFee) + (quote.OneCurrency ? "" : " " + Money.Short[quote.StorageCurrency]);
            string ask = quote.OneCurrency ? "Pay " + Money.Amount(quote.Total) + " and send?" : "Pay " + Money.Amount(quote.TransportFee) + " + " + storage + "?";
            var warning = page.Line(2, confirm ? ask : "Cannot be cancelled.");
            // The amount and its currency's name, no parts under it (the user, 2026-10-29: "remove the detail fee composition",
            // then "all fee please show currency name also"); with two currencies the confirm's ask names the storage.
            page.Fee("Fee:", Money.Amount(quote.OneCurrency ? quote.Total : quote.TransportFee), Money.Names[quote.Currency]);
            page.RouteMap(Ports.ByName(quote.From.Name), Ports.ByName(destination.Name));
            // Send asks to confirm in place: its line and label change (0.1.6: it rebuilt the list and page). The page's redraw
            // above shares `confirm`, so a refresh keeps the asking state.
            BoardButton main = null;
            main = page.MainButton(confirm ? "Confirm" : "Send", () =>
            {
                if (!confirm)
                {
                    confirm = true;
                    if (warning) warning.text = ask;
                    BoardPage.Relabel(main, "Confirm");
                    return;
                }
                // The quote is made again at the click: past a midnight the day's rate and the storage days have moved, so a
                // total other than the one asked about is shown and asked again rather than paid (the code review, 2026-10-28).
                var fresh = Transport.QuoteFor(record, info, destination, yard.region);
                if (fresh.TransportFee != quote.TransportFee || fresh.StorageFee != quote.StorageFee || GameClock.DayOf(fresh.Arrive) != GameClock.DayOf(quote.Arrive))
                {
                    Notify.Show("The fee has changed.");
                    TransportPage(index, record, destination, true);
                    return;
                }
                if (Transport.Book(record, fresh)) ShowShips(index);
            });
        }

        // A ship not stored: the destinations of a tow, nearest to her first, her current shipyard among them ("E. Allowed").
        // A refusal (Tow.Refusal) is told at the click, and her page stays.
        private void ShowTowDestinations(int index, string to, bool confirm)
        {
            var boat = Ships.BoatOf(index);
            if (!boat || Storage.RecordOf(index) != null || Transport.RecordOf(index) != null) { ShowShips(index); return; }
            string refusal = Tow.Refusal(boat);
            if (refusal != null) { Notify.Show(refusal); ShowShips(index); return; }
            var destinations = new List<KeyValuePair<ShipyardInfo, int>>();
            foreach (var other in Shipyards.Table)
                if (Shipyards.Present(other))
                    destinations.Add(new KeyValuePair<ShipyardInfo, int>(other, Tow.MilesFrom(other.Name, boat.transform.position)));
            destinations.Sort((a, b) => a.Value.CompareTo(b.Value));
            items.Clear();
            int selected = 0;
            for (int i = 0; i < destinations.Count; i++)
            {
                string name = destinations[i].Key.Name;
                items.Add(new BoardItem { Text = "to " + name, Clicked = () => ShowTowDestinations(index, name, false) });
                if (name == to) selected = i;
            }
            ShowList(BoardList.Destinations, destinations.Count > 0 ? selected : -1, false, "back", () => ShowShips(index));
            if (destinations.Count == 0) { BeginPage(() => ShowShips(index)); page.Title("No shipyard", false); return; }
            TowPage(index, boat, destinations[selected].Key, confirm);
        }

        // A tow's booking page: its hours, both legs' miles and fees, the route on the map; Send → Confirm as a transport's.
        private void TowPage(int index, PurchasableBoat boat, ShipyardInfo destination, bool confirm)
        {
            var quote = Tow.QuoteFor(boat, info, destination, yard.region);
            var clock = quote.Hours;
            BeginPage(() => ShowTowDestinations(index, destination.Name, confirm));
            page.Title(Ships.NameOf(index) + " to " + destination.Name, false);
            // The user's words (2026-10-29): "Tugboat leave:", "Reach ship in:", no "Takes her", "Arrives" in its place.
            page.Block(BoardPage.Left, 0.70f, "Tugboat leave:", "day " + GameClock.DayOf(clock.Depart));
            page.Block(BoardPage.Left, 0.50f, "Reach ship in:", "day " + GameClock.DayOf(clock.Reach));
            page.Block(BoardPage.Left, 0.30f, "Arrives:", "day " + GameClock.DayOf(clock.Arrive), "at the earliest");
            var toShip = page.Line(0, "To ship: " + Miles.Text(quote.TugMiles));
            var back = page.Line(1, "Back to port: " + Miles.Text(quote.TowMiles));
            BoardPage.FitLines(toShip, back);
            string ask = "Pay " + Money.Amount(quote.Total) + " and send?";
            var warning = page.Line(2, confirm ? ask : "Cannot be cancelled.");
            page.Fee("Fee:", Money.Amount(quote.Total), Money.Names[quote.Currency]);
            page.TowMap(Ports.ByName(info.Name), quote.At, Ports.ByName(destination.Name), -1, null, 0f);
            BoardButton main = null;
            main = page.MainButton(confirm ? "Confirm" : "Send", () =>
            {
                if (!confirm)
                {
                    confirm = true;
                    if (warning) warning.text = ask;
                    BoardPage.Relabel(main, "Confirm");
                    return;
                }
                // Made again at the click, as a transport's: the day's rate, her place and her cargo may have changed.
                var fresh = boat ? Tow.QuoteFor(boat, info, destination, yard.region) : null;
                if (fresh == null) { ShowShips(index); return; }
                if (fresh.TugFee != quote.TugFee || fresh.TowFee != quote.TowFee || GameClock.DayOf(fresh.Hours.Arrive) != GameClock.DayOf(clock.Arrive))
                {
                    Notify.Show("The fee has changed.");
                    TowPage(index, boat, destination, true);
                    return;
                }
                if (Tow.Book(boat, fresh)) ShowShips(index);
            });
        }

        // Her page while a tow is booked or under way: where the tug is in its day, and the map with the tug on it.
        private void TowTripPage(TransportRecord tow)
        {
            double hours = GameClock.Hours, due = tow.reachHour + Tow.WaitHours;
            page.Block(BoardPage.Left, 0.64f, "Tug from:", tow.from);
            page.Block(BoardPage.Left, 0.41f, "Destination:", tow.to);
            if (Tow.PickedUp(tow))
                page.Block(BoardPage.Left, 0.18f, "Arrives:", "day " + GameClock.DayOf(tow.arriveHour), "under tow");
            else if (hours < tow.departHour)
                page.Block(BoardPage.Left, 0.18f, "Tug leaves:", "day " + GameClock.DayOf(tow.departHour), "reaches her day " + GameClock.DayOf(tow.reachHour));
            else if (hours < tow.reachHour)
                page.Block(BoardPage.Left, 0.18f, "Reaches her:", "day " + GameClock.DayOf(tow.reachHour), "on its way to her");
            else
                page.Block(BoardPage.Left, 0.18f, "Tug waiting:", "at your ship", hours < due ? "takes her day " + GameClock.DayOf(due) : "until nobody is near");
            double tug = Tow.TugProgress(hours, tow.departHour, tow.reachHour, tow.pickupHour, tow.arriveHour);
            page.TowMap(Ports.ByName(tow.from), new Vector3(tow.atX, 0f, tow.atZ), Ports.ByName(tow.to), tug, BoardPage.TripMapPlace, BoardPage.TripMapScale);
        }
    }

    // ==== BoardPage (the tow's map) ====

    internal sealed partial class BoardPage
    {
        // On her ship page the map stands smaller, right of the blocks and under the rename hint.
        internal static readonly Vector3 TripMapPlace = new Vector3(-0.42f, 0.2f, 0.65f);
        internal const float TripMapScale = 0.6f;

        // The tow's route on the ocean map (SPEC.md §6.4): a line from the ordering yard to where she lay at
        // booking (`her`, real coordinates) and on to the destination, the destination's marker, and with tug 0-2
        // (Tow.TugProgress) a smaller copy of that marker where the tug is now. A port stands at its own ocean marker; her place,
        // and a port without one, by the fit (MapFit). `place` and `scale` put the map elsewhere on the page; null keeps the
        // mission map's own place.
        internal void TowMap(Port yard, Vector3 her, Port destination, double tug, Vector3? place, float scale)
        {
            var fit = MapFit.FromPorts();
            if (fit == null || !parts.OceanMap || !yard || !destination) return;
            Vector3 a = fit.Marker(yard), b = fit.At(her), c = fit.Marker(destination);
            var map = MapCopy(parts.OceanMap);
            if (place.HasValue)
            {
                map.transform.localPosition = place.Value;
                map.transform.localScale = Vector3.one * scale;
            }
            // The route cut at the map's edge (the user, 2026-10-29: Kicia Bay and Chronos lie off the ocean map, and she may):
            // the map quad's own mesh bounds, in the frame the markers and the line share. Each piece inside gets a line of its
            // own, and a marker off the map is hidden (Claude's choice). No mesh: drawn uncut, as before.
            var filter = map.GetComponent<MeshFilter>();
            var mesh = filter ? filter.sharedMesh : null;
            Vector2 min = mesh ? (Vector2)mesh.bounds.min : new Vector2(float.MinValue, float.MinValue);
            Vector2 max = mesh ? (Vector2)mesh.bounds.max : new Vector2(float.MaxValue, float.MaxValue);
            var line = map.GetComponentInChildren<LineRenderer>(true);
            if (line)
            {
                var pieces = MapClip.Inside(new[] { a, b, c }, min, max);
                line.positionCount = 0;
                for (int i = 0; i < pieces.Count; i++)
                {
                    var drawn = i == 0 ? line : LineLike(line, map);
                    drawn.positionCount = pieces[i].Count;
                    drawn.SetPositions(pieces[i].ToArray());
                }
            }
            var marker = map.transform.Find("Line destination");
            if (marker)
            {
                marker.localPosition = c;
                marker.gameObject.SetActive(MapClip.Contains(c, min, max));
                Vector3 at = tug <= 1 ? Vector3.Lerp(a, b, (float)tug) : Vector3.Lerp(b, c, (float)(tug - 1));
                if (tug >= 0 && MapClip.Contains(at, min, max))
                {
                    var copy = Object.Instantiate(marker.gameObject, map.transform, false);
                    copy.name = "shipwright tug";
                    copy.SetActive(true);
                    copy.transform.localScale = marker.localScale * 0.6f;
                    copy.transform.localPosition = at;
                }
            }
            map.SetActive(true);
        }

        // Another line drawn as the map's own: a copy of its object when it is a child of the map; on the map's own object (not
        // known from the game's files) a new child with its settings, as a copy of the object would copy the whole map.
        private static LineRenderer LineLike(LineRenderer line, GameObject map)
        {
            if (line.gameObject != map) return Object.Instantiate(line.gameObject, line.transform.parent, false).GetComponent<LineRenderer>();
            var go = new GameObject("shipwright route piece") { layer = map.layer };
            go.transform.SetParent(map.transform, false);
            var copy = go.AddComponent<LineRenderer>();
            copy.sharedMaterials = line.sharedMaterials;
            copy.useWorldSpace = line.useWorldSpace;
            copy.widthCurve = line.widthCurve;
            copy.widthMultiplier = line.widthMultiplier;
            copy.colorGradient = line.colorGradient;
            copy.alignment = line.alignment;
            copy.textureMode = line.textureMode;
            copy.numCapVertices = line.numCapVertices;
            copy.numCornerVertices = line.numCornerVertices;
            copy.shadowCastingMode = line.shadowCastingMode;
            copy.receiveShadows = line.receiveShadows;
            copy.sortingLayerID = line.sortingLayerID;
            copy.sortingOrder = line.sortingOrder;
            return copy;
        }

        // Lines longer than the page's 20 characters ("Back to port: 1246 miles"), set smaller together so they keep clear of
        // the Send button: as wide as "Cannot be cancelled.", the longest line the pages had beside it (Claude's choice).
        internal static void FitLines(params TextMesh[] lines)
        {
            if (lines.Length == 0 || !lines[0]) return;
            float allowed = TextFit.Width(lines[0], "Cannot be cancelled."), widest = 0f;
            foreach (var line in lines) if (line) widest = Math.Max(widest, TextFit.Width(line, line.text));
            if (allowed <= 0f || widest <= allowed) return;
            foreach (var line in lines) if (line) line.transform.localScale = LineScale * (allowed / widest);
        }
    }

    // ==== MapFit ====

    // The world on the ocean map (SPEC.md §6.4). The mission map draws only between ports, each at its own
    // marker (Port.oceanMapLocation, a place on the map); a place at sea has none. One straight fit of the markers against the
    // ports' real world positions places it: measured on 0.39.2 (run 20261029-182831), off by 0.0054 on average and 0.031 at most
    // of markers spanning 0.92. It is refitted at each draw (Claude's choice), so a game update that moves a marker moves the
    // fit. With ScrambledSeas the islands move and the map doesn't: the fit is wrong there, as the mission map's routes are.
    internal sealed class MapFit
    {
        // mapX = X[0]·x + X[1]·z + X[2], mapY the same with Y; x and z real world coordinates.
        internal readonly double[] X, Y;
        // The markers' depth on the map.
        private const float MarkerZ = -0.0001f;

        internal MapFit(double[] x, double[] y) { X = x; Y = y; }

        // m = a·x + b·z + c by least squares: { a, b, c }, or null with fewer than 3 points or all of them on one line. Centred
        // first, so world coordinates of 10^5 m lose no precision.
        internal static double[] Solve(IList<double> xs, IList<double> zs, IList<double> ms)
        {
            int n = xs.Count;
            if (n < 3 || zs.Count != n || ms.Count != n) return null;
            double mx = 0, mz = 0, mm = 0;
            for (int i = 0; i < n; i++) { mx += xs[i]; mz += zs[i]; mm += ms[i]; }
            mx /= n; mz /= n; mm /= n;
            double sxx = 0, sxz = 0, szz = 0, sxm = 0, szm = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - mx, dz = zs[i] - mz, dm = ms[i] - mm;
                sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxm += dx * dm; szm += dz * dm;
            }
            double det = sxx * szz - sxz * sxz;
            if (det <= 1e-9 * sxx * szz || det <= 0) return null;
            double a = (sxm * szz - szm * sxz) / det, b = (szm * sxx - sxm * sxz) / det;
            return new[] { a, b, mm - a * mx - b * mz };
        }

        // From the game's ports with an ocean marker; null when they don't make a fit.
        internal static MapFit FromPorts()
        {
            var xs = new List<double>(); var zs = new List<double>(); var mapX = new List<double>(); var mapY = new List<double>();
            foreach (var port in Ports.All())
            {
                if (!port.oceanMapLocation) continue;
                Vector3 real = Tow.Real(port.transform.position);
                Vector3 marker = port.oceanMapLocation.localPosition;
                xs.Add(real.x); zs.Add(real.z); mapX.Add(marker.x); mapY.Add(marker.y);
            }
            var x = Solve(xs, zs, mapX);
            var y = Solve(xs, zs, mapY);
            return x != null && y != null ? new MapFit(x, y) : null;
        }

        internal Vector3 At(Vector3 real) =>
            new Vector3((float)(X[0] * real.x + X[1] * real.z + X[2]), (float)(Y[0] * real.x + Y[1] * real.z + Y[2]), MarkerZ);

        internal Vector3 Marker(Port port) => port.oceanMapLocation ? port.oceanMapLocation.localPosition : At(Tow.Real(port.transform.position));
    }

    // ==== MapClip ====

    // A route on the map cut to the map's rectangle (x and y; z follows along): the pieces inside, each a run of points.
    // Liang–Barsky on each leg; a leg that ends inside and the next that starts there make one piece.
    internal static class MapClip
    {
        internal static bool Contains(Vector3 p, Vector2 min, Vector2 max) => p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y;

        internal static List<List<Vector3>> Inside(IList<Vector3> points, Vector2 min, Vector2 max)
        {
            var pieces = new List<List<Vector3>>();
            List<Vector3> piece = null;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3 p = points[i], q = points[i + 1];
                double t0 = 0, t1 = 1, dx = q.x - p.x, dy = q.y - p.y;
                bool seen = Edge(-dx, p.x - min.x, ref t0, ref t1) && Edge(dx, max.x - p.x, ref t0, ref t1)
                    && Edge(-dy, p.y - min.y, ref t0, ref t1) && Edge(dy, max.y - p.y, ref t0, ref t1) && t1 > t0;
                if (!seen) { piece = null; continue; }
                if (piece == null || t0 > 0)
                {
                    piece = new List<Vector3> { Vector3.Lerp(p, q, (float)t0) };
                    pieces.Add(piece);
                }
                piece.Add(Vector3.Lerp(p, q, (float)t1));
                if (t1 < 1) piece = null;
            }
            return pieces;
        }

        // One edge: the leg's share t kept where num·t ≤ limit.
        private static bool Edge(double num, double limit, ref double t0, ref double t1)
        {
            if (num == 0) return limit >= 0;
            double t = limit / num;
            if (num < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
    }
}
