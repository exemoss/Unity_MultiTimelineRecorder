using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Installs <c>com.unity.recorder</c> via the Unity Package Manager API.
    ///
    /// Usage: call <see cref="StartInstall"/> once; then poll <see cref="IsInstalling"/>
    /// from <c>EditorApplication.update</c>.  On completion, <see cref="LastResult"/>
    /// and <see cref="LastError"/> are set.
    ///
    /// If the installation fails, <see cref="OpenPackageManagerWindow"/> guides the
    /// artist to the manual installation path.
    /// </summary>
    public static class RecorderPackageInstaller
    {
        public const string RecorderPackageName = "com.unity.recorder";

        private static AddRequest _addRequest;

        /// <summary>True while a <see cref="Client.Add"/> request is in flight.</summary>
        public static bool IsInstalling => _addRequest != null && !_addRequest.IsCompleted;

        /// <summary>Status text set when installation completes or fails.</summary>
        public static string LastResult { get; private set; } = string.Empty;

        /// <summary>Error text set when installation fails.</summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>Invoked when the install completes (success or failure).</summary>
        public static event Action<bool> OnCompleted;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts an asynchronous <c>com.unity.recorder</c> install.
        /// Does nothing when another install is already in flight.
        /// </summary>
        public static void StartInstall()
        {
            if (IsInstalling)
            {
                Debug.Log("[RecorderPackageInstaller] Install already in progress.");
                return;
            }

            LastResult  = string.Empty;
            LastError   = string.Empty;
            _addRequest = Client.Add(RecorderPackageName);

            // Hook the update loop to poll for completion.
            EditorApplication.update += PollInstall;
            Debug.Log($"[RecorderPackageInstaller] Installing {RecorderPackageName}...");
        }

        /// <summary>
        /// Opens the Unity Package Manager window so the artist can install
        /// <c>com.unity.recorder</c> manually.
        /// </summary>
        public static void OpenPackageManagerWindow()
        {
            EditorApplication.ExecuteMenuItem("Window/Package Manager");
        }

        // ------------------------------------------------------------------
        // Polling
        // ------------------------------------------------------------------

        private static void PollInstall()
        {
            if (_addRequest == null || !_addRequest.IsCompleted) return;

            EditorApplication.update -= PollInstall;

            if (_addRequest.Status == StatusCode.Success)
            {
                LastResult = $"{RecorderPackageName} のインストールが完了しました。\n" +
                             "manifest.json が変更されました。git status を確認してください。";
                LastError  = string.Empty;
                Debug.Log($"[RecorderPackageInstaller] {LastResult}");
                OnCompleted?.Invoke(true);
            }
            else
            {
                LastError  = _addRequest.Error?.message ?? "不明なエラー";
                LastResult = $"{RecorderPackageName} のインストールに失敗しました: {LastError}";
                Debug.LogError($"[RecorderPackageInstaller] {LastResult}");
                OnCompleted?.Invoke(false);
            }

            _addRequest = null;
        }
    }
}
