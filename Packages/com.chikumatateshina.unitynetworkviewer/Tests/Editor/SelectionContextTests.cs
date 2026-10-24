using System.Reflection;
using NUnit.Framework;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Editor;
using UnityNetworkViewer.Editor.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNetworkViewer.Tests
{
    /// <summary>Scene のオブジェクトを選択したとき、祖先・所属 Prefab・参照元が既定で表示されること。</summary>
    public class SelectionContextTests
    {
        const string Folder = "Assets/__UnvSelectionTests";
        UnvService _service;
        GraphViewModel _vm;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__UnvSelectionTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            _vm?.Dispose();
            if (_service != null) UnvService.Release();
            _service = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        (GameObject bulb, GameObject lampInstance, UnvTestBehaviour referrer, string prefabGuid) BuildScene()
        {
            var lamp = new GameObject("Lamp");
            new GameObject("Bulb").transform.SetParent(lamp.transform);
            var prefab = PrefabUtility.SaveAsPrefabAsset(lamp, Folder + "/Lamp.prefab");
            Object.DestroyImmediate(lamp);

            var scene = SceneManager.GetActiveScene();
            var room = new GameObject("Room");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.transform.SetParent(room.transform);
            var bulb = instance.transform.Find("Bulb").gameObject;
            var sw = new GameObject("Switch");
            var b = sw.AddComponent<UnvTestBehaviour>();
            b.componentRef = bulb.transform; // 参照は GameObject ではなく Transform に向いている
            EditorSceneManager.SaveScene(scene, Folder + "/Room.unity");
            return (bulb, instance, b, AssetDatabase.AssetPathToGUID(Folder + "/Lamp.prefab"));
        }

        void StartService()
        {
            _service = UnvService.Acquire();
            _vm = new GraphViewModel(_service);
        }

        void IndexLoadedScenes()
        {
            _service.IndexLoadedScenes();
            for (int i = 0; i < 10000 && !_service.Scheduler.IsIdle; i++) _service.Scheduler.Step();
            Assert.IsTrue(_service.Scheduler.IsIdle);
        }

        [Test]
        public void SelectingChildOfPrefabInstance_ShowsAncestorsPrefabAndReferrers()
        {
            var (bulb, instance, referrer, prefabGuid) = BuildScene();
            StartService();
            IndexLoadedScenes();

            _vm.OnUnitySelection(new[] { ObjectIdUtility.GetId(bulb) });

            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(instance)), "親（Prefab Instance のルート）");
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(instance.transform.parent.gameObject)), "祖先");
            Assert.IsTrue(_vm.Visible.Contains(NodeIds.Asset(prefabGuid)), "所属 Prefab");
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(referrer)), "Transform を参照している Component");
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(referrer.gameObject)), "参照元の GameObject");
            Assert.Greater(_vm.Highlight.GetDepth(ObjectIdUtility.GetId(referrer)), 0, "文脈もハイライトされる");

            // 文脈を切ると、深さ 1 の範囲だけになる（Transform の参照元は 2 段先）
            _vm.State.showSelectionContext = false;
            _vm.RefreshFromRoots();
            Assert.IsFalse(_vm.Visible.Contains(ObjectIdUtility.GetId(referrer)));
        }

        [Test]
        public void SelectionBeforeIndexing_IsExpandedWhenAnalysisArrives()
        {
            var (bulb, instance, referrer, _) = BuildScene();
            StartService();
            // 解析が終わる前に選択する（以前は選択ノードしか表示されず、隣接の展開が必要だった）
            _vm.OnUnitySelection(new[] { ObjectIdUtility.GetId(bulb) });
            IndexLoadedScenes();
            typeof(GraphViewModel).GetMethod("ApplyStoreChange", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_vm, null);

            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(bulb)));
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(instance)));
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(referrer)));

            // さらに索引が更新されても（選択に関係する変更なら）周辺が保たれる
            var extra = new GameObject("Extra").AddComponent<UnvTestBehaviour>();
            extra.target = bulb;
            _service.Scheduler.NotifyStructureChanged(SceneManager.GetActiveScene(), new[] { extra.gameObject.GetInstanceID() });
            for (int i = 0; i < 1000 && !_service.Scheduler.IsIdle; i++) _service.Scheduler.Step();
            typeof(GraphViewModel).GetMethod("ApplyStoreChange", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_vm, null);
            Assert.IsTrue(_vm.Visible.Contains(ObjectIdUtility.GetId(extra)), "後から追加された参照元も表示される");
        }
    }
}
