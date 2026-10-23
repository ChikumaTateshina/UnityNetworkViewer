# Changelog

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
