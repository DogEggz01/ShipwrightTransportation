using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ShipwrightAndTransport
{
    // ==== Orders ====

    // Ordering, delivering and building copies (§8). One open order per shipyard (decision 9); each shipyard builds only
    // its own hulls (decision 10); the building fee is paid when ordering and nothing is refunded (decisions 11, 15).
    internal static class Orders
    {
        // The six goods, as their ShipItem.name; OrderRecord.delivered counts them in this order.
        internal static readonly string[] Goods = { "logs", "lumber", "nails", "wool", "copper", "iron" };

        internal struct Bill
        {
            internal int[] Amounts;
            internal int Hours;
            internal int Total { get { int total = 0; foreach (int a in Amounts) total += a; return total; } }
        }

        // §8.2, decided by the user.
        internal static Bill BillFor(int hull)
        {
            switch (hull)
            {
                case 10: case 90: return new Bill { Amounts = new[] { 1, 1, 2, 1, 1, 0 }, Hours = 72 };
                case 40: return new Bill { Amounts = new[] { 1, 2, 3, 1, 1, 0 }, Hours = 84 };
                case 20: case 80: return new Bill { Amounts = new[] { 2, 4, 5, 2, 2, 1 }, Hours = 168 };
                case 50: return new Bill { Amounts = new[] { 3, 5, 6, 2, 2, 2 }, Hours = 240 };
                case 30: return new Bill { Amounts = new[] { 3, 6, 8, 3, 3, 2 }, Hours = 312 };
                case 70: return new Bill { Amounts = new[] { 3, 7, 10, 4, 4, 2 }, Hours = 360 };
                default: return new Bill { Amounts = new int[6], Hours = 0 };
            }
        }

        internal static OrderRecord OrderAt(string shipyard) => State.Save.orders.Find(o => o.shipyard == shipyard);

        internal static int Price(int hull)
        {
            var template = CopyEngine.Template(hull);
            if (template != null) return template.Price;
            var original = Ships.OriginalBoat(hull);
            return original ? original.price : 0;
        }

        internal static int Missing(OrderRecord order)
        {
            var bill = BillFor(order.hull);
            int missing = 0;
            for (int i = 0; i < 6; i++) missing += Math.Max(0, bill.Amounts[i] - order.delivered[i]);
            return missing;
        }

        internal static bool Building(OrderRecord order) => order.readyHour >= 0;

        // [Building] Instant ships (SPEC.md §8.6): an order finishes at once, with no materials and no building time.
        internal static bool Instant => Plugin.InstantShips != null && Plugin.InstantShips.Value;

        // With Instant ships on, an open order is ready now, whether it waits for goods or is building; otherwise its ready hour
        // stays (-1 while it waits for goods).
        internal static double ReadyHour(double readyHour, double now, bool instant) => instant && (readyHour < 0 || readyHour > now) ? now : readyHour;

        internal static string RefusalToOrder(ShipyardInfo info, int hull)
        {
            if (OrderAt(info.Name) != null) return "One order at a time.";
            if (CopyEngine.Template(hull) == null) return "This ship can't be\nbuilt right now.";
            if (!CopyNumberLeft(State.Save.nextCopy, State.Save.orders.Count)) return "The shipyards can't build\nany more ships.";
            return null;
        }

        // Each open order takes the next copy number when it is finished, so a new one needs a number beyond theirs: with two
        // orders open at the last number, the second never finished (the code review, 2026-10-28).
        internal static bool CopyNumberLeft(int nextCopy, int openOrders) => nextCopy + openOrders <= CopyEngine.MaxCopies;

        // Ordering pays the building fee in the shipyard's currency (decision 13). The order page's typed name passes
        // to the ship when she is finished. With Instant ships on she is finished at once: built is her save index then, else -1.
        internal static bool Place(Shipyard yard, ShipyardInfo info, int hull, string typed, out int built)
        {
            built = -1;
            string refusal = RefusalToOrder(info, hull);
            if (refusal != null) { Notify.Show(refusal); return false; }
            int currency = yard.region;
            int fee = Fees.BuildingFee(Price(hull), Money.Rate(currency));
            if (!Money.Pay(currency, fee)) return false;
            var order = new OrderRecord
            {
                shipyard = info.Name, hull = hull, typed = ShipNames.Clean(typed), orderedDay = GameClock.Day, feePaid = fee, currency = currency,
            };
            State.Save.orders.Add(order);
            Log.Info("Ordered a " + ShipTypes.Name(hull, null) + " at " + info.Name + " for " + fee + " " + Money.Short[currency] + (Instant ? ", built at once (Instant ships)." : "."));
            if (Instant)
            {
                order.readyHour = GameClock.Hours;
                built = Complete(order);
            }
            DropAreas.Refresh();
            return true;
        }

        // Cancelling refunds neither the coins nor the delivered goods (decisions 11, 15).
        internal static void Cancel(ShipyardInfo info)
        {
            var order = OrderAt(info.Name);
            if (order == null) return;
            State.Save.orders.Remove(order);
            Log.Info("Cancelled the order at " + info.Name + ".");
            DropAreas.Refresh();
        }

        // The goods taken at drop areas, by their objects' instance IDs: DestroyItem destroys at the end of the frame, and a
        // good with more than one collider touches the area once per collider in that frame, so it is counted once only.
        private static readonly OnceEach taken = new OnceEach();

        // A good touched the drop area (§8.3): taken when this shipyard has an open order that still needs it, the item is
        // sold and not mission cargo, and it is not in an inventory slot. Otherwise it is left, with the reason.
        internal static void Offer(string shipyard, Good good)
        {
            if (!good || State.Off) return;
            var item = good.GetComponent<ShipItem>();
            if (!item || !item.sold || item.GetCurrentInventorySlot() >= 0 || taken.Seen(item.gameObject.GetInstanceID())) return;
            var order = OrderAt(shipyard);
            if (order == null) { Notify.Show("No order at this shipyard."); return; }
            if (good.GetAssignedMission() != null) { Notify.Show("That is mission cargo."); return; }
            int kind = Array.IndexOf(Goods, item.name);
            var bill = BillFor(order.hull);
            if (Building(order) || kind < 0 || order.delivered[kind] >= bill.Amounts[kind]) { Notify.Show("Not needed for this ship."); return; }
            order.delivered[kind]++;
            taken.Add(item.gameObject.GetInstanceID());
            item.DestroyItem();
            if (Missing(order) > 0)
            {
                Notify.Show("Delivered " + Goods[kind] + "\n( " + order.delivered[kind] + " / " + bill.Amounts[kind] + " )");
                return;
            }
            order.readyHour = GameClock.Hours + bill.Hours;
            Notify.Show("All materials delivered.\nReady in " + Fees.Remaining(bill.Hours) + ".", 5f);
            Log.Info("All materials delivered at " + shipyard + "; ready on day " + GameClock.DayOf(order.readyHour) + ".");
        }

        private static float nextCheck;

        // Every few seconds and after loading, wherever the player is (§8.5).
        internal static void Tick(bool now = false)
        {
            if (State.Off || (!now && Time.time < nextCheck)) return;
            nextCheck = Time.time + 2f;
            if (!Sun.sun) return;
            double hours = GameClock.Hours;
            bool instant = Instant;
            foreach (var order in State.Save.orders.ToArray())
            {
                order.readyHour = ReadyHour(order.readyHour, hours, instant);
                if (Building(order) && hours >= order.readyHour) Complete(order);
            }
        }

        // The finished ship takes the next number of her type and the typed name, and is stored at the building shipyard
        // as a record only, her fee running from the completion day (decision 12). She is built at her first take-out.
        // Returns her save index, or -1 when no copy number is left.
        private static int Complete(OrderRecord order)
        {
            var copy = CopyEngine.NewRecord(order.hull);
            if (copy == null) { Log.Error("Orders: no copy number is left; the order at " + order.shipyard + " waits."); return -1; }
            string type = ShipTypes.Name(order.hull, null);
            copy.number = ShipNames.NextNumber(State.Save, type);
            int index = CopyEngine.IndexOf(copy.k);
            if (!string.IsNullOrEmpty(order.typed)) ShipNames.SetTyped(State.Save, index, order.typed);
            var template = CopyEngine.Template(order.hull);
            State.Save.stored.Add(new StoredShip
            {
                saveIndex = index, shipyard = order.shipyard, slot = -1, dayStored = GameClock.DayOf(order.readyHour),
                lengthM = template != null ? template.Length : 10f, feeUnits = Fees.DailyUnits(Price(order.hull)),
            });
            State.Save.orders.Remove(order);
            DropAreas.Refresh();
            string name = Ships.NameOf(index);
            Log.Info(name + " (copy " + copy.k + ") is ready in " + order.shipyard + ".");
            Notify.Show(name + " is ready in\n" + order.shipyard + " shipyard.", 5f);
            return index;
        }
    }

    // Keys seen once; Unity's instance IDs are never reused within a session.
    internal sealed class OnceEach
    {
        private readonly HashSet<int> seen = new HashSet<int>();

        internal bool Seen(int key) => seen.Contains(key);

        internal void Add(int key) => seen.Add(key);
    }

    // ==== BoardOrders ====

    // The "order" tab (§8, pages 03, 04, 04b): this shipyard's hulls; the open order's hull reads "– ordered".
    internal sealed partial class Board
    {
        // A name typed on an order page before ordering, per shipyard and hull.
        private static readonly Dictionary<string, string> pendingNames = new Dictionary<string, string>();
        private bool confirmCancel;

        internal void ShowOrders(int hull)
        {
            if (info.Builds.Length == 0) { ShowShips(-1); return; }
            var order = Orders.OrderAt(info.Name);
            if (hull < 0) hull = order != null ? order.hull : info.Builds[0];
            items.Clear();
            foreach (int h in info.Builds)
            {
                int each = h;
                string type = ShipTypes.Name(h, null);
                items.Add(new BoardItem { Text = order != null && order.hull == h ? type + Separator + "ordered" : type, Clicked = () => { confirmCancel = false; ShowOrders(each); } });
            }
            SetTabs(BoardTab.Order);
            ShowList(BoardList.Hulls, Array.IndexOf(info.Builds, hull), false, "close", () => Close());
            if (order != null && order.hull == hull) OrderStatusPage(order);
            else OrderPage(hull, order);
        }

        private static string BuildTime(int hours)
        {
            double days = hours / 24.0;
            return days == Math.Floor(days) ? Days((int)days) : days.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " days";
        }

        // Order a ship (picture 03): the number she will get, the build time, where the goods go, the bill and the fee.
        private void OrderPage(int hull, OrderRecord existing)
        {
            string type = ShipTypes.Name(hull, null);
            string key = info.Name + "/" + hull;
            BeginPage(() => ShowOrders(hull));
            NameTitle(() => pendingNames.TryGetValue(key, out var typed) && typed.Length > 0 ? typed : ShipNames.Default(type, ShipNames.PeekNumber(State.Save, type)),
                () => pendingNames.TryGetValue(key, out var typed) ? typed : "",
                name => pendingNames[key] = name);
            var bill = Orders.BillFor(hull);
            if (Orders.Instant)
            {
                // Instant ships (SPEC.md §8.6): built at once, no materials, so no goods to bring and no table.
                page.Block(BoardPage.Left, 0.64f, "Build time:", "at once", "no materials needed");
                page.Block(BoardPage.Left, 0.36f, "When it is built:", "stored here", "see the ship list");
            }
            else
            {
                page.Block(BoardPage.Left, 0.64f, "Build time:", BuildTime(bill.Hours), "after all materials delivered");
                page.Block(BoardPage.Left, 0.36f, "Bring the goods to:", "SHIPWRIGHT sign", "on the dock");
                page.Block(BoardPage.Left, 0.08f, "When it is built:", "stored here", "see the ship list");
                Table("Materials:", bill, null);
            }
            int currency = yard.region;
            int fee = Fees.BuildingFee(Orders.Price(hull), Money.Rate(currency));
            page.Fee("Building fee:", Money.Amount(fee), Money.Names[currency] + ", paid now");
            if (Orders.Instant)
            {
                page.Small("Instant ships is on:", BoardPage.Left, -0.33f);
                page.Small("no goods, no waiting.", BoardPage.Left, -0.405f);
            }
            else
            {
                page.Small("One order at a time.", BoardPage.Left, -0.33f);
                page.Small("A cancelled order refunds", BoardPage.Left, -0.405f);
                page.Small("neither coins nor goods.", BoardPage.Left, -0.48f);
            }
            page.MainButton("Order", () =>
            {
                pendingNames.TryGetValue(key, out var typed);
                if (!Orders.Place(yard, info, hull, typed, out int built)) return;
                pendingNames.Remove(key);
                // Built at once: the ship list opens on her.
                if (built > 0) ShowShips(built);
                else ShowOrders(hull);
            }, (existing != null ? 1 : 0) + " / 1");
        }

        // The open order (pictures 04, 04b): waiting for goods, then building with the time left.
        private void OrderStatusPage(OrderRecord order)
        {
            string type = ShipTypes.Name(order.hull, null);
            BeginPage(() => ShowOrders(order.hull));
            NameTitle(() => order.typed.Length > 0 ? order.typed : ShipNames.Default(type, ShipNames.PeekNumber(State.Save, type)),
                () => order.typed, name => order.typed = name);
            var bill = Orders.BillFor(order.hull);
            bool building = Orders.Building(order);
            page.Block(BoardPage.Left, 0.64f, "Ordered:", "day " + order.orderedDay, Money.Amount(order.feePaid) + " " + Money.Names[order.currency] + " paid");
            if (building)
            {
                page.Block(BoardPage.Left, 0.36f, "Status:", "building", "all materials delivered");
                page.Block(BoardPage.Left, 0.08f, "When it is built:", "stored here", "see the ship list");
            }
            else
            {
                int missing = Orders.Missing(order);
                page.Block(BoardPage.Left, 0.36f, "Status:", "waiting for goods", missing + (missing == 1 ? " good" : " goods") + " still to bring");
                page.Block(BoardPage.Left, 0.08f, "Bring the goods to:", "SHIPWRIGHT sign", "on the dock");
            }
            Table("Delivered:", bill, order.delivered);
            // While goods are missing, the remaining time is the full build time (decision 44).
            if (building) page.Fee("Remaining time:", Fees.Remaining(order.readyHour - GameClock.Hours), "ready on day " + GameClock.DayOf(order.readyHour));
            else page.Fee("Remaining time:", BuildTime(bill.Hours), null);
            page.Small("Cancelling refunds", BoardPage.Left, -0.33f);
            page.Small("neither coins nor goods.", BoardPage.Left, -0.405f);
            int hull = order.hull;
            BoardButton cancel = null;
            cancel = page.MainButton(confirmCancel ? "Confirm" : "Cancel order", () =>
            {
                // The first click only asks to confirm: the label changes in place (0.1.6: it rebuilt the page).
                if (!confirmCancel) { confirmCancel = true; BoardPage.Relabel(cancel, "Confirm"); return; }
                confirmCancel = false;
                Orders.Cancel(info);
                ShowOrders(hull);
            }, "1 / 1");
        }

        // The bill as a two-column table in the right column; amounts right-aligned inside the margin (§10.2).
        private void Table(string heading, Orders.Bill bill, int[] delivered)
        {
            page.Text(parts.Label, heading, new Vector3(BoardPage.Right, 0.64f, 0.648f), BoardPage.LabelScale, BoardPage.AnchorMiddleLeft, BoardPage.AlignLeft);
            int row = 0;
            for (int i = 0; i < Orders.Goods.Length; i++)
            {
                if (bill.Amounts[i] == 0) continue;
                float y = 0.54f - 0.087f * row++;
                page.Text(parts.Label, Orders.Goods[i], new Vector3(BoardPage.Right, y, 0.648f), BoardPage.LabelScale, BoardPage.AnchorMiddleLeft, BoardPage.AlignLeft);
                string amount = delivered == null ? bill.Amounts[i].ToString() : delivered[i] + " / " + bill.Amounts[i];
                page.Text(parts.Value, amount, new Vector3(BoardPage.TableAmount, y, 0.648f), BoardPage.ValueScale, BoardPage.AnchorMiddleRight, BoardPage.AlignRight);
            }
        }
    }

    // ==== DropAreas ====

    // The material drop area (§8.3, §8.4): a 2.5 × 2.5 m trigger, 1.6 m tall, a few metres from the documents, with the
    // "SHIPWRIGHT / materials here" sign in its middle. It exists while the shipyard has an open order and its island is
    // loaded; shipyards load and unload with their islands, so it is checked again every few seconds.
    internal sealed class DropArea : MonoBehaviour
    {
        internal string Shipyard;

        private void OnTriggerEnter(Collider other)
        {
            if (!other || other.CompareTag("Player") || !other.CompareTag("Good")) return;
            try { Orders.Offer(Shipyard, other.GetComponent<Good>()); }
            catch (Exception e) { Log.Error("Drop area: " + e); }
        }
    }

    internal static class DropAreas
    {
        // Per building shipyard: the area's centre offset from the documents (metres, x east, z north), its yaw and the
        // floor height below the documents (assets/docks/drop-areas.json).
        private struct Pose
        {
            internal float X, Z, Yaw, Floor;
            internal Pose(float x, float z, float yaw, float floor) { X = x; Z = z; Yaw = yaw; Floor = floor; }
        }

        private static readonly Dictionary<string, Pose> Poses = new Dictionary<string, Pose>
        {
            { "Gold Rock City", new Pose(5.5f, 3.5f, 30f, -0.77f) },
            { "Fort Aestrin", new Pose(-5.5f, -2.5f, 23f, -0.735f) },
            { "Dragon Cliffs", new Pose(5.0f, -1.8f, 30f, -0.59f) },
            { "Kicia Bay", new Pose(5.0f, -2.5f, 21f, -0.65f) },
        };

        private static readonly Dictionary<string, GameObject> areas = new Dictionary<string, GameObject>();
        private static float nextCheck;

        internal static void Tick()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 3f;
            Refresh();
        }

        // An area for every loaded shipyard with an open order; none elsewhere.
        internal static void Refresh()
        {
            foreach (var name in new List<string>(areas.Keys))
                if (!areas[name] || State.Off || Orders.OrderAt(name) == null)
                {
                    if (areas[name]) Object.Destroy(areas[name]);
                    areas.Remove(name);
                }
            // With no order open no area is built, so the scene search is skipped (the code review, 2026-10-28: it ran every 3 s).
            if (State.Off || State.Save.orders.Count == 0) return;
            foreach (var yard in Shipyards.Loaded())
            {
                var info = Shipyards.InfoOf(yard);
                if (info == null || areas.ContainsKey(info.Name) || Orders.OrderAt(info.Name) == null || !Poses.ContainsKey(info.Name)) continue;
                var documents = Shipyards.Documents(yard);
                if (!documents) continue;
                int documentsFound = 0;
                foreach (var d in Object.FindObjectsOfType<ShipyardDocuments>()) if (d.shipyard == yard) documentsFound++;
                try { areas[info.Name] = Build(info.Name, documents.transform, Poses[info.Name], documentsFound); }
                catch (Exception e) { Log.Error("Drop area at " + info.Name + " failed: " + e); }
            }
        }

        // The area's centre: its fixed offset from the documents and the measured floor height (0.1.6, the user: fixed spots, no
        // raycasting; 0.1.5 snapped it to a ray's hit within 0.6 m).
        private static Vector3 FloorPoint(Transform documents, Pose pose) =>
            documents.position + new Vector3(pose.X, pose.Floor, pose.Z);

        private static string PathOf(Transform t)
        {
            string path = t ? t.name : "";
            for (var p = t ? t.parent : null; p; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // Built relative to the live documents, so floating-origin shifts don't matter; parented beside them (never on
        // them: other mods clone the documents).
        private static GameObject Build(string shipyard, Transform documents, Pose pose, int documentsFound)
        {
            var root = new GameObject("Shipwright drop area " + shipyard);
            root.transform.SetParent(documents.parent, true);
            root.transform.position = FloorPoint(documents, pose);
            Vector3 toDocuments = documents.position - root.transform.position;
            toDocuments.y = 0f;
            root.transform.rotation = toDocuments.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toDocuments) : Quaternion.Euler(0f, pose.Yaw, 0f);

            var trigger = new GameObject("trigger");
            trigger.transform.SetParent(root.transform, false);
            trigger.transform.localRotation = Quaternion.Inverse(root.transform.rotation) * Quaternion.Euler(0f, pose.Yaw, 0f);
            var box = trigger.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2.5f, 1.6f, 2.5f);
            box.center = new Vector3(0f, 0.8f, 0f);
            var body = trigger.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            trigger.AddComponent<DropArea>().Shipyard = shipyard;

            Sign.Build(root.transform);
            // At Info since 0.1.6 (B3: the user saw no sign at Fort Aestrin, and 0.1.4 logged this only at Debug).
            Vector3 offset = root.transform.position - documents.position;
            Log.Info("Drop area built at " + shipyard + ": " + offset.ToString("F2") + " from the documents '" + PathOf(documents) + "' (" + documentsFound +
                " documents object(s) for this shipyard, active " + documents.gameObject.activeInHierarchy + "); parent '" + PathOf(root.transform.parent) + "', active " + root.activeInHierarchy + ".");
            return root;
        }
    }

    // The sign (decisions 32, 36), from primitives with no collider and no asset bundle: a post, a board, and a painted
    // face whose local +z points at the documents.
    internal static class Sign
    {
        private static Texture2D paint;

        internal static void Build(Transform parent)
        {
            var sign = new GameObject("Shipwright sign");
            sign.transform.SetParent(parent, false);
            Box(sign.transform, "post", new Vector3(0.09f, 1.3f, 0.09f), new Vector3(0f, 0.65f, 0f), Colour(0x5f4129));
            Box(sign.transform, "board", new Vector3(0.8f, 0.44f, 0.045f), new Vector3(0f, 1.12f, 0.07f), Colour(0xa97c50));
            var face = GameObject.CreatePrimitive(PrimitiveType.Quad);
            face.name = "painted";
            Object.Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(sign.transform, false);
            face.transform.localPosition = new Vector3(0f, 1.12f, 0.0935f);
            face.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            face.transform.localScale = new Vector3(0.76f, 0.42f, 1f);
            var renderer = face.GetComponent<Renderer>();
            renderer.sharedMaterial = Shared(renderer, "painted", m => m.mainTexture = Paint());
        }

        private static void Box(Transform parent, string name, Vector3 size, Vector3 position, Color colour)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            Object.Destroy(cube.GetComponent<Collider>());
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = size;
            var renderer = cube.GetComponent<Renderer>();
            renderer.sharedMaterial = Shared(renderer, name, m => m.color = colour);
        }

        // One material per part, shared by every sign: Renderer.material made a copy at each build, which stayed behind when
        // the island unloaded the sign (the code review, 2026-10-28). Each is the primitive's own material, as that copy was,
        // with its colour or paint; not scene objects, so they last the session. Matte (the user, 2026-10-28: "please don't make
        // it reflective"): no gloss, no metal, no specular highlights or reflections.
        private static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        private static Material Shared(Renderer renderer, string part, Action<Material> dress)
        {
            if (materials.TryGetValue(part, out var material) && material) return material;
            material = new Material(renderer.sharedMaterial) { name = "Shipwright sign " + part };
            dress(material);
            material.SetFloat("_Glossiness", 0f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_SpecularHighlights", 0f);
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetFloat("_GlossyReflections", 0f);
            material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            materials[part] = material;
            return material;
        }

        // The board's wood with the frame `strokeRect(26, 26, 748, 388)`, line width 12, rgba(36, 20, 8, 0.9), and the words
        // painted in: "SHIPWRIGHT" at 92 px and "materials here" at 88 px, in IMMORTAL, rgba(28, 16, 6, 0.95), centred 73 px
        // above and below the middle (where 0.1.x's TextMesh words stood, 0.07 m of the 0.42 m face). Painted into the face,
        // they are lit as the board is and drawn on its front only: as TextMesh words, in the font's unlit two-sided material,
        // they were dark on a lit board at night and showed mirrored from the back (the user's test, 2026-10-28).
        private static Texture2D Paint()
        {
            if (paint) return paint;
            const int w = 800, h = 440;
            var font = Fonts.Find("IMMORTAL");
            var ink = new float[w * h];
            bool words = TextPixels.Draw(font, "SHIPWRIGHT", 92, w, h, w / 2f, h / 2f - 73f, ink)
                && TextPixels.Draw(font, "materials here", 88, w, h, w / 2f, h / 2f + 73f, ink);
            if (!words) Log.Warn("The SHIPWRIGHT sign's font IMMORTAL was not found: the sign shows its frame only.");
            paint = new Texture2D(w, h, TextureFormat.RGBA32, true);
            var wood = (Color32)Colour(0xa97c50);
            var frame = new Color32(36, 20, 8, 230);
            var text = new Color32(28, 16, 6, 255);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int top = h - 1 - y;
                    bool onFrame = InBand(x, 26, 26 + 748) && InBand(top, 26, 26 + 388)
                        && (Math.Abs(x - 26) <= 6 || Math.Abs(x - 774) <= 6 || Math.Abs(top - 26) <= 6 || Math.Abs(top - 414) <= 6);
                    var colour = onFrame ? Color32.Lerp(wood, frame, frame.a / 255f) : wood;
                    pixels[y * w + x] = Color32.Lerp(colour, text, ink[top * w + x] * 0.95f);
                }
            paint.SetPixels32(pixels);
            paint.Apply(true);
            return paint;
        }

        private static bool InBand(int v, int from, int to) => v >= from - 6 && v <= to + 6;

        private static Color Colour(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    // The game's fonts, found by name (§9.6).
    internal static class Fonts
    {
        private static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        internal static Font Find(string name)
        {
            if (cache.TryGetValue(name, out var font) && font) return font;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Font>())
                if (candidate && candidate.name == name) { cache[name] = candidate; return candidate; }
            return null;
        }
    }

    // ==== TextPixels ====

    // A line of text drawn into pixels, white on black, by the font's own material into a render texture (as the plates'
    // name, PlateFaces.NameMask), so a painted face can carry it in its own lit material.
    // shortcut: PlateFaces.NameMask has the same draw loop; merge the two once the plates' round 7 (another chat, 2026-10-28)
    // has landed its changes to PlateFaces (Nameplate.cs).
    internal static class TextPixels
    {
        // Adds `text` at `px` font pixels, centred on (centreX, centreY) of a top-down w x h canvas, into `coverage`
        // (top-down, 0..1 per pixel, the brighter of what was there and the text). Returns false without a font.
        internal static bool Draw(Font font, string text, int px, int w, int h, float centreX, float centreY, float[] coverage)
        {
            if (!font || string.IsNullOrEmpty(text)) return false;
            font.RequestCharactersInTexture(text, px);
            var m = PlateFaces.Measure(font, text, px);
            float pen = centreX - m.Advance / 2f;
            float baseline = centreY + (m.InkTop + m.InkBottom) / 2f;
            var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, h, 0);
                font.material.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Color.white);
                foreach (char c in text)
                {
                    if (!font.GetCharacterInfo(c, out CharacterInfo info, px)) continue;
                    float x0 = pen + info.minX, x1 = pen + info.maxX;
                    float top = baseline - info.maxY, bottom = baseline - info.minY;
                    GL.TexCoord(info.uvTopLeft); GL.Vertex3(x0, top, 0f);
                    GL.TexCoord(info.uvTopRight); GL.Vertex3(x1, top, 0f);
                    GL.TexCoord(info.uvBottomRight); GL.Vertex3(x1, bottom, 0f);
                    GL.TexCoord(info.uvBottomLeft); GL.Vertex3(x0, bottom, 0f);
                    pen += info.advance;
                }
                GL.End();
                GL.PopMatrix();
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply();
                var pixels = read.GetPixels32();
                // Read back bottom-up; the canvas is top-down.
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (h - 1 - y) * w + x;
                        coverage[i] = Math.Max(coverage[i], pixels[y * w + x].r / 255f);
                    }
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.Destroy(read);
            }
        }
    }

    // ==== CopyEngine ====

    // A vanilla hull captured as an inactive template (§7.1), with its deck (walk collider) and anchor inside, so that
    // Instantiate remaps every reference into the copy.
    internal sealed class HullTemplate
    {
        internal int Hull;
        internal GameObject Root;
        internal string Name;
        internal int Price;
        internal float Length;
        internal Transform WalkColsRoot;
        internal Quaternion DeckRotation;
        internal Vector3 DeckScale;
    }

    // Copies of the eight vanilla hulls (§7). Copy k owns the save-index block 1000 + 10k … +9: the boat at +0, her
    // ropes at +1…+4, her anchor at +9 (the offsets of the original's own indices), spare objects at +5…+8. A copy is
    // never in the vanilla save: her objects' and items' save data live in Shipwright's records instead (§7.4).
    internal static class CopyEngine
    {
        internal const int FirstIndex = 1000, BlockSize = 10, MaxCopies = 103, RegistrySize = 2048;
        private static readonly Dictionary<int, HullTemplate> templates = new Dictionary<int, HullTemplate>();
        private static GameObject holder;

        internal static int IndexOf(int k) => FirstIndex + BlockSize * k;

        internal static bool IsCopyIndex(int index) => index >= FirstIndex + BlockSize && index < RegistrySize && (index - FirstIndex) % BlockSize == 0;

        internal static bool InCopyBlock(int index) => index >= FirstIndex + BlockSize && index < RegistrySize;

        internal static CopyRecord RecordOfIndex(int index) =>
            IsCopyIndex(index) ? State.Save.copies.Find(c => IndexOf(c.k) == index) : null;

        internal static CopyRecord RecordOfBlock(int index) =>
            InCopyBlock(index) ? State.Save.copies.Find(c => IndexOf(c.k) == FirstIndex + (index - FirstIndex) / BlockSize * BlockSize) : null;

        internal static bool CanSpawn(CopyRecord copy) => copy != null && templates.TryGetValue(copy.hull, out var t) && t.Root;

        internal static HullTemplate Template(int hull) => templates.TryGetValue(hull, out var t) && t.Root ? t : null;

        // ---- Templates ----

        // On the main scene's sceneLoaded: after the scene's Awake, before its Start, so a copy gets the vanilla default
        // sail plan and stock hull options, as when the ship is bought new (decision 28).
        internal static void CaptureTemplates()
        {
            templates.Clear();
            if (holder) Object.Destroy(holder);
            holder = new GameObject("Shipwright hull templates");
            holder.SetActive(false);
            foreach (int hull in ShipTypes.VanillaHulls)
            {
                try
                {
                    var template = Capture(hull);
                    if (template != null) templates[hull] = template;
                }
                catch (Exception e) { Log.Error("Copies: capturing the template of hull " + hull + " failed: " + e); }
            }
            Log.Info("Copies: " + templates.Count + " of " + ShipTypes.VanillaHulls.Length + " hull templates captured.");
        }

        private static PurchasableBoat Original(int hull)
        {
            var registered = Ships.BoatOf(hull);
            if (registered) return registered;
            foreach (var saveable in Resources.FindObjectsOfTypeAll<SaveableObject>())
                if (saveable && saveable.sceneIndex == hull && saveable.gameObject.scene.IsValid() && saveable.GetComponent<PurchasableBoat>() && saveable.GetComponent<BoatRefs>())
                    return saveable.GetComponent<PurchasableBoat>();
            return null;
        }

        // A ship's deck (walk collider). The scene sets BoatRefs.walkCol on every vanilla hull but the Kakam (90), whose field
        // is empty; the game itself finds a deck through the ship's embark colliders (BoatEmbarkCollider.walkCollider), which
        // agree with BoatRefs wherever it is set. ShipyardExpansion fills the Kakam's field, so only without it was her
        // template missing (run 20261030-195201).
        internal static Transform DeckOf(GameObject boat)
        {
            if (!boat) return null;
            var refs = boat.GetComponent<BoatRefs>();
            var embarkDecks = new List<Transform>();
            foreach (var embark in boat.GetComponentsInChildren<BoatEmbarkCollider>(true))
                if (embark && embark.walkCollider) embarkDecks.Add(embark.walkCollider);
            return DeckRule(refs && refs.walkCol ? refs.walkCol : null, embarkDecks);
        }

        // BoatRefs' deck when set; else the one deck all the embark colliders name (none, or two different, is no deck).
        internal static T DeckRule<T>(T refsDeck, IEnumerable<T> embarkDecks) where T : class
        {
            if (refsDeck != null) return refsDeck;
            T found = null;
            foreach (var deck in embarkDecks)
            {
                if (deck == null) continue;
                if (found == null) found = deck;
                else if (!found.Equals(deck)) return null;
            }
            return found;
        }

        private static HullTemplate Capture(int hull)
        {
            var live = Original(hull);
            var refs = live ? live.GetComponent<BoatRefs>() : null;
            var liveDeck = live ? DeckOf(live.gameObject) : null;
            if (!refs || !liveDeck || !refs.boatModel) { Log.Warn("Copies: hull " + hull + " was not found complete; it can't be copied."); return null; }
            Transform deck = liveDeck, deckParent = deck.parent;
            Vector3 deckPosition = deck.localPosition, deckScale = deck.localScale;
            Quaternion deckRotation = deck.localRotation;
            var mooring = live.GetComponent<BoatMooringRopes>();
            Transform anchor = mooring && mooring.anchor ? mooring.anchor.transform : null;
            Transform anchorParent = anchor ? anchor.parent : null;
            GameObject root;
            // The deck and the anchor sit outside the ship; inside her while copying, Unity remaps the references to
            // them. The anchor goes under the hull model, where Anchor.Awake expects it (it moves itself three up).
            deck.SetParent(live.transform, true);
            if (anchor) anchor.SetParent(refs.boatModel, true);
            try { root = Object.Instantiate(live.gameObject, holder.transform, true); }
            finally
            {
                deck.SetParent(deckParent, false);
                deck.localPosition = deckPosition;
                deck.localRotation = deckRotation;
                deck.localScale = deckScale;
                if (anchor) anchor.SetParent(anchorParent, true);
            }
            root.SetActive(false);
            root.name = live.gameObject.name;
            var copyRefs = root.GetComponent<BoatRefs>();
            // The Kakam's empty field: the template's own deck (her embark colliders were remapped to it), so that each copy
            // gets hers and the code below and at spawning finds it.
            if (copyRefs && !copyRefs.walkCol)
            {
                copyRefs.walkCol = DeckOf(root);
                Log.Info("Copies: the " + root.name + " template's BoatRefs had no deck; given hers from her embark colliders (" + (copyRefs.walkCol ? copyRefs.walkCol.name : "none") + ").");
            }
            var copyMooring = root.GetComponent<BoatMooringRopes>();
            if (copyMooring) { copyMooring.mooringFront = null; copyMooring.mooringBack = null; }
            // GPButtonTrapdoor.Awake added a "trapdoor" box to the original's deck; the copy's Awake adds its own.
            if (copyRefs && copyRefs.walkCol)
                foreach (var box in copyRefs.walkCol.GetComponentsInChildren<BoxCollider>(true))
                    if (box && box.gameObject.name == "trapdoor" && box.GetComponents<Component>().Length == 2) Object.DestroyImmediate(box.gameObject);
            Compat.ShipMods.OnTemplate(root, live.gameObject);
            return new HullTemplate
            {
                Hull = hull, Root = root, Name = live.gameObject.name, Price = live.price, Length = Storage.LengthOf(live, hull),
                WalkColsRoot = deckParent, DeckRotation = deckRotation, DeckScale = deckScale,
            };
        }

        // ---- Spawning (§7.3) ----

        // Builds copy k at a place: from her template, with her own index block, purchased before she wakes (ScrambledSeas
        // moves only unpurchased ships), her deck on its own spot in the walk area, her pumps on her own hull, and the
        // game's lists and other mods told about her.
        internal static PurchasableBoat Spawn(CopyRecord copy, Vector3 position, Quaternion rotation, bool fresh = true)
        {
            var template = Template(copy.hull);
            if (template == null) { Log.Error("Copies: no template for hull " + copy.hull + "; copy " + copy.k + " can't be built."); return null; }
            int block = IndexOf(copy.k);
            GameObject go = null;
            try
            {
                go = Object.Instantiate(template.Root, Refs.shiftingWorld ? Refs.shiftingWorld : null, false);
                go.name = template.Name + "(Clone)";
                go.transform.position = position;
                go.transform.rotation = rotation;
                int spare = block + 5;
                foreach (var saveable in go.GetComponentsInChildren<SaveableObject>(true))
                {
                    int offset = saveable.sceneIndex - copy.hull;
                    saveable.sceneIndex = offset >= 0 && offset < BlockSize ? block + offset : spare < block + 9 ? spare++ : block + 8;
                }
                var root = go.GetComponent<SaveableObject>();
                root.sceneIndex = block;
                root.extraSetting = true;
                Compat.ShipMods.BeforeActivation(go, copy.hull);
                go.SetActive(true);
                var refs = go.GetComponent<BoatRefs>();
                if (refs && refs.walkCol) PlaceDeck(refs.walkCol, template, copy.k);
                var damage = go.GetComponent<BoatDamage>();
                if (refs && refs.walkCol && damage)
                    foreach (var pump in refs.walkCol.GetComponentsInChildren<BilgePump>(true)) pump.damage = damage;
                Register(go);
                Compat.ShipMods.AfterActivation(go, copy.hull);
                var boat = go.GetComponent<PurchasableBoat>();
                var purchase = GameAccess.Get<GameObject>(boat, "purchaseUI");
                if (purchase) purchase.SetActive(false);
                if (fresh) Storage.ResetJibs(boat);
                copy.spawned = true;
                Log.Info("Copies: built " + Ships.NameOf(block) + " (copy " + copy.k + ", hull " + copy.hull + ").");
                return boat;
            }
            catch (Exception e)
            {
                Log.Error("Copies: building copy " + copy.k + " failed: " + e);
                if (go) Object.Destroy(go);
                return null;
            }
        }

        // The deck goes at `walk cols` local (0, 0, 250 + 150k), further on while it would overlap another deck (the
        // Happy Bay boat's and the Dinghies cutter's keep their prefab places).
        private static void PlaceDeck(Transform deck, HullTemplate template, int k)
        {
            deck.SetParent(template.WalkColsRoot, false);
            deck.localRotation = template.DeckRotation;
            deck.localScale = template.DeckScale;
            float z = 250f + 150f * k;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                deck.localPosition = new Vector3(0f, 0f, z);
                if (!Overlaps(deck)) return;
                z += 150f;
            }
            Log.Warn("Copies: no free spot found for a deck; it may overlap another.");
        }

        private static bool Overlaps(Transform deck)
        {
            if (!deck.parent) return false;
            Bounds? mine = ColliderBounds(deck);
            if (mine == null) return false;
            foreach (Transform other in deck.parent)
            {
                if (other == deck) continue;
                Bounds? theirs = ColliderBounds(other);
                if (theirs != null && theirs.Value.Intersects(mine.Value)) return true;
            }
            return false;
        }

        private static Bounds? ColliderBounds(Transform t)
        {
            Bounds? bounds = null;
            foreach (var collider in t.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled) continue;
                if (bounds == null) bounds = collider.bounds;
                else { var b = bounds.Value; b.Encapsulate(collider.bounds); bounds = b; }
            }
            return bounds;
        }

        // The game's 10 km switch and the boat camera's splash sets list the ships by hand.
        private static void Register(GameObject go)
        {
            var switcher = Object.FindObjectOfType<BoatHorizonPerformanceSwitcher>();
            var horizon = FindHorizon(go);
            if (switcher && horizon && switcher.horizons != null && Array.IndexOf(switcher.horizons, horizon) < 0)
            {
                var horizons = new BoatHorizon[switcher.horizons.Length + 1];
                switcher.horizons.CopyTo(horizons, 0);
                horizons[horizons.Length - 1] = horizon;
                switcher.horizons = horizons;
            }
            try
            {
                var camera = BoatCamera.instance;
                var field = AccessTools.Field(typeof(BoatCamera), "splashAnimSets");
                var splash = go.transform.Find("anim splash set");
                if (camera && field != null && splash && field.GetValue(camera) is Transform[] sets && Array.IndexOf(sets, splash) < 0)
                {
                    var grown = new Transform[sets.Length + 1];
                    sets.CopyTo(grown, 0);
                    grown[grown.Length - 1] = splash;
                    field.SetValue(camera, grown);
                }
            }
            catch (Exception e) { Log.Warn("Copies: the splash set was not registered: " + e.Message); }
        }

        private static BoatHorizon FindHorizon(GameObject go)
        {
            foreach (var horizon in go.GetComponentsInChildren<BoatHorizon>(true))
                if (horizon.transform.parent == go.transform) return horizon;
            return null;
        }

        // A new copy record: the next k, never reused. The number is given at completion (decision 50).
        internal static CopyRecord NewRecord(int hull)
        {
            int k = State.Save.nextCopy;
            if (k > MaxCopies) return null;
            State.Save.nextCopy = k + 1;
            var copy = new CopyRecord { k = k, hull = hull };
            State.Save.copies.Add(copy);
            return copy;
        }

        // ---- Keeping copies out of the vanilla save (§7.4) ----

        private static readonly FieldInfo CurrentObjects = AccessTools.Field(typeof(SaveLoadManager), "currentObjects");
        private static readonly FieldInfo CurrentPrefabs = AccessTools.Field(typeof(SaveLoadManager), "currentPrefabs");
        internal static SaveableObject[] RealObjects;
        internal static List<SaveablePrefab> RealPrefabs;

        // The copy block a parent index lies in, or 0: every index from 1000 on is kept out of the vanilla save.
        internal static int BlockOfParent(int parent) => parent >= FirstIndex ? FirstIndex + (parent - FirstIndex) / BlockSize * BlockSize : 0;

        // Which copy each item goes with (her block; 0 for the vanilla save), by instanceId: by its parent, or, for an item
        // inside a crate, by its crate's (CrateRule). Every ship's cache counts too, so a crate packed with a ship still
        // takes the items left out of the cache.
        private static Dictionary<int, int> ItemOwners(SaveableObject[] objects, List<SaveablePrefab> prefabs)
        {
            var items = new List<CrateRule.Item>(prefabs.Count);
            foreach (var prefab in prefabs)
                if (prefab) items.Add(new CrateRule.Item(prefab.instanceId, BlockOfParent(prefab.GetParentObject()), prefab.currentCrateId));
            for (int i = 0; i < objects.Length; i++)
            {
                var local = objects[i] ? objects[i].localItems : null;
                if (!local || !local.HasLocalItems()) continue;
                foreach (var data in local.GetCachedItems())
                    if (data != null) items.Add(new CrateRule.Item(data.instanceId, BlockOfParent(i), data.crateId));
            }
            return CrateRule.Owners(items);
        }

        private static int OwnerOf(Dictionary<int, int> owners, SaveablePrefab prefab) =>
            owners.TryGetValue(prefab.instanceId, out int owner) ? owner : BlockOfParent(prefab.GetParentObject());

        // Each step of the save swaps in lists without the copies, their ropes and anchors, and the items aboard them or in
        // their crates; the finalizer puts the real ones back.
        internal static object[] HideFromSave()
        {
            var manager = SaveLoadManager.instance;
            if (!manager || State.Save.copies.Count == 0) return null;
            var objects = (SaveableObject[])CurrentObjects.GetValue(manager);
            var prefabs = (List<SaveablePrefab>)CurrentPrefabs.GetValue(manager);
            var filteredObjects = (SaveableObject[])objects.Clone();
            for (int i = FirstIndex; i < filteredObjects.Length; i++) filteredObjects[i] = null;
            var owners = ItemOwners(objects, prefabs);
            LeaveForTheirCrates(prefabs, owners);
            var filteredPrefabs = new List<SaveablePrefab>(prefabs.Count);
            foreach (var prefab in prefabs)
                if (!prefab || OwnerOf(owners, prefab) == 0) filteredPrefabs.Add(prefab);
            RealObjects = objects;
            RealPrefabs = prefabs;
            CurrentObjects.SetValue(manager, filteredObjects);
            CurrentPrefabs.SetValue(manager, filteredPrefabs);
            return new object[] { objects, prefabs };
        }

        // Code review R1 of 0.1.4: an item put into a crate aboard a copy keeps her as its parent while it is in the crate (the
        // game's ExitBoat skips an attached item). With the crate carried off her (without NANDFixes' cascade, or before it), the
        // item goes to the vanilla save with its crate (CrateRule), but the vanilla save holds none of her objects, so the game's
        // PrepareSaveData found no ship at its parent and threw: that save wrote nothing, and every later one in the session only
        // logged "not ready to save" (SaveLoadManager.busy stayed set). Such an item leaves her for good first, as at a store
        // (GameAccess.LeaveShip), to its crate's ship or house, or the world (CrateRule.LeavingParent).
        private static void LeaveForTheirCrates(List<SaveablePrefab> prefabs, Dictionary<int, int> owners)
        {
            var leaving = new List<KeyValuePair<SaveablePrefab, int>>();
            Dictionary<int, SaveablePrefab> byId = null;
            foreach (var prefab in prefabs)
            {
                if (!prefab || prefab.GetParentObject() < FirstIndex || OwnerOf(owners, prefab) != 0) continue;
                if (byId == null)
                {
                    byId = new Dictionary<int, SaveablePrefab>();
                    foreach (var other in prefabs) if (other) byId[other.instanceId] = other;
                }
                SaveablePrefab crate = null;
                bool inWorld = prefab.currentCrateId != 0 && prefab.currentCrateId != prefab.instanceId && byId.TryGetValue(prefab.currentCrateId, out crate) && crate;
                int? parent = CrateRule.LeavingParent(prefab.GetParentObject(), 0, inWorld, inWorld ? crate.GetParentObject() : 0);
                if (parent.HasValue) leaving.Add(new KeyValuePair<SaveablePrefab, int>(prefab, parent.Value));
            }
            if (leaving.Count == 0) return;
            var names = new List<string>();
            foreach (var pair in leaving)
            {
                names.Add(pair.Key.name + " (off " + Ships.NameOf(BlockOfParent(pair.Key.GetParentObject())) + ", now " + (pair.Value > 0 ? "aboard " + pair.Value : "in the world") + ")");
                GameAccess.LeaveShip(pair.Key, pair.Value);
            }
            Log.Info("Copies: " + leaving.Count + " item(s) in crates off a copy left her before the save, as at a store: " + string.Join(", ", names.ToArray()) + ".");
        }

        internal static void RestoreAfterSave(object[] state)
        {
            if (state == null) return;
            var manager = SaveLoadManager.instance;
            if (manager)
            {
                CurrentObjects.SetValue(manager, state[0]);
                CurrentPrefabs.SetValue(manager, state[1]);
            }
            RealObjects = null;
            RealPrefabs = null;
        }

        // Inside SaveModData: each built copy's objects and items, as DoSaveGame would have saved them.
        internal static void CaptureForSave()
        {
            var objects = RealObjects ?? (SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentObjects() : null);
            var prefabs = RealPrefabs ?? (SaveLoadManager.instance ? SaveLoadManager.instance.GetCurrentPrefabs() : null);
            if (objects == null || prefabs == null) return;
            Vector3 offset = FloatingOriginManager.instance ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
            // PrepareSaveData finds an item's ship with GetObject, which reads the game's object list; while the copies are
            // hidden from the vanilla save it finds none of them (0.1.2 threw here and saved no copy's items), so the real
            // list is put back for these calls.
            var manager = SaveLoadManager.instance;
            object hidden = RealObjects != null && manager ? CurrentObjects.GetValue(manager) : null;
            if (hidden != null) CurrentObjects.SetValue(manager, RealObjects);
            try
            {
                var owners = ItemOwners(objects, prefabs);
                foreach (var copy in State.Save.copies)
                {
                    if (!copy.spawned) continue;
                    int block = IndexOf(copy.k);
                    if (block + BlockSize > objects.Length || !objects[block]) continue;
                    try
                    {
                        var data = new List<SaveObjectData>();
                        for (int i = block; i < block + BlockSize; i++)
                            if (objects[i]) data.Add(ObjectData(objects[i], offset));
                        var items = new List<SavePrefabData>();
                        int inCrates = 0;
                        foreach (var prefab in prefabs)
                        {
                            if (!prefab || OwnerOf(owners, prefab) != block) continue;
                            items.Add(prefab.PrepareSaveData());
                            if (BlockOfParent(prefab.GetParentObject()) != block) inCrates++;
                        }
                        if (inCrates > 0) Log.Debug("Copies: copy " + copy.k + " keeps " + inCrates + " items in her crates that aren't counted aboard her.");
                        var local = objects[block].localItems;
                        if (local && local.HasLocalItems()) items.AddRange(local.GetCachedItems());
                        copy.objects = Encode(data.ToArray());
                        copy.items = Encode(items.ToArray());
                    }
                    catch (Exception e) { Log.Error("Copies: saving copy " + copy.k + " failed (her last saved state stays): " + e); }
                }
            }
            finally { if (hidden != null) CurrentObjects.SetValue(manager, hidden); }
        }

        // As DoSaveGame builds it (SaveLoadManager), with the dirt texture always as PNG.
        private static SaveObjectData ObjectData(SaveableObject saveable, Vector3 offset)
        {
            byte[] texture = saveable.extraTexture ? saveable.extraTexture.EncodeToPNG() : null;
            SaveBoatCustomizationData customization = saveable.customization ? saveable.customization.GetData() : null;
            Quaternion sinkRotation = default(Quaternion);
            if (saveable.damage)
            {
                saveable.extraValue = saveable.damage.waterLevel;
                saveable.extraValue1 = saveable.damage.hullDamage;
                saveable.extraValue2 = saveable.damage.oakum;
                sinkRotation = saveable.damage.GetSinkRotation();
            }
            if (saveable.anchor)
            {
                saveable.extraSetting = saveable.anchor.IsSet();
                saveable.extraValue = saveable.anchor.GetRopeLength();
            }
            return new SaveObjectData(saveable.transform.position - offset, saveable.transform.rotation, sinkRotation, saveable.sceneIndex,
                saveable.extraSetting, saveable.extraValue, saveable.extraValue1, saveable.extraValue2, texture, customization);
        }

        private static string Encode(object value)
        {
            using (var stream = new MemoryStream())
            {
                new BinaryFormatter().Serialize(stream, value);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        private static T Decode<T>(string text) where T : class
        {
            if (string.IsNullOrEmpty(text)) return null;
            using (var stream = new MemoryStream(Convert.FromBase64String(text)))
                return new BinaryFormatter().Deserialize(stream) as T;
        }

        // ---- Restoring after a load ----

        // In the LoadModData postfix: each copy built before is built again where she was saved; a frame later, once her own
        // Start has run, she is fed her objects' data through SaveableObject.Load and her items into her cargo cache, as
        // LoadGame does for any ship. The game loads a ship only after her Start, and Load needs what Start sets up: her
        // mooring ropes (BoatMooringRopes), her hull's dirt material (CleanableObject) and, for a sunk ship, her hull
        // (BoatDamage). 0.1.3's first in-game load threw on the ropes, so her cargo and a plate hung on her were lost.
        internal static void RestoreAll()
        {
            foreach (var copy in State.Save.copies)
            {
                if (!copy.spawned) continue;
                copy.spawned = false;
                try
                {
                    var data = Decode<SaveObjectData[]>(copy.objects);
                    var items = Decode<SavePrefabData[]>(copy.items);
                    int block = IndexOf(copy.k);
                    SaveObjectData main = null;
                    if (data != null) foreach (var d in data) if (d.sceneIndex == block) main = d;
                    if (main == null) { Log.Warn("Copies: copy " + copy.k + " has no saved state; she waits as a record."); copy.spawned = true; continue; }
                    Vector3 offset = FloatingOriginManager.instance ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
                    var boat = Spawn(copy, (Vector3)main.position + offset, main.rotation, false);
                    if (!boat) { copy.spawned = true; continue; }
                    Plugin.Instance.StartCoroutine(LoadAfterStart(copy, boat, data, items));
                }
                catch (Exception e)
                {
                    copy.spawned = true;
                    Log.Error("Copies: restoring copy " + copy.k + " failed (her saved state is kept): " + e);
                }
            }
        }

        // The next frame, after her Start (the loading screen is still up: LoadGame's caller waits 1.5 s). Each object loads
        // on its own, and her items go into her cache whatever failed, so the next save still holds them.
        private static IEnumerator LoadAfterStart(CopyRecord copy, PurchasableBoat boat, SaveObjectData[] data, SavePrefabData[] items)
        {
            yield return null;
            if (!boat) { Log.Error("Copies: copy " + copy.k + " was gone before her state was loaded."); yield break; }
            foreach (var d in data)
            {
                var target = SaveLoadManager.instance.GetObject(d.sceneIndex);
                if (!target) continue;
                try { target.Load(d); }
                catch (Exception e) { Log.Error("Copies: loading " + target.name + " (" + d.sceneIndex + ") of copy " + copy.k + " failed: " + e); }
            }
            var local = boat.GetComponent<BoatLocalItems>();
            if (items != null && local)
                foreach (var item in items) local.AddCachedItemFromSaveData(item);
            // Her saved state made her the player's again (SaveableObject.Load); a copy EconomyOverhaul seized is handed back
            // now, as it hands back its seized ships in its own LoadModData postfix, a frame before (code review R3).
            Compat.EconomyOverhaul.HandBackIfSeized(boat);
        }
    }

    // Where an item is saved: an item inside a crate goes where its crate goes, whatever its own parent; any other item with
    // its parent. An item can lie in a crate aboard a ship without counting as aboard her: a crate unsealed aboard puts its
    // items in from 100 m over it, so they keep parent -1. Saved apart from a copy's crate, such an item looked for its crate
    // once, two frames after the load, while the crate was still in her cache, and stayed loose (code review R2).
    internal static class CrateRule
    {
        internal struct Item
        {
            internal int Id, Owner, Crate;

            internal Item(int id, int owner, int crate) { Id = id; Owner = owner; Crate = crate; }
        }

        // Each item's owner by instanceId. An item's Owner is what its own parent says (a copy's block, or 0); a crate is
        // never inside another, so a crate keeps its own.
        internal static Dictionary<int, int> Owners(IList<Item> items)
        {
            var byParent = new Dictionary<int, int>();
            foreach (var item in items) byParent[item.Id] = item.Owner;
            var owners = new Dictionary<int, int>();
            foreach (var item in items)
                owners[item.Id] = item.Crate != 0 && item.Crate != item.Id && byParent.TryGetValue(item.Crate, out int crate) ? crate : item.Owner;
            return owners;
        }

        // An item in a crate: its crate id names another item.
        internal static bool InCrate(Item item, ICollection<int> crates) => item.Crate != 0 && item.Crate != item.Id && crates.Contains(item.Crate);

        // Of the crates `hers` (by instanceId), those some item lies in (Storage.FilledCrates); the crates, not their items.
        internal static HashSet<int> FilledCrates(ICollection<int> hers, IEnumerable<Item> items)
        {
            var filled = new HashSet<int>();
            foreach (var item in items) if (InCrate(item, hers)) filled.Add(item.Crate);
            return filled;
        }

        // Code review R1: an item going to the vanilla save (owner 0) whose own parent is a copy's (1000 and up, none of which
        // the vanilla save holds) lies in a crate off her. It leaves her: aboard its crate's ship or house when the vanilla save
        // holds that (1-999), else in the world (-1). Null for every other item: it keeps its parent.
        internal static int? LeavingParent(int parent, int owner, bool crateInWorld, int crateParent) =>
            owner != 0 || parent < CopyEngine.FirstIndex ? (int?)null
            : crateInWorld && crateParent > 0 && crateParent < CopyEngine.FirstIndex ? crateParent : -1;
    }

    // A copy's save index goes up to 2039, so the registry grows from 255 to 2048 (§7.2).
    [HarmonyPatch(typeof(SaveLoadManager), "Awake")]
    internal static class RegistrySizePatch
    {
        private static readonly FieldInfo Objects = AccessTools.Field(typeof(SaveLoadManager), "currentObjects");

        private static void Postfix(SaveLoadManager __instance)
        {
            var objects = (SaveableObject[])Objects.GetValue(__instance);
            if (objects == null || objects.Length >= CopyEngine.RegistrySize) return;
            var grown = new SaveableObject[CopyEngine.RegistrySize];
            objects.CopyTo(grown, 0);
            Objects.SetValue(__instance, grown);
        }
    }

    // Prefix and finalizer on each step of the DoSaveGame iterator (§7.4).
    [HarmonyPatch]
    internal static class SaveFilterPatch
    {
        private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SaveLoadManager), "DoSaveGame"));

        private static void Prefix(out object[] __state)
        {
            __state = null;
            try { __state = CopyEngine.HideFromSave(); }
            catch (Exception e) { Log.Error("Copies: hiding copies from the save failed: " + e); }
        }

        private static Exception Finalizer(Exception __exception, object[] __state)
        {
            try { CopyEngine.RestoreAfterSave(__state); }
            catch (Exception e) { Log.Error("Copies: restoring the registry after the save failed: " + e); }
            return __exception;
        }
    }
}

namespace ShipwrightAndTransport.Compat
{
    // ==== ShipMods ====

    // Ship mods a copy must be introduced to (§7.5), by reflection. Each step is skipped when its mod is missing.
    //   Dinghies: adds davits to the four originals only, after the templates are taken; copies get theirs here.
    //   Shipyard Expansion: lists ships in Plugin.moddedBoats and stock options in Plugin.stockParts (its save cleaner).
    //   Unstayed Junk Sail Mast: migrates sail indices per save index; a copy has nothing to migrate, so nothing to do.
    //   BetterDrag: looks ships up by name without "(Clone)"; copies keep "<vanilla name>(Clone)".
    //   ScrambledSeas: moves only unpurchased ships; copies are purchased before they wake.
    //   TowableBoats: a template loses the TowingSet her original got, so that a copy gets one of her own (OnTemplate).
    //   NANDFixes: the Sanbuq template loses what NANDFixes removes from her clipping mask a moment later (OnTemplate).
    //   MoreSailwindSails: a template loses the Fisherman's Stays her original got, so that a copy registers her own (OnTemplate).
    internal static class ShipMods
    {
        private static readonly Dictionary<int, string> DavitHulls = new Dictionary<int, string>
        {
            { 50, "brig" }, { 20, "sanbuq" }, { 80, "junk" }, { 70, "jong" },
        };

        // On each hull template, inactive, before any copy is made from it; original is the ship it was cloned from.
        internal static void OnTemplate(GameObject template, GameObject original)
        {
            StripTowingSets(template);
            StripClipMaskTrigger(template);
            StripFishermansStays(template, original);
        }

        internal static void BeforeActivation(GameObject copy, int hull)
        {
            if (DavitHulls.TryGetValue(hull, out string name)) AddDavits(copy, name);
        }

        internal static void AfterActivation(GameObject copy, int hull)
        {
            RegisterWithShipyardExpansion(copy, hull);
        }

        private static void AddDavits(GameObject copy, string name)
        {
            var type = AccessTools.TypeByName("Dinghies.DinghiesPatches");
            if (type == null) return;
            try
            {
                var method = AccessTools.Method(type, "AddDavitsOptions", new[] { typeof(GameObject), typeof(GameObject), typeof(string) });
                var assets = AccessTools.Field(type, name + "Assets")?.GetValue(null) as GameObject;
                if (method == null || !assets) { Log.Warn("Dinghies: davits for copies are not available."); return; }
                method.Invoke(null, new object[] { copy, assets, name });
            }
            catch (Exception e) { Log.Warn("Dinghies: adding davits to a copy failed: " + e.Message); }
        }

        // TowableBoats 0.2.7: MooringSet.Awake adds a TowingSet to her ship, and its Awake swaps each cleat button of her
        // "towing set" for a TowingCleat, destroying the old one. A template cloned from a ship that has a TowingSet would give
        // a copy two: the second's Awake destroys the first's new cleats, and the first, the one TowableBoats looks up, throws
        // on them in UnmoorAllRopes, so Shipyard.AdmitShip stops halfway and Exit shipyard loads the previous ship's rig into
        // her. The template keeps her "towing set" with one cleat button each and loses the TowingSet; the copy's own
        // MooringSet.Awake then gives her one, with cleats of her own. (Her save index is a copy's, so TowingSet.AddCleats
        // couldn't pick her cleats by hull: they come with her "towing set".)
        private static void StripTowingSets(GameObject template)
        {
            var setType = AccessTools.TypeByName("TowableBoats.TowingSet");
            if (setType == null) return;
            try
            {
                var cleatType = AccessTools.TypeByName("TowableBoats.TowingCleat");
                foreach (var set in template.GetComponentsInChildren(setType, true))
                {
                    var towing = set.transform.Find("towing set");
                    UnityEngine.Object.DestroyImmediate(set);
                    if (!towing) continue;
                    foreach (var part in towing.GetComponentsInChildren<Transform>(true))
                    {
                        var buttons = part.GetComponents<GPButtonDockMooring>();
                        if (buttons.Length < 2) continue;
                        var keep = Array.Find(buttons, b => cleatType != null && cleatType.IsInstanceOfType(b)) ?? buttons[0];
                        foreach (var button in buttons) if (button != keep) UnityEngine.Object.DestroyImmediate(button);
                    }
                }
            }
            catch (Exception e) { Log.Warn("TowableBoats: preparing a hull template failed; copies of it may not be admitted at a shipyard: " + e.Message); }
        }

        // NANDFixes 1.4.4, ClippingMaskPatch (a SaveableObject.Awake postfix for the Sanbuq, save index 20): it clones her
        // "interior_trigger" as a clipping mask, "newMask", and Destroys the clone's InteriorEffectsTrigger and collider. Unity
        // destroys them at the end of the frame, after the templates were taken in that frame's sceneLoaded: the Sanbuq template
        // kept both, so a copy had a second interior trigger, and leaving either set GameState.indoors off while the player was
        // still inside (code review R5; a disabled script still gets trigger calls). Taking the templates later can't help: by
        // the end of that frame the ships' Start has run (the in-game probe, run 20261006-074313), and decision 28 wants the
        // templates before it. The copy's own Awake doesn't clone again (her save index isn't 20), so the template loses what
        // NANDFixes destroys on the original: the trigger and the first collider of each such mask.
        private static void StripClipMaskTrigger(GameObject template)
        {
            int stripped = 0;
            try
            {
                foreach (var part in template.GetComponentsInChildren<Transform>(true))
                {
                    var trigger = part.name == "newMask" ? part.GetComponent<InteriorEffectsTrigger>() : null;
                    if (!trigger || !HasComponent(part, "RegisterClipSurfaceInput")) continue;
                    UnityEngine.Object.DestroyImmediate(trigger);
                    var collider = part.GetComponent<Collider>();
                    if (collider) UnityEngine.Object.DestroyImmediate(collider);
                    stripped++;
                }
            }
            catch (Exception e) { Log.Warn("NANDFixes: preparing the " + template.name + " template failed; her copies may have a second interior trigger: " + e.Message); }
            if (stripped > 0) Log.Info("NANDFixes: the " + template.name + " template's clipping mask lost the interior trigger NANDFixes removes from her own (" + stripped + ").");
        }

        // MoreSailwindSails (FishermansStayRegistry.Register, a SaveableBoatCustomization.Awake postfix): it adds "Fisherman's
        // Stay" parts to her BoatCustomParts.availableParts, a serialized list, so a template cloned from her keeps them, with
        // objects of its own straight under her root and her deck (a Mast mount holding its winches and visuals, a walk object,
        // an empty option and its walk object), the mounts in BoatRefs.masts, and a FishermansStayRegistry and WinchControls
        // whose lists are not serialized. A copy's own Awake registered her stays a second time beside the cloned ones, which
        // no list of MoreSailwindSails knew (its second "Registered Fisherman's Stays" line, for the user's Instant Cog, 0.1.0's
        // test, 2026-10-28). The template loses what Register added (found from the original's registry), so the copy's Awake
        // registers her stays once, fully wired.
        private static void StripFishermansStays(GameObject template, GameObject original)
        {
            var registryType = AccessTools.TypeByName("MoreSailwindSails.Stays.FishermansStay.FishermansStayRegistry");
            var registry = registryType != null && original ? original.GetComponent(registryType) : null;
            if (!registry) return;
            int parts = 0, objects = 0, missed = 0;
            try
            {
                var stayType = AccessTools.TypeByName("MoreSailwindSails.Stays.FishermansStay.FishermansStay");
                var stays = AccessTools.Field(registryType, "Stays")?.GetValue(registry) as IEnumerable;
                var registered = AccessTools.Field(registryType, "Parts")?.GetValue(registry) as IEnumerable;
                var auxiliary = AccessTools.Field(registryType, "AuxiliaryObjects")?.GetValue(registry) as IEnumerable<GameObject>;
                var mountField = stayType != null ? AccessTools.Field(stayType, "Mount") : null;
                var walkField = stayType != null ? AccessTools.Field(stayType, "WalkObject") : null;
                BoatRefs originalRefs = original.GetComponent<BoatRefs>(), templateRefs = template.GetComponent<BoatRefs>();
                BoatCustomParts originalParts = original.GetComponent<BoatCustomParts>(), templateParts = template.GetComponent<BoatCustomParts>();
                if (stays == null || registered == null || auxiliary == null || mountField == null || walkField == null
                    || !originalRefs || !templateRefs || !originalParts || !templateParts)
                {
                    Log.Warn("MoreSailwindSails changed; copies of " + template.name + " may not be admitted at a shipyard.");
                    return;
                }
                var indices = new List<int>();
                foreach (BoatPart part in registered) indices.Add(originalParts.availableParts.IndexOf(part));
                parts = RemoveAtIndices(templateParts.availableParts, indices);
                var added = new List<GameObject>(auxiliary);
                foreach (var stay in stays)
                {
                    if (mountField.GetValue(stay) is Mast mount && mount) added.Add(mount.gameObject);
                    if (walkField.GetValue(stay) is GameObject walk) added.Add(walk);
                }
                foreach (var go in added)
                {
                    if (!go) continue;
                    var counterpart = Counterpart(go.transform, original.transform, template.transform, CopyEngine.DeckOf(original), templateRefs.walkCol);
                    if (!counterpart) { missed++; continue; }
                    var mast = counterpart.GetComponent<Mast>();
                    if (mast && templateRefs.masts != null)
                        for (int i = 0; i < templateRefs.masts.Length; i++) if (templateRefs.masts[i] == mast) templateRefs.masts[i] = null;
                    UnityEngine.Object.DestroyImmediate(counterpart.gameObject);
                    objects++;
                }
                UnityEngine.Object.DestroyImmediate(template.GetComponent(registryType));
                var winchType = AccessTools.TypeByName("MoreSailwindSails.Controls.WinchControls");
                var winches = winchType != null ? template.GetComponent(winchType) : null;
                if (winches) UnityEngine.Object.DestroyImmediate(winches);
            }
            catch (Exception e) { Log.Warn("MoreSailwindSails: preparing the " + template.name + " template failed; her copies may not be admitted at a shipyard: " + e); return; }
            Log.Info("MoreSailwindSails: the " + template.name + " template lost the Fisherman's Stays her original got (" + parts + " parts, "
                + objects + " objects" + (missed > 0 ? ", " + missed + " not found" : "") + "); her copies register their own.");
        }

        // The template's object made from an original's object straight under her root or her deck: the same name, and the
        // same place among her siblings of that name (the template's deck lost its "trapdoor" box, so places by index differ).
        private static Transform Counterpart(Transform item, Transform originalRoot, Transform templateRoot, Transform originalDeck, Transform templateDeck)
        {
            Transform from = item.parent, to = from == originalRoot ? templateRoot : from && from == originalDeck ? templateDeck : null;
            if (!to) return null;
            int i = NthWithName(Names(to), item.name, SameNameRank(Names(from), item.GetSiblingIndex()));
            return i >= 0 ? to.GetChild(i) : null;
        }

        private static List<string> Names(Transform parent)
        {
            var names = new List<string>(parent.childCount);
            foreach (Transform child in parent) names.Add(child.name);
            return names;
        }

        // How many siblings before `index` have its name.
        internal static int SameNameRank(IList<string> siblings, int index)
        {
            int rank = 0;
            for (int i = 0; i < index; i++) if (siblings[i] == siblings[index]) rank++;
            return rank;
        }

        // The index of the sibling named `name` that has `rank` of that name before it, or -1.
        internal static int NthWithName(IList<string> siblings, string name, int rank)
        {
            for (int i = 0; i < siblings.Count; i++) if (siblings[i] == name && rank-- == 0) return i;
            return -1;
        }

        // Removes the entries at these indices (each once; out of range or -1 skipped), the others keeping their order.
        // Returns how many went.
        internal static int RemoveAtIndices<T>(List<T> list, IEnumerable<int> indices)
        {
            var sorted = new SortedSet<int>(indices);
            int removed = 0;
            foreach (int i in sorted.Reverse())
                if (i >= 0 && i < list.Count) { list.RemoveAt(i); removed++; }
            return removed;
        }

        // A component found by its type's name (Crest's, from a DLL Shipwright doesn't reference).
        private static bool HasComponent(Component on, string typeName)
        {
            foreach (var component in on.GetComponents<Component>())
                if (component && component.GetType().Name == typeName) return true;
            return false;
        }

        private static void RegisterWithShipyardExpansion(GameObject copy, int hull)
        {
            var type = AccessTools.TypeByName("ShipyardExpansion.Plugin");
            if (type == null) return;
            try
            {
                var original = Ships.OriginalBoat(hull);
                var originalParts = original ? original.GetComponent<BoatCustomParts>() : null;
                var copyParts = copy.GetComponent<BoatCustomParts>();
                if (!originalParts || !copyParts) return;
                if (AccessTools.Field(type, "moddedBoats")?.GetValue(null) is List<BoatCustomParts> modded && modded.Contains(originalParts) && !modded.Contains(copyParts))
                    modded.Add(copyParts);
                var stock = AccessTools.Field(type, "stockParts")?.GetValue(null) as Dictionary<BoatRefs, Dictionary<BoatPart, int>>;
                var originalRefs = original.GetComponent<BoatRefs>();
                var copyRefs = copy.GetComponent<BoatRefs>();
                if (stock == null || !originalRefs || !copyRefs || !stock.TryGetValue(originalRefs, out var originalStock) || stock.ContainsKey(copyRefs)) return;
                var copyStock = new Dictionary<BoatPart, int>();
                foreach (var pair in originalStock)
                {
                    int i = originalParts.availableParts.IndexOf(pair.Key);
                    if (i >= 0 && i < copyParts.availableParts.Count) copyStock[copyParts.availableParts[i]] = pair.Value;
                }
                stock[copyRefs] = copyStock;
            }
            catch (Exception e) { Log.Warn("Shipyard Expansion: registering a copy failed: " + e.Message); }
        }
    }

    // VirtualCrew 0.2.2 keeps each ship's setup under a "vessel key", the hull model's name without "(Clone)", so a copy
    // would share her original's setup. A transpiler on its four key sites appends " #n" (the copy's number within her
    // type, decision 50) for copies; renaming never changes the key, so the setup is never lost (draft, "VirtualCrew
    // patch"). All or nothing: if any site doesn't match exactly once, nothing is patched.
    public static class VirtualCrew
    {
        private const string Guid = "com.zorkinian.virtualcrew";

        private static readonly string[][] KeySites =
        {
            new[] { "SailwindVirtualCrew.CrewBoatContextResolver", "GetActiveVesselKey" },
            new[] { "SailwindVirtualCrew.Plugin", "Update" },
            new[] { "SailwindVirtualCrew.Plugin", "RequestEmbarkedVesselScan" },
            new[] { "SailwindVirtualCrew.CargoAreaPainter", "GetArea" },
        };

        private static readonly MethodInfo ReplaceMethod = AccessTools.Method(typeof(string), "Replace", new[] { typeof(string), typeof(string) });
        private static readonly MethodInfo TrimMethod = AccessTools.Method(typeof(string), "Trim", Type.EmptyTypes);
        private static Func<Transform> activeWorldBoat;

        internal static void Apply(Harmony harmony)
        {
            if (!Mods.Installed(Guid)) return;
            try
            {
                var getActive = AccessTools.Method(AccessTools.TypeByName("SailwindVirtualCrew.CrewBoatContextResolver"), "GetActiveWorldBoat");
                var targets = new List<MethodBase>();
                foreach (string[] site in KeySites)
                {
                    var type = AccessTools.TypeByName(site[0]);
                    var method = type != null ? AccessTools.Method(type, site[1]) : null;
                    if (getActive == null || method == null || CountKeySites(PatchProcessor.GetOriginalInstructions(method)) != 1)
                    {
                        Log.Warn("VirtualCrew changed at " + site[0] + "." + site[1] + "; ship copies share their original's VirtualCrew setup.");
                        return;
                    }
                    targets.Add(method);
                }
                activeWorldBoat = AccessTools.MethodDelegate<Func<Transform>>(getActive);
                var transpiler = new HarmonyMethod(typeof(VirtualCrew), nameof(Transpiler));
                foreach (var method in targets) harmony.Patch(method, transpiler: transpiler);
                Log.Info("VirtualCrew found: each ship copy gets its own crew setup.");
            }
            catch (Exception e) { Log.Warn("VirtualCrew: the copy patch failed: " + e.Message); }
        }

        // `ldstr "(Clone)"`, `ldstr ""`, `call string.Replace(string, string)`, `call string.Trim()`
        private static bool IsKeyTrim(IList<CodeInstruction> code, int i) =>
            i >= 3 && code[i].Calls(TrimMethod) && code[i - 1].Calls(ReplaceMethod)
            && code[i - 3].opcode == System.Reflection.Emit.OpCodes.Ldstr && (string)code[i - 3].operand == "(Clone)";

        private static int CountKeySites(IList<CodeInstruction> code)
        {
            int count = 0;
            for (int i = 0; i < code.Count; i++) if (IsKeyTrim(code, i)) count++;
            return count;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            for (int i = 0; i < code.Count; i++)
                if (IsKeyTrim(code, i))
                    code.Insert(++i, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(VirtualCrew), nameof(KeyFor))));
            return code;
        }

        // Original ships keep VirtualCrew's key; a copy gets "<key> #n".
        public static string KeyFor(string key)
        {
            if (string.IsNullOrEmpty(key) || activeWorldBoat == null) return key;
            try
            {
                var boat = activeWorldBoat();
                var saveable = boat ? boat.GetComponentInParent<SaveableObject>() : null;
                var copy = saveable ? CopyEngine.RecordOfIndex(saveable.sceneIndex) : null;
                return copy == null ? key : key + " #" + copy.number;
            }
            catch { return key; }
        }
    }
}
