using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AvatarChangeLog
{
    public static class SnapshotCapture
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static string AvatarId(GameObject root)
        {
            if (!root || EditorUtility.IsPersistent(root) || !root.scene.IsValid() ||
                string.IsNullOrEmpty(root.scene.path) || EditorSceneManager.IsPreviewScene(root.scene))
                throw new InvalidOperationException("保存済みシーン内のアバターを指定してください。Prefabはシーンに配置してください。");
            var id = GlobalObjectId.GetGlobalObjectIdSlow(root);
            if (id.targetObjectId == 0)
                throw new InvalidOperationException("アバターを配置した後、シーンを一度保存してください。");
            return id.ToString();
        }

        public static Snapshot Capture(GameObject root, string note)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("再生モードを終了してから記録してください。");
            var snapshot = new Snapshot {
                id = Guid.NewGuid().ToString("N"), avatarId = AvatarId(root), avatarName = root.name,
                createdUtc = DateTime.UtcNow.ToString("o", Invariant), note = note ?? "" };
            new CaptureContext(root, snapshot).Run();
            return snapshot;
        }

        sealed class CaptureContext
        {
            readonly GameObject root;
            readonly Snapshot snapshot;
            readonly Dictionary<Object, string> identities = new Dictionary<Object, string>();
            readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            readonly StringBuilder keyBuilder = new StringBuilder();
            readonly HashSet<string> warnings = new HashSet<string>(StringComparer.Ordinal);
            readonly Dictionary<Transform, List<(Component component, string slot)>> components = new Dictionary<Transform, List<(Component, string)>>();
            readonly List<Component> attached = new List<Component>();
            readonly List<Material> materials = new List<Material>();
            readonly Dictionary<(Shader shader, string property), string> materialLabels = new Dictionary<(Shader, string), string>();
            readonly Dictionary<Transform, string> paths = new Dictionary<Transform, string>();
            readonly Dictionary<Material, (int prefixLength, List<HistoryEntry> entries)> materialEntries = new Dictionary<Material, (int, List<HistoryEntry>)>();
            public CaptureContext(GameObject root, Snapshot snapshot) { this.root = root; this.snapshot = snapshot; }

            public void Run()
            {
                Index(root.transform, ".");
                foreach (var pair in paths)
                {
                    var t = pair.Key;
                    string path = pair.Value;
                    Add("オブジェクト", path, "Object", "存在", "あり");
                    Add("オブジェクト", path, "Object", "名前", t.name);
                    Add("オブジェクト", path, "Object", "表示", t.gameObject.activeSelf ? "ON" : "OFF");
                    Add("オブジェクト", path, "Object", "Layer", t.gameObject.layer.ToString(Invariant));
                    Add("オブジェクト", path, "Object", "Tag", t.gameObject.tag);
                    int missing = 0;
                    foreach (var indexed in components[t])
                    {
                        var component = indexed.component;
                        if (!component) { missing++; continue; }
                        string slot = indexed.slot;
                        string category = component is Transform ? "Transform" : "コンポーネント";
                        Add(category, path, slot, "存在", "あり");
                        ReadSerialized(component, category, path, slot);
                        if (component is SkinnedMeshRenderer skin && skin.sharedMesh)
                            for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++)
                                Add("BlendShape", path, slot, i + ": " + skin.sharedMesh.GetBlendShapeName(i), Number(skin.GetBlendShapeWeight(i)));
                        if (component is Renderer renderer)
                        {
                            renderer.GetSharedMaterials(materials);
                            for (int i = 0; i < materials.Count; i++)
                            {
                                string materialSlot = slot + " / Material " + i;
                                Add("マテリアル", path, materialSlot, "割り当て", Reference(materials[i]), materials[i] ? AssetDatabase.GetAssetPath(materials[i]) : null);
                                if (materials[i]) ReadMaterial(materials[i], path, materialSlot);
                            }
                        }
                    }
                    if (missing > 0)
                    {
                        Add("コンポーネント", path, "Missing Script", "個数", missing.ToString(Invariant));
                        Warn(path + ": Missing Scriptの設定内容は取得できません。");
                    }
                }
                snapshot.entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.key, b.key));
                snapshot.warnings = warnings.OrderBy(s => s, StringComparer.Ordinal).ToList();
            }

            void ReadMaterial(Material material, string path, string owner)
            {
                // Only reuse within this capture. Every later snapshot reads fresh values.
                if (!materialEntries.TryGetValue(material, out var cached))
                {
                    int start = snapshot.entries.Count;
                    ReadSerialized(material, "マテリアル", path, owner);
                    foreach (var value in MaterialValues(material, Reference))
                    {
                        var entry = Add("マテリアル", path, owner, "ShaderProperty/" + value.Key, value.Value);
                        entry.materialName = material.name;
                        var labelKey = (material.shader, value.Key);
                        if (!materialLabels.TryGetValue(labelKey, out string label))
                        {
                            label = MaterialLabels.For(material.shader, value.Key, value.Index);
                            materialLabels.Add(labelKey, label);
                        }
                        entry.displayName = label;
                        var type = value.Type;
                        entry.colorValue = type == ShaderPropertyType.Color;
                        if (type == ShaderPropertyType.Texture && value.Key.IndexOf('/') < 0)
                        {
                            var texture = material.GetTexture(value.Key);
                            entry.referencePath = texture ? AssetDatabase.GetAssetPath(texture) : null;
                        }
                        entry.numericValue = type == ShaderPropertyType.Float || type == ShaderPropertyType.Range || type == ShaderPropertyType.Int;
                    }
                    var entries = snapshot.entries.Skip(start).Where(e => e.category == "マテリアル").ToList();
                    materialEntries.Add(material, (owner.Length + 3, entries));
                    return;
                }
                foreach (var original in cached.entries)
                {
                    var entry = Add("マテリアル", path, owner, original.item.Substring(cached.prefixLength), original.value, original.referencePath);
                    entry.materialName = original.materialName;
                    entry.displayName = original.displayName;
                    entry.numericValue = original.numericValue;
                    entry.colorValue = original.colorValue;
                }
            }

            void Index(Transform t, string path)
            {
                paths.Add(t, path);
                identities[t.gameObject] = "Avatar/" + path;
                var counts = new Dictionary<Type, int>();
                t.GetComponents(attached);
                var indexed = new List<(Component, string)>(attached.Count);
                components.Add(t, indexed);
                foreach (var c in attached)
                {
                    if (!c) { indexed.Add((null, null)); continue; }
                    counts.TryGetValue(c.GetType(), out int n);
                    counts[c.GetType()] = n + 1;
                    string slot = c.GetType().FullName + " [" + n + "]";
                    indexed.Add((c, slot));
                    identities[c] = "Avatar/" + path + " / " + slot;
                }
                var names = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Transform child in t)
                {
                    names.TryGetValue(child.name, out int n);
                    names[child.name] = n + 1;
                    Index(child, path + "/" + Uri.EscapeDataString(child.name) + "[" + n + "]");
                }
            }

            HistoryEntry Add(string category, string path, string owner, string property, string value, string referencePath = null)
            {
                // Length prefixes prevent collisions even when object names contain separators.
                string key = EntryKey(keyBuilder, category, path, owner, property);
                if (!keys.Add(key)) throw new InvalidOperationException("記録項目が重複しています: " + category + " / " + path + " / " + owner + " / " + property);
                var entry = new HistoryEntry { key = key, category = category,
                    path = path, item = owner + " / " + property, value = value ?? "", referencePath = referencePath };
                snapshot.entries.Add(entry);
                return entry;
            }

            void ReadSerialized(Object target, string category, string path, string owner)
            {
                using (var so = new SerializedObject(target))
                {
                    var p = so.GetIterator();
                    bool children = true;
                    var managedIds = new HashSet<long>();
                    while (p.Next(children))
                    {
                        children = false;
                        if (Skip(p.propertyPath, target)) continue;
                        if (p.propertyType == SerializedPropertyType.Generic) { children = true; continue; }
                        if (p.propertyType == SerializedPropertyType.ManagedReference)
                        {
                            Add(category, path, owner, p.propertyPath + " (型)", p.managedReferenceFullTypename);
                            children = p.managedReferenceId >= 0 && managedIds.Add(p.managedReferenceId);
                            if (!children && p.managedReferenceId >= 0)
                                Warn(path + " / " + p.propertyPath + ": 共有・循環参照の再走査を省略しました。");
                            continue;
                        }
                        try
                        {
                            string value = Value(p);
                            if (value != null)
                            {
                                Object reference = p.propertyType == SerializedPropertyType.ObjectReference ? p.objectReferenceValue :
                                    p.propertyType == SerializedPropertyType.ExposedReference ? p.exposedReferenceValue : null;
                                Add(category, path, owner, p.propertyPath, value, reference ? AssetDatabase.GetAssetPath(reference) : null);
                            }
                            else Warn(path + " / " + owner + " / " + p.propertyPath + ": 未対応の型 " + p.propertyType);
                        }
                        catch (Exception e)
                        {
                            // Fail the entire snapshot; partial data must never look like a complete record.
                            throw new InvalidOperationException(path + " / " + owner + " / " + p.propertyPath + " の取得に失敗しました。", e);
                        }
                    }
                }
            }

            static bool Skip(string path, Object target)
            {
                string type = target.GetType().FullName;
                if (RecordingFields.IsConstraintCache(type, path) || RecordingFields.IsAvatarDebugCache(type, path)) return true;
                int separator = path.IndexOf('.');
                string first = separator < 0 ? path : path.Substring(0, separator);
                if (target is Material && first == "m_SavedProperties") return true;
                if (target is Renderer && first == "m_Materials") return true;
                if (first == "m_ObjectHideFlags" || first == "m_CorrespondingSourceObject" ||
                    first == "m_PrefabInstance" || first == "m_PrefabAsset" || first == "m_GameObject" ||
                    first == "m_Script" || first == "m_EditorClassIdentifier") return true;
                if (target is Transform && (first == "m_Children" || first == "m_Father" || first == "m_RootOrder" || first == "m_LocalEulerAnglesHint")) return true;
                return target is SkinnedMeshRenderer && first == "m_BlendShapeWeights";
            }

            string Value(SerializedProperty p)
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer: return Convert.ToString(p.boxedValue, Invariant);
                    case SerializedPropertyType.Boolean: return p.boolValue ? "ON" : "OFF";
                    case SerializedPropertyType.Float: return ((IFormattable)p.boxedValue).ToString("R", Invariant);
                    case SerializedPropertyType.String: return p.stringValue;
                    case SerializedPropertyType.Enum: return p.intValue.ToString(Invariant) +
                        (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? " (" + p.enumDisplayNames[p.enumValueIndex] + ")" : "");
                    case SerializedPropertyType.ArraySize: return p.intValue.ToString(Invariant);
                    case SerializedPropertyType.Character: return p.intValue.ToString(Invariant);
                    case SerializedPropertyType.LayerMask: return p.intValue.ToString(Invariant);
                    case SerializedPropertyType.ObjectReference:
                        return p.objectReferenceValue ? Reference(p.objectReferenceValue) :
                            p.objectReferenceInstanceIDValue != 0 ? "Missing reference" : "なし";
                    case SerializedPropertyType.ExposedReference: return Reference(p.exposedReferenceValue);
                    case SerializedPropertyType.Color:
                        var c = p.colorValue; return Numbers(c.r, c.g, c.b, c.a);
                    case SerializedPropertyType.Vector2:
                        var v2 = p.vector2Value; return Numbers(v2.x, v2.y);
                    case SerializedPropertyType.Vector3:
                        var v3 = p.vector3Value; return Numbers(v3.x, v3.y, v3.z);
                    case SerializedPropertyType.Vector4:
                        var v4 = p.vector4Value; return Numbers(v4.x, v4.y, v4.z, v4.w);
                    case SerializedPropertyType.Quaternion:
                        var q = p.quaternionValue; return Numbers(q.x, q.y, q.z, q.w);
                    case SerializedPropertyType.Rect:
                        var r = p.rectValue; return Numbers(r.x, r.y, r.width, r.height);
                    case SerializedPropertyType.Bounds:
                        var b = p.boundsValue; return Numbers(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
                    case SerializedPropertyType.Vector2Int: return p.vector2IntValue.ToString();
                    case SerializedPropertyType.Vector3Int: return p.vector3IntValue.ToString();
                    case SerializedPropertyType.RectInt: return p.rectIntValue.ToString();
                    case SerializedPropertyType.BoundsInt: return p.boundsIntValue.ToString();
                    case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                    case SerializedPropertyType.AnimationCurve:
                        var curve = p.animationCurveValue;
                        return curve.preWrapMode + "/" + curve.postWrapMode + ":" + string.Join(";", curve.keys.Select(k =>
                            Numbers(k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight) + "," + k.weightedMode));
                    case SerializedPropertyType.Gradient:
                        return JsonUtility.ToJson(new GradientValue { value = (Gradient)p.boxedValue });
                    case SerializedPropertyType.FixedBufferSize: return p.fixedBufferSize.ToString(Invariant);
                    default: return null;
                }
            }

            string Reference(Object obj)
            {
                if (!obj) return "なし";
                if (identities.TryGetValue(obj, out string local)) return local;
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long fileId))
                {
                    if (!(obj is MonoScript) && !hashes.ContainsKey(assetPath))
                    {
                        hashes[assetPath] = AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
                        Add("参照アセット", assetPath, "Asset", "保存済み内容の識別値", hashes[assetPath]);
                        if (EditorUtility.IsDirty(obj) && !(obj is Material))
                            Warn(assetPath + ": 未保存の変更があります。参照アセットの識別値には未保存の内容が反映されない場合があります。");
                    }
                    return obj.name + " (" + assetPath + "; " + guid + ":" + fileId + ")";
                }
                var global = GlobalObjectId.GetGlobalObjectIdSlow(obj);
                if (global.targetObjectId != 0) return obj.name + " (" + global + ")";
                Warn(obj.name + ": 永続IDのない参照です。再起動後に同一性を比較できない場合があります。");
                return obj.name + " (一時参照 " + obj.GetInstanceID().ToString(Invariant) + ")";
            }

            void Warn(string message) { warnings.Add(message); }
        }

        [Serializable] sealed class GradientValue { public Gradient value; }

        // Read live shader values, including defaults absent from m_SavedProperties.
        // Polling reads the same live values without formatting allocations.
        internal static IEnumerable<(string Key, string Value, int Index, ShaderPropertyType Type)> MaterialValues(Material material, Func<Object, string> reference)
        {
            var shader = material.shader;
            if (!shader) yield break;
            foreach (int i in UniqueShaderPropertyIndices(Enumerable.Range(0, shader.GetPropertyCount()).Select(shader.GetPropertyName)))
            {
                string name = shader.GetPropertyName(i);
                string value;
                var type = shader.GetPropertyType(i);
                switch (type)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: value = Number(material.GetFloat(name)); break;
                    case ShaderPropertyType.Int: value = material.GetInteger(name).ToString(Invariant); break;
                    case ShaderPropertyType.Color:
                        var c = material.GetColor(name); value = Numbers(c.r, c.g, c.b, c.a); break;
                    case ShaderPropertyType.Vector:
                        var v = material.GetVector(name); value = Numbers(v.x, v.y, v.z, v.w); break;
                    case ShaderPropertyType.Texture:
                        value = reference(material.GetTexture(name));
                        var scale = material.GetTextureScale(name);
                        var offset = material.GetTextureOffset(name);
                        yield return (name + "/Scale", Numbers(scale.x, scale.y), i, type);
                        yield return (name + "/Offset", Numbers(offset.x, offset.y), i, type);
                        break;
                    default: continue;
                }
                yield return (name, value, i, type);
            }
        }

        internal static IEnumerable<int> UniqueShaderPropertyIndices(IEnumerable<string> names)
        {
            // Inspector placeholders such as _DummyProperty may be repeated.
            // Getters address values by name: retain only the first declaration.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (string name in names)
            {
                if (seen.Add(name)) yield return index;
                index++;
            }
        }
        internal static string EntryKey(StringBuilder builder, string category, string path, string owner, string property)
        {
            // One builder per capture; preserve the existing length-prefixed identity exactly.
            builder.Clear();
            return builder.Append(category.Length).Append(':').Append(category)
                .Append(path.Length).Append(':').Append(path)
                .Append(owner.Length).Append(':').Append(owner)
                .Append(property.Length).Append(':').Append(property).ToString();
        }

        static string Number(float value) => value.ToString("R", Invariant);
        static string Numbers(params float[] values) => string.Join(", ", values.Select(Number));
    }

}
