# Provider Contract

```csharp
public interface IAnalysisProvider
{
    string Id { get; }          // 例: "unity.serialized"
    int Version { get; }        // 出力形式を変えたら上げる（キャッシュ無効化）
    int Order { get; }          // 同一ターゲット内の実行順
    bool CanAnalyze(AnalysisTarget target);
    AnalysisResult Analyze(AnalysisTarget target, AnalysisContext context);
}
```

- パラメータなしコンストラクタを持つ非抽象クラスは `TypeCache` により自動登録される。
- 全体再解析で Asset を発見させたい場合は `IAssetDiscoveryProvider.AssetSearchFilters`（`AssetDatabase.FindAssets` のフィルタ）を実装する。
- **GraphStore を直接変更しない。** 結果（Nodes / Edges / Diagnostics / DependencyKeys）を返す。Scheduler が owner 単位で原子的に差し替える。
- **読み取り専用。** `SerializedObject.ApplyModifiedProperties`、`EditorUtility.SetDirty`、Scene のロード/保存などを行わない。
- ロード済み Scene / Prefab の走査は `context.GetTable(target)`（`ObjectTable`）を共有して使う（ID はバッチで計算済み）。
- ロード済み Scene は `SceneChunk`（GameObject の部分木の組）単位で呼ばれる。対象を判定するときは `AnalysisTargetKind.LoadedScene` と `SceneChunk` の両方を受け付ける。
  表（`ObjectTable`）にはチャンク内のオブジェクトだけが入るため、Scene 全体を前提にした処理（Scene 内の全オブジェクトの列挙など）をしない。
  チャンク外の参照先の ID が多い場合は `ObjectTable.PrimeIds` でまとめて計算してから `IdOf` を使う。
- 参照先がターゲット外のときは `ObjectTable.CreateNodeFor` / `ObjectIdUtility.CreatePlaceholder` で Placeholder ノードを返す。
- 依存する Asset の GUID を `DependencyKeys` に入れると、その Asset の変更時に再解析される。
- ファイルの読み込みなど準備に時間がかかる場合は `IPreparedAnalysisProvider.Prepare` を実装する。Scheduler は true が返るまで毎フレーム呼ぶ。
  Unity API を使わない処理はワーカースレッドに出し、Unity API を使う処理は `context.OutOfTime` を見て少しずつ進める。
  `Prepare` を経ずに `Analyze` が呼ばれても（同期的に）動作すること。
- 対象を特定できない参照は null エッジにせず、`UnresolvedReference` ノード + `EvidenceLevel.Unresolved` エッジ + 診断で表す。
- `Confirmed` を「実行される」の意味で使わない。静的推定は `Inferred`。
