using System.IO;
using UnityEditor.Recorder;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// 出力ファイル名の衝突回避 (自動リネーム)。
    ///
    /// MTR のファイル名テンプレートに &lt;Take&gt; や &lt;Date&gt; が入っていない
    /// (あるいはテイク番号が増えないまま) 同名で再録画すると、Unity Recorder /
    /// FFmpeg エンコーダは既存ファイルを黙って上書きする。納品前の素材が
    /// 予告なく消える事故を防ぐため、録画設定へファイル名を焼き込む直前に
    /// 出力先の既存ファイルと照合し、衝突していれば "_001" "_002" … を付けた
    /// 空き名へ自動リネームする (リネーム時はコンソールへ通知する)。
    /// </summary>
    internal static class OutputFileUniquifier
    {
        /// <summary>
        /// ワイルドカード展開済みのファイル名 <paramref name="fileName"/> が出力先
        /// <paramref name="directory"/> の既存ファイルと衝突しないよう調整して返す。
        /// 衝突しなければそのまま返す。
        /// &lt;Frame&gt; 等の未解決ワイルドカード (Recorder が録画時に展開する分) が
        /// 残っている場合は「* に置換したパターンに一致するファイルが 1 つでもあるか」で
        /// 衝突と見なし、サフィックスは最初のワイルドカードの直前に挿入する。
        /// </summary>
        public static string EnsureUnique(string directory, string fileName,
            MultiRecorderConfig.RecorderConfigItem recorderItem)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || recorderItem == null)
            {
                return fileName;
            }

            try
            {
                if (!Directory.Exists(directory))
                {
                    return fileName;   // 出力先がまだ無ければ衝突しようがない
                }

                var extension = GetPrimaryExtension(recorderItem);
                // 連番系 (Image / AOV) は <Frame> がテンプレートに無くても Recorder が
                // フレーム番号を付けて書き出すため、完全一致ではなく前方一致で照合する
                var isSequence = recorderItem.recorderType == RecorderSettingsType.Image
                              || recorderItem.recorderType == RecorderSettingsType.AOV;
                if (!Collides(directory, fileName, extension, isSequence))
                {
                    return fileName;
                }

                for (int n = 1; n <= 999; n++)
                {
                    var candidate = InsertSuffix(fileName, "_" + n.ToString("000"));
                    if (!Collides(directory, candidate, extension, isSequence))
                    {
                        MultiTimelineRecorderLogger.Log(
                            $"[MultiTimelineRecorder] 出力先に同名ファイルがあるため自動リネームしました: " +
                            $"{fileName} → {candidate} ({directory})");
                        return candidate;
                    }
                }

                // 999 まで埋まっているのは異常事態。上書きよりはマシなのでそのまま警告して返す
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] 自動リネームの空き番号が見つかりません (_001〜_999 すべて使用済み): " +
                    $"{fileName} ({directory})。既存ファイルが上書きされる可能性があります");
                return fileName;
            }
            catch (System.Exception ex)
            {
                // 権限・ネットワークドライブ等での失敗は録画自体を止めない (従来挙動 = 上書きに戻るだけ)
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] 出力ファイル名の衝突チェックに失敗しました ({ex.GetType().Name}: {ex.Message})。" +
                    "既存ファイルが上書きされる可能性があります");
                return fileName;
            }
        }

        /// <summary>fileName (+ extension) が directory の既存ファイルと衝突するか。</summary>
        private static bool Collides(string directory, string fileName, string extension, bool isSequence)
        {
            var withExt = string.IsNullOrEmpty(extension) ? fileName : fileName + "." + extension;
            if (withExt.IndexOf('<') < 0)
            {
                if (!isSequence)
                {
                    return File.Exists(Path.Combine(directory, withExt));
                }

                // 連番系で <Frame> がテンプレートに無い場合、Recorder が末尾にフレーム番号を
                // 付けるため前方一致で照合する
                var seqPattern = string.IsNullOrEmpty(extension)
                    ? fileName + "*"
                    : fileName + "*." + extension;
                return Directory.GetFiles(directory, seqPattern, SearchOption.TopDirectoryOnly).Length > 0;
            }

            // <Frame> 等が残っている場合: ワイルドカードを * にした検索パターンで照合する
            var pattern = System.Text.RegularExpressions.Regex.Replace(withExt, "<[^<>]*>", "*");
            var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            return files.Length > 0;
        }

        /// <summary>
        /// サフィックスを挿入する。未解決ワイルドカードが残っていれば最初のワイルドカードの
        /// 直前 ("name_&lt;Frame&gt;" → "name_001_&lt;Frame&gt;")、無ければ末尾に付ける。
        /// </summary>
        private static string InsertSuffix(string fileName, string suffix)
        {
            var idx = fileName.IndexOf('<');
            if (idx < 0)
            {
                return fileName + suffix;
            }

            var head = fileName.Substring(0, idx).TrimEnd('_');
            var tail = fileName.Substring(idx);
            return head + suffix + "_" + tail;
        }

        /// <summary>
        /// レコーダ種別ごとの代表拡張子。衝突チェック用 (実際の拡張子決定は各 RecorderSettings 側)。
        /// </summary>
        private static string GetPrimaryExtension(MultiRecorderConfig.RecorderConfigItem item)
        {
            switch (item.recorderType)
            {
                case RecorderSettingsType.Movie:
                    switch (item.movieConfig != null ? item.movieConfig.outputFormat : MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
                    {
                        case MovieRecorderSettings.VideoRecorderOutputFormat.MOV: return "mov";
                        case MovieRecorderSettings.VideoRecorderOutputFormat.WebM: return "webm";
                        default: return "mp4";
                    }
                case RecorderSettingsType.Image:
                    switch (item.imageFormat)
                    {
                        case ImageRecorderSettings.ImageRecorderOutputFormat.JPEG: return "jpg";
                        case ImageRecorderSettings.ImageRecorderOutputFormat.EXR: return "exr";
                        default: return "png";
                    }
                case RecorderSettingsType.AOV: return "exr";
                case RecorderSettingsType.Alembic: return "abc";
                case RecorderSettingsType.FBX: return "fbx";
                case RecorderSettingsType.Animation: return "anim";
                default: return "";
            }
        }
    }
}
