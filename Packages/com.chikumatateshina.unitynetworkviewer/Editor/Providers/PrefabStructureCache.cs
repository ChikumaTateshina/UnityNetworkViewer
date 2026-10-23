using System;
using System.Collections.Generic;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor
{
    public sealed class PrefabEntry
    {
        public long LocalId;
        public bool IsGameObject;
        public string Name;
        public string TypeName;
        /// <summary>Component の場合は所有 GameObject、GameObject の場合は自身。</summary>
        public long GameObjectLocalId;
        /// <summary>GameObject の親 GameObject（ルートは 0）。</summary>
        public long ParentLocalId;
        public bool IsRoot;
        public bool Inactive;
        public string ScriptGuid;
        public string ScriptName;
        public string RelativePath;
    }

    public sealed class PrefabInternalRef
    {
        public long SourceLocalId;
        public string PropertyPath;
        /// <summary>同一 Prefab 内の参照先（0 の場合は外部）。</summary>
        public long TargetLocalId;
        public string ExternalTargetId;
        public GraphNode ExternalPlaceholder;
        public bool IsUnityEvent;
        public string Method;
        public bool Missing;
    }

    /// <summary>未ロード Scene 内の Prefab Instance を展開するための Prefab 構造（localId ベース）。</summary>
    public sealed class PrefabStructure
    {
        public string Guid;
        public string Path;
        public string DependencyHash;
        public long RootGameObjectLocalId;
        public string RootName;
        public readonly List<PrefabEntry> Entries = new List<PrefabEntry>();
        public readonly Dictionary<long, PrefabEntry> ByLocalId = new Dictionary<long, PrefabEntry>();
        public readonly List<PrefabInternalRef> References = new List<PrefabInternalRef>();
    }

    /// <summary>
    /// Prefab Asset の構造キャッシュ。AssetDatabase.LoadAssetAtPath で読み込むのみで Prefab Stage は開かない。
    /// 依存ハッシュが変わった場合に再構築する。
    /// </summary>
    public sealed class PrefabStructureCache
    {
        readonly Dictionary<string, PrefabStructure> _cache = new Dictionary<string, PrefabStructure>(StringComparer.Ordinal);

        public long Revision { get; private set; }

        public void Invalidate(string guid)
        {
            if (guid != null) _cache.Remove(guid);
            Revision++;
        }

        public void Clear()
        {
            _cache.Clear();
            Revision++;
        }

        public PrefabStructure Get(string guid, UnvSettings settings)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return null;
            string hash = ObjectIdUtility.GetDependencyHash(path);
            if (_cache.TryGetValue(guid, out var cached) && cached.DependencyHash == hash) return cached;

            var structure = Build(guid, path, settings);
            if (structure != null) structure.DependencyHash = hash;
            _cache[guid] = structure;
            return structure;
        }

        static PrefabStructure Build(string guid, string path, UnvSettings settings)
        {
            var table = ObjectTable.Create(AnalysisTarget.ForAsset(guid, path));
            if (!table.IsValid) return null;

            var s = new PrefabStructure { Guid = guid, Path = path, RootName = table.PrefabRoot.name };
            var localIdOf = new Dictionary<int, long>();

            long LocalId(UnityEngine.Object o)
            {
                if (o == null) return 0;
                if (localIdOf.TryGetValue(o.GetInstanceID(), out long id)) return id;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string g, out id) || g != guid) id = 0;
                localIdOf[o.GetInstanceID()] = id;
                return id;
            }

            for (int i = 0; i < table.GameObjects.Count; i++)
            {
                var go = table.GameObjects[i];
                long goId = LocalId(go);
                if (goId == 0) continue;
                var parent = go.transform.parent;
                var entry = new PrefabEntry
                {
                    LocalId = goId,
                    IsGameObject = true,
                    Name = go.name,
                    TypeName = "GameObject",
                    GameObjectLocalId = goId,
                    ParentLocalId = parent != null ? LocalId(parent.gameObject) : 0,
                    IsRoot = go == table.PrefabRoot,
                    Inactive = !go.activeSelf,
                    RelativePath = table.PathOf(go),
                };
                if (entry.IsRoot) s.RootGameObjectLocalId = goId;
                s.Entries.Add(entry);
                s.ByLocalId[goId] = entry;

                foreach (var c in table.ComponentsOf[i])
                {
                    if (c == null) continue;
                    long cid = LocalId(c);
                    if (cid == 0) continue;
                    var ce = new PrefabEntry
                    {
                        LocalId = cid,
                        IsGameObject = false,
                        Name = c.GetType().Name,
                        TypeName = c.GetType().FullName,
                        GameObjectLocalId = goId,
                        RelativePath = entry.RelativePath + "/" + c.GetType().Name,
                    };
                    if (c is MonoBehaviour mb)
                    {
                        var script = MonoScript.FromMonoBehaviour(mb);
                        if (script != null)
                        {
                            ce.ScriptGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
                            ce.ScriptName = script.name;
                        }
                    }
                    s.Entries.Add(ce);
                    s.ByLocalId[cid] = ce;
                }
            }

            var refs = new List<SerializedReferenceInfo>();
            foreach (var c in table.Components)
            {
                if (c == null || SerializedReferenceScanner.ShouldSkipObject(c)) continue;
                long sid = LocalId(c);
                if (sid == 0) continue;
                refs.Clear();
                try
                {
                    SerializedReferenceScanner.Scan(c, settings, refs, out _);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var r in refs)
                {
                    var ir = new PrefabInternalRef
                    {
                        SourceLocalId = sid,
                        PropertyPath = r.PropertyPath,
                        IsUnityEvent = r.IsUnityEventTarget,
                        Method = r.UnityEventMethod,
                        Missing = r.IsMissing,
                    };
                    if (!r.IsMissing)
                    {
                        if (table.Contains(r.Target)) ir.TargetLocalId = LocalId(r.Target);
                        if (ir.TargetLocalId == 0)
                        {
                            ir.ExternalTargetId = ObjectIdUtility.GetId(r.Target);
                            ir.ExternalPlaceholder = ObjectIdUtility.CreatePlaceholder(r.Target, ir.ExternalTargetId);
                        }
                    }
                    s.References.Add(ir);
                }
            }
            return s;
        }
    }
}
