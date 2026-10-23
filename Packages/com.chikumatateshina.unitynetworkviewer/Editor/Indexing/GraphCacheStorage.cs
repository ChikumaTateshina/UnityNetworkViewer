using System;
using System.Collections.Generic;
using System.IO;
using UnityNetworkViewer.Core;
using UnityEditor;
using UnityEngine;

namespace UnityNetworkViewer.Editor
{
    /// <summary>
    /// Library/UnityNetworkViewer/ 以下の索引キャッシュ。リポジトリにはコミットされない。
    /// 版・依存ハッシュが一致する owner のみ復元し、それ以外は再解析対象として返す。破損時は削除して再構築する。
    /// </summary>
    public static class GraphCacheStorage
    {
        public const string Directory = "Library/UnityNetworkViewer";
        public static string FilePath => Path.Combine(Directory, "index.bin");

        public static GraphCacheHeader CreateHeader()
        {
            var header = new GraphCacheHeader { SchemaVersion = NodeIds.SchemaVersion };
            header.Values["unityVersion"] = Application.unityVersion;
            header.Values["sdkVersion"] = GetPackageVersion("com.vrchat.worlds") ?? "";
            header.Values["providers"] = ProviderRegistry.DescribeVersions();
            header.Values["savedAtUtc"] = DateTime.UtcNow.ToString("o");
            return header;
        }

        static string GetPackageVersion(string name)
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + name);
                return info?.version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool Save(GraphStore store)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    GraphCacheSerializer.Write(fs, CreateHeader(), store, o => o.Metadata != null && o.Metadata.Persistent);
                }
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityNetworkViewer] キャッシュを保存できません: " + ex.Message);
                return false;
            }
        }

        public sealed class LoadReport
        {
            public int Restored;
            public int Invalidated;
            public string Message;
            public readonly List<AnalysisTarget> StaleTargets = new List<AnalysisTarget>();
            public readonly HashSet<string> RestoredTargetKeys = new HashSet<string>(StringComparer.Ordinal);
        }

        public static LoadReport Load(GraphStore store)
        {
            var report = new LoadReport();
            if (!File.Exists(FilePath))
            {
                report.Message = "キャッシュなし";
                return report;
            }

            List<CachedOwner> owners;
            GraphCacheHeader header;
            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                {
                    owners = GraphCacheSerializer.Read(fs, out header);
                }
            }
            catch (Exception ex)
            {
                report.Message = "キャッシュが破損しているため削除しました: " + ex.Message;
                Delete();
                return report;
            }

            var current = CreateHeader();
            if (header.SchemaVersion != current.SchemaVersion ||
                header.Get("unityVersion") != current.Get("unityVersion") ||
                header.Get("sdkVersion") != current.Get("sdkVersion"))
            {
                report.Message = "Unity / SDK / スキーマの版が変わったためキャッシュを破棄しました";
                Delete();
                return report;
            }

            var staleKeys = new HashSet<string>(StringComparer.Ordinal);
            store.BeginBatch();
            try
            {
                foreach (var owner in owners)
                {
                    var m = owner.Metadata;
                    var provider = ProviderRegistry.Get(m.ProviderId);
                    var target = AnalysisTarget.FromKey(m.TargetKey);
                    bool valid = provider != null && provider.Version == m.ProviderVersion && target != null
                                 && target.Kind != AnalysisTargetKind.LoadedScene
                                 && ObjectIdUtility.GetDependencyHash(target.Path) == m.DependencyHash;
                    if (!valid)
                    {
                        report.Invalidated++;
                        if (target != null && staleKeys.Add(target.Key)) report.StaleTargets.Add(target);
                        continue;
                    }
                    store.ReplaceOwner(owner.Key, owner.Result, m);
                    report.Restored++;
                    report.RestoredTargetKeys.Add(m.TargetKey);
                }
            }
            finally
            {
                store.EndBatch();
            }
            report.Message = $"キャッシュから {report.Restored} 件を復元（無効 {report.Invalidated} 件）";
            return report;
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception)
            {
                // 次回保存時に上書きされる
            }
        }
    }
}
