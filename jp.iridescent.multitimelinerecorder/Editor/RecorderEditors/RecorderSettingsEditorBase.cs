using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.RecorderEditors
{
    /// <summary>
    /// Base class for all recorder settings editors
    /// Follows Unity Recorder's standard UI pattern
    /// </summary>
    public abstract class RecorderSettingsEditorBase
    {
        protected IRecorderSettingsHost host;
        protected bool inputFoldout = true;
        protected bool outputFormatFoldout = true;
        protected bool outputFileFoldout = true;
        
        // セクション見出しのスタイル
        private static class SectionStyles
        {
            public static readonly Color HeaderBackgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.18f, 0.18f, 0.18f, 1f)  // Pro Skin: 暗い背景
                : new Color(0.8f, 0.8f, 0.8f, 1f);     // Light Skin: 明るいグレー
                
            public static readonly Color LineColor = EditorGUIUtility.isProSkin
                ? new Color(0.3f, 0.3f, 0.3f, 1f)      // Pro Skin: 暗いライン
                : new Color(0.6f, 0.6f, 0.6f, 1f);     // Light Skin: 明るいライン
                
            public static GUIStyle HeaderLabel => new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black }
            };
        }
        
        /// <summary>
        /// Draws a section header with background and optional foldout
        /// </summary>
        protected bool DrawSectionHeader(string title, bool foldout = true, bool isFoldable = true)
        {
            EditorGUILayout.Space(2);
            
            // セクションヘッダーの背景を描画
            Rect headerRect = EditorGUILayout.GetControlRect(false, 20);
            if (Event.current.type == EventType.Repaint)
            {
                // 背景を描画
                EditorGUI.DrawRect(headerRect, SectionStyles.HeaderBackgroundColor);
                
                // 下線を描画
                Rect lineRect = new Rect(headerRect.x, headerRect.yMax - 1, headerRect.width, 1);
                EditorGUI.DrawRect(lineRect, SectionStyles.LineColor);
            }
            
            // インデントを調整してヘッダーを描画
            headerRect.x += 4;
            headerRect.width -= 8;
            
            if (isFoldable)
            {
                return EditorGUI.Foldout(headerRect, foldout, title, true, SectionStyles.HeaderLabel);
            }
            else
            {
                GUI.Label(headerRect, title, SectionStyles.HeaderLabel);
                return true;
            }
        }
        
        /// <summary>
        /// Draws the complete recorder settings UI
        /// </summary>
        public virtual void DrawRecorderSettings()
        {
            // Input section
            inputFoldout = DrawSectionHeader("Input", inputFoldout);
            if (inputFoldout)
            {
                EditorGUI.indentLevel++;
                DrawInputSettings();
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);
            
            // Output Format section
            outputFormatFoldout = DrawSectionHeader("Output Format", outputFormatFoldout);
            if (outputFormatFoldout)
            {
                EditorGUI.indentLevel++;
                DrawOutputFormatSettings();
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space(5);

            // Output File section
            outputFileFoldout = DrawSectionHeader("Output File", outputFileFoldout);
            if (outputFileFoldout)
            {
                EditorGUI.indentLevel++;
                DrawOutputFileSettings();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(5);

            // Recording Range section（全レコーダー種別で共通）
            recordingRangeFoldout = DrawSectionHeader("Recording Range", recordingRangeFoldout);
            if (recordingRangeFoldout)
            {
                EditorGUI.indentLevel++;
                DrawRecordingRangeSettings();
                EditorGUI.indentLevel--;
            }
        }

        protected bool recordingRangeFoldout = true;

        /// <summary>
        /// 尺範囲（このレコーダーだけ Timeline の一部区間を録る）の UI。
        /// 入力単位はフレーム / 秒を切り替えられるが、保持は常にフレーム。
        /// </summary>
        protected virtual void DrawRecordingRangeSettings()
        {
            host.useCustomRange = EditorGUILayout.Toggle(
                new GUIContent("Use Custom Range",
                    "有効にすると、このレコーダーだけ Timeline の指定区間だけを録画する。無効なら Timeline 全体（SignalEmitter 使用時はその範囲）"),
                host.useCustomRange);

            if (!host.useCustomRange)
            {
                EditorGUILayout.LabelField(" ", "Timeline 全体を録画します", EditorStyles.miniLabel);
                return;
            }

            int fps = Mathf.Max(1, host.frameRate);

            host.rangeUnit = (RecorderRangeUnit)EditorGUILayout.EnumPopup(
                new GUIContent("Unit", "入力単位。内部は常にフレームで保持するため、切り替えても値は失われない"),
                host.rangeUnit);

            if (host.rangeUnit == RecorderRangeUnit.Frames)
            {
                host.rangeStartFrame = Mathf.Max(0, EditorGUILayout.IntField(
                    new GUIContent("Start Frame", "Timeline の先頭を 0 とした開始フレーム（このフレームを含む）"),
                    host.rangeStartFrame));
                host.rangeEndFrame = EditorGUILayout.IntField(
                    new GUIContent("End Frame", "終了フレーム（このフレームを含む）"),
                    host.rangeEndFrame);
            }
            else
            {
                float startSec = Mathf.Max(0f, EditorGUILayout.FloatField(
                    new GUIContent("Start (s)", "Timeline の先頭を 0 とした開始時刻（秒）"),
                    host.rangeStartFrame / (float)fps));
                float endSec = EditorGUILayout.FloatField(
                    new GUIContent("End (s)", "終了時刻（秒）。この時刻のフレームまで録画に含む"),
                    (host.rangeEndFrame + 1) / (float)fps);

                host.rangeStartFrame = Mathf.Max(0, Mathf.RoundToInt(startSec * fps));
                // 秒指定は「その時刻まで」を意味するので、最終フレームは 1 引いて inclusive に戻す
                host.rangeEndFrame = Mathf.Max(host.rangeStartFrame, Mathf.RoundToInt(endSec * fps) - 1);
            }

            if (host.rangeEndFrame < host.rangeStartFrame)
            {
                EditorGUILayout.HelpBox("終了が開始より前です。録画開始時にエラーになります。", MessageType.Error);
                return;
            }

            int frameCount = host.rangeEndFrame - host.rangeStartFrame + 1;
            EditorGUILayout.LabelField(" ",
                $"→ {frameCount} frames / {frameCount / (float)fps:F2}s" +
                $"  ({host.rangeStartFrame / (float)fps:F2}s 〜 {(host.rangeEndFrame + 1) / (float)fps:F2}s)",
                EditorStyles.miniLabel);

            // 前尺スキップ（録画範囲の手前から再生を始め、それより前は再生しない）
            EditorGUILayout.Space(3);
            host.skipBeforeRange = EditorGUILayout.Toggle(
                new GUIContent("Skip Before Range",
                    "録画範囲より前の再生（前尺）をスキップし、下の助走ぶんだけ手前から再生する。長い前尺の再生待ちが無くなる"),
                host.skipBeforeRange);

            if (!host.skipBeforeRange)
            {
                EditorGUILayout.LabelField(" ", "Timeline の先頭から再生します（録画は上の範囲のみ）", EditorStyles.miniLabel);
                return;
            }

            EditorGUI.indentLevel++;
            if (host.rangeUnit == RecorderRangeUnit.Frames)
            {
                host.leadInFrames = Mathf.Max(0, EditorGUILayout.IntField(
                    new GUIContent("Lead-in (frames)",
                        "録画開始の何フレーム前から再生を始めるか。布・パーティクル等を落ち着かせる助走で、この区間は録画されない"),
                    host.leadInFrames));
            }
            else
            {
                float leadSec = Mathf.Max(0f, EditorGUILayout.FloatField(
                    new GUIContent("Lead-in (s)",
                        "録画開始の何秒前から再生を始めるか。布・パーティクル等を落ち着かせる助走で、この区間は録画されない"),
                    host.leadInFrames / (float)fps));
                host.leadInFrames = Mathf.Max(0, Mathf.RoundToInt(leadSec * fps));
            }

            int playbackStartFrame = Mathf.Max(0, host.rangeStartFrame - host.leadInFrames);
            int skippedFrames = playbackStartFrame;
            EditorGUILayout.LabelField(" ",
                $"→ 再生は frame {playbackStartFrame} ({playbackStartFrame / (float)fps:F2}s) から" +
                (skippedFrames > 0 ? $"（前尺 {skippedFrames} frames / {skippedFrames / (float)fps:F2}s をスキップ）" : "（先頭から）"),
                EditorStyles.miniLabel);

            EditorGUILayout.HelpBox(
                "同じ Timeline の有効なレコーダーが全て Skip 設定のときだけ前尺をスキップします。" +
                "1 つでも全体録画のレコーダーがあると、その絵が必要なため先頭から再生されます（録画範囲は各レコーダーの指定どおり）。",
                MessageType.Info);
            EditorGUI.indentLevel--;
        }
        
        /// <summary>
        /// Draws a simple separator line
        /// </summary>
        protected void DrawSeparator()
        {
            EditorGUILayout.Space(3);
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, SectionStyles.LineColor);
            }
            EditorGUILayout.Space(3);
        }
        
        /// <summary>
        /// Draws a subsection header without background
        /// </summary>
        protected void DrawSubsectionHeader(string title)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        }
        
        /// <summary>
        /// Draws the input settings specific to this recorder type
        /// </summary>
        protected virtual void DrawInputSettings()
        {
            EditorGUILayout.LabelField("Source", "Game View");
            
            // Resolution settings (common for most recorders)
            EditorGUILayout.Space(5);
            DrawSubsectionHeader("Resolution");
            
            // Use Global Resolution toggle
            EditorGUI.BeginChangeCheck();
            host.useGlobalResolution = EditorGUILayout.Toggle("Use Global Resolution", host.useGlobalResolution);
            bool resolutionChanged = EditorGUI.EndChangeCheck();
            
            // Show resolution fields
            using (new EditorGUI.DisabledScope(host.useGlobalResolution))
            {
                EditorGUI.indentLevel++;
                
                if (host.useGlobalResolution)
                {
                    // Show global values as read-only
                    EditorGUILayout.LabelField("Width", "Using global setting", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Height", "Using global setting", EditorStyles.miniLabel);
                }
                else
                {
                    // Allow editing local values
                    host.width = EditorGUILayout.IntField("Width", host.width);
                    host.height = EditorGUILayout.IntField("Height", host.height);
                    
                    // Resolution presets
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    if (GUILayout.Button("HD", GUILayout.Width(40)))
                    {
                        host.width = 1920;
                        host.height = 1080;
                    }
                    if (GUILayout.Button("2K", GUILayout.Width(40)))
                    {
                        host.width = 2048;
                        host.height = 1080;
                    }
                    if (GUILayout.Button("4K", GUILayout.Width(40)))
                    {
                        host.width = 3840;
                        host.height = 2160;
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
                
                EditorGUI.indentLevel--;
            }
            
            // If resolution changed and using global, sync with global values
            if (resolutionChanged && host.useGlobalResolution)
            {
                // The host will handle syncing with global values
            }
        }
        
        /// <summary>
        /// Draws the output format settings specific to this recorder type
        /// </summary>
        protected abstract void DrawOutputFormatSettings();
        
        /// <summary>
        /// Draws the output file settings
        /// </summary>
        protected virtual void DrawOutputFileSettings()
        {
            // File Name field with wildcards button
            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName("FileNameField");
            
            // Use DelayedTextField to handle updates more smoothly
            EditorGUI.BeginChangeCheck();
            string newFileName = EditorGUILayout.TextField("File Name", host.fileName);
            bool fileNameChanged = EditorGUI.EndChangeCheck();
            
            if (fileNameChanged || GUI.changed)
            {
                host.fileName = newFileName;
                
                // Auto-add <Frame> for image sequence types if missing
                RecorderSettingsType currentType = GetRecorderType();
                if ((currentType == RecorderSettingsType.Image || currentType == RecorderSettingsType.AOV) 
                    && !host.fileName.Contains("<Frame>"))
                {
                    // Add <Frame> before extension if present, otherwise at the end
                    if (host.fileName.Contains("."))
                    {
                        int lastDotIndex = host.fileName.LastIndexOf('.');
                        host.fileName = host.fileName.Substring(0, lastDotIndex) + "_<Frame>" + host.fileName.Substring(lastDotIndex);
                    }
                    else
                    {
                        host.fileName += "_<Frame>";
                    }
                }
                
                GUI.changed = true;
            }
            
            // Add wildcards button
            if (GUILayout.Button(new GUIContent("▼"), EditorStyles.popup, GUILayout.MaxWidth(18)))
            {
                ShowWildcardsMenu();
            }
            
            EditorGUILayout.EndHorizontal();
            
            // Show example output with processed filename
            string exampleFileName = GetFullOutputPath();
            EditorGUILayout.LabelField($"Example: {exampleFileName}", EditorStyles.miniLabel);
            
            // Path settings are now handled by OutputPathSettingsUI in MultiTimelineRecorder
            // This prevents duplicate path UI elements
            
            // Take number
            host.takeNumber = EditorGUILayout.IntField("Take Number", host.takeNumber);
        }
        
        /// <summary>
        /// Show wildcards popup menu
        /// </summary>
        protected void ShowWildcardsMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("<Scene>"), false, () => InsertWildcard("<Scene>"));
            menu.AddItem(new GUIContent("<Take>"), false, () => InsertWildcard("<Take>"));
            menu.AddItem(new GUIContent("<RecorderTake>"), false, () => InsertWildcard("<RecorderTake>"));
            menu.AddItem(new GUIContent("<Recorder>"), false, () => InsertWildcard("<Recorder>"));
            menu.AddItem(new GUIContent("<RecorderName>"), false, () => InsertWildcard("<RecorderName>"));
            menu.AddItem(new GUIContent("<Time>"), false, () => InsertWildcard("<Time>"));
            menu.AddItem(new GUIContent("<Frame>"), false, () => InsertWildcard("<Frame>"));
            menu.AddItem(new GUIContent("<Resolution>"), false, () => InsertWildcard("<Resolution>"));
            menu.AddItem(new GUIContent("<Product>"), false, () => InsertWildcard("<Product>"));
            menu.AddItem(new GUIContent("<Date>"), false, () => InsertWildcard("<Date>"));
            
            // Add context-specific wildcards
            bool addedSeparator = false;
            
            // Add Timeline wildcard if available
            if (GetTimelineName() != null)
            {
                if (!addedSeparator)
                {
                    menu.AddSeparator("");
                    addedSeparator = true;
                }
                menu.AddItem(new GUIContent("<Timeline>"), false, () => InsertWildcard("<Timeline>"));
            }
            
            // Add GameObject wildcard if available
            if (GetTargetGameObjectName() != null)
            {
                if (!addedSeparator)
                {
                    menu.AddSeparator("");
                    addedSeparator = true;
                }
                menu.AddItem(new GUIContent("<GameObject>"), false, () => InsertWildcard("<GameObject>"));
            }
            
            menu.ShowAsContext();
        }
        
        /// <summary>
        /// Insert wildcard at cursor position
        /// </summary>
        protected void InsertWildcard(string wildcard)
        {
            // Get the current TextEditor for the FileNameField
            TextEditor textEditor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            
            // Store cursor position for later
            int newCursorPos = 0;
            
            // If the FileNameField has focus and we have a TextEditor
            if (GUI.GetNameOfFocusedControl() == "FileNameField" && textEditor != null && textEditor.text == host.fileName)
            {
                if (textEditor.hasSelection)
                {
                    // Replace selection
                    int start = Mathf.Min(textEditor.selectIndex, textEditor.cursorIndex);
                    int end = Mathf.Max(textEditor.selectIndex, textEditor.cursorIndex);
                    host.fileName = host.fileName.Substring(0, start) + wildcard + host.fileName.Substring(end);
                    newCursorPos = start + wildcard.Length;
                }
                else
                {
                    // Insert at cursor
                    int pos = textEditor.cursorIndex;
                    host.fileName = host.fileName.Insert(pos, wildcard);
                    newCursorPos = pos + wildcard.Length;
                }
                
                // Update TextEditor's text immediately
                textEditor.text = host.fileName;
                textEditor.cursorIndex = newCursorPos;
                textEditor.selectIndex = newCursorPos;
            }
            else
            {
                // Simple append if field doesn't have focus
                host.fileName += wildcard;
                newCursorPos = host.fileName.Length;
            }
            
            // Force immediate repaint
            GUI.changed = true;
            
            // Use Event to force immediate update
            Event e = Event.current;
            if (e != null)
            {
                e.Use();
            }
            
            // Force all inspectors to repaint immediately
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            
            // Also mark the host object dirty if it's a Unity Object
            if (host is UnityEngine.Object obj)
            {
                EditorUtility.SetDirty(obj);
            }
        }
        
        /// <summary>
        /// Get full output path for preview
        /// </summary>
        protected virtual string GetFullOutputPath()
        {
            // Process wildcards for preview
            var context = new WildcardContext(host.takeNumber, host.width, host.height)
            {
                RecorderName = GetRecorderName(),
                // <RecorderName> は実際の録画と同じくアイテム表示名で解決する（プレビューの一致のため）
                RecorderDisplayName = host.recorderItemName,
                GameObjectName = GetTargetGameObjectName(),
                TimelineName = GetTimelineName(),
                TimelineTakeNumber = GetTimelineTakeNumber()
            };
            string processedFileName = WildcardProcessor.ProcessWildcards(host.fileName, context);
            
            // Add file extension based on recorder type
            string extension = GetFileExtension();
            if (!string.IsNullOrEmpty(extension) && !processedFileName.EndsWith("." + extension))
            {
                processedFileName += "." + extension;
            }
            
            // Return just the processed filename for preview
            return processedFileName;
        }
        
        /// <summary>
        /// Get file extension for the current recorder type
        /// </summary>
        protected abstract string GetFileExtension();
        
        /// <summary>
        /// Get recorder name for wildcard processing
        /// </summary>
        protected abstract string GetRecorderName();
        
        /// <summary>
        /// Get target GameObject name for wildcard processing
        /// Override in recorders that have target GameObjects
        /// </summary>
        protected virtual string GetTargetGameObjectName()
        {
            return null;
        }
        
        /// <summary>
        /// Get Timeline name for wildcard processing
        /// </summary>
        protected virtual string GetTimelineName()
        {
            if (host.selectedDirector != null && host.selectedDirector.playableAsset != null)
            {
                return host.selectedDirector.playableAsset.name;
            }
            return null;
        }
        
        /// <summary>
        /// Get Timeline Take Number for wildcard processing
        /// </summary>
        protected virtual int? GetTimelineTakeNumber()
        {
            // Use the interface method to get timeline take number
            return host.GetTimelineTakeNumber();
        }
        
        /// <summary>
        /// Validates the current settings
        /// </summary>
        public virtual bool ValidateSettings(out string errorMessage)
        {
            errorMessage = null;
            return true;
        }
        
        /// <summary>
        /// Get the recorder type for this editor
        /// </summary>
        protected abstract RecorderSettingsType GetRecorderType();
    }
}