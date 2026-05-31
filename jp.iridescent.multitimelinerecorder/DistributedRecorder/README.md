# DistributedRecorder – パッケージ README

セットアップ手順の詳細はプロジェクトルートの `README.md` を参照してください。

## 名前空間レイアウト

| 名前空間 | 場所 | 役割 |
|---------|------|------|
| `DistributedRecorder.Setup` | `Editor/Setup/` | セットアップ UI (SetupHubWindow), UDP Discovery, パスワードヘルパ, プロジェクト同期, パッケージインストール |
| `DistributedRecorder.Shared` | `Editor/Shared/` | プロトコル DTO, HMAC 認証, 入力検証, PasswordKeyDeriver, SharedKeyLoader, プロジェクトハッシュ, バージョンチェック |
| `DistributedRecorder.Master` | `Editor/Master/` | HTTP トランスポート, ジョブ投入, 進捗監視, 結果ダウンロード |
| `DistributedRecorder.Worker` | `Editor/Worker/` | HTTP リスナー, ジョブランナー, ジョブストア |
| `DistributedRecorder.UI` | `Editor/UI/` | DistributedRecorderWindow（ジョブ実行 EditorWindow） |
| `DistributedRecorder.Cli` | `Editor/CLI/` | `-executeMethod` エントリポイント引数パーサー |

## Setup Hub モジュール構成（Pivot v2）

```
Editor/Shared/
├── PasswordKeyDeriver.cs          – パスワード → PBKDF2-SHA256(100k) → 32 バイト HMAC 鍵
├── SharedKeyLoader.cs             – HMAC 鍵の解決 (EditorPrefs パスワード優先, 旧ファイル fallback)
└── ...

Editor/Setup/
├── SetupHubWindow.cs              – ダッシュボード EditorWindow（メニュー: DistributedRecorder > Setup Wizard）
│                                    パスワード入力・保存・ランダム生成, UDP Discovery, 同期
├── HealthCheck.cs                 – 各ヘルスチェック項目のロジックとステータス判定
├── KeyGenerator.cs                – ランダムパスワード生成ヘルパ (GenerateRandomPassword)
├── UdpDiscovery.cs                – UDP broadcast Worker discovery (port 11081, HMAC-signed)
├── ProjectSyncer.cs               – C# delta-sync（ファイルサイズ + 更新日時比較 + ProjectHasher 検証）
├── SyncRules.cs                   – 同期除外ルール定義
├── RecorderPackageInstaller.cs    – Client.Add("com.unity.recorder") ラッパ
└── WorkerRegistryAutoFactory.cs   – WorkerRegistryAsset の自動生成・検索
```

**削除済み (iter4 Pivot v2)**: `KeyTransferQr.cs`, `QrEncoder.cs`, `QrTextureBuilder.cs`,
`KeyTransferFile.cs`, `LanScanner.cs`

## セキュリティ設計

### 鍵導出（Pivot v2）

- パスワード（人が打てる 8〜32 文字）→ PBKDF2-SHA256（100,000 iter）→ 32 バイト HMAC 鍵
- Salt は固定値（コードに埋め込み）。同一パスワードは全 PC で同一 HMAC 鍵を生成する
  - 目的: Master と Worker が同じ鍵を独立して導出できるようにするため
- パスワードは EditorPrefs（Windows HKCU レジストリ、平文）に per-machine 保存
  - 同一 OS ユーザーの他プロセスから読める。イントラネット専用として許容

### UDP Discovery セキュリティ

- パスワードが一致しない Worker は Discovery に応答しない（HMAC 検証失敗 → サイレント破棄）
- タイムスタンプ ±60 秒 replay 防止
- 応答パケットの HMAC も検証（Master 側で二重確認）
- 応答の host フィールドは無視し、送信元 IP を採用（中間者ホスト偽装防止）

### HTTP HMAC 認証（既存、変更なし）

`GET /health` を除くすべてのリクエストに以下のヘッダーが必要です。

| ヘッダー | 値 |
|---------|---|
| `X-Timestamp` | Unix エポック秒（UTC） |
| `X-Nonce` | ランダム16進数文字列（16文字以上、リクエストごとに一意） |
| `X-Signature` | HMAC-SHA256(key, `"{METHOD}\n{PATH}\n{TIMESTAMP}\n{NONCE}\n{BODY_SHA256}"`) |

リプレイ攻撃対策: タイムスタンプ ±60 秒、Nonce は 24 時間キャッシュ。

## プロトコル

### ジョブリクエスト（POST /jobs）

```json
{
  "jobId":                     "valid-guid-string",
  "recorderSettingsAssetPath": "Assets/Recordings/MyRec.asset",
  "scenePath":                 "Assets/OutdoorsScene.unity",
  "projectHash":               "<64文字の SHA-256 ハッシュ>",
  "masterUnityVersion":        "6000.2.10f1",
  "masterRecorderVersion":     "5.1.2",
  "metaJson":                  ""
}
```

## CLI 引数（Bootstrap）

| 引数 | 説明 |
|-----|------|
| `-distRecorderPort <int>` | HTTP 待受ポート（デフォルト 11080） |
| `-distRecorderAllowedIps <csv>` | 接続許可 IP の CSV |
| `-distRecorderPassword <pw>` | 共有パスワード（EditorPrefs より優先） |
| `-distRecorderKeyPath <path>` | レガシー鍵ファイルパス（後方互換） |
| `-distRecorderMaxJobs <int>` | 自動再起動までのジョブ数（デフォルト 10） |

## 既知の制限事項（MVP）

- Worker は**同時に 1 ジョブのみ**実行可能（batchmode Unity は単一プロセス）。
- 自動リトライなし: 失敗したジョブは EditorWindow から手動で再投入が必要。
- UDP Discovery は同一サブネット（/24 ブロードキャスト）のみ対応。ルーター越えは非対応。
- 進捗ストリームはチャンク HTTP（NDJSON）で実装しており、ネイティブ WebSocket は未使用。
- プロジェクト同期のリトライ・再開は未対応（失敗時は再度「同期」ボタンを押してください）。
