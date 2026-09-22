using System;
using System.Collections.Generic;
using System.Linq;
using AvatarChangeLog;

static class CoreTests
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
    static Snapshot S(params HistoryEntry[] entries) => new Snapshot { avatarId = "avatar-a", avatarName = "Test", entries = entries.ToList(), createdUtc = "2026-09-17T00:00:00Z", note = "" };
    static HistoryEntry E(string key, string value, string category = "Transform", string path = ".") =>
        new HistoryEntry { key = key, value = value, category = category, path = path, item = key };
    static void Main()
    {
        var keyBuilder = new System.Text.StringBuilder();
        string[] keyParts = { "", "a", "a:b", "12:顔 / 目[0]", "🙂", "改行\n名前", "0:", new string('長', 200) };
        int keyCases = 0;
        foreach (string keyCategory in keyParts)
            foreach (string keyPath in keyParts)
                foreach (string keyOwner in keyParts)
                    foreach (string keyProperty in keyParts)
                    {
                        string expected = string.Concat(new[] { keyCategory, keyPath, keyOwner, keyProperty }.Select(s => s.Length + ":" + s));
                        if (SnapshotCapture.EntryKey(keyBuilder, keyCategory, keyPath, keyOwner, keyProperty) != expected)
                            throw new Exception("Capture key compatibility failed");
                        keyCases++;
                    }
        Check(keyCases == 4096, "Reusable capture keys match all 4096 legacy combinations including separators, Unicode and empty fields");
        string savedKey = SnapshotCapture.EntryKey(keyBuilder, "マテリアル", ".", "Renderer [0]", "カラー");
        SnapshotCapture.EntryKey(keyBuilder, "別", "", "", "");
        Check(savedKey == "5:マテリアル1:.12:Renderer [0]3:カラー",
            "Previously captured key strings remain unchanged when the builder is reused");
        Check(HistoryDiff.Compare(S(), S()).Count == 0, "Empty histories");
        Check(HistoryDiff.Compare(S(E("a", "1")), S(E("a", "1"))).Count == 0, "Unchanged item");
        var d = HistoryDiff.Compare(S(E("a", "old"), E("deleted", "x"), E("same", "1")), S(E("a", "new"), E("added", "y"), E("same", "1")));
        Check(d.Count == 3 && d.Single(c => c.Entry.key == "a").Kind == "変更" && d.Single(c => c.Entry.key == "deleted").Kind == "削除" && d.Single(c => c.Entry.key == "added").Kind == "追加", "Mixed add/change/remove");
        Check(d.Single(c => c.Entry.key == "a").before.value == "old" && d.Single(c => c.Entry.key == "a").after.value == "new", "Old and new values preserved");
        var a = S(E("顔/目", "0.12345678"), E("empty", ""));
        var b = S(E("顔/目", "0.12345679"), E("empty", " "));
        Check(HistoryDiff.Compare(a, b).Count == 2, "Japanese keys, precision and whitespace changes");
        var ordered = HistoryDiff.Compare(S(), S(E("z", "1", "B"), E("y", "1", "A", "b"), E("x", "1", "A", "a")));
        Check(string.Join(",", ordered.Select(c => c.Entry.key)) == "x,y,z", "Stable category and path ordering");
        var reverse = HistoryDiff.Compare(b, a);
        Check(reverse.All(c => c.before.value == b.entries.Single(e => e.key == c.Entry.key).value), "Reverse comparisons");
        bool rejected = false;
        var alien = S(); alien.avatarId = "avatar-b";
        try { HistoryDiff.Compare(a, alien); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Cross-avatar comparison rejected");
        rejected = false;
        try { HistoryDiff.Compare(null, a); } catch (ArgumentNullException) { rejected = true; }
        Check(rejected, "Null snapshot rejected");
        rejected = false;
        try { HistoryDiff.Compare(S(E("x", "1"), E("x", "2")), a); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Duplicate snapshot keys rejected");
        var random = new Random(1709);
        for (int trial = 0; trial < 200; trial++)
        {
            var left = new Dictionary<string, string>(); var right = new Dictionary<string, string>();
            for (int i = 0; i < 100; i++)
            {
                if (random.Next(3) != 0) left[i.ToString()] = random.Next(4).ToString();
                if (random.Next(3) != 0) right[i.ToString()] = random.Next(4).ToString();
            }
            var actual = HistoryDiff.Compare(S(left.Select(p => E(p.Key, p.Value)).ToArray()), S(right.Select(p => E(p.Key, p.Value)).ToArray()));
            int expected = 0;
            for (int i = 0; i < 100; i++)
            {
                bool l = left.TryGetValue(i.ToString(), out var lv), r = right.TryGetValue(i.ToString(), out var rv);
                if (l != r || (l && lv != rv)) expected++;
            }
            if (actual.Count != expected || actual.Select(c => c.Entry.key).Distinct().Count() != expected)
                throw new Exception("Randomized diff failed at " + trial);
        }
        Check(true, "200 seeded randomized comparisons (100 possible entries each)");
        for (int trial = 0; trial < 200; trial++)
        {
            var left = S(Enumerable.Range(0, 100).Where(i => random.Next(3) != 0).Select(i => E(i.ToString(), random.Next(4).ToString())).ToArray());
            var right = S(Enumerable.Range(0, 100).Where(i => random.Next(3) != 0).Select(i => E(i.ToString(), random.Next(4).ToString())).ToArray());
            left.entries.Sort((x, y) => StringComparer.Ordinal.Compare(x.key, y.key));
            right.entries.Sort((x, y) => StringComparer.Ordinal.Compare(x.key, y.key));
            if (HistoryDiff.HasChanges(left, right) != (HistoryDiff.Compare(left, right).Count > 0))
                throw new Exception("Fast change detection differs from detailed diff");
        }
        Check(true, "Fast recording decision matches detailed diff across 200 randomized sorted snapshots");
        Check(!HistoryDiff.HasChanges(a, a) && !HistoryDiff.HasChanges(S(), S()), "Fast decision leaves unchanged and empty records idle");
        Check(HistoryDiff.HasChanges(S(E("a", "1")), S(E("b", "1"))) &&
            HistoryDiff.HasChanges(S(E("a", "1")), S(E("a", "2"))) &&
            HistoryDiff.HasChanges(S(), S(E("a", "1"))) && HistoryDiff.HasChanges(S(E("a", "1")), S()),
            "Fast decision detects replacement, edit, addition and removal");
        rejected = false;
        try { HistoryDiff.HasChanges(a, alien); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Fast recording decision rejects different avatars");
        var schedule = new CaptureSchedule();
        Check(!schedule.Ready(100), "Idle recording schedule does not request a snapshot");
        schedule.Changed(0);
        Check(!schedule.Ready(0.4), "Slider edit is not committed immediately");
        schedule.Changed(0.4);
        Check(!schedule.Ready(0.9) && schedule.Ready(1.1), "Consecutive edits are coalesced after the last edit");
        schedule.Reset();
        Check(!schedule.Pending && !schedule.Ready(9), "Committed schedule stays idle without edits");
        schedule.Changed(10);
        for (int i = 1; i <= 49; i++) schedule.Changed(10 + i * 0.1);
        Check(!schedule.Ready(14.95) && schedule.Ready(15), "Long slider drag checkpoints after five seconds");
        schedule.Reset(); schedule.Changed(20);
        Check(!schedule.Ready(20.3) && schedule.Ready(20.7), "New editing session resets debounce clock");

        HistoryEntry Named(string key, string value, string categoryName, string item, string path = ".")
        { var entry = E(key, value, categoryName, path); entry.item = item; return entry; }
        var shapeBefore = S(Named("shape", "0", "BlendShape", "UnityEngine.SkinnedMeshRenderer [0] / 0: Smile"));
        var shapeAfter = S(Named("shape", "65", "BlendShape", "UnityEngine.SkinnedMeshRenderer [0] / 0: Smile"));
        shapeAfter.avatarName = "アバター";
        var activity = HistoryText.Build(shapeBefore, shapeAfter);
        Check(activity.Count == 1 && activity[0].Summary == "アバター：シェイプキー「Smile」の数値が 0 → 65 に変更", "BlendShape log matches readable Japanese sentence");
        Check(HistoryText.Build(shapeAfter, shapeBefore).Single().Summary.Contains("65 → 0"), "Undo-direction value changes remain visible");
        Check(HistoryText.Build(shapeBefore, shapeBefore).Count == 0, "Unchanged snapshots produce no log spam");
        Check(HistoryText.ReadablePath("./%E9%A1%94%2F%E7%9B%AE[0]") == "顔/目[0]", "Encoded Japanese object path is readable");
        Check(HistoryText.ReadablePath("Assets/Color%20Blue.mat") == "Assets/Color%20Blue.mat", "Asset filenames preserve literal percent sequences");
        var addOutfit = S(
            Named("object", "あり", "オブジェクト", "Object / 存在", "./Outfit[0]"),
            Named("child", "あり", "オブジェクト", "Object / 存在", "./Outfit[0]/Ribbon[0]"),
            Named("position", "0, 0, 0", "Transform", "UnityEngine.Transform [0] / m_LocalPosition", "./Outfit[0]"),
            Named("shape", "0", "BlendShape", "UnityEngine.SkinnedMeshRenderer [0] / 0: Smile", "./Outfit[0]/Ribbon[0]"));
        Check(HistoryText.Build(S(), addOutfit).Count == 1 && HistoryText.Build(S(), addOutfit)[0].Summary.EndsWith("を追加"), "Added outfit subtree collapsed into one log row");
        Check(HistoryText.Build(addOutfit, S()).Count == 1 && HistoryText.Build(addOutfit, S())[0].Summary.EndsWith("を削除"), "Removed outfit subtree collapsed into one log row");
        Check(HistoryDiff.Compare(S(), addOutfit).Count == 4, "Log aggregation preserves underlying snapshot data");
        var addComponent = S(Named("exists", "あり", "コンポーネント", "VRC.PhysBone [0] / 存在"),
            Named("pull", "0.5", "コンポーネント", "VRC.PhysBone [0] / pull"));
        Check(HistoryText.Build(S(), addComponent).Count == 1, "New component produces one concise row");
        var mixed = S(addOutfit.entries.Concat(new[] { Named("outside", "90", "BlendShape", "Renderer [0] / 0: Face", "./Body[0]") }).ToArray());
        Check(HistoryText.Build(S(), mixed).Count == 2, "Subtree collapse retains unrelated changes");
        var shapeWithSlash = new Change { before = Named("s", "0", "BlendShape", "Renderer [0] / 4: Smile / Wide"), after = Named("s", "50", "BlendShape", "Renderer [0] / 4: Smile / Wide") };
        Check(HistoryText.Describe("Avatar", shapeWithSlash).Contains("「Smile / Wide」"), "BlendShape names containing separators preserved");
        HistoryEntry Reference(string key, string assetPath, string owner)
        {
            var entry = Named(key, "Asset (" + assetPath + "; 0123456789abcdef0123456789abcdef:2100000)", "コンポーネント", "Renderer [0] / " + key, owner);
            entry.referencePath = assetPath;
            return entry;
        }
        HistoryEntry Asset(string path, string hash = "hash") => Named("asset:" + path, hash, "参照アセット", "Asset / 保存済み内容の識別値", path);
        const string mat = "Assets/衣装 (Blue)/Body (コピー).mat", tex = "Assets/衣装/Texture.png";
        var outfitAssets = S(addOutfit.entries.Concat(new[] {
            Reference("mat", mat, "./Outfit[0]/Ribbon[0]"), Asset(mat),
            Reference("tex", tex, "./Outfit[0]/Ribbon[0]"), Asset(tex)
        }).ToArray());
        var grouped = HistoryText.Build(S(), outfitAssets);
        Check(grouped.Count == 1 && grouped[0].change.Entry.key == "object", "Added hierarchy and referenced assets form one parent log");
        Check(grouped[0].NestedItems.Count == 3 && grouped[0].NestedItems.Count(c => c.Entry.category == "参照アセット") == 2,
            "Parent details retain child object and both assets");
        Check(grouped[0].details.Count == HistoryDiff.Compare(S(), outfitAssets).Count, "Grouping retains every underlying added change");
        var removedGroup = HistoryText.Build(outfitAssets, S());
        Check(removedGroup.Count == 1 && removedGroup[0].NestedItems.Count == 3 && removedGroup[0].NestedItems.All(c => c.Kind == "削除"),
            "Removed hierarchy uses old owners and keeps children in details");
        Check(HistoryText.Matches(grouped[0], "オブジェクト", "Texture.png"), "Object filter and child asset filename search find parent row");
        Check(HistoryText.Matches(grouped[0], null, "Ribbon"), "Child object search finds collapsed parent");
        Check(!HistoryText.Matches(grouped[0], "参照アセット", "does-not-exist"), "Unmatched nested search excluded");
        string groupedExport = HistoryText.Export(grouped[0]);
        Check(groupedExport.Contains(mat) && groupedExport.Contains(tex) && groupedExport.Contains("Ribbon"), "Text export contains grouped child and asset details");
        var duplicateRef = S(outfitAssets.entries.Concat(new[] { Reference("mat2", mat, "./Outfit[0]") }).ToArray());
        Check(HistoryText.Build(S(), duplicateRef).Single().NestedItems.Count(c => c.Entry.path == mat) == 1,
            "Shared material appears once within a parent");
        var secondOutfit = Named("secondRoot", "あり", "オブジェクト", "Object / 存在", "./Second[0]");
        var shared = S(outfitAssets.entries.Concat(new[] { secondOutfit, Reference("sharedMat", mat, "./Second[0]") }).ToArray());
        var sharedGroups = HistoryText.Build(S(), shared);
        Check(sharedGroups.Count == 2 && sharedGroups.All(g => g.NestedItems.Any(c => c.Entry.path == mat)),
            "Two separate added parents each expose their shared material");
        var existingBefore = S(Reference("oldReference", mat, "./Body[0]"), Asset(mat));
        var existingAfter = S(Reference("newReference", tex, "./Body[0]"), Asset(tex));
        var referenceChanges = HistoryText.Build(existingBefore, existingAfter).Where(g => g.referenceOwner != null).ToList();
        Check(referenceChanges.Count == 1 && referenceChanges[0].referenceOwner == "./Body[0]" && referenceChanges[0].NestedItems.Count == 2,
            "Reference additions and removals on an existing object share one parent asset row");
        var changedFile = S(Reference("oldReference", mat, "./Body[0]"), Asset(mat, "different hash"));
        var contentLog = HistoryText.Build(existingBefore, changedFile);
        Check(contentLog.Count == 1 && contentLog[0].referenceOwner == null && contentLog[0].change.Kind == "変更",
            "Asset content modification remains an independent change");
        var unknownReferences = HistoryText.Build(S(), S(Asset(mat), Asset(tex)));
        Check(unknownReferences.Count == 1 && unknownReferences[0].NestedItems.Count == 2,
            "Unattributed assets use one avatar-level group without data loss");
        var prefixCollision = S(addOutfit.entries.Concat(new[] { Named("neighbor", "あり", "オブジェクト", "Object / 存在", "./Outfit[0]Extra") }).ToArray());
        Check(HistoryText.Build(S(), prefixCollision).Count == 2, "Grouping respects path boundaries rather than string prefixes");
        var metadataOnly = S(existingBefore.entries.Select(e => new HistoryEntry { key = e.key, category = e.category, path = e.path, item = e.item, value = e.value }).ToArray());
        Check(HistoryDiff.Compare(existingBefore, metadataOnly).Count == 0, "Adding optional ownership metadata creates no false change");
        HistoryEntry MaterialEntry(string key, string property, string value) => Named(key, value, "マテリアル", "Renderer [0] / Material 0 / " + property, ".");
        var assignment = MaterialEntry("assignment", "割り当て", "Body.mat");
        var namedBefore = S(assignment, MaterialEntry("named", "ShaderProperty/_Glossiness", "0"));
        var namedAfter = S(assignment, MaterialEntry("named", "ShaderProperty/_Glossiness", "0.75"));
        namedAfter.entries.Last().materialName = "Body";
        var materialChanges = HistoryDiff.Compare(namedBefore, namedAfter);
        Check(materialChanges.Count == 1 && materialChanges[0].Entry.key == "named", "Material numeric change appears once without raw serialized duplicates");
        Check(HistoryText.Describe("Avatar", materialChanges[0]) == "Avatar：マテリアル「Body」の「_Glossiness」が 0 → 0.75 に変更", "Material log includes material, property and before/after values");
        var newEmpty = S();
        Check(HistoryDiff.Compare(newEmpty, namedAfter).Any(c => c.Entry.key == "named" && c.Kind == "追加"), "New material slot retains named values");
        Check(HistoryDiff.Compare(namedAfter, newEmpty).Any(c => c.Entry.key == "named" && c.Kind == "削除"), "Removed material slot retains named values");
        Check(HistoryDiff.Compare(namedAfter, namedAfter).Count == 0, "Unchanged named material creates no noise");
        Check(!HistoryText.Matches(grouped[0], "マテリアル", ""), "Material filter excludes added parent with nested materials");
        Check(!HistoryText.Matches(removedGroup[0], "マテリアル", ""), "Material filter excludes removed parent with nested materials");
        var materialRow = HistoryText.Build(namedBefore, namedAfter).Single();
        Check(HistoryText.Matches(materialRow, "マテリアル", "_Glossiness"), "Material filter retains numeric edits and search");
        Check(HistoryText.Matches(grouped[0], "オブジェクト", "") && HistoryText.Matches(grouped[0], null, ""), "Hierarchy additions remain in object and all filters");
        Check(!HistoryText.Matches(grouped[0], "参照アセット", ""), "Hidden reference filter follows visible category too");
        var componentRow = new Activity { after = namedAfter, change = new Change { after = Named("renderer", "あり", "コンポーネント", "Renderer [0] / 存在", ".") } };
        componentRow.details.Add(new Change { after = assignment });
        Check(!HistoryText.Matches(componentRow, "マテリアル", ""), "Material filter excludes renderer addition containing material details");
        var repeatedProperties = new[] { "_DummyProperty", "_Color", "_DummyProperty", "_Glossiness", "_DummyProperty" };
        Check(SnapshotCapture.UniqueShaderPropertyIndices(repeatedProperties).SequenceEqual(new[] { 0, 1, 3 }), "Repeated _DummyProperty is captured once without losing real properties");
        Check(SnapshotCapture.UniqueShaderPropertyIndices(new[] { "_Color", "_color", "_Color" }).SequenceEqual(new[] { 0, 1 }), "Shader property deduplication preserves case-sensitive names");
        string Localize(string key) => key == "sNormalMap" ? "ノーマルマップ" : key == "sNormalMap2nd" ? "ノーマルマップ2nd" : key == "sColor" ? "色" : key;
        Check(MaterialLabels.Resolve("_BumpScale", "Scale", true, Localize) == "ノーマルマップ", "lilToon normal strength uses inspector texture row label");
        Check(MaterialLabels.Resolve("_Bump2ndScale", "Scale", true, Localize) == "ノーマルマップ2nd", "Second normal map remains distinguishable");
        Check(MaterialLabels.Resolve("_Color", "sColor", true, Localize) == "色", "lilToon localization token resolved");
        Check(MaterialLabels.Resolve("_Mode", "Mode|A|B", true, Localize) == "Mode", "Enum choices are not included in property label");
        Check(MaterialLabels.Resolve("_Value", "独自の強さ", false, null) == "独自の強さ", "Other shaders retain their declared inspector description");
        Check(MaterialLabels.Resolve("_BumpMap/Scale", "Normal Map", true, Localize) == "ノーマルマップ / タイリング", "Texture tiling is distinct from normal strength");
        Check(MaterialLabels.Resolve("_BumpScale", "Scale", true, null) == "ノーマルマップ", "Normal map label remains readable without loaded localization");
        var normalBefore = MaterialEntry("normal", "ShaderProperty/_BumpScale", "1");
        var normalAfter = MaterialEntry("normal", "ShaderProperty/_BumpScale", "0.5");
        normalAfter.materialName = "Body";
        normalAfter.displayName = "ノーマルマップ";
        normalAfter.numericValue = true;
        var normalRow = new Activity { after = S(normalAfter), change = new Change { before = normalBefore, after = normalAfter } };
        normalRow.details.Add(normalRow.change);
        Check(normalRow.Summary == "Test：マテリアル「Body」の「ノーマルマップ」の数値が 1 → 0.5 に変更", "Numeric log uses readable inspector label");
        Check(HistoryText.Matches(normalRow, "マテリアル", "ノーマルマップ") && HistoryText.Matches(normalRow, "マテリアル", "_BumpScale"), "Readable and internal property names both searchable");
        var metadataEntry = MaterialEntry("normal", "ShaderProperty/_BumpScale", "0.5");
        Check(HistoryDiff.Compare(S(normalAfter), S(metadataEntry)).Count == 0, "Label metadata changes do not create value changes");
        metadataEntry.materialName = "Body";
        rejected = false;
        try { HistoryDiff.Compare(S(), S(E("x", "1"), E("x", "1"))); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Optimized diff still rejects duplicate keys in the newer snapshot");
        var unchangedLarge = S(Enumerable.Range(0, 10000).Select(i => E("large" + i, "same")).ToArray());
        Check(HistoryDiff.Compare(unchangedLarge, unchangedLarge).Count == 0 && HistoryText.Build(unchangedLarge, unchangedLarge).Count == 0, "Large unchanged history yields no difference or activity");
        var sample = new MaterialSample { property = 1, x = 1, type = UnityEngine.Rendering.ShaderPropertyType.Float };
        var edited = sample; edited.x = 1.0000001f;
        Check(!sample.Same(edited), "Numeric polling detects one-ULP float change without approximate comparison");
        edited = sample; edited.x = float.NaN;
        Check(edited.Same(edited) && !sample.Same(edited), "Stable NaN does not create endless polling changes");
        sample.type = UnityEngine.Rendering.ShaderPropertyType.Int; sample.integer = 16777216;
        edited = sample; edited.integer++;
        Check(!sample.Same(edited), "Integer polling preserves values beyond float integer precision");
        sample = new MaterialSample { property = 2, integer = 45, type = UnityEngine.Rendering.ShaderPropertyType.Texture, scaleX = 1, scaleY = 1 };
        edited = sample; edited.integer = 46;
        Check(!sample.Same(edited), "Texture instance replacement detected");
        edited = sample; edited.offsetY = 0.00001f;
        Check(!sample.Same(edited), "Texture offset edit detected");
        edited = sample; edited.scaleX = 2;
        Check(!sample.Same(edited), "Texture tiling edit detected");
        edited = sample; edited.property = 3;
        Check(!sample.Same(edited), "Shader property identity change detected");
        edited = sample; edited.type = UnityEngine.Rendering.ShaderPropertyType.Vector;
        Check(!sample.Same(edited), "Shader property type change detected");
        sample = new MaterialSample { property = 4, type = UnityEngine.Rendering.ShaderPropertyType.Color, x = 1, y = 1, z = 1, w = 1 };
        edited = sample; edited.w = 0.5f;
        Check(!sample.Same(edited) && sample.Same(sample), "Color alpha edit detected and unchanged value stays stable");
        HistoryEntry CacheEntry(string type, string property, string value) => Named(type + property, value, "コンポーネント", type + " [0] / " + property);
        const string constraint = "VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint";
        Check(RecordingFields.IsConstraintCache(constraint, "cachedExecutionGroupIndex") &&
            RecordingFields.IsConstraintCache(constraint, "latestValidExecutionGroupIndex") &&
            !RecordingFields.IsConstraintCache(constraint, "Weight") &&
            !RecordingFields.IsConstraintCache("Custom.Component", "cachedExecutionGroupIndex"),
            "Capture skips only known internal constraint cache fields");
        Check(HistoryDiff.Compare(S(CacheEntry(constraint, "Weight", "1")), S(CacheEntry(constraint, "Weight", "0.5"))).Count == 1, "User-facing constraint settings remain recorded");
        Check(HistoryDiff.Compare(S(CacheEntry("Custom.Component", "cachedExecutionGroupIndex", "3")), S(CacheEntry("Custom.Component", "cachedExecutionGroupIndex", "4"))).Count == 1, "Same property name on unrelated component is preserved");
        const string descriptor = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        Check(new[] { "animationHashSet", "animationHashSet.Array.size", "animationHashSet.Array.data[100].hash", "animationHashSet.Array.data[100].name" }
            .All(property => RecordingFields.IsAvatarDebugCache(descriptor, property)),
            "SDK generated animation debug hashes are excluded including size, names and values");
        Check(!RecordingFields.IsAvatarDebugCache(descriptor, "animationHashSetOverride") &&
            !RecordingFields.IsAvatarDebugCache(descriptor, "baseAnimationLayers.Array.data[0].animatorController") &&
            !RecordingFields.IsAvatarDebugCache("Custom.Descriptor", "animationHashSet"),
            "Animation layer settings and similarly named custom fields remain recordable");
        var debugEntry = CacheEntry(descriptor, "animationHashSet.Array.data[100].hash", "123");
        Check(RecordingFields.IsAvatarDebugEntry(debugEntry) &&
            !RecordingFields.IsAvatarDebugEntry(CacheEntry("Custom.Descriptor", "animationHashSet.Array.data[100].hash", "123")) &&
            !RecordingFields.IsAvatarDebugEntry(CacheEntry(descriptor, "animationHashSetOverride", "1")),
            "Historical cache filtering matches only exact SDK component and property boundaries");
        var malformedOwner = CacheEntry(descriptor, "animationHashSet.Array.size", "1");
        malformedOwner.item = descriptor + " [custom] / animationHashSet.Array.size";
        Check(!RecordingFields.IsAvatarDebugEntry(malformedOwner), "Similar serialized owner text is not treated as an SDK component");
        var validationBefore = S(Enumerable.Range(0, 71).Select(i => CacheEntry(descriptor, "animationHashSet.Array.data[" + i + "].hash", "1")).ToArray());
        var validationAfter = S(Enumerable.Range(0, 71).Select(i => CacheEntry(descriptor, "animationHashSet.Array.data[" + i + "].hash", "2")).ToArray());
        validationBefore.entries.RemoveAll(RecordingFields.IsAvatarDebugEntry);
        validationAfter.entries.RemoveAll(RecordingFields.IsAvatarDebugEntry);
        Check(!HistoryDiff.HasChanges(validationBefore, validationAfter) && HistoryText.Build(validationBefore, validationAfter).Count == 0,
            "SDK validation-only hash regeneration neither consumes a new snapshot nor produces 71 log rows");
        HistoryEntry[] MaterialAt(string path, string identity, string value, int slot = 0)
        {
            string owner = "UnityEngine.SkinnedMeshRenderer [0] / Material " + slot;
            var valueEntry = Named(path + slot + "value", value, "マテリアル", owner + " / ShaderProperty/_BumpScale", path);
            valueEntry.materialName = "Same name"; valueEntry.displayName = "ノーマルマップ"; valueEntry.numericValue = true;
            return new[] { Named(path + slot + "assignment", "Same name (Assets/Body.mat; " + identity + ")", "マテリアル", owner + " / 割り当て", path), valueEntry };
        }
        const string matId = "0123456789abcdef0123456789abcdef:2100000";
        Snapshot Shared(string front, string back, string backId = matId) => S(MaterialAt("./front[0]", matId, front).Concat(MaterialAt("./back[0]", backId, back)).ToArray());
        var sharedBefore = Shared("1", "1"); var sharedAfter = Shared("2", "2");
        var sharedRows = HistoryText.Build(sharedBefore, sharedAfter);
        Check(sharedRows.Count == 1 && sharedRows[0].SharedMaterial && sharedRows[0].details.Count == 2, "Shared material property becomes one row with two reference locations");
        Check(HistoryText.Matches(sharedRows[0], "マテリアル", "front") && HistoryText.Matches(sharedRows[0], "マテリアル", "back"), "All merged material locations remain searchable");
        string sharedText = HistoryText.Export(sharedRows[0]);
        Check(sharedText.Contains("front") && sharedText.Contains("back") && sharedText.Contains("変更前: 1") && sharedText.Contains("変更後: 2"), "Shared material export retains values and all reference locations");
        var separateId = "fedcba9876543210fedcba9876543210:2100000";
        Check(HistoryText.Build(Shared("1", "1", separateId), Shared("2", "2", separateId)).Count == 2, "Same material name with different GUIDs is not merged");
        var subassetId = "0123456789abcdef0123456789abcdef:2100001";
        Check(HistoryText.Build(Shared("1", "1", subassetId), Shared("2", "2", subassetId)).Count == 2, "Sub-assets in the same file remain distinct by local ID");
        Check(HistoryText.Build(Shared("0", "1"), Shared("2", "2")).Single().materialItems.Count == 2, "Different before values remain distinct within one material row");
        Check(HistoryText.Build(Shared("1", "1"), Shared("2", "3")).Single().materialItems.Count == 2, "Different after values remain distinct within one material row");
        Check(HistoryText.Build(Shared("1", "1", "unknown"), Shared("2", "2", "unknown")).Count == 2, "Unknown material identity is not guessed from name or path");
        var slotsBefore = S(MaterialAt("./Body[0]", matId, "1", 0).Concat(MaterialAt("./Body[0]", matId, "1", 1)).ToArray());
        var slotsAfter = S(MaterialAt("./Body[0]", matId, "2", 0).Concat(MaterialAt("./Body[0]", matId, "2", 1)).ToArray());
        Check(HistoryText.Build(slotsBefore, slotsAfter).Single().details.Count == 2, "Multiple slots on one object are listed in shared material details");
        Snapshot ColorEdit(string value, string hash)
        {
            var entries = MaterialAt("./Body[0]", matId, value).ToList();
            entries[0].referencePath = "Assets/Body.mat";
            entries[1].item = "UnityEngine.SkinnedMeshRenderer [0] / Material 0 / ShaderProperty/_Color";
            entries[1].displayName = "メインカラー";
            entries.Add(Named("emission", value, "マテリアル", "UnityEngine.SkinnedMeshRenderer [0] / Material 0 / ShaderProperty/_EmissionColor", "./Body[0]"));
            entries.Last().materialName = "Same name"; entries.Last().displayName = "発光色";
            entries.Add(Named("keywords", value, "マテリアル", "UnityEngine.SkinnedMeshRenderer [0] / Material 0 / m_ValidKeywords.Array.size", "./Body[0]"));
            entries.Add(Asset("Assets/Body.mat", hash));
            return S(entries.ToArray());
        }
        var colorBefore = ColorEdit("white", "hash-before");
        var colorAfter = ColorEdit("red", "hash-after");
        var colorBatch = HistoryText.Build(colorBefore, colorAfter).Single();
        Check(colorBatch.MaterialBatch && colorBatch.materialItems.Count == 4 && colorBatch.materialReferenceCount == 1,
            "Three material fields and matching saved asset form one four-item row");
        Check(HistoryText.CardTitle(colorBatch).Contains("4項目") && !HistoryText.CardValue(colorBatch).Contains("white"),
            "Multi-property card shows count without implying one representative value");
        Check(HistoryText.Matches(colorBatch, "マテリアル", "発光色 white") && HistoryText.Matches(colorBatch, null, "hash-after"),
            "Material batch search includes all labels, before values and attached asset details");
        string colorExport = HistoryText.Export(colorBatch);
        Check(colorExport.Contains("_Color") && colorExport.Contains("_EmissionColor") && colorExport.Contains("m_ValidKeywords") &&
            colorExport.Contains("hash-before") && colorExport.Contains("hash-after"), "Material batch export preserves all property and asset changes");
        Check(!new ActivityDisplay(colorBatch).grouped, "Attached material hash is not presented as an object hierarchy group");
        var anotherAfter = ColorEdit("blue", "hash-third");
        Check(!ReferenceEquals(colorBatch, HistoryText.Build(colorAfter, anotherAfter).Single()) && colorBatch.after == colorAfter,
            "Successive records remain independent material activities");
        var assetOnly = ColorEdit("white", "hash-new");
        Check(HistoryText.Build(colorBefore, assetOnly).Single().change.Entry.category == "参照アセット",
            "Asset-only updates are retained when no material value change exists");
        var unrelatedAfter = ColorEdit("red", "hash-after");
        colorBefore.entries.Add(Asset("Assets/Other.mat", "old")); unrelatedAfter.entries.Add(Asset("Assets/Other.mat", "new"));
        Check(HistoryText.Build(colorBefore, unrelatedAfter).Count == 2, "Unrelated asset change is never swallowed by material grouping");
        var addedProperty = ColorEdit("white", "hash-before");
        addedProperty.entries.Add(Named("new-property", "2", "マテリアル", "UnityEngine.SkinnedMeshRenderer [0] / Material 0 / ShaderProperty/_New", "./Body[0]"));
        var cleanBefore = ColorEdit("white", "hash-before");
        Check(HistoryText.Build(cleanBefore, addedProperty).Single().change.Kind == "追加" &&
            HistoryText.Build(addedProperty, cleanBefore).Single().change.Kind == "削除", "Added and removed shader properties retain their actions");
        Check(!HistoryText.Export(grouped[0]).Contains("m_LocalPosition") && HistoryText.Export(grouped[0]).Contains("子オブジェクト"), "Hierarchy export lists children and assets without raw component settings");
        Check(HistoryText.Export(removedGroup[0]).Contains("[削除]") && !HistoryText.Export(removedGroup[0]).Contains("変更前:"), "Removed hierarchy export stays compact without losing child deletion entries");
        Check(grouped[0].details.Count == HistoryDiff.Compare(S(), outfitAssets).Count, "Compact export leaves complete original activity details intact");
        var display = new ActivityDisplay(grouped[0]);
        Check(display.target == HistoryText.TableTarget(grouped[0]) && display.preview.rows.Single().displayAfter == "あり" &&
            display.nested.SequenceEqual(grouped[0].NestedItems), "Cached page presentation preserves target, presence values and sorted details");
        Check(object.ReferenceEquals(display.nested, display.nested) && display.grouped && display.counts.Contains("子オブジェクト"), "Repeated presentation access reuses nested list and counts");
        Check(HistoryStore.HeaderJson("{\"id\":\"x\",\"note\":\"fake , \\\"entries\\\": [\",\"entries\":[1]}") ==
            "{\"id\":\"x\",\"note\":\"fake , \\\"entries\\\": [\"}", "Header reader ignores entries marker escaped inside a note");
        Check(HistoryStore.HeaderJson("{\"x\":{\"a\":1,\"entries\":[]},\"entries\":[]}") == "{\"x\":{\"a\":1,\"entries\":[]}}", "Header reader ignores nested entries fields");
        Check(HistoryStore.HeaderJson("{\"entries\":[]}") == null, "Unknown field order uses full-read fallback");
        string testDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "acl-latest-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(testDirectory);
        try
        {
            int fullReads = 0;
            Snapshot ParseFixture(string json)
            {
                bool full = json.Contains("\"entries\"");
                if (full) fullReads++;
                string Field(string name) => System.Text.RegularExpressions.Regex.Match(json, "\\\"" + name + "\\\":\\\"([^\\\"]*)\\\"").Groups[1].Value;
                var record = S(); record.id = Field("id"); record.avatarId = Field("avatarId"); record.createdUtc = Field("createdUtc");
                if (full && json.Contains("BROKEN")) throw new System.IO.InvalidDataException();
                return record;
            }
            string Record(int number, bool broken = false) => "{\"id\":\"" + number.ToString("x32") + "\",\"avatarId\":\"avatar-a\",\"createdUtc\":\"2026-09-17T00:00:" + number.ToString("00") + "Z\",\"entries\":[\"" + (broken ? "BROKEN" : new string('x', 10000)) + "\"]}";
            for (int i = 1; i <= 8; i++) System.IO.File.WriteAllText(System.IO.Path.Combine(testDirectory, i + ".json"), Record(i));
            var latest = HistoryStore.LoadLatestFrom(testDirectory, "avatar-a", ParseFixture);
            Check(latest.id == 8.ToString("x32") && fullReads == 1, "Latest load fully parses only newest record despite large historical entries");
            System.IO.File.WriteAllText(System.IO.Path.Combine(testDirectory, "9.json"), Record(9, true)); fullReads = 0;
            latest = HistoryStore.LoadLatestFrom(testDirectory, "avatar-a", ParseFixture);
            Check(latest.id == 8.ToString("x32") && fullReads == 2, "Corrupt latest record falls back to previous valid snapshot");
            Check(HistoryStore.LoadLatestFrom(testDirectory, "different-avatar", ParseFixture) == null, "Latest load rejects foreign avatar histories");
            Snapshot ParseOldDebugFixture(string json)
            {
                var record = ParseFixture(json);
                record.entries.Add(CacheEntry(descriptor, "animationHashSet.Array.size", "127"));
                record.entries.Add(CacheEntry(descriptor, "ViewPosition", "0, 1, 0"));
                return record;
            }
            string originalHistoryText = System.IO.File.ReadAllText(System.IO.Path.Combine(testDirectory, "8.json"));
            latest = HistoryStore.LoadLatestFrom(testDirectory, "avatar-a", ParseOldDebugFixture);
            Check(latest.entries.Count == 1 && latest.entries[0].item.EndsWith("ViewPosition") &&
                !HistoryDiff.HasChanges(latest, S(CacheEntry(descriptor, "ViewPosition", "0, 1, 0"))),
                "Loading a pre-fix baseline removes debug cache without generating false deletion records");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(testDirectory, "8.json")) == originalHistoryText,
                "Historical debug cache filtering never rewrites the saved JSON file");
            string retentionDirectory = System.IO.Path.Combine(testDirectory, "retention");
            System.IO.Directory.CreateDirectory(retentionDirectory);
            string RetentionFile(int i) => System.IO.Path.Combine(retentionDirectory, i.ToString("x32") + ".json");
            for (int i = 1; i <= 8; i++) System.IO.File.WriteAllText(RetentionFile(i), Record(i));
            System.IO.File.WriteAllText(RetentionFile(9), Record(9, true));
            System.IO.File.WriteAllText(System.IO.Path.Combine(retentionDirectory, "unknown.json"), Record(1));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(retentionDirectory, "BuildErrors"));
            string oldError = System.IO.Path.Combine(retentionDirectory, "BuildErrors/old.json");
            System.IO.File.WriteAllText(oldError, "keep");
            var retainedFiles = HistoryStore.PruneFrom(retentionDirectory, "avatar-a", 3, ParseFixture);
            Check(retainedFiles.SetEquals(System.IO.Directory.GetFiles(retentionDirectory, "*.json").Select(System.IO.Path.GetFileName)),
                "Retention result exactly matches remaining files including preserved damaged and unknown files");
            Check(System.IO.File.Exists(RetentionFile(6)) && System.IO.File.Exists(RetentionFile(7)) && System.IO.File.Exists(RetentionFile(8)) &&
                !System.IO.File.Exists(RetentionFile(5)), "Retention keeps newest valid snapshots and removes older ones");
            Check(System.IO.File.Exists(RetentionFile(9)) && System.IO.File.Exists(oldError) &&
                System.IO.File.Exists(System.IO.Path.Combine(retentionDirectory, "unknown.json")), "Retention preserves damaged files, unknown names and error subdirectory");
            Check(HistoryStore.LoadLatestFrom(retentionDirectory, "avatar-a", ParseFixture).id == 8.ToString("x32"), "Retention preserves latest comparison baseline despite newer corrupt record");
            fullReads = 0;
            HistoryStore.PruneFrom(retentionDirectory, "avatar-a", 0, ParseFixture);
            Check(!System.IO.File.Exists(RetentionFile(6)) && System.IO.File.Exists(RetentionFile(7)) && System.IO.File.Exists(RetentionFile(8)), "Retention enforces minimum of two valid snapshots");
            Check(fullReads == 1, "Unchanged validated retention files reuse metadata; only corrupt record reparsed");
            HistoryStore.PruneFrom(retentionDirectory, "different-avatar", 2, ParseFixture);
            Check(System.IO.File.Exists(RetentionFile(7)) && System.IO.File.Exists(RetentionFile(8)), "Retention never removes another avatar's records");
            System.IO.File.WriteAllText(RetentionFile(10), Record(10));
            retainedFiles = HistoryStore.PruneFrom(retentionDirectory, "avatar-a", 2, ParseFixture);
            Check(!System.IO.File.Exists(RetentionFile(7)) && System.IO.File.Exists(RetentionFile(8)) && System.IO.File.Exists(RetentionFile(10)), "New saved snapshot rolls retention forward");
            Check(retainedFiles.Contains(System.IO.Path.GetFileName(RetentionFile(10))) &&
                !retainedFiles.Contains(System.IO.Path.GetFileName(RetentionFile(7))), "Saved-file notification excludes pruned records and includes the latest record");
            System.IO.File.Delete(RetentionFile(8));
            retainedFiles = HistoryStore.PruneFrom(retentionDirectory, "avatar-a", 2, ParseFixture);
            Check(!retainedFiles.Contains(System.IO.Path.GetFileName(RetentionFile(8))), "Retention result also reconciles files deleted externally");
            if (System.IO.Path.DirectorySeparatorChar == '\\')
            {
                string lockedDirectory = System.IO.Path.Combine(testDirectory, "locked-retention");
                System.IO.Directory.CreateDirectory(lockedDirectory);
                string LockedFile(int i) => System.IO.Path.Combine(lockedDirectory, i.ToString("x32") + ".json");
                for (int i = 1; i <= 8; i++) System.IO.File.WriteAllText(LockedFile(i), Record(i));
                HashSet<string> partialResult = null;
                string partialWarning = null;
                using (var locked = new System.IO.FileStream(LockedFile(4), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                {
                    bool complete = AutoRecorder.TryRetention(() => partialResult = HistoryStore.PruneFrom(lockedDirectory, "avatar-a", 3, ParseFixture),
                        warning => partialWarning = warning);
                    Check(!complete && partialResult == null && partialWarning != null && !System.IO.File.Exists(LockedFile(5)) &&
                        System.IO.File.Exists(LockedFile(4)) && System.IO.File.Exists(LockedFile(8)),
                        "Partial retention failure reports a warning and no misleading complete file list");
                }
                partialResult = HistoryStore.PruneFrom(lockedDirectory, "avatar-a", 3, ParseFixture);
                Check(partialResult.SetEquals(new[] { 6, 7, 8 }.Select(i => System.IO.Path.GetFileName(LockedFile(i)))),
                    "Retry after a locked-file failure returns the correct retained set");
            }
        }
        finally { System.IO.Directory.Delete(testDirectory, true); }
        foreach (string filter in new[] { "BlendShape", "Transform", "コンポーネント" })
        {
            Check(!HistoryText.Matches(grouped[0], filter, "") && !HistoryText.Matches(removedGroup[0], filter, ""), "Hierarchy rows excluded from " + filter);
        }
        Check(HistoryText.Matches(componentRow, "コンポーネント", ""), "Standalone component addition remains in component filter");
        var componentBefore = S(E("Fixture / weight", "1", "コンポーネント"));
        var componentAfter = S(E("Fixture / weight", "2", "コンポーネント"));
        var componentEdit = HistoryText.Build(componentBefore, componentAfter).Single();
        Check(HistoryText.Matches(componentEdit, "コンポーネント", "weight") && !HistoryText.Matches(componentEdit, "オブジェクト", ""), "Component setting edits stay distinct from object changes");
        Check(HistoryText.Matches(grouped[0], "オブジェクト", "Ribbon") && !HistoryText.Matches(grouped[0], "コンポーネント", "Ribbon"), "Nested name search respects selected visible category");
        Check(HistoryStore.RecordLimit == 100, "Production retention limit is fixed at 100");
        Check(HistoryText.Matches(normalRow, null, " ノーマルマップ "), "Search ignores surrounding spaces");
        Check(HistoryText.Matches(normalRow, null, "Body ノーマルマップ"), "Search matches multiple words across displayed fields");
        Check(HistoryText.Matches(normalRow, null, "Ｂｏｄｙ　ノーマルマップ"), "Search normalizes full-width Latin and spaces");
        var transformSearch = HistoryText.Build(S(E("Transform / m_LocalPosition", "0", "Transform", "./Body[0]")), S(E("Transform / m_LocalPosition", "1", "Transform", "./Body[0]"))).Single();
        Check(HistoryText.Matches(transformSearch, null, "トランスフォーム"), "Displayed Japanese category is searchable");
        Check(HistoryText.Matches(transformSearch, null, HistoryText.CardTitle(transformSearch)), "Displayed card title is searchable verbatim");
        Check(!HistoryText.Matches(normalRow, null, "Body 存在しない名前"), "Every query word must match");
        Check(!HistoryText.Matches(normalRow, "コンポーネント", "Body"), "Search still respects selected category");
        string retentionStatus = null;
        Check(!AutoRecorder.TryRetention(() => { throw new System.IO.IOException("locked"); }, message => retentionStatus = message) &&
            retentionStatus.Contains("locked"), "Retention failure remains visible after a saved record");
        Check(AutoRecorder.TryRetention(() => { }, message => retentionStatus = message) && retentionStatus == null,
            "Successful retention retry clears the previous warning");
        string smallDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "acl-small-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(smallDirectory);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(smallDirectory, "record.json"), "unparsed");
            int parses = 0;
            var smallFiles = HistoryStore.PruneFrom(smallDirectory, "avatar", 100, json => { parses++; throw new Exception(); });
            Check(smallFiles.SetEquals(new[] { "record.json" }), "Below-limit retention returns filenames without parsing snapshot contents");
            Check(HistoryStore.PruneFrom(System.IO.Path.Combine(smallDirectory, "absent"), "avatar", 100,
                json => { throw new Exception(); }).Count == 0, "Missing history directory returns an empty retention result");
            Check(parses == 0 && System.IO.File.Exists(System.IO.Path.Combine(smallDirectory, "record.json")),
                "Below-limit retention skips JSON parsing and leaves files intact");
        }
        finally { System.IO.Directory.Delete(smallDirectory, true); }
        Check(HistoryText.TableTarget(colorBatch) == "Same name" && HistoryText.TableItem(colorBatch) == "マテリアル" &&
            new ActivityPreview(colorBatch).Text.Contains("メインカラー：white → red") &&
            !new ActivityPreview(colorBatch).Text.Contains("保存情報の更新あり") && HistoryText.Export(colorBatch).Contains("保存情報の更新あり"), "C table shows material settings separately from saved asset updates");
        Check(HistoryText.TableItem(transformSearch) == "位置" && new ActivityPreview(transformSearch).Text == "位置：0 → 1",
            "C table uses readable transform label and before/after values");
        Check(new ActivityPreview(grouped[0]).Text == "オブジェクト：なし → あり" && HistoryText.Export(grouped[0]).Contains("参照アセット: 2 件"),
            "Hierarchy preview shows presence while detail export keeps child and reference counts");
        var tableDisplay = new ActivityDisplay(colorBatch);
        Check(tableDisplay.target == HistoryText.TableTarget(colorBatch) && tableDisplay.category == "マテリアル" &&
            tableDisplay.preview.Text == new ActivityPreview(colorBatch).Text, "Page cache uses the same C table formatting as export");
        var exportRows = new[] { colorBatch, transformSearch, grouped[0] };
        string document = string.Join("\n", HistoryText.ExportDocument(exportRows, "すべて", "Body"));
        Check(document.StartsWith("Avatar Change Log v0.1.1") && document.Contains("検索: Body") && document.Contains("件数: 3"),
            "C export retains release version, filter and result count");
        Check(document.Contains("001 |") && document.Contains("003 |") && document.Contains("詳細 #001") &&
            document.Contains("詳細 #003") && document.IndexOf("003 |", StringComparison.Ordinal) < document.IndexOf("【詳細】", StringComparison.Ordinal),
            "C export places all summary rows before matching numbered details");
        Check(document.Contains("_EmissionColor") && document.Contains("hash-before") && document.Contains("hash-after") && document.Contains("Ribbon"),
            "C export keeps complete material and hierarchy details");
        var unusual = HistoryText.Build(S(E("value", "old", "コンポーネント", "./Body\nline\t|[0]")),
            S(E("value", "new\nline\t|value", "コンポーネント", "./Body\nline\t|[0]"))).Single();
        var unusualLines = HistoryText.ExportDocument(new[] { unusual }, "すべて", "\n\t|").ToList();
        string summaryRow = unusualLines.Single(line => line.StartsWith("001 |", StringComparison.Ordinal));
        Check(!summaryRow.Contains("\n") && !summaryRow.Contains("\t") && summaryRow.Contains("\\n") && summaryRow.Contains("\\|"),
            "Summary escapes line breaks, tabs and delimiters without corrupting the table");
        Check(string.Join("\n", unusualLines).Contains("new\nline\t|value"), "Details preserve exact multiline values");
        var manyRows = Enumerable.Repeat(colorBatch, 45).ToList();
        Check(HistoryText.ExportDocument(manyRows, "マテリアル", "").Any(line => line.StartsWith("045 |", StringComparison.Ordinal)),
            "C export includes matches beyond the visible forty-row page");
        Check(HistoryText.Matches(colorBatch, "マテリアル", "4項目"), "C table count is searchable");
        Snapshot PreviewSnapshot(bool after)
        {
            var result = ColorEdit(after ? "0.5, 0.3, 0.2, 0" : "1, 1, 1, 1", after ? "hash-after" : "hash-before");
            foreach (var entry in result.entries.Where(e => e.item.Contains("ShaderProperty/")))
            { entry.colorValue = true; entry.numericValue = false; }
            var scale = Named("scale", after ? "0.7" : "1", "マテリアル",
                "UnityEngine.SkinnedMeshRenderer [0] / Material 0 / ShaderProperty/_BumpScale", "./Body[0]");
            scale.displayName = "ノーマルマップの強さ"; scale.numericValue = true;
            result.entries.Add(scale);
            return result;
        }
        var previewActivity = HistoryText.Build(PreviewSnapshot(false), PreviewSnapshot(true)).Single();
        var preview = new ActivityDisplay(previewActivity).preview;
        Check(preview.rows.Count == 1 && preview.remaining == 3 && preview.savedInfo,
            "Material preview limits settings to one and excludes saved info from remaining count");
        Check(preview.rows.Take(2).All(r => r.beforeColor != null && r.afterColor != null) &&
            new ActivityPreview(HistoryText.Build(S(MaterialAt("./Body[0]", matId, "1")), S(MaterialAt("./Body[0]", matId, "0.7"))).Single()).rows.Single().displayText == "ノーマルマップ：1.00 → 0.70",
            "Colors and shader settings precede internal material fields with readable numeric formatting");
        Check(preview.rows[0].afterColor[3] == 0 && preview.rows[0].text.Contains("RGBA 1, 1, 1, 1 → 0.5, 0.3, 0.2, 0") &&
            preview.Text.Contains("ほか3項目") && !preview.Text.Contains("保存情報の更新あり"),
            "Preview retains exact RGBA including transparency and explains hidden settings");
        string previewExport = string.Join("\n", HistoryText.ExportDocument(new[] { previewActivity }, "すべて", ""));
        Check(previewExport.Contains("RGBA 1, 1, 1, 1") && previewExport.Contains("ノーマルマップの強さ") &&
            previewExport.Contains("ほか3項目") && previewExport.Contains("m_ValidKeywords") && previewExport.Contains("hash-after"),
            "Export provides readable material summary and complete hidden changes");
        var previewLines = HistoryText.ExportDocument(new[] { previewActivity }, "すべて", "").ToList();
        Check(previewLines.Single(line => line.StartsWith("001 |", StringComparison.Ordinal)).EndsWith("メインカラー") &&
            previewLines.Count(line => line.StartsWith("      変更前:", StringComparison.Ordinal)) == 1 &&
            previewLines.Contains("      変更前: RGBA 1, 1, 1, 1") &&
            previewLines.Contains("      変更後: RGBA 0.5, 0.3, 0.2, 0") &&
            previewLines.Contains("    ほか3項目 → 詳細 #001"),
            "Material export separates setting names and before/after values under one ID with a valid detail reference");
        var unusualMaterial = HistoryText.Build(S(MaterialAt("./Body[0]", matId, "old")),
            S(MaterialAt("./Body[0]", matId, "new\nline\t|value"))).Single();
        var unusualMaterialLines = HistoryText.ExportDocument(new[] { unusualMaterial }, "すべて", "").ToList();
        Check(unusualMaterialLines.Contains("      値を変更（全文は詳細で確認）") &&
            string.Join("\n", unusualMaterialLines).Contains("変更後: new\nline\t|value"),
            "Material summary escapes embedded control characters while details preserve the original value");
        Check(HistoryText.Matches(previewActivity, "マテリアル", "m_ValidKeywords") &&
            HistoryText.Matches(previewActivity, "マテリアル", "保存情報"), "Hidden details and new preview notes remain searchable");
        Check(HistoryText.Matches(previewActivity, "マテリアル", "ノーマルマップ 1.00 0.70"),
            "Search matches the numeric text actually shown in the preview");
        var legacyPreview = new ActivityPreview(colorBatch);
        Check(legacyPreview.rows.All(r => r.beforeColor == null && r.afterColor == null) && legacyPreview.remaining == 2,
            "Existing histories without color metadata retain textual previews");
        var colorEntry = E("color", "2.5, 0.2, 0.3, 0.4", "マテリアル");
        Check(!ChangePreviewRow.TryColor(colorEntry, out _), "Four-component vectors are not inferred to be colors");
        colorEntry.colorValue = true;
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            Check(ChangePreviewRow.TryColor(colorEntry, out var rgba) && rgba[0] == 2.5f && rgba[3] == 0.4f,
                "Color parsing preserves HDR and alpha independent of current locale");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
        Check(new[] { "NaN,0,0,1", "Infinity,0,0,1", "0,0,0", "red", null }.All(value =>
            { colorEntry.value = value; return !ChangePreviewRow.TryColor(colorEntry, out _); }),
            "Malformed colors safely use the textual fallback");
        var tinyBefore = MaterialAt("./Body[0]", matId, "0.000000000001");
        var tinyAfter = MaterialAt("./Body[0]", matId, "0.000000000002");
        var tinyPreview = new ActivityPreview(HistoryText.Build(S(tinyBefore), S(tinyAfter)).Single());
        Check(tinyPreview.rows.Single().displayText.Contains("0.000000000001 → 0.000000000002") && !tinyPreview.savedInfo,
            "Tiny numeric differences remain visible and single settings do not invent saved info");
        var metadataBefore = E("color", "1,1,1,1", "マテリアル");
        var metadataAfter = E("color", "1,1,1,1", "マテリアル"); metadataAfter.colorValue = true;
        Check(HistoryDiff.Compare(S(metadataBefore), S(metadataAfter)).Count == 0,
            "Color display metadata alone never produces a changed-value record");
        var shapePreview = new ActivityPreview(activity.Single()).rows.Single();
        Check(shapePreview.label == "Smile" && shapePreview.displayBefore == "0.00" && shapePreview.displayAfter == "65.00",
            "BlendShape preview uses the shared named before/after presentation");
        var positionActivity = HistoryText.Build(S(Named("p", "0, 1, 2", "Transform", "Transform [0] / m_LocalPosition")),
            S(Named("p", "3, 4, 5", "Transform", "Transform [0] / m_LocalPosition"))).Single();
        var positionPreview = new ActivityPreview(positionActivity).rows.Single();
        Check(positionPreview.label == "位置" && positionPreview.displayBefore == "X 0 / Y 1 / Z 2" && positionPreview.displayAfter == "X 3 / Y 4 / Z 5",
            "Position preview identifies axes without changing stored vector values");
        var enabledActivity = HistoryText.Build(S(Named("e", "False", "コンポーネント", "UnityEngine.BoxCollider [0] / m_Enabled")),
            S(Named("e", "True", "コンポーネント", "UnityEngine.BoxCollider [0] / m_Enabled"))).Single();
        Check(new ActivityPreview(enabledActivity).Text.Contains("無効 → 有効") && HistoryText.Matches(enabledActivity, null, "有効"),
            "Enabled state uses searchable readable values instead of serialized booleans");
        var componentPresence = new ActivityPreview(HistoryText.Build(S(), addComponent).Single()).rows.Single();
        Check(componentPresence.label.Contains("PhysBone") && componentPresence.displayBefore == "なし" && componentPresence.displayAfter == "あり" &&
            HistoryText.Export(HistoryText.Build(S(), addComponent).Single()).Contains("pull"),
            "Component addition shows presence while complete settings remain in details");
        Check(new ActivityPreview(removedGroup.Single()).rows.Single().displayAfter == "なし" &&
            !new ActivityPreview(grouped.Single()).Text.Contains("Ribbon") && HistoryText.Export(grouped.Single()).Contains("Ribbon"),
            "Hierarchy removal shows absence and child names remain in details only");
        var rotationActivity = HistoryText.Build(S(Named("r", "0, 0, 0, 1", "Transform", "Transform [0] / m_LocalRotation")),
            S(Named("r", "0, 0.707, 0, 0.707", "Transform", "Transform [0] / m_LocalRotation"))).Single();
        Check(new ActivityPreview(rotationActivity).Text.Contains("回転を変更") && !new ActivityPreview(rotationActivity).Text.Contains("0.707") &&
            HistoryText.Export(rotationActivity).Contains("0.707") && HistoryText.Matches(rotationActivity, null, "0.707"),
            "Raw quaternion values stay in searchable details and are never presented as degrees");
        var longBefore = Named("long", new string('a', 90) + "old", "コンポーネント", "Example [0] / text");
        var longAfter = Named("long", new string('a', 90) + "new", "コンポーネント", "Example [0] / text");
        var longActivity = HistoryText.Build(S(longBefore), S(longAfter)).Single();
        var longDocument = string.Join("\n", HistoryText.ExportDocument(new[] { longActivity }, "すべて", ""));
        Check(!longDocument.Substring(0, longDocument.IndexOf("【詳細】", StringComparison.Ordinal)).Contains(longAfter.value) &&
            longDocument.Contains(longAfter.value) && HistoryText.Matches(longActivity, null, "new"),
            "Long text is omitted only from summaries and remains complete and searchable in details");
        var refBefore = Reference("r", "Assets/Old.mat", "./Clothes[0]/Body[0]");
        var refAfter = Reference("r", "Assets/New.mat", "./Clothes[0]/Body[0]");
        var refActivity = HistoryText.Build(S(refBefore), S(refAfter)).Single();
        Check(new ActivityPreview(refActivity).rows.Single().message != null &&
            !new ActivityPreview(refActivity).Text.Contains("0123456789") && HistoryText.Export(refActivity).Contains("Assets/New.mat") &&
            HistoryText.TableTarget(refActivity) == "Body[0]" && HistoryText.Matches(refActivity, null, "Clothes"),
            "Same-name reference replacements remain explicit while identifiers and parent paths move to details");
        var internalActivity = HistoryText.Build(S(Named("k", "0", "マテリアル", "Renderer [0] / Material 0 / m_ValidKeywords.Array.size")),
            S(Named("k", "1", "マテリアル", "Renderer [0] / Material 0 / m_ValidKeywords.Array.size"))).Single();
        Check(new ActivityPreview(internalActivity).rows.Count == 0 && !new ActivityPreview(internalActivity).Text.Contains("m_ValidKeywords") &&
            HistoryText.Export(internalActivity).Contains("m_ValidKeywords"), "Internal-only material changes remain visible as a summary with full details");
        var allTypes = new[] { activity.Single(), positionActivity, enabledActivity, grouped.Single(), removedGroup.Single(), contentLog.Single(), longActivity, internalActivity };
        var unifiedLines = HistoryText.ExportDocument(allTypes, "すべて", "").ToList();
        Check(unifiedLines.Count(line => line.Contains(" | ") && char.IsDigit(line[0])) == allTypes.Length &&
            unifiedLines.Contains("      変更前: 0.00") && unifiedLines.Contains("      変更後: 65.00") &&
            unifiedLines.Contains("      変更前: なし") && unifiedLines.Contains("      変更後: なし"),
            "All log categories share the compact export layout with consistent IDs and presence values");
        var pausedIds = new List<string>();
        int pauseSaves = 0;
        RecordingPauseState.Change(pausedIds, "avatar-a", true, () => pauseSaves++);
        RecordingPauseState.Change(pausedIds, "avatar-a", true, () => pauseSaves++);
        Check(pausedIds.SequenceEqual(new[] { "avatar-a" }) && pauseSaves == 1,
            "Pausing is avatar-specific and repeated requests do not write settings twice");
        RecordingPauseState.Change(pausedIds, "avatar-b", true, () => pauseSaves++);
        RecordingPauseState.Change(pausedIds, "avatar-a", false, () => pauseSaves++);
        Check(pausedIds.SequenceEqual(new[] { "avatar-b" }), "Resuming avatar A never resumes paused avatar B");
        bool pauseFailed = false;
        try { RecordingPauseState.Change(pausedIds, "avatar-c", true, () => { throw new System.IO.IOException("locked"); }); }
        catch (System.IO.IOException) { pauseFailed = true; }
        Check(pauseFailed && pausedIds.SequenceEqual(new[] { "avatar-b" }), "Failed pause preference save leaves existing preferences unchanged");
        bool resumeFailed = false;
        try { RecordingPauseState.Change(pausedIds, "avatar-b", false, () => { throw new System.IO.IOException("locked"); }); }
        catch (System.IO.IOException) { resumeFailed = true; }
        Check(resumeFailed && pausedIds.SequenceEqual(new[] { "avatar-b" }), "Failed resume preference save restores the paused preference");
        bool emptyPauseRejected = false;
        try { RecordingPauseState.Change(pausedIds, "", true, () => pauseSaves++); }
        catch (ArgumentException) { emptyPauseRejected = true; }
        Check(emptyPauseRejected && pauseSaves == 3, "An empty avatar cannot create or save a pause preference");
        var beforePause = S(Named("pause-shape", "10", "BlendShape", "Renderer [0] / 0: Smile"));
        var resumeBaseline = S(Named("pause-shape", "80", "BlendShape", "Renderer [0] / 0: Smile"));
        resumeBaseline.startsRecordingSegment = true;
        Check(HistoryText.Build(beforePause, resumeBaseline).Count == 0 && HistoryDiff.Compare(beforePause, resumeBaseline).Count == 1,
            "A resume baseline hides paused edits from logs without mutating the captured values");
        var afterResume = S(Named("pause-shape", "90", "BlendShape", "Renderer [0] / 0: Smile"));
        var resumedRows = HistoryText.Build(resumeBaseline, afterResume);
        Check(resumedRows.Count == 1 && resumedRows[0].change.before.value == "80" && resumedRows[0].change.after.value == "90",
            "The first resumed edit compares against the resume-time value rather than the pre-pause value");
        var pausedAddition = S(E("added-while-paused", "exists")); pausedAddition.startsRecordingSegment = true;
        var pausedDeletion = S(); pausedDeletion.startsRecordingSegment = true;
        Check(HistoryText.Build(S(), pausedAddition).Count == 0 && HistoryText.Build(pausedAddition, pausedDeletion).Count == 0,
            "Added and deleted entries during pauses never become resume log rows");
        Check(!S().startsRecordingSegment && HistoryText.Build(beforePause, afterResume).Count == 1,
            "Existing histories without segment metadata preserve normal change detection");
        bool resumedAlienRejected = false, resumedNullRejected = false;
        var alienResume = S(); alienResume.startsRecordingSegment = true; alienResume.avatarId = "another-avatar";
        try { HistoryText.Build(beforePause, alienResume); } catch (InvalidOperationException) { resumedAlienRejected = true; }
        try { HistoryText.Build(null, resumeBaseline); } catch (ArgumentNullException) { resumedNullRejected = true; }
        Check(resumedAlienRejected && resumedNullRejected, "Skipping a resume diff still rejects null and cross-avatar inputs");
        var cache = new ActivitySearchCache();
        var searchQueries = new[] { "", "white", "発光色 white", "ノーマルマップ 1.00 0.70", "保存情報", "m_ValidKeywords", "unlikely-no-result", "Ｓａｍｅ", "Body", "hash-after" };
        Check(searchQueries.All(query => new[] { colorBatch, previewActivity, grouped.Single(), refActivity }.All(row =>
            HistoryText.MatchesWords(row, null, HistoryText.SearchWords(query), cache) == HistoryText.Matches(row, null, query))),
            "Cached and uncached search agree for Japanese, full-width, compound and hidden-detail queries");
        Check(!HistoryText.MatchesWords(colorBatch, "Transform", HistoryText.SearchWords("white"), cache),
            "Search cache never bypasses the category filter");
        var spaced = HistoryText.Build(S(Named("boundary", "unchanged", "コンポーネント", "Owner / alpha")),
            S(Named("boundary", "beta", "コンポーネント", "Owner / alpha"))).Single();
        Check(!HistoryText.MatchesWords(spaced, null, HistoryText.SearchWords("alphabeta"), cache) &&
            HistoryText.MatchesWords(spaced, null, HistoryText.SearchWords("alpha beta"), cache),
            "Search keeps field boundaries while allowing AND queries across fields");
        var limitedCache = new ActivitySearchCache(1, 1);
        Check(HistoryText.MatchesWords(colorBatch, null, HistoryText.SearchWords("発光色 white"), limitedCache) && limitedCache.Count == 0,
            "Oversized search data is not retained and still produces correct results");
        limitedCache = new ActivitySearchCache(1000000, 1);
        HistoryText.MatchesWords(colorBatch, null, HistoryText.SearchWords("unlikely-no-result"), limitedCache);
        Check(HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("ノーマルマップ"), limitedCache) && limitedCache.Count == 1,
            "Entry limit bounds search cache memory without hiding uncached matches");
        limitedCache.Retain(new HashSet<Snapshot> { previewActivity.after });
        Check(limitedCache.Count == 0, "Pruned snapshots are released by the search cache");
        HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("unlikely-no-result"), limitedCache);
        Check(limitedCache.Count == 1, "A complete search can reuse capacity released by pruning");
        limitedCache.Clear();
        Check(limitedCache.Count == 0, "Ending a search session releases its cached data");
        Check(HistoryText.MatchesWords(spaced, null, HistoryText.SearchWords("alpha"), limitedCache) && limitedCache.Count == 0,
            "Early summary match does not retain an incomplete search index");
        Check(HistoryText.MatchesWords(spaced, null, HistoryText.SearchWords("beta"), limitedCache),
            "A later query still searches fields skipped by the earlier match");
        limitedCache.Clear();
        Check(HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("m_ValidKeywords"), limitedCache) && limitedCache.Count == 1 &&
            HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("hash-after"), limitedCache),
            "Raw detail matches reuse their prefix and later queries still find unvisited fields");
        Check(!HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("no-such-field"), limitedCache) && limitedCache.Count == 1 &&
            HistoryText.MatchesWords(previewActivity, null, HistoryText.SearchWords("ノーマルマップ 0.70"), limitedCache),
            "A complete index retains raw fields and formatted values after the two-stage search");
        var noCache = new ActivitySearchCache(0, 0);
        Check(searchQueries.All(query => new[] { colorBatch, previewActivity, grouped.Single(), refActivity }.All(row =>
            HistoryText.MatchesWords(row, null, HistoryText.SearchWords(query), noCache) == HistoryText.MatchesWords(row, null, HistoryText.SearchWords(query), cache))),
            "Cache exhaustion preserves compound, formatted and raw-detail search results");
        var detailDisplay = HistoryText.DetailPage(previewActivity, 0, 20);
        Check(detailDisplay.Count == previewActivity.materialItems.Count && detailDisplay.Select(row => row.change).SequenceEqual(previewActivity.materialItems) &&
            detailDisplay.Any(row => row.label.Contains("保存済みアセット")) && detailDisplay.Any(row => row.label.Contains("メインカラー")),
            "Cached detail page preserves every material setting, saved asset and readable label");
        var sharedDetailDisplay = HistoryText.DetailPage(sharedRows.Single(), 0, 20);
        Check(sharedDetailDisplay.Single().locations.Contains("front") && sharedDetailDisplay.Single().locations.Contains("back"),
            "Detail lookup preserves all shared material reference locations");
        Check(HistoryText.DetailPage(previewActivity, 0, 2).Concat(HistoryText.DetailPage(previewActivity, 1, 2))
            .Concat(HistoryText.DetailPage(previewActivity, 2, 2)).Select(row => row.change).SequenceEqual(previewActivity.materialItems),
            "Changing detail pages neither drops nor repeats material settings");
        var wideEntries = new List<HistoryEntry>();
        void AddPresence(string path) => wideEntries.Add(new HistoryEntry {
            key = "object:" + path, category = "オブジェクト", path = path, item = "Object / 存在", value = "あり" });
        for (int i = 0; i < 80; i++)
        {
            string rootPath = "./Outfit%2F" + i + "[0]";
            AddPresence(rootPath);
            for (int j = 0; j < 8; j++) AddPresence(rootPath + "/Child[" + j + "]");
        }
        var wideSnapshot = S(wideEntries.ToArray());
        var wideAdded = HistoryText.Build(S(), wideSnapshot);
        Check(wideAdded.Count == 80 && wideAdded.All(row => row.details.Count == 9) &&
            wideAdded.SelectMany(row => row.details).Select(c => c.Entry.key).Distinct().Count() == 720,
            "Wide hierarchy groups each parent once and preserves all 720 object changes without duplicates");
        var wideRemoved = HistoryText.Build(wideSnapshot, S());
        Check(wideRemoved.Count == 80 && wideRemoved.All(row => row.details.Count == 9 && row.change.Kind == "削除"),
            "Wide hierarchy removal preserves the same parent boundaries");
        wideEntries.Clear();
        string nestedPath = ".";
        for (int i = 0; i < 64; i++) { AddPresence(nestedPath); nestedPath += "/Part[0]"; }
        var deepAdded = HistoryText.Build(S(), S(wideEntries.ToArray()));
        Check(deepAdded.Count == 1 && deepAdded[0].Path == "." && deepAdded[0].details.Count == 64,
            "Deep hierarchy resolves the outermost changed parent including the avatar root");
        var fixBefore = S(Named("setting", "old", "コンポーネント", "Descriptor / setting"),
            Named("deleted", "あり", "オブジェクト", "Object / 存在", "./Old[0]"),
            Named("mat", "0.5", "マテリアル", "Renderer / Material[0] / _NormalScale", "./Body[0]"));
        var fixAfter = S(Named("setting", "new", "コンポーネント", "Descriptor / setting"),
            Named("added", "あり", "オブジェクト", "Object / 存在", "./New[0]"),
            Named("child", "あり", "オブジェクト", "Object / 存在", "./New[0]/Child[0]"),
            Named("mat", "0.7", "マテリアル", "Renderer / Material[0] / _NormalScale", "./Body[0]"),
            Named("asset", "asset-hash", "参照アセット", "Assets/Texture.png"));
        fixAfter.autoFix = true;
        var fix = HistoryText.Build(fixBefore, fixAfter).Single();
        Check(fix.IsAutoFix && fix.details.Count == 6 && fix.details.Select(c => c.Entry.key).Distinct().Count() == 6,
            "Auto Fix forms one row containing every mixed-category addition, deletion and edit");
        Check(fix.details.Single(c => c.Entry.key == "mat").before.value == "0.5" &&
            fix.details.Single(c => c.Entry.key == "mat").after.value == "0.7",
            "Auto Fix grouping preserves original values without modifying snapshots");
        var fixDisplay = new ActivityDisplay(fix);
        Check(fixDisplay.category == "Auto Fix" && fixDisplay.kind == "変更" && !fixDisplay.grouped &&
            HistoryText.CardTitle(fix).Contains("Test") && HistoryText.CardValue(fix).Contains("6項目"),
            "Auto Fix has a compact avatar-level summary independent of the first changed category");
        Check(new[] { "コンポーネント", "オブジェクト", "マテリアル", "参照アセット" }.All(category =>
            HistoryText.MatchesWords(fix, category, new string[0], cache)) &&
            !HistoryText.MatchesWords(fix, "シェイプキー", new string[0], cache),
            "Category filters include Auto Fix only when its details contain that category");
        Check(HistoryText.MatchesWords(fix, null, HistoryText.SearchWords("Ａｕｔｏ Ｆｉｘ Child asset-hash _NormalScale"), cache) &&
            !HistoryText.MatchesWords(fix, null, HistoryText.SearchWords("nonexistent-fix-field"), cache),
            "Auto Fix title and all nested raw details remain searchable with cached compound queries");
        var fixDetails = HistoryText.DetailPage(fix, 0, 2).Concat(HistoryText.DetailPage(fix, 1, 2))
            .Concat(HistoryText.DetailPage(fix, 2, 2)).ToList();
        Check(fixDetails.Select(row => row.change).SequenceEqual(fix.details) &&
            fixDetails.Any(row => row.label.Contains("マテリアル")) && fixDetails.Any(row => row.label.Contains("コンポーネント")),
            "Auto Fix detail pagination preserves all changes and identifies each category");
        string fixText = HistoryText.Export(fix);
        Check(fix.details.All(c => fixText.Contains(c.Entry.item) && fixText.Contains(c.before?.value ?? "（項目なし）") &&
            fixText.Contains(c.after?.value ?? "（項目なし）")) && fixText.Contains("Child") && fixText.Contains("Auto Fix"),
            "Auto Fix TXT details retain children, material values, asset metadata and removals");
        var fixDocument = HistoryText.ExportDocument(new[] { fix }, "すべて", "").ToList();
        Check(fixDocument.Contains("件数: 1 / 新しい順") && fixDocument.Count(line => line.StartsWith("001 | ")) == 1 &&
            fixDocument.Count(line => line.StartsWith("--- 詳細 #001")) == 1 && fixDocument.Any(line => line.Contains("asset-hash")),
            "Auto Fix TXT uses one summary ID and one complete matching detail section");
        Check(HistoryText.Build(fixAfter, fixAfter).Count == 0, "An unchanged Auto Fix creates no activity");
        var normalAfterFix = S(fixAfter.entries.Concat(new[] { E("manual", "1") }).ToArray());
        Check(HistoryText.Build(fixAfter, normalAfterFix).All(row => !row.IsAutoFix) && HistoryText.Build(fixAfter, normalAfterFix).Count == 1,
            "Later manual edits do not inherit Auto Fix grouping");
        fixAfter.startsRecordingSegment = true;
        Check(HistoryText.Build(fixBefore, fixAfter).Count == 0, "Pause segment boundaries take precedence over Auto Fix grouping");
        fixAfter.startsRecordingSegment = false;
        fixAfter.autoFix = false;
        Check(HistoryText.Build(fixBefore, fixAfter).Count > 1, "Ordinary simultaneous edits are never guessed to be Auto Fix");
        var distinctProperties = S(Named("a", "1", "コンポーネント", "Owner / first", "./A[0]"),
            Named("b", "1", "コンポーネント", "Owner / second", "./B[0]"));
        distinctProperties.autoFix = true;
        var distinctDetails = HistoryText.DetailPage(HistoryText.Build(S(), distinctProperties).Single(), 0, 20);
        Check(distinctDetails.Count == 2 && distinctDetails[0].locations != distinctDetails[1].locations,
            "Equal values in different component properties do not mix their reference locations");
        var separateBefore = S(MaterialAt("./Body[0]", matId, "1").Concat(MaterialAt("./Coat[0]", separateId, "1")).ToArray());
        var separateAfter = S(MaterialAt("./Body[0]", matId, "0.5").Concat(MaterialAt("./Coat[0]", separateId, "0.5")).ToArray());
        separateAfter.autoFix = true;
        var separateFix = HistoryText.Build(separateBefore, separateAfter).Single();
        var separateFixDetails = HistoryText.DetailPage(separateFix, 0, 20);
        Check(separateFixDetails.Count == 2 && separateFixDetails.All(row =>
            row.locations.StartsWith(HistoryText.ReadablePath(row.change.Entry.path)) && !row.locations.Contains("\n")),
            "Auto Fix keeps equal property/value edits on different materials at their own locations");
        var sharedFixBefore = Shared("1", "1"); var sharedFixAfter = Shared("0.5", "0.5"); sharedFixAfter.autoFix = true;
        var sharedFix = HistoryText.Build(sharedFixBefore, sharedFixAfter).Single();
        Check(HistoryText.DetailPage(sharedFix, 0, 20).All(row => !row.locations.Contains("\n")) && sharedFix.details.Count == 2,
            "Auto Fix keeps individual shared-material references without cross-listing them on every detail row");
        var massBefore = S(Enumerable.Range(0, 63).Select(i => Named("mass" + i, "1", "コンポーネント", "Collider [0] / m_Radius", "./Object" + i + "[0]")).ToArray());
        var massAfter = S(Enumerable.Range(0, 63).Select(i => Named("mass" + i, "2", "コンポーネント", "Collider [0] / m_Radius", "./Object" + i + "[0]")).ToArray());
        massAfter.autoFix = true;
        var massFix = HistoryText.Build(massBefore, massAfter).Single();
        var massPages = Enumerable.Range(0, 4).SelectMany(i => HistoryText.DetailPage(massFix, i, 20)).ToList();
        Check(massPages.Count == 63 && massPages.Select(row => row.change).SequenceEqual(massFix.details) &&
            massPages.All(row => row.locations == HistoryText.ReadablePath(row.change.Entry.path)),
            "Four Auto Fix detail pages preserve every equal-valued component edit and its exact location");
        Check(HistoryText.DetailPage(massFix, 4, 20).Count == 0,
            "Auto Fix detail pages beyond the last change stay empty");
        fixAfter.autoFix = true;
        Check(new ActivityDisplay(fix).nested.Count == 0 && fix.details.Count == 6 &&
            HistoryText.Matches(fix, null, "Child") && HistoryText.Export(fix).Contains("Child"),
            "Skipping Auto Fix's unused nested preview preserves complete search and exported details");
        Console.WriteLine("SUCCESS: " + passed + " test groups");
    }
}
