using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ObjectRelationshipExplorer.Core;
using UnityEditor;
using UnityEngine;

namespace ObjectRelationshipExplorer.Editor
{
    internal static class AnimationBindingUtil
    {
        public struct PathGroup
        {
            public string Path;
            public Type Type;
            public List<string> Properties;
            public bool IsObjectReference;
        }

        /// <summary>Clip のバインディングを (path, type) 単位にまとめる。</summary>
        public static List<PathGroup> GroupBindings(AnimationClip clip)
        {
            var map = new Dictionary<string, PathGroup>(StringComparer.Ordinal);
            void Add(EditorCurveBinding b, bool objRef)
            {
                string key = b.path + "|" + (b.type != null ? b.type.FullName : "?");
                if (!map.TryGetValue(key, out var g))
                {
                    g = new PathGroup { Path = b.path ?? "", Type = b.type, Properties = new List<string>(), IsObjectReference = objRef };
                    map[key] = g;
                }
                if (g.Properties.Count < 32) g.Properties.Add(b.propertyName);
                if (objRef && !g.IsObjectReference)
                {
                    g.IsObjectReference = true;
                    map[key] = g;
                }
            }
            foreach (var b in AnimationUtility.GetCurveBindings(clip)) Add(b, false);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip)) Add(b, true);
            return map.Values.ToList();
        }

        public static string Describe(PathGroup g)
        {
            var sb = new StringBuilder();
            sb.Append(g.Type != null ? g.Type.Name : "?").Append(": ");
            sb.Append(string.Join(", ", g.Properties.Distinct().Take(8)));
            if (g.Properties.Count > 8) sb.Append(", …");
            return sb.ToString();
        }
    }

    /// <summary>
    /// AnimatorController → Clip、AnimationClip → バインディングパス（相対パス）。
    /// Clip 単独では対象 GameObject が確定しないため Path ノードを保持する。
    /// </summary>
    public sealed class AnimationAssetProvider : IAnalysisProvider, IAssetDiscoveryProvider
    {
        public string Id => ProviderIds.Animation;
        public int Version => 1;
        public int Order => 40;
        public IEnumerable<string> AssetSearchFilters => new[] { "t:AnimatorController", "t:AnimatorOverrideController", "t:AnimationClip" };

        public bool CanAnalyze(AnalysisTarget target)
        {
            if (target.Kind != AnalysisTargetKind.Asset) return false;
            return TargetFilters.HasExtension(target.Path, ".controller") ||
                   TargetFilters.HasExtension(target.Path, ".overrideController") ||
                   TargetFilters.HasExtension(target.Path, ".anim");
        }

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var main = AssetDatabase.LoadMainAssetAtPath(target.Path);
            if (main == null) return result;
            string mainId = NodeIds.Asset(target.Guid);

            if (main is RuntimeAnimatorController controller)
            {
                result.AddNode(new GraphNode
                {
                    Id = mainId, Kind = NodeKind.AnimatorController, Name = main.name, TypeName = main.GetType().Name,
                    AssetGuid = target.Guid, AssetPath = target.Path, ProviderId = Id,
                });
                if (controller is AnimatorOverrideController aoc && aoc.runtimeAnimatorController != null)
                {
                    string baseId = ObjectIdUtility.GetAssetObjectId(aoc.runtimeAnimatorController);
                    if (!result.ContainsNode(baseId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(aoc.runtimeAnimatorController, baseId));
                    result.AddEdge(mainId, baseId, EdgeKind.SerializedReference, EvidenceLevel.Confirmed, "m_Controller");
                }
                foreach (var clip in controller.animationClips.Where(c => c != null).Distinct())
                {
                    string clipId = ObjectIdUtility.GetAssetObjectId(clip);
                    if (!result.ContainsNode(clipId)) result.AddNode(ObjectIdUtility.CreatePlaceholder(clip, clipId));
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = mainId, TargetId = clipId, Kind = EdgeKind.AnimatorClip, Evidence = EvidenceLevel.Confirmed,
                        MemberName = clip.name, ProvenanceAssetGuid = target.Guid, Explanation = "Controller が使用する Clip（状態遷移で再生されるとは限らない）",
                    });
                    string clipGuid = NodeIds.TryGetAssetGuid(clipId);
                    if (clipGuid != null) result.DependencyKeys.Add(clipGuid);
                }
                return result;
            }

            if (main is AnimationClip mainClip)
            {
                result.AddNode(new GraphNode
                {
                    Id = mainId, Kind = NodeKind.AnimationClip, Name = main.name, TypeName = "AnimationClip",
                    AssetGuid = target.Guid, AssetPath = target.Path, ProviderId = Id,
                });
                foreach (var g in AnimationBindingUtil.GroupBindings(mainClip))
                {
                    string pathId = NodeIds.AnimationPath(target.Guid, g.Path);
                    if (!result.ContainsNode(pathId))
                        result.AddNode(new GraphNode
                        {
                            Id = pathId, Kind = NodeKind.AnimationPath,
                            Name = string.IsNullOrEmpty(g.Path) ? "(root)" : g.Path,
                            TypeName = "Binding Path", AssetGuid = target.Guid, AssetPath = target.Path, ProviderId = Id,
                        });
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = mainId, TargetId = pathId, Kind = EdgeKind.AnimationBinding, Evidence = EvidenceLevel.Confirmed,
                        PropertyPath = g.Path, MemberName = AnimationBindingUtil.Describe(g), ProvenanceAssetGuid = target.Guid,
                        Explanation = "Clip のバインディング（相対パス）。実際の対象は再生する Animator の階層で決まる",
                    });
                }
                // オブジェクト参照カーブのキーフレーム値（Sprite/Material 差し替え等）
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(mainClip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(mainClip, b);
                    if (keys == null) continue;
                    foreach (var k in keys)
                    {
                        if (k.value == null) continue;
                        string vid = ObjectIdUtility.GetId(k.value);
                        if (vid == null || vid == mainId) continue;
                        if (!result.ContainsNode(vid)) result.AddNode(ObjectIdUtility.CreatePlaceholder(k.value, vid));
                        var e = result.AddEdge(new GraphEdge
                        {
                            SourceId = mainId, TargetId = vid, Kind = EdgeKind.SerializedReference, Evidence = EvidenceLevel.Confirmed,
                            PropertyPath = b.path + ":" + b.propertyName, ProvenanceAssetGuid = target.Guid,
                        });
                        e.AddQualifier("keyframe");
                    }
                }
            }
            return result;
        }
    }

    /// <summary>
    /// ロード済み Scene / Prefab 内の Animator について、Controller の Clip のバインディングパスを
    /// Animator の階層で解決する。再生されることは保証しないため Inferred とする。
    /// </summary>
    public sealed class AnimatorBindingProvider : IAnalysisProvider
    {
        public string Id => ProviderIds.AnimatorBinding;
        public int Version => 1;
        public int Order => 41;
        const int MaxBindingsPerAnimator = 4000;

        public bool CanAnalyze(AnalysisTarget target) => TargetFilters.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            var table = context.GetTable(target);
            if (!table.IsValid) return result;

            foreach (var c in table.Components)
            {
                if (!(c is Animator animator) || animator.runtimeAnimatorController == null) continue;
                string animatorId = table.IdOf(animator);
                if (!result.ContainsNode(animatorId)) result.AddNode(table.CreateComponentNode(animator, Id));
                int count = 0;

                foreach (var clip in animator.runtimeAnimatorController.animationClips.Where(x => x != null).Distinct())
                {
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    string clipGuid = AssetDatabase.AssetPathToGUID(clipPath);
                    if (!string.IsNullOrEmpty(clipGuid)) result.DependencyKeys.Add(clipGuid);
                    string ctrlGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController));
                    if (!string.IsNullOrEmpty(ctrlGuid)) result.DependencyKeys.Add(ctrlGuid);

                    foreach (var g in AnimationBindingUtil.GroupBindings(clip))
                    {
                        if (++count > MaxBindingsPerAnimator)
                        {
                            result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Truncated, $"Animator のバインディングが多いため打ち切りました: {table.PathOf(animator.gameObject)}", animatorId, target.Path);
                            goto NextAnimator;
                        }
                        var t = string.IsNullOrEmpty(g.Path) ? animator.transform : animator.transform.Find(g.Path);
                        if (t == null)
                        {
                            string pathNodeId = NodeIds.AnimationPath(string.IsNullOrEmpty(clipGuid) ? "embedded" : clipGuid, g.Path);
                            if (!result.ContainsNode(pathNodeId))
                                result.AddNode(new GraphNode { Id = pathNodeId, Kind = NodeKind.AnimationPath, Name = g.Path, TypeName = "Binding Path", AssetGuid = clipGuid, AssetPath = clipPath, ProviderId = Id });
                            result.AddEdge(new GraphEdge
                            {
                                SourceId = animatorId, TargetId = pathNodeId, Kind = EdgeKind.AnimationBinding, Evidence = EvidenceLevel.Unresolved,
                                PropertyPath = g.Path, MemberName = clip.name + " / " + AnimationBindingUtil.Describe(g),
                                ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = clipGuid,
                                Explanation = "Animator の階層にバインディングパスが見つかりません",
                            });
                            continue;
                        }

                        UnityEngine.Object targetObj = t.gameObject;
                        if (g.Type != null && typeof(Component).IsAssignableFrom(g.Type) && g.Type != typeof(Transform))
                        {
                            var comp = t.GetComponent(g.Type);
                            if (comp != null) targetObj = comp;
                        }
                        string targetId = table.IdOf(targetObj);
                        if (targetId == null) continue;
                        if (!result.ContainsNode(targetId)) result.AddNode(table.CreateNodeFor(targetObj, Id));
                        result.AddEdge(new GraphEdge
                        {
                            SourceId = animatorId, TargetId = targetId, Kind = EdgeKind.AnimationBinding, Evidence = EvidenceLevel.Inferred,
                            PropertyPath = g.Path, MemberName = clip.name + " / " + AnimationBindingUtil.Describe(g),
                            ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = clipGuid,
                            Explanation = "Clip のバインディングパスを Animator ルートからの相対パスで解決（再生されることは保証しない）",
                        });
                    }
                }
            NextAnimator:;
            }
            return result;
        }
    }
}
