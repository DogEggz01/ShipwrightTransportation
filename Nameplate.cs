using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ShipwrightAndTransport
{
    // ==== Plates ====

    // On every plate prefab (added when the bundle loads): the plate's record, and its carved face. Every plate that
    // exists, hanging, in a crate or in the inventory, is in Live, so the faces it shows are kept.
    internal sealed class PlateItem : MonoBehaviour
    {
        internal static readonly HashSet<PlateItem> Live = new HashSet<PlateItem>();
        internal PlateRecord Record;
        // The face this plate shows, which PlateFaces keeps while the plate exists.
        internal string FaceKey;

        // Start comes after LoadGame has run LoadModData, so a loaded plate finds its record by its instanceId; a bought
        // plate has it already. The style is the prefab's.
        private void Start()
        {
            Live.Add(this);
            var saveable = GetComponent<SaveablePrefab>();
            if (Record == null && saveable) Record = Plates.RecordOf(saveable.instanceId);
            if (Record == null)
            {
                Log.Warn("Plates: a name plate (instance " + (saveable ? saveable.instanceId : 0) + ") has no record; it shows no name.");
                return;
            }
            var style = (saveable ? PlateData.StyleOfPrefab(saveable.prefabIndex) : null) ?? PlateData.Style(Record.style);
            var renderer = GetComponent<MeshRenderer>();
            if (!renderer) return;
            var materials = renderer.sharedMaterials;
            if (style.FaceSlot >= materials.Length) return;
            renderer.sharedMaterials = PlateFaces.Dress(style, materials, Record.font, Record.size, Record.name);
            FaceKey = PlateFaces.Key(style.Key, Record.font, Record.size, Record.name);
            PlateScaling.Apply(this, PlateData.CleanScale(Record.scale));
        }

        private void OnDestroy() => Live.Remove(this);
    }

    // A plate's size (0.1.6, B5). The game sets every loose item's transform scale back to 1 each frame
    // (ItemRigidbody.LateUpdate), so the plate is made bigger in its parts instead: a scaled copy of its mesh (shared per
    // style and size; the carved face's UVs unchanged), its box collider, which the game copies onto the item's physics body
    // three physics steps after it wakes (ItemRigidbody.AddCollider: so this runs in Start, before that), its sell value
    // (half its price, as at size 1), and its hold distance; its inventory scale shrinks by as much, so it looks as before
    // in the inventory.
    internal static class PlateScaling
    {
        private static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();

        internal static void Apply(PlateItem plate, float scale)
        {
            if (!plate || Mathf.Approximately(scale, 1f)) return;
            try
            {
                var filter = plate.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh || !filter.sharedMesh.isReadable)
                {
                    Log.Warn("Plates: a plate's mesh can't be read, so it stays size 1.");
                    return;
                }
                filter.sharedMesh = Scaled(filter.sharedMesh, scale);
                var box = plate.GetComponent<BoxCollider>();
                if (box) { box.center *= scale; box.size *= scale; }
                var item = plate.GetComponent<ShipItem>();
                if (item)
                {
                    item.value = Mathf.RoundToInt(item.value * scale);
                    item.inventoryScale /= scale;
                    if (scale > 1f) item.holdDistance *= scale;
                }
            }
            catch (Exception e) { Log.Warn("Plates: sizing a plate to " + scale + " failed; it stays size 1: " + e.Message); }
        }

        // Shared per mesh and size: the hung plates and the size preview's life-size plate (LifeSizePlate) use the same copy.
        internal static Mesh Scaled(Mesh source, float scale)
        {
            string key = source.GetInstanceID() + "/" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (meshes.TryGetValue(key, out var mesh) && mesh) return mesh;
            mesh = Object.Instantiate(source);
            mesh.name = source.name + " x" + scale;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] *= scale;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
            meshes[key] = mesh;
            return mesh;
        }
    }

    // Name plates (§9, decision 91): six prefabs from the bundle, 650-655, saved and loaded by the game itself. Shipwright
    // keeps each plate's name, font and size in its records, found by the plate's instanceId. A renamed ship needs a new
    // plate, so records are never rewritten, and kept: a plate may hang aboard a far ship or wait in a crate.
    internal static class Plates
    {
        internal static PlateRecord RecordOf(int instanceId) => instanceId == 0 ? null : State.Save.plates.Find(p => p.instanceId == instanceId);

        // ---- Buying (§9.8) ----

        // Pays the fixed price, makes the record, and puts the plate on this shipyard's documents table.
        internal static bool Buy(Shipyard yard, ShipyardInfo info, PlateStyle style, string fontAsset, int size, float scale, int shipIndex)
        {
            var prefab = PlateBundle.Prefab(style);
            if (!prefab) { Notify.Show("Name plates are\nnot available."); return false; }
            var documents = Shipyards.Documents(yard);
            if (!documents) return false;
            scale = PlateData.CleanScale(scale);
            int currency = Mathf.Clamp(yard.region, 0, Fees.PlatePrice.Length - 1);
            if (!Money.Pay(currency, Fees.PlatePriceAt(currency, scale))) return false;
            var record = new PlateRecord { style = style.Key, name = Ships.NameOf(shipIndex), font = fontAsset, size = size, shipIndex = shipIndex, scale = scale };
            TableSpot(documents.transform, BottomOf(prefab.GetComponent<MeshFilter>().sharedMesh, scale), out Vector3 position, out Quaternion rotation);
            var plate = Object.Instantiate(prefab, position, rotation);
            (plate.GetComponent<PlateItem>() ?? plate.AddComponent<PlateItem>()).Record = record;
            plate.GetComponent<ShipItem>().sold = true;
            var saveable = plate.GetComponent<SaveablePrefab>();
            saveable.RegisterToSave();
            record.instanceId = saveable.instanceId;
            if (FloatingOriginManager.instance) plate.transform.SetParent(FloatingOriginManager.instance.transform, true);
            State.Save.plates.Add(record);
            Log.Info("Bought a " + style.Key + " plate \"" + record.name + "\" (" + fontAsset + ", size " + size + ", plate size " + PlateData.ScaleText(scale) +
                ", " + Fees.PlatePriceAt(currency, scale) + " " + Money.Short[currency] + ", instance " + record.instanceId + ").");
            Notify.Show("Your name plate is ready\non the documents.");
            return true;
        }

        // Where a bought plate stands (0.1.6, the user: "fixed spot ... fixed transform XYZ local position", "it could be place on
        // document", "make the offset higher, so nameplate and document don't overlap, also make it show it's name outward"):
        // upright on the documents, its bottom edge at a fixed local position in their own frame, the same at every shipyard
        // (every documents object is turned alike, rotation 270 on x so local +z is up: the QA's spot survey, run
        // 20261007-231040), 5 cm above them; its name toward the player, who stands at the documents to buy it. A plate already
        // there puts the next one 8 cm behind it. No raycasting (0.1.4's table search put the plate on the order sign's spot, B2).
        internal static readonly Vector3 OnDocuments = new Vector3(0f, 0f, 0.05f);
        internal const float StackStep = 0.08f;

        // plateBottom: how far the plate's lowest edge lies below its pivot when it stands upright (its mesh, at its size).
        internal static void TableSpot(Transform documents, float plateBottom, out Vector3 position, out Quaternion rotation)
        {
            Vector3 viewer = Refs.observerMirror ? Refs.observerMirror.transform.position : documents.position - documents.up;
            Vector3 toPlayer = viewer - documents.position;
            toPlayer.y = 0f;
            toPlayer = toPlayer.sqrMagnitude > 0.01f ? toPlayer.normalized : Vector3.forward;
            // The carved face is on the plate's -z side (a hung plate's +z goes into the wall): +z away from the player.
            rotation = Quaternion.LookRotation(-toPlayer, Vector3.up);
            Vector3 foot = documents.TransformPoint(OnDocuments);
            int before = 0;
            foreach (var plate in PlateItem.Live)
            {
                if (!plate) continue;
                var off = plate.transform.position - foot;
                if (new Vector2(off.x, off.z).magnitude < 0.6f && off.y > -0.05f && off.y < 6f) before++;
            }
            position = foot + Vector3.up * plateBottom - toPlayer * (StackStep * before);
        }

        // A standing plate's lowest edge below its pivot: from its mesh's bounds on its local y, at its size.
        internal static float BottomOf(Mesh mesh, float scale) => mesh ? (mesh.bounds.extents.y - mesh.bounds.center.y) * scale : 0.25f * scale;
    }

    // ==== PlateData ====

    // A plate style (assets\plates\plates.json → styles, made by BuildTools\make_plates.py; SPEC.md §9): its prefab
    // in the bundle (decision 91), the material slot of its face, the face canvas and the text box on it, the base font
    // size, and where the picture's camera stands. A style with brass letters also has their material slot and their
    // rectangle on the face canvas (x, y, width, height in canvas pixels, top-down), which their cutout quad covers. An
    // oval plate also keeps the name's ink inside an ellipse centred on the box (its half-width and half-height in canvas
    // pixels; 0 for none). Width and Height are the plate's own in metres at plate size 1 (plates.json's size, its mesh's
    // bounds), shown on the page (the size preview). The rest of the plate (mesh, materials, item settings) is in the bundle,
    // made by UJSM_AuthoringProject's TransportPlateBuilder.
    internal sealed class PlateStyle
    {
        internal string Key, Title;
        internal int PrefabIndex, FaceSlot, CanvasWidth, CanvasHeight, BasePixels;
        internal int LettersSlot = -1;
        internal int[] LettersRect;
        internal bool Ornaments;
        internal float Width, Height;
        internal float BoxX, BoxY, BoxWidth, BoxHeight, EllipseX, EllipseY, CameraDistance, LookY;

        internal bool BrassLetters => LettersSlot >= 0 && LettersRect != null;
    }

    // A font (§9.6). LabelScale sizes its button label against Immortal's, so the six labels look alike (the mockup's).
    internal sealed class PlateFont
    {
        internal string Button, Asset;
        internal float StrokeWeight, LabelScale = 1f;
    }

    internal static class PlateData
    {
        // Font sizes; 24 since 0.1.6 (B4: 16 before). The largest size that fits the name still caps it (PlateFaces).
        internal const int MinSize = 1, MaxSize = 24, DefaultSize = 10;

        // Plate sizes (0.1.6, B5, the user's steps): 0.8 to 1.5 by 0.1, then 2 to 5 by 0.5. 1 is the plate as it was before
        // 0.1.6; the price and the sell value scale with it.
        internal static readonly float[] Scales = { 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f };
        internal const int DefaultScaleStep = 2;

        // The step nearest a saved size; a missing, zero or broken one is 1.
        internal static int ScaleStep(float scale)
        {
            if (float.IsNaN(scale) || scale <= 0f) return DefaultScaleStep;
            int best = DefaultScaleStep;
            for (int i = 0; i < Scales.Length; i++)
                if (Math.Abs(Scales[i] - scale) < Math.Abs(Scales[best] - scale)) best = i;
            return best;
        }

        internal static float CleanScale(float scale) => Scales[ScaleStep(scale)];

        // "0.8", "1", "1.5", "2.5", "5" (the page's value).
        internal static string ScaleText(float scale) => CleanScale(scale).ToString("0.#", CultureInfo.InvariantCulture);

        // The plate's real size at a plate size, width by height to the centimetre: "0.80 × 0.19 m" (the size preview; the user:
        // "currently there is no way to know how big the nameplate are"). times: the sign between them, for a font without "×".
        internal static string SizeText(PlateStyle style, float scale, string times = " × ")
        {
            float k = CleanScale(scale);
            return (style.Width * k).ToString("0.00", CultureInfo.InvariantCulture) + times + (style.Height * k).ToString("0.00", CultureInfo.InvariantCulture) + " m";
        }

        // As plates.json gives them (a host test compares the two), in the page list's order: each region's two plates
        // together, the new design first, then Shipwright and Storage's (the user, 2026-10-28: "so each region have 2
        // nameplate", "Six rows, one page"). The new Al'Ankh's letters are carved only, the new Aestrin's and Emerald's carved
        // and covered in brass; Shipwright and Storage's three are carved only, as they were there. Round 8's text rooms (the
        // user: "if there is space, make the allowed font bigger", then "A"): every font's letters at least 5 mm from each
        // edge and ornament (BuildTools\text_room.py measured them).
        internal static readonly PlateStyle[] Styles =
        {
            new PlateStyle
            {
                Key = "alankh", Title = "Al'Ankh Copper Star", PrefabIndex = 650, FaceSlot = 0, Ornaments = true,
                CanvasWidth = 1800, CanvasHeight = 420, BasePixels = 185, BoxX = 185.1f, BoxY = 43.3f, BoxWidth = 1429.8f, BoxHeight = 333.3f,
                Width = 0.8f, Height = 0.1867f, CameraDistance = 1.28f, LookY = 0f,
            },
            new PlateStyle
            {
                Key = "alankh_rope", Title = "Al'Ankh rope plaque", PrefabIndex = 653, FaceSlot = 0, Ornaments = true,
                CanvasWidth = 1600, CanvasHeight = 800, BasePixels = 150, BoxX = 179.5f, BoxY = 116.7f, BoxWidth = 1240.9f, BoxHeight = 566.7f,
                EllipseX = 583.2f, EllipseY = 255f,
                Width = 0.6343f, Height = 0.3343f, CameraDistance = 1.03f, LookY = 0f,
            },
            new PlateStyle
            {
                Key = "aestrin", Title = "Aestrin Brass Anchor", PrefabIndex = 651, FaceSlot = 0, Ornaments = false,
                CanvasWidth = 1928, CanvasHeight = 711, BasePixels = 200, BoxX = 77.9f, BoxY = 260.1f, BoxWidth = 1772.3f, BoxHeight = 360f,
                LettersSlot = 4, LettersRect = new[] { 41, 224, 1846, 433 },
                Width = 0.9273f, Height = 0.3391f, CameraDistance = 1.48f, LookY = 0f,
            },
            new PlateStyle
            {
                Key = "aestrin_quarter", Title = "Aestrin quarter board", PrefabIndex = 654, FaceSlot = 0, Ornaments = true,
                CanvasWidth = 2200, CanvasHeight = 600, BasePixels = 150, BoxX = 384f, BoxY = 160f, BoxWidth = 1431.9f, BoxHeight = 280f,
                Width = 1.085f, Height = 0.226f, CameraDistance = 1.64f, LookY = 0f,
            },
            new PlateStyle
            {
                Key = "emerald", Title = "Emerald lacquered red", PrefabIndex = 652, FaceSlot = 0, Ornaments = true,
                CanvasWidth = 1660, CanvasHeight = 388, BasePixels = 170, BoxX = 233.2f, BoxY = 39.9f, BoxWidth = 1193.6f, BoxHeight = 297.8f,
                LettersSlot = 3, LettersRect = new[] { 203, 10, 1254, 358 },
                Width = 0.86f, Height = 0.2752f, CameraDistance = 1.37f, LookY = 0f,
            },
            new PlateStyle
            {
                Key = "emerald_torii", Title = "Emerald torii", PrefabIndex = 655, FaceSlot = 2, Ornaments = false,
                CanvasWidth = 1100, CanvasHeight = 440, BasePixels = 130, BoxX = 43.6f, BoxY = 87.2f, BoxWidth = 1012.8f, BoxHeight = 244.4f,
                Width = 0.9045f, Height = 0.4277f, CameraDistance = 1.45f, LookY = 0.025f,
            },
        };

        internal static PlateStyle Style(string key)
        {
            foreach (var style in Styles) if (style.Key == key) return style;
            return Styles[0];
        }

        internal static PlateStyle StyleOfPrefab(int prefabIndex)
        {
            foreach (var style in Styles) if (style.PrefabIndex == prefabIndex) return style;
            return null;
        }

        internal static readonly PlateFont[] Fonts =
        {
            new PlateFont { Button = "Immortal", Asset = "IMMORTAL", StrokeWeight = 1.0f, LabelScale = 1.0f },
            new PlateFont { Button = "Architects Daughter", Asset = "ArchitectsDaughter", StrokeWeight = 0.55f, LabelScale = 0.74f },
            new PlateFont { Button = "Kaushan Script", Asset = "KaushanScript-Regular", StrokeWeight = 0.8f, LabelScale = 0.9f },
            new PlateFont { Button = "Callimundial", Asset = "Callimundial", StrokeWeight = 0.8f, LabelScale = 0.95f },
            new PlateFont { Button = "Polo", Asset = "Polo-SemiBold", StrokeWeight = 0.85f, LabelScale = 1.0f },
            new PlateFont { Button = "Alex Brush", Asset = "AlexBrush-Regular", StrokeWeight = 0.4f, LabelScale = 1.2f },
        };

        internal static PlateFont Font(string asset)
        {
            foreach (var font in Fonts) if (font.Asset == asset) return font;
            return Fonts[0];
        }
    }

    // ==== PlateBundle ====

    // The name plates' AssetBundle (decision 91), made by UJSM_AuthoringProject's TransportPlateBuilder: the six plate
    // prefabs at 650-655 (650-652 the new designs, 653-655 Shipwright and Storage's three, round 7), and each face's carving
    // inputs (its wood, and the ornaments), readable. It loads once, at
    // start-up, from the plugin's assets folder. The prefabs join the game's prefab list when PrefabsDirectory starts, so
    // the game saves, loads and spawns plates like its own items; a save with plates needs the mod.
    internal static class PlateBundle
    {
        internal const string FileName = "dogeggz.shipwrighttransport.assets";
        private static AssetBundle bundle;
        private static readonly Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();
        private static readonly Dictionary<string, byte[]> inputs = new Dictionary<string, byte[]>();
        // The bundle's own copies of the Standard shader, read as it loads (UseGameShader).
        private static readonly HashSet<Shader> bundleShaders = new HashSet<Shader>();

        internal static bool Loaded => prefabs.Count == PlateData.Styles.Length;

        // The game's own Standard shader, once UseGameShader has found it.
        internal static Shader GameStandard { get; private set; }

        internal static void Load(string pluginDirectory)
        {
            string path = Path.Combine(Path.Combine(pluginDirectory, "assets"), FileName);
            if (!File.Exists(path)) { Log.Error("Plates: " + path + " is missing, so there are no name plates."); return; }
            bundle = AssetBundle.LoadFromFile(path);
            if (!bundle) { Log.Error("Plates: " + path + " could not be loaded."); return; }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>())
            {
                var saveable = prefab.GetComponent<SaveablePrefab>();
                if (!saveable || PlateData.StyleOfPrefab(saveable.prefabIndex) == null) continue;
                if (!prefab.GetComponent<PlateItem>()) prefab.AddComponent<PlateItem>();
                prefabs[saveable.prefabIndex] = prefab;
            }
            foreach (var material in StandardMaterials()) bundleShaders.Add(material.shader);
            if (Loaded) Log.Info("Plates: bundle loaded, prefabs 650-655.");
            else Log.Error("Plates: the bundle has " + prefabs.Count + " of the " + PlateData.Styles.Length + " plate prefabs.");
        }

        internal static GameObject Prefab(PlateStyle style) => prefabs.TryGetValue(style.PrefabIndex, out var prefab) ? prefab : null;

        // The fog (the user, 2026-10-11: a plate stayed sharp while the rest of her faded into it). The game draws its fog
        // with Unity's built-in fog, which each shader has compiled in or not. The bundle carries its own compiled copy of
        // Unity's Standard shader, built in a project with no foggy scene, so Unity kept none of its fog code. So every plate
        // material on Standard takes the game's own Standard, read from a vanilla prefab's material (Shader.Find("Standard")
        // may return the bundle's copy of the same name), keeping its keywords and its render queue (a shader change resets
        // the queue; the Aestrin anchor and the brass letters are cut out at 2450). The carved faces and letters copy the
        // prefab's materials, so they follow. Runs at each game start, before any plate exists; after the first it finds
        // them switched and says nothing.
        internal static void UseGameShader(PrefabsDirectory directory)
        {
            try
            {
                var materials = StandardMaterials();
                if (!directory || materials.Count == 0) return;
                Shader game = null;
                string from = null;
                var list = directory.directory ?? new GameObject[0];
                // The lowest index first: the game's own items, before any other mod's.
                for (int i = 0; i < list.Length && !game; i++)
                {
                    if (!list[i] || list[i].GetComponent<PlateItem>()) continue;
                    foreach (var renderer in list[i].GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (!material || !material.shader || material.shader.name != "Standard" || bundleShaders.Contains(material.shader)) continue;
                            game = material.shader;
                            from = "prefab " + i + " '" + list[i].name + "', material '" + material.name + "'";
                            break;
                        }
                        if (game) break;
                    }
                }
                if (!game)
                {
                    Log.Warn("Plates: no game item has a material on the game's Standard shader, so the plates keep the bundle's, which the fog doesn't cover.");
                    return;
                }
                GameStandard = game;
                int switched = 0, same = 0;
                foreach (var material in materials)
                {
                    if (material.shader != game)
                    {
                        var keywords = material.shaderKeywords;
                        int queue = material.renderQueue;
                        material.shader = game;
                        material.shaderKeywords = keywords;
                        if (material.renderQueue != queue) material.renderQueue = queue;
                        switched++;
                    }
                    // The same shader object as the game item's, not just one of the same name.
                    if (material.shader && material.shader.GetInstanceID() == game.GetInstanceID()) same++;
                }
                if (same < materials.Count)
                    Log.Error("Plates: only " + same + " of the " + materials.Count + " plate materials use the game's Standard shader (from " + from + ").");
                else if (switched > 0)
                    Log.Info("Plates: " + switched + " of their " + materials.Count + " materials now use the game's Standard shader (from " + from + "), so the fog covers them; each checked.");
            }
            catch (Exception e) { Log.Error("Plates: switching to the game's Standard shader failed: " + e); }
        }

        // The plate prefabs' materials whose shader is named Standard (the bundle's copy, or the game's once switched).
        private static List<Material> StandardMaterials()
        {
            var materials = new List<Material>();
            foreach (var prefab in prefabs.Values)
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials)
                        if (material && material.shader && material.shader.name == "Standard" && !materials.Contains(material)) materials.Add(material);
            return materials;
        }

        // Grows the game's list to hold 650-655, keeping what the game and other mods put in it.
        internal static void Register(PrefabsDirectory directory)
        {
            if (!directory || prefabs.Count == 0) return;
            var list = directory.directory ?? new GameObject[0];
            int needed = 0;
            foreach (int index in prefabs.Keys) needed = Math.Max(needed, index + 1);
            if (list.Length < needed)
            {
                var grown = new GameObject[needed];
                Array.Copy(list, grown, list.Length);
                list = grown;
            }
            foreach (var pair in prefabs)
            {
                if (list[pair.Key] && list[pair.Key] != pair.Value)
                    Log.Warn("Plates: prefab index " + pair.Key + " held '" + list[pair.Key].name + "'; the name plate takes it.");
                list[pair.Key] = pair.Value;
            }
            directory.directory = list;
        }

        // A face's carving inputs at canvas size, top-down: the wood as RGBA, the ornaments as one byte a pixel. Null when
        // missing (a style without ornaments has none).
        internal static byte[] FaceWood(PlateStyle style) => Input(style.Key + "-face-wood", style, false);

        internal static byte[] Ornaments(PlateStyle style) => style.Ornaments ? Input(style.Key + "-ornaments", style, true) : null;

        private static byte[] Input(string name, PlateStyle style, bool mask)
        {
            if (inputs.TryGetValue(name, out var cached)) return cached;
            byte[] bytes = null;
            try
            {
                var texture = bundle ? bundle.LoadAsset<Texture2D>(name) : null;
                if (!texture || texture.width != style.CanvasWidth || texture.height != style.CanvasHeight)
                    Log.Error("Plates: the bundle's " + name + " is missing or not " + style.CanvasWidth + " x " + style.CanvasHeight + ".");
                else
                {
                    int w = texture.width, h = texture.height;
                    var pixels = texture.GetPixels32();
                    bytes = new byte[w * h * (mask ? 1 : 4)];
                    for (int y = 0; y < h; y++)
                    {
                        int source = (h - 1 - y) * w;
                        for (int x = 0; x < w; x++)
                        {
                            var p = pixels[source + x];
                            int i = y * w + x;
                            if (mask) bytes[i] = p.r;
                            else { bytes[i * 4] = p.r; bytes[i * 4 + 1] = p.g; bytes[i * 4 + 2] = p.b; bytes[i * 4 + 3] = p.a; }
                        }
                    }
                }
            }
            catch (Exception e) { Log.Error("Plates: reading the bundle's " + name + " failed: " + e.Message); bytes = null; }
            inputs[name] = bytes;
            return bytes;
        }
    }

    // Before PrefabsDirectory.Start fills its item list from the prefab list. The plates take the game's Standard shader
    // from the list's own items first.
    [HarmonyPatch(typeof(PrefabsDirectory), "Start")]
    internal static class PlateRegistrationPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(PrefabsDirectory __instance)
        {
            PlateBundle.UseGameShader(__instance);
            PlateBundle.Register(__instance);
        }
    }

    // ==== PlateFaces ====

    // Carved faces (§9.5): the name drawn from the game font's glyphs into a mask, combined with the style's ornaments,
    // carved into the style's wood on a worker thread, and uploaded on the main thread. The wood and ornaments come from
    // the bundle. A style with brass letters (SPEC.md §9.5, step 11) also gets the name alone, cut to the letters' rectangle,
    // as the alpha of its letters material, a cutout of 'metal gold' over the carved name. A face, and its materials
    // (copies of the prefab's face and letters materials), is shared by every plate with the same style, font, size and
    // name; they show the plain wood and no letters until the carving is ready. Faces no plate and no picture uses are
    // let go, since each holds two canvas-sized textures.
    internal static class PlateFaces
    {
        private sealed class Face
        {
            internal Material Material, Letters;
            internal Texture2D Albedo, Normal, LettersTexture;
            internal volatile bool Carved, Failed;
            // Bottom-up, as Texture2D.SetPixels32 takes them, made on the worker thread.
            internal Color32[] AlbedoPixels, NormalPixels, LettersPixels;
            internal int Width, Height, LettersWidth, LettersHeight;
            internal bool Handed;
            internal float LastUse;
            internal readonly List<Action> Then = new List<Action>();
        }

        private static readonly Dictionary<string, Face> faces = new Dictionary<string, Face>();
        private static float nextEviction;

        internal static string Key(string style, string font, int size, string name) => style + "|" + font + "|" + size + "|" + name;

        // A plate's materials (the prefab's, which the caller has checked hold the face slot) with its face, and its
        // letters if the style has them, put in their slots; carves the face first if needed. The plates hung and the
        // plate page's picture both dress their materials here.
        internal static Material[] Dress(PlateStyle style, Material[] materials, string fontAsset, int size, string name)
        {
            bool letters = style.BrassLetters && style.LettersSlot < materials.Length;
            var face = FaceFor(style, materials[style.FaceSlot], letters ? materials[style.LettersSlot] : null, fontAsset, size, name);
            materials[style.FaceSlot] = face.Material;
            if (letters && face.Letters) materials[style.LettersSlot] = face.Letters;
            return materials;
        }

        // template: the prefab's face material, plain wood; lettersTemplate: its letters material, clear (null: none).
        private static Face FaceFor(PlateStyle style, Material template, Material lettersTemplate, string fontAsset, int size, string name)
        {
            string key = Key(style.Key, fontAsset, size, name);
            if (!faces.TryGetValue(key, out var face) || !face.Material)
            {
                face = new Face();
                faces[key] = face;
                // Without a template, the game's Standard shader (the fog: PlateBundle.UseGameShader).
                face.Material = template ? new Material(template) : new Material(PlateBundle.GameStandard ? PlateBundle.GameStandard : Shader.Find("Standard"));
                face.Material.name = "Shipwright plate face " + key;
                if (lettersTemplate) face.Letters = new Material(lettersTemplate) { name = "Shipwright plate letters " + key };
                try { Start(face, style, fontAsset, size, name); }
                catch (Exception e)
                {
                    face.Failed = true;
                    face.Carved = true;
                    Log.Error("Plates: carving \"" + name + "\" failed: " + e);
                }
            }
            face.LastUse = Time.unscaledTime;
            return face;
        }

        // Runs done once the face is carved and on its material, or once carving it failed; at once if it already is.
        internal static void WhenDone(PlateStyle style, string fontAsset, int size, string name, Action done)
        {
            if (!faces.TryGetValue(Key(style.Key, fontAsset, size, name), out var face) || face.Handed) { done(); return; }
            face.Then.Add(done);
        }

        internal static bool Done(string key) => faces.TryGetValue(key, out var face) && face.Handed;

        private static void Start(Face face, PlateStyle style, string fontAsset, int size, string name)
        {
            byte[] woodRgba = PlateBundle.FaceWood(style);
            if (woodRgba == null) { face.Failed = true; face.Carved = true; return; }
            var font = Fonts.Find(fontAsset) ?? Fonts.Find("IMMORTAL");
            int w = style.CanvasWidth, h = style.CanvasHeight;
            int px = style.BasePixels;
            // The name's render is read back here (it needs the main thread); turned into the mask on the worker.
            Color32[] rendered = font ? NameMask(font, name, style, size, out px) : null;
            // The ornaments are at canvas size (the bundle's check), cached and only read here.
            var ornaments = PlateBundle.Ornaments(style);
            bool letters = face.Letters;
            float sigma = Carving.BlurSigma(px, PlateData.Font(fontAsset).StrokeWeight);
            face.Width = w;
            face.Height = h;
            if (letters) { face.LettersWidth = style.LettersRect[2]; face.LettersHeight = style.LettersRect[3]; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] mask = rendered != null ? MaskOf(rendered, w, h) : new byte[w * h];
                    // The brass letters are the name alone, cut out before the ornaments join the mask.
                    if (letters) face.LettersPixels = Pixels(Carving.Letters(mask, w, h, style.LettersRect), face.LettersWidth, face.LettersHeight);
                    if (ornaments != null)
                        for (int i = 0; i < mask.Length; i++)
                            if (ornaments[i] > mask[i]) mask[i] = ornaments[i];
                    Carving.Carve(woodRgba, mask, w, h, sigma, out byte[] albedo, out byte[] normal);
                    face.AlbedoPixels = Pixels(albedo, w, h);
                    face.NormalPixels = Pixels(normal, w, h);
                }
                catch (Exception e) { face.Failed = true; Log.Error("Plates: carving failed: " + e); }
                face.Carved = true;
            });
        }

        // Every frame: uploads finished faces and hands them out; now and then lets unused faces go.
        internal static void Tick()
        {
            foreach (var face in faces.Values)
            {
                if (face.Handed || !face.Carved) continue;
                face.Handed = true;
                if (!face.Failed && face.AlbedoPixels != null)
                {
                    try
                    {
                        face.Albedo = Upload(face.AlbedoPixels, face.Width, face.Height, false);
                        face.Normal = Upload(face.NormalPixels, face.Width, face.Height, true);
                        if (face.Material)
                        {
                            face.Material.SetTexture("_MainTex", face.Albedo);
                            face.Material.SetTexture("_BumpMap", face.Normal);
                            face.Material.EnableKeyword("_NORMALMAP");
                            face.Material.SetFloat("_BumpScale", 1f);
                        }
                        if (face.LettersPixels != null && face.Letters)
                        {
                            face.LettersTexture = Upload(face.LettersPixels, face.LettersWidth, face.LettersHeight, false);
                            face.Letters.SetTexture("_MainTex", face.LettersTexture);
                        }
                    }
                    catch (Exception e) { face.Failed = true; Log.Error("Plates: uploading a face failed: " + e); }
                }
                face.AlbedoPixels = face.NormalPixels = face.LettersPixels = null;
                foreach (var done in face.Then)
                {
                    try { done(); } catch (Exception e) { Log.Error("Plates: " + e); }
                }
                face.Then.Clear();
            }
            if (Time.unscaledTime >= nextEviction)
            {
                nextEviction = Time.unscaledTime + 10f;
                Evict();
            }
        }

        // Top-down RGBA to pixels, bottom-up: Unity's row 0 is the bottom. Run on the worker thread (Color32 is plain data),
        // since at 2-5 ms and 2.5-5 MB a texture it was the main thread's share of each new face (the code review,
        // 2026-10-28, host timings).
        internal static Color32[] Pixels(byte[] rgba, int width, int height)
        {
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int source = (height - 1 - y) * width * 4, target = y * width;
                for (int x = 0; x < width; x++, source += 4)
                    pixels[target + x] = new Color32(rgba[source], rgba[source + 1], rgba[source + 2], rgba[source + 3]);
            }
            return pixels;
        }

        // The name's render, read back bottom-up, as the top-down mask of its red channel (on the worker thread too).
        internal static byte[] MaskOf(Color32[] pixels, int width, int height)
        {
            var mask = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    mask[(height - 1 - y) * width + x] = pixels[y * width + x].r;
            return mask;
        }

        // To a texture with mipmaps. Nothing reads it back, so the CPU copy is dropped.
        private static Texture2D Upload(Color32[] pixels, int width, int height, bool linear)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Apply(true, true);
            return texture;
        }

        // A face is kept while a plate (in the world, a crate or the inventory) or the plate page's picture shows it,
        // or while it is being carved, and for 10 seconds after its last use.
        private static void Evict()
        {
            if (faces.Count == 0) return;
            var used = new HashSet<string>();
            foreach (var plate in PlateItem.Live) if (plate && plate.FaceKey != null) used.Add(plate.FaceKey);
            if (PlatePreview.ShownKey != null) used.Add(PlatePreview.ShownKey);
            var gone = new List<string>();
            foreach (var pair in faces)
            {
                var face = pair.Value;
                if (used.Contains(pair.Key) || !face.Handed || Time.unscaledTime - face.LastUse < 10f) continue;
                if (face.Albedo) Object.Destroy(face.Albedo);
                if (face.Normal) Object.Destroy(face.Normal);
                if (face.LettersTexture) Object.Destroy(face.LettersTexture);
                if (face.Material) Object.Destroy(face.Material);
                if (face.Letters) Object.Destroy(face.Letters);
                gone.Add(pair.Key);
            }
            foreach (var key in gone) faces.Remove(key);
        }

        // ---- The name mask (§9.5 steps 1-2) ----

        // The pen moves by the advances (from 0); the ink runs from the leftmost glyph edge to the rightmost (InkLeft, InkRight,
        // from the pen's start) and from the highest top to the lowest bottom.
        internal struct Metrics
        {
            internal float Advance, InkLeft, InkRight, InkTop, InkBottom;
            internal float InkWidth => InkRight - InkLeft;
        }

        internal static Metrics Measure(Font font, string text, int px)
        {
            var metrics = new Metrics { InkLeft = float.MaxValue, InkRight = float.MinValue, InkTop = float.MinValue, InkBottom = float.MaxValue };
            foreach (char c in text)
            {
                if (!font.GetCharacterInfo(c, out CharacterInfo info, px)) continue;
                float pen = metrics.Advance;
                metrics.Advance += info.advance;
                if (info.maxY == info.minY) continue;
                metrics.InkLeft = Math.Min(metrics.InkLeft, pen + info.minX);
                metrics.InkRight = Math.Max(metrics.InkRight, pen + info.maxX);
                metrics.InkTop = Math.Max(metrics.InkTop, info.maxY);
                metrics.InkBottom = Math.Min(metrics.InkBottom, info.minY);
            }
            if (metrics.InkTop < metrics.InkBottom) metrics.InkTop = metrics.InkBottom = 0f;
            if (metrics.InkRight < metrics.InkLeft) metrics.InkLeft = metrics.InkRight = 0f;
            return metrics;
        }

        private static float Fit(Metrics m, PlateStyle style) =>
            Carving.Fit(m.InkWidth, m.InkTop - m.InkBottom, style.BoxWidth, style.BoxHeight, style.EllipseX, style.EllipseY);

        private static bool Fits(Font font, string name, PlateStyle style, int size)
        {
            int px = Carving.SizePixels(style.BasePixels, size);
            font.RequestCharactersInTexture(name, px);
            return Fit(Measure(font, name, px), style) >= 1f;
        }

        // The largest size, 1-24, at which the name fits its box without shrinking (§9.6). Estimated from the metrics at
        // the base size, then checked at its own size, since glyph metrics are rounded per pixel size: the number on the
        // page always matches the carving.
        internal static int MaxFittingSize(PlateStyle style, string fontAsset, string name)
        {
            var font = Fonts.Find(fontAsset);
            if (!font || string.IsNullOrEmpty(name)) return PlateData.MaxSize;
            int px = style.BasePixels;
            font.RequestCharactersInTexture(name, px);
            var m = Measure(font, name, px);
            int size = Carving.MaxFittingSize(style.BasePixels, m.InkWidth / px, (m.InkTop - m.InkBottom) / px, style.BoxWidth, style.BoxHeight,
                style.EllipseX, style.EllipseY);
            while (size > PlateData.MinSize && !Fits(font, name, style, size)) size--;
            return size;
        }

        // The name in white on black at canvas size, fitted to the text box and centred on its ink, drawn quad by quad
        // with the font's own material into a render texture, and read back as it lies (bottom-up; MaskOf turns it into the
        // mask). Requests, measures and draws in one go, since the dynamic font may rebuild its texture when characters are
        // requested.
        internal static Color32[] NameMask(Font font, string name, PlateStyle style, int size, out int px)
        {
            int w = style.CanvasWidth, h = style.CanvasHeight;
            px = Carving.SizePixels(style.BasePixels, size);
            font.RequestCharactersInTexture(name, px);
            var m = Measure(font, name, px);
            float fit = Fit(m, style);
            if (fit < 1f)
            {
                px = Math.Max(1, (int)Math.Floor(px * fit));
                font.RequestCharactersInTexture(name, px);
                m = Measure(font, name, px);
            }
            float pen = style.BoxX + style.BoxWidth / 2f - (m.InkLeft + m.InkRight) / 2f;
            float baseline = style.BoxY + style.BoxHeight / 2f + (m.InkTop + m.InkBottom) / 2f;
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
                foreach (char c in name)
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
                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.Destroy(read);
            }
        }
    }

    // ==== Carving ====

    // The carving of a name into a plate's face (§9.5), on plain arrays so it can run on a worker thread and in the
    // tests. Images are top-down (row 0 at the top), as the recipes and the mockup draw them.
    internal static class Carving
    {
        internal const float DepthDark = 0.72f, NormalStrength = 8f;

        // fit = min(1, box.w × 0.94 / inkWidth, box.h × 0.9 / inkHeight); px = floor(px × fit). With an ellipse (an oval
        // plate's half-width rx and half-height ry), also the ink's rectangle inside it: fit ≤ 1 / √((w / 2rx)² + (h / 2ry)²).
        // Round 8: the width is the ink's, from the leftmost glyph edge to the rightmost; it was the glyphs' advances, which
        // let a script's swashes (Alex Brush's K and Y) reach past the box, on the torii onto its pillar.
        internal static float Fit(float inkWidth, float inkHeight, float boxWidth, float boxHeight, float ellipseX = 0f, float ellipseY = 0f)
        {
            float fit = 1f;
            if (inkWidth > 0f) fit = Math.Min(fit, boxWidth * 0.94f / inkWidth);
            if (inkHeight > 0f) fit = Math.Min(fit, boxHeight * 0.9f / inkHeight);
            if (ellipseX > 0f && ellipseY > 0f && (inkWidth > 0f || inkHeight > 0f))
            {
                float u = inkWidth / (2f * ellipseX), v = inkHeight / (2f * ellipseY);
                fit = Math.Min(fit, 1f / (float)Math.Sqrt(u * u + v * v));
            }
            return fit;
        }

        // Font pixels for a size: base × size / 10 (each step a tenth of the default size 10).
        internal static int SizePixels(int basePixels, int size) => (int)Math.Floor(basePixels * size / 10.0);

        // The largest size, 1-24, at which the name fits its box without shrinking. Glyph metrics grow with the pixel
        // size, so they are given per font pixel. Round 8 (the user, 2026-10-28: "if there is space, make the allowed font
        // bigger"): the search starts at the largest size; it started at 16, though Shipwright and Storage 0.1.6 let sizes
        // go to 24, so the up arrow never passed 16.
        internal static int MaxFittingSize(int basePixels, float widthPerPixel, float inkPerPixel, float boxWidth, float boxHeight,
            float ellipseX = 0f, float ellipseY = 0f)
        {
            for (int size = PlateData.MaxSize; size > 1; size--)
            {
                int px = SizePixels(basePixels, size);
                if (Fit(widthPerPixel * px, inkPerPixel * px, boxWidth, boxHeight, ellipseX, ellipseY) >= 1f) return size;
            }
            return 1;
        }

        // σ = max(1.2, px × 0.045 × strokeWeight): thin scripts carve lighter.
        internal static float BlurSigma(int fittedPixels, float strokeWeight) => Math.Max(1.2f, fittedPixels * 0.045f * strokeWeight);

        internal static float[] Kernel(float sigma)
        {
            int radius = (int)Math.Ceiling(3f * sigma);
            var kernel = new float[2 * radius + 1];
            float sum = 0f;
            for (int i = 0; i < kernel.Length; i++)
            {
                float d = i - radius;
                kernel[i] = (float)Math.Exp(-d * d / (2f * sigma * sigma));
                sum += kernel[i];
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
            return kernel;
        }

        internal static float Depth(float mask, float blurred) => mask * Math.Min(1f, 1.6f * blurred);

        // k = 1 − depthDark × depth^0.8; rgb = wood.rgb × k + (18, 10, 4) × depth.
        internal static void Albedo(byte r, byte g, byte b, float depth, out byte outR, out byte outG, out byte outB)
        {
            float k = 1f - DepthDark * (float)Math.Pow(depth, 0.8);
            outR = Clamp(r * k + 18f * depth);
            outG = Clamp(g * k + 10f * depth);
            outB = Clamp(b * k + 4f * depth);
        }

        // The normal from h = −depth: du, dv from the neighbours (y is the row from the top); n = normalize(−du, −dv, 1).
        internal static void Normal(float left, float right, float up, float down, out byte outR, out byte outG, out byte outB)
        {
            float du = (-right - -left) * 0.5f * NormalStrength;
            float dv = -(-down - -up) * 0.5f * NormalStrength;
            float x = -du, y = -dv, z = 1f;
            float length = (float)Math.Sqrt(x * x + y * y + z * z);
            outR = Clamp((x / length * 0.5f + 0.5f) * 255f);
            outG = Clamp((y / length * 0.5f + 0.5f) * 255f);
            outB = Clamp((z / length * 0.5f + 0.5f) * 255f);
        }

        private static byte Clamp(float v) => (byte)(v <= 0f ? 0 : v >= 255f ? 255 : (int)Math.Round(v));

        // The brass letters' texture (SPEC.md §9.5, step 11): the name's mask (canvas size, top-down) cut to the letters'
        // rectangle {x, y, width, height}, as white RGBA whose alpha is the mask; the cutout material shows the brass where
        // the alpha passes its cutoff. Outside the canvas it is clear.
        internal static byte[] Letters(byte[] mask, int width, int height, int[] rect)
        {
            int rx = rect[0], ry = rect[1], rw = rect[2], rh = rect[3];
            var rgba = new byte[rw * rh * 4];
            for (int y = 0; y < rh; y++)
            {
                int sy = ry + y;
                for (int x = 0; x < rw; x++)
                {
                    int sx = rx + x, i = (y * rw + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                    rgba[i + 3] = sx >= 0 && sx < width && sy >= 0 && sy < height ? mask[sy * width + sx] : (byte)0;
                }
            }
            return rgba;
        }

        // Steps 3-7 of §9.5. wood: RGBA top-down; mask: 0..255 top-down (the name and the ornaments, already combined by
        // max). Out: the albedo (RGBA, alpha 255) and the normal map (RGBA, alpha 255), top-down.
        internal static void Carve(byte[] wood, byte[] mask, int width, int height, float sigma, out byte[] albedo, out byte[] normal)
        {
            int count = width * height;
            albedo = new byte[count * 4];
            normal = new byte[count * 4];
            Buffer.BlockCopy(wood, 0, albedo, 0, Math.Min(wood.Length, albedo.Length));
            for (int i = 0; i < count; i++)
            {
                albedo[i * 4 + 3] = 255;
                normal[i * 4] = 128; normal[i * 4 + 1] = 128; normal[i * 4 + 2] = 255; normal[i * 4 + 3] = 255;
            }
            // Depth is 0 wherever the mask is 0, so only the mask's box plus the kernel needs blurring.
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (mask[y * width + x] > 0)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return;
            var kernel = Kernel(sigma);
            int radius = kernel.Length / 2;
            int x0 = Math.Max(0, minX - radius), x1 = Math.Min(width - 1, maxX + radius);
            int y0 = Math.Max(0, minY - radius), y1 = Math.Min(height - 1, maxY + radius);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            // Work whose result is known to be zero is skipped, the rest summed as before, in the same order, so the output is
            // the same byte for byte (the code review, 2026-10-28: 1.2-2.2 times faster on the three styles; the host test
            // compares it with the full sums): a box row without ink blurs to zeros, a zero tap adds +0, and the vertical sum
            // is needed only where the mask is inked, since the depth is the mask times it.
            var horizontal = new float[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                int row = (y0 + y) * width;
                bool inked = false;
                for (int x = x0; x <= x1 && !inked; x++) inked = mask[row + x] > 0;
                if (!inked) continue;
                for (int x = 0; x < bw; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sx = x0 + x + k;
                        if (sx < 0 || sx >= width) continue;
                        byte m = mask[row + sx];
                        if (m == 0) continue;
                        sum += m / 255f * kernel[k + radius];
                    }
                    horizontal[y * bw + x] = sum;
                }
            }
            var depth = new float[bw * bh];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    byte m = mask[(y0 + y) * width + x0 + x];
                    if (m == 0) continue;
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sy = y + k;
                        if (sy < 0 || sy >= bh) continue;
                        sum += horizontal[sy * bw + x] * kernel[k + radius];
                    }
                    depth[y * bw + x] = Depth(m / 255f, sum);
                }
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    float d = depth[y * bw + x];
                    int i = ((y0 + y) * width + x0 + x) * 4;
                    if (d > 0f) Albedo(wood[i], wood[i + 1], wood[i + 2], d, out albedo[i], out albedo[i + 1], out albedo[i + 2]);
                    float left = depth[y * bw + Math.Max(0, x - 1)], right = depth[y * bw + Math.Min(bw - 1, x + 1)];
                    float up = depth[Math.Max(0, y - 1) * bw + x], down = depth[Math.Min(bh - 1, y + 1) * bw + x];
                    if (left != 0f || right != 0f || up != 0f || down != 0f)
                        Normal(left, right, up, down, out normal[i], out normal[i + 1], out normal[i + 2]);
                }
        }
    }

    // ==== PlatePreview ====

    // The plate page's picture (§9.7): the plate rendered on a transparent background into a texture with the map
    // quad's aspect, by a temporary camera that sees only a free layer, lit by two lights of its own. Ambient and fog
    // are fixed for the render and the game's own lights are kept off that layer, so the picture looks the same at
    // any hour. The rig is active only while it renders.
    internal static class PlatePreview
    {
        private const int Width = 1024, Height = 665;
        // The mockup's 'wide' view in Unity axes.
        private static readonly Quaternion Turn = Quaternion.AngleAxis(-2.86f, Vector3.right) * Quaternion.AngleAxis(8.02f, Vector3.up);
        private static readonly Color Ambient = new Color(0.3f, 0.29f, 0.27f);

        private static GameObject rig;
        private static Camera camera;
        private static MeshFilter subjectMesh;
        private static MeshRenderer subject;
        private static RenderTexture texture;
        private static Material shown;
        private static int layer = -1;
        private static int ticket;
        private static string shownStyle;

        // The face the picture shows, kept by PlateFaces while the page is open.
        internal static string ShownKey;

        // Draws the plate into the picture. Until a new name, font or size is carved, the picture keeps showing the
        // last one of the same style; a new style is drawn at once, with the plain wood face, and again when carved.
        internal static void Show(Renderer picture, PlateStyle style, string fontAsset, int size, string name)
        {
            if (!picture || !Prepare()) return;
            picture.sharedMaterial = shown;
            int mine = ++ticket;
            string key = PlateFaces.Key(style.Key, fontAsset, size, name);
            ShownKey = key;
            var prefab = PlateBundle.Prefab(style);
            if (!prefab) return;
            subjectMesh.sharedMesh = prefab.GetComponent<MeshFilter>().sharedMesh;
            var materials = prefab.GetComponent<MeshRenderer>().sharedMaterials;
            if (style.FaceSlot < materials.Length) materials = PlateFaces.Dress(style, materials, fontAsset, size, name);
            subject.sharedMaterials = materials;
            if (PlateFaces.Done(key)) { Render(style); return; }
            if (shownStyle != style.Key) Render(style);
            PlateFaces.WhenDone(style, fontAsset, size, name, () => { if (mine == ticket && picture) Render(style); });
        }

        // The board closed: the picture's texture goes, and its face may be let go.
        internal static void Release()
        {
            ticket++;
            ShownKey = null;
            shownStyle = null;
            if (texture) texture.Release();
        }

        private static bool Prepare()
        {
            try
            {
                if (!rig) Build();
                if (!texture)
                {
                    texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                    {
                        name = "Shipwright plate picture",
                        antiAliasing = 4,
                    };
                }
                if (!texture.IsCreated()) { texture.Create(); shownStyle = null; }
                camera.targetTexture = texture;
                if (!shown) shown = PictureMaterial();
                shown.mainTexture = texture;
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Plate picture: " + e);
                return false;
            }
        }

        private static void Build()
        {
            if (layer < 0) layer = FreeLayer();
            rig = new GameObject("Shipwright plate picture") { layer = layer };
            rig.SetActive(false);
            Object.DontDestroyOnLoad(rig);
            rig.transform.position = new Vector3(0f, -2000f, 0f);

            var plate = new GameObject("plate") { layer = layer };
            plate.transform.SetParent(rig.transform, false);
            plate.transform.localRotation = Turn;
            subjectMesh = plate.AddComponent<MeshFilter>();
            subject = plate.AddComponent<MeshRenderer>();
            subject.shadowCastingMode = ShadowCastingMode.Off;
            subject.receiveShadows = false;
            subject.lightProbeUsage = LightProbeUsage.Off;
            subject.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var eye = new GameObject("camera") { layer = layer };
            eye.transform.SetParent(rig.transform, false);
            camera = eye.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.fieldOfView = 26f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 10f;
            camera.aspect = Width / (float)Height;
            camera.renderingPath = RenderingPath.Forward;
            camera.allowHDR = false;
            camera.allowMSAA = true;
            camera.useOcclusionCulling = false;

            // The mockup's lights: a warm key from (−4, 5, −4) and a weak cool fill from (4, 1, −2), toward the plate.
            AddLight(rig.transform, "key", new Vector3(-4f, 5f, -4f), new Color(1f, 0.957f, 0.878f), 0.75f);
            AddLight(rig.transform, "fill", new Vector3(4f, 1f, -2f), new Color(0.749f, 0.831f, 1f), 0.16f);
        }

        private static void AddLight(Transform parent, string name, Vector3 from, Color colour, float intensity)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = from;
            go.transform.localRotation = Quaternion.LookRotation(-from);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << layer;
            light.renderMode = LightRenderMode.ForcePixel;
        }

        // Layers with no name are unused; the highest is taken.
        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            Log.Warn("Plate picture: no free layer; using 31.");
            return 31;
        }

        private static Material PictureMaterial()
        {
            foreach (string name in new[] { "Unlit/Transparent", "Sprites/Default", "UI/Default" })
            {
                var shader = Shader.Find(name);
                if (!shader) continue;
                Log.Info("Plate picture shader: " + name + ".");
                return new Material(shader) { name = "Shipwright plate picture" };
            }
            Log.Warn("Plate picture: no transparent unlit shader found; using Standard.");
            return new Material(Shader.Find("Standard")) { name = "Shipwright plate picture" };
        }

        private static void Render(PlateStyle style)
        {
            if (!Prepare()) return;
            var eye = camera.transform;
            eye.localPosition = new Vector3(0.03f, 0.02f, -style.CameraDistance);
            eye.LookAt(rig.transform.TransformPoint(new Vector3(0f, style.LookY, 0f)));

            var lights = Object.FindObjectsOfType<Light>();
            var masks = new int[lights.Length];
            var ambientMode = RenderSettings.ambientMode;
            var ambientLight = RenderSettings.ambientLight;
            var ambientProbe = RenderSettings.ambientProbe;
            bool fog = RenderSettings.fog;
            try
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    masks[i] = lights[i].cullingMask;
                    lights[i].cullingMask &= ~(1 << layer);
                }
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Ambient;
                var probe = new SphericalHarmonicsL2();
                probe.AddAmbientLight(Ambient);
                RenderSettings.ambientProbe = probe;
                RenderSettings.fog = false;
                rig.SetActive(true);
                camera.Render();
                shownStyle = style.Key;
            }
            catch (Exception e) { Log.Error("Plate picture: rendering failed: " + e); }
            finally
            {
                rig.SetActive(false);
                for (int i = 0; i < lights.Length; i++) if (lights[i]) lights[i].cullingMask = masks[i];
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientProbe = ambientProbe;
                RenderSettings.fog = fog;
            }
        }
    }

    // ==== LifeSizePlate ====

    // The size preview's life-size plate (§9.7; the user, 2026-10-28: "for preview, do 1 and 3", then "add button, button name
    // 'Preview size'"): the plate page's choices (style, name, font, font size, plate size) as a plate standing where Buy would
    // put it on the documents (Plates.TableSpot), while the board is put aside. Only its look: no ShipItem, no collider, never
    // saved, so a click through it reaches the documents. Its face is the page picture's (PlatePreview.ShownKey keeps it).
    internal static class LifeSizePlate
    {
        internal const string Name = "Shipwright plate preview";
        private static GameObject plate;

        internal static bool Showing => plate;

        internal static bool Show(Transform documents, PlateStyle style, string fontAsset, int size, string name, float scale)
        {
            Hide();
            var prefab = PlateBundle.Prefab(style);
            var source = prefab ? prefab.GetComponent<MeshFilter>() : null;
            var look = prefab ? prefab.GetComponent<MeshRenderer>() : null;
            if (!documents || !source || !source.sharedMesh || !look) return false;
            try
            {
                scale = PlateData.CleanScale(scale);
                var mesh = Mathf.Approximately(scale, 1f) || !source.sharedMesh.isReadable ? source.sharedMesh : PlateScaling.Scaled(source.sharedMesh, scale);
                Plates.TableSpot(documents, Plates.BottomOf(source.sharedMesh, scale), out Vector3 position, out Quaternion rotation);
                plate = new GameObject(Name) { layer = prefab.layer };
                plate.transform.SetPositionAndRotation(position, rotation);
                if (FloatingOriginManager.instance) plate.transform.SetParent(FloatingOriginManager.instance.transform, true);
                plate.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = plate.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = look.shadowCastingMode;
                renderer.receiveShadows = look.receiveShadows;
                var materials = look.sharedMaterials;
                if (style.FaceSlot < materials.Length) materials = PlateFaces.Dress(style, materials, fontAsset, size, name);
                renderer.sharedMaterials = materials;
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Plates: the size preview failed: " + e);
                Hide();
                return false;
            }
        }

        internal static void Hide()
        {
            if (plate) Object.Destroy(plate);
            plate = null;
        }
    }

    // ==== BoardPlates ====

    // The name plate page (§9.7, plate.json): the list becomes the six styles, each region's two together (all six rows on
    // one page, BoardParts.Rows), and back returns to the ship. The page
    // has the six font buttons, the plate's picture, the font size and the plate size with their arrows, "Preview size" and the
    // plate's real size to their right, three lines, the price and Buy. Built once when it opens; a click changes only what it
    // changes (0.1.6, B6: every click rebuilt the whole page, which flickered).
    internal sealed partial class Board
    {
        private int plateStyle;
        private string plateFont;
        // The size asked for; the page shows the largest that fits the name, if that is smaller (§9.6).
        private int plateSize;
        // The plate size's step in PlateData.Scales (0.1.6, B5).
        private int plateScale;
        private PlatePageParts platePage;

        // What a click on the plate page changes in place.
        private sealed class PlatePageParts
        {
            internal int Index;
            internal Renderer Picture;
            internal TextMesh Size, Scale, Metres, Price;
            internal readonly List<KeyValuePair<string, MeshRenderer>> FontButtons = new List<KeyValuePair<string, MeshRenderer>>();
            internal readonly List<Material> FontMaterials = new List<Material>();
        }

        // The selectors' text and arrows at 75% of the page's price label and value (B5: two now share the old one's room).
        private const float SelectorScale = 0.75f;
        // The selectors' centre, 0.12 left of the price's column (-0.275 until the size preview; the user: "adjust both font
        // size and plate size adjuster left a bit, and fit new info on the right of the adjuster"), their arrows 0.125 to each side.
        private const float SelectorX = -0.155f, ArrowStep = 0.125f;

        // The size preview (the user, 2026-10-28: "add button, button name 'Preview size'"; "after clicking preview button, on top
        // keep showing notification remind player how to resume to nameplate UI"): the board's parts put aside, the life-size
        // plate on the documents, the player free to look; a click on the documents brings the page back (DocumentsPatches).
        internal const string PreviewReminder = "Click the documents\nto go back to the\nname plate page.";
        private bool previewing;
        private float nextReminder;
        private readonly List<GameObject> putAside = new List<GameObject>();

        internal static bool Previewing => IsOpen && Instance.previewing;

        // From a ship's page: Al'Ankh, Immortal, size 10, plate size 1.
        private void ShowStyles(int index)
        {
            if (!PlateBundle.Loaded) { Notify.Show("Name plates are\nnot available."); return; }
            plateStyle = 0;
            plateFont = PlateData.Fonts[0].Asset;
            plateSize = PlateData.DefaultSize;
            plateScale = PlateData.DefaultScaleStep;
            ShowPlate(index);
        }

        private void ShowPlate(int index)
        {
            if (Ships.Find(index) == null) { ShowShips(-1); return; }
            PlateList(index);
            PlatePage(index);
        }

        private void PlateList(int index)
        {
            items.Clear();
            for (int i = 0; i < PlateData.Styles.Length; i++)
            {
                int style = i;
                items.Add(new BoardItem { Text = PlateData.Styles[i].Title, Clicked = () => PickStyle(index, style) });
            }
            ShowList(BoardList.Styles, plateStyle, false, "back", () => ShowShips(index));
        }

        // A style keeps the page: the list's highlight, the size that fits and the picture change.
        private void PickStyle(int index, int style)
        {
            if (style == plateStyle) return;
            plateStyle = style;
            if (Ships.Find(index) == null) { ShowShips(-1); return; }
            PlateList(index);
            UpdatePlatePage();
        }

        private void PlatePage(int index)
        {
            BeginPage(() => ShowPlate(index));
            var shown = platePage = new PlatePageParts { Index = index };
            page.Title(Ships.NameOf(index) + " name plate", false);

            for (int i = 0; i < PlateData.Fonts.Length; i++) FontButton(i, PlateData.Fonts[i], shown);

            shown.Picture = page.Picture(new Vector3(-0.275f, 0.43f, 0.65f), new Vector3(0.95f, 0.6175f, 0.92743f));

            var labelScale = BoardPage.PriceLabelScale * SelectorScale;
            var valueScale = new Vector3(0.01248f, 0.01248f, 0.0104f) * SelectorScale;
            page.Text(parts.PriceLabel, "Font size", new Vector3(SelectorX, 0.085f, 0.6488f), labelScale, BoardPage.AnchorMiddleCenter, BoardPage.AlignCenter);
            shown.Size = page.Text(parts.Value, "", new Vector3(SelectorX, 0.02f, 0.648f), valueScale, BoardPage.AnchorMiddleCenter, BoardPage.AlignCenter);
            page.Arrow(parts.Previous, new Vector3(SelectorX + ArrowStep, 0.02f, 0.645f), () => StepPlateSize(-1), SelectorScale);
            page.Arrow(parts.Next, new Vector3(SelectorX - ArrowStep, 0.02f, 0.645f), () => StepPlateSize(1), SelectorScale);
            page.Text(parts.PriceLabel, "Plate size", new Vector3(SelectorX, -0.05f, 0.6488f), labelScale, BoardPage.AnchorMiddleCenter, BoardPage.AlignCenter);
            shown.Scale = page.Text(parts.Value, "", new Vector3(SelectorX, -0.115f, 0.648f), valueScale, BoardPage.AnchorMiddleCenter, BoardPage.AlignCenter);
            page.Arrow(parts.Previous, new Vector3(SelectorX + ArrowStep, -0.115f, 0.645f), () => StepPlateScale(-1), SelectorScale);
            page.Arrow(parts.Next, new Vector3(SelectorX - ArrowStep, -0.115f, 0.645f), () => StepPlateScale(1), SelectorScale);
            // To the selectors' right: "Preview size" beside the font size (a font button's copy, 58%), and the plate's real size
            // beside the plate size (the user: "fit new info on the right of the adjuster"). The button's face sits 0.029 above its
            // pivot (measured on the screen, run 20261028-231103: its centre 9.5 px above the arrows'), so its pivot is that much
            // below the font size's row, to line up with its arrows (the user: "make the preview button align with the size adjuster").
            var preview = page.Button("Preview size", new Vector3(-0.555f, 0.02f - 0.029f, 0.639f), new Vector3(0.58f, 0.58f, 1f), StartPreview);
            var previewLabel = preview.transform.parent.Find("text");
            if (previewLabel) previewLabel.localScale = new Vector3(previewLabel.localScale.x * 0.72f, previewLabel.localScale.y * 0.72f, previewLabel.localScale.z);
            shown.Metres = page.Small("", -0.36f, -0.115f);

            page.Line(0, "Hang it on any wall.");
            page.Line(1, "A renamed ship");
            page.Line(2, "needs a new plate.");
            shown.Price = page.Fee("Price:", "", Money.Names[yard.region]);
            page.MainButton("Buy", () =>
            {
                if (Plates.Buy(yard, info, PlateData.Styles[plateStyle], plateFont, ShownPlateSize(index), PlateData.Scales[plateScale], index)) ShowShips(index);
            });
            UpdatePlatePage();
        }

        // The font size shown: the one asked for, capped by the largest that fits the name in this style and font.
        private int ShownPlateSize(int index) => ShownPlateSize(index, out _);

        private int ShownPlateSize(int index, out int fits)
        {
            fits = PlateFaces.MaxFittingSize(PlateData.Styles[plateStyle], plateFont, Ships.NameOf(index));
            return Math.Max(PlateData.MinSize, Math.Min(plateSize, fits));
        }

        // Puts the page's values, font highlight and picture in line with the choices, without rebuilding it.
        private void UpdatePlatePage()
        {
            var shown = platePage;
            if (shown == null || !shown.Size || !shown.Metres || !shown.Picture) { Refresh(); return; }
            int size = ShownPlateSize(shown.Index);
            shown.Size.text = size.ToString();
            shown.Scale.text = PlateData.ScaleText(PlateData.Scales[plateScale]);
            var font = shown.Metres.font;
            shown.Metres.text = PlateData.SizeText(PlateData.Styles[plateStyle], PlateData.Scales[plateScale], font && !font.HasCharacter('×') ? " x " : " × ");
            int currency = Mathf.Clamp(yard.region, 0, Fees.PlatePrice.Length - 1);
            shown.Price.text = Money.Amount(Fees.PlatePriceAt(currency, PlateData.Scales[plateScale]));
            for (int i = 0; i < shown.FontButtons.Count; i++)
                if (shown.FontButtons[i].Value) shown.FontButtons[i].Value.sharedMaterial = shown.FontButtons[i].Key == plateFont ? parts.Medium : shown.FontMaterials[i];
            PlatePreview.Show(shown.Picture, PlateData.Styles[plateStyle], plateFont, size, Ships.NameOf(shown.Index));
        }

        // The up arrow stops at the largest size that fits the name, the down arrow at 1.
        private void StepPlateSize(int step)
        {
            var shown = platePage;
            if (shown == null) return;
            int size = ShownPlateSize(shown.Index, out int fits) + step;
            if (size < PlateData.MinSize || size > Math.Min(PlateData.MaxSize, fits)) return;
            plateSize = size;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayParchmentSound();
            UpdatePlatePage();
        }

        private void StepPlateScale(int step)
        {
            int next = plateScale + step;
            if (next < 0 || next >= PlateData.Scales.Length) return;
            plateScale = next;
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayParchmentSound();
            UpdatePlatePage();
        }

        // "Preview size": the board's parts hidden (its root stays active, so Update still closes it at 4 m, and Esc or the
        // inventory key still close it), the life-size plate on the documents, the cursor let go so the player can look round
        // it, and the reminder on top until the page comes back.
        private void StartPreview()
        {
            var shown = platePage;
            if (previewing || shown == null || !documents) return;
            if (!LifeSizePlate.Show(documents, PlateData.Styles[plateStyle], plateFont, ShownPlateSize(shown.Index), Ships.NameOf(shown.Index), PlateData.Scales[plateScale]))
            {
                Notify.Show("The preview is\nnot available.");
                return;
            }
            previewing = true;
            foreach (Transform part in transform)
            {
                if (!part.gameObject.activeSelf) continue;
                putAside.Add(part.gameObject);
                part.gameObject.SetActive(false);
            }
            MouseLook.ToggleMouseLookAndCursor(true);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayCloseSound();
            nextReminder = 0f;
            KeepReminder();
        }

        // The reminder stays: the notification's own timer, set again before it runs out.
        private void KeepReminder()
        {
            if (!previewing || Time.unscaledTime < nextReminder) return;
            nextReminder = Time.unscaledTime + 0.5f;
            Notify.Show(PreviewReminder, 1f);
        }

        // Undoes the preview: the plate, the board's parts, the reminder; with the board staying open, the cursor back too
        // (Close leaves the cursor to its own path).
        private void EndPreview(bool boardStays)
        {
            if (!previewing) return;
            previewing = false;
            LifeSizePlate.Hide();
            foreach (var part in putAside) if (part) part.SetActive(true);
            putAside.Clear();
            // The reminder goes at once: a timer this short ends on the notification's next frame.
            Notify.Show(PreviewReminder, 0.001f);
            if (boardStays) MouseLook.ToggleMouseLookAndCursor(false);
        }

        // A click (or R) on the documents while previewing brings the plate page back as it was, instead of what it would do.
        internal static bool ResumeFromPreview(GoPointerButton clicked)
        {
            if (!Previewing || !clicked || clicked.transform != Instance.documents) return false;
            Instance.EndPreview(true);
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayOpenSound();
            Instance.UpdatePlatePage();
            return true;
        }

        // A copy of the page's button at 58%, its label in its own font: the TextMesh's font and its renderer's
        // material both switch. The picked font is on the picked plate.
        private void FontButton(int i, PlateFont font, PlatePageParts shown)
        {
            var button = page.Button(font.Button, new Vector3(0.455f, 0.58f - 0.176f * i, 0.639f), new Vector3(0.58f, 0.58f, 1f), () =>
            {
                if (plateFont == font.Asset) return;
                plateFont = font.Asset;
                UpdatePlatePage();
            });
            var label = button.transform.parent.Find("text");
            var mesh = label ? label.GetComponent<TextMesh>() : null;
            var asset = Fonts.Find(font.Asset);
            if (mesh && asset)
            {
                mesh.font = asset;
                label.GetComponent<MeshRenderer>().sharedMaterial = asset.material;
            }
            if (label)
            {
                // The button is squeezed alike in x and y; 0.72 keeps the long names on it.
                float k = font.LabelScale * 0.72f;
                label.localScale = new Vector3(label.localScale.x * k, label.localScale.y * k, label.localScale.z);
            }
            var plate = button.GetComponent<MeshRenderer>();
            shown.FontButtons.Add(new KeyValuePair<string, MeshRenderer>(font.Asset, plate));
            shown.FontMaterials.Add(plate ? plate.sharedMaterial : null);
        }
    }
}
