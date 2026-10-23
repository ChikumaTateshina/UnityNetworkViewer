using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using NUnit.Framework;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Export;
using UnityNetworkViewer.Core.Layout;

namespace UnityNetworkViewer.Tests
{
    static class G
    {
        public static GraphNode N(string id, NodeKind kind = NodeKind.GameObject, string name = null, string scene = null, NodeFlags flags = NodeFlags.None)
            => new GraphNode { Id = id, Kind = kind, Name = name ?? id, SceneGuid = scene, Flags = flags };

        public static AnalysisResult Result(params (string s, string t, EdgeKind k)[] edges)
        {
            var r = new AnalysisResult("test");
            foreach (var (s, t, k) in edges)
            {
                if (!r.ContainsNode(s)) r.AddNode(N(s));
                if (!r.ContainsNode(t)) r.AddNode(N(t));
                r.AddEdge(s, t, k, EvidenceLevel.Confirmed);
            }
            return r;
        }
    }

    public class GraphStoreTests
    {
        [Test]
        public void ReplaceOwner_ReplacesPreviousResultAtomically()
        {
            var store = new GraphStore();
            store.ReplaceOwner("p|a", G.Result(("A", "B", EdgeKind.SerializedReference)), null);
            Assert.AreEqual(2, store.NodeCount);
            Assert.AreEqual(1, store.EdgeCount);

            store.ReplaceOwner("p|a", G.Result(("A", "C", EdgeKind.SerializedReference)), null);
            Assert.IsFalse(store.ContainsNode("B"), "古い結果のノードは削除される");
            Assert.IsTrue(store.ContainsNode("C"));
            Assert.AreEqual(1, store.EdgeCount);
            Assert.AreEqual(1, store.GetInEdgeIds("C").Count);
            Assert.AreEqual(0, store.GetInEdgeIds("B").Count);
        }

        [Test]
        public void MultiEdges_SameKindAreNotCollapsed()
        {
            var r = new AnalysisResult("test");
            r.AddNode(G.N("A"));
            r.AddNode(G.N("B"));
            r.AddEdge("A", "B", EdgeKind.SerializedReference, EvidenceLevel.Confirmed, "list.Array.data[0]");
            r.AddEdge("A", "B", EdgeKind.SerializedReference, EvidenceLevel.Confirmed, "list.Array.data[1]");
            r.AddEdge("A", "B", EdgeKind.SerializedReference, EvidenceLevel.Confirmed, "list.Array.data[1]");
            var store = new GraphStore();
            store.ReplaceOwner("p|x", r, null);
            Assert.AreEqual(3, store.EdgeCount);
            Assert.AreEqual(3, store.GetOutEdges("A").Select(e => e.Id).Distinct().Count());
        }

        [Test]
        public void EdgeIds_AreDeterministic()
        {
            var e1 = new GraphEdge { SourceId = "A", TargetId = "B", Kind = EdgeKind.SerializedReference, ProviderId = "p", PropertyPath = "x" };
            var e2 = new GraphEdge { SourceId = "A", TargetId = "B", Kind = EdgeKind.SerializedReference, ProviderId = "p", PropertyPath = "x" };
            var e3 = new GraphEdge { SourceId = "A", TargetId = "B", Kind = EdgeKind.SerializedReference, ProviderId = "p", PropertyPath = "y" };
            Assert.AreEqual(EdgeIds.Create(e1), EdgeIds.Create(e2));
            Assert.AreNotEqual(EdgeIds.Create(e1), EdgeIds.Create(e3));
        }

        [Test]
        public void Placeholder_IsReplacedByRealNode_AndSurvivesWhileReferenced()
        {
            var store = new GraphStore();
            var refs = new AnalysisResult("serialized");
            refs.AddNode(G.N("comp", NodeKind.Component));
            refs.AddNode(new GraphNode { Id = "asset1:prefab", Kind = NodeKind.PrefabAsset, Name = "ph", Flags = NodeFlags.Placeholder });
            refs.AddEdge("comp", "asset1:prefab", EdgeKind.SerializedReference, EvidenceLevel.Confirmed);
            store.ReplaceOwner("serialized|scene:1", refs, null);

            var asset = new AnalysisResult("prefab");
            asset.AddNode(new GraphNode { Id = "asset1:prefab", Kind = NodeKind.PrefabAsset, Name = "Door" });
            store.ReplaceOwner("prefab|asset:p", asset, null);
            Assert.AreEqual("Door", store.GetNode("asset1:prefab").Name);
            Assert.IsFalse(store.GetNode("asset1:prefab").IsPlaceholder);

            store.RemoveOwner("prefab|asset:p");
            Assert.IsTrue(store.ContainsNode("asset1:prefab"), "他 owner のエッジが参照している間は残る");
            Assert.IsTrue(store.GetNode("asset1:prefab").IsPlaceholder);

            store.RemoveOwner("serialized|scene:1");
            Assert.AreEqual(0, store.NodeCount);
            Assert.AreEqual(0, store.EdgeCount);
        }

        [Test]
        public void ProviderPlaceholderInheritance_DoesNotLeakAfterRepeatedReplace()
        {
            var store = new GraphStore();
            var other = new AnalysisResult("o");
            other.AddNode(G.N("X"));
            other.AddEdge("X", "Y", EdgeKind.SerializedReference, EvidenceLevel.Confirmed);
            store.ReplaceOwner("o|1", other, null);
            for (int i = 0; i < 5; i++)
            {
                var y = new AnalysisResult("y");
                y.AddNode(G.N("Y", name: "Y" + i));
                store.ReplaceOwner("y|1", y, null);
            }
            Assert.AreEqual("Y4", store.GetNode("Y").Name);
            Assert.LessOrEqual(store.GetOwner("o|1").Nodes.Count, 3);
        }

        [Test]
        public void MissingEndpoint_CreatesPlaceholderNode()
        {
            var r = new AnalysisResult("t");
            r.AddNode(G.N("A"));
            r.AddEdge("A", "ghost", EdgeKind.SerializedReference, EvidenceLevel.Confirmed);
            var store = new GraphStore();
            store.ReplaceOwner("t|1", r, null);
            Assert.IsTrue(store.ContainsNode("ghost"));
            Assert.IsTrue(store.GetNode("ghost").IsPlaceholder);
        }

        [Test]
        public void SecondaryIndexes_AndDependencyIndex()
        {
            var r = new AnalysisResult("t");
            r.AddNode(G.N("A", scene: "S1"));
            r.AddNode(new GraphNode { Id = "B", Kind = NodeKind.Component, Name = "B", AssetGuid = "G1" });
            r.DependencyKeys.Add("dep-guid");
            var store = new GraphStore();
            store.ReplaceOwner("t|scene:S1", r, null);
            CollectionAssert.Contains(store.GetNodesInScene("S1").ToList(), "A");
            CollectionAssert.Contains(store.GetNodesOfAsset("G1").ToList(), "B");
            CollectionAssert.Contains(store.GetNodesOfKind(NodeKind.Component).ToList(), "B");
            CollectionAssert.Contains(store.GetOwnersDependingOn("dep-guid").ToList(), "t|scene:S1");
            store.RemoveOwner("t|scene:S1");
            Assert.AreEqual(0, store.GetOwnersDependingOn("dep-guid").Count);
            Assert.AreEqual(0, store.GetNodesInScene("S1").Count);
        }

        [Test]
        public void Batch_CoalescesChangeEvents()
        {
            var store = new GraphStore();
            int events = 0;
            store.Changed += _ => events++;
            store.BeginBatch();
            store.ReplaceOwner("a|1", G.Result(("A", "B", EdgeKind.ParentChild)), null);
            store.ReplaceOwner("a|2", G.Result(("C", "D", EdgeKind.ParentChild)), null);
            store.EndBatch();
            Assert.AreEqual(1, events);
        }
    }

    public class QueryTests
    {
        static GraphQueryService Build(params (string s, string t, EdgeKind k)[] edges)
        {
            var store = new GraphStore();
            store.ReplaceOwner("t|1", G.Result(edges), null);
            return new GraphQueryService(store);
        }

        [Test]
        public void Bfs_RespectsDepthAndDirection()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference), ("B", "C", EdgeKind.SerializedReference), ("Z", "A", EdgeKind.SerializedReference));
            var r1 = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 1, Direction = RelationDirection.Outgoing });
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, r1.NodeDepth.Keys);
            var r2 = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 2, Direction = RelationDirection.Both });
            CollectionAssert.AreEquivalent(new[] { "A", "B", "C", "Z" }, r2.NodeDepth.Keys);
            Assert.AreEqual(2, r2.GetDepth("C"));
            var r3 = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 3, Direction = RelationDirection.Incoming });
            CollectionAssert.AreEquivalent(new[] { "A", "Z" }, r3.NodeDepth.Keys);
            Assert.AreEqual(EdgeRole.Incoming, r3.EdgeRoles.Values.Single());
        }

        [Test]
        public void Bfs_TerminatesOnCycles()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference), ("B", "A", EdgeKind.SerializedReference), ("B", "C", EdgeKind.SerializedReference), ("C", "A", EdgeKind.SerializedReference));
            var r = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 50 });
            Assert.AreEqual(3, r.NodeDepth.Count);
            Assert.AreEqual(4, r.EdgeRoles.Count);
        }

        [Test]
        public void Depth0_ReturnsOnlyRoots()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference));
            var r = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 0 });
            CollectionAssert.AreEquivalent(new[] { "A" }, r.NodeDepth.Keys);
        }

        [Test]
        public void EdgeKindFilter_IsApplied()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference), ("A", "C", EdgeKind.ParentChild));
            var r = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 1, EdgeKinds = new HashSet<EdgeKind> { EdgeKind.ParentChild } });
            CollectionAssert.AreEquivalent(new[] { "A", "C" }, r.NodeDepth.Keys);
        }

        [Test]
        public void MaxNodes_TruncatesAndReportsNeighbors()
        {
            var edges = Enumerable.Range(0, 50).Select(i => ("Hub", "N" + i, EdgeKind.SerializedReference)).ToArray();
            var q = Build(edges);
            var r = q.QuerySubgraph(new[] { "Hub" }, new QueryOptions { MaxDepth = 1, MaxNodes = 10 });
            Assert.AreEqual(10, r.NodeDepth.Count);
            Assert.IsTrue(r.Truncated);
            Assert.AreEqual(41, r.TruncatedNeighbors["Hub"]);
        }

        [Test]
        public void Intersection_ReturnsCommonNeighbors()
        {
            var q = Build(("A", "Shared", EdgeKind.SerializedReference), ("B", "Shared", EdgeKind.SerializedReference),
                ("A", "OnlyA", EdgeKind.SerializedReference), ("B", "OnlyB", EdgeKind.SerializedReference));
            var opt = new QueryOptions { MaxDepth = 1, Direction = RelationDirection.Outgoing };
            var union = q.QueryMulti(new[] { "A", "B" }, opt, SelectionSetMode.Union);
            CollectionAssert.AreEquivalent(new[] { "A", "B", "Shared", "OnlyA", "OnlyB" }, union.NodeDepth.Keys);
            var inter = q.QueryMulti(new[] { "A", "B" }, opt, SelectionSetMode.Intersection);
            CollectionAssert.AreEquivalent(new[] { "A", "B", "Shared" }, inter.NodeDepth.Keys);
        }

        [Test]
        public void MissingRoots_AreReported()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference));
            var r = q.QuerySubgraph(new[] { "nope" }, new QueryOptions());
            CollectionAssert.AreEqual(new[] { "nope" }, r.MissingRoots);
        }

        [Test]
        public void FindPath_ReturnsShortestEdgeSequence()
        {
            var q = Build(("A", "B", EdgeKind.SerializedReference), ("B", "C", EdgeKind.SerializedReference), ("A", "X", EdgeKind.SerializedReference), ("X", "Y", EdgeKind.SerializedReference), ("Y", "C", EdgeKind.SerializedReference));
            var path = q.FindPath("A", "C", new QueryOptions { Direction = RelationDirection.Outgoing });
            Assert.AreEqual(2, path.Count);
            Assert.IsNull(q.FindPath("C", "A", new QueryOptions { Direction = RelationDirection.Outgoing }));
            Assert.AreEqual(2, q.FindPath("C", "A", new QueryOptions { Direction = RelationDirection.Both }).Count);
        }

        [Test]
        public void Search_SupportsTokens()
        {
            var store = new GraphStore();
            var r = new AnalysisResult("t");
            r.AddNode(new GraphNode { Id = "1", Kind = NodeKind.GameObject, Name = "Door", HierarchyPath = "World/Door" });
            r.AddNode(new GraphNode { Id = "2", Kind = NodeKind.Component, Name = "Animator", TypeName = "UnityEngine.Animator" });
            r.AddNode(new GraphNode { Id = "3", Kind = NodeKind.GameObject, Name = "Window", Flags = NodeFlags.Missing });
            r.AddEdge(new GraphEdge { SourceId = "2", TargetId = "1", Kind = EdgeKind.MethodControl, Evidence = EvidenceLevel.Inferred, MemberName = "GameObject.SetActive" });
            store.ReplaceOwner("t|1", r, null);
            var q = new GraphQueryService(store);
            CollectionAssert.AreEquivalent(new[] { "1" }, q.Search("door").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "2" }, q.Search("type:Animator").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "1", "3" }, q.Search("kind:GameObject").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "3" }, q.Search("flag:Missing").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "1", "2" }, q.Search("evidence:Inferred").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "2" }, q.Search("method:SetActive").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "3" }, q.Search("kind:GameObject -door").Select(n => n.Id));
        }

        [Test]
        public void Search_IndexedFiltersSupportPartialNamesNegationAndReplacement()
        {
            var store = new GraphStore();
            var r = new AnalysisResult("test");
            r.AddNode(new GraphNode { Id = NodeIds.Scene("s1"), Kind = NodeKind.Scene, Name = "Main World", SceneGuid = "s1", AssetPath = "Assets/Main.unity", ProviderId = "unity.hierarchy" });
            r.AddNode(new GraphNode { Id = "a", Kind = NodeKind.GameObject, Name = "Door", SceneGuid = "s1", ProviderId = "unity.hierarchy" });
            r.AddNode(new GraphNode { Id = "b", Kind = NodeKind.Component, Name = "Door", SceneGuid = "s1", ProviderId = "unity.serialized" });
            r.AddNode(new GraphNode { Id = "c", Kind = NodeKind.GameObject, Name = "Door", SceneGuid = "s2", ProviderId = "custom" });
            store.ReplaceOwner("test|a", r, null);
            var q = new GraphQueryService(store);
            CollectionAssert.AreEquivalent(new[] { "a" }, q.Search("kind:game scene:main provider:HIER door").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, q.Search("scene:S1 door").Select(n => n.Id));
            CollectionAssert.AreEquivalent(new[] { "c" }, q.Search("-provider:unity door").Select(n => n.Id));
            Assert.IsEmpty(q.Search("scene:unknown door"));
            Assert.IsEmpty(q.Search("kind:unknown"));
            var replacement = new AnalysisResult("test");
            replacement.AddNode(new GraphNode { Id = "a", Kind = NodeKind.Component, Name = "Door", SceneGuid = "s2", ProviderId = "custom" });
            store.ReplaceOwner("test|a", replacement, null);
            Assert.IsEmpty(q.Search("provider:unity"));
            Assert.IsEmpty(q.Search("scene:s1"));
            CollectionAssert.AreEquivalent(new[] { "a" }, q.Search("kind:comp provider:custom scene:s2").Select(n => n.Id));
        }

        [Test]
        public void CrossSceneBoundary_CanBeDisabled()
        {
            var store = new GraphStore();
            var r = new AnalysisResult("t");
            r.AddNode(G.N("A", scene: "S1"));
            r.AddNode(G.N("B", scene: "S2"));
            r.AddNode(new GraphNode { Id = "Asset", Kind = NodeKind.PrefabAsset, Name = "Asset" });
            r.AddEdge("A", "Asset", EdgeKind.PrefabSource, EvidenceLevel.Confirmed);
            r.AddEdge("B", "Asset", EdgeKind.PrefabSource, EvidenceLevel.Confirmed);
            store.ReplaceOwner("t|1", r, null);
            var q = new GraphQueryService(store);
            var cross = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 2 });
            Assert.IsTrue(cross.ContainsNode("B"));
            var local = q.QuerySubgraph(new[] { "A" }, new QueryOptions { MaxDepth = 2, CrossSceneBoundaries = false });
            Assert.IsFalse(local.ContainsNode("B"));
            Assert.IsTrue(local.ContainsNode("Asset"));
        }

        [Test]
        public void Diagnostics_DetectCyclesAndOrphans()
        {
            var store = new GraphStore();
            var r = G.Result(("A", "B", EdgeKind.SerializedReference), ("B", "A", EdgeKind.SerializedReference), ("P", "C", EdgeKind.ParentChild), ("C", "P", EdgeKind.ParentChild));
            r.AddNode(G.N("Lonely", NodeKind.Component));
            store.ReplaceOwner("t|1", r, null);
            var report = GraphDiagnosticsAnalyzer.Analyze(store);
            Assert.AreEqual(1, report.CycleCount, "構造エッジの循環は数えない");
            Assert.AreEqual(1, report.OrphanCount);
        }
    }

    public class LayoutTests
    {
        static LayoutInput Input(int n, params (int s, int t)[] edges)
        {
            var input = new LayoutInput();
            for (int i = 0; i < n; i++) input.AddNode("n" + i);
            foreach (var (s, t) in edges) input.AddEdge("n" + s, "n" + t, EdgeKind.ParentChild);
            input.Roots.Add(0);
            return input;
        }

        [Test]
        public void AllLayouts_ProduceFinitePositions_WithCycles()
        {
            foreach (LayoutKind kind in Enum.GetValues(typeof(LayoutKind)))
            {
                var input = Input(8, (0, 1), (1, 2), (2, 0), (2, 3), (4, 5), (6, 7));
                for (int i = 0; i < 8; i++) input.GroupKeys[i] = i < 4 ? "g1" : "g2";
                var result = LayoutFactory.Create(kind).Compute(input, CancellationToken.None);
                Assert.AreEqual(8, result.Positions.Length, kind.ToString());
                foreach (var p in result.Positions)
                {
                    Assert.IsFalse(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y), kind.ToString());
                }
                if (kind == LayoutKind.Grouped) Assert.AreEqual(2, result.Groups.Count);
            }
        }

        [Test]
        public void Hierarchical_PlacesParentAboveChild()
        {
            var input = Input(3, (0, 1), (1, 2));
            var p = new HierarchicalLayout().Compute(input, CancellationToken.None).Positions;
            Assert.Less(p[0].Y, p[1].Y);
            Assert.Less(p[1].Y, p[2].Y);
        }

        [Test]
        public void PinnedNodes_KeepTheirPosition()
        {
            foreach (LayoutKind kind in Enum.GetValues(typeof(LayoutKind)))
            {
                var input = Input(4, (0, 1), (1, 2), (2, 3));
                input.Pinned[2] = new Vec2(1234f, -567f);
                var p = LayoutFactory.Create(kind).Compute(input, CancellationToken.None).Positions;
                Assert.AreEqual(1234f, p[2].X, 0.01f, kind.ToString());
                Assert.AreEqual(-567f, p[2].Y, 0.01f, kind.ToString());
            }
        }

        [Test]
        public void Radial_PutsRootAtCenter()
        {
            var input = Input(5, (0, 1), (0, 2), (1, 3), (2, 4));
            var p = new RadialLayout().Compute(input, CancellationToken.None).Positions;
            Assert.AreEqual(0f, p[0].X, 0.01f);
            Assert.AreEqual(0f, p[0].Y, 0.01f);
            Assert.Greater(p[3].Length, p[1].Length);
        }

        [Test]
        public void Tarjan_FindsStronglyConnectedComponents()
        {
            var adj = new[] { new List<int> { 1 }, new List<int> { 2 }, new List<int> { 0, 3 }, new List<int>() };
            var comp = HierarchicalLayout.Tarjan(adj, out int count);
            Assert.AreEqual(2, count);
            Assert.AreEqual(comp[0], comp[1]);
            Assert.AreEqual(comp[1], comp[2]);
            Assert.AreNotEqual(comp[0], comp[3]);
        }

        [Test]
        public void Hierarchical_ManyDisconnectedPairs_KeepDirectionAndDistinctPositions()
        {
            var input = Input(4000);
            for (int i = 0; i < 4000; i += 2) input.AddEdge("n" + i, "n" + (i + 1), EdgeKind.ParentChild);
            var positions = new HierarchicalLayout().Compute(input, CancellationToken.None).Positions;
            var distinct = new HashSet<(float, float)>();
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.IsTrue(distinct.Add((positions[i].X, positions[i].Y)));
                if (i % 2 == 0) Assert.Less(positions[i].Y, positions[i + 1].Y);
            }
        }

        [Test]
        public void Radial_DeepChain_ProducesFinitePositionsWithoutRecursion()
        {
            var input = Input(20000);
            for (int i = 1; i < 20000; i++) input.AddEdge("n" + (i - 1), "n" + i, EdgeKind.ParentChild);
            var positions = new RadialLayout().Compute(input, CancellationToken.None).Positions;
            Assert.AreEqual(20000, positions.Length);
            foreach (var p in positions)
                Assert.IsFalse(float.IsNaN(p.X) || float.IsInfinity(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.Y));
            Assert.Greater(positions[19999].Length, positions[1].Length);
        }

        [Test]
        public void ForceDirected_GridPath_IsDeterministicAndRespectsPins()
        {
            var input = Input(800);
            input.Pinned[0] = new Vec2(42, -17);
            var layout = new ForceDirectedLayout { Iterations = 12 };
            var first = layout.Compute(input, CancellationToken.None).Positions;
            var second = layout.Compute(input, CancellationToken.None).Positions;
            Assert.AreEqual(42, first[0].X);
            Assert.AreEqual(-17, first[0].Y);
            for (int i = 0; i < first.Length; i++)
            {
                Assert.AreEqual(first[i].X, second[i].X);
                Assert.AreEqual(first[i].Y, second[i].Y);
                Assert.IsFalse(float.IsNaN(first[i].X) || float.IsInfinity(first[i].X));
            }
        }

        [Test]
        public void Cancellation_IsObserved()
        {
            var input = Input(200, Enumerable.Range(0, 199).Select(i => (i, i + 1)).ToArray());
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => new ForceDirectedLayout().Compute(input, cts.Token));
        }
    }

    public class CacheAndExportTests
    {
        static GraphStore Sample()
        {
            var store = new GraphStore();
            var r = new AnalysisResult("p");
            r.AddNode(new GraphNode { Id = "so1:abc:1:0", Kind = NodeKind.GameObject, Name = "Door <\"&>", SceneGuid = "abc", HierarchyPath = "World/Door" });
            r.AddNode(new GraphNode { Id = "asset1:def", Kind = NodeKind.PrefabAsset, Name = "DoorPrefab", AssetGuid = "def", AssetPath = "Assets/Door.prefab" });
            r.AddEdge(new GraphEdge { SourceId = "so1:abc:1:0", TargetId = "asset1:def", Kind = EdgeKind.PrefabSource, Evidence = EvidenceLevel.Confirmed, SourceFile = "Assets/Scene.unity", SourceLine = 42, Explanation = "why", Qualifiers = "outermost" });
            r.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingReference, "missing", "so1:abc:1:0");
            r.DependencyKeys.Add("def");
            store.ReplaceOwner("p|scene:abc", r, new OwnerMetadata { ProviderId = "p", ProviderVersion = 3, TargetKey = "scene:abc", DependencyHash = "h", Persistent = true });
            return store;
        }

        [Test]
        public void Cache_RoundTrip()
        {
            var store = Sample();
            var header = new GraphCacheHeader { SchemaVersion = NodeIds.SchemaVersion };
            header.Values["unityVersion"] = "2022.3.22f1";
            var ms = new MemoryStream();
            GraphCacheSerializer.Write(ms, header, store, null);
            ms.Position = 0;
            var owners = GraphCacheSerializer.Read(ms, out var readHeader);
            Assert.AreEqual("2022.3.22f1", readHeader.Get("unityVersion"));
            Assert.AreEqual(1, owners.Count);
            Assert.AreEqual(3, owners[0].Metadata.ProviderVersion);
            Assert.AreEqual("h", owners[0].Metadata.DependencyHash);

            var restored = new GraphStore();
            restored.ReplaceOwner(owners[0].Key, owners[0].Result, owners[0].Metadata);
            Assert.AreEqual(store.NodeCount, restored.NodeCount);
            Assert.AreEqual(store.EdgeCount, restored.EdgeCount);
            var e = restored.Edges.Single();
            Assert.AreEqual(42, e.SourceLine);
            Assert.AreEqual("outermost", e.Qualifiers);
            Assert.AreEqual(1, restored.AllDiagnostics().Count());
            CollectionAssert.Contains(restored.GetOwnersDependingOn("def").ToList(), "p|scene:abc");
        }

        [Test]
        public void Cache_CorruptionIsDetected()
        {
            var store = Sample();
            var ms = new MemoryStream();
            GraphCacheSerializer.Write(ms, new GraphCacheHeader { SchemaVersion = 1 }, store, null);
            var bytes = ms.ToArray();
            var truncated = new MemoryStream(bytes, 0, bytes.Length / 2);
            Assert.Throws<GraphCacheFormatException>(() => GraphCacheSerializer.Read(truncated, out _));
            var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Throws<GraphCacheFormatException>(() => GraphCacheSerializer.Read(garbage, out _));
        }

        [Test]
        public void Cache_FilterSkipsTransientOwners()
        {
            var store = Sample();
            store.ReplaceOwner("p|scene:h1", G.Result(("ses1:1", "ses1:2", EdgeKind.ParentChild)), new OwnerMetadata { Persistent = false });
            var ms = new MemoryStream();
            GraphCacheSerializer.Write(ms, new GraphCacheHeader { SchemaVersion = 1 }, store, o => o.Metadata.Persistent);
            ms.Position = 0;
            Assert.AreEqual(1, GraphCacheSerializer.Read(ms, out _).Count);
        }

        [Test]
        public void Json_ContainsEvidenceAndProvenance_AndRespectsPrivacyOptions()
        {
            var store = Sample();
            var set = ExportSet.FromStore(store);
            string json = GraphJsonExporter.Export(set, new ExportOptions());
            StringAssert.Contains("\"schemaVersion\":1", json);
            StringAssert.Contains("\"evidence\":\"Confirmed\"", json);
            StringAssert.Contains("\"sourceFile\":\"Assets/Scene.unity\"", json);
            StringAssert.Contains("Door <\\\"&>", json);
            string privateJson = GraphJsonExporter.Export(set, new ExportOptions { IncludeAssetPaths = false, IncludeSourceLocations = false, IncludeHierarchyPaths = false });
            StringAssert.DoesNotContain("Assets/Door.prefab", privateJson);
            StringAssert.DoesNotContain("Assets/Scene.unity", privateJson);
            StringAssert.DoesNotContain("World/Door", privateJson);
        }

        [Test]
        public void GraphMl_IsWellFormed()
        {
            string xml = GraphMlExporter.Export(ExportSet.FromStore(Sample()), new ExportOptions());
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("g", "http://graphml.graphdrawing.org/xmlns");
            Assert.AreEqual(2, doc.SelectNodes("//g:node", ns).Count);
            Assert.AreEqual(1, doc.SelectNodes("//g:edge", ns).Count);
        }

        [Test]
        public void ExportSet_FromNodesKeepsOnlyInducedEdges()
        {
            var store = Sample();
            var set = ExportSet.FromNodes(store, new[] { "so1:abc:1:0" });
            Assert.AreEqual(1, set.Nodes.Count);
            Assert.AreEqual(0, set.Edges.Count);
            Assert.AreEqual(1, set.Diagnostics.Count);
        }
    }

    public class NodeIdTests
    {
        [Test]
        public void SceneObjectIds_RoundTrip()
        {
            string id = NodeIds.SceneObject("0123456789abcdef0123456789abcdef", -8123456789012345678, 4567);
            Assert.IsTrue(NodeIds.TryParseSceneObject(id, out string guid, out long obj, out long prefab));
            Assert.AreEqual("0123456789abcdef0123456789abcdef", guid);
            Assert.AreEqual(-8123456789012345678, obj);
            Assert.AreEqual(4567, prefab);
            Assert.AreEqual(guid, NodeIds.TryGetSceneGuid(id));
        }

        [Test]
        public void AssetObjectIds_RoundTrip()
        {
            string id = NodeIds.AssetObject("abc", 123);
            Assert.IsTrue(NodeIds.TryParseAssetObject(id, out string guid, out long local));
            Assert.AreEqual("abc", guid);
            Assert.AreEqual(123, local);
            Assert.AreEqual("abc", NodeIds.TryGetAssetGuid(NodeIds.Asset("abc")));
        }

        [Test]
        public void Qualifiers_MatchExactlyOrWithValue()
        {
            var e = new GraphEdge { Qualifiers = "prefabOverride;op=Activation" };
            Assert.IsTrue(e.HasQualifier("prefabOverride"));
            Assert.IsTrue(e.HasQualifier("op"));
            Assert.IsFalse(e.HasQualifier("prefab"));
            e.AddQualifier("mayEdge");
            e.AddQualifier("mayEdge");
            Assert.AreEqual("prefabOverride;op=Activation;mayEdge", e.Qualifiers);
        }
    }
}
