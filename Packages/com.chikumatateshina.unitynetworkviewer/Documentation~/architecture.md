# Architecture

## データの流れ

1. **変更検知** `ChangeTracker` が `ObjectChangeEvents` / `Undo.undoRedoPerformed` / `EditorSceneManager` の Scene イベント /
   `AssetPostprocessor` を購読し、デバウンス（既定 300ms）後に対象を `AnalysisScheduler` へ投入する。
   ロード済み Scene の変更は「値の変更（オブジェクト単位）」「構造の変更（生成・削除・親子・Prefab 構造）」に分けて通知する。
   依存索引（`GraphStore.GetOwnersDependingOn`）から影響を受ける解析単位も再投入する。
   `Selection.selectionChanged` は再解析に使わない（ハイライト専用）。
2. **解析** `AnalysisScheduler` が `EditorApplication.update` 上でフレーム予算（既定 12ms）内に処理する。
   1 ステップは「1 解析単位 × 1 Provider」で、解析単位の途中でフレームをまたげる。
   解析単位は `AnalysisTarget`（ロード済み Scene / Scene のチャンク / 未ロード Scene / Asset）。
   結果は owner（`providerId|targetKey`）単位で `GraphStore.ReplaceOwner` する。Provider の例外は診断として記録し、他 Provider を止めない。
   準備に時間のかかる Provider（`IPreparedAnalysisProvider`）は、準備が終わるまで待ち列に置き、その間に他の解析単位を進める。
3. **索引** `GraphStore` はノードの提供元（owner）ごとの寄与を保持し、実体ノードを Placeholder より優先して統合する。
   他 owner のエッジから参照されているノードは、提供元が消えても Placeholder として残る。
4. **クエリ** `GraphQueryService` が BFS・共通部分・最短経路・検索を提供する。選択のたびに再スキャンしない。
5. **表示** `GraphViewModel` が表示集合（Visible）とハイライト（Highlight）を管理し、レイアウトをワーカースレッドで計算する。
   `GraphCanvas` は Painter2D で一括描画し、ラベルは可視範囲内のノードのみプールした Label で表示する。

## ロード済み Scene のチャンク解析

大きな Scene を 1 フレームで解析するとエディタが止まるため、ロード済み Scene は GameObject 階層を「チャンク」に分けて解析する。

- **分割**: 階層を前順に並べ、上限（`chunkSize`、既定 64 GameObject）を超える部分木のルートは単独チャンクにして子へ進み、
  上限以下の兄弟の部分木は上限まで 1 チャンクにまとめる。チャンクのキーは `scene:{guid}#c{instanceId}`（組）/ `#s{instanceId}`（単独）。
- **コンテナ処理**: Scene の解析単位（`scene:{guid}`）は Scene ノードを出し、分割を作り直して前回と比較する。
  メンバー（instanceId の集合のハッシュ）が変わったチャンクだけを投入し、消えたチャンクの結果を取り除く。
- **値の変更**: 変更されたオブジェクトを含むチャンクだけを再解析する。単独チャンクの GameObject の名前が変わった場合は子孫のチャンクも再解析する（階層パス）。
- **削除の伝播**: チャンクの再解析で実体のあったノードが消えた場合、そのノードを参照している他のチャンクを再解析し、参照を Missing にする。
- **ID キャッシュ**: Scene ごとに instanceId → ノード ID をキャッシュし、チャンクをまたぐ参照先の ID はまとめて（GlobalObjectId のバッチ API）計算する。
  Prefab の Unpack・Scene 間の移動では該当する部分木だけをキャッシュから除く。
- **新規オブジェクト**: 作成直後のオブジェクトに GlobalObjectId を求めると Unity が Scene 全体のファイル ID を割り当て直す（大きな Scene では数百 ms 以上）。
  保存されるまでは一時 ID（`ses1:`）で扱い（`NewObjectRegistry`）、保存後に一時 ID を含むチャンクを再解析する。
- ロード済み Scene の結果はキャッシュに保存しない（Scene を開くたびにチャンク単位で解析し直す）。

## 重い処理の分散

| 処理 | 実行場所 |
|---|---|
| 未ロード Scene の読み込み・構文解析 | ワーカースレッド（参照する Prefab の構造構築はフレームごとに 1 件ずつ）|
| キャッシュの読み込み・復号 | ワーカースレッド（妥当性検証とストアへの反映はフレーム予算内で少しずつ）|
| キャッシュの書き出し | ワーカースレッド（永続対象が変わったときのみ。メインスレッドは参照の複製だけ）|
| プロジェクト内 Asset の探索 | 候補の判定をフレーム予算内で少しずつ |
| レイアウト計算 | ワーカースレッド |

## 表示の更新

- `GraphViewModel.Changed` は変化の種類（`ViewChange`: 位置・選択・表示集合・索引）を通知し、受け手は必要なものだけ更新する。
- 索引の更新は、表示中・ハイライト中のノードに関係する場合だけ再計算・再描画する（バックグラウンド索引中は 1 秒ごとに判定）。
- `GraphCanvas` は描画内容を「生成時のパン/ズーム」で作り、パン中は層の移動（`style.translate` / `scale`）だけで追従する。
  内容・位置が変わったとき、生成範囲（画面の周囲 50%）を外れたとき、ズーム操作が止まったときだけ作り直す。
- 詳細パネルの関係一覧は方向ごとに 300 件、診断一覧は 5,000 件までを表示する。検索は入力が止まってから実行する。

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

Unity API はメインスレッドのみ。ワーカースレッドで行うのは Unity API を使わない処理（レイアウト計算・Unity YAML の構文解析・キャッシュの読み書き）だけで、
入力は Unity Object を含まないデータ。
