using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor
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

        public static string Describe(PathGroup g) => Describe(g.Type, g.Properties);

        public static string Describe(Type type, IList<string> properties)
        {
            var sb = new StringBuilder();
            sb.Append(type != null ? type.Name : "?").Append(": ");
            sb.Append(string.Join(", ", properties.Distinct().Take(8)));
            if (properties.Count > 8) sb.Append(", …");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Clip / Controller のバインディングのキャッシュ。AnimationUtility.GetCurveBindings は大きな Clip で重く、
    /// 同じ Controller を使う Animator ごとに読み直すと Scene の解析が極端に遅くなるため、内容が変わるまで再利用する。
    /// 変更の検出は Clip / Controller の dirty count と Asset の依存ハッシュで行う。
    /// </summary>
    internal static class AnimationBindingCache
    {
        /// <summary>Controller 内の全 Clip のバインディングを (path, type) で統合したもの。</summary>
        public sealed class UnionGroup
        {
            public string Path;
            public Type Type;
            public readonly List<string> Properties = new List<string>();
            public readonly List<string> ClipNames = new List<string>();
            public string FirstClipGuid;
            public string FirstClipPath;
            public bool IsObjectReference;
        }

        public sealed class ControllerBindings
        {
            public readonly List<UnionGroup> Groups = new List<UnionGroup>();
            public readonly List<string> ClipGuids = new List<string>();
            public string ControllerGuid;
            public int ClipCount;
        }

        sealed class Entry<T>
        {
            public string Version;
            public T Value;
        }

        static readonly Dictionary<int, Entry<List<AnimationBindingUtil.PathGroup>>> Clips = new Dictionary<int, Entry<List<AnimationBindingUtil.PathGroup>>>();
        static readonly Dictionary<int, Entry<ControllerBindings>> Controllers = new Dictionary<int, Entry<ControllerBindings>>();

        static string VersionOf(UnityEngine.Object o)
        {
            string path = AssetDatabase.GetAssetPath(o);
            var hash = string.IsNullOrEmpty(path) ? default : AssetDatabase.GetAssetDependencyHash(path);
            return EditorUtility.GetDirtyCount(o) + "/" + hash;
        }

        public static List<AnimationBindingUtil.PathGroup> GetClipGroups(AnimationClip clip)
        {
            string version = VersionOf(clip);
            int id = clip.GetInstanceID();
            if (Clips.TryGetValue(id, out var e) && e.Version == version) return e.Value;
            var groups = AnimationBindingUtil.GroupBindings(clip);
            Clips[id] = new Entry<List<AnimationBindingUtil.PathGroup>> { Version = version, Value = groups };
            return groups;
        }

        public static ControllerBindings GetControllerBindings(RuntimeAnimatorController controller)
        {
            var clips = controller.animationClips.Where(c => c != null).Distinct().ToList();
            var sb = new StringBuilder(VersionOf(controller));
            foreach (var c in clips) sb.Append('|').Append(c.GetInstanceID()).Append(':').Append(VersionOf(c));
            string version = sb.ToString();
            int id = controller.GetInstanceID();
            if (Controllers.TryGetValue(id, out var e) && e.Version == version) return e.Value;

            var result = new ControllerBindings { ClipCount = clips.Count };
            result.ControllerGuid = GuidOf(controller);
            var map = new Dictionary<string, UnionGroup>(StringComparer.Ordinal);
            foreach (var clip in clips)
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                string clipGuid = string.IsNullOrEmpty(clipPath) ? null : AssetDatabase.AssetPathToGUID(clipPath);
                if (!string.IsNullOrEmpty(clipGuid) && !result.ClipGuids.Contains(clipGuid)) result.ClipGuids.Add(clipGuid);
                foreach (var g in GetClipGroups(clip))
                {
                    string key = g.Path + "|" + (g.Type != null ? g.Type.FullName : "?");
                    if (!map.TryGetValue(key, out var u))
                    {
                        u = new UnionGroup { Path = g.Path, Type = g.Type, FirstClipGuid = clipGuid, FirstClipPath = clipPath };
                        map[key] = u;
                        result.Groups.Add(u);
                    }
                    u.IsObjectReference |= g.IsObjectReference;
                    u.ClipNames.Add(clip.name);
                    foreach (var p in g.Properties)
                        if (u.Properties.Count < 32 && !u.Properties.Contains(p)) u.Properties.Add(p);
                }
            }
            Controllers[id] = new Entry<ControllerBindings> { Version = version, Value = result };
            return result;
        }

        static string GuidOf(UnityEngine.Object o)
        {
            string path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        public static void Clear()
        {
            Clips.Clear();
            Controllers.Clear();
        }
    }

    /// <summary>
    /// AnimatorController → Clip、AnimationClip → バインディングパス（相対パス）。
    /// Clip 単独では対象 GameObject が確定しないため Path ノードを保持する。
    /// </summary>
    public sealed class AnimationAssetProvider : IAnalysisProvider, IIncrementalAnalysisProvider, IAssetDiscoveryProvider
    {
        public string Id => ProviderIds.Animation;
        public int Version => 2;
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
            foreach (var step in AnalyzeIncrementally(target, context, result)) { }
            return result;
        }

        public IEnumerable<int> AnalyzeIncrementally(AnalysisTarget target, AnalysisContext context, AnalysisResult result)
        {
            var main = AssetDatabase.LoadMainAssetAtPath(target.Path);
            if (main == null) yield break;
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
                int n = 0;
                foreach (var clip in controller.animationClips.Where(c => c != null).Distinct())
                {
                    if (++n % 32 == 0) yield return 0;
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
                yield break;
            }

            if (main is AnimationClip mainClip)
            {
                result.AddNode(new GraphNode
                {
                    Id = mainId, Kind = NodeKind.AnimationClip, Name = main.name, TypeName = "AnimationClip",
                    AssetGuid = target.Guid, AssetPath = target.Path, ProviderId = Id,
                });
                int n = 0;
                foreach (var g in AnimationBindingCache.GetClipGroups(mainClip))
                {
                    if (++n % 64 == 0) yield return 0;
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

                // オブジェクト参照カーブのキーフレーム値（Sprite/Material 差し替え等）。同じ参照先は 1 本のエッジにまとめる
                var placeholders = new Dictionary<int, string>();
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(mainClip))
                {
                    yield return 0;
                    var keys = AnimationUtility.GetObjectReferenceCurve(mainClip, b);
                    if (keys == null) continue;
                    var counts = new Dictionary<int, (UnityEngine.Object value, int count)>();
                    foreach (var k in keys)
                    {
                        if (k.value == null) continue;
                        int iid = k.value.GetInstanceID();
                        counts[iid] = counts.TryGetValue(iid, out var c) ? (c.value, c.count + 1) : (k.value, 1);
                    }
                    foreach (var kv in counts)
                    {
                        if (!placeholders.TryGetValue(kv.Key, out var vid))
                        {
                            vid = ObjectIdUtility.GetId(kv.Value.value);
                            placeholders[kv.Key] = vid;
                            if (vid != null && vid != mainId && !result.ContainsNode(vid)) result.AddNode(ObjectIdUtility.CreatePlaceholder(kv.Value.value, vid));
                        }
                        if (vid == null || vid == mainId) continue;
                        var e = result.AddEdge(new GraphEdge
                        {
                            SourceId = mainId, TargetId = vid, Kind = EdgeKind.SerializedReference, Evidence = EvidenceLevel.Confirmed,
                            PropertyPath = b.path + ":" + b.propertyName, ProvenanceAssetGuid = target.Guid,
                            MemberName = kv.Value.count > 1 ? $"キーフレーム {kv.Value.count} 件" : null,
                        });
                        e.AddQualifier("keyframe");
                        string assetGuid = NodeIds.TryGetAssetGuid(vid);
                        if (assetGuid != null) result.DependencyKeys.Add(assetGuid);
                    }
                }
            }
        }
    }

    /// <summary>
    /// ロード済み Scene / Prefab 内の Animator について、Controller の Clip のバインディングパスを
    /// Animator の階層で解決する。再生されることは保証しないため Inferred とする。
    /// エッジは「Animator → 対象」を (パス, 型) ごとに 1 本にまとめ、どの Clip が動かすかは説明に列挙する
    /// （Clip ごとに作ると、多数の Clip を持つ Controller でエッジ数が爆発するため）。
    /// </summary>
    public sealed class AnimatorBindingProvider : IAnalysisProvider, IIncrementalAnalysisProvider
    {
        public string Id => ProviderIds.AnimatorBinding;
        public int Version => 2;
        public int Order => 41;
        const int MaxBindingsPerAnimator = 4000;
        const int BindingsPerStep = 64;

        public bool CanAnalyze(AnalysisTarget target) => TargetFilters.IsHierarchyTarget(target);

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            foreach (var step in AnalyzeIncrementally(target, context, result)) { }
            return result;
        }

        public IEnumerable<int> AnalyzeIncrementally(AnalysisTarget target, AnalysisContext context, AnalysisResult result)
        {
            var table = context.GetTable(target);
            if (!table.IsValid) yield break;

            foreach (var c in table.Components)
            {
                if (!(c is Animator animator) || animator == null || animator.runtimeAnimatorController == null) continue;
                yield return 0;
                if (animator == null || animator.runtimeAnimatorController == null) continue;
                string animatorId = table.IdOf(animator);
                if (!result.ContainsNode(animatorId)) result.AddNode(table.CreateComponentNode(animator, Id));

                var bindings = AnimationBindingCache.GetControllerBindings(animator.runtimeAnimatorController);
                if (bindings.ControllerGuid != null) result.DependencyKeys.Add(bindings.ControllerGuid);
                foreach (var g in bindings.ClipGuids) result.DependencyKeys.Add(g);

                // 解決先をまとめて ID 計算してから出力する
                var resolved = new List<(AnimationBindingCache.UnionGroup group, UnityEngine.Object target)>();
                var root = animator.transform;
                int count = 0;
                foreach (var g in bindings.Groups)
                {
                    if (++count > MaxBindingsPerAnimator)
                    {
                        result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Truncated, $"Animator のバインディングが多いため打ち切りました: {table.PathOf(animator.gameObject)}", animatorId, target.Path);
                        break;
                    }
                    if (count % BindingsPerStep == 0)
                    {
                        yield return 0;
                        if (animator == null) yield break;
                    }
                    var t = string.IsNullOrEmpty(g.Path) ? root : root.Find(g.Path);
                    UnityEngine.Object targetObj = null;
                    if (t != null)
                    {
                        targetObj = t.gameObject;
                        if (g.Type != null && typeof(Component).IsAssignableFrom(g.Type) && g.Type != typeof(Transform))
                        {
                            var comp = t.GetComponent(g.Type);
                            if (comp != null) targetObj = comp;
                        }
                    }
                    resolved.Add((g, targetObj));
                }
                table.PrimeIds(resolved.Where(r => r.target != null).Select(r => r.target));

                foreach (var (g, targetObj) in resolved)
                {
                    string clips = DescribeClips(g, bindings.ClipCount);
                    string member = clips + " / " + AnimationBindingUtil.Describe(g.Type, g.Properties);
                    if (targetObj == null)
                    {
                        string pathNodeId = NodeIds.AnimationPath(string.IsNullOrEmpty(g.FirstClipGuid) ? "embedded" : g.FirstClipGuid, g.Path);
                        if (!result.ContainsNode(pathNodeId))
                            result.AddNode(new GraphNode { Id = pathNodeId, Kind = NodeKind.AnimationPath, Name = g.Path, TypeName = "Binding Path", AssetGuid = g.FirstClipGuid, AssetPath = g.FirstClipPath, ProviderId = Id });
                        result.AddEdge(new GraphEdge
                        {
                            SourceId = animatorId, TargetId = pathNodeId, Kind = EdgeKind.AnimationBinding, Evidence = EvidenceLevel.Unresolved,
                            PropertyPath = g.Path, MemberName = member,
                            ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = g.FirstClipGuid,
                            Explanation = "Animator の階層にバインディングパスが見つかりません",
                        });
                        continue;
                    }
                    string targetId = table.IdOf(targetObj);
                    if (targetId == null) continue;
                    if (!result.ContainsNode(targetId)) result.AddNode(table.CreateNodeFor(targetObj, Id));
                    result.AddEdge(new GraphEdge
                    {
                        SourceId = animatorId, TargetId = targetId, Kind = EdgeKind.AnimationBinding, Evidence = EvidenceLevel.Inferred,
                        PropertyPath = g.Path, MemberName = member,
                        ProvenanceSceneGuid = table.SceneGuid, ProvenanceAssetGuid = g.FirstClipGuid,
                        Explanation = "Clip のバインディングパスを Animator ルートからの相対パスで解決（再生されることは保証しない）",
                    });
                }
            }
        }

        static string DescribeClips(AnimationBindingCache.UnionGroup g, int clipCount)
        {
            var names = g.ClipNames.Distinct().ToList();
            string head = string.Join(", ", names.Take(3));
            if (names.Count > 3) head += $" ほか {names.Count - 3} Clip";
            return names.Count == clipCount && clipCount > 1 ? $"全 {clipCount} Clip" : head;
        }
    }
}
