using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Layout;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityNetworkViewer.Editor.UI
{
    /// <summary>保存ビュー（UserSettings/ 以下に JSON で保存）。</summary>
    public static class SavedViewStore
    {
        [Serializable]
        sealed class Container
        {
            public List<ViewSnapshot> views = new List<ViewSnapshot>();
        }

        const string FilePath = "UserSettings/UnityNetworkViewer/Views.json";

        public static List<ViewSnapshot> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<ViewSnapshot>();
                var c = JsonUtility.FromJson<Container>(File.ReadAllText(FilePath));
                return c?.views ?? new List<ViewSnapshot>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityNetworkViewer] 保存ビューを読めません: " + ex.Message);
                return new List<ViewSnapshot>();
            }
        }

        public static void Save(List<ViewSnapshot> views)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(new Container { views = views }, true));
        }
    }

    /// <summary>左パネル: 選択同期・方向・深さ・関係種別・ノード種類・確度のフィルタ、保存ビュー。</summary>
    public sealed class FilterPanel : ScrollView
    {
        GraphViewModel _vm;
        readonly List<Action> _syncers = new List<Action>();
        DropdownField _viewsDropdown;
        List<ViewSnapshot> _views = new List<ViewSnapshot>();

        public FilterPanel() : base(ScrollViewMode.Vertical)
        {
            style.flexGrow = 1;
            contentContainer.style.paddingLeft = 4;
            contentContainer.style.paddingRight = 4;
        }

        public void Bind(GraphViewModel vm)
        {
            _vm = vm;
            Clear();
            _syncers.Clear();
            Build();
        }

        /// <summary>ViewModel 側の State 変更（保存ビュー適用など）を UI に反映する。</summary>
        public void SyncFromState()
        {
            foreach (var s in _syncers) s();
        }

        ViewState S => _vm.State;

        void Build()
        {
            var sync = Section("選択の同期", true);
            AddToggle(sync, "Follow selection（選択に追従）", () => S.followSelection, v => S.followSelection = v, false,
                "無効にするとエディタで選択を変えてもグラフ表示を固定します");
            AddToggle(sync, "Focus selection（自動パン）", () => S.focusSelection, v => S.focusSelection = v, false,
                "有効時のみ選択ノードへ自動でパンします（ズームは維持）");
            AddToggle(sync, "Graph → Unity 選択", () => S.syncGraphToUnity, v => S.syncGraphToUnity = v, false,
                "グラフ上のクリックを Unity の Selection に反映します（未ロード Scene は開きません）");
            AddToggle(sync, "選択 GameObject の Component も起点にする", () => S.includeComponents, v => S.includeComponents = v, true);
            AddEnum(sync, "複数選択", () => S.setMode, v => S.setMode = v, true, "Union: 和集合 / Intersection: 共通の参照元・参照先のみ");
            AddEnum(sync, "表示外の選択", () => S.newSelection, v => S.newSelection = v, false,
                "ReplaceView: 表示を置換 / AddToView: 追加 / KeepViewAndNotify: 維持して通知");

            var query = Section("探索", true);
            AddEnum(query, "方向", () => S.direction, v => S.direction = v, true);
            var depth = new SliderInt("ホップ数", 0, 5) { showInputField = true, value = S.depth };
            depth.RegisterValueChangedCallback(e => { S.depth = e.newValue; _vm.RefreshFromRoots(); });
            _syncers.Add(() => depth.SetValueWithoutNotify(S.depth));
            query.Add(depth);
            var maxNodes = new IntegerField("最大表示ノード") { value = S.maxNodes };
            maxNodes.RegisterValueChangedCallback(e => { S.maxNodes = Mathf.Clamp(e.newValue, 10, 5000); _vm.RefreshFromRoots(); });
            _syncers.Add(() => maxNodes.SetValueWithoutNotify(S.maxNodes));
            query.Add(maxNodes);
            AddToggle(query, "Scene 境界を越えて探索", () => S.crossScene, v => S.crossScene = v, true);
            AddToggle(query, "無関係ノードを非表示", () => S.hideUnrelated, v => S.hideUnrelated = v, true);

            var layout = Section("レイアウト", true);
            AddEnum(layout, "レイアウト", () => S.layout, v => S.layout = v, true);
            AddEnum(layout, "グループ化 (Grouped)", () => S.groupBy, v => S.groupBy = v, true);

            var edges = Section("関係の種類", false);
            AddAllNone(edges, () => S.disabledEdgeKinds.Clear(), () => { S.disabledEdgeKinds.Clear(); S.disabledEdgeKinds.AddRange(EdgeKindInfo.All); });
            foreach (var kind in EdgeKindInfo.All)
            {
                var k = kind;
                AddToggle(edges, $"{k} ({GraphStyles.EdgeAbbrev(k)})", () => !S.disabledEdgeKinds.Contains(k), v => SetIn(S.disabledEdgeKinds, k, !v), true);
            }

            var nodes = Section("ノードの種類", false);
            AddAllNone(nodes, () => S.disabledNodeKinds.Clear(), () => { S.disabledNodeKinds.Clear(); S.disabledNodeKinds.AddRange(EdgeKindInfo.AllNodeKinds); });
            foreach (var kind in EdgeKindInfo.AllNodeKinds)
            {
                var k = kind;
                AddToggle(nodes, k.ToString(), () => !S.disabledNodeKinds.Contains(k), v => SetIn(S.disabledNodeKinds, k, !v), true);
            }

            var evidence = Section("確度", false);
            foreach (EvidenceLevel ev in Enum.GetValues(typeof(EvidenceLevel)))
            {
                var e = ev;
                AddToggle(evidence, $"{e}（{GraphStyles.EvidenceLabel(e)}）", () => !S.disabledEvidence.Contains(e), v => SetIn(S.disabledEvidence, e, !v), true);
            }

            var views = Section("保存ビュー", true);
            _views = SavedViewStore.Load();
            _viewsDropdown = new DropdownField("ビュー", _views.Select(v => v.name).ToList(), _views.Count > 0 ? 0 : -1);
            views.Add(_viewsDropdown);
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.Add(new Button(LoadView) { text = "開く" });
            row.Add(new Button(SaveView) { text = "現在を保存" });
            row.Add(new Button(DeleteView) { text = "削除" });
            views.Add(row);

            var settings = Section("索引設定", false);
            var st = UnvSettings.instance;
            AddSettingToggle(settings, "自動差分更新", () => st.autoUpdate, v => st.autoUpdate = v);
            AddSettingToggle(settings, "Packages/ も索引する（読み取り専用）", () => st.includePackages, v => st.includePackages = v);
            AddSettingToggle(settings, "キャッシュ (Library/) を使用", () => st.useCache, v => st.useCache = v);
            AddSettingToggle(settings, "開いたときに全体索引", () => st.indexProjectOnOpen, v => st.indexProjectOnOpen = v);
            AddSettingToggle(settings, "UdonSharp ソース静的解析", () => st.enableUdonSharpSourceAnalysis, v => st.enableUdonSharpSourceAnalysis = v);
            var unloaded = new EnumField("未ロード Scene", st.unloadedSceneMode);
            unloaded.RegisterValueChangedCallback(e => { st.unloadedSceneMode = (UnloadedSceneMode)e.newValue; st.SaveSettings(); });
            settings.Add(unloaded);
            var debounce = new IntegerField("デバウンス (ms)") { value = st.debounceMilliseconds };
            debounce.RegisterValueChangedCallback(e => { st.debounceMilliseconds = Mathf.Clamp(e.newValue, 50, 5000); st.SaveSettings(); });
            settings.Add(debounce);
            var budget = new IntegerField("フレーム予算 (ms)") { value = st.frameBudgetMilliseconds };
            budget.RegisterValueChangedCallback(e => { st.frameBudgetMilliseconds = Mathf.Clamp(e.newValue, 2, 200); st.SaveSettings(); });
            settings.Add(budget);
            var chunk = new IntegerField("チャンク (GameObject 数)") { value = st.chunkSize, tooltip = "ロード済み Scene を分割して解析する単位。小さいほど 1 フレームの処理が短く、大きいほど全体の解析が速くなります。" };
            chunk.RegisterValueChangedCallback(e => { st.chunkSize = Mathf.Clamp(e.newValue, 16, 4096); st.SaveSettings(); });
            settings.Add(chunk);
            var note = new HelpBox("索引設定の一部（Packages/、Udon 解析、チャンク）は全体再解析後に反映されます。", HelpBoxMessageType.Info);
            settings.Add(note);
        }

        static void SetIn<T>(List<T> list, T value, bool present)
        {
            if (present) { if (!list.Contains(value)) list.Add(value); }
            else list.Remove(value);
        }

        Foldout Section(string title, bool open)
        {
            var f = new Foldout { text = title, value = open };
            f.style.marginBottom = 4;
            Add(f);
            return f;
        }

        void AddAllNone(VisualElement parent, Action all, Action none)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.Add(new Button(() => { all(); SyncFromState(); _vm.OnStateChanged(true); }) { text = "すべて" });
            row.Add(new Button(() => { none(); SyncFromState(); _vm.OnStateChanged(true); }) { text = "なし" });
            parent.Add(row);
        }

        void AddToggle(VisualElement parent, string label, Func<bool> get, Action<bool> set, bool affectsQuery, string tooltip = null)
        {
            var t = new Toggle(label) { value = get(), tooltip = tooltip };
            t.labelElement.style.minWidth = 0;
            t.labelElement.style.flexGrow = 1;
            t.labelElement.style.whiteSpace = WhiteSpace.Normal;
            t.style.flexDirection = FlexDirection.RowReverse;
            t.style.justifyContent = Justify.FlexEnd;
            t.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                if (affectsQuery) _vm.RefreshFromRoots();
                else _vm.OnStateChanged(false);
            });
            _syncers.Add(() => t.SetValueWithoutNotify(get()));
            parent.Add(t);
        }

        void AddEnum<T>(VisualElement parent, string label, Func<T> get, Action<T> set, bool affectsQuery, string tooltip = null) where T : Enum
        {
            var f = new EnumField(label, get()) { tooltip = tooltip };
            f.RegisterValueChangedCallback(e =>
            {
                set((T)e.newValue);
                if (affectsQuery) _vm.RefreshFromRoots();
                else _vm.OnStateChanged(false);
            });
            _syncers.Add(() => f.SetValueWithoutNotify(get()));
            parent.Add(f);
        }

        static void AddSettingToggle(VisualElement parent, string label, Func<bool> get, Action<bool> set)
        {
            var t = new Toggle(label) { value = get() };
            t.labelElement.style.minWidth = 0;
            t.labelElement.style.flexGrow = 1;
            t.style.flexDirection = FlexDirection.RowReverse;
            t.style.justifyContent = Justify.FlexEnd;
            t.RegisterValueChangedCallback(e =>
            {
                set(e.newValue);
                UnvSettings.instance.SaveSettings();
            });
            parent.Add(t);
        }

        void RefreshViewsDropdown(int select)
        {
            _viewsDropdown.choices = _views.Select(v => v.name).ToList();
            _viewsDropdown.index = _views.Count == 0 ? -1 : Mathf.Clamp(select, 0, _views.Count - 1);
        }

        void LoadView()
        {
            int i = _viewsDropdown.index;
            if (i < 0 || i >= _views.Count) return;
            _vm.ApplySnapshot(_views[i]);
            SyncFromState();
        }

        void SaveView()
        {
            string name = $"View {DateTime.Now:MM-dd HH:mm:ss}";
            var root = _vm.Store.GetNode(_vm.Roots.FirstOrDefault());
            if (root != null) name = root.Name + " · " + DateTime.Now.ToString("MM-dd HH:mm");
            _views.Add(_vm.CreateSnapshot(name));
            SavedViewStore.Save(_views);
            RefreshViewsDropdown(_views.Count - 1);
        }

        void DeleteView()
        {
            int i = _viewsDropdown.index;
            if (i < 0 || i >= _views.Count) return;
            if (!EditorUtility.DisplayDialog("保存ビューの削除", $"「{_views[i].name}」を削除しますか？", "削除", "キャンセル")) return;
            _views.RemoveAt(i);
            SavedViewStore.Save(_views);
            RefreshViewsDropdown(i);
        }
    }
}
