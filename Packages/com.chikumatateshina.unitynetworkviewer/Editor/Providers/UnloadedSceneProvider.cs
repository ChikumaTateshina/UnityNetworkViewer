using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityNetworkViewer.Core;
using UnityNetworkViewer.Core.Yaml;
using UnityEditor;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// 未ロード Scene をテキストシリアライズから索引する。Scene を開かないため作業状態に影響しない。
    /// ノードIDはロード済み解析（GlobalObjectId）と一致する形式で生成し、Scene を開いた時点で
    /// ロード済み Provider の結果に置き換わる。
    /// 制約: バイナリ形式は依存情報のみ。Prefab Instance 内部の参照は Prefab Asset の値＋Override から推定する。
    /// 準備段階（<see cref="Prepare"/>）でファイルの読み込みと構文解析をワーカースレッドで行い、
    /// 参照される Prefab の構造をフレームごとに少しずつ構築してから、本解析を行う。
    /// </summary>
    public sealed class UnloadedSceneProvider : IAnalysisProvider, IAssetDiscoveryProvider, IPreparedAnalysisProvider, IIncrementalAnalysisProvider
    {
        sealed class Preparation
        {
            public string Path;
            public Task<UnityYamlDocument> Parse;
            public UnityYamlDocument Doc;
            public List<string> PrefabGuids;
            public int Next;
        }

        static readonly Dictionary<string, Preparation> Preparations = new Dictionary<string, Preparation>(StringComparer.Ordinal);

        public string Id => ProviderIds.UnloadedScene;
        public int Version => 2;
        public int Order => 0;
        public IEnumerable<string> AssetSearchFilters => new[] { "t:Scene" };

        static readonly Regex PersistentCallTarget = new Regex(@"^(.*m_PersistentCalls\.m_Calls\.Array\.data\[\d+\]\.)m_Target$", RegexOptions.Compiled);

        /// <summary>主要 Asset 種別のメインオブジェクト fileID。</summary>
        static readonly HashSet<long> KnownMainFileIds = new HashSet<long>
        {
            2100000,   // Material
            2800000,   // Texture2D
            4300000,   // Mesh (.asset)
            7400000,   // AnimationClip
            8300000,   // AudioClip
            9100000,   // AnimatorController
            11400000,  // ScriptableObject / MonoBehaviour asset
            11500000,  // MonoScript
            22100000,  // AnimatorOverrideController
            102900000, // SceneAsset
            12800000,  // Font
            4800000,   // Shader
            2000000,   // Cubemap/RenderTexture variants
            8400000,   // RenderTexture
            13400000,  // PhysicMaterial
            11700000,  // TextAsset
        };

        public bool CanAnalyze(AnalysisTarget target)
            => target.Kind == AnalysisTargetKind.UnloadedScene && UnvSettings.instance.unloadedSceneMode != UnloadedSceneMode.Disabled;

        public bool Prepare(AnalysisTarget target, AnalysisContext context)
        {
            if (!Preparations.TryGetValue(target.Key, out var prep) || prep.Path != target.Path)
            {
                string full = Path.GetFullPath(target.Path);
                // 構文解析は Unity API を使わないためワーカースレッドで行う
                prep = new Preparation { Path = target.Path, Parse = Task.Run(() => UnityYamlParser.ParseFile(full)) };
                Preparations[target.Key] = prep;
                return false;
            }
            if (prep.Doc == null)
            {
                if (!prep.Parse.IsCompleted) return false;
                if (prep.Parse.IsFaulted || prep.Parse.IsCanceled) return true; // Analyze でエラーを記録する
                prep.Doc = prep.Parse.Result;
                prep.PrefabGuids = CollectPrefabGuids(prep.Doc);
            }
            // 参照される Prefab の構造を少しずつ構築する（1 件ずつ Prefab を読み込むため）
            while (prep.Next < prep.PrefabGuids.Count)
            {
                if (context.OutOfTime) return false;
                context.GetPrefabStructure(prep.PrefabGuids[prep.Next++]);
            }
            return true;
        }

        static List<string> CollectPrefabGuids(UnityYamlDocument doc)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            if (doc.IsText)
            {
                foreach (var o in doc.Objects)
                {
                    foreach (var r in o.References)
                        if (!string.IsNullOrEmpty(r.Guid)) guids.Add(r.Guid);
                    foreach (var m in o.Modifications)
                    {
                        if (!string.IsNullOrEmpty(m.Target.Guid)) guids.Add(m.Target.Guid);
                        if (!string.IsNullOrEmpty(m.ObjectReference.Guid)) guids.Add(m.ObjectReference.Guid);
                    }
                }
            }
            var list = new List<string>();
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) list.Add(g);
            }
            return list;
        }

        static UnityYamlDocument TakeParsed(AnalysisTarget target)
        {
            if (!Preparations.TryGetValue(target.Key, out var prep)) return null;
            Preparations.Remove(target.Key);
            if (prep.Path != target.Path) return null;
            if (prep.Doc != null) return prep.Doc;
            if (prep.Parse.IsCompleted)
            {
                if (prep.Parse.IsFaulted) throw prep.Parse.Exception.GetBaseException();
                return prep.Parse.Result;
            }
            return null;
        }

        public AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context)
        {
            var result = new AnalysisResult(Id);
            foreach (var step in AnalyzeIncrementally(target, context, result)) { }
            return result;
        }

        public IEnumerable<int> AnalyzeIncrementally(AnalysisTarget target, AnalysisContext context, AnalysisResult result)
        {
            string sceneGuid = target.Guid;
            string sceneId = NodeIds.Scene(sceneGuid);
            result.AddNode(new GraphNode
            {
                Id = sceneId,
                Kind = NodeKind.Scene,
                Name = Path.GetFileNameWithoutExtension(target.Path),
                TypeName = "Scene (unloaded)",
                SceneGuid = sceneGuid,
                AssetGuid = sceneGuid,
                AssetPath = target.Path,
                Flags = NodeFlags.Unloaded | (ObjectIdUtility.IsPackagePath(target.Path) ? NodeFlags.ExternalPackage : NodeFlags.None),
                ProviderId = Id,
            });

            UnityYamlDocument doc = null;
            try
            {
                // 準備済みならその結果を使う（Prepare を経ない呼び出しでは同期的に読む）
                doc = TakeParsed(target) ?? UnityYamlParser.ParseFile(target.Path);
            }
            catch (Exception ex)
            {
                result.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.ProviderError, "Scene ファイルを読めません: " + ex.Message, sceneId, target.Path);
            }
            if (doc == null) yield break;

            if (!doc.IsText)
            {
                AddDependencyOnly(result, target, sceneId);
                yield break;
            }

            foreach (var step in new Builder(this, result, doc, target, sceneGuid, sceneId, context).Build()) yield return step;
        }

        static void AddDependencyOnly(AnalysisResult result, AnalysisTarget target, string sceneId)
        {
            result.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.BinarySerialization,
                "バイナリ形式の Scene のため、AssetDatabase の依存情報のみ索引しました（Component 単位の参照は不明）。", sceneId, target.Path);
            foreach (var dep in AssetDatabase.GetDependencies(target.Path, false))
            {
                if (dep == target.Path) continue;
                string guid = AssetDatabase.AssetPathToGUID(dep);
                if (string.IsNullOrEmpty(guid)) continue;
                string id = NodeIds.Asset(guid);
                if (!result.ContainsNode(id))
                    result.AddNode(new GraphNode
                    {
                        Id = id, Kind = KindFromPath(dep), Name = Path.GetFileNameWithoutExtension(dep), AssetGuid = guid, AssetPath = dep,
                        Flags = NodeFlags.Placeholder, ProviderId = ProviderIds.UnloadedScene,
                    });
                var e = result.AddEdge(new GraphEdge
                {
                    SourceId = sceneId, TargetId = id, Kind = EdgeKind.AssetDependency, Evidence = EvidenceLevel.Confirmed,
                    Explanation = "AssetDatabase.GetDependencies による依存（参照元 Component は不明）",
                });
                e.AddQualifier("dependencyOnly");
                result.DependencyKeys.Add(guid);
            }
        }

        internal static NodeKind KindFromPath(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".prefab": return NodeKind.PrefabAsset;
                case ".cs": return NodeKind.ScriptAsset;
                case ".anim": return NodeKind.AnimationClip;
                case ".controller":
                case ".overridecontroller": return NodeKind.AnimatorController;
                case ".unity": return NodeKind.Scene;
                default: return NodeKind.OtherAsset;
            }
        }

        sealed class Builder
        {
            readonly UnloadedSceneProvider _p;
            readonly AnalysisResult _r;
            readonly UnityYamlDocument _doc;
            readonly AnalysisTarget _target;
            readonly string _sceneGuid;
            readonly string _sceneId;
            readonly AnalysisContext _ctx;
            readonly Dictionary<string, string> _scriptNames = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly Dictionary<string, string> _parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly long _now = DateTime.UtcNow.Ticks;

            public Builder(UnloadedSceneProvider p, AnalysisResult r, UnityYamlDocument doc, AnalysisTarget target, string sceneGuid, string sceneId, AnalysisContext ctx)
            {
                _p = p;
                _r = r;
                _doc = doc;
                _target = target;
                _sceneGuid = sceneGuid;
                _sceneId = sceneId;
                _ctx = ctx;
            }

            string SO(long objectId, long prefabId) => NodeIds.SceneObject(_sceneGuid, objectId, prefabId);

            static bool IsTransform(UnityYamlObject o) => o.ClassId == 4 || o.ClassId == 224;

            /// <summary>Scene 内 fileID → ノードID（stripped は Prefab Instance 内オブジェクトのID）。</summary>
            string LocalId(long fileId)
            {
                var o = _doc.Get(fileId);
                if (o == null) return null;
                if (o.Stripped)
                {
                    long corr = o.TryGetReference("m_CorrespondingSourceObject", out var cr) ? cr.FileId : 0;
                    long pi = o.GetLocalReference("m_PrefabInstance");
                    return corr != 0 ? SO(corr, pi) : null;
                }
                return SO(fileId, 0);
            }

            /// <summary>Transform の fileID → 所有 GameObject のノードID。</summary>
            string GameObjectOfTransform(long transformFileId)
            {
                var t = _doc.Get(transformFileId);
                if (t == null) return null;
                if (!t.Stripped) return LocalId(t.GetLocalReference("m_GameObject"));
                if (!t.TryGetReference("m_CorrespondingSourceObject", out var cr)) return null;
                long pi = t.GetLocalReference("m_PrefabInstance");
                var structure = _ctx.GetPrefabStructure(cr.Guid);
                if (structure != null && structure.ByLocalId.TryGetValue(cr.FileId, out var entry))
                    return SO(entry.GameObjectLocalId, pi);
                return SO(cr.FileId, pi);
            }

            public IEnumerable<int> Build()
            {
                var prefabInstances = new List<UnityYamlObject>();
                foreach (var o in _doc.Objects)
                {
                    yield return 0;
                    if (o.Stripped) continue;
                    if (o.ClassId == 1001 || o.TypeName == "PrefabInstance") { prefabInstances.Add(o); continue; }
                    if (o.ClassId == 1) AddGameObject(o);
                }
                foreach (var o in _doc.Objects)
                {
                    if (o.Stripped || o.ClassId == 1 || o.ClassId == 1001) continue;
                    if (!o.TryGetReference("m_GameObject", out var goRef) || goRef.FileId == 0) continue;
                    AddComponent(o, goRef.FileId);
                }
                foreach (var pi in prefabInstances)
                {
                    yield return 0;
                    foreach (var step in AddPrefabInstance(pi)) yield return step;
                }
                foreach (var step in FillHierarchyPaths()) yield return step;
            }

            void AddGameObject(UnityYamlObject o)
            {
                var node = new GraphNode
                {
                    Id = SO(o.FileId, 0),
                    Kind = NodeKind.GameObject,
                    Name = o.GetScalar("m_Name") ?? "GameObject",
                    TypeName = "GameObject",
                    SceneGuid = _sceneGuid,
                    LocalFileId = o.FileId,
                    Flags = NodeFlags.Unloaded,
                    ProviderId = _p.Id,
                };
                if (o.GetScalar("m_IsActive") == "0") node.Flags |= NodeFlags.Inactive;
                if (o.GetScalar("m_TagString") == "EditorOnly") node.Flags |= NodeFlags.EditorOnly;
                _r.AddNode(node);
            }

            void AddComponent(UnityYamlObject o, long goFileId)
            {
                string goId = LocalId(goFileId);
                if (goId == null) return;
                string id = SO(o.FileId, 0);
                var node = new GraphNode
                {
                    Id = id,
                    Kind = NodeKind.Component,
                    Name = o.TypeName ?? ("Class " + o.ClassId),
                    TypeName = o.TypeName,
                    SceneGuid = _sceneGuid,
                    LocalFileId = o.FileId,
                    Flags = NodeFlags.Unloaded,
                    ProviderId = _p.Id,
                };
                if (o.GetScalar("m_Enabled") == "0") node.Flags |= NodeFlags.Inactive;

                if (o.TryGetReference("m_Script", out var script) && !string.IsNullOrEmpty(script.Guid))
                {
                    string scriptName = ScriptName(script.Guid);
                    if (scriptName == null)
                    {
                        node.Name = "Missing Script";
                        node.Flags |= NodeFlags.MissingScript | NodeFlags.Missing;
                        _r.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingScript, "Missing Script（未ロードScene）", id, _target.Path, null, o.Line);
                    }
                    else
                    {
                        node.Name = ProgramDisplayName(o) ?? scriptName;
                        string scriptId = NodeIds.Asset(script.Guid);
                        if (!_r.ContainsNode(scriptId))
                            _r.AddNode(new GraphNode { Id = scriptId, Kind = NodeKind.ScriptAsset, Name = scriptName, AssetGuid = script.Guid, AssetPath = AssetDatabase.GUIDToAssetPath(script.Guid), Flags = NodeFlags.Placeholder, ProviderId = _p.Id });
                        _r.AddEdge(new GraphEdge { SourceId = id, TargetId = scriptId, Kind = EdgeKind.ScriptUsage, Evidence = EvidenceLevel.Confirmed, PropertyPath = "m_Script", ProvenanceSceneGuid = _sceneGuid, SourceLine = o.Line });
                        _r.DependencyKeys.Add(script.Guid);
                    }
                }
                _r.AddNode(node);
                _r.AddEdge(new GraphEdge { SourceId = goId, TargetId = id, Kind = EdgeKind.OwnsComponent, Evidence = EvidenceLevel.Confirmed, ProvenanceSceneGuid = _sceneGuid });

                if (IsTransform(o))
                {
                    long father = o.GetLocalReference("m_Father");
                    string parentGo = father == 0 ? _sceneId : GameObjectOfTransform(father);
                    if (parentGo != null)
                    {
                        _r.AddEdge(new GraphEdge { SourceId = parentGo, TargetId = goId, Kind = EdgeKind.ParentChild, Evidence = EvidenceLevel.Confirmed, PropertyPath = father == 0 ? "root" : "m_Children", ProvenanceSceneGuid = _sceneGuid });
                        _parentOf[goId] = parentGo;
                    }
                    return;
                }

                foreach (var reference in o.References)
                {
                    if (reference.IsNull || reference.Path == "m_GameObject" || reference.Path == "m_Script") continue;
                    if (_ctx.Settings.IsExcludedProperty(reference.Path)) continue;
                    AddReference(id, reference, o, false);
                }
            }

            void AddReference(string sourceId, UnityYamlReference reference, UnityYamlObject owner, bool isOverride, string overrideMethod = null)
            {
                string targetId;
                if (reference.IsLocal)
                {
                    targetId = LocalId(reference.FileId);
                    if (targetId == null)
                    {
                        AddMissing(sourceId, reference, "Scene 内に参照先 fileID が存在しません");
                        return;
                    }
                    if (!_r.ContainsNode(targetId))
                        _r.AddNode(new GraphNode { Id = targetId, Kind = NodeKind.GameObject, Name = "(fileID " + reference.FileId + ")", SceneGuid = _sceneGuid, Flags = NodeFlags.Placeholder | NodeFlags.Unloaded, ProviderId = _p.Id });
                }
                else
                {
                    targetId = ExternalId(reference.Guid, reference.FileId, out bool missing);
                    if (missing)
                    {
                        AddMissing(sourceId, reference, "参照先 Asset (GUID " + reference.Guid + ") が見つかりません");
                        return;
                    }
                }

                var m = PersistentCallTarget.Match(reference.Path ?? "");
                string method = null;
                if (m.Success)
                {
                    method = overrideMethod;
                    if (method == null && owner != null) owner.PathScalars.TryGetValue(m.Groups[1].Value + "m_MethodName", out method);
                }
                var e = _r.AddEdge(new GraphEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = m.Success ? EdgeKind.UnityEventCall : EdgeKind.SerializedReference,
                    Evidence = EvidenceLevel.Confirmed,
                    PropertyPath = reference.Path,
                    MemberName = method,
                    SourceLine = reference.Line,
                    SourceFile = _target.Path,
                    ProvenanceSceneGuid = _sceneGuid,
                    AnalyzedAtUtcTicks = _now,
                    Explanation = "未ロード Scene のテキスト索引",
                });
                e.AddQualifier("textIndex");
                if (isOverride) e.AddQualifier("prefabOverride");
                if (reference.Path != null && reference.Path.EndsWith("]", StringComparison.Ordinal)) e.AddQualifier("arrayElement");
            }

            void AddMissing(string sourceId, UnityYamlReference reference, string reason)
            {
                string missingId = NodeIds.Unresolved(sourceId, "missing:" + reference.Path);
                _r.AddNode(new GraphNode { Id = missingId, Kind = NodeKind.UnresolvedReference, Name = "Missing", Flags = NodeFlags.Missing | NodeFlags.Unloaded, SceneGuid = _sceneGuid, ProviderId = _p.Id });
                var e = _r.AddEdge(new GraphEdge
                {
                    SourceId = sourceId, TargetId = missingId, Kind = EdgeKind.SerializedReference, Evidence = EvidenceLevel.Unresolved,
                    PropertyPath = reference.Path, SourceFile = _target.Path, SourceLine = reference.Line, ProvenanceSceneGuid = _sceneGuid, Explanation = reason,
                });
                e.AddQualifier("textIndex");
                _r.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingReference, "Missing Reference（未ロードScene）: " + reference.Path + " — " + reason, sourceId, _target.Path, reference.Path, reference.Line);
            }

            string ExternalId(string guid, long fileId, out bool missing)
            {
                missing = false;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                bool builtin = guid.StartsWith("0000000000000000", StringComparison.Ordinal);
                if (string.IsNullOrEmpty(path) && !builtin)
                {
                    missing = true;
                    return null;
                }

                bool isMain = KnownMainFileIds.Contains(fileId);
                PrefabStructure structure = null;
                if (!builtin && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    structure = _ctx.GetPrefabStructure(guid);
                    isMain = structure != null && structure.RootGameObjectLocalId == fileId;
                }

                string id = isMain && !builtin ? NodeIds.Asset(guid) : NodeIds.AssetObject(guid, fileId);
                if (!_r.ContainsNode(id))
                {
                    var node = new GraphNode
                    {
                        Id = id,
                        Kind = builtin ? NodeKind.OtherAsset : KindFromPath(path),
                        Name = builtin ? "Built-in Resource #" + fileId : Path.GetFileNameWithoutExtension(path),
                        AssetGuid = guid,
                        AssetPath = builtin ? null : path,
                        LocalFileId = fileId,
                        Flags = NodeFlags.Placeholder,
                        ProviderId = _p.Id,
                    };
                    if (!isMain && structure != null && structure.ByLocalId.TryGetValue(fileId, out var entry))
                    {
                        node.Kind = entry.IsGameObject ? NodeKind.GameObject : NodeKind.Component;
                        node.Name = entry.IsGameObject ? entry.Name : entry.Name;
                        node.HierarchyPath = entry.RelativePath;
                        node.Flags |= NodeFlags.PrefabAssetContent;
                    }
                    else if (!isMain && node.Kind == NodeKind.PrefabAsset)
                    {
                        node.Kind = NodeKind.GameObject;
                    }
                    if (ObjectIdUtility.IsPackagePath(path)) node.Flags |= NodeFlags.ExternalPackage;
                    _r.AddNode(node);
                }
                if (!builtin) _r.DependencyKeys.Add(guid);
                return id;
            }

            /// <summary>UdonBehaviour などプログラム Asset を持つ Component は、そのプログラム名で表示する。</summary>
            static string ProgramDisplayName(UnityYamlObject o)
            {
                if (!o.TryGetReference("programSource", out var program) || string.IsNullOrEmpty(program.Guid)) return null;
                string path = AssetDatabase.GUIDToAssetPath(program.Guid);
                return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
            }

            string ScriptName(string guid)
            {
                if (_scriptNames.TryGetValue(guid, out var n)) return n;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                n = string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
                _scriptNames[guid] = n;
                return n;
            }

            IEnumerable<int> AddPrefabInstance(UnityYamlObject pi)
            {
                long P = pi.FileId;
                if (!pi.TryGetReference("m_SourcePrefab", out var src) || string.IsNullOrEmpty(src.Guid))
                {
                    if (!pi.TryGetReference("m_ParentPrefab", out src) || string.IsNullOrEmpty(src.Guid)) yield break;
                }
                string sourceGuid = src.Guid;
                _r.DependencyKeys.Add(sourceGuid);

                long transformParent = pi.GetLocalReference("m_Modification.m_TransformParent");
                string parentId = transformParent == 0 ? _sceneId : GameObjectOfTransform(transformParent);

                var structure = _ctx.GetPrefabStructure(sourceGuid);
                if (structure == null)
                {
                    string missingId = NodeIds.Unresolved(SO(P, 0), "missing-prefab");
                    _r.AddNode(new GraphNode { Id = missingId, Kind = NodeKind.UnresolvedReference, Name = "Missing Prefab", Flags = NodeFlags.Missing | NodeFlags.Unloaded, SceneGuid = _sceneGuid, ProviderId = _p.Id });
                    if (parentId != null) _r.AddEdge(new GraphEdge { SourceId = parentId, TargetId = missingId, Kind = EdgeKind.ParentChild, Evidence = EvidenceLevel.Unresolved, ProvenanceSceneGuid = _sceneGuid });
                    _r.AddDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingPrefab, "Prefab Asset を読み込めません (GUID " + sourceGuid + ")", missingId, _target.Path, null, pi.Line);
                    yield break;
                }

                // 削除された Component / GameObject
                var removed = new HashSet<long>();
                foreach (var r in pi.References)
                    if (r.Path != null && (r.Path.StartsWith("m_Modification.m_RemovedComponents", StringComparison.Ordinal) ||
                                           r.Path.StartsWith("m_Modification.m_RemovedGameObjects", StringComparison.Ordinal)))
                        removed.Add(r.FileId);

                // Override の集計
                var names = new Dictionary<long, string>();
                var inactive = new Dictionary<long, bool>();
                var overriddenPaths = new HashSet<string>(StringComparer.Ordinal);
                var methodOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
                int overrideCount = 0;
                foreach (var m in pi.Modifications)
                {
                    yield return 0;
                    if (m.Target.Guid != sourceGuid) continue;
                    overrideCount++;
                    overriddenPaths.Add(m.Target.FileId + "|" + m.PropertyPath);
                    if (m.PropertyPath == "m_Name") names[m.Target.FileId] = m.Value;
                    else if (m.PropertyPath == "m_IsActive") inactive[m.Target.FileId] = m.Value == "0";
                    else if (m.PropertyPath != null && m.PropertyPath.EndsWith(".m_MethodName", StringComparison.Ordinal))
                        methodOverrides[m.Target.FileId + "|" + m.PropertyPath.Substring(0, m.PropertyPath.Length - "m_MethodName".Length)] = m.Value;
                }

                foreach (var entry in structure.Entries)
                {
                    yield return 0;
                    if (removed.Contains(entry.LocalId) || removed.Contains(entry.GameObjectLocalId)) continue;
                    string id = SO(entry.LocalId, P);
                    var node = new GraphNode
                    {
                        Id = id,
                        Kind = entry.IsGameObject ? NodeKind.GameObject : NodeKind.Component,
                        Name = entry.IsGameObject && names.TryGetValue(entry.LocalId, out var nm) ? nm : (entry.Name ?? entry.ScriptName),
                        TypeName = entry.TypeName,
                        SceneGuid = _sceneGuid,
                        LocalFileId = entry.LocalId,
                        Flags = NodeFlags.Unloaded | NodeFlags.PrefabInstance,
                        ProviderId = _p.Id,
                    };
                    if (entry.IsRoot) node.Flags |= NodeFlags.PrefabInstanceRoot;
                    bool isInactive = inactive.TryGetValue(entry.LocalId, out bool ia) ? ia : entry.Inactive;
                    if (entry.IsGameObject && isInactive) node.Flags |= NodeFlags.Inactive;
                    _r.AddNode(node);

                    if (entry.IsGameObject)
                    {
                        string parent = entry.IsRoot ? parentId : SO(entry.ParentLocalId, P);
                        if (parent != null)
                        {
                            _r.AddEdge(new GraphEdge { SourceId = parent, TargetId = id, Kind = EdgeKind.ParentChild, Evidence = EvidenceLevel.Confirmed, ProvenanceSceneGuid = _sceneGuid });
                            _parentOf[id] = parent;
                        }
                        string sourceObjId = entry.IsRoot ? NodeIds.Asset(sourceGuid) : NodeIds.AssetObject(sourceGuid, entry.LocalId);
                        if (!_r.ContainsNode(sourceObjId))
                            _r.AddNode(new GraphNode
                            {
                                Id = sourceObjId, Kind = entry.IsRoot ? NodeKind.PrefabAsset : NodeKind.GameObject,
                                Name = entry.IsRoot ? structure.RootName : entry.Name, AssetGuid = sourceGuid, AssetPath = structure.Path,
                                HierarchyPath = entry.RelativePath, LocalFileId = entry.LocalId, Flags = NodeFlags.Placeholder | NodeFlags.PrefabAssetContent, ProviderId = _p.Id,
                            });
                        var pe = _r.AddEdge(new GraphEdge
                        {
                            SourceId = id, TargetId = sourceObjId, Kind = EdgeKind.PrefabSource, Evidence = EvidenceLevel.Confirmed,
                            ProvenanceSceneGuid = _sceneGuid, SourceFile = _target.Path, SourceLine = pi.Line,
                            Explanation = entry.IsRoot ? $"Prefab Instance のルート → Source Prefab（Override {overrideCount} 件）" : "Instance 内オブジェクト → Source Object",
                        });
                        pe.AddQualifier("textIndex");
                        if (entry.IsRoot && overrideCount > 0) pe.AddQualifier("propertyOverrides=" + overrideCount);
                    }
                    else
                    {
                        _r.AddEdge(new GraphEdge { SourceId = SO(entry.GameObjectLocalId, P), TargetId = id, Kind = EdgeKind.OwnsComponent, Evidence = EvidenceLevel.Confirmed, ProvenanceSceneGuid = _sceneGuid });
                        if (!string.IsNullOrEmpty(entry.ScriptGuid))
                        {
                            string scriptId = NodeIds.Asset(entry.ScriptGuid);
                            if (!_r.ContainsNode(scriptId))
                                _r.AddNode(new GraphNode { Id = scriptId, Kind = NodeKind.ScriptAsset, Name = entry.ScriptName, AssetGuid = entry.ScriptGuid, AssetPath = AssetDatabase.GUIDToAssetPath(entry.ScriptGuid), Flags = NodeFlags.Placeholder, ProviderId = _p.Id });
                            _r.AddEdge(new GraphEdge { SourceId = id, TargetId = scriptId, Kind = EdgeKind.ScriptUsage, Evidence = EvidenceLevel.Confirmed, PropertyPath = "m_Script" });
                        }
                    }
                }

                // Prefab 側の参照（Override されていないもの）
                foreach (var ir in structure.References)
                {
                    yield return 0;
                    if (removed.Contains(ir.SourceLocalId)) continue;
                    if (overriddenPaths.Contains(ir.SourceLocalId + "|" + ir.PropertyPath)) continue;
                    string sourceId = SO(ir.SourceLocalId, P);
                    if (ir.Missing)
                    {
                        AddMissing(sourceId, new UnityYamlReference { Path = ir.PropertyPath, Line = pi.Line }, "Prefab 内の Missing Reference");
                        continue;
                    }
                    string targetId;
                    if (ir.TargetLocalId != 0)
                    {
                        if (removed.Contains(ir.TargetLocalId)) continue;
                        targetId = SO(ir.TargetLocalId, P);
                    }
                    else
                    {
                        targetId = ir.ExternalTargetId;
                        if (targetId == null) continue;
                        if (!_r.ContainsNode(targetId) && ir.ExternalPlaceholder != null) _r.AddNode(ir.ExternalPlaceholder.Clone());
                    }
                    var e = _r.AddEdge(new GraphEdge
                    {
                        SourceId = sourceId, TargetId = targetId,
                        Kind = ir.IsUnityEvent ? EdgeKind.UnityEventCall : EdgeKind.SerializedReference,
                        Evidence = EvidenceLevel.Confirmed, PropertyPath = ir.PropertyPath, MemberName = ir.Method,
                        ProvenanceSceneGuid = _sceneGuid, ProvenanceAssetGuid = sourceGuid,
                        Explanation = "Prefab Asset 側の値（Instance で Override されていない）",
                    });
                    e.AddQualifier("fromPrefabSource");
                    e.AddQualifier("textIndex");
                }

                // Override された参照
                foreach (var m in pi.Modifications)
                {
                    yield return 0;
                    if (m.ObjectReference.IsNull) continue;
                    if (m.Target.Guid != sourceGuid)
                    {
                        _r.AddDiagnostic(DiagnosticSeverity.Info, DiagnosticCodes.Unsupported, "入れ子 Prefab への Override 参照は未ロード索引で解決できません: " + m.PropertyPath, SO(P, 0), _target.Path, m.PropertyPath, m.Line);
                        continue;
                    }
                    if (removed.Contains(m.Target.FileId)) continue;
                    var reference = m.ObjectReference;
                    reference.Path = m.PropertyPath;
                    reference.Line = m.Line;
                    string method = null;
                    var pm = PersistentCallTarget.Match(m.PropertyPath ?? "");
                    if (pm.Success) methodOverrides.TryGetValue(m.Target.FileId + "|" + pm.Groups[1].Value, out method);
                    AddReference(SO(m.Target.FileId, P), reference, null, true, method);
                }
            }

            IEnumerable<int> FillHierarchyPaths()
            {
                var memo = new Dictionary<string, string>(StringComparer.Ordinal);
                string PathOf(string id, int guard)
                {
                    if (memo.TryGetValue(id, out var p)) return p;
                    var n = _r.GetNode(id);
                    if (n == null || n.Kind == NodeKind.Scene || guard > 256) return null;
                    string parent = _parentOf.TryGetValue(id, out var pid) ? PathOf(pid, guard + 1) : null;
                    p = parent == null ? n.Name : parent + "/" + n.Name;
                    memo[id] = p;
                    return p;
                }
                foreach (var n in _r.Nodes)
                {
                    yield return 0;
                    if (n.Kind == NodeKind.GameObject && n.SceneGuid == _sceneGuid) n.HierarchyPath = PathOf(n.Id, 0);
                }
                foreach (var e in _r.Edges)
                {
                    yield return 0;
                    if (e.Kind != EdgeKind.OwnsComponent) continue;
                    var go = _r.GetNode(e.SourceId);
                    var c = _r.GetNode(e.TargetId);
                    if (go != null && c != null && go.HierarchyPath != null) c.HierarchyPath = go.HierarchyPath + "/" + c.Name;
                }
            }
        }
    }
}
