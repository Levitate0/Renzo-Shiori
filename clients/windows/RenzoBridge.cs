using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RenzoWindows;

/// <summary>
/// Native bridge exposed to the web UI as a WebView2 host object. A small JS shim
/// (injected at document creation) wraps this as <c>window.__RenzoWindows</c>,
/// giving the shared frontend the same synchronous file/KV/network contract it
/// gets from Android's <c>window.__RenzoAndroid</c>. File/KV work is delegated to
/// <see cref="RenzoStore"/>; downloads are handed to <see cref="RenzoDownloader"/>
/// so they run natively (and keep going when the window is hidden).
///
/// Folder-picker and reconnect are UI actions, so they raise events the shell
/// handles on the WPF dispatcher (the host-object call returns immediately; the
/// result is posted back to JS as a `renzo:folderpicked` event).
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class RenzoBridge
{
    private readonly RenzoStore _store;
    private readonly RenzoDownloader _downloader;

    public RenzoBridge(RenzoStore store, RenzoDownloader downloader)
    {
        _store = store;
        _downloader = downloader;
    }

    /// <summary>Raised by <see cref="PickFolder"/>; the shell shows the folder dialog.</summary>
    public event Action? PickFolderRequested;

    /// <summary>Raised by <see cref="Reconnect"/>; the shell re-loads the server.</summary>
    public event Action? ReconnectRequested;

    // ── files (base64 across the JS boundary, matching the Android bridge) ────────
    public void WriteFileB64(string relPath, string b64) => _store.WriteFile(relPath, Convert.FromBase64String(b64));

    public string ReadFileB64(string relPath)
    {
        byte[]? bytes = _store.ReadFile(relPath);
        return bytes == null ? "" : Convert.ToBase64String(bytes);
    }

    public void DeletePath(string relPath) => _store.DeletePath(relPath);

    public bool Exists(string relPath) => _store.Exists(relPath);

    // ── KV ────────────────────────────────────────────────────────────────────────
    public string? KvGet(string key) => _store.KvGet(key);

    public void KvSet(string key, string value) => _store.KvSet(key, value);

    // ── network ────────────────────────────────────────────────────────────────────
    public bool IsOnline() => NetworkInterface.GetIsNetworkAvailable();

    // ── native background download ─────────────────────────────────────────────────
    public void EnqueueDownload(string payload)
    {
        _store.EnqueueJob(payload);
        _downloader.Start();
    }

    // ── folder selection / reconnect (UI actions) ───────────────────────────────────
    public void PickFolder() => PickFolderRequested?.Invoke();

    public string? GetFolder() => _store.FolderLabel();

    public void Reconnect() => ReconnectRequested?.Invoke();
}
