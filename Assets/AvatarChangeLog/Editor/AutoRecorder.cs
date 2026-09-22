using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarChangeLog
{
    // Recording lifetime is owned by RecordingService, independently of the window.
    public sealed class AutoRecorder : IDisposable
    {
        readonly GameObject avatar;
        readonly Action<Snapshot, HashSet<string>> onSaved;
        readonly Action<string> onError;
        readonly CaptureSchedule schedule = new CaptureSchedule();
        Snapshot baseline;
        HashSet<string> retainedFiles;
        Dictionary<int, ShapeState> shapes = new Dictionary<int, ShapeState>(), nextShapes = new Dictionary<int, ShapeState>();
        Dictionary<int, MaterialState> materialStates = new Dictionary<int, MaterialState>(), nextMaterials = new Dictionary<int, MaterialState>();
        readonly List<Renderer> renderers = new List<Renderer>();
        readonly List<Material> sharedMaterials = new List<Material>();
        readonly HashSet<int> propertyIds = new HashSet<int>();
        Dictionary<int, (int id, ShaderPropertyType type)[]> shaderProperties = new Dictionary<int, (int, ShaderPropertyType)[]>(),
            nextShaderProperties = new Dictionary<int, (int, ShaderPropertyType)[]>();
        int shaderRevision = -1;
        bool hasPollBaseline, retentionFailed;
        bool autoFixPending, autoFixComplete;
        int autoFixToken;
        double nextShapePoll, nextFullCheck;
        public bool Running { get; private set; }

        sealed class ShapeState
        {
            public int mesh;
            public float[] weights;
        }

        sealed class MaterialState
        {
            public int shader;
            public readonly List<MaterialSample> values = new List<MaterialSample>();
        }

        public AutoRecorder(GameObject avatar, Snapshot previous, Action<Snapshot, HashSet<string>> onSaved, Action<string> onError)
        {
            this.avatar = avatar;
            baseline = previous;
            this.onSaved = onSaved;
            this.onError = onError;
        }

        public void Start(bool newSegment = false)
        {
            if (Running) return;
            if (Blocked) throw new InvalidOperationException("再生・コンパイル・インポートが終わってから自動記録を開始してください。");
            Record(newSegment ? "一時停止から再開（比較の基準）" : baseline == null ? "記録の開始" : "記録再開時の差分（記録できなかった期間の変更を含む）", newSegment);
            Poll();
            nextFullCheck = EditorApplication.timeSinceStartup + 3;
            Running = true;
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += MarkChanged;
            EditorApplication.projectChanged += MarkChanged;
            Undo.postprocessModifications += OnModifications;
            Undo.undoRedoPerformed += MarkChanged;
        }

        static bool Blocked => EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating;

        UndoPropertyModification[] OnModifications(UndoPropertyModification[] changes)
        {
            if (!Running || Blocked) return changes;
            if (changes == null) { MarkChanged(); return changes; }
            foreach (var change in changes)
            {
                var current = change.currentValue?.target;
                var previous = change.previousValue?.target;
                if ((!current && !previous) || IsRelevantTarget(current) || IsRelevantTarget(previous))
                { MarkChanged(); break; }
            }
            return changes; // Never alter the user's Undo chain.
        }

        bool IsRelevantTarget(UnityEngine.Object target)
        {
            if (!target || !avatar) return false;
            if (EditorUtility.IsPersistent(target)) return true;
            var transform = target is GameObject gameObject ? gameObject.transform : (target as Component)?.transform;
            if (transform) return transform.IsChildOf(avatar.transform);
            // Assets may be shared or referenced by arbitrary serialized components. Keep their notifications.
            return true;
        }

        void MarkChanged()
        {
            if (Running && !Blocked) schedule.Changed(EditorApplication.timeSinceStartup);
        }

        internal int BeginAutoFix()
        {
            if (!Running || !avatar || Blocked) throw new InvalidOperationException("記録中のアバターでAuto Fixを実行してください。");
            Flush(); // Keep any preceding manual edits in their own record.
            autoFixPending = true;
            autoFixComplete = false;
            return ++autoFixToken;
        }

        internal void CompleteAutoFix(int token)
        {
            if (Running && autoFixPending && token == autoFixToken) autoFixComplete = true;
        }

        void Tick()
        {
            if (!Running) return;
            if (!avatar) { Stop(false); return; }
            if (Blocked)
            {
                schedule.Reset();
                hasPollBaseline = false;
                shapes.Clear(); nextShapes.Clear();
                materialStates.Clear(); nextMaterials.Clear();
                shaderProperties.Clear(); nextShaderProperties.Clear();
                return;
            }
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (autoFixPending)
                {
                    // SDK callbacks run synchronously; the bridge completes on the next editor delay call.
                    // While importing, keep waiting rather than writing intermediate snapshots.
                    if (autoFixComplete)
                    {
                        Flush();
                        nextFullCheck = EditorApplication.timeSinceStartup + 3;
                    }
                    return;
                }
                if (now >= nextShapePoll)
                {
                    nextShapePoll = now + 0.2;
                    if (Poll()) schedule.Changed(now);
                }
                if (schedule.Ready(now) || (!schedule.Pending && now >= nextFullCheck))
                {
                    double started = EditorApplication.timeSinceStartup;
                    Record("自動記録");
                    // Avoid repeated heavy scans on unusually large avatars.
                    nextFullCheck = EditorApplication.timeSinceStartup + Math.Max(3, (EditorApplication.timeSinceStartup - started) * 10);
                    schedule.Reset();
                }
            }
            catch (Exception e)
            {
                Stop(false);
                onError("自動記録を停止しました: " + e.Message);
            }
        }

        bool Poll()
        {
            avatar.GetComponentsInChildren(true, renderers);
            bool shapeChanged = PollShapes();
            bool materialChanged = PollMaterials();
            bool changed = hasPollBaseline && (shapeChanged || materialChanged);
            hasPollBaseline = true;
            return changed;
        }

        bool PollShapes()
        {
            bool changed = false;
            nextShapes.Clear();
            foreach (var candidate in renderers)
            {
                if (!(candidate is SkinnedMeshRenderer renderer)) continue;
                var mesh = renderer.sharedMesh;
                int count = mesh ? mesh.blendShapeCount : 0;
                int id = renderer.GetInstanceID(), meshId = mesh ? mesh.GetInstanceID() : 0;
                if (!shapes.TryGetValue(id, out var state) || state.mesh != meshId || state.weights.Length != count)
                {
                    state = new ShapeState { mesh = meshId, weights = new float[count] };
                    changed = true;
                }
                for (int i = 0; i < count; i++)
                {
                    float value = renderer.GetBlendShapeWeight(i);
                    if (!state.weights[i].Equals(value)) changed = true;
                    state.weights[i] = value;
                }
                nextShapes[id] = state;
            }
            changed |= shapes.Count != nextShapes.Count;
            var old = shapes; shapes = nextShapes; nextShapes = old;
            return changed;
        }

        bool PollMaterials()
        {
            if (shaderRevision != ShaderImportRevision.Value)
            {
                shaderProperties.Clear();
                shaderRevision = ShaderImportRevision.Value;
            }
            nextShaderProperties.Clear();
            bool changed = false;
            nextMaterials.Clear();
            foreach (var renderer in renderers)
            {
                renderer.GetSharedMaterials(sharedMaterials);
                foreach (var material in sharedMaterials)
                {
                    if (!material || nextMaterials.ContainsKey(material.GetInstanceID())) continue;
                    int id = material.GetInstanceID();
                    if (!materialStates.TryGetValue(id, out var state)) { state = new MaterialState(); changed = true; }
                    var shader = material.shader;
                    int shaderId = shader ? shader.GetInstanceID() : 0;
                    if (state.shader != shaderId) changed = true;
                    state.shader = shaderId;
                    if (!nextShaderProperties.TryGetValue(shaderId, out var properties) && !shaderProperties.TryGetValue(shaderId, out properties))
                    {
                        var list = new List<(int, ShaderPropertyType)>();
                        propertyIds.Clear();
                        int count = shader ? shader.GetPropertyCount() : 0;
                        for (int i = 0; i < count; i++)
                        {
                            int property = shader.GetPropertyNameId(i);
                            if (propertyIds.Add(property)) list.Add((property, shader.GetPropertyType(i)));
                        }
                        properties = list.ToArray();
                    }
                    nextShaderProperties[shaderId] = properties;
                    int index = 0;
                    foreach (var property in properties)
                    {
                        var value = MaterialSample.Read(material, property.id, property.type);
                        if (index >= state.values.Count) { state.values.Add(value); changed = true; }
                        else
                        {
                            if (!state.values[index].Same(value)) changed = true;
                            state.values[index] = value;
                        }
                        index++;
                    }
                    if (index < state.values.Count) { state.values.RemoveRange(index, state.values.Count - index); changed = true; }
                    nextMaterials[id] = state;
                }
            }
            changed |= materialStates.Count != nextMaterials.Count;
            var old = materialStates; materialStates = nextMaterials; nextMaterials = old;
            var oldProperties = shaderProperties; shaderProperties = nextShaderProperties; nextShaderProperties = oldProperties;
            nextShaderProperties.Clear(); // Retain only shaders still referenced by this avatar.
            return changed;
        }

        void Record(string note, bool newSegment = false)
        {
            var snapshot = SnapshotCapture.Capture(avatar, note);
            snapshot.startsRecordingSegment = newSegment;
            snapshot.autoFix = autoFixPending;
            if (baseline != null && snapshot.avatarId != baseline.avatarId)
                throw new InvalidOperationException("シーンまたはアバターのIDが変わりました。アバターを指定し直してください。");
            if (!newSegment && baseline != null &&
                !HistoryDiff.HasChanges(baseline, snapshot))
            {
                autoFixPending = autoFixComplete = false;
                if (retentionFailed)
                {
                    FinishRetention(snapshot.avatarId);
                    if (!retentionFailed) onSaved(baseline, retainedFiles); // Refresh retained rows without creating a new record.
                }
                else onError(null);
                return;
            }
            HistoryStore.Save(snapshot); // Advance the baseline only after a successful write.
            baseline = snapshot;
            autoFixPending = autoFixComplete = false;
            FinishRetention(snapshot.avatarId);
            onSaved(snapshot, retainedFiles);
        }

        void FinishRetention(string avatarId)
        {
            retainedFiles = null;
            retentionFailed = !TryRetention(() => retainedFiles = HistoryStore.Prune(avatarId), onError);
        }

        internal static bool TryRetention(Action prune, Action<string> status)
        {
            try { prune(); }
            catch (Exception e)
            {
                status("記録は保存しましたが、古い履歴を整理できませんでした: " + e.Message);
                return false;
            }
            status(null);
            return true;
        }
        public void Flush()
        {
            if (Running && avatar && !Blocked)
            {
                Record(autoFixPending ? "VRChat SDK Auto Fix" : "自動記録");
                schedule.Reset();
            }
        }

        public void Stop(bool flush)
        {
            try { if (flush) Flush(); }
            catch (Exception e) { onError("最後の変更を保存できませんでした: " + e.Message); }
            finally
            {
                Running = false;
                autoFixPending = autoFixComplete = false;
                EditorApplication.update -= Tick;
                EditorApplication.hierarchyChanged -= MarkChanged;
                EditorApplication.projectChanged -= MarkChanged;
                Undo.postprocessModifications -= OnModifications;
                Undo.undoRedoPerformed -= MarkChanged;
                schedule.Reset();
                shaderProperties.Clear(); nextShaderProperties.Clear();
            }
        }

        public void Dispose() { Stop(true); }
    }

    // Include/include-file and shader reimports can change metadata without changing the Shader instance ID.
    internal sealed class ShaderImportRevision : AssetPostprocessor
    {
        internal static int Value { get; private set; }
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        { unchecked { Value++; } }
    }

    // Exact numeric comparison for polling. No formatting or probabilistic hash.
    internal struct MaterialSample
    {
        public int property, integer;
        public ShaderPropertyType type;
        public float x, y, z, w, scaleX, scaleY, offsetX, offsetY;

        public bool Same(MaterialSample other) => property == other.property && type == other.type && integer == other.integer &&
            x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w) &&
            scaleX.Equals(other.scaleX) && scaleY.Equals(other.scaleY) && offsetX.Equals(other.offsetX) && offsetY.Equals(other.offsetY);

        public static MaterialSample Read(Material material, int property, ShaderPropertyType type)
        {
            var value = new MaterialSample { property = property, type = type };
            switch (type)
            {
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: value.x = material.GetFloat(property); break;
                case ShaderPropertyType.Int: value.integer = material.GetInteger(property); break;
                case ShaderPropertyType.Color:
                    var color = material.GetColor(property);
                    value.x = color.r; value.y = color.g; value.z = color.b; value.w = color.a;
                    break;
                case ShaderPropertyType.Vector:
                    var vector = material.GetVector(property);
                    value.x = vector.x; value.y = vector.y; value.z = vector.z; value.w = vector.w;
                    break;
                case ShaderPropertyType.Texture:
                    var texture = material.GetTexture(property);
                    value.integer = texture ? texture.GetInstanceID() : 0;
                    var scale = material.GetTextureScale(property);
                    var offset = material.GetTextureOffset(property);
                    value.scaleX = scale.x; value.scaleY = scale.y; value.offsetX = offset.x; value.offsetY = offset.y;
                    break;
            }
            return value;
        }
    }

    [InitializeOnLoad]
    public static class RecordingService
    {
        static AutoRecorder recorder;
        static double nextCheck;
        static bool shuttingDown;
        public static GameObject Target { get; private set; }
        public static string TargetId => RecordingSettings.instance.AvatarId;
        public static string LastError { get; private set; }
        public static bool Running => recorder != null && recorder.Running;
        public static bool Paused => RecordingSettings.instance.IsPaused(TargetId);
        public static event Action<Snapshot, HashSet<string>> SnapshotSaved;

        internal static Action BeginAutoFix(GameObject sdkAvatar)
        {
            if (!sdkAvatar || sdkAvatar != Target || !Running || Paused) return null;
            try
            {
                var activeRecorder = recorder;
                int token = activeRecorder.BeginAutoFix();
                return () => activeRecorder.CompleteAutoFix(token);
            }
            catch (Exception e) { ReportError("Auto Fixの記録開始に失敗しました: " + e.Message); return null; }
        }

        static RecordingService()
        {
            // Restore only after Unity has finished loading/importing editor state.
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        public static void Select(GameObject target)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("再生モードを終了してからアバターを指定してください。");
            string id = target ? SnapshotCapture.AvatarId(target) : "";
            // Flush before switching so a failed write leaves the current target available for retry.
            recorder?.Flush();
            RecordingSettings.instance.Select(id);
            recorder?.Stop(false);
            recorder = null;
            Target = target;
            LastError = null;
            nextCheck = 0;
        }

        public static void SetPaused(bool paused)
        {
            if (!Target || string.IsNullOrEmpty(TargetId)) throw new InvalidOperationException("アバターを指定してください。");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("再生・コンパイル・インポートが終わってから記録状態を変更してください。");
            RefreshTargetId();
            if (Paused == paused) return;
            // Preserve edits made before the pause. A failed save must not silently stop recording.
            if (paused)
            {
                recorder?.Flush();
                RecordingSettings.instance.SetPaused(TargetId, true);
                recorder?.Stop(false);
                recorder = null;
            }
            else
            {
                // Keep the paused preference until a fresh baseline has been saved successfully.
                var resumed = new AutoRecorder(Target, null, Publish, ReportError);
                try
                {
                    resumed.Start(true);
                    RecordingSettings.instance.SetPaused(TargetId, false);
                    recorder?.Stop(false);
                    recorder = resumed;
                }
                catch { resumed.Stop(false); throw; }
            }
            nextCheck = 0;
        }

        static void Update()
        {
            if (shuttingDown || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 1;
            try
            {
                if (!Target)
                {
                    recorder?.Stop(false);
                    recorder = null;
                    string savedId = TargetId;
                    if (string.IsNullOrEmpty(savedId) || !GlobalObjectId.TryParse(savedId, out var global)) return;
                    Target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as GameObject;
                    // The selected scene may be closed; retain its ID and resume when reopened.
                    if (!Target) return;
                }
                string actualId = RefreshTargetId();
                if (Paused)
                {
                    recorder?.Stop(false);
                    recorder = null;
                    return;
                }
                if (Running) return;
                recorder?.Stop(false);
                var latest = HistoryStore.LoadLatest(actualId);
                recorder = new AutoRecorder(Target, latest, Publish, ReportError);
                recorder.Start();
            }
            catch (Exception e) { recorder?.Stop(false); ReportError(e.Message); }
        }

        static string RefreshTargetId()
        {
            string actualId = SnapshotCapture.AvatarId(Target);
            if (actualId != TargetId)
            {
                // Preserve a pause when the selected object moves to a different saved scene.
                recorder?.Stop(false);
                recorder = null;
                if (Paused) RecordingSettings.instance.SetPaused(actualId, true);
                RecordingSettings.instance.Select(actualId);
            }
            return actualId;
        }

        static void Publish(Snapshot snapshot, HashSet<string> retainedFiles)
        {
            // A view failing to render must not stop background recording.
            var listeners = SnapshotSaved;
            if (listeners == null) return;
            foreach (Action<Snapshot, HashSet<string>> listener in listeners.GetInvocationList())
                try { listener(snapshot, retainedFiles); }
                catch (Exception e) { Debug.LogException(e); }
        }

        static void ReportError(string message)
        {
            if (message != null && LastError != message) Debug.LogError("[Avatar Change Log] " + message);
            LastError = message;
            if (message != null) nextCheck = EditorApplication.timeSinceStartup + 5;
        }

        static void Shutdown()
        {
            shuttingDown = true;
            recorder?.Stop(true);
            recorder = null;
            EditorApplication.update -= Update;
        }
    }
}
