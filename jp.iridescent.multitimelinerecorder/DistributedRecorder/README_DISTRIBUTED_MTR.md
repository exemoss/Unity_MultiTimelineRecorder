# MTR 分散レンダリング — セットアップ & 使い方ガイド

## 概要

Multi Timeline Recorder (MTR) に分散レンダリング機能を統合したフォークです。  
**1 Timeline = 1 ジョブ** として複数 Worker PC に並列分配し、Image Sequence を録画します。

```
[Master PC]  MTR ウィンドウ →「分散実行」ボタン
     ↓ HMAC 認証 + ジョブ投入 (HTTP POST /jobs)
[Worker PC1] Timeline A を Image 録画 → 連番 PNG 出力
[Worker PC2] Timeline B を Image 録画 → 連番 PNG 出力
[Worker PC3] Timeline C を Image 録画 → 連番 PNG 出力
     ↓ 進捗収集 (NDJSON /progress) + ファイル回収 (/files)
[Master PC]  Recordings/Distributed/<jobId>/ に回収
```

**対応形式**: Image Sequence（PNG / JPEG / EXR）のみ。  
Movie / AOV / FBX / Animation / Alembic は次フェーズ対応予定です。

---

## 前提条件

| 項目 | 内容 |
|------|------|
| Unity バージョン | 6000.2.10f1 以降推奨（MTR 本体は 2021.3+ 対応） |
| レンダーパイプライン | HDRP 17.x（または URP / BRP。暗転対策は後述） |
| Recorder パッケージ | `com.unity.recorder` 5.1.2 以降 |
| ネットワーク | 同一 LAN（/24 サブネット内）。ルーター越えは非対応 |
| プロジェクト同期 | **両 PC が同一 git コミットで同期済み**であること（基本前提） |
| Worker 起動モード | **非 batchmode の GUI Editor**（`-batchmode` 不可。後述） |

> **Worker を batchmode で起動してはいけない理由**: Unity Recorder 5.1 は batchmode
> 実行時に GameView が初期化されず、フレームを取得できないため録画が失敗します。
> Worker は必ず通常の GUI Editor として起動し、**Run In Background** を有効にしてください。

---

## 2-PC セットアップ手順

### Step 1: 両 PC にパッケージを導入する

**git URL 経由（UPM）**

1. Unity Package Manager を開く（Window > Package Manager）
2. 左上の「+」→「Add package from git URL...」
3. 次の URL を入力:
   ```
   https://github.com/<your-fork>/Unity_MultiTimelineRecorder.git?path=/jp.iridescent.multitimelinerecorder
   ```
4. Master PC・Worker PC の両方で同じ手順を繰り返す

**ローカルパス経由**（同一 LAN 共有フォルダや USB コピーの場合）

1. フォークをローカルにクローン（または zip 展開）
2. UPM の「Add package from disk...」で `jp.iridescent.multitimelinerecorder/package.json` を選択

---

### Step 2: Recorder パッケージをインストールする

`DistributedRecorder > Setup Wizard` を開き「Install Unity Recorder」をクリック、  
または UPM で `com.unity.recorder` を手動追加します。

---

### Step 3: 共有鍵（パスワード）を両 PC に設定する

Master PC と Worker PC は HMAC-SHA256 で相互認証します。  
同じパスワードを設定することで、両 PC で同一の HMAC 鍵が導出されます。

1. Master PC で `DistributedRecorder > Setup Wizard` を開く
2. 「Shared Password」欄に任意のパスワードを入力（8 ～ 32 文字）
3. 「Save Password」ボタンをクリック
4. 表示された同じパスワードを Worker PC にも入力・保存する

> セキュリティ上の注意: パスワードは Windows の HKCU レジストリ（EditorPrefs）に  
> 平文で保存されます。イントラネット専用ツールとして設計されており、  
> インターネット公開環境では使用しないでください。

---

### Step 4: Worker PC で Unity を起動する

Worker PC で対象プロジェクトを Unity 6000.2 以降の GUI Editor で開きます。

**推奨設定:**

- Edit > Preferences > General > **Run In Background** → **ON**  
  （バックグラウンド時もレンダリングが続行されます）
- DistributedRecorder > Setup Wizard → 「Start Worker」ボタンをクリック  
  （HTTP リスナーがポート 11080 で起動します）

起動確認: `DistributedRecorder > Setup Wizard` のヘルスチェックが  
「Worker Running」と表示されれば OK です。

---

### Step 5: Master PC で Worker を登録する

1. `DistributedRecorder > Setup Wizard` を開く
2. 「Discover Workers」で同一サブネット内の Worker を自動検出
3. 見つかった Worker にチェックを入れて「Register」
4. WorkerRegistryAsset が自動生成されます（Assets/ 直下）

手動登録する場合: Assets > Create > DistributedRecorder > WorkerRegistry で  
`WorkerRegistryAsset` を作成し、Worker のホスト名 / IP とポートを手入力します。

---

## 使い方

### 1. サンプルシーンを生成する（任意）

メニューから `DistributedRecorder > Create MTR Multi-Timeline Sample` を実行します。

次のアセットが `Assets/MtrDistributedSample/` に生成されます:

| ファイル | 内容 |
|---------|------|
| `MtrMultiSample.unity` | 3 体の Cube + カメラ + ライトを含むサンプルシーン |
| `TimelineA.playable` | 赤 Cube の Y 軸回転（2 秒 / 60 フレーム） |
| `TimelineB.playable` | 緑 Cube の上下バウンス（2 秒 / 60 フレーム） |
| `TimelineC.playable` | 青 Cube の前後移動（2 秒 / 60 フレーム） |
| `CubeMat_Red.mat` など | HDRP Lit 鮮色マテリアル（3 色） |

> このメニューはユーザーが明示的に実行した場合のみアセットを生成します。  
> 自動テストからは呼ばれません。

---

### 2. MTR ウィンドウで Timeline を選択する

1. `Window > Multi Timeline Recorder` を開く
2. 生成した `MtrMultiSample` シーンを開く
3. 画面左列の「+」ボタンで `DirectorA` / `DirectorB` / `DirectorC` を追加
4. 各 Timeline の「Recorder」列で **Image Sequence** を有効化  
   （フォーマット: PNG、解像度: 1280×720 を推奨）

---

### 3. 分散実行する

1. MTR ウィンドウ下部の「**分散レンダリング (Distributed Render)**」チェックボックスを ON
2. Worker Registry に登録した `WorkerRegistryAsset` を割り当て
3. **「分散実行 (N ジョブ → M Worker)」** ボタンをクリック

#### ハッシュ不一致ダイアログが出た場合

Master と Worker のプロジェクト内容が異なる場合に表示されます。  
「**上書き送信（Send anyway）**」を選択すると、Worker は自分のローカル版プロジェクトで録画を続行します。

---

### 4. 進捗を確認する

分散実行後、ジョブリストに各 Timeline の進捗バーが表示されます:

- `Running`: 録画中（`currentFrame / totalFrames`）
- `Completed + Done`: 録画完了 + ファイル回収完了
- `Failed`: 失敗（Console にエラーメッセージが出力されます）

完了後に「**開く**」ボタンをクリックすると出力フォルダを Finder / Explorer で開けます。

---

### 5. 出力ファイルを確認する

回収先: `<ProjectRoot>/Recordings/Distributed/<jobId>/`

各 Timeline ごとに独立した jobId フォルダが作成され、その中に PNG 連番が入ります。

---

## 前提・制約

| 項目 | 内容 |
|------|------|
| 対応形式 | Image Sequence（PNG / JPEG / EXR）のみ |
| Worker 同時実行 | 1 Editor = 1 ジョブ（複数ジョブの並列不可） |
| プロジェクト転送 | 自動転送なし（git 同期か Send anyway で運用） |
| Worker 起動 | 非 batchmode GUI Editor のみ |
| ネットワーク | 同一 LAN のみ。ルーター越え非対応 |
| Object 参照 | Camera / RenderTexture のネットワーク解決は未対応 |

---

## トラブルシューティング

### 録画フレームが真っ暗になる

HDRP の自動露光（Auto Exposure）が GameView を暗く補正している場合があります。

対策:
- HDRP Volume に **Exposure** コンポーネントを追加し「Fixed」モードに設定する
- `Assets/Settings/` にある HDRenderPipelineAsset を確認し、Exposure Mode を `Fixed` に変更する

---

### 403 Forbidden が返る（IP ブロック）

Worker の許可 IP リストに Master の IP が含まれていない場合に発生します。

対策:
- Worker PC の `DistributedRecorder > Setup Wizard` → 「Allowed IPs」に Master の IP を追加する
- またはコマンドライン引数 `-distRecorderAllowedIps 192.168.1.0/24` で起動する

---

### 409 Conflict が返る（プロジェクトハッシュ不一致）

Master と Worker のプロジェクト内容が異なる場合に発生します。

対策:
- 両 PC を同じ git コミットに揃えて再実行する
- または「Send anyway」ダイアログで続行する（Worker のローカル版で録画）

---

### シーンが自動で開かない（Worker 側）

JobRunner は `scenePath` のシーンを自動で開きますが、Worker の Unity Editor が  
すでに Play Mode の場合は開けません。

対策:
- Worker PC が Play Mode でないことを確認してからジョブを投入する
- ジョブが Failed になった場合は Worker の Console でエラーを確認する

---

### シーン開放後にディレクターが見つからない

JobRunner はシーン内の `PlayableDirector` を `directorObjectName` または  
`directorHierarchyPath` で検索します。名前が一致しない場合は Failed になります。

対策:
- Master PC と Worker PC のシーン内 GameObject 名が一致しているか確認する
- 名前が日本語の場合は ASCII 名称に変更するか、両 PC のシーンを git 同期する

---

## コマンドライン引数（Worker 起動オプション）

Worker PC のコマンドライン（または起動スクリプト）から設定を渡せます:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.2.10f1\Editor\Unity.exe" `
    -projectPath "C:\Projects\MyProject" `
    -distRecorderPort 11080 `
    -distRecorderAllowedIps "192.168.1.100,192.168.1.101" `
    -distRecorderPassword "MySecretPass123"
```

| 引数 | 説明 | デフォルト |
|------|------|-----------|
| `-distRecorderPort <int>` | HTTP 待受ポート | `11080` |
| `-distRecorderAllowedIps <csv>` | 接続許可 IP の CSV | 全許可 |
| `-distRecorderPassword <pw>` | 共有パスワード（EditorPrefs より優先） | — |
| `-distRecorderMaxJobs <int>` | 自動リスタートまでのジョブ数 | `10` |

---

## ライセンス表記

このパッケージは **Multi Timeline Recorder**（以下 MTR）のフォークです。

- **Multi Timeline Recorder 本体**  
  Copyright (c) 2024 Murasaqi  
  MIT License — 原著作権・ライセンス表記は保持しています。  
  元リポジトリ: <https://github.com/murasaqi/Unity_MultiTimelineRecorder>

- **分散レンダリングモジュール (`DistributedRecorder/` 配下)**  
  Unity_Recorder_DistRendering プロジェクト由来のコードを MTR フォーク内に移植。  
  上記 MIT ライセンスのもとで同梱しています。

本フォークへの改変は MTR の MIT ライセンスに従い公開されます。  
`DistributedRecorder/` モジュールのみを別プロジェクトで利用する場合も MIT ライセンスが適用されます。

詳細は `LICENSE` ファイルを参照してください。
