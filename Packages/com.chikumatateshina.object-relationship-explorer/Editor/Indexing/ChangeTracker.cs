using System;
using System.Collections.Generic;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace ObjectRelationshipExplorer.Editor
{
    /// <summary>
    /// 変更源を購読し、デバウンスしてから差分再解析を投入する。
    /// Selection 変更はここでは扱わない（ハイライト専用で再解析しない）。
    /// </summary>
    public sealed class ChangeTracker
    {
        readonly OreService _service;
        readonly HashSet<int> _dirtyScenes = new HashSet<int>();
        readonly HashSet<string> _dirtyAssetGuids = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _deletedGuids = new HashSet<string>(StringComparer.Ordinal);
        double _lastChange;
        bool _allScenesDirty;
        bool _subscribed;
        bool _objectEventsThisWindow;

        internal static ChangeTracker Active;

        public ChangeTracker(OreService service)
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

        bool Enabled => OreSettings.instance.autoUpdate && !EditorApplication.isPlayingOrWillChangePlaymode;

        void Touch()
        {
            _lastChange = EditorApplication.timeSinceStartup;
        }

        void MarkScene(Scene scene)
        {
            if (!scene.IsValid() || EditorSceneManager.IsPreviewScene(scene)) return;
            _dirtyScenes.Add(scene.handle);
            Touch();
        }

        void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (!Enabled) return;
            _objectEventsThisWindow = true;
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeScene:
                        stream.GetChangeSceneEvent(i, out var cs); MarkScene(cs.scene); break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var cg); MarkScene(cg.scene); break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var sh); MarkScene(sh.scene); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var st); MarkScene(st.scene); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var pa); MarkScene(pa.previousScene); MarkScene(pa.newScene); break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var pr); MarkScene(pr.scene); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var de); MarkScene(de.scene); break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var co); MarkScene(co.scene); break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var up); MarkScene(up.scene); break;
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
            _allScenesDirty = true;
            Touch();
        }

        void OnHierarchyChanged()
        {
            // ObjectChangeEvents で拾えなかった変更の保険（同じデバウンス窓で詳細イベントがあれば使わない）
            if (!Enabled) return;
            Touch();
            if (!_objectEventsThisWindow) _allScenesDirty = true;
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                _allScenesDirty = true;
                Touch();
            }
        }

        void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            // 未ロード索引 → ロード済み解析へ切替（Scheduler が不要になった owner を除去する）
            _service.Scheduler.Enqueue(AnalysisTarget.ForLoadedScene(scene), AnalysisPriority.High);
        }

        void OnSceneClosed(Scene scene)
        {
            string guid = ObjectIdUtility.GetSceneGuid(scene);
            if (guid == null)
            {
                _service.Scheduler.RemoveTarget("scene:h" + scene.handle);
                return;
            }
            _service.Scheduler.Enqueue(AnalysisTarget.ForUnloadedScene(guid, scene.path), AnalysisPriority.Normal);
        }

        void OnSceneSaved(Scene scene)
        {
            // 保存で session ID が永続 ID に変わるため、未保存時の結果を破棄して再解析
            _service.Scheduler.RemoveTarget("scene:h" + scene.handle);
            MarkScene(scene);
        }

        void OnNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            MarkScene(scene);
        }

        internal void OnAssetsChanged(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!OreSettings.instance.autoUpdate) return;
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

        void OnUpdate()
        {
            if (_dirtyScenes.Count == 0 && _dirtyAssetGuids.Count == 0 && _deletedGuids.Count == 0 && !_allScenesDirty) return;
            double debounce = Math.Max(0.05, OreSettings.instance.debounceMilliseconds / 1000.0);
            if (EditorApplication.timeSinceStartup - _lastChange < debounce) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Flush();
        }

        void Flush()
        {
            var scheduler = _service.Scheduler;
            var store = _service.Store;

            if (_allScenesDirty)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded) _dirtyScenes.Add(s.handle);
                }
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && _dirtyScenes.Contains(s.handle))
                    scheduler.Enqueue(AnalysisTarget.ForLoadedScene(s), AnalysisPriority.Normal);
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
                if (!OreSettings.instance.includePackages && ObjectIdUtility.IsPackagePath(path)) continue;
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

            _dirtyScenes.Clear();
            _dirtyAssetGuids.Clear();
            _deletedGuids.Clear();
            _allScenesDirty = false;
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
    internal sealed class OreAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            ChangeTracker.Active?.OnAssetsChanged(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }
}
