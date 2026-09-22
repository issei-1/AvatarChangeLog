using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AvatarChangeLog
{
    public sealed class HistoryWindow : EditorWindow
    {
        const string ToolTitle = "Avatar Change Log";
        GameObject avatar;
        [SerializeField] string search = "";
        [SerializeField] int category;
        readonly List<Snapshot> snapshots = new List<Snapshot>();
        readonly List<Activity> activities = new List<Activity>();
        List<Activity> visible = new List<Activity>();
        List<string> loadErrors = new List<string>();
        int page, detailPage;
        int cachedPage = -1;
        List<Activity> cachedVisible;
        readonly List<(ActivityDisplay display, GUIContent title)> pageRows = new List<(ActivityDisplay, GUIContent)>();
        readonly HashSet<string> collapsedDates = new HashSet<string>(StringComparer.Ordinal);
        readonly ActivitySearchCache searchCache = new ActivitySearchCache();
        Activity detailActivity;
        int cachedDetailPage = -1;
        List<ChangeDetailDisplay> detailRows;
        string avatarId, error, notice, expanded;
        Vector2 scroll;
        GUIStyle activityStyle, addedStyle, removedStyle, changedStyle, versionStyle, previewNameStyle, previewAfterStyle;
        Texture2D logo;
        const string LogoGuid = "a1f04a69a29146e9bd95d45c680120d6";
        bool darkSkin;
        string lastRecordingError;
        string lastRecordingStatus;
        const int PageSize = 40;
        static readonly string[] Categories = { "すべて", "BlendShape", "オブジェクト", "Transform", "マテリアル", "コンポーネント" };
        static readonly string[] CategoryLabels = { "すべて", "シェイプキー", "オブジェクト", "トランスフォーム", "マテリアル", "コンポーネント" };

        [MenuItem("Tools/" + ToolTitle)]
        public static void Open()
        {
            var window = GetWindow<HistoryWindow>(ToolTitle);
            window.Show();
        }
        void OnEnable()
        {
            titleContent = new GUIContent(ToolTitle);
            logo = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(LogoGuid));
            minSize = new Vector2(620, 440);
            RecordingService.SnapshotSaved += OnSnapshotSaved;
            try
            {
                Reload();
            }
            catch (Exception e) { error = e.Message; }
        }
        void OnDisable() { RecordingService.SnapshotSaved -= OnSnapshotSaved; searchCache.Clear(); ClearDetailCache(); }
        void OnInspectorUpdate()
        {
            if (SynchronizeAvatar()) Repaint();
            string status = RecordingStatus();
            if (lastRecordingStatus != status) { lastRecordingStatus = status; Repaint(); }
            if (lastRecordingError != RecordingService.LastError)
            { lastRecordingError = RecordingService.LastError; Repaint(); }
        }

        void OnGUI()
        {
            SynchronizeAvatar();
            if (activityStyle == null || darkSkin != EditorGUIUtility.isProSkin)
            {
                darkSkin = EditorGUIUtility.isProSkin;
                versionStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                activityStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 13, fontStyle = FontStyle.Bold, padding = new RectOffset(3, 3, 4, 4) };
                addedStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                removedStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                changedStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                addedStyle.normal.textColor = darkSkin ? new Color(0.45f, 0.85f, 0.55f) : new Color(0.12f, 0.42f, 0.2f);
                removedStyle.normal.textColor = darkSkin ? new Color(1f, 0.55f, 0.55f) : new Color(0.7f, 0.16f, 0.16f);
                changedStyle.normal.textColor = darkSkin ? new Color(0.5f, 0.75f, 1f) : new Color(0.13f, 0.35f, 0.65f);
                previewNameStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold };
                previewAfterStyle = new GUIStyle(previewNameStyle);
                previewAfterStyle.normal.textColor = Color.white;
            }
            EditorGUILayout.Space(6);
            DrawHeader();
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var selected = (GameObject)EditorGUILayout.ObjectField("アバター", avatar, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) SelectAvatar(selected);
                using (new EditorGUI.DisabledScope(!avatar || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
                    if (GUILayout.Button(RecordingService.Paused ? "記録を再開" : "一時停止", GUILayout.Width(90)))
                    {
                        try { RecordingService.SetPaused(!RecordingService.Paused); error = null; Repaint(); }
                        catch (Exception e) { error = e.Message; }
                    }
            }
            GUILayout.Label(RecordingStatus(), EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択中のオブジェクトを指定", EditorStyles.miniButton, GUILayout.Width(200)))
                { SelectAvatar(Selection.activeGameObject); }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("再読込", EditorStyles.miniButton, GUILayout.Width(70))) Reload();
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(RecordingService.LastError) && RecordingService.LastError != error)
                EditorGUILayout.HelpBox(RecordingService.LastError, MessageType.Error);
            if (loadErrors.Count > 0) EditorGUILayout.HelpBox("読めない記録が " + loadErrors.Count + " 件あります。元ファイルは保持されています。\n" + string.Join("\n", loadErrors.Take(2)), MessageType.Warning);
            if (!string.IsNullOrEmpty(notice)) EditorGUILayout.HelpBox(notice, MessageType.Info);
            EditorGUILayout.Space(8);
            DrawLog();
            Footer();
        }

        string RecordingStatus()
        {
            if (!avatar) return "自動記録：対象なし";
            string state = RecordingService.Paused ? "一時停止中" :
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ? "待機中（Unityの処理終了後に再開）" :
                RecordingService.Running ? "記録中" : !string.IsNullOrEmpty(RecordingService.LastError) ? "停止中（エラー）" : "開始待ち";
            return "自動記録：" + avatar.name + " ／ " + state;
        }

        void DrawHeader()
        {
            float aspect = logo ? (float)logo.width / logo.height : 2048f / 264f;
            float height = Mathf.Clamp((position.width - 32f) / aspect + 24f, 80f, 164f);
            Rect area = EditorGUILayout.GetControlRect(false, height);
            EditorGUI.DrawRect(area, new Color(25f / 255f, 29f / 255f, 41f / 255f));
            if (logo)
            {
                float width = Mathf.Min(area.width - 16f, (area.height - 24f) * aspect) * 0.9f;
                var bounds = new Rect(area.center.x - width / 2f, area.y + 6f + ((area.height - 24f) - width / aspect) / 2f, width, width / aspect);
                GUI.DrawTextureWithTexCoords(bounds, logo,
                    new Rect(0f, 0f, 1f, 1f), false);
            }
            Color previous = GUI.contentColor;
            GUI.contentColor = new Color(0.78f, 0.78f, 0.82f);
            GUI.Label(new Rect(area.xMax - 78f, area.yMax - 20f, 70f, 18f), "v0.1.1", versionStyle);
            GUI.contentColor = previous;
        }
        void DrawLog()
        {
            string previousSearch = search;
            int previousCategory = category;
            bool executeSearch = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                category = EditorGUILayout.Popup(Mathf.Clamp(category, 0, Categories.Length - 1), CategoryLabels, GUILayout.Width(165));
                GUI.SetNextControlName("AvatarChangeLog.Search");
                search = EditorGUILayout.TextField(search ?? "", EditorStyles.toolbarSearchField);
                if (GUILayout.Button("検索", EditorStyles.miniButton, GUILayout.Width(45))) executeSearch = true;
                if (GUILayout.Button("クリア", EditorStyles.miniButton, GUILayout.Width(55)))
                { category = 0; search = ""; GUI.FocusControl(null); executeSearch = true; }
            }
            if (Event.current.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == "AvatarChangeLog.Search" &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            { executeSearch = true; Event.current.Use(); }
            if (executeSearch || previousCategory != category || !string.Equals(previousSearch, search, StringComparison.Ordinal))
            { collapsedDates.Clear(); Filter(true); Repaint(); }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label((string.IsNullOrWhiteSpace(search) ? "" : "検索結果：") + visible.Count + " 件の変更  ·  新しい順", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("最新へ", EditorStyles.miniButton, GUILayout.Width(65)))
                {
                    page = 0; scroll = Vector2.zero;
                    if (visible.Count > 0)
                    {
                        string recorded = visible[0].after.createdUtc;
                        collapsedDates.Remove(DateTimeOffset.TryParse(recorded, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd") : recorded);
                    }
                }
                using (new EditorGUI.DisabledScope(visible.Count == 0))
                    if (GUILayout.Button("ログを書き出す", EditorStyles.miniButton, GUILayout.Width(105))) ExportLog();
            }
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                if (visible.Count == 0)
                    EditorGUILayout.HelpBox(activities.Count > 0 ? "検索条件に一致する変更はありません。" :
                        !avatar ? "アバターを上の欄にドラッグ＆ドロップしてください。" :
                        "まだ変更はありません。シェイプキーなどを変更すると、ここにログが追加されます。", MessageType.Info);
                page = Mathf.Clamp(page, 0, Math.Max(0, (visible.Count - 1) / PageSize));
                if (cachedVisible != visible || cachedPage != page)
                {
                    pageRows.Clear();
                    foreach (var item in visible.Skip(page * PageSize).Take(PageSize))
                    {
                        var display = new ActivityDisplay(item);
                        pageRows.Add((display, new GUIContent(display.target, display.path)));
                    }
                    cachedVisible = visible; cachedPage = page;
                }
                string previousDate = null;
                bool dateOpen = true;
                foreach (var row in pageRows)
                {
                    string date = row.display.date;
                    if (date != previousDate)
                    {
                        EditorGUILayout.Space(6);
                        dateOpen = EditorGUILayout.Foldout(!collapsedDates.Contains(date), date, true, EditorStyles.foldoutHeader);
                        if (dateOpen) collapsedDates.Remove(date); else collapsedDates.Add(date);
                        if (dateOpen) DrawTableHeader();
                        previousDate = date;
                    }
                    if (dateOpen) DrawActivity(row.display, row.title);
                }
            }
            Pagination(visible.Count);
        }

        float TargetColumnWidth => Mathf.Max(140f, (position.width - 190f) * 0.48f);
        float ChangeColumnWidth => Mathf.Max(120f, position.width - TargetColumnWidth - 190f);

        void DrawTableHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("時刻 / 操作", EditorStyles.miniBoldLabel, GUILayout.Width(76));
                GUILayout.Space(8);
                GUILayout.Label("対象 / 分類", EditorStyles.miniBoldLabel, GUILayout.Width(TargetColumnWidth));
                GUILayout.Space(8);
                GUILayout.Label("変更内容", EditorStyles.miniBoldLabel, GUILayout.Width(ChangeColumnWidth));
                GUILayout.Space(56);
            }
        }

        void DrawActivity(ActivityDisplay display, GUIContent title)
        {
            var activity = display.activity;
            var change = activity.change;
            var nested = display.nested;
            bool grouped = display.grouped;
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.Space(5);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(76)))
                    {
                        GUILayout.Label(display.time, EditorStyles.miniLabel);
                        GUILayout.Label(display.kind, display.kind == "追加" ? addedStyle : display.kind == "削除" ? removedStyle : changedStyle);
                    }
                    GUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(TargetColumnWidth)))
                    {
                        GUILayout.Label(title, activityStyle, GUILayout.Width(TargetColumnWidth));
                        GUILayout.Label(display.category, EditorStyles.wordWrappedMiniLabel, GUILayout.Width(TargetColumnWidth));
                    }
                    GUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(ChangeColumnWidth)))
                    {
                        DrawActivityPreview(display.preview);
                    }
                    if (GUILayout.Button(expanded == display.id ? "閉じる" : "詳細", EditorStyles.miniButton, GUILayout.Width(52)))
                    { expanded = expanded == display.id ? null : display.id; detailPage = 0; ClearDetailCache(); }
                }
                if (expanded == display.id)
                {
                    EditorGUILayout.Space(5);
                    GUILayout.Label("場所：" + display.path, EditorStyles.wordWrappedLabel);
                    if (display.preview.savedInfo) GUILayout.Label("保存情報の更新あり", EditorStyles.wordWrappedMiniLabel);
                    if (activity.SharedMaterial) GUILayout.Label(display.references, EditorStyles.wordWrappedMiniLabel);
                    if (grouped) GUILayout.Label(display.counts, EditorStyles.wordWrappedMiniLabel);
                    if (!string.IsNullOrEmpty(activity.after.note) && activity.after.note != "自動記録")
                        GUILayout.Label(activity.after.note, EditorStyles.wordWrappedMiniLabel);
                    if (activity.IsAutoFix || activity.MaterialBatch || (!HistoryText.IsObjectPresence(change) && HistoryText.Property(change.Entry) == "存在" && activity.details.Count > 1)) DrawDetailedChanges(activity);
                    else if (activity.SharedMaterial)
                    {
                        Value("変更前", change.before?.value ?? "（項目なし）");
                        Value("変更後", change.after?.value ?? "（項目なし）");
                        DrawNestedItems(activity.details, true);
                    }
                    else if (grouped) DrawNestedItems(nested);
                    else
                    {
                        GUILayout.Label(change.Entry.item, EditorStyles.wordWrappedMiniLabel);
                        Value("変更前", change.before?.value ?? "（項目なし）");
                        Value("変更後", change.after?.value ?? "（項目なし）");
                    }
                }
                EditorGUILayout.Space(5);
                Rect divider = EditorGUILayout.GetControlRect(false, 1);
                EditorGUI.DrawRect(divider, darkSkin ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.75f, 0.75f, 0.75f));
            }
        }

        void DrawActivityPreview(ActivityPreview preview)
        {
            float valueWidth = (ChangeColumnWidth - 24) / 2;
            foreach (var row in preview.rows)
            {
                GUILayout.Label(new GUIContent(row.label, row.text), previewNameStyle, GUILayout.Width(ChangeColumnWidth));
                if (row.message != null)
                {
                    GUILayout.Label(row.message, EditorStyles.wordWrappedLabel, GUILayout.Width(ChangeColumnWidth));
                    continue;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPreviewValue("変更前", row.displayBefore, row.beforeColor, row.before, valueWidth, false);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(16)))
                    {
                        GUILayout.Space(18);
                        GUILayout.Label("→", GUILayout.Width(16));
                    }
                    DrawPreviewValue("変更後", row.displayAfter, row.afterColor, row.after, valueWidth, true);
                }
                GUILayout.Space(6);
            }
            if (preview.message != null)
            {
                GUILayout.Label(preview.heading, previewNameStyle, GUILayout.Width(ChangeColumnWidth));
                GUILayout.Label(preview.message, EditorStyles.wordWrappedLabel, GUILayout.Width(ChangeColumnWidth));
            }
            if (preview.remaining > 0) GUILayout.Label(preview.DetailHint + " · 詳細で確認", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawPreviewValue(string caption, string value, float[] color, string raw, float width, bool changed)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                GUILayout.Label(caption, EditorStyles.miniLabel, GUILayout.Width(width));
                if (color != null) DrawColorSample(color, caption + " RGBA: " + raw, width);
                else GUILayout.Label(new GUIContent(value, raw), changed ? previewAfterStyle : EditorStyles.wordWrappedLabel, GUILayout.Width(width));
            }
        }

        static void DrawColorSample(float[] rgba, string tooltip, float width)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                Rect rect = GUILayoutUtility.GetRect(width, 20, GUILayout.Width(width));
                EditorGUI.DrawRect(rect, new Color(0.4f, 0.4f, 0.4f));
                var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
                // Checkerboard makes transparent colors visible without opening an editable color picker.
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 5; x++)
                    {
                        float shade = (x + y) % 2 == 0 ? 0.35f : 0.65f;
                        EditorGUI.DrawRect(new Rect(inner.x + x * inner.width / 5, inner.y + y * inner.height / 2,
                            inner.width / 5, inner.height / 2), new Color(shade, shade, shade));
                    }
                EditorGUI.DrawRect(inner, new Color(Mathf.Clamp01(rgba[0]), Mathf.Clamp01(rgba[1]), Mathf.Clamp01(rgba[2]), Mathf.Clamp01(rgba[3])));
                GUI.Label(rect, new GUIContent("", tooltip));
                bool hdr = rgba[0] < 0 || rgba[0] > 1 || rgba[1] < 0 || rgba[1] > 1 || rgba[2] < 0 || rgba[2] > 1;
                if (hdr || rgba[3] != 1f)
                    GUILayout.Label((hdr ? "HDR " : "") + (rgba[3] != 1f ? "不透明度 " + (rgba[3] * 100d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : ""),
                        EditorStyles.wordWrappedMiniLabel, GUILayout.Width(width));
            }
        }

        void DrawDetailedChanges(Activity activity)
        {
            const int size = 20;
            var items = activity.materialItems.Count > 0 ? activity.materialItems : activity.details;
            int pages = Math.Max(1, (items.Count + size - 1) / size);
            detailPage = Mathf.Clamp(detailPage, 0, pages - 1);
            if (detailActivity != activity || cachedDetailPage != detailPage)
            {
                detailRows = HistoryText.DetailPage(activity, detailPage, size);
                detailActivity = activity; cachedDetailPage = detailPage;
            }
            foreach (var row in detailRows)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var item = row.change;
                    GUILayout.Label(row.label, previewNameStyle);
                    GUILayout.Label(item.Entry.item, EditorStyles.wordWrappedMiniLabel);
                    Value("変更前", item.before?.value ?? "（項目なし）");
                    Value("変更後", item.after?.value ?? "（項目なし）");
                    GUILayout.Label(row.locations, EditorStyles.wordWrappedMiniLabel);
                }
            if (pages > 1)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(detailPage == 0))
                        if (GUILayout.Button("詳細の前へ", EditorStyles.miniButton)) detailPage--;
                    GUILayout.Label((detailPage + 1) + " / " + pages, EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(detailPage + 1 >= pages))
                        if (GUILayout.Button("詳細の次へ", EditorStyles.miniButton)) detailPage++;
                }
        }

        void DrawNestedItems(List<Change> nested, bool materialReferences = false)
        {
            const int detailPageSize = 20;
            int pages = Math.Max(1, (nested.Count + detailPageSize - 1) / detailPageSize);
            detailPage = Mathf.Clamp(detailPage, 0, pages - 1);
            foreach (var child in nested.Skip(detailPage * detailPageSize).Take(detailPageSize))
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    string label = materialReferences ? "参照先" : child.Entry.category == "参照アセット" ? "参照アセット" : "子オブジェクト";
                    GUILayout.Label(child.Kind + " · " + label, EditorStyles.miniBoldLabel);
                    GUILayout.Label(HistoryText.ReadablePath(child.Entry.path), EditorStyles.wordWrappedLabel);
                    if (materialReferences) GUILayout.Label(child.Entry.item, EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button("パスをコピー", EditorStyles.miniButton, GUILayout.Width(100)))
                        EditorGUIUtility.systemCopyBuffer = child.Entry.path;
                }
            if (pages > 1)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(detailPage == 0))
                        if (GUILayout.Button("詳細の前へ", EditorStyles.miniButton)) detailPage--;
                    GUILayout.Label((detailPage + 1) + " / " + pages, EditorStyles.miniLabel);
                    using (new EditorGUI.DisabledScope(detailPage + 1 >= pages))
                        if (GUILayout.Button("詳細の次へ", EditorStyles.miniButton)) detailPage++;
                }
        }

        void Value(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(45));
                float height = Mathf.Max(36, EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(value), Mathf.Max(120, position.width - 140)));
                EditorGUILayout.SelectableLabel(value, EditorStyles.wordWrappedLabel, GUILayout.Height(height));
                if (GUILayout.Button("コピー", EditorStyles.miniButton, GUILayout.Width(52))) EditorGUIUtility.systemCopyBuffer = value;
            }
        }

        void Pagination(int count)
        {
            int pages = Math.Max(1, (count + PageSize - 1) / PageSize);
            page = Mathf.Clamp(page, 0, pages - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(page == 0))
                    if (GUILayout.Button("前へ", GUILayout.Width(55))) { page--; scroll = Vector2.zero; }
                GUILayout.FlexibleSpace(); GUILayout.Label((page + 1) + " / " + pages, EditorStyles.miniLabel); GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(page >= pages - 1))
                    if (GUILayout.Button("次へ", GUILayout.Width(55))) { page++; scroll = Vector2.zero; }
            }
        }

        void Footer()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("ローカル保存  ·  " + snapshots.Count + " 記録", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("保存フォルダ", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    string path = string.IsNullOrEmpty(avatarId) ? HistoryStore.RootDirectory : HistoryStore.DirectoryFor(avatarId);
                    if (Directory.Exists(path)) EditorUtility.RevealInFinder(path);
                }
            }
        }

        void Reload()
        {
            searchCache.Clear(); ClearDetailCache();
            if (!RecordingService.Target || avatarId != RecordingService.TargetId) collapsedDates.Clear();
            pageRows.Clear(); cachedVisible = null; cachedPage = -1;
            snapshots.Clear(); activities.Clear(); visible.Clear(); loadErrors.Clear();
            avatarId = error = notice = expanded = null; page = 0; scroll = Vector2.zero;
            try
            {
                avatar = RecordingService.Target;
                avatarId = RecordingService.TargetId;
                // Keep the remembered ID for scene restoration, but only show logs for a visible target.
                if (!avatar || string.IsNullOrEmpty(avatarId)) return;
                snapshots.AddRange(HistoryStore.Load(avatarId, out loadErrors));
                for (int i = snapshots.Count - 1; i > 0; i--) activities.AddRange(HistoryText.Build(snapshots[i - 1], snapshots[i]));
                Filter(true);
            }
            catch (Exception e) { error = e.Message; }
        }

        void SelectAvatar(GameObject selected)
        {
            try { RecordingService.Select(selected); Reload(); }
            catch (Exception e) { error = e.Message; }
        }
        void OnSnapshotSaved(Snapshot snapshot, HashSet<string> retainedFiles)
        {
            SynchronizeAvatar();
            if (!avatar || snapshot.avatarId != avatarId) return;
            if (!snapshots.Any(s => s.id == snapshot.id))
            {
                var previous = snapshots.LastOrDefault();
                snapshots.Add(snapshot);
                if (previous != null) activities.InsertRange(0, HistoryText.Build(previous, snapshot));
            }
            if (retainedFiles != null) snapshots.RemoveAll(s => !retainedFiles.Contains(s.id + ".json"));
            else
            {
                // A partial retention failure has no reliable result; reconcile from disk only in that case.
                var directory = HistoryStore.DirectoryFor(avatarId);
                snapshots.RemoveAll(s => !File.Exists(Path.Combine(directory, s.id + ".json")));
            }
            var retained = new HashSet<Snapshot>(snapshots.Skip(1));
            activities.RemoveAll(a => !retained.Contains(a.after));
            searchCache.Retain(retained);
            if (detailActivity != null && !retained.Contains(detailActivity.after)) ClearDetailCache();
            pageRows.Clear(); cachedVisible = null; cachedPage = -1;
            Filter(false); Repaint();
        }

        void Filter(bool reset)
        {
            category = Mathf.Clamp(category, 0, Categories.Length - 1);
            var words = HistoryText.SearchWords(search);
            if (words.Length == 0) searchCache.Clear();
            visible = activities.Where(a => HistoryText.MatchesWords(a, category == 0 ? null : Categories[category], words, searchCache)).ToList();
            if (reset) { page = 0; scroll = Vector2.zero; }
        }

        void ExportLog()
        {
            SynchronizeAvatar();
            if (!avatar || visible.Count == 0) return;
            string path = EditorUtility.SaveFilePanel(ToolTitle, "", "avatar-change-log", "txt");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllLines(path, HistoryText.ExportDocument(visible, CategoryLabels[category], search), new UTF8Encoding(false));
                notice = visible.Count + " 件のログを出力しました。";
            }
            catch (Exception e) { error = e.Message; }
        }

        void ClearDetailCache() { detailActivity = null; detailRows = null; cachedDetailPage = -1; }

        bool SynchronizeAvatar()
        {
            // Destroyed Unity objects compare equal to null on both sides, so check stale view data too.
            bool stale = !avatar && (snapshots.Count > 0 || activities.Count > 0 || visible.Count > 0 ||
                pageRows.Count > 0 || loadErrors.Count > 0);
            if (avatar == RecordingService.Target && avatarId == RecordingService.TargetId && !stale) return false;
            Reload();
            return true;
        }

    }
}
