# Changelog

## [0.5.1] - 2026-10-25

### Fixed
- 拡大表示で推定・未解決（破線）のエッジを描くと、破線の区切りの計算が終わらずメモリを使い切り（OutOfMemoryException）、エディタが応答しなくなる不具合を修正。
  破線は描画範囲で切り取ってから作り、パターン内の位置を 1 周期の範囲に保って必ず先へ進むようにし、1 本あたりの区切りに上限を設けた。座標が NaN・無限大のエッジは描かない。
- ミニマップの描画中にスタイルを変更していたため発生していた InvalidOperationException（"VisualElements cannot be marked for dirty repaint ... during generateVisualContent"）を修正。

### Added
- 破線生成の回帰テスト（非常に長い線・大きな座標・NaN）。

## [0.5.0] - 2026-10-25

### Changed（Animation とズームの応答性）
- Animator の解析を分割実行に対応し、Clip・Controller のバインディングをキャッシュ（Clip / Controller の変更で作り直す）。
  同じ Controller を使う Animator ごとに全 Clip を読み直していたのを解消。
- Animator → 対象のエッジを Clip ごとではなく（パス, 型）ごとに 1 本にまとめ、動かす Clip を説明に列挙
  （Clip 20 個・バインディング 300 の Controller を使う Animator 200 個の合成 Scene で、エッジ 109 万 → 35 万本、
  Animator の変更時の再解析 210ms → 18ms、1 フレームの最大 310ms → 111ms〔各 Controller の初回のみ〕）。
- AnimationClip のオブジェクト参照キーフレームを参照先ごとに 1 本のエッジにまとめ、分割実行に対応。
- Animation ウィンドウ / Timeline のプレビュー中（AnimationMode）の値の変化では再解析しない。
- グラフのズーム: 操作中は描画を作り直さず変換で追従し、描画済みの範囲を外れたときだけ間隔を空けて作り直す。
  ノード 200 / エッジ 400 を超える表示では Painter2D を使わず頂点を直接生成し、ノードラベルは選択に近い 300 件までにした
  （5,000 ノード表示でホイール 1 ノッチあたりの再描画 平均 48.5ms → 0.6ms）。

### Added
- Animation 解析のテスト（バインディングの統合・Clip 変更時の更新・キーフレーム参照の集約）。

## [0.4.0] - 2026-10-24

### Changed（解析・検索・レイアウトの高速化）
- 階層レイアウトの連結成分ごとの全体走査と作業配列生成を除去。独立した親子ペア1万ノードの合成データでは、計算時間612ms → 13ms、累積割り当て3,067MB → 7.6MB（.NET 8 / Releaseの参考値）。
- 力学レイアウトのグリッドと作業配列を再利用し、反復中のGC負荷を削減。
- 放射レイアウトを非再帰化し、深い参照連鎖でのスタック不足を防止。
- 階層・SerializedReference・Prefab・未ロードSceneの主要Providerとオブジェクト表生成・ID取得を時間分割。結果は完了時だけowner単位で反映。
- プロジェクト探索のFindAssets呼び出しをフィルタごとにフレーム分散。
- 解析コンテキスト内でPrefab構造の検証結果を共有し、キャッシュ失効時に再検証。
- kind / scene / provider検索で二次索引を利用。
- 無関係な索引更新で表示キャッシュを再生成せず、削除・差し替え時は関係する表示を更新。
- 不要になったレイアウト計算をキャンセルし、古い結果の適用を抑制。
- 診断一覧の一時保持件数を制限し、全診断のソートを除去。

### Added
- 詳細パネルの参照元・参照先を300件単位で前後にページ切り替え。
- Provider向けの段階実行インターフェースと実装ガイド。
- 検索、途中再開、キャッシュ失効、表示更新、ページ切り替え、大規模レイアウトの回帰テスト。Unity 2022.3.22f1の一時プロジェクトで計64件成功（VRChat SDKなし）。

### Notes
- Unity内部の同期APIとowner単位のコミットは途中中断できません。Animation・VRChat系Providerは従来の同期解析を継続します。
- 実際の大規模ワールドにおけるフレーム時間は未測定です。


## [0.3.0] - 2026-10-24

### Changed（大規模プロジェクトでの応答性）
- ロード済み Scene を GameObject の部分木（チャンク、既定 64 個）に分けて解析し、フレームごとの時間予算内で進めるようにした。
  値の変更・Component の追加は変更されたオブジェクトのチャンクだけを、生成・削除・親子変更は分割の差分だけを再解析する
  （以前は変更のたびに Scene 全体を 1 フレームで再解析していた。3 万 GameObject の Scene で 4.6 秒 → 値の変更は数 ms）
- 削除されたオブジェクトを参照している他のチャンクを再解析し、Missing を検出する
- 作成直後（未保存）のオブジェクトは保存まで一時 ID で扱い、GlobalObjectId の割り当て（大きな Scene で数百 ms）を起こさない
- チャンクをまたぐ参照先の ID をまとめて計算し、Scene ごとにキャッシュする
- 未ロード Scene の読み込み・構文解析をワーカースレッドで行い、参照する Prefab の構造構築をフレームごとに分割
- キャッシュの読み込み・書き出しをワーカースレッドで行い、復元はフレームごとに分割。保存は永続対象が変わったときだけ行う
- プロジェクト内 Asset の探索をフレームごとに分割
- 解析済みの Scene / Asset を、索引に無いオブジェクトの選択で再解析しない
- GraphStore: 解析単位 → owner の索引を追加（O(n) の検索を解消）、ノード提供元の管理の割り当てを削減
- UI: パン操作で描画を作り直さない（層の移動のみ）、ズームは操作が止まってから描き直す、ミニマップの表示範囲枠を分離
- UI: 索引の更新は表示中・ハイライト中のノードに関係するときだけ再計算する。ドラッグ中に詳細・診断パネルを作り直さない
- UI: 詳細パネルの関係一覧（方向ごとに 300 件）と診断一覧（5,000 件）に上限を設定、検索は入力が止まってから実行
- 設定: 「チャンク (GameObject 数)」を追加

## [0.2.0] - 2026-10-24

### Changed
- 名称を UnityNetworkViewer に変更
  - パッケージ ID: `com.chikumatateshina.object-relationship-explorer` → `com.chikumatateshina.unitynetworkviewer`（旧パッケージは削除してから導入してください）
  - メニュー: `Window > Analysis > UnityNetworkViewer`、`Tools > UnityNetworkViewer`
  - 名前空間・アセンブリ: `ObjectRelationshipExplorer.*` → `UnityNetworkViewer.*`、スクリプト定義シンボル `UNV_VRCHAT_WORLDS`
  - 保存先: `Library/UnityNetworkViewer/`、`UserSettings/UnityNetworkViewer/`（旧キャッシュ・設定は引き継がれません）

## [0.1.1] - 2026-10-24

### Added
- MIT ライセンス（LICENSE.md）
- VCC / ALCOM 用の VPM listing と導入ページ（GitHub Pages）、Git URL での導入手順

## [0.1.0] - 2026-10-23

### Added
- Core: 型付き有向マルチグラフ（GraphStore: forward/reverse adjacency、種類/Scene/Asset/Provider 別索引、owner 単位の原子的差し替え）
- Core: BFS 部分グラフ（方向・深さ 0–5・関係/ノード/確度フィルタ・Scene 境界・表示上限と省略数）、和集合/共通部分、最短経路、トークン検索
- Core: レイアウト Hierarchical（SCC 縮約 + barycenter）/ Radial / Force-directed / Grouped（ワーカースレッドで計算）
- Core: 索引キャッシュ（スキーマ・Unity/SDK/Provider 版・依存ハッシュで owner ごとに検証）、JSON / GraphML エクスポート、Unity テキストシリアライズのパーサ
- Unity 解析: Hierarchy / Component / SerializedReference / UnityEvent PersistentCall / Prefab Instance・Variant・Nested・Override / ScriptableObject / AnimatorController・AnimationClip・Animator バインディング / 未ロード Scene（テキスト索引）
- 差分更新（ObjectChangeEvents / Undo / Scene イベント / AssetPostprocessor、デバウンス）と全体再解析（進捗・キャンセル）
- UI: 選択連動ハイライト、Graph → Unity 選択（ループ防止）、詳細・根拠パネル、診断、保存ビュー、ミニマップ、凡例、Scene View オーバーレイ、SVG エクスポート
- VRChat: UdonBehaviour（Program / Proxy / public variables / Proxy 値不一致診断）、Udon Graph 解析、インスタンス変数との結合（MethodControl / EventDispatch）
- UdonSharp: Roslyn SyntaxTree による構文ベースの静的解析（フィールド・メソッド・呼出し・操作分類・到達元・may-edge）
- Tests: Core 単体テスト、実 Scene/Prefab 統合テスト、VRChat 解析テスト。Fixture Generator サンプル
