using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace RenzoWindows;

public partial class MainWindow : Window
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private ServerConfig _config = new();
    private bool _webViewReady;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _config = ServerConfig.Load();
        if (!string.IsNullOrEmpty(_config.ServerUrl))
        {
            AddressBox.Text = _config.ServerUrl;
            await ConnectAsync(_config.ServerUrl);
        }
        else
        {
            AddressBox.Focus();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+S: return to the server-address screen (e.g. to switch servers).
        if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowServerPanel(null);
            e.Handled = true;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await ConnectAsync(AddressBox.Text);

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await ConnectAsync(AddressBox.Text);
    }

    private async Task ConnectAsync(string input)
    {
        input = input.Trim().TrimEnd('/');
        if (input.Length == 0)
        {
            ShowError("Enter a server address.");
            return;
        }

        SetBusy(true, "Connecting…");
        string? server = await ValidateServerAsync(input);
        if (server == null)
        {
            SetBusy(false, null);
            ShowError("Could not reach a Renzō server at that address. Check the address (including port) and that the server is running.");
            return;
        }

        _config.ServerUrl = server;
        _config.Save();

        try
        {
            await EnsureWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetBusy(false, null);
            ShowError("Microsoft Edge WebView2 Runtime is not installed. Opening the download page…");
            OpenInBrowser("https://developer.microsoft.com/en-us/microsoft-edge/webview2/");
            return;
        }
        catch (Exception ex)
        {
            SetBusy(false, null);
            ShowError("Failed to start the embedded browser: " + ex.Message);
            return;
        }

        SetBusy(false, null);
        WebView.Source = new Uri(server);
        ServerPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;
        WebView.Focus();
    }

    /// <summary>
    /// Probes {address}/api/system/info/public and returns the working base URL,
    /// or null when no Renzo server answers. When the user omitted a scheme,
    /// https is tried first, then http (for LAN addresses).
    /// </summary>
    private static async Task<string?> ValidateServerAsync(string input)
    {
        List<string> candidates = new();
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(input);
        }
        else
        {
            candidates.Add("https://" + input);
            candidates.Add("http://" + input);
        }

        foreach (string baseUrl in candidates)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
                continue;
            try
            {
                using HttpResponseMessage resp = await _http.GetAsync(baseUrl + "/api/system/info/public");
                if (!resp.IsSuccessStatusCode)
                    continue;
                using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("product", out JsonElement product))
                {
                    string? p = product.GetString();
                    // Accept both the current brand and the legacy handshake id.
                    if (string.Equals(p, "Renzo", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p, "Rensaio", StringComparison.OrdinalIgnoreCase))
                    {
                        return baseUrl;
                    }
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
            return;

        // A fixed user-data folder makes cookies (incl. the refresh-token cookie)
        // survive restarts, so login is permanent until the server expires it.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renzo", "WebView2");
        CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await WebView.EnsureCoreWebView2Async(env);

        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
        WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
        _webViewReady = true;
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Keep same-server links inside the app; external links go to the default browser.
        e.Handled = true;
        if (_config.ServerUrl != null &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? target) &&
            Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out Uri? server) &&
            string.Equals(target.Host, server.Host, StringComparison.OrdinalIgnoreCase))
        {
            WebView.CoreWebView2.Navigate(e.Uri);
        }
        else
        {
            OpenInBrowser(e.Uri);
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess &&
            e.WebErrorStatus is CoreWebView2WebErrorStatus.CannotConnect
                or CoreWebView2WebErrorStatus.HostNameNotResolved
                or CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.ConnectionReset
                or CoreWebView2WebErrorStatus.Timeout)
        {
            ShowServerPanel("Lost connection to the server.");
        }
    }

    private void ShowServerPanel(string? error)
    {
        WebView.Visibility = Visibility.Collapsed;
        ServerPanel.Visibility = Visibility.Visible;
        AddressBox.Text = _config.ServerUrl ?? "";
        if (error != null)
            ShowError(error);
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string? status)
    {
        ConnectButton.IsEnabled = !busy;
        AddressBox.IsEnabled = !busy;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = status ?? "";
        StatusText.Visibility = status != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Non-critical: the link just doesn't open.
        }
    }
}
