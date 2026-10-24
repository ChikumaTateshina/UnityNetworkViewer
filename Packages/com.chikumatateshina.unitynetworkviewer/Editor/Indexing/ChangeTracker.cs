using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// 変更源を購読し、デバウンスしてから差分再解析を投入する。
    /// ロード済み Scene は変更されたオブジェクトを含むチャンクだけを再解析し、
    /// 生成・削除・親子変更などの構造変更は Scene の分割の差分だけを再解析する。
    /// Selection 変更はここでは扱わない（ハイライト専用で再解析しない）。
    /// </summary>
    public sealed class ChangeTracker
    {
        readonly UnvService _service;
        readonly Dictionary<int, SceneChanges> _scenes = new Dictionary<int, SceneChanges>();
        readonly HashSet<string> _dirtyAssetGuids = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _deletedGuids = new HashSet<string>(StringComparer.Ordinal);
        double _lastChange;
        bool _allScenesFull;
        bool _allScenesStructural;
        bool _undoPending;
        bool _hierarchyPending;
        bool _subscribed;
        bool _objectEventsThisWindow;

        internal static ChangeTracker Active;

        sealed class SceneChanges
        {
            public Scene Scene;
            public readonly HashSet<int> Objects = new HashSet<int>();
            public readonly HashSet<int> Subtrees = new HashSet<int>();
            /// <summary>ID が変わり得る部分木（Prefab の Unpack・Scene 間の移動）。</summary>
            public readonly HashSet<int> IdChanged = new HashSet<int>();
            public bool Structural;
        }

        public ChangeTracker(UnvService service)
        {
            _service = service;
        }

        public void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            Active = this;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.newSceneCreated += OnNewScene;
        }

        public void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (Active == this) Active = null;
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.newSceneCreated -= OnNewScene;
        }

        bool Enabled => UnvSettings.instance.autoUpdate && !EditorApplication.isPlayingOrWillChangePlaymode;

        void Touch()
        {
            _lastChange = EditorApplication.timeSinceStartup;
        }

        SceneChanges For(Scene scene)
        {
            if (!scene.IsValid() || EditorSceneManager.IsPreviewScene(scene)) return null;
            if (!_scenes.TryGetValue(scene.handle, out var c))
            {
                c = new SceneChanges { Scene = scene };
                _scenes[scene.handle] = c;
            }
            Touch();
            return c;
        }

        void MarkObject(Scene scene, int instanceId) => For(scene)?.Objects.Add(instanceId);

        void MarkStructure(Scene scene, int subtreeRoot = 0)
        {
            var c = For(scene);
            if (c == null) return;
            c.Structural = true;
            if (subtreeRoot != 0) c.Subtrees.Add(subtreeRoot);
        }

        void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!Enabled) return;
            _objectEventsThisWindow = true;
            // Animation ウィンドウ・Timeline のプレビュー中は値が毎フレーム書き換わるが、保存される変更ではないため再解析しない
            bool previewing = AnimationMode.InAnimationMode();
            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                if (previewing && kind == ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                switch (kind)
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var pr);
                        MarkObject(pr.scene, pr.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        // Component の追加・削除: その GameObject のチャンクだけ
                        stream.GetChangeGameObjectStructureEvent(i, out var st);
                        MarkObject(st.scene, st.instanceId);
                        NewObjectRegistry.MarkComponentsChanged(EditorUtility.InstanceIDToObject(st.instanceId) as UnityEngine.GameObject);
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var cg);
                        MarkStructure(cg.scene, cg.instanceId);
                        NewObjectRegistry.MarkCreated(EditorUtility.InstanceIDToObject(cg.instanceId) as UnityEngine.GameObject);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        // Prefab の Unpack・Revert など。部分木の ID が変わり得る
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var sh);
                        MarkStructure(sh.scene, sh.instanceId);
                        For(sh.scene)?.IdChanged.Add(sh.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        // 親が変わると子孫の階層パスも変わる。Scene をまたぐ移動では ID も変わる
                        stream.GetChangeGameObjectParentEvent(i, out var pa);
                        MarkStructure(pa.previousScene);
                        MarkStructure(pa.newScene, pa.instanceId);
                        if (pa.previousScene != pa.newScene) For(pa.newScene)?.IdChanged.Add(pa.instanceId);
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var de);
                        MarkStructure(de.scene);
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var up);
                        MarkStructure(up.scene);
                        var c = For(up.scene);
                        if (c != null) foreach (var id in up.instanceIds) c.Subtrees.Add(id);
                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                    case ObjectChangeKind.ChangeScene:
                        // 並び順・Scene 設定は関係に影響しない
                        break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var ca); MarkAsset(ca.guid.ToString()); break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var cap); MarkAsset(cap.guid.ToString()); break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var da); MarkAsset(da.guid.ToString()); break;
                }
            }
        }

        void MarkAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid == "00000000000000000000000000000000") return;
            _dirtyAssetGuids.Add(guid);
            Touch();
        }

        void OnUndoRedo()
        {
            if (!Enabled) return;
            // Undo が詳細イベントを出さなかった場合のみ、ロード済み Scene 全体を（時間分割で）再解析する
            _undoPending = true;
            Touch();
        }

        void OnHierarchyChanged()
        {
            // ObjectChangeEvents で拾えなかった変更の保険。内容までは再解析せず、分割の差分だけを見る
            if (!Enabled || AnimationMode.InAnimationMode()) return;
            _hierarchyPending = true;
            Touch();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                _allScenesFull = true;
                Touch();
            }
        }

        void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            // 未ロード索引 → ロード済み解析へ切替（コンテナ処理が未ロード索引の結果を除去する）
            _service.Scheduler.RequestScene(scene, true, AnalysisPriority.High);
        }

        void OnSceneClosed(Scene scene)
        {
            _scenes.Remove(scene.handle);
            NewObjectRegistry.ClearScene(scene);
            string guid = ObjectIdUtility.GetSceneGuid(scene);
            _service.Scheduler.ForgetScene(guid == null ? "scene:h" + scene.handle : "scene:" + guid);
            if (guid != null) _service.Scheduler.Enqueue(AnalysisTarget.ForUnloadedScene(guid, scene.path), AnalysisPriority.Normal);
        }

        void OnSceneSaved(Scene scene)
        {
            // 保存で新規オブジェクトにもファイル ID が付くため、一時 ID の扱いをやめて再解析する
            NewObjectRegistry.ClearScene(scene);
            _service.Scheduler.NotifySceneSaved(scene);
        }

        void OnNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            _service.Scheduler.RequestScene(scene, true, AnalysisPriority.Normal);
        }

        internal void OnAssetsChanged(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!UnvSettings.instance.autoUpdate) return;
            foreach (var p in imported) MarkAssetPath(p);
            foreach (var p in moved) MarkAssetPath(p);
            foreach (var p in deleted)
            {
                string guid = AssetDatabase.AssetPathToGUID(p, AssetPathToGUIDOptions.IncludeRecentlyDeletedAssets);
                if (!string.IsNullOrEmpty(guid)) _deletedGuids.Add(guid);
            }
            Touch();
        }

        void MarkAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid)) _dirtyAssetGuids.Add(guid);
        }

        bool HasPending => _scenes.Count > 0 || _dirtyAssetGuids.Count > 0 || _deletedGuids.Count > 0 || _allScenesFull || _allScenesStructural || _undoPending || _hierarchyPending;

        void OnUpdate()
        {
            if (!HasPending) return;
            double debounce = Math.Max(0.05, UnvSettings.instance.debounceMilliseconds / 1000.0);
            if (EditorApplication.timeSinceStartup - _lastChange < debounce) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Flush();
        }

        void Flush()
        {
            var scheduler = _service.Scheduler;
            var store = _service.Store;

            // 詳細イベントが無かった場合の保険
            if (!_objectEventsThisWindow)
            {
                if (_undoPending) _allScenesFull = true;
                if (_hierarchyPending) _allScenesStructural = true;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                if (_allScenesFull)
                {
                    scheduler.RequestScene(s, true, AnalysisPriority.Normal);
                    continue;
                }
                _scenes.TryGetValue(s.handle, out var c);
                if (_allScenesStructural || (c != null && c.Structural)) scheduler.NotifyStructureChanged(s, c?.Subtrees, c?.IdChanged);
                if (c != null && c.Objects.Count > 0) scheduler.NotifyObjectsChanged(s, c.Objects);
            }

            foreach (var guid in _deletedGuids)
            {
                scheduler.RemoveTarget("asset:" + guid);
                scheduler.RemoveTarget("scene:" + guid);
                scheduler.PrefabStructures.Invalidate(guid);
                EnqueueDependents(guid, store, scheduler);
            }

            foreach (var guid in _dirtyAssetGuids)
            {
                if (_deletedGuids.Contains(guid)) continue;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!UnvSettings.instance.includePackages && ObjectIdUtility.IsPackagePath(path)) continue;
                scheduler.PrefabStructures.Invalidate(guid);
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    var target = AnalysisTarget.ForScenePath(path);
                    // ロード済み Scene の保存によるインポートは Scene 側のイベントで扱う
                    if (target.Kind == AnalysisTargetKind.UnloadedScene) scheduler.Enqueue(target, AnalysisPriority.Normal);
                }
                else
                {
                    scheduler.Enqueue(AnalysisTarget.ForAsset(guid, path), AnalysisPriority.Normal);
                }
                EnqueueDependents(guid, store, scheduler);
            }

            _scenes.Clear();
            _dirtyAssetGuids.Clear();
            _deletedGuids.Clear();
            _allScenesFull = false;
            _allScenesStructural = false;
            _undoPending = false;
            _hierarchyPending = false;
            _objectEventsThisWindow = false;
        }

        static void EnqueueDependents(string guid, GraphStore store, AnalysisScheduler scheduler)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in store.GetOwnersDependingOn(guid)) keys.Add(OwnerKeys.GetTargetKey(owner));
            foreach (var key in keys)
            {
                var t = AnalysisTarget.FromKey(key);
                if (t != null) scheduler.Enqueue(t, AnalysisPriority.Normal);
            }
        }
    }

    /// <summary>Asset の追加・再インポート・削除・移動を ChangeTracker へ通知する。</summary>
    internal sealed class UnvAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            ChangeTracker.Active?.OnAssetsChanged(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }
}
