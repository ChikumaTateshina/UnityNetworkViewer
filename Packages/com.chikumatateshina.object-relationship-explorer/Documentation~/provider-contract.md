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
- 参照先がターゲット外のときは `ObjectTable.CreateNodeFor` / `ObjectIdUtility.CreatePlaceholder` で Placeholder ノードを返す。
- 依存する Asset の GUID を `DependencyKeys` に入れると、その Asset の変更時に再解析される。
- 対象を特定できない参照は null エッジにせず、`UnresolvedReference` ノード + `EvidenceLevel.Unresolved` エッジ + 診断で表す。
- `Confirmed` を「実行される」の意味で使わない。静的推定は `Inferred`。
