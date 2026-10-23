using System.Linq;
using NUnit.Framework;
using ObjectRelationshipExplorer.Core;
using ObjectRelationshipExplorer.Editor;
using ObjectRelationshipExplorer.Editor.VRChat;
using ObjectRelationshipExplorer.Editor.VRChat.UdonSharpAnalysis;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Editor.ProgramSources.UdonGraphProgram;
using VRC.Udon.Graph;
using VRC.Udon.Serialization;

namespace ObjectRelationshipExplorer.Tests.VRChat
{
    public class UdonSharpSourceAnalyzerTests
    {
        const string Source = @"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

public class DoorController : UdonSharpBehaviour
{
    public GameObject door;
    [SerializeField] private Animator animator;
    public GameObject[] lamps;
    public OtherBehaviour other;
    public UdonSharpBehaviour target;
    const string EventName = ""OnOpened"";

    public override void Interact()
    {
        Open();
        if (animator != null) animator.SetTrigger(""Open"");
    }

    void Open()
    {
        door.SetActive(false);
        foreach (var l in lamps) l.SetActive(true);
        var found = GameObject.Find(""X"");
        found.SetActive(false);
        other.Notify();
        target.SendCustomEvent(EventName);
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Sync));
        transform.position = Vector3.zero;
        Networking.SetOwner(Networking.LocalPlayer, door);
        lamps[0].GetComponent<Animator>().SetBool(""On"", true);
    }

    public void Sync() { }

    void Unused() { door.SetActive(true); }
}";

        static ProgramModel Analyze() => UdonSharpSourceAnalyzer.AnalyzeSource(Source, "Assets/DoorController.cs", "scriptguid", "DoorController", new[] { "OtherBehaviour" });

        [Test]
        public void ExtractsDeclarationsAndCalls()
        {
            var m = Analyze();
            Assert.AreEqual("DoorController", m.ClassName);
            Assert.IsNotNull(m.FindDeclaration("door", DeclarationKind.Field));
            Assert.IsTrue(m.FindDeclaration("lamps", DeclarationKind.Field).IsArray);
            Assert.IsTrue(m.FindDeclaration("animator", DeclarationKind.Field).IsPublic, "[SerializeField] は Inspector 設定値を持つ");
            Assert.IsTrue(m.Calls.Any(c => c.FromName == "Interact" && c.ToName == "Open"));
            Assert.IsTrue(m.DeclaresEvent("Sync"));
            Assert.IsTrue(m.DeclaresEvent("_interact"), "組込みイベント名の正規化");
            Assert.IsFalse(m.DeclaresEvent("Open"), "private メソッドはイベントとして公開されない");
        }

        [Test]
        public void ClassifiesOperationsWithReceivers()
        {
            var facts = Analyze().Facts;
            var setActive = facts.Single(f => f.Entry == "Open" && f.Variable == "door" && f.Api == "GameObject.SetActive");
            Assert.AreEqual(ReceiverKind.Variable, setActive.Receiver);
            Assert.IsFalse(setActive.Conditional);
            CollectionAssert.Contains(setActive.ReachableFrom, "Interact");

            var lamp = facts.First(f => f.Variable == "lamps" && f.Api == "GameObject.SetActive");
            Assert.IsTrue(lamp.ArrayElement);
            Assert.IsTrue(lamp.Conditional, "foreach 内は may-edge");

            var trigger = facts.Single(f => f.Api == "Animator.SetTrigger");
            Assert.AreEqual("animator", trigger.Variable);
            Assert.IsTrue(trigger.Conditional);

            Assert.IsTrue(facts.Any(f => f.Api == "GameObject.SetActive" && f.Receiver == ReceiverKind.Unresolved && f.UnresolvedReason.Contains("Find")), "GameObject.Find は Unresolved");

            var notify = facts.Single(f => f.IsDispatch && f.Variable == "other");
            Assert.AreEqual("Notify", notify.EventName);

            var send = facts.Single(f => f.IsDispatch && f.Variable == "target");
            Assert.AreEqual("OnOpened", send.EventName, "const string を解決");

            var net = facts.Single(f => f.IsNetwork);
            Assert.AreEqual(ReceiverKind.Self, net.Receiver);
            Assert.AreEqual("Sync", net.EventName, "nameof を解決");

            var pos = facts.Single(f => f.Category == OperationRules.Transform);
            Assert.AreEqual(ReceiverKind.Self, pos.Receiver);
            Assert.AreEqual("Transform", pos.ReceiverType);

            var owner = facts.Single(f => f.Category == OperationRules.Ownership);
            Assert.AreEqual("door", owner.Variable);

            var viaGetComponent = facts.Single(f => f.Api == "Animator.SetBool");
            Assert.AreEqual("lamps", viaGetComponent.Variable);
            Assert.IsTrue(viaGetComponent.ArrayElement);
            StringAssert.Contains("GetComponent", viaGetComponent.Via);

            var unused = facts.Single(f => f.Entry == "Unused");
            StringAssert.Contains("到達しない", unused.UnresolvedReason);
        }
    }

    public class UdonGraphAndBindingTests
    {
        const string Folder = "Assets/__OreUdonTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__OreUdonTests");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ProgramModelCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
            ProgramModelCache.Clear();
        }

        static UdonNodeData Node(UdonGraphData g, string fullName, string uid)
        {
            var n = new UdonNodeData(g, fullName) { uid = uid, nodeUIDs = new string[0], flowUIDs = new string[0], nodeValues = new SerializableObjectContainer[0] };
            g.nodes.Add(n);
            return n;
        }

        static UdonGraphProgramAsset BuildGraph()
        {
            var asset = ScriptableObject.CreateInstance<UdonGraphProgramAsset>();
            var g = asset.graphData;
            var v = Node(g, "Variable_UnityEngineGameObject", "v1");
            v.nodeValues = new[]
            {
                SerializableObjectContainer.Serialize(null, typeof(GameObject)),
                SerializableObjectContainer.Serialize("door", typeof(string)),
                SerializableObjectContainer.Serialize(true, typeof(bool)),
            };
            var ev = Node(g, "Event_Interact", "e1");
            ev.flowUIDs = new[] { "b1" };
            var branch = Node(g, "Branch", "b1");
            branch.flowUIDs = new[] { "m1" };
            var get = Node(g, "Get_Variable", "g1");
            get.nodeValues = new[] { SerializableObjectContainer.Serialize("v1", typeof(string)) };
            var set = Node(g, "UnityEngineGameObject.__SetActive__SystemBoolean__SystemVoid", "m1");
            set.nodeUIDs = new[] { "g1|0", "" };
            set.nodeValues = new[] { SerializableObjectContainer.Serialize(null, typeof(GameObject)), SerializableObjectContainer.Serialize(false, typeof(bool)) };
            set.flowUIDs = new[] { "s1" };
            var send = Node(g, "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", "s1");
            send.nodeUIDs = new[] { "", "" };
            send.nodeValues = new[] { SerializableObjectContainer.Serialize(null, typeof(Object)), SerializableObjectContainer.Serialize("Ping", typeof(string)) };
            return asset;
        }

        [Test]
        public void GraphAnalyzer_FindsVariableTargetsAndDispatch()
        {
            var model = UdonGraphAnalyzer.Analyze(BuildGraph(), "guid", "Assets/x.asset");
            Assert.IsNotNull(model.FindDeclaration("door", DeclarationKind.Variable));
            Assert.IsTrue(model.DeclaresEvent("Interact"));
            var setActive = model.Facts.Single(f => f.Api == "GameObject.SetActive");
            Assert.AreEqual(ReceiverKind.Variable, setActive.Receiver);
            Assert.AreEqual("door", setActive.Variable);
            Assert.IsTrue(setActive.Conditional, "Branch 以降は may-edge");
            var send = model.Facts.Single(f => f.IsDispatch);
            Assert.AreEqual(ReceiverKind.Self, send.Receiver, "未接続のインスタンス入力は自身");
            Assert.AreEqual("Ping", send.EventName);
        }

        [Test]
        public void Binding_ResolvesInstanceVariables_AndChecksReceiverEvents()
        {
            // SDK がテスト用グラフのコンパイルでエラーを出しても解析には影響しない
            LogAssert.ignoreFailingMessages = true;
            var asset = BuildGraph();
            AssetDatabase.CreateAsset(asset, Folder + "/DoorGraph.asset");
            string programGuid = AssetDatabase.AssetPathToGUID(Folder + "/DoorGraph.asset");

            var doorTarget = new GameObject("DoorMesh");
            var host = new GameObject("DoorSwitch");
            var ub = host.AddComponent<UdonBehaviour>();
            ub.programSource = asset;
            Assert.IsTrue(ub.publicVariables.TryAddVariable(new UdonVariable<GameObject>("door", doorTarget)));

            var target = AnalysisTarget.ForLoadedScene(SceneManager.GetActiveScene());
            var context = new AnalysisContext(OreSettings.instance, new PrefabStructureCache());
            var store = new GraphStore();
            foreach (var p in ProviderRegistry.Providers)
                if (p.CanAnalyze(target)) store.ReplaceOwner(OwnerKeys.Create(p.Id, target.Key), p.Analyze(target, context), null);

            string ubId = ObjectIdUtility.GetId(ub);
            string doorId = ObjectIdUtility.GetId(doorTarget);
            var outEdges = store.GetOutEdges(ubId).ToList();

            Assert.IsTrue(outEdges.Any(e => e.Kind == EdgeKind.UdonProgramBinding && e.TargetId == NodeIds.Asset(programGuid)));
            var varRef = outEdges.Single(e => e.Kind == EdgeKind.UdonVariableReference);
            Assert.AreEqual(doorId, varRef.TargetId);
            Assert.AreEqual("door", varRef.MemberName);
            Assert.AreEqual(EvidenceLevel.Confirmed, varRef.Evidence);

            var control = outEdges.Single(e => e.Kind == EdgeKind.MethodControl);
            Assert.AreEqual(doorId, control.TargetId);
            Assert.AreEqual(EvidenceLevel.Inferred, control.Evidence, "静的推定は確定扱いしない");
            StringAssert.Contains("Interact", control.MemberName);
            Assert.IsTrue(control.HasQualifier("mayEdge"));

            var dispatch = outEdges.Single(e => e.Kind == EdgeKind.EventDispatch);
            Assert.AreEqual(ubId, dispatch.TargetId);
            Assert.AreEqual(EvidenceLevel.Unresolved, dispatch.Evidence, "受信側に Ping イベントが無い");
            StringAssert.Contains("Ping", dispatch.Explanation);

            // Udon 内部表現（publicVariablesUnityEngineObjects）を名前なしの SerializedReference として重複させない
            Assert.IsFalse(outEdges.Any(e => e.Kind == EdgeKind.SerializedReference && e.TargetId == doorId));
        }
    }
}
