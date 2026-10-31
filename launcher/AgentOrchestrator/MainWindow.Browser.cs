using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgentOrchestrator;

// The in-app browser: a page in the right column, in place of the worker details, opened by the globe beside the context
// ring. Links in replies open in it, and with "Follow the model" on it shows each search and page the model reads. Its
// WebView2 (the Edge engine Windows ships) is made the first time it opens; its data stays in <data>\browser.
public partial class MainWindow
{
    private WebView2? _web;
    private const string BrowserHome = "https://duckduckgo.com/";

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserPanel.IsVisible)
            CloseBrowser();
        else
            OpenBrowser(null);
    }

    private void BrowserClose_Click(object sender, RoutedEventArgs e) => CloseBrowser();

    // url null: the last page, else the home page.
    private void OpenBrowser(string? url)
    {
        StopColumnTween(); // a slide still running finishes first
        if (!BrowserPanel.IsVisible)
        {
            RailPopup.IsOpen = false;
            var fromRail = WorkerRail.IsVisible; // from the rail the column slides open; from the details it only swaps pages
            WorkerRail.Visibility = DetailsPanel.Visibility = Visibility.Collapsed;
            BrowserPanel.Visibility = Visibility.Visible;
            SlideColumn(BrowserPanel, collapsed: false, slide: fromRail);
            BrowserFollowToggle.IsChecked = _settings.BrowserFollow;
            _settings.BrowserOpen = true;
            _settings.Save();
        }
        if (url is not null || _web is null)
            Navigate(url ?? (_settings.BrowserUrl.Length > 0 ? _settings.BrowserUrl : BrowserHome));
    }

    // Back to the worker details (or their rail), as they were.
    private void CloseBrowser()
    {
        _settings.BrowserOpen = false;
        _settings.Save();
        SetWorkerRail(_settings.DetailsCollapsed); // hides the browser
    }

    private void Navigate(string text)
    {
        if (BrowserUrl(text) is not { } url)
            return;
        BrowserAddress.Text = url;
        if (EnsureWeb() is not { } web)
            return;
        if (web.CoreWebView2 is { } core)
            core.Navigate(url);
        else
            web.Source = new Uri(url); // the first page: this starts the engine
    }

    private WebView2? EnsureWeb()
    {
        if (_web is not null || BrowserMessage.IsVisible)
            return _web;
        try
        {
            var web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(AppSettings.DataRoot, "browser") } };
            web.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    BrowserFailed(e.InitializationException);
                    return;
                }
                // pop-ups and target=_blank links open here, not in a separate window
                web.CoreWebView2.NewWindowRequested += (_, a) =>
                {
                    a.Handled = true;
                    web.CoreWebView2.Navigate(a.Uri);
                };
            };
            web.NavigationCompleted += (_, _) =>
            {
                if (web.Source?.AbsoluteUri is { } shown)
                {
                    BrowserAddress.Text = shown;
                    _settings.BrowserUrl = shown;
                    _settings.Save();
                }
                RefreshBrowserButtons();
            };
            BrowserHost.Content = _web = web;
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            BrowserFailed(e);
        }
        return _web;
    }

    private void BrowserFailed(Exception? e)
    {
        BrowserHost.Content = _web = null;
        BrowserMessage.Text = e is WebView2RuntimeNotFoundException
            ? "The browser needs the Microsoft Edge WebView2 Runtime, which this PC doesn't have. Install it from Microsoft (search \"WebView2 Runtime\"), then reopen the browser."
            : $"The browser could not start: {e?.Message}";
        BrowserMessage.Visibility = Visibility.Visible;
    }

    private void RefreshBrowserButtons()
    {
        BrowserBack.IsEnabled = _web?.CanGoBack == true;
        BrowserForward.IsEnabled = _web?.CanGoForward == true;
    }

    private void BrowserBack_Click(object sender, RoutedEventArgs e) => _web?.GoBack();

    private void BrowserForward_Click(object sender, RoutedEventArgs e) => _web?.GoForward();

    private void BrowserReload_Click(object sender, RoutedEventArgs e) => _web?.Reload();

    private void BrowserExternal_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserUrl(_web?.Source?.AbsoluteUri ?? BrowserAddress.Text) is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void BrowserAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Navigate(BrowserAddress.Text);
    }

    private void BrowserFollow_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrowserFollow = BrowserFollowToggle.IsChecked == true;
        _settings.Save();
    }

    // A link in a reply: opens in the browser.
    private void OpenLink(string url) => OpenBrowser(url);

    // A model's web_search or fetch_page: shown in the open browser when it follows the model.
    private void BrowserFollowTool(string tool, string args)
    {
        if (!BrowserPanel.IsVisible || !_settings.BrowserFollow || tool is not ("web_search" or "fetch_page"))
            return;
        try
        {
            using var doc = JsonDocument.Parse(args);
            var key = tool == "fetch_page" ? "url" : "query";
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text)
                Navigate(tool == "fetch_page" ? text : SearchUrl(text));
        }
        catch (JsonException) { }
    }

    private static string SearchUrl(string words) => BrowserHome + "?q=" + Uri.EscapeDataString(words);

    // What the address box (or a link) leads to: an http(s) address as is, a bare domain over https, other words a search.
    // Null for nothing, or another scheme (file:, javascript:...).
    internal static string? BrowserUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
            return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        return !text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var site) ? site.AbsoluteUri : SearchUrl(text);
    }

    internal static void BrowserSelfTest()
    {
        if (BrowserUrl("https://example.org/a?b=1") != "https://example.org/a?b=1" || BrowserUrl("example.org") != "https://example.org/" ||
            BrowserUrl("what is wpf") != BrowserHome + "?q=what%20is%20wpf" || BrowserUrl("javascript:alert(1)") is not null ||
            BrowserUrl(@"file:///C:/x") is not null || BrowserUrl("  ") is not null || BrowserUrl("localhost") != BrowserHome + "?q=localhost")
            throw new InvalidOperationException("BrowserUrl");
    }
}
