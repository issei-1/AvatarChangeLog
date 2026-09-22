using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvatarChangeLog;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public static class Validation
{
    static readonly List<string> Results = new List<string>();
    static string Output => Environment.GetEnvironmentVariable("AVATAR_CHANGE_LOG_OUTPUT") ??
        Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports"));
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Results.Add("PASS: " + name);
        Debug.Log("PASS: " + name);
    }

    public static void Run()
    {
        try
        {
            Directory.CreateDirectory(Output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("テストアバター");
            var body = new GameObject("Body/顔[0]"); body.transform.SetParent(root.transform);
            var sibling = new GameObject("Body/顔[0]"); sibling.transform.SetParent(root.transform);
            sibling.SetActive(false);
            var mesh = new Mesh { name = "Test Mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            AssetDatabase.CreateAsset(mesh, "Assets/TestMesh.asset");
            var material = new Material(Shader.Find("Standard")) { name = "Test Material" };
            AssetDatabase.CreateAsset(material, "Assets/TestMaterial.mat");
            var texture = new Texture2D(2, 2) { name = "Test Texture" };
            AssetDatabase.CreateAsset(texture, "Assets/TestTexture.asset");
            material.SetTexture("_MainTex", texture);
            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.sharedMaterials = new[] { material };
            var fixture = root.AddComponent<Fixture>(); fixture.reference = body;
            root.AddComponent<BoxCollider>(); root.AddComponent<BoxCollider>();
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(root.scene, "Assets/Validation.unity");
            Check(!root.scene.isDirty, "Scene starts clean");
            var original = SnapshotCapture.Capture(root, "改変前");
            Check(!original.entries.Any(e => e.item.Contains(" / m_SavedProperties") || e.item.Contains(" / m_Materials")),
                "Capture excludes duplicate material serialization and renderer arrays");
            Check(original.entries.Any(e => e.item.EndsWith("ShaderProperty/_MainTex") && e.referencePath == "Assets/TestTexture.asset"),
                "Named texture property preserves explicit asset ownership");
            var materialProperties = original.entries.Where(e => e.item.Contains("ShaderProperty/")).ToList();
            Check(materialProperties.Any(e => e.item.EndsWith("ShaderProperty/_Color") && e.colorValue) &&
                materialProperties.Where(e => e.item.EndsWith("ShaderProperty/_Glossiness")).All(e => !e.colorValue),
                "Color metadata distinguishes colors from numeric properties");
            Check(materialProperties.Select(p => p.key).Distinct().Count() == materialProperties.Count, "Live material enumeration has unique property keys");
            Check(materialProperties.Any(e => e.item.EndsWith("ShaderProperty/_Glossiness") && e.displayName == material.shader.GetPropertyDescription(material.shader.FindPropertyIndex("_Glossiness")) && e.numericValue), "Shader inspector descriptions captured separately from stable property keys");
            renderer.sharedMaterials = new[] { material, material };
            var sharedCapture = SnapshotCapture.Capture(root, "共有マテリアル");
            var firstSlot = sharedCapture.entries.Where(e => e.category == "マテリアル" && e.item.Contains(" / Material 0 / ")).ToList();
            var secondSlot = sharedCapture.entries.Where(e => e.category == "マテリアル" && e.item.Contains(" / Material 1 / ")).ToList();
            Check(firstSlot.Count == secondSlot.Count && firstSlot.All(a => secondSlot.Any(b =>
                b.item == a.item.Replace(" / Material 0 / ", " / Material 1 / ") && b.value == a.value &&
                b.referencePath == a.referencePath && b.displayName == a.displayName && b.numericValue == a.numericValue && b.colorValue == a.colorValue)),
                "Shared material reuse preserves every per-slot value, label and reference");
            float originalGloss = material.GetFloat("_Glossiness");
            material.SetFloat("_Glossiness", 0.123f);
            material.SetFloat("_Mode", 2f);
            var numeric = SnapshotCapture.Capture(root, "未保存の数値変更");
            var numericDiff = HistoryDiff.Compare(original, numeric);
            var sharedRows = HistoryText.Build(sharedCapture, numeric).Where(a => a.change.Entry.item.EndsWith("ShaderProperty/_Glossiness")).ToList();
            Check(sharedRows.Count == 1 && sharedRows[0].SharedMaterial && sharedRows[0].MaterialBatch &&
                sharedRows[0].details.Count(c => c.Entry.item.EndsWith("ShaderProperty/_Glossiness")) == 2 &&
                sharedRows[0].details.Count(c => c.Entry.item.EndsWith("ShaderProperty/_Mode")) == 2,
                "Real material identity groups different properties and shared slots into one activity");
            Check(numericDiff.Any(c => c.Entry.item.EndsWith("ShaderProperty/_Glossiness") && c.Kind == "変更" && c.after.value == "0.123"), "Unsaved Range reads live value including initial shader default");
            Check(numericDiff.Any(c => c.Entry.item.EndsWith("ShaderProperty/_Mode") && c.after.value == "2"), "Unsaved Float reads live value");
            Check(!numericDiff.Any(c => c.Entry.category == "マテリアル" && c.Entry.item.Contains("m_SavedProperties")), "Named material values avoid serialized duplicate logs");
            Check(numeric.entries.Count(e => e.item.EndsWith("ShaderProperty/_Glossiness") && e.value == "0.123") == 2,
                "Shared material cache is refreshed between captures");
            renderer.sharedMaterials = new[] { material };
            material.SetFloat("_Glossiness", originalGloss);
            var differentMaterial = new Material(material);
            differentMaterial.SetFloat("_Glossiness", 0.876f);
            renderer.sharedMaterials = new[] { material, differentMaterial };
            var distinctMaterialCapture = SnapshotCapture.Capture(root, "同一シェーダー・別マテリアル");
            var glossEntries = distinctMaterialCapture.entries.Where(e => e.item.EndsWith("ShaderProperty/_Glossiness")).ToList();
            Check(glossEntries.Count == 2 && glossEntries[0].displayName == glossEntries[1].displayName &&
                glossEntries[0].value != glossEntries[1].value, "Shared shader labels never reuse values from a different material");
            renderer.sharedMaterials = new[] { material };
            Object.DestroyImmediate(differentMaterial);
            material.SetFloat("_Mode", 0f);
            AssetDatabase.SaveAssets();
            original = SnapshotCapture.Capture(root, "改変前");
            Check(!root.scene.isDirty && !EditorUtility.IsDirty(material), "Capture never dirties scene or material");
            Check(original.entries.Select(e => e.key).Distinct().Count() == original.entries.Count, "Duplicate names and components have unique keys");
            Check(original.entries.Any(e => e.path.EndsWith("[1]") && e.value == "OFF"), "Inactive children captured");
            Check(original.entries.Any(e => e.value == "9007199254740993"), "64-bit integer precision preserved");
            Check(!original.warnings.Any(w => w.Contains("未対応")), "All fixture property types supported");
            Check(HistoryDiff.Compare(original, SnapshotCapture.Capture(root, "same")).Count == 0, "Repeated capture has zero noise");
            var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var french = SnapshotCapture.Capture(root, "French locale");
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            Check(HistoryDiff.Compare(original, french).Count == 0, "Numeric capture independent of locale");

            body.transform.localPosition = new Vector3(1.25f, 2, 3);
            renderer.SetBlendShapeWeight(0, 65);
            material.color = Color.red;
            fixture.number = 42;
            fixture.node.weight = 0.5f;
            fixture.curve = AnimationCurve.Linear(0, 0, 1, 2);
            fixture.gradient.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.blue, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.5f, 1) });
            sibling.SetActive(true);
            var added = new GameObject("Ribbon"); added.transform.SetParent(root.transform);
            var changed = SnapshotCapture.Capture(root, "色・表情・位置を調整");
            var diff = HistoryDiff.Compare(original, changed);
            Check(diff.Any(c => c.Entry.category == "Transform" && c.Kind == "変更"), "Transform differences");
            Check(diff.Any(c => c.Entry.category == "BlendShape" && c.after.value == "65"), "Named BlendShape differences");
            Check(diff.Any(c => c.Entry.category == "マテリアル" && c.Kind == "変更"), "Unsaved material property differences");
            Check(diff.Any(c => c.Entry.item.EndsWith("number") && c.after.value == "42"), "Custom component property differences");
            Check(diff.Any(c => c.Entry.item.EndsWith("node.weight")), "SerializeReference child differences");
            Check(diff.Any(c => c.Entry.item.EndsWith("gradient")), "Gradient differences");
            Check(diff.Any(c => c.Entry.item.EndsWith("curve")), "AnimationCurve differences");
            Check(diff.Any(c => c.Entry.path.Contains("Ribbon") && c.Kind == "追加"), "Added objects");
            Object.DestroyImmediate(added);
            var removed = SnapshotCapture.Capture(root, "Ribbon削除");
            Check(HistoryDiff.Compare(changed, removed).Any(c => c.Kind == "削除" && c.Entry.path.Contains("Ribbon")), "Removed objects");
            var unique = new GameObject("Unique"); unique.transform.SetParent(root.transform);
            var reorderBefore = SnapshotCapture.Capture(root, "before reorder");
            unique.transform.SetSiblingIndex(0);
            Check(HistoryDiff.Compare(reorderBefore, SnapshotCapture.Capture(root, "reordered")).Count == 0, "Unrelated sibling reorder causes no false changes");
            var renameBefore = SnapshotCapture.Capture(root, "rename before");
            unique.name = "Renamed";
            var renameDiff = HistoryDiff.Compare(renameBefore, SnapshotCapture.Capture(root, "rename after"));
            Check(renameDiff.Any(c => c.Kind == "追加") && renameDiff.Any(c => c.Kind == "削除"), "Rename reported as remove/add");
            HistoryStore.Save(original); HistoryStore.Save(changed);
            string compact = File.ReadAllText(Path.Combine(HistoryStore.DirectoryFor(original.avatarId), original.id + ".json"));
            Check(!compact.Contains("\n") && compact.Length < JsonUtility.ToJson(original, true).Length,
                "Saved snapshot omits formatting whitespace without losing fields");
            Check(HistoryDiff.Compare(original, JsonUtility.FromJson<Snapshot>(compact)).Count == 0,
                "Compact JSON preserves all comparable values");
            var loaded = HistoryStore.Load(original.avatarId, out var errors);
            Check(errors.Count == 0 && loaded.Count == 2, "JSON save and reload");
            Check(HistoryStore.LoadLatest(original.avatarId).id == loaded.Last().id, "Latest-only load matches full history with real Unity JSON");
            Check(HistoryDiff.Compare(loaded[0], loaded[1]).Count == diff.Count, "JSON roundtrip preserves diff");
            bool duplicateRejected = false;
            try { HistoryStore.Save(original); } catch (IOException) { duplicateRejected = true; }
            Check(duplicateRejected, "Existing snapshots cannot be overwritten");
            string corrupt = Path.Combine(HistoryStore.DirectoryFor(original.avatarId), "broken.json");
            File.WriteAllText(corrupt, "{ not valid json");
            loaded = HistoryStore.Load(original.avatarId, out errors);
            Check(loaded.Count == 2 && errors.Count == 1 && File.Exists(corrupt), "Corrupt snapshot isolated and retained");
            File.Delete(corrupt);
            var other = SnapshotCapture.Capture(root, "other"); other.avatarId = "different";
            bool differentRejected = false;
            try { HistoryDiff.Compare(original, other); } catch (InvalidOperationException) { differentRejected = true; }
            Check(differentRejected, "Different avatar histories rejected");
            fixture.text = "changed";
            EditorUtility.SetDirty(fixture);
            ValidateGroupedAssets(root, mesh);
            EditorSceneManager.MarkSceneDirty(root.scene);
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(root.scene);
            var restart = SnapshotCapture.Capture(root, "restart baseline");
            HistoryStore.Save(restart);
            File.WriteAllText(Path.Combine(Application.dataPath, "../restart.json"), JsonUtility.ToJson(restart));
            string retentionId = "retention-integration-" + Guid.NewGuid().ToString("N");
            var retentionIds = new List<string>();
            for (int i = 0; i < 103; i++)
            {
                var item = JsonUtility.FromJson<Snapshot>(JsonUtility.ToJson(original));
                item.avatarId = retentionId; item.id = Guid.NewGuid().ToString("N");
                item.createdUtc = DateTimeOffset.UtcNow.AddSeconds(i).ToString("O");
                HistoryStore.Save(item); retentionIds.Add(item.id);
            }

            HistoryStore.Prune(retentionId);
            var retainedRecords = HistoryStore.Load(retentionId, out var retentionErrors);
            Check(retentionErrors.Count == 0 && retainedRecords.Count == 100 && retainedRecords[0].id == retentionIds[3], "Retention with actual Unity JSON keeps newest 100 records");
            HistoryWindow.Open();
            Check(EditorWindow.HasOpenInstances<HistoryWindow>(), "Editor window opens");
            var historyWindow = EditorWindow.GetWindow<HistoryWindow>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            RecordingService.Select(root);
            Preference("Select", retentionId);
            typeof(HistoryWindow).GetMethod("Reload", flags).Invoke(historyWindow, null);
            var windowSnapshots = (List<Snapshot>)typeof(HistoryWindow).GetField("snapshots", flags).GetValue(historyWindow);
            windowSnapshots.Clear(); windowSnapshots.AddRange(retainedRecords);
            File.Delete(Path.Combine(HistoryStore.DirectoryFor(retentionId), retainedRecords[0].id + ".json"));
            typeof(HistoryWindow).GetMethod("OnSnapshotSaved", flags).Invoke(historyWindow,
                new object[] { retainedRecords.Last(), HistoryStore.Prune(retentionId) });
            Check(windowSnapshots.Count == 99 && windowSnapshots.All(s => s.id != retainedRecords[0].id),
                "Retention recovery notification removes stale records without duplicating the last snapshot");
            File.Delete(Path.Combine(HistoryStore.DirectoryFor(retentionId), retainedRecords[1].id + ".json"));
            typeof(HistoryWindow).GetMethod("OnSnapshotSaved", flags).Invoke(historyWindow, new object[] { retainedRecords.Last(), null });
            Check(windowSnapshots.Count == 98 && windowSnapshots.All(s => s.id != retainedRecords[1].id),
                "Failed retention result reconciles the view from disk without losing retained histories");
            RecordingService.Select(root);
            string selectedId = RecordingService.TargetId;
            var failedFlush = new AutoRecorder(root, new Snapshot { avatarId = "invalid-baseline" }, (_, files) => { }, _ => { });
            typeof(AutoRecorder).GetProperty("Running").GetSetMethod(true).Invoke(failedFlush, new object[] { true });
            typeof(RecordingService).GetField("recorder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .SetValue(null, failedFlush);
            bool switchRejected = false;
            try { RecordingService.Select(null); } catch (InvalidOperationException) { switchRejected = true; }
            Check(switchRejected && RecordingService.Target == root && RecordingService.TargetId == selectedId && failedFlush.Running,
                "Failed final flush propagates the error and preserves the selected avatar and recorder");
            failedFlush.Stop(false);
            RecordingService.Select(null);
            ValidateEmptyAvatarWindow(historyWindow, root);
            AssetDatabase.ExportPackage("Assets/AvatarChangeLog", Path.Combine(Output, "AvatarChangeLog-0.1.1.unitypackage"), ExportPackageOptions.Recurse);
            Check(File.Exists(Path.Combine(Output, "AvatarChangeLog-0.1.1.unitypackage")), "Unitypackage exported");
            File.WriteAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            File.WriteAllLines(Path.Combine(Output, "validation-results.txt"), Results.Concat(new[] { e.ToString() }));
            EditorApplication.Exit(1);
        }
    }

    static void ValidateEmptyAvatarWindow(HistoryWindow window, GameObject root)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var reload = typeof(HistoryWindow).GetMethod("Reload", flags);
        var sync = typeof(HistoryWindow).GetMethod("SynchronizeAvatar", flags);
        var saved = typeof(HistoryWindow).GetMethod("OnSnapshotSaved", flags);
        var targetSetter = typeof(RecordingService).GetProperty("Target").GetSetMethod(true);
        var snapshots = (List<Snapshot>)typeof(HistoryWindow).GetField("snapshots", flags).GetValue(window);
        bool Empty() => new[] { "snapshots", "activities", "visible", "pageRows", "loadErrors" }.All(name =>
            ((System.Collections.ICollection)typeof(HistoryWindow).GetField(name, flags).GetValue(window)).Count == 0);

        RecordingService.Select(root);
        reload.Invoke(window, null);
        Check(snapshots.Count > 0, "Selected existing avatar displays its saved histories");
        string id = RecordingService.TargetId;
        var previous = snapshots.Last();
        int files = Directory.GetFiles(HistoryStore.DirectoryFor(id), "*.json").Length;
        // Simulate scene-unavailable/startup state: remembered ID exists but no resolved GameObject.
        targetSetter.Invoke(null, new object[] { null });
        sync.Invoke(window, null);
        Check(Empty() && RecordingService.TargetId == id, "Unresolved avatar hides all history without losing its remembered ID");
        reload.Invoke(window, null);
        saved.Invoke(window, new object[] { previous, null });
        Check(Empty(), "Reload and delayed snapshot callbacks cannot repopulate an empty avatar window");
        Check(Directory.GetFiles(HistoryStore.DirectoryFor(id), "*.json").Length == files,
            "Hiding unavailable-avatar logs leaves saved files untouched");
        targetSetter.Invoke(null, new object[] { root });
        sync.Invoke(window, null);
        Check(snapshots.Count == files, "Restoring the avatar restores its history display");
        RecordingService.Select(null);
        sync.Invoke(window, null);
        Check(Empty() && string.IsNullOrEmpty(RecordingService.TargetId), "Explicitly clearing the avatar also clears the log window");
        RecordingService.Select(root);
        sync.Invoke(window, null);
        Object.DestroyImmediate(root);
        sync.Invoke(window, null);
        Check(Empty(), "Destroyed Unity target clears cached rows even when both object references compare null");
        RecordingService.Select(null);
    }

    static void ValidateGroupedAssets(GameObject root, Mesh mesh)
    {
        AssetDatabase.SaveAssets();
        var before = SnapshotCapture.Capture(root, "grouping before");
        var parent = new GameObject("追加衣装"); parent.transform.SetParent(root.transform);
        var child = new GameObject("Ribbon"); child.transform.SetParent(parent.transform);
        var renderer = child.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
        var texture = new Texture2D(1, 1);
        AssetDatabase.CreateAsset(texture, "Assets/GroupedTexture.asset");
        var material = new Material(Shader.Find("Standard")); material.mainTexture = texture;
        AssetDatabase.CreateAsset(material, "Assets/GroupedMaterial.mat");
        renderer.sharedMaterials = new[] { material };
        AssetDatabase.SaveAssets();
        var added = SnapshotCapture.Capture(root, "grouping added");
        var groups = HistoryText.Build(before, added);
        Check(groups.Count == 1 && groups[0].NestedItems.Any(c => c.Entry.path.Contains("Ribbon")), "Live capture groups new hierarchy into one parent with child detail");
        Check(groups[0].NestedItems.Count(c => c.Entry.category == "参照アセット") == 2, "Live capture groups material and texture under parent");
        Check(added.entries.Any(e => e.referencePath == "Assets/GroupedTexture.asset"), "Capture records explicit asset ownership metadata");
        var roundtrip = JsonUtility.FromJson<Snapshot>(JsonUtility.ToJson(added));
        Check(HistoryText.Build(before, roundtrip).Single().NestedItems.Count == 3, "JSON roundtrip preserves child and asset grouping");
        Object.DestroyImmediate(parent);
        var removed = SnapshotCapture.Capture(root, "grouping removed");
        var removedGroups = HistoryText.Build(added, removed);
        Check(removedGroups.Count == 1 && removedGroups[0].NestedItems.Count == 3, "Live deletion retains removed child and asset details");
    }

    public static void Restart()
    {
        try
        {
            EditorSceneManager.OpenScene("Assets/Validation.unity");
            var root = GameObject.Find("テストアバター");
            var baseline = JsonUtility.FromJson<Snapshot>(File.ReadAllText(Path.Combine(Application.dataPath, "../restart.json")));
            var current = SnapshotCapture.Capture(root, "after restart");
            Check(baseline.avatarId == current.avatarId, "Avatar identity stable across editor restart");
            Check(HistoryDiff.Compare(baseline, current).Count == 0, "Snapshot stable across editor restart");
            Check(HistoryStore.Load(current.avatarId, out var errors).Count == 3 && errors.Count == 0, "History available after restart");
            File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.AppendAllText(Path.Combine(Output, "validation-results.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    static AutoRecorder automatic;
    static SkinnedMeshRenderer automaticRenderer;
    static Material automaticMaterial;
    static readonly List<Snapshot> automaticSaved = new List<Snapshot>();
    static double automaticStart;
    static int automaticStep;
    static string automaticError;

    public static void Automatic()
    {
        try
        {
            Directory.CreateDirectory(Output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("自動ログアバター");
            automaticRenderer = root.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "Auto Mesh" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            AssetDatabase.CreateAsset(mesh, "Assets/AutomaticMesh.asset");
            automaticRenderer.sharedMesh = mesh;
            automaticMaterial = new Material(Shader.Find("Standard"));
            automaticMaterial.SetFloat("_Glossiness", 0);
            AssetDatabase.CreateAsset(automaticMaterial, "Assets/AutomaticMaterial.mat");
            automaticRenderer.sharedMaterial = automaticMaterial;
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(root.scene, "Assets/Automatic.unity");
            automatic = new AutoRecorder(root, null, (s, files) => automaticSaved.Add(s), e => automaticError = e);
            automatic.Start();
            Check(automaticSaved.Count == 1, "Auto recorder creates initial baseline");
            automaticStart = EditorApplication.timeSinceStartup;
            EditorApplication.update += AutomaticTick;
        }
        catch (Exception e) { AutomaticFinish(e); }
    }

    static void AutomaticTick()
    {
        try
        {
            if (automaticError != null) throw new Exception(automaticError);
            double elapsed = EditorApplication.timeSinceStartup - automaticStart;
            if (elapsed > 30) throw new Exception("Automatic tests timed out.");
            switch (automaticStep)
            {
                case 0: if (elapsed < .2) return; automaticRenderer.SetBlendShapeWeight(0, 10); break;
                case 1: if (elapsed < .4) return; automaticRenderer.SetBlendShapeWeight(0, 35); break;
                case 2: if (elapsed < .6) return; automaticRenderer.SetBlendShapeWeight(0, 70); break;
                case 3:
                    if (elapsed < 2.5) return;
                    Check(automaticSaved.Count == 2, "Automatic polling coalesces three BlendShape edits into one record");
                    Check(HistoryText.Build(automaticSaved[0], automaticSaved[1]).Any(a => a.Summary.Contains("0 → 70")), "Automatic BlendShape log contains old and new values");
                    Undo.RecordObject(automaticRenderer, "Shape test");
                    automaticRenderer.SetBlendShapeWeight(0, 20);
                    Undo.FlushUndoRecordObjects();
                    break;
                case 4:
                    if (elapsed < 4.5) return;
                    Check(automaticSaved.Count == 3, "Inspector-style Undo modification is recorded");
                    Undo.PerformUndo();
                    break;
                case 5:
                    if (elapsed < 6.5) return;
                    Check(automaticSaved.Count == 4 && automaticRenderer.GetBlendShapeWeight(0) == 70, "Undo automatically creates reverse-change log");
                    break;
                case 6:
                    if (elapsed < 8.5) return;
                    Check(automaticSaved.Count == 4, "Idle safety scans do not create duplicate records");
                    automaticRenderer.SetBlendShapeWeight(0, 30);
                    automatic.Stop(true);
                    Check(automaticSaved.Count == 5 && !automatic.Running, "Closing or pausing flushes the last edit");
                    automaticRenderer.SetBlendShapeWeight(0, 40);
                    break;
                case 7:
                    if (elapsed < 9.5) return;
                    Check(automaticSaved.Count == 5, "Paused recorder does not save edits");
                    automatic = new AutoRecorder(automaticRenderer.gameObject, automaticSaved.Last(), (s, files) => automaticSaved.Add(s), e => automaticError = e);
                    automatic.Start();
                    Check(automaticSaved.Count == 6 && automaticSaved.Last().note.Contains("再開"), "Resume labels changes made between recorder sessions");
                    automatic.Stop(true);
                    automatic.Start();
                    automatic.Stop(true);
                    Check(automaticSaved.Count == 6, "Restarting unchanged recorder creates no duplicate");
                    Check(HistoryStore.Load(automaticSaved[0].avatarId, out var errors).Count == 6 && errors.Count == 0, "Automatic logs persist to disk");
                    automatic.Start();
                    automaticMaterial.SetFloat("_Glossiness", 0.25f);
                    break;
                case 8:
                    if (elapsed < 9.8) return;
                    automaticMaterial.SetFloat("_Glossiness", 0.75f);
                    break;
                case 9:
                    if (elapsed < 11.5) return;
                    Check(automaticSaved.Count == 7, "Material edits without Undo or asset save coalesce into one automatic record");
                    Check(HistoryText.Build(automaticSaved[5], automaticSaved[6]).Any(a => a.Summary.Contains("_Glossiness") && a.Summary.Contains("0 → 0.75")), "Automatic material log contains property and numeric values");
                    break;
                case 10:
                    if (elapsed < 15) return;
                    Check(automaticSaved.Count == 7, "Unchanged material polling creates no duplicate records");
                    AutomaticFinish(null);
                    return;
            }
            automaticStep++;
        }
        catch (Exception e) { AutomaticFinish(e); }
    }

    static void AutomaticFinish(Exception error)
    {
        EditorApplication.update -= AutomaticTick;
        automatic?.Stop(false);
        if (error != null) { Debug.LogException(error); Results.Add(error.ToString()); }
        File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
        EditorApplication.Exit(error == null ? 0 : 1);
    }

    public static void Polling()
    {
        try
        {
            Directory.CreateDirectory(Output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("PollingAvatar");
            var child = new GameObject("Body"); child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/TestMesh.asset");
            var material = new Material(Shader.Find("Standard"));
            material.SetFloat("_Glossiness", 0);
            renderer.sharedMaterials = new[] { material, material };
            var recorder = new AutoRecorder(root, null, (_, files) => { }, e => { if (e != null) throw new Exception(e); });
            var relevant = typeof(AutoRecorder).GetMethod("IsRelevantTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool Relevant(Object target) => (bool)relevant.Invoke(recorder, new object[] { target });
            var unrelated = new GameObject("OtherAvatar");
            Check(Relevant(root) && Relevant(child.transform) && Relevant(renderer),
                "Undo filter includes avatar root, child transforms and components");
            Check(!Relevant(unrelated) && !Relevant(unrelated.transform),
                "Undo filter excludes scene objects and components outside the avatar");
            Check(Relevant(material), "Shared or newly assigned material edits remain eligible before polling");
            string prefabPath = "Assets/UndoFilter.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(unrelated, prefabPath);
            Check(Relevant(prefab) && Relevant(prefab.transform),
                "Persistent prefab asset edits are not mistaken for unrelated scene objects");
            ValidateUndoFiltering(recorder, renderer, unrelated);
            Object.DestroyImmediate(unrelated);
            var method = typeof(AutoRecorder).GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool Poll() => (bool)method.Invoke(recorder, null);
            Check(!Poll() && !Poll(), "Initial and stable polling create no change");
            var shaderCacheField = typeof(AutoRecorder).GetField("shaderProperties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            System.Collections.IDictionary ShaderCache() => (System.Collections.IDictionary)shaderCacheField.GetValue(recorder);
            object originalMetadata = ShaderCache()[material.shader.GetInstanceID()];
            Check(!Poll() && ReferenceEquals(originalMetadata, ShaderCache()[material.shader.GetInstanceID()]),
                "Stable polls reuse shader metadata while continuing live value checks");
            renderer.SetBlendShapeWeight(0, 0.0001f);
            Check(Poll() && !Poll(), "Reused shape buffer detects small edit once");
            material.SetFloat("_Glossiness", 0.0001f);
            Check(Poll() && !Poll(), "Shared material numeric edit detected once without save or Undo");
            material.SetTextureOffset("_MainTex", new Vector2(0.2f, 0.3f));
            Check(Poll() && !Poll(), "Texture offset remains observable in numeric polling");
            material.SetTextureScale("_MainTex", new Vector2(2, 3));
            Check(Poll() && !Poll(), "Texture tiling remains observable in numeric polling");
            var texture = new Texture2D(2, 2);
            material.SetTexture("_MainTex", texture);
            Check(Poll() && !Poll(), "Texture replacement detected without string fingerprint");
            child.SetActive(false);
            material.SetFloat("_Glossiness", 0.5f);
            Check(Poll(), "Inactive renderer remains monitored");
            material.shader = Shader.Find("Unlit/Texture");
            Check(Poll() && !Poll(), "Shader replacement refreshes property identity and list length");
            var replacement = new Material(material);
            renderer.sharedMaterials = new[] { replacement, replacement };
            Check(Poll() && !Poll(), "Material replacement with unchanged count is detected");
            renderer.sharedMaterials = new Material[0];
            Check(Poll() && !Poll(), "Removal clears stale material state");
            Check(ShaderCache().Count == 0, "Removing all material references releases unused shader metadata");
            renderer.sharedMaterial = material;
            Check(Poll(), "Readded material gets a fresh state");
            Object.DestroyImmediate(child);
            Check(Poll() && !Poll(), "Removed renderer clears shape and material buffers");
            var next = new GameObject("Replacement"); next.transform.SetParent(root.transform);
            next.AddComponent<SkinnedMeshRenderer>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/TestMesh.asset");
            Check(Poll() && !Poll(), "Added renderer detected after buffers are empty");
            ValidateShaderReimport(root, next.GetComponent<SkinnedMeshRenderer>(), Poll, ShaderCache);
            recorder.Stop(false);
            Check(ShaderCache().Count == 0, "Stopping the recorder releases shader metadata");
            Object.DestroyImmediate(material); Object.DestroyImmediate(replacement); Object.DestroyImmediate(texture);
            File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.AppendAllText(Path.Combine(Output, "validation-results.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    static void ValidateShaderReimport(GameObject root, Renderer renderer,
        Func<bool> poll, Func<System.Collections.IDictionary> cache)
    {
        const string path = "Assets/MetadataRefresh.shader";
        string ShaderText(string property, string label) => "Shader \"Hidden/ACLMetadataTest\" { Properties { " + property +
            " (\"" + label + "\", Float) = 0 } SubShader { Pass {} } }";
        File.WriteAllText(path, ShaderText("_OldValue", "Before"));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
        var first = new Material(shader);
        var second = new Material(shader);
        renderer.sharedMaterials = new[] { first, second };
        Check(poll() && !poll() && cache().Count == 1, "Different materials share one shader metadata list");
        object oldMetadata = cache()[shader.GetInstanceID()];
        File.WriteAllText(path, ShaderText("_NewValue", "After"));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        // Property count stays unchanged; only ID/label changes.
        first.SetFloat("_NewValue", 0.2f); second.SetFloat("_NewValue", 0.8f);
        Check(poll() && !ReferenceEquals(oldMetadata, cache()[first.shader.GetInstanceID()]) && !poll(),
            "Same-count shader reimport refreshes metadata and detects live changes once");
        EditorSceneManager.SaveScene(root.scene, "Assets/MetadataRefresh.unity");
        var capture = SnapshotCapture.Capture(root, "metadata refresh");
        var entries = capture.entries.Where(e => e.item.EndsWith("ShaderProperty/_NewValue")).ToList();
        Check(entries.Count == 2 && entries.All(e => e.displayName == "After") && entries.Select(e => e.value).Distinct().Count() == 2,
            "Capture reads fresh reimported labels and each material's own value");
        renderer.sharedMaterials = Array.Empty<Material>();
        poll();
        Object.DestroyImmediate(first); Object.DestroyImmediate(second);
    }

    static void ValidateUndoFiltering(AutoRecorder recorder, Component owned, GameObject unrelated)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var schedule = (CaptureSchedule)typeof(AutoRecorder).GetField("schedule", flags).GetValue(recorder);
        var method = typeof(AutoRecorder).GetMethod("OnModifications", flags);
        var running = typeof(AutoRecorder).GetProperty("Running").GetSetMethod(true);
        var outside = new UndoPropertyModification { currentValue = new PropertyModification { target = unrelated.transform } };
        var inside = new UndoPropertyModification { previousValue = new PropertyModification { target = owned } };
        try
        {
            running.Invoke(recorder, new object[] { true });
            var changes = new[] { outside };
            Check(ReferenceEquals(method.Invoke(recorder, new object[] { changes }), changes) && !schedule.Pending,
                "Unrelated Undo edits leave the schedule idle and return the original Undo data");
            method.Invoke(recorder, new object[] { new[] { outside, inside } });
            Check(schedule.Pending, "Mixed Undo batch schedules capture when a previous target belongs to the avatar");
            schedule.Reset();
            method.Invoke(recorder, new object[] { new[] { new UndoPropertyModification() } });
            Check(schedule.Pending, "Unresolved Undo targets conservatively preserve recording notifications");
        }
        finally { schedule.Reset(); running.Invoke(recorder, new object[] { false }); }
    }

    static void UpdateRecordingService()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        typeof(RecordingService).GetField("nextCheck", flags).SetValue(null, 0d);
        typeof(RecordingService).GetMethod("Update", flags).Invoke(null, null);
    }

    static object Preference(string method, string id)
    {
        var type = typeof(RecordingService).Assembly.GetType("AvatarChangeLog.RecordingSettings", true);
        var instance = type.GetProperty("instance", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy).GetValue(null);
        return type.GetMethod(method).Invoke(instance, new object[] { id });
    }

    public static void AutoFix()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("AutoFixAvatar");
            var other = new GameObject("OtherAvatar");
            var renderer = root.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, "Assets/AutoFixMaterial.mat");
            renderer.sharedMaterial = material;
            EditorSceneManager.SaveScene(root.scene, "Assets/AutoFix.unity");
            var legacy = SnapshotCapture.Capture(root, "SDK debug cache fixture");
            legacy.entries.Add(new HistoryEntry { key = "zz-debug-cache", category = "コンポーネント", path = ".",
                item = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor [0] / animationHashSet.Array.data[100].hash", value = "123" });
            HistoryStore.Save(legacy);
            string legacyPath = Path.Combine(HistoryStore.DirectoryFor(legacy.avatarId), legacy.id + ".json");
            string legacyJson = File.ReadAllText(legacyPath);
            var filteredHistory = HistoryStore.Load(legacy.avatarId, out var loadErrors);
            Check(loadErrors.Count == 0 && !filteredHistory.Single().entries.Any(e => e.key == "zz-debug-cache") &&
                !HistoryStore.LoadLatest(legacy.avatarId).entries.Any(e => e.key == "zz-debug-cache") && File.ReadAllText(legacyPath) == legacyJson,
                "Full and latest JSON load both exclude generated SDK hashes without modifying historical files");
            RecordingService.Select(root); UpdateRecordingService();
            string id = SnapshotCapture.AvatarId(root);
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var beginMethod = typeof(RecordingService).GetMethod("BeginAutoFix", statics);
            Action Begin(GameObject avatar) => (Action)beginMethod.Invoke(null, new object[] { avatar });
            var recorder = (AutoRecorder)typeof(RecordingService).GetField("recorder", statics).GetValue(null);
            var tick = typeof(AutoRecorder).GetMethod("Tick", members);
            void Tick() { typeof(AutoRecorder).GetField("nextFullCheck", members).SetValue(recorder, 0d); tick.Invoke(recorder, null); }
            int startCount = HistoryStore.Load(id, out _).Count;
            Check(Begin(other) == null, "SDK Auto Fix for another avatar cannot label the selected avatar's changes");
            root.transform.localPosition = Vector3.right;
            var complete = Begin(root);
            var preFix = HistoryStore.Load(id, out _);
            Check(complete != null && preFix.Count == startCount + 1 && !preFix.Last().autoFix,
                "Auto Fix saves preceding manual edits as a separate ordinary snapshot");
            root.transform.localScale = Vector3.one * 2;
            Tick();
            material.color = Color.red;
            root.AddComponent<BoxCollider>();
            Tick(); Tick();
            Check(HistoryStore.Load(id, out _).Count == preFix.Count,
                "Pending Auto Fix suppresses intermediate snapshots even when a full scan is due");
            complete(); Tick();
            var fixedRecords = HistoryStore.Load(id, out _);
            var fixRows = HistoryText.Build(fixedRecords[fixedRecords.Count - 2], fixedRecords.Last());
            Check(fixedRecords.Count == preFix.Count + 1 && fixedRecords.Last().autoFix && fixRows.Count == 1 &&
                fixRows[0].details.Any(c => c.Entry.category == "マテリアル") &&
                fixRows[0].details.Any(c => c.Entry.category == "コンポーネント"),
                "Auto Fix persists one final snapshot and JSON reload retains every grouped category");
            complete = Begin(root); complete(); Tick();
            Check(HistoryStore.Load(id, out _).Count == fixedRecords.Count, "A no-op Auto Fix consumes no snapshot capacity");
            root.transform.localPosition = Vector3.up;
            recorder.Flush();
            Check(!HistoryStore.LoadLatest(id).autoFix, "The next manual edit returns to ordinary recording");
            var firstComplete = Begin(root);
            root.transform.localScale = Vector3.one * 3;
            var secondComplete = Begin(root);
            int firstFixCount = HistoryStore.Load(id, out _).Count;
            root.transform.localScale = Vector3.one * 4;
            firstComplete(); Tick();
            Check(HistoryStore.Load(id, out _).Count == firstFixCount,
                "A delayed completion from the previous click cannot finish the next Auto Fix");
            secondComplete(); Tick();
            Check(HistoryStore.Load(id, out _).Count == firstFixCount + 1 && HistoryStore.LoadLatest(id).autoFix,
                "Consecutive Auto Fix clicks remain separate final snapshots");
            RecordingService.SetPaused(true);
            Check(Begin(root) == null, "Paused avatars do not start Auto Fix recording");
            root.transform.localScale = Vector3.one * 5;
            RecordingService.SetPaused(false);
            Check(HistoryStore.LoadLatest(id).startsRecordingSegment && !HistoryStore.LoadLatest(id).autoFix,
                "Resuming after a paused fix creates only the normal fresh baseline");
            RecordingService.Select(null);
            Check(Begin(root) == null, "An empty avatar selection cannot start an Auto Fix group");
            File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.AppendAllText(Path.Combine(Output, "validation-results.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    public static void Pause()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var first = new GameObject("SameAvatarName");
            var second = new GameObject("SameAvatarName");
            EditorSceneManager.SaveScene(first.scene, "Assets/Pause.unity");
            string firstId = SnapshotCapture.AvatarId(first), secondId = SnapshotCapture.AvatarId(second);
            RecordingService.Select(first); UpdateRecordingService();
            Check(RecordingService.Running && !RecordingService.Paused, "A newly selected avatar starts recording");
            first.transform.localPosition = Vector3.right;
            RecordingService.SetPaused(true);
            Check(RecordingService.Paused && !RecordingService.Running &&
                HistoryStore.LoadLatest(firstId).entries.Any(e => e.item.EndsWith(" / m_LocalPosition") && e.value == "1, 0, 0"),
                "Pausing flushes the last active edit and detaches the recorder");
            int pausedCount = HistoryStore.Load(firstId, out _).Count;
            first.transform.localPosition = Vector3.right * 2;
            UpdateRecordingService(); UpdateRecordingService();
            Check(!RecordingService.Running && HistoryStore.Load(firstId, out _).Count == pausedCount,
                "Paused service updates create no snapshots or active recorder");
            RecordingService.Select(second); UpdateRecordingService();
            Check(RecordingService.Running && !RecordingService.Paused && (bool)Preference("IsPaused", firstId),
                "A same-name avatar has an independent pause preference");
            RecordingService.SetPaused(true);
            RecordingService.Select(first); UpdateRecordingService();
            Check(RecordingService.Paused && !RecordingService.Running, "Reselecting a paused avatar does not resume it");
            RecordingService.SetPaused(false);
            var resumed = HistoryStore.Load(firstId, out _);
            Check(RecordingService.Running && !RecordingService.Paused && (bool)Preference("IsPaused", secondId) &&
                resumed.Last().startsRecordingSegment && HistoryText.Build(resumed[resumed.Count - 2], resumed.Last()).Count == 0,
                "Resume persists a segment boundary and excludes paused changes after JSON reload");
            first.transform.localPosition = Vector3.right * 3;
            RecordingService.SetPaused(true);
            var later = HistoryStore.Load(firstId, out _);
            var diff = HistoryText.Build(later[later.Count - 2], later.Last());
            Check(diff.Any(a => a.change.Entry.item.EndsWith(" / m_LocalPosition") &&
                a.change.before.value == "2, 0, 0" && a.change.after.value == "3, 0, 0"),
                "Edits after resume use the fresh baseline instead of the pre-pause value");
            EditorSceneManager.SaveScene(first.scene);
            File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.AppendAllText(Path.Combine(Output, "validation-results.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    public static void PauseRestart()
    {
        try
        {
            EditorSceneManager.OpenScene("Assets/Pause.unity");
            var avatars = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            Check(avatars.Length == 2 && avatars.All(a => (bool)Preference("IsPaused", SnapshotCapture.AvatarId(a))),
                "Per-avatar pause preferences survive a Unity process restart");
            UpdateRecordingService();
            Check(RecordingService.Target && RecordingService.Paused && !RecordingService.Running,
                "Restored target stays paused without opening the log window");
            RecordingService.Select(null);
            File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); File.AppendAllText(Path.Combine(Output, "validation-results.txt"), e.ToString()); EditorApplication.Exit(1); }
    }

    static GameObject backgroundRoot;
    static int backgroundStep, backgroundCount;
    static double backgroundStarted, backgroundPhase;
    static bool backgroundRestart;

    public static void Background() { BeginBackground(false); }
    public static void BackgroundRestart() { BeginBackground(true); }

    static void BeginBackground(bool restart)
    {
        try
        {
            backgroundRestart = restart;
            if (restart)
            {
                EditorSceneManager.OpenScene("Assets/Background.unity");
                backgroundRoot = GameObject.Find("BackgroundAvatar");
                Check(RecordingService.TargetId == SnapshotCapture.AvatarId(backgroundRoot), "Recording target survives editor restart without opening window");
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                backgroundRoot = new GameObject("BackgroundAvatar");
                var renderer = backgroundRoot.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/TestMesh.asset");
                EditorSceneManager.SaveScene(backgroundRoot.scene, "Assets/Background.unity");
                RecordingService.Select(backgroundRoot);
            }
            backgroundStarted = EditorApplication.timeSinceStartup;
            EditorApplication.update += BackgroundTick;
        }
        catch (Exception e) { BackgroundFinish(e); }
    }

    static void BackgroundTick()
    {
        try
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - backgroundStarted > 30) throw new Exception("Background recording test timed out.");
            if (!string.IsNullOrEmpty(RecordingService.LastError)) throw new Exception(RecordingService.LastError);
            switch (backgroundStep)
            {
                case 0:
                    if (!RecordingService.Running || RecordingService.Target != backgroundRoot) return;
                    backgroundCount = HistoryStore.Load(RecordingService.TargetId, out var initialErrors).Count;
                    Check(initialErrors.Count == 0 && backgroundCount == (backgroundRestart ? 2 : 1), "Background recorder resumes without duplicate baseline");
        
            HistoryWindow.Open();
                    EditorWindow.GetWindow<HistoryWindow>().Close();
                    Check(RecordingService.Running, "Closing history window does not stop recording");
                    backgroundRoot.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, backgroundRestart ? 75 : 55);
                    backgroundPhase = now;
                    break;
                case 1:
                    if (now - backgroundPhase < 2.5) return;
                    var history = HistoryStore.Load(RecordingService.TargetId, out var errors);
                    Check(errors.Count == 0 && history.Count == backgroundCount + 1, "Edits made with window closed are written to disk");
                    Check(history.Last().entries.Any(e => e.category == "BlendShape" && e.value == (backgroundRestart ? "75" : "55")), "Background log preserves changed BlendShape value");
        
            HistoryWindow.Open();
                    EditorWindow.GetWindow<HistoryWindow>().Close();
                    Check(RecordingService.Running && HistoryStore.Load(RecordingService.TargetId, out _).Count == history.Count,
                        "Reopening the view creates no second recorder or duplicate record");
                    EditorSceneManager.MarkSceneDirty(backgroundRoot.scene);
                    EditorSceneManager.SaveScene(backgroundRoot.scene);
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    backgroundPhase = now;
                    break;
                case 2:
                    if (now - backgroundPhase < 2) return;
                    Check(!RecordingService.Running && !string.IsNullOrEmpty(RecordingService.TargetId), "Unloaded scene retains recording target for later restoration");
                    EditorSceneManager.OpenScene("Assets/Background.unity");
                    backgroundRoot = GameObject.Find("BackgroundAvatar");
                    backgroundPhase = now;
                    break;
                case 3:
                    if (now - backgroundPhase < 2) return;
                    Check(RecordingService.Running && RecordingService.Target == backgroundRoot, "Reopening scene resumes background recording automatically");
                    Check(HistoryStore.Load(RecordingService.TargetId, out _).Count == backgroundCount + 1, "Scene reopen does not create a duplicate snapshot");
                    BackgroundFinish(null);
                    return;
            }
            backgroundStep++;
        }
        catch (Exception e) { BackgroundFinish(e); }
    }

    static void BackgroundFinish(Exception error)
    {
        EditorApplication.update -= BackgroundTick;
        if (error != null) { Debug.LogException(error); Results.Add(error.ToString()); }
        File.AppendAllLines(Path.Combine(Output, "validation-results.txt"), Results);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
}
