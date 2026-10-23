# Changelog

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
