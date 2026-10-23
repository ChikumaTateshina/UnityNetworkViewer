# Architecture

## データの流れ

1. **変更検知** `ChangeTracker` が `ObjectChangeEvents` / `Undo.undoRedoPerformed` / `EditorSceneManager` の Scene イベント /
   `AssetPostprocessor` を購読し、デバウンス（既定 300ms）後に対象を `AnalysisScheduler` へ投入する。
   依存索引（`GraphStore.GetOwnersDependingOn`）から影響を受ける解析単位も再投入する。
   `Selection.selectionChanged` は再解析に使わない（ハイライト専用）。
2. **解析** `AnalysisScheduler` が `EditorApplication.update` 上でフレーム予算（既定 12ms）内に解析単位を処理する。
   解析単位は `AnalysisTarget`（ロード済み Scene / 未ロード Scene / Asset）。適用可能な全 Provider を実行し、
   結果を owner（`providerId|targetKey`）単位で `GraphStore.ReplaceOwner` する。Provider の例外は診断として記録し、他 Provider を止めない。
3. **索引** `GraphStore` はノードの提供元（owner）ごとの寄与を保持し、実体ノードを Placeholder より優先して統合する。
   他 owner のエッジから参照されているノードは、提供元が消えても Placeholder として残る。
4. **クエリ** `GraphQueryService` が BFS・共通部分・最短経路・検索を提供する。選択のたびに再スキャンしない。
5. **表示** `GraphViewModel` が表示集合（Visible）とハイライト（Highlight）を管理し、レイアウトをワーカースレッドで計算する。
   `GraphCanvas` は Painter2D で一括描画し、ラベルは可視範囲内のノードのみプールした Label で表示する。

## ノード ID

| 種類 | 形式 |
|---|---|
| Scene | `scene1:{guid}`（未保存 `scenes1:{handle}`）|
| 保存済み Scene 内オブジェクト | `so1:{sceneGuid}:{targetObjectId}:{targetPrefabId}`（GlobalObjectId と同値）|
| 未保存・Prefab Stage 等 | `ses1:{instanceId}`（セッション内のみ有効、キャッシュしない）|
| Asset（メイン）| `asset1:{guid}` |
| Asset 内サブオブジェクト | `ao1:{guid}:{localFileId}` |
| UdonSharp メソッド / フィールド | `method1:{scriptGuid}:{Class.Method}` / `field1:...` |
| Udon Graph イベント / 変数 | `uevt1:{programGuid}:{name}` / `uvar1:...` |
| アニメーションパス | `anim1:{clipGuid}:{path}` |
| 未解決 | `unres1:{hash}` |

表示名・パスは ID に使わない。Scene 移動等で GlobalObjectId が変わった場合は旧 ID の失効・新 ID の生成として扱う（owner の差し替え）。

## スレッド

Unity API はメインスレッドのみ。レイアウト計算のみ `Task.Run` で実行し、入力は Unity Object を含まないスナップショット。
