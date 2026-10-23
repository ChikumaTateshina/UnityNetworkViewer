using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityNetworkViewer.Editor
{
    internal static class TargetFilters
    {
        public static bool IsHierarchyTarget(AnalysisTarget t)
            => t.Kind == AnalysisTargetKind.LoadedScene ||
               (t.Kind == AnalysisTargetKind.Asset && HasExtension(t.Path, ".prefab"));

        public static bool HasExtension(string path, string ext)
            => path != null && path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scene → Root、親子、GameObject → Component、Component → Script、Missing Script。</summary>
    public sealed class SceneHierarchyProvider : IAnalysisProvider, IAssetDiscoveryProvider
    {
        public string Id => ProviderIds.Hierarchy;
        public int Version => 1;
        public int Order => 0;
        public IEnumerable<string> AssetSearchFilters => new[] { "t:Prefab" };

        public bool CanAnalyze(AnalysisTarget target) => TargetFilters.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid)
            {
                result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.Unsupported, table.InvalidReason, null, target.Path);
                return result;
            }

            if (!table.IsPrefabAsset)
            {
                var scene = target.GetScene();
                var sceneNode = new GraphNode
                {
                    Id = table.ContainerId,
                    Kind = NodeKind.Scene,
                    Name = string.IsNullOrEmpty(scene.name) ? "(Untitled)" : scene.name,
                    TypeName = "Scene",
                    SceneGuid = table.SceneGuid,
                    AssetGuid = table.SceneGuid,
                    AssetPath = scene.path,
                    ProviderId = Id,
                };
                if (table.SceneGuid == null) sceneNode.Flags |= NodeFlags.Transient;
                result.AddNode(sceneNode);
            }

            for (int i = 0; i < table.GameObjects.Count; i++)
            {
                var go = table.GameObjects[i];
                var node = result.AddNode(table.CreateGameObjectNode(go, Id));
                var parent = go.transform.parent;
                if (parent == null)
                {
                    if (!table.IsPrefabAsset)
                        result.AddEdge(table.ContainerId, node.Id, EdgeKind.ParentChild, EvidenceLevel.Confirmed, "root");
                }
                else
                {
                    result.AddEdge(table.IdOf(parent.gameObject), node.Id, EdgeKind.ParentChild, EvidenceLevel.Confirmed, "m_Children");
                }

                var comps = table.ComponentsOf[i];
                for (int k = 0; k < comps.Length; k++)
                {
                    var c = comps[k];
                    if (c == null)
                    {
                        string missingId = table.MissingScriptId(go, k);
                        result.AddNode(new GraphNode
                        {
                            Id = missingId,
                            Kind = NodeKind.Component,
                            Name = "Missing Script",
                            TypeName = "MonoBehaviour (Missing)",
                            SceneGuid = table.SceneGuid,
                            AssetGuid = table.AssetGuid,
                            AssetPath = table.AssetPath,
                            HierarchyPath = table.PathOf(go) + "/Missing Script",
                            Flags = NodeFlags.MissingScript | NodeFlags.Missing,
                            ProviderId = Id,
                        });
                        result.AddEdge(node.Id, missingId, EdgeKind.OwnsComponent, EvidenceLevel.Confirmed, "m_Component.Array.data[" + k + "]");
                        node.Flags |= NodeFlags.MissingScript;
                        result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingScript,
                            $"Missing Script: {table.PathOf(go)} (index {k})", missingId, target.Path);
                        continue;
                    }

                    var cn = result.AddNode(table.CreateComponentNode(c, Id));
                    result.AddEdge(node.Id, cn.Id, EdgeKind.OwnsComponent, EvidenceLevel.Confirmed, "m_Component.Array.data[" + k + "]");

                    if (c is MonoBehaviour mb)
                    {
                        var script = MonoScript.FromMonoBehaviour(mb);
                        if (script != null)
                        {
                            string scriptId = ObjectIdUtility.GetAssetObjectId(script);
                            if (!result.ContainsNode(scriptId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(script, scriptId));
                            result.AddEdge(cn.Id, scriptId, EdgeKind.ScriptUsage, EvidenceLevel.Confirmed, "m_Script");
                            string scriptGuid = NodeIds.TryGetAssetGuid(scriptId);
                            if (scriptGuid != null) result.DependencyKeys.Add(scriptGuid);
                        }
                    }
                }
            }
            return result;
        }
    }

    /// <summary>Component の SerializedProperty から ObjectReference / UnityEvent PersistentCall を抽出する。</summary>
    public sealed class SerializedReferenceProvider : IAnalysisProvider
    {
        public string Id => ProviderIds.Serialized;
        public int Version => 1;
        public int Order => 10;

        public bool CanAnalyze(AnalysisTarget target) => TargetFilters.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid) return result;

            var refs = new List<SerializedReferenceInfo>();
            foreach (var c in table.Components)
            {
                if (c == null || SerializedReferenceScanner.ShouldSkipObject(c)) continue;
                refs.Clear();
                try
                {
                    SerializedReferenceScanner.Scan(c, context.Settings, refs, out bool truncated);
                    if (truncated)
                        result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Truncated,
                            $"プロパティ数が上限を超えたため走査を打ち切りました: {table.PathOf(c.gameObject)} {c.GetType().Name}",
                            table.IdOf(c), target.Path);
                }
                catch (Exception ex)
                {
                    result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.ProviderError,
                        $"{c.GetType().Name} の走査に失敗: {ex.Message}", table.IdOf(c), target.Path);
                    continue;
                }
                if (refs.Count == 0) continue;

                string sourceId = table.IdOf(c);
                if (!result.ContainsNode(sourceId)) result.AddNode(table.CreateComponentNode(c, Id));
                SerializedReferenceScanner.EmitEdges(result, table, c, sourceId, refs, Id);
            }

            foreach (var n in result.Nodes)
            {
                if (!string.IsNullOrEmpty(n.AssetGuid) && n.AssetGuid != table.AssetGuid && n.AssetGuid != table.SceneGuid)
                    result.DependencyKeys.Add(n.AssetGuid);
            }
            return result;
        }
    }

    /// <summary>
    /// Prefab Instance → Source、Prefab Variant → Base、Nested Prefab、Override の有無。
    /// Instance と Asset 側のオブジェクトは別ノードとして扱う。
    /// </summary>
    public sealed class PrefabProvider : IAnalysisProvider
    {
        public string Id => ProviderIds.Prefab;
        public int Version => 1;
        public int Order => 20;

        public bool CanAnalyze(AnalysisTarget target) => TargetFilters.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid) return result;

            if (table.IsPrefabAsset && table.PrefabRoot != null)
            {
                var root = table.PrefabRoot;
                if (PrefabUtility.GetPrefabAssetType(root) == PrefabAssetType.Variant)
                {
                    var baseRoot = PrefabUtility.GetCorrespondingObjectFromSource(root);
                    if (baseRoot != null)
                    {
                        string baseId = ObjectIdUtility.GetAssetObjectId(baseRoot);
                        if (!result.ContainsNode(baseId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(baseRoot, baseId));
                        if (!result.ContainsNode(table.ContainerId)) result.AddNode(table.CreateGameObjectNode(root, Id));
                        result.AddEdge(new GraphEdge
                        {
                            SourceId = table.ContainerId,
                            TargetId = baseId,
                            Kind = EdgeKind.PrefabVariant,
                            Evidence = EvidenceLevel.Confirmed,
                            ProvenanceAssetGuid = table.AssetGuid,
                            Explanation = "Prefab Variant の Base",
                        });
                        AddDependency(result, baseRoot);
                    }
                    else
                    {
                        result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingPrefab, "Variant の Base Prefab が見つかりません", table.ContainerId, target.Path);
                    }
                }
            }

            foreach (var go in table.GameObjects)
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                // Variant のルート自身は PrefabVariant で表現済み
                if (table.IsPrefabAsset && go == table.PrefabRoot) continue;

                string goId = table.IdOf(go);
                bool isInstanceRoot = PrefabUtility.IsAnyPrefabInstanceRoot(go);

                if (PrefabUtility.IsPrefabAssetMissing(go))
                {
                    if (isInstanceRoot)
                    {
                        string missingId = NodeIds.Unresolved(goId, "missing-prefab");
                        result.AddNode(new GraphNode { Id = missingId, Kind = NodeKind.UnresolvedReference, Name = "Missing Prefab", Flags = NodeFlags.Missing, ProviderId = Id });
                        if (!result.ContainsNode(goId)) result.AddNode(table.CreateGameObjectNode(go, Id));
                        result.AddEdge(new GraphEdge { SourceId = goId, TargetId = missingId, Kind = EdgeKind.PrefabSource, Evidence = EvidenceLevel.Unresolved, Explanation = "Prefab Asset が存在しません" });
                        result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingPrefab, $"Missing Prefab: {table.PathOf(go)}", goId, target.Path);
                    }
                    continue;
                }

                var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (source == null) continue;
                if (!result.ContainsNode(goId)) result.AddNode(table.CreateGameObjectNode(go, Id));
                string sourceId = ObjectIdUtility.GetAssetObjectId(source);
                if (!result.ContainsNode(sourceId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(source, sourceId));
                AddDependency(result, source);

                var edge = new GraphEdge
                {
                    SourceId = goId,
                    TargetId = sourceId,
                    Kind = EdgeKind.PrefabSource,
                    Evidence = EvidenceLevel.Confirmed,
                    ProvenanceSceneGuid = table.SceneGuid,
                    ProvenanceAssetGuid = table.AssetGuid,
                    Explanation = isInstanceRoot ? "Prefab Instance のルート → Source Prefab" : "Instance 内オブジェクト → Source Object",
                };
                if (isInstanceRoot)
                {
                    edge.AddQualifier(PrefabUtility.IsOutermostPrefabInstanceRoot(go) ? "outermost" : "nested");
                    AnnotateOverrides(go, edge, result, table);
                }
                result.AddEdge(edge);
            }
            return result;
        }

        static void AnnotateOverrides(GameObject instanceRoot, GraphEdge edge, AnalysisResult result, ObjectTable table)
        {
            try
            {
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(instanceRoot)) return;
                var mods = PrefabUtility.GetPropertyModifications(instanceRoot);
                int overrideCount = 0;
                if (mods != null)
                    foreach (var m in mods)
                        if (m != null && !PrefabUtility.IsDefaultOverride(m)) overrideCount++;
                var added = PrefabUtility.GetAddedGameObjects(instanceRoot);
                var addedComps = PrefabUtility.GetAddedComponents(instanceRoot);
                var removed = PrefabUtility.GetRemovedComponents(instanceRoot);
                if (overrideCount > 0) edge.AddQualifier("propertyOverrides=" + overrideCount);
                if (added.Count > 0) edge.AddQualifier("addedGameObjects=" + added.Count);
                if (addedComps.Count > 0) edge.AddQualifier("addedComponents=" + addedComps.Count);
                if (removed.Count > 0) edge.AddQualifier("removedComponents=" + removed.Count);
                if (overrideCount + added.Count + addedComps.Count + removed.Count > 0)
                    edge.Explanation += $"（Override: プロパティ {overrideCount} / 追加GO {added.Count} / 追加Comp {addedComps.Count} / 削除Comp {removed.Count}）";

                foreach (var a in added)
                {
                    if (a.instanceGameObject == null) continue;
                    var n = result.GetNode(table.IdOf(a.instanceGameObject));
                    if (n == null) result.AddNode(n = table.CreateGameObjectNode(a.instanceGameObject, ProviderIds.Prefab));
                    n.Flags |= NodeFlags.AddedByOverride;
                }
                foreach (var a in addedComps)
                {
                    if (a.instanceComponent == null) continue;
                    var n = result.GetNode(table.IdOf(a.instanceComponent));
                    if (n == null) result.AddNode(n = table.CreateComponentNode(a.instanceComponent, ProviderIds.Prefab));
                    n.Flags |= NodeFlags.AddedByOverride;
                }
            }
            catch (Exception ex)
            {
                result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.ProviderError, "Override 情報の取得に失敗: " + ex.Message, edge.SourceId);
            }
        }

        static void AddDependency(AnalysisResult result, Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid)) result.DependencyKeys.Add(guid);
        }
    }

    /// <summary>ScriptableObject アセット（.asset）内の参照。</summary>
    public sealed class ScriptableAssetProvider : IAnalysisProvider, IAssetDiscoveryProvider
    {
        public string Id => ProviderIds.ScriptableAsset;
        public int Version => 1;
        public int Order => 30;
        public IEnumerable<string> AssetSearchFilters => new[] { "t:ScriptableObject" };

        public bool CanAnalyze(AnalysisTarget target)
        {
            if (target.Kind != AnalysisTargetKind.Asset || !TargetFilters.HasExtension(target.Path, ".asset")) return false;
            var type = AssetDatabase.GetMainAssetTypeAtPath(target.Path);
            return type != null && typeof(ScriptableObject).IsAssignableFrom(type);
        }

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var main = AssetDatabase.LoadMainAssetAtPath(target.Path);
            if (main == null) return result;
            string mainId = NodeIds.Asset(target.Guid);
            result.AddNode(new GraphNode
            {
                Id = mainId,
                Kind = ObjectIdUtility.Classify(main),
                Name = main.name,
                TypeName = main.GetType().Name,
                AssetGuid = target.Guid,
                AssetPath = target.Path,
                ProviderId = Id,
                Flags = ObjectIdUtility.IsPackagePath(target.Path) ? NodeFlags.ExternalPackage : NodeFlags.None,
            });

            var refs = new List<SerializedReferenceInfo>();
            SerializedReferenceScanner.Scan(main, context.Settings, refs, out bool truncated);
            if (truncated) result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Truncated, "プロパティ数上限で走査を打ち切りました", mainId, target.Path);
            foreach (var r in refs)
            {
                if (r.IsMissing)
                {
                    string missingId = NodeIds.Unresolved(mainId, "missing:" + r.PropertyPath);
                    result.AddNode(new GraphNode { Id = missingId, Kind = NodeKind.UnresolvedReference, Name = "Missing", Flags = NodeFlags.Missing, ProviderId = Id });
                    result.AddEdge(new GraphEdge { SourceId = mainId, TargetId = missingId, Kind = EdgeKind.SerializedReference, Evidence = EvidenceLevel.Unresolved, PropertyPath = r.PropertyPath, Explanation = "Missing Reference" });
                    continue;
                }
                string targetId = ObjectIdUtility.GetId(r.Target);
                if (targetId == null || targetId == mainId) continue;
                if (!result.ContainsNode(targetId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(r.Target, targetId));
                var e = result.AddEdge(new GraphEdge
                {
                    SourceId = mainId,
                    TargetId = targetId,
                    Kind = r.IsUnityEventTarget ? EdgeKind.UnityEventCall : EdgeKind.SerializedReference,
                    Evidence = EvidenceLevel.Confirmed,
                    PropertyPath = r.PropertyPath,
                    MemberName = r.UnityEventMethod,
                    ProvenanceAssetGuid = target.Guid,
                });
                if (r.IsArrayElement) e.AddQualifier("arrayElement");
            }
            return result;
        }
    }
}
