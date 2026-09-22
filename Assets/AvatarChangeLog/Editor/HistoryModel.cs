using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace AvatarChangeLog
{
    [Serializable]
    public sealed class HistoryEntry
    {
        public string key, category, path, item, value;
        // Capture metadata used for grouping and Inspector labels.
        public string referencePath;
        public string materialName;
        public string displayName;
        public bool numericValue;
        public bool colorValue;
    }

    [Serializable]
    public sealed class Snapshot
    {
        public const int CurrentFormat = 2;
        public int formatVersion = CurrentFormat;
        public string id, avatarId, avatarName, createdUtc, note;
        public bool startsRecordingSegment;
        public bool autoFix;
        public List<HistoryEntry> entries = new List<HistoryEntry>();
        public List<string> warnings = new List<string>();
    }

    public sealed class Change
    {
        public HistoryEntry before, after;
        public HistoryEntry Entry => after ?? before;
        public string Kind => before == null ? "追加" : after == null ? "削除" : "変更";
    }

    public static class RecordingFields
    {
        const string AvatarDescriptor = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        const string DescriptorOwner = AvatarDescriptor + " [";

        // Rebuilt by SDK validation from Animator state names; not an avatar setting.
        public static bool IsAvatarDebugCache(string type, string property) => type == AvatarDescriptor &&
            (property == "animationHashSet" || property.StartsWith("animationHashSet.", StringComparison.Ordinal));

        internal static bool IsAvatarDebugEntry(HistoryEntry entry)
        {
            if (entry.category != "コンポーネント" || !entry.item.StartsWith(DescriptorOwner, StringComparison.Ordinal)) return false;
            int separator = entry.item.IndexOf("] / ", DescriptorOwner.Length, StringComparison.Ordinal);
            if (separator <= DescriptorOwner.Length) return false;
            for (int i = DescriptorOwner.Length; i < separator; i++)
                if (entry.item[i] < '0' || entry.item[i] > '9') return false;
            return IsAvatarDebugCache(AvatarDescriptor, entry.item.Substring(separator + 4));
        }

        public static bool IsConstraintCache(string type, string property) =>
            (property == "cachedExecutionGroupIndex" || property == "latestValidExecutionGroupIndex") &&
            (type == "VRC.Dynamics.VRCConstraintBase" ||
             (type.StartsWith("VRC.SDK3.Dynamics.Constraint.Components.", StringComparison.Ordinal) && type.EndsWith("Constraint", StringComparison.Ordinal)));

    }

    public static class HistoryDiff
    {
        internal static void ValidatePair(Snapshot before, Snapshot after)
        {
            if (before == null || after == null) throw new ArgumentNullException();
            if (before.avatarId != after.avatarId)
                throw new InvalidOperationException("異なるアバターの記録は比較できません。");
        }

        // Capture and storage produce entries sorted by their unique stable key.
        // Use only for recorder baselines; general comparisons remain order-independent.
        internal static bool HasChanges(Snapshot before, Snapshot after)
        {
            ValidatePair(before, after);
            if (before.entries.Count != after.entries.Count) return true;
            for (int i = 0; i < before.entries.Count; i++)
                if (before.entries[i].key != after.entries[i].key || before.entries[i].value != after.entries[i].value) return true;
            return false;
        }

        public static List<Change> Compare(Snapshot before, Snapshot after)
        {
            ValidatePair(before, after);
            var left = before.entries.ToDictionary(e => e.key, StringComparer.Ordinal);
            var right = after.entries.ToDictionary(e => e.key, StringComparer.Ordinal);
            var changes = new List<Change>();
            void Add(HistoryEntry a, HistoryEntry b)
            {
                var change = new Change { before = a, after = b };
                changes.Add(change);
            }
            foreach (var entry in before.entries)
            {
                if (!right.TryGetValue(entry.key, out var next)) Add(entry, null);
                else if (entry.value != next.value) Add(entry, next);
            }
            foreach (var entry in after.entries)
                if (!left.ContainsKey(entry.key)) Add(null, entry);
            return changes.OrderBy(c => c.Entry.category, StringComparer.Ordinal)
                .ThenBy(c => c.Entry.path, StringComparer.Ordinal)
                .ThenBy(c => c.Entry.item, StringComparer.Ordinal).ToList();
        }

    }

    // Unity-independent debounce policy. Slider edits settle into a single record.
    public sealed class CaptureSchedule
    {
        public bool Pending { get; private set; }
        double firstChange, lastChange;
        public void Changed(double now)
        {
            if (!Pending) firstChange = now;
            lastChange = now;
            Pending = true;
        }
        public bool Ready(double now) => Pending && (now - lastChange >= 0.65 || now - firstChange >= 5);
        public void Reset() { Pending = false; }
    }

    public sealed class Activity
    {
        public Snapshot after;
        public Change change;
        public string referenceOwner;
        public string materialIdentity;
        public readonly List<Change> materialItems = new List<Change>();
        public int materialReferenceCount;
        public bool MaterialBatch => materialItems.Count > 1;
        public bool IsAutoFix => after != null && after.autoFix;
        public string MaterialName => details.Select(c => c.Entry.materialName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "マテリアル";
        public readonly List<Change> details = new List<Change>();
        public bool SharedMaterial => materialIdentity != null && materialReferenceCount > 1;
        public string Path => IsAutoFix ? "." : referenceOwner ?? change.Entry.path;
        public string Summary => IsAutoFix ? after.avatarName + "：Auto Fixによる変更（" + details.Count + "項目）" : MaterialBatch ? after.avatarName + "：" + MaterialName + "のマテリアルを変更（" + materialItems.Count + "項目）" : referenceOwner == null ? HistoryText.Describe(after.avatarName, change) :
            after.avatarName + "：「" + HistoryText.ReadablePath(referenceOwner) + "」の参照アセットが変更（追加 " +
            details.Count(c => c.before == null) + " 件・削除 " + details.Count(c => c.after == null) + " 件）";
        public string Id => after.id + "/" + change.Entry.key + "/" + referenceOwner;
        public List<Change> NestedItems => details.Where(c =>
            c.Entry.category == "参照アセット" || (c != change && HistoryText.IsObjectPresence(c)))
            .OrderBy(c => c.Entry.category == "参照アセット" ? 1 : 0)
            .ThenBy(c => c.Entry.path, StringComparer.Ordinal).ToList();
    }

    public sealed class ChangePreviewRow
    {
        static readonly char[] LineBreaks = { '\r', '\n', '\t' };
        public readonly string label, before, after, text, displayText, displayBefore, displayAfter, message;
        public readonly float[] beforeColor, afterColor;
        public ChangePreviewRow(Change change, string heading = null)
        {
            HistoryText.MaterialSlot(change.Entry, out _, out string property);
            label = heading ?? (!string.IsNullOrEmpty(change.Entry.displayName) ? change.Entry.displayName :
                property != null && property.StartsWith("ShaderProperty/", StringComparison.Ordinal) ? property.Substring(15) : property ?? HistoryText.Property(change.Entry));
            before = change.before?.value ?? "（項目なし）";
            after = change.after?.value ?? "（項目なし）";
            TryColor(change.before, out beforeColor);
            TryColor(change.after, out afterColor);
            string values = (beforeColor != null || afterColor != null ? "RGBA " : "") + before + " → " + after;
            text = label + "：" + values;
            string left = DisplayValue(change.before), right = DisplayValue(change.after);
            if (left == right && before != after)
            {
                if (change.Entry.numericValue || change.Entry.category == "BlendShape") { left = before; right = after; }
                else message = "値を" + change.Kind + "（詳細で確認）";
            }
            if (beforeColor == null && afterColor == null &&
                (left.Length > 60 || right.Length > 60 || before.IndexOfAny(LineBreaks) >= 0 || after.IndexOfAny(LineBreaks) >= 0))
                message = "値を" + change.Kind + "（全文は詳細で確認）";
            if (change.Entry.category == "Transform" && HistoryText.Property(change.Entry) == "m_LocalRotation")
                message = "回転を" + change.Kind + "（数値は詳細で確認）";
            if (HistoryText.Property(change.Entry) == "存在")
            { left = change.before == null ? "なし" : "あり"; right = change.after == null ? "なし" : "あり"; }
            displayBefore = left; displayAfter = right;
            displayText = label + "：" + (message ?? left + " → " + right);
        }

        static string DisplayValue(HistoryEntry entry)
        {
            if (entry == null) return "（項目なし）";
            string value = entry.value ?? "";
            string property = HistoryText.Property(entry);
            if (property == "m_Enabled" || property == "m_IsTrigger" || property == "表示")
            {
                if (value == "1" || value == "true" || value == "True" || value == "ON") return "有効";
                if (value == "0" || value == "false" || value == "False" || value == "OFF") return "無効";
            }
            if (!string.IsNullOrEmpty(entry.referencePath))
            {
                int marker = value.LastIndexOf(" (" + entry.referencePath + "; ", StringComparison.Ordinal);
                if (marker >= 0 && value.EndsWith(")", StringComparison.Ordinal)) return value.Substring(0, marker);
            }
            if (entry.category == "Transform" && (property == "m_LocalPosition" || property == "m_LocalScale"))
            {
                var parts = value.Split(',');
                if (parts.Length == 3) return "X " + parts[0].Trim() + " / Y " + parts[1].Trim() + " / Z " + parts[2].Trim();
            }
            return (entry.numericValue || entry.category == "BlendShape") && double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) && !double.IsInfinity(number) && !double.IsNaN(number) ?
                number.ToString("0.00#########", CultureInfo.InvariantCulture) : value;
        }

        internal static bool TryColor(HistoryEntry entry, out float[] values)
        {
            values = null;
            if (entry == null || !entry.colorValue || entry.value == null) return false;
            var parts = entry.value.Split(',');
            if (parts.Length != 4) return false;
            var parsed = new float[4];
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) ||
                    float.IsNaN(parsed[i]) || float.IsInfinity(parsed[i])) return false;
            values = parsed;
            return true;
        }
    }

    public sealed class ActivityPreview
    {
        public readonly List<ChangePreviewRow> rows;
        public readonly int remaining;
        public readonly bool savedInfo;
        public readonly string heading, message;
        public string DetailHint => remaining > 0 ? (rows.Count > 0 ? "ほか" : "設定 ") + remaining + "項目" : "";
        public string Text => string.Join("\n", rows.Select(r => r.displayText)
            .Concat(message == null ? Array.Empty<string>() : new[] { heading + "：" + message })
            .Concat(remaining > 0 ? new[] { DetailHint } : Array.Empty<string>()));
        public ActivityPreview(Activity activity)
        {
            rows = new List<ChangePreviewRow>();
            if (activity.IsAutoFix)
            {
                heading = "Auto Fix";
                message = activity.details.Count + "項目の変更（詳細で確認）";
                return;
            }
            if (activity.change.Entry.category != "マテリアル")
            {
                heading = HistoryText.TableItem(activity);
                string property = HistoryText.Property(activity.change.Entry);
                if (activity.referenceOwner != null)
                    message = "追加 " + activity.details.Count(c => c.before == null) + "件 / 削除 " + activity.details.Count(c => c.after == null) + "件";
                else if (activity.change.Entry.category == "参照アセット") message = "内容を更新（詳細で確認）";
                else if (property.Contains(".Array.") || property.EndsWith(" (型)", StringComparison.Ordinal) ||
                    (property.StartsWith("m_", StringComparison.Ordinal) && HistoryText.FriendlyProperty(property) == property))
                { heading = "設定"; message = "設定を" + activity.change.Kind + "（詳細で確認）"; }
                else rows.Add(new ChangePreviewRow(activity.change, heading));
                return;
            }
            heading = "マテリアル設定";
            var changes = activity.materialItems.Count > 0 ? activity.materialItems : new List<Change> { activity.change };
            var settings = changes.Where(c => c.Entry.category == "マテリアル")
                .OrderBy(c => c.Entry.colorValue ? 0 : c.Entry.item.Contains("ShaderProperty/") ? 1 : 2).ToList();
            rows.AddRange(settings.Where(c => c.Entry.item.Contains("ShaderProperty/") ||
                (HistoryText.MaterialSlot(c.Entry, out _, out string p) && p == "割り当て"))
                .Take(1).Select(c => new ChangePreviewRow(c)));
            remaining = Math.Max(0, settings.Count - rows.Count);
            savedInfo = changes.Any(c => c.Entry.category == "参照アセット");
            if (rows.Count == 0) message = "設定を" + activity.change.Kind + "（詳細で確認）";
        }
    }

    // Cached only for the visible page; activities are complete before presentation is created.
    public sealed class ActivityDisplay
    {
        public readonly Activity activity;
        public readonly string path, date, time, id, kind, category, counts, references, target;
        public readonly List<Change> nested;
        public readonly bool grouped;
        public readonly ActivityPreview preview;
        public ActivityDisplay(Activity activity)
        {
            this.activity = activity;
            path = HistoryText.ReadablePath(activity.Path); id = activity.Id;
            kind = activity.IsAutoFix || activity.MaterialBatch || activity.referenceOwner != null ? "変更" : activity.change.Kind;
            category = activity.IsAutoFix ? "Auto Fix" : HistoryText.CategoryLabel(activity.change.Entry.category);
            bool valid = DateTimeOffset.TryParse(activity.after.createdUtc, out var timestamp);
            date = valid ? timestamp.ToLocalTime().ToString("yyyy/MM/dd") : activity.after.createdUtc;
            time = valid ? timestamp.ToLocalTime().ToString("HH:mm") : activity.after.createdUtc;
            nested = activity.IsAutoFix ? new List<Change>() : activity.NestedItems;
            grouped = !activity.IsAutoFix && activity.materialIdentity == null && (activity.referenceOwner != null || nested.Any(c => c != activity.change));
            counts = grouped ? "子オブジェクト " + nested.Count(HistoryText.IsObjectPresence) + " 件・参照アセット " +
                nested.Count(c => c.Entry.category == "参照アセット") + " 件" : null;
            references = activity.SharedMaterial ? "参照先 " + activity.materialReferenceCount + " 箇所" : null;
            target = HistoryText.TableTarget(activity);
            preview = new ActivityPreview(activity);
        }
    }

    // Scoped to the window's search session. Activities are immutable after Build completes.
    internal sealed class ActivitySearchCache
    {
        readonly Dictionary<Activity, (string[] fields, long characters, bool complete)> entries = new Dictionary<Activity, (string[], long, bool)>();
        readonly int maxEntries;
        readonly long maxCharacters;
        long characters;
        internal int Count => entries.Count;
        internal bool CanAdd => entries.Count < maxEntries && characters < maxCharacters;
        internal long AvailableCharacters => maxCharacters - characters;
        internal ActivitySearchCache(long maxCharacters = 1000000, int maxEntries = 512)
        { this.maxCharacters = maxCharacters; this.maxEntries = maxEntries; }
        internal bool TryGet(Activity activity, out string[] fields, out bool complete)
        {
            bool found = entries.TryGetValue(activity, out var cached);
            fields = cached.fields;
            complete = cached.complete;
            return found;
        }
        internal string[] Store(Activity activity, HashSet<string> fields, long size, bool complete)
        {
            var values = fields.ToArray();
            entries.Add(activity, (values, size, complete)); characters += size;
            return values;
        }
        internal void Remove(Activity activity)
        {
            if (!entries.TryGetValue(activity, out var entry)) return;
            characters -= entry.characters; entries.Remove(activity);
        }
        internal string[] Get(Activity activity, HashSet<string> fields, long size)
        {
            bool Add(string field)
            {
                if (!string.IsNullOrEmpty(field) && fields.Add(field)) size += field.Length;
                return size > maxCharacters - characters;
            }
            // Stop building immediately at the budget; the caller searches without retaining an index.
            if (size > maxCharacters - characters || HistoryText.VisitFormattedDetails(activity, Add)) return null;
            return Store(activity, fields, size, true);
        }
        internal void Retain(HashSet<Snapshot> snapshots)
        {
            foreach (var activity in entries.Keys.Where(a => !snapshots.Contains(a.after)).ToArray())
                Remove(activity);
        }
        internal void Clear() { entries.Clear(); characters = 0; }
    }

    public sealed class ChangeDetailDisplay
    {
        public readonly Change change;
        public readonly string label, locations;
        internal ChangeDetailDisplay(Change change, string label, string locations)
        { this.change = change; this.label = label; this.locations = locations; }
    }

    public static class HistoryText
    {
        public static string CategoryLabel(string category) => category == "BlendShape" ? "シェイプキー" :
            category == "Transform" ? "トランスフォーム" : category;

        public static string TableTarget(Activity activity)
        {
            if (activity.IsAutoFix) return activity.after.avatarName;
            if (activity.materialIdentity != null) return activity.MaterialName;
            if (activity.change.Entry.category == "マテリアル" && !string.IsNullOrEmpty(activity.change.Entry.materialName))
                return activity.change.Entry.materialName;
            string path = activity.Path;
            if (path == ".") return "アバター本体";
            return path.StartsWith("./", StringComparison.Ordinal) ? Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1)) :
                path.Substring(path.LastIndexOf('/') + 1);
        }

        public static string TableItem(Activity activity)
        {
            if (activity.IsAutoFix) return "Auto Fix";
            if (activity.referenceOwner != null) return "参照アセット";
            if (activity.MaterialBatch) return "マテリアル";
            var entry = activity.change.Entry;
            string property = Property(entry);
            if (entry.category == "BlendShape") return ShapeName(property);
            if (entry.category == "マテリアル") return MaterialProperty(entry, property) ?? FriendlyProperty(property);
            if (entry.category == "コンポーネント")
            {
                string owner = entry.item.Split(new[] { " / " }, StringSplitOptions.None)[0];
                owner = owner.Substring(owner.LastIndexOf('.') + 1);
                return owner + (property == "存在" ? "" : " / " + FriendlyProperty(property));
            }
            return property == "存在" || entry.category == "参照アセット" ? entry.category : FriendlyProperty(property);
        }

        static string ExportCell(string value) => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        static string Timestamp(Snapshot snapshot) => DateTimeOffset.TryParse(snapshot.createdUtc, out var time) ?
            time.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : snapshot.createdUtc;

        public static IEnumerable<string> ExportDocument(IReadOnlyList<Activity> rows, string category, string search)
        {
            yield return "Avatar Change Log v0.1.1";
            yield return "対象: " + ExportCell(string.Join("、", rows.Select(a => a.after.avatarName).Distinct()));
            yield return "分類: " + ExportCell(category) + " / 検索: " + ExportCell(string.IsNullOrWhiteSpace(search) ? "なし" : search);
            yield return "件数: " + rows.Count + " / 新しい順";
            yield return "";
            yield return "【一覧】";
            yield return "ID | 日時 | 操作 | 対象 / 分類 | 変更内容";
            for (int i = 0; i < rows.Count; i++)
            {
                var activity = rows[i];
                var preview = new ActivityPreview(activity);
                string kind = activity.IsAutoFix || activity.MaterialBatch || activity.referenceOwner != null ? "変更" : activity.change.Kind;
                yield return (i + 1).ToString("D3") + " | " + ExportCell(Timestamp(activity.after)) + " | " + kind + " | " +
                    ExportCell(TableTarget(activity) + " / " + (activity.IsAutoFix ? "Auto Fix" : CategoryLabel(activity.change.Entry.category))) + " | " +
                    ExportCell(preview.rows.Count > 0 ? preview.rows[0].label : preview.heading);
                foreach (var row in preview.rows)
                {
                    if (row.message != null) yield return "      " + ExportCell(row.message);
                    else
                    {
                        yield return "      変更前: " + ExportCell(row.beforeColor == null ? row.displayBefore : "RGBA " + row.before);
                        yield return "      変更後: " + ExportCell(row.afterColor == null ? row.displayAfter : "RGBA " + row.after);
                    }
                }
                if (preview.message != null) yield return "      " + ExportCell(preview.message);
                if (preview.remaining > 0) yield return "    " + preview.DetailHint + " → 詳細 #" + (i + 1).ToString("D3");
            }
            yield return "";
            yield return "【詳細】一覧と同じIDで参照できます。";
            for (int i = 0; i < rows.Count; i++)
            {
                yield return "";
                yield return "--- 詳細 #" + (i + 1).ToString("D3") + " / " + ExportCell(Timestamp(rows[i].after)) + " ---";
                yield return Export(rows[i]);
            }
        }

        public static string Property(HistoryEntry entry)
        {
            int separator = entry.item.IndexOf(" / ", StringComparison.Ordinal);
            return separator < 0 ? entry.item : entry.item.Substring(separator + 3);
        }

        public static string ReadablePath(string path)
        {
            if (path == ".") return "アバター本体";
            if (!path.StartsWith("./", StringComparison.Ordinal)) return path;
            // Decode individual names, retaining same-name indices for disambiguation.
            return string.Join(" / ", path.Split('/').Where(s => s != ".").Select(s => Uri.UnescapeDataString(s)));
        }

        static string ShapeName(string property)
        {
            int colon = property.IndexOf(": ", StringComparison.Ordinal);
            return colon < 0 ? property : property.Substring(colon + 2);
        }

        static string MaterialProperty(HistoryEntry entry, string property)
        {
            const string marker = " / ShaderProperty/";
            int index = property.IndexOf(marker, StringComparison.Ordinal);
            return index < 0 ? null : string.IsNullOrEmpty(entry.displayName) ? property.Substring(index + marker.Length) : entry.displayName;
        }
        public static string Describe(string avatarName, Change change)
        {
            var entry = change.Entry;
            string property = Property(entry);
            string subject;
            switch (entry.category)
            {
                case "BlendShape":
                    string shape = ShapeName(property);
                    subject = "シェイプキー「" + shape + "」の数値";
                    break;
                case "Transform":
                    subject = "「" + ReadablePath(entry.path) + "」の" + FriendlyProperty(property);
                    break;
                case "オブジェクト":
                    subject = "「" + ReadablePath(entry.path) + "」";
                    if (property != "存在") subject += "の" + property;
                    break;
                case "マテリアル":
                    string label = MaterialProperty(entry, property);
                    subject = label == null ? "マテリアルの「" + FriendlyProperty(property) + "」" :
                        "マテリアル「" + entry.materialName + "」の「" + label + "」" + (entry.numericValue ? "の数値" : "");
                    break;
                case "参照アセット":
                    return avatarName + "：参照アセット「" + entry.path + "」" +
                        (change.before == null ? "を追加" : change.after == null ? "の参照を削除" : "の内容が更新");
                default:
                    string owner = entry.item.Split(new[] { " / " }, StringSplitOptions.None)[0];
                    int dot = owner.LastIndexOf('.');
                    if (dot >= 0) owner = owner.Substring(dot + 1);
                    subject = "「" + owner + "」" + (property == "存在" ? "" : "の「" + FriendlyProperty(property) + "」");
                    break;
            }
            string prefix = avatarName + "：" + subject;
            if (change.before == null) return prefix + "を追加" + (property == "存在" ? "" : "（" + Short(change.after.value) + "）");
            if (change.after == null) return prefix + "を削除";
            return prefix + "が " + Short(change.before.value) + " → " + Short(change.after.value) + " に変更";
        }

        public static string CardTitle(Activity activity)
        {
            if (activity.IsAutoFix) return activity.after.avatarName + " › Auto Fix";
            if (activity.MaterialBatch) return activity.MaterialName + "：マテリアルを変更（" + activity.materialItems.Count + "項目）";
            var entry = activity.change.Entry;
            string path = activity.Path;
            string target = path == "." ? "アバター本体" : string.Join(" › ", path.Split('/').Where(s => s != ".").Reverse().Take(2).Reverse().Select(s =>
                Uri.UnescapeDataString(s.EndsWith("[0]", StringComparison.Ordinal) ? s.Substring(0, s.Length - 3) : s)));
            if (activity.referenceOwner != null) return target + " › 参照アセット";
            string property = Property(entry);
            if (entry.category == "BlendShape")
            {
                return target + " › " + ShapeName(property);
            }
            if (entry.category == "マテリアル")
            {
                string label = MaterialProperty(entry, property);
                if (label != null) return (string.IsNullOrEmpty(entry.materialName) ? target : entry.materialName) + " › " + label;
            }
            if (entry.category == "コンポーネント")
            {
                string owner = entry.item.Split(new[] { " / " }, StringSplitOptions.None)[0];
                owner = owner.Substring(owner.LastIndexOf('.') + 1);
                return target + " › " + owner + (property == "存在" ? "" : " › " + FriendlyProperty(property));
            }
            return target + (property == "存在" || entry.category == "参照アセット" ? "" : " › " + FriendlyProperty(property));
        }

        public static string CardValue(Activity activity)
        {
            if (activity.IsAutoFix) return activity.details.Count + "項目の変更（詳細で確認）";
            if (activity.MaterialBatch) return "各項目の変更前後は詳細で確認";
            var change = activity.change;
            if (activity.referenceOwner != null || Property(change.Entry) == "存在") return "";
            if (change.Entry.category == "参照アセット") return "参照アセットの内容を更新";
            return change.before == null ? "追加後：" + Short(change.after.value) :
                change.after == null ? "削除前：" + Short(change.before.value) : Short(change.before.value) + " → " + Short(change.after.value);
        }

        internal static string FriendlyProperty(string property)
        {
            switch (property)
            {
                case "m_LocalPosition": return "位置";
                case "m_LocalRotation": return "回転";
                case "m_LocalScale": return "スケール";
                case "m_Enabled": return "有効状態";
                case "m_Center": return "中心";
                case "m_Size": return "大きさ";
                case "m_Radius": return "半径";
                case "m_Height": return "高さ";
                case "m_IsTrigger": return "トリガー";
                case "m_Name": return "名前";
                case "表示": return "表示状態";
                case "Layer": return "レイヤー";
                case "Tag": return "タグ";
                case "存在": return "コンポーネント";
                default: return property;
            }
        }
        static string Short(string value)
        {
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 100 ? value : value.Substring(0, 100) + "…";
        }

        public static List<Activity> Build(Snapshot before, Snapshot after)
        {
            // Resuming establishes a fresh baseline; paused edits are never presented as history.
            if (after != null && after.startsRecordingSegment)
            {
                HistoryDiff.ValidatePair(before, after);
                return new List<Activity>();
            }
            var changes = HistoryDiff.Compare(before, after);
            if (after.autoFix)
            {
                if (changes.Count == 0) return new List<Activity>();
                var activity = new Activity { after = after, change = changes[0] };
                activity.details.AddRange(changes);
                return new List<Activity> { activity };
            }
            var objectChanges = changes.Where(c => IsObjectPresence(c) && c.Kind != "変更").ToList();
            var objectsByPath = objectChanges.ToDictionary(c => (c.Kind, c.Entry.path));
            var rootCache = new Dictionary<(string kind, string path), Change>();
            Change FindRoot(string kind, string path)
            {
                if (objectsByPath.Count == 0) return null;
                var key = (kind, path);
                if (rootCache.TryGetValue(key, out var cached)) return cached;
                Change root = null;
                for (string ancestor = path; ;)
                {
                    if (objectsByPath.TryGetValue((kind, ancestor), out var parent)) root = parent;
                    int separator = ancestor.LastIndexOf('/');
                    if (separator < 0) break;
                    ancestor = ancestor.Substring(0, separator);
                }
                rootCache.Add(key, root);
                return root;
            }
            var components = changes.Where(c => c.Entry.category != "オブジェクト" && Property(c.Entry) == "存在")
                .ToLookup(c => (c.Kind, c.Entry.path));
            var groups = new Dictionary<Change, Activity>();
            var referenceGroups = new Dictionary<string, Activity>(StringComparer.Ordinal);
            var materialGroups = new Dictionary<string, Activity>(StringComparer.Ordinal);
            Dictionary<(string path, string owner), string> previousMaterials = null, currentMaterials = null;

            Activity Group(Change primary)
            {
                if (!groups.TryGetValue(primary, out var group))
                { group = new Activity { after = after, change = primary }; groups.Add(primary, group); }
                return group;
            }

            foreach (var c in changes.Where(c => c.Entry.category != "参照アセット"))
            {
                var root = FindRoot(c.Kind, c.Entry.path);
                if (root == null)
                    root = components[(c.Kind, c.Entry.path)].FirstOrDefault(o =>
                        c.Entry.item.StartsWith(o.Entry.item.Substring(0, o.Entry.item.Length - "存在".Length), StringComparison.Ordinal));
                if (root == null && c.Entry.category == "マテリアル" && MaterialSlot(c.Entry, out string owner, out string property) && property != "割り当て")
                {
                    previousMaterials = previousMaterials ?? MaterialAssignments(before);
                    currentMaterials = currentMaterials ?? MaterialAssignments(after);
                    var slot = (c.Entry.path, owner);
                    if (previousMaterials.TryGetValue(slot, out string oldId) && currentMaterials.TryGetValue(slot, out string id) && oldId == id)
                    {
                        var key = id;
                        if (!materialGroups.TryGetValue(key, out var materialGroup))
                        {
                            materialGroup = Group(c);
                            materialGroup.materialIdentity = id;
                            materialGroups.Add(key, materialGroup);
                        }
                        materialGroup.details.Add(c);
                        continue;
                    }
                }
                Group(root ?? c).details.Add(c);
            }

            Dictionary<string, HashSet<string>> addedOwners = null, removedOwners = null;
            Dictionary<string, HashSet<string>> materialIdsByPath = null;
            foreach (var c in changes.Where(c => c.Entry.category == "参照アセット"))
            {
                // Actual file-content edits remain independent of object addition/removal.
                if (c.Kind == "変更")
                {
                    // Attach only a directly referenced material file, never a dependency hash.
                    if (materialIdsByPath == null)
                    {
                        materialIdsByPath = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                        if (currentMaterials != null)
                            foreach (var entry in after.entries)
                            {
                                if (entry.category != "マテリアル" || entry.referencePath == null ||
                                    !MaterialSlot(entry, out string owner, out string property) || property != "割り当て" ||
                                    !currentMaterials.TryGetValue((entry.path, owner), out string id)) continue;
                                if (!materialIdsByPath.TryGetValue(entry.referencePath, out var identities))
                                    materialIdsByPath.Add(entry.referencePath, identities = new HashSet<string>(StringComparer.Ordinal));
                                identities.Add(id);
                            }
                    }
                    if (materialIdsByPath.TryGetValue(c.Entry.path, out var ids) && ids.Count == 1 &&
                        materialGroups.TryGetValue(ids.First(), out var materialGroup)) materialGroup.details.Add(c);
                    else Group(c).details.Add(c);
                    continue;
                }
                var owners = c.before == null ? addedOwners ?? (addedOwners = ReferenceOwners(after)) :
                    removedOwners ?? (removedOwners = ReferenceOwners(before));
                var paths = owners.TryGetValue(c.Entry.path, out var known) ? known : new HashSet<string> { "." };
                var assigned = new HashSet<Activity>();
                foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    var root = FindRoot(c.Kind, path);
                    Activity group;
                    if (root != null) group = Group(root);
                    else if (!referenceGroups.TryGetValue(path, out group))
                    {
                        group = new Activity { after = after, change = c, referenceOwner = path };
                        referenceGroups.Add(path, group);
                    }
                    // Shared assets appear once in each relevant parent's details, never as stray rows.
                    if (assigned.Add(group)) group.details.Add(c);
                }
            }
            foreach (var group in materialGroups.Values)
            {
                group.materialItems.AddRange(group.details.GroupBy(c =>
                {
                    MaterialSlot(c.Entry, out _, out string property);
                    return (c.Entry.category, property ?? c.Entry.item, c.before?.value, c.after?.value);
                }).Select(g => g.First()));
                group.materialReferenceCount = group.details.Where(c => c.Entry.category == "マテリアル").Select(c =>
                { MaterialSlot(c.Entry, out string owner, out _); return (c.Entry.path, owner); }).Distinct().Count();
            }
            return groups.Values.Concat(referenceGroups.Values)
                .OrderBy(a => a.change.Entry.category, StringComparer.Ordinal)
                .ThenBy(a => a.Path, StringComparer.Ordinal)
                .ThenBy(a => a.change.Entry.item, StringComparer.Ordinal).ToList();
        }

        public static bool IsObjectPresence(Change c) => c.Entry.category == "オブジェクト" && Property(c.Entry) == "存在";

        internal static bool MaterialSlot(HistoryEntry entry, out string owner, out string property)
        {
            owner = property = null;
            int slot = entry.item.IndexOf(" / Material ", StringComparison.Ordinal);
            int separator = slot < 0 ? -1 : entry.item.IndexOf(" / ", slot + " / Material ".Length, StringComparison.Ordinal);
            if (separator < 0) return false;
            owner = entry.item.Substring(0, separator);
            property = entry.item.Substring(separator + 3);
            return true;
        }

        static Dictionary<(string path, string owner), string> MaterialAssignments(Snapshot snapshot)
        {
            var result = new Dictionary<(string, string), string>();
            foreach (var entry in snapshot.entries)
            {
                if (entry.category != "マテリアル" || !MaterialSlot(entry, out string owner, out string property) || property != "割り当て") continue;
                string value = entry.value;
                int start = value.LastIndexOf("; ", StringComparison.Ordinal);
                if (start < 0 || !value.EndsWith(")", StringComparison.Ordinal)) continue;
                string identity = value.Substring(start + 2, value.Length - start - 3);
                int colon = identity.IndexOf(':');
                if (colon != 32 || !Guid.TryParseExact(identity.Substring(0, colon), "N", out var guid) ||
                    !long.TryParse(identity.Substring(colon + 1), out long fileId)) continue;
                result[(entry.path, owner)] = guid.ToString("N") + ":" + fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }

        static Dictionary<string, HashSet<string>> ReferenceOwners(Snapshot snapshot)
        {
            var paths = new HashSet<string>(snapshot.entries.Where(e => e.category == "参照アセット").Select(e => e.path), StringComparer.Ordinal);
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var entry in snapshot.entries.Where(e => e.category != "参照アセット"))
            {
                string path = entry.referencePath;
                if (path == null || !paths.Contains(path)) continue;
                if (!result.TryGetValue(path, out var owners)) result[path] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(entry.path);
            }
            return result;
        }

        public static string[] SearchWords(string search) => string.IsNullOrWhiteSpace(search) ? Array.Empty<string>() :
            search.Normalize(System.Text.NormalizationForm.FormKC).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        public static bool Matches(Activity activity, string category, string search) => MatchesWords(activity, category, SearchWords(search));

        public static bool MatchesWords(Activity activity, string category, string[] words) => MatchesWords(activity, category, words, null);

        internal static bool MatchesWords(Activity activity, string category, string[] words, ActivitySearchCache cache)
        {
            if (!string.IsNullOrEmpty(category) && (activity.IsAutoFix ? !activity.details.Any(c => c.Entry.category == category) :
                activity.change.Entry.category != category)) return false;
            if (words.Length == 0) return true;
            var matched = new bool[words.Length];
            int remaining = words.Length;
            bool Match(string field)
            {
                for (int i = 0; i < words.Length; i++)
                    if (!matched[i] && field.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    { matched[i] = true; remaining--; }
                return remaining == 0;
            }
            if (cache != null && cache.TryGet(activity, out var cached, out bool complete))
            {
                foreach (string field in cached) if (Match(field)) return true;
                if (complete) return false;
                // A raw-field hit may have stopped before later fields. Rebuild only when a new query needs them.
                cache.Remove(activity);
            }
            var fields = cache != null && cache.CanAdd ? new HashSet<string>(StringComparer.Ordinal) : null;
            long size = 0;
            bool Collect(string field)
            {
                bool found = Match(field);
                if (fields != null && !string.IsNullOrEmpty(field) && fields.Add(field))
                {
                    size += field.Length;
                    if (size > cache.AvailableCharacters) fields = null;
                }
                return found;
            }
            foreach (string field in SearchSummaryFields(activity))
                if (Collect(field)) return true;
            // Raw names and values often suffice; format every detail only if the query still needs it.
            if (VisitRawDetails(activity, Collect))
            {
                if (fields != null) cache.Store(activity, fields, size, false);
                return true;
            }
            var details = fields == null ? null : cache.Get(activity, fields, size);
            if (details == null) return VisitFormattedDetails(activity, Match);
            foreach (string field in details) if (Match(field)) return true;
            return false;
        }
        static string NormalizeSearch(string value) => string.IsNullOrEmpty(value) ? "" : value.Normalize(System.Text.NormalizationForm.FormKC);
        static IEnumerable<string> SearchSummaryFields(Activity activity)
        {
            // Keep cheap summary matches ahead of preview/detail construction, including when the cache is full.
            yield return NormalizeSearch(CardTitle(activity)); yield return NormalizeSearch(CardValue(activity)); yield return NormalizeSearch(activity.Summary);
            yield return NormalizeSearch(activity.after.note); yield return NormalizeSearch(TableTarget(activity)); yield return NormalizeSearch(TableItem(activity));
            var preview = new ActivityPreview(activity);
            yield return NormalizeSearch(preview.Text); yield return NormalizeSearch(ReadablePath(activity.Path));
            string kind = activity.change.Entry.category;
            yield return NormalizeSearch(kind); yield return NormalizeSearch(CategoryLabel(kind));
            yield return NormalizeSearch(activity.referenceOwner != null ? "変更" : activity.change.Kind);
            if (preview.savedInfo) yield return "保存情報の更新あり";
        }
        static bool VisitRawDetails(Activity activity, Func<string, bool> visit)
        {
            bool Value(string value) => !string.IsNullOrEmpty(value) && visit(NormalizeSearch(value));
            bool Entry(HistoryEntry entry) => entry != null &&
                (Value(ReadablePath(entry.path)) || Value(entry.item) || Value(entry.displayName) ||
                 Value(entry.materialName) || Value(entry.value) || Value(entry.referencePath));
            foreach (var change in activity.details)
                if ((activity.IsAutoFix && Value(CategoryLabel(change.Entry.category))) || Entry(change.before) || Entry(change.after)) return true;
            return false;
        }
        internal static bool VisitFormattedDetails(Activity activity, Func<string, bool> visit)
        {
            foreach (var change in activity.details)
                if (visit(NormalizeSearch(new ChangePreviewRow(change).displayText))) return true;
            return false;
        }

        public static List<ChangeDetailDisplay> DetailPage(Activity activity, int page, int size)
        {
            var items = activity.materialItems.Count > 0 ? activity.materialItems : activity.details;
            (string category, string property, string before, string after) Key(Change change)
            {
                MaterialSlot(change.Entry, out _, out string property);
                return (change.Entry.category, property ?? change.Entry.item, change.before?.value, change.after?.value);
            }
            string Location(Change change)
            {
                MaterialSlot(change.Entry, out string owner, out _);
                return ReadablePath(change.Entry.path) + (owner == null ? "" : " / " + owner);
            }
            // Auto Fix details are individual changes, not a shared-material group.
            // Read only the requested page and never mix distinct targets with equal values.
            var locations = activity.IsAutoFix ? null : activity.details.ToLookup(Key);
            var rows = new List<ChangeDetailDisplay>();
            foreach (var item in items.Skip(page * size).Take(size))
            {
                MaterialSlot(item.Entry, out _, out string property);
                string label = item.Entry.category == "参照アセット" ? "保存済みアセットの内容" :
                    string.IsNullOrEmpty(item.Entry.displayName) ? property ?? TableItem(new Activity { change = item }) : item.Entry.displayName;
                if (activity.IsAutoFix) label = CategoryLabel(item.Entry.category) + " / " + label;
                string paths = activity.IsAutoFix ? Location(item) : string.Join("\n", locations[Key(item)].Select(Location).Distinct());
                rows.Add(new ChangeDetailDisplay(item, item.Kind + " · " + label, paths));
            }
            return rows;
        }
        public static string Export(Activity activity)
        {
            var text = new System.Text.StringBuilder(activity.Summary);
            text.AppendLine(); text.AppendLine("  場所: " + ReadablePath(activity.Path));
            text.AppendLine("  記録: " + activity.after.note);
            if (activity.materialItems.Any(c => c.Entry.category == "参照アセット")) text.AppendLine("  保存情報の更新あり");
            if (!activity.IsAutoFix && IsObjectPresence(activity.change) && activity.change.Kind != "変更")
            {
                var nested = activity.NestedItems;
                text.AppendLine("  子オブジェクト: " + nested.Count(IsObjectPresence) + " 件 / 参照アセット: " + nested.Count(c => c.Entry.category == "参照アセット") + " 件");
                foreach (var c in nested)
                    text.AppendLine("  [" + c.Kind + "] " + (c.Entry.category == "参照アセット" ? "参照アセット: " : "子オブジェクト: ") + ReadablePath(c.Entry.path));
                return text.ToString();
            }
            if (activity.IsAutoFix || activity.MaterialBatch)
            {
                foreach (var c in activity.details)
                {
                    text.AppendLine("  [" + c.Kind + "] " + (activity.IsAutoFix ? CategoryLabel(c.Entry.category) + " / " : "") + ReadablePath(c.Entry.path) + " / " + c.Entry.item);
                    if (!string.IsNullOrEmpty(c.Entry.displayName)) text.AppendLine("    項目名: " + c.Entry.displayName);
                    text.AppendLine("    変更前: " + (c.before?.value ?? "（項目なし）"));
                    text.AppendLine("    変更後: " + (c.after?.value ?? "（項目なし）"));
                }
                return text.ToString();
            }
            if (activity.SharedMaterial)
            {
                MaterialSlot(activity.change.Entry, out _, out string property);
                text.AppendLine("  項目: " + property);
                text.AppendLine("  変更前: " + (activity.change.before?.value ?? "（項目なし）"));
                text.AppendLine("  変更後: " + (activity.change.after?.value ?? "（項目なし）"));
                text.AppendLine("  参照先: " + activity.details.Count + " 箇所");
                foreach (var c in activity.details)
                {
                    MaterialSlot(c.Entry, out string owner, out _);
                    text.AppendLine("    " + ReadablePath(c.Entry.path) + " / " + owner);
                }
                return text.ToString();
            }
            foreach (var c in activity.details)
            {
                text.AppendLine("  [" + c.Kind + "] " + ReadablePath(c.Entry.path) + " / " + c.Entry.item);
                text.AppendLine("    変更前: " + (c.before?.value ?? "（項目なし）"));
                text.AppendLine("    変更後: " + (c.after?.value ?? "（項目なし）"));
            }
            return text.ToString();
        }
    }
}
