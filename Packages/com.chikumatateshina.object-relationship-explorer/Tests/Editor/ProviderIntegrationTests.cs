using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ObjectRelationshipExplorer.Core;
using ObjectRelationshipExplorer.Editor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ObjectRelationshipExplorer.Tests
{
    /// <summary>
    /// 実際の Scene / Prefab を一時フォルダに作成して Provider を実行する統合テスト。
    /// ロード済み解析と未ロード（テキスト）解析でノードIDが一致すること、解析で Dirty にならないことを確認する。
    /// </summary>
    public class ProviderIntegrationTests
    {
        const string Folder = "Assets/__OreIntegrationTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__OreIntegrationTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        static GraphStore Analyze(AnalysisTarget target)
        {
            var store = new GraphStore();
            var context = new AnalysisContext(OreSettings.instance, new PrefabStructureCache());
            foreach (var p in ProviderRegistry.Providers)
            {
                if (!p.CanAnalyze(target)) continue;
                store.ReplaceOwner(OwnerKeys.Create(p.Id, target.Key), p.Analyze(target, context), null);
            }
            return store;
        }

        static (GameObject door, GameObject handle, OreTestBehaviour behaviour, GameObject lamp) BuildScene()
        {
            var door = new GameObject("Door");
            var handle = new GameObject("Handle");
            handle.transform.SetParent(door.transform);
            var lamp = new GameObject("Lamp");
            var b = door.AddComponent<OreTestBehaviour>();
            b.target = lamp;
            b.targets = new[] { handle, lamp };
            b.componentRef = lamp.transform;
            UnityEventTools.AddPersistentListener(b.onUse, b.Use);
            return (door, handle, b, lamp);
        }

        [Test]
        public void LoadedScene_HierarchyComponentsAndSerializedReferences()
        {
            var (door, handle, b, lamp) = BuildScene();
            var store = Analyze(AnalysisTarget.ForLoadedScene(SceneManager.GetActiveScene()));

            string doorId = ObjectIdUtility.GetId(door), handleId = ObjectIdUtility.GetId(handle), lampId = ObjectIdUtility.GetId(lamp), bId = ObjectIdUtility.GetId(b);
            Assert.IsTrue(NodeIds.IsSession(doorId), "未保存 Scene では session ID");
            Assert.IsTrue(store.GetOutEdges(doorId).Any(e => e.Kind == EdgeKind.ParentChild && e.TargetId == handleId));
            Assert.IsTrue(store.GetOutEdges(doorId).Any(e => e.Kind == EdgeKind.OwnsComponent && e.TargetId == bId));

            var refs = store.GetOutEdges(bId).Where(e => e.Kind == EdgeKind.SerializedReference).ToList();
            Assert.IsTrue(refs.Any(e => e.TargetId == lampId && e.PropertyPath == "target"));
            Assert.IsTrue(refs.Any(e => e.TargetId == handleId && e.PropertyPath == "targets.Array.data[0]"));
            Assert.IsTrue(refs.Any(e => e.TargetId == lampId && e.PropertyPath == "targets.Array.data[1]"));
            Assert.IsTrue(refs.Any(e => e.TargetId == ObjectIdUtility.GetId(lamp.transform) && e.PropertyPath == "componentRef"));
            var ue = store.GetOutEdges(bId).Single(e => e.Kind == EdgeKind.UnityEventCall);
            Assert.AreEqual("Use", ue.MemberName);
            Assert.AreEqual(bId, ue.TargetId);
            Assert.IsTrue(store.GetOutEdges(bId).Any(e => e.Kind == EdgeKind.ScriptUsage));

            // 逆引き
            Assert.GreaterOrEqual(store.GetInEdges(lampId).Count(e => e.Kind == EdgeKind.SerializedReference), 2);
        }

        [Test]
        public void MissingReference_IsReportedAsUnresolved()
        {
            var (door, handle, b, lamp) = BuildScene();
            Object.DestroyImmediate(lamp);
            var store = Analyze(AnalysisTarget.ForLoadedScene(SceneManager.GetActiveScene()));
            string bId = ObjectIdUtility.GetId(b);
            var missing = store.GetOutEdges(bId).Where(e => e.Evidence == EvidenceLevel.Unresolved).ToList();
            Assert.IsTrue(missing.Any(e => e.PropertyPath == "target"), "Destroy された参照は Missing として残る");
            Assert.IsTrue(store.AllDiagnostics().Any(d => d.Code == DiagnosticCodes.MissingReference));
        }

        [Test]
        public void SavedScene_LoadedAndUnloadedIdsMatch_AndAnalysisDoesNotDirty()
        {
            var (door, handle, b, lamp) = BuildScene();
            var scene = SceneManager.GetActiveScene();
            string path = Folder + "/IdScene.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
            string guid = AssetDatabase.AssetPathToGUID(path);

            var loaded = Analyze(AnalysisTarget.ForLoadedScene(scene));
            Assert.IsFalse(scene.isDirty, "解析で Scene を Dirty にしない");
            string doorId = ObjectIdUtility.GetId(door);
            Assert.IsTrue(NodeIds.IsSceneObject(doorId));
            Assert.AreEqual(guid, NodeIds.TryGetSceneGuid(doorId));

            var loadedGos = loaded.GetNodesOfKind(NodeKind.GameObject).Where(id => NodeIds.IsSceneObject(id)).OrderBy(x => x).ToList();
            var loadedRefs = loaded.Edges.Where(e => e.Kind == EdgeKind.SerializedReference || e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.ParentChild)
                .Select(e => e.Kind + "|" + e.SourceId + "|" + e.TargetId + "|" + e.PropertyPath).OrderBy(x => x).ToList();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unloaded = Analyze(AnalysisTarget.ForUnloadedScene(guid, path));
            var unloadedGos = unloaded.GetNodesOfKind(NodeKind.GameObject).Where(id => NodeIds.IsSceneObject(id)).OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(loadedGos, unloadedGos, "GameObject の ID がロード有無で一致する");

            var unloadedRefs = unloaded.Edges.Where(e => e.Kind == EdgeKind.SerializedReference || e.Kind == EdgeKind.UnityEventCall || e.Kind == EdgeKind.ParentChild)
                .Select(e => e.Kind + "|" + e.SourceId + "|" + e.TargetId + "|" + e.PropertyPath).OrderBy(x => x).ToList();
            var parentLoaded = loadedRefs.Where(x => !x.StartsWith("ParentChild")).ToList();
            var parentUnloaded = unloadedRefs.Where(x => !x.StartsWith("ParentChild")).ToList();
            CollectionAssert.AreEquivalent(parentLoaded, parentUnloaded, "参照エッジ（propertyPath 含む）がロード有無で一致する");
            Assert.IsTrue(unloaded.GetNode(NodeIds.Scene(guid)).IsUnloaded);
        }

        [Test]
        public void PrefabInstance_VariantAndUnloadedInstanceIds()
        {
            // Prefab と Variant を作成
            var src = new GameObject("LampPrefabRoot");
            var bulb = new GameObject("Bulb");
            bulb.transform.SetParent(src.transform);
            var pb = src.AddComponent<OreTestBehaviour>();
            pb.target = bulb;
            string prefabPath = Folder + "/Lamp.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(src, prefabPath);
            Object.DestroyImmediate(src);

            var forVariant = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            string variantPath = Folder + "/LampVariant.prefab";
            var variant = PrefabUtility.SaveAsPrefabAsset(forVariant, variantPath);
            Object.DestroyImmediate(forVariant);

            var variantStore = Analyze(AnalysisTarget.ForAsset(AssetDatabase.AssetPathToGUID(variantPath), variantPath));
            Assert.IsTrue(variantStore.Edges.Any(e => e.Kind == EdgeKind.PrefabVariant && e.SourceId == NodeIds.Asset(AssetDatabase.AssetPathToGUID(variantPath)) && e.TargetId == NodeIds.Asset(AssetDatabase.AssetPathToGUID(prefabPath))));

            // Scene に Instance を置き Override
            var scene = SceneManager.GetActiveScene();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = "Lamp (overridden)";
            var other = new GameObject("Other");
            var ib = instance.GetComponent<OreTestBehaviour>();
            ib.target = other;
            PrefabUtility.RecordPrefabInstancePropertyModifications(ib);
            string scenePath = Folder + "/PrefabScene.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);

            var loaded = Analyze(AnalysisTarget.ForLoadedScene(scene));
            string instanceId = ObjectIdUtility.GetId(instance);
            string bulbInstanceId = ObjectIdUtility.GetId(instance.transform.Find("Bulb").gameObject);
            string otherId = ObjectIdUtility.GetId(other);
            Assert.IsTrue(loaded.GetOutEdges(instanceId).Any(e => e.Kind == EdgeKind.PrefabSource && e.TargetId == NodeIds.Asset(AssetDatabase.AssetPathToGUID(prefabPath))));
            var overrideEdge = loaded.GetOutEdges(ObjectIdUtility.GetId(ib)).Single(e => e.Kind == EdgeKind.SerializedReference && e.PropertyPath == "target");
            Assert.AreEqual(otherId, overrideEdge.TargetId);
            Assert.IsTrue(overrideEdge.HasQualifier("prefabOverride"));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unloaded = Analyze(AnalysisTarget.ForUnloadedScene(sceneGuid, scenePath));
            Assert.IsTrue(unloaded.ContainsNode(instanceId), "Prefab Instance ルートの ID が未ロード索引と一致する");
            Assert.IsTrue(unloaded.ContainsNode(bulbInstanceId), "Prefab Instance 内オブジェクトの ID が未ロード索引と一致する");
            Assert.AreEqual("Lamp (overridden)", unloaded.GetNode(instanceId).Name, "m_Name の Override を反映");
            var unloadedOverride = unloaded.Edges.Where(e => e.Kind == EdgeKind.SerializedReference && e.PropertyPath == "target" && e.HasQualifier("prefabOverride")).ToList();
            Assert.AreEqual(1, unloadedOverride.Count);
            Assert.AreEqual(otherId, unloadedOverride[0].TargetId);
            Assert.IsFalse(unloaded.Edges.Any(e => e.PropertyPath == "target" && e.HasQualifier("fromPrefabSource")), "Override された参照は Prefab 側の値を出さない");
            Assert.IsTrue(unloaded.Edges.Any(e => e.Kind == EdgeKind.PrefabSource && e.SourceId == instanceId));
        }

        [Test]
        public void ResolveBackFromId_InLoadedScene()
        {
            var (door, _, b, _) = BuildScene();
            string path = Folder + "/Resolve.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
            Assert.AreEqual(ResolveStatus.Resolved, ObjectResolver.TryResolve(ObjectIdUtility.GetId(door), out var obj));
            Assert.AreSame(door, obj);
            Assert.AreEqual(ResolveStatus.Resolved, ObjectResolver.TryResolve(ObjectIdUtility.GetId(b), out var comp));
            Assert.AreSame(b, comp);

            string doorId = ObjectIdUtility.GetId(door);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.AreEqual(ResolveStatus.SceneNotLoaded, ObjectResolver.TryResolve(doorId, out _), "未ロード Scene は勝手に開かない");
        }
    }
}
