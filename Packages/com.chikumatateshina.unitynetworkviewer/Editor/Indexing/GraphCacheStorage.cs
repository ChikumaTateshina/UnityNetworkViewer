using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

        static bool IsPersistent(OwnerRecord o) => o.Metadata != null && o.Metadata.Persistent;

        /// <summary>同期保存（終了時・Domain Reload 前）。</summary>
        public static bool Save(GraphStore store)
        {
            try
            {
                WriteFile(CreateHeader(), GraphCacheSerializer.CreateSnapshots(store, IsPersistent));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityNetworkViewer] キャッシュを保存できません: " + ex.Message);
                return false;
            }
        }

        static void WriteFile(GraphCacheHeader header, IReadOnlyList<OwnerSnapshot> owners)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                GraphCacheSerializer.Write(fs, header, owners);
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        /// <summary>ワーカースレッドでの保存。owner の参照の複製だけをメインスレッドで行う。</summary>
        public sealed class SaveJob
        {
            readonly Task _task;

            internal SaveJob(GraphCacheHeader header, List<OwnerSnapshot> owners)
            {
                _task = Task.Run(() => WriteFile(header, owners));
            }

            public bool IsCompleted => _task.IsCompleted;
            public Exception Error => _task.Exception?.GetBaseException();

            public void Wait()
            {
                try
                {
                    _task.Wait();
                }
                catch (Exception)
                {
                    // Error で参照する
                }
            }
        }

        public static SaveJob BeginSave(GraphStore store)
            => new SaveJob(CreateHeader(), GraphCacheSerializer.CreateSnapshots(store, IsPersistent));

        public sealed class LoadReport
        {
            public int Restored;
            public int Invalidated;
            public string Message;
            public readonly List<AnalysisTarget> StaleTargets = new List<AnalysisTarget>();
            public readonly HashSet<string> RestoredTargetKeys = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// キャッシュの復元。ファイルの読み込みと復号はワーカースレッドで行い、
        /// 妥当性の検証（AssetDatabase を使う）とストアへの反映は <see cref="Step"/> でフレームごとに少しずつ行う。
        /// </summary>
        public sealed class LoadJob
        {
            readonly Task<List<CachedOwner>> _task;
            GraphCacheHeader _header;
            List<CachedOwner> _owners;
            int _next;
            readonly HashSet<string> _staleKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly LoadReport Report = new LoadReport();

            internal LoadJob()
            {
                if (!File.Exists(FilePath))
                {
                    Report.Message = "キャッシュなし";
                    _owners = new List<CachedOwner>();
                    return;
                }
                string path = Path.GetFullPath(FilePath);
                _task = Task.Run(() =>
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                    {
                        var owners = GraphCacheSerializer.Read(fs, out var header);
                        _header = header;
                        return owners;
                    }
                });
            }

            /// <summary>処理を進める。完了したら true。</summary>
            public bool Step(GraphStore store, AnalysisScheduler scheduler, Func<bool> outOfTime)
            {
                if (_owners == null)
                {
                    if (!_task.IsCompleted) return false;
                    if (_task.IsFaulted || _task.IsCanceled)
                    {
                        Report.Message = "キャッシュが破損しているため削除しました: " + _task.Exception?.GetBaseException().Message;
                        Delete();
                        return true;
                    }
                    var current = CreateHeader();
                    if (_header.SchemaVersion != current.SchemaVersion ||
                        _header.Get("unityVersion") != current.Get("unityVersion") ||
                        _header.Get("sdkVersion") != current.Get("sdkVersion"))
                    {
                        Report.Message = "Unity / SDK / スキーマの版が変わったためキャッシュを破棄しました";
                        Delete();
                        return true;
                    }
                    _owners = _task.Result;
                }

                store.BeginBatch();
                try
                {
                    while (_next < _owners.Count)
                    {
                        if (outOfTime()) return false;
                        var owner = _owners[_next++];
                        // 復元中にすでに解析し直された owner は上書きしない
                        if (store.HasOwner(owner.Key)) continue;
                        var m = owner.Metadata;
                        var provider = ProviderRegistry.Get(m.ProviderId);
                        var target = AnalysisTarget.FromKey(m.TargetKey);
                        bool valid = provider != null && provider.Version == m.ProviderVersion && target != null
                                     && (target.Kind == AnalysisTargetKind.Asset || target.Kind == AnalysisTargetKind.UnloadedScene)
                                     && ObjectIdUtility.GetDependencyHash(target.Path) == m.DependencyHash;
                        if (!valid)
                        {
                            Report.Invalidated++;
                            if (target != null && _staleKeys.Add(target.Key)) Report.StaleTargets.Add(target);
                            continue;
                        }
                        store.ReplaceOwner(owner.Key, owner.Result, m);
                        Report.Restored++;
                        Report.RestoredTargetKeys.Add(m.TargetKey);
                    }
                }
                finally
                {
                    store.EndBatch();
                }
                if (Report.Message == null) Report.Message = $"キャッシュから {Report.Restored} 件を復元（無効 {Report.Invalidated} 件）";
                _owners.Clear();
                return true;
            }
        }

        public static LoadJob BeginLoad() => new LoadJob();

        /// <summary>同期的な復元（テスト用）。</summary>
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
                                 && (target.Kind == AnalysisTargetKind.Asset || target.Kind == AnalysisTargetKind.UnloadedScene)
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
