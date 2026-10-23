using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ObjectRelationshipExplorer.Core.Yaml;

namespace ObjectRelationshipExplorer.Tests
{
    public class UnityYamlParserTests
    {
        const string Scene = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!29 &1
OcclusionCullingSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 2
--- !u!1 &100
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 101}
  - component: {fileID: 102}
  m_Layer: 0
  m_Name: 'Door: ''Main'''
  m_TagString: EditorOnly
  m_IsActive: 0
--- !u!4 &101
Transform:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 100}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_Children:
  - {fileID: 201}
  m_Father: {fileID: 0}
--- !u!114 &102
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_GameObject: {fileID: 100}
  m_Enabled: 1
  m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
  m_Name:
  target: {fileID: 200}
  targets:
  - {fileID: 200}
  - {fileID: 0}
  - {fileID: 999}
  material: {fileID: 2100000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 2}
  description: ""This: is
    a multi-line string with {fileID: 12345}""
  nested:
    inner: {fileID: 100}
  onClick:
    m_PersistentCalls:
      m_Calls:
      - m_Target: {fileID: 102}
        m_TargetAssemblyTypeName: Door, Assembly-CSharp
        m_MethodName: Open
        m_Mode: 1
        m_Arguments:
          m_ObjectArgument: {fileID: 0}
        m_CallState: 2
--- !u!1 &200
GameObject:
  m_Component:
  - component: {fileID: 201}
  m_Name: Child
  m_IsActive: 1
--- !u!4 &201
Transform:
  m_GameObject: {fileID: 200}
  m_Children: []
  m_Father: {fileID: 101}
--- !u!1001 &300
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 101}
    m_Modifications:
    - target: {fileID: 5000, guid: cccccccccccccccccccccccccccccccc, type: 3}
      propertyPath: m_Name
      value: Lamp (1)
      objectReference: {fileID: 0}
    - target: {fileID: 5002, guid: cccccccccccccccccccccccccccccccc, type: 3}
      propertyPath: door
      value:
      objectReference: {fileID: 100}
    m_RemovedComponents:
    - {fileID: 5003, guid: cccccccccccccccccccccccccccccccc, type: 3}
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: cccccccccccccccccccccccccccccccc, type: 3}
--- !u!4 &301 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: 5001, guid: cccccccccccccccccccccccccccccccc, type: 3}
  m_PrefabInstance: {fileID: 300}
  m_PrefabAsset: {fileID: 0}
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 101}
  - {fileID: 300}
";

        static UnityYamlDocument Parse() => UnityYamlParser.ParseText(Scene);

        [Test]
        public void ParsesDocumentHeaders()
        {
            var doc = Parse();
            Assert.IsTrue(doc.IsText);
            Assert.AreEqual(9, doc.Objects.Count);
            Assert.AreEqual("GameObject", doc.Get(100).TypeName);
            Assert.AreEqual(114, doc.Get(102).ClassId);
            Assert.IsTrue(doc.Get(301).Stripped);
            Assert.IsNotNull(doc.Get(9223372036854775807));
        }

        [Test]
        public void ParsesScalars_IncludingQuotedValues()
        {
            var go = Parse().Get(100);
            Assert.AreEqual("Door: 'Main'", go.GetScalar("m_Name"));
            Assert.AreEqual("EditorOnly", go.GetScalar("m_TagString"));
            Assert.AreEqual("0", go.GetScalar("m_IsActive"));
        }

        [Test]
        public void ParsesComponentList()
        {
            var go = Parse().Get(100);
            var comps = go.GetArrayReferences("m_Component").ToList();
            Assert.AreEqual(2, comps.Count);
            Assert.AreEqual("m_Component.Array.data[0].component", comps[0].Path);
            Assert.AreEqual(102, comps[1].FileId);
        }

        [Test]
        public void ParsesReferencePaths_LikeSerializedProperty()
        {
            var mb = Parse().Get(102);
            Assert.IsTrue(mb.TryGetReference("target", out var t));
            Assert.AreEqual(200, t.FileId);
            Assert.IsTrue(t.IsLocal);
            var list = mb.GetArrayReferences("targets").ToList();
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual("targets.Array.data[2]", list[2].Path);
            Assert.AreEqual(999, list[2].FileId);
            Assert.IsTrue(list[1].IsNull);
            Assert.IsTrue(mb.TryGetReference("material", out var mat));
            Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", mat.Guid);
            Assert.AreEqual(2, mat.Type);
            Assert.IsTrue(mb.TryGetReference("nested.inner", out var inner));
            Assert.AreEqual(100, inner.FileId);
            Assert.IsTrue(mb.TryGetReference("m_Script", out var script));
            Assert.AreEqual(11500000, script.FileId);
        }

        [Test]
        public void MultiLineQuotedStrings_DoNotProduceReferences()
        {
            var mb = Parse().Get(102);
            Assert.IsFalse(mb.References.Any(r => r.FileId == 12345));
        }

        [Test]
        public void ParsesUnityEventPersistentCalls()
        {
            var mb = Parse().Get(102);
            Assert.IsTrue(mb.TryGetReference("onClick.m_PersistentCalls.m_Calls.Array.data[0].m_Target", out var target));
            Assert.AreEqual(102, target.FileId);
            Assert.AreEqual("Open", mb.PathScalars["onClick.m_PersistentCalls.m_Calls.Array.data[0].m_MethodName"]);
            Assert.IsTrue(mb.TryGetReference("onClick.m_PersistentCalls.m_Calls.Array.data[0].m_Arguments.m_ObjectArgument", out _));
        }

        [Test]
        public void ParsesTransformHierarchy()
        {
            var doc = Parse();
            Assert.AreEqual(101, doc.Get(201).GetLocalReference("m_Father"));
            Assert.AreEqual(201, doc.Get(101).GetArrayReferences("m_Children").Single().FileId);
            Assert.AreEqual(0, doc.Get(101).GetLocalReference("m_Father"));
        }

        [Test]
        public void ParsesPrefabInstanceModifications()
        {
            var pi = Parse().Get(300);
            Assert.AreEqual(2, pi.Modifications.Count);
            var name = pi.Modifications[0];
            Assert.AreEqual(5000, name.Target.FileId);
            Assert.AreEqual("cccccccccccccccccccccccccccccccc", name.Target.Guid);
            Assert.AreEqual("m_Name", name.PropertyPath);
            Assert.AreEqual("Lamp (1)", name.Value);
            Assert.IsTrue(name.ObjectReference.IsNull);
            var door = pi.Modifications[1];
            Assert.AreEqual("door", door.PropertyPath);
            Assert.AreEqual(100, door.ObjectReference.FileId);
            Assert.AreEqual(101, pi.GetLocalReference("m_Modification.m_TransformParent"));
            Assert.IsTrue(pi.TryGetReference("m_SourcePrefab", out var src));
            Assert.AreEqual("cccccccccccccccccccccccccccccccc", src.Guid);
            Assert.IsTrue(pi.References.Any(r => r.Path.StartsWith("m_Modification.m_RemovedComponents") && r.FileId == 5003));
        }

        [Test]
        public void ParsesStrippedObjects()
        {
            var stripped = Parse().Get(301);
            Assert.IsTrue(stripped.TryGetReference("m_CorrespondingSourceObject", out var corr));
            Assert.AreEqual(5001, corr.FileId);
            Assert.AreEqual(300, stripped.GetLocalReference("m_PrefabInstance"));
        }

        [Test]
        public void BinaryFiles_AreReportedAsNonText()
        {
            var doc = UnityYamlParser.Parse(new MemoryStream(new byte[] { 0, 0, 0, 0, 0x16, 0, 0, 0 }));
            Assert.IsFalse(doc.IsText);
            Assert.IsNotNull(doc.Error);
            var text = UnityYamlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Scene)));
            Assert.IsTrue(text.IsText);
            Assert.AreEqual(9, text.Objects.Count);
        }

        [Test]
        public void NegativeFileIds_AreParsed()
        {
            var doc = UnityYamlParser.ParseText("%YAML 1.1\n--- !u!114 &-4567890123456789\nMonoBehaviour:\n  m_GameObject: {fileID: -12}\n");
            var o = doc.Get(-4567890123456789);
            Assert.IsNotNull(o);
            Assert.AreEqual(-12, o.GetLocalReference("m_GameObject"));
        }
    }
}
