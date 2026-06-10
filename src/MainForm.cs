using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WinEML;

internal enum ViewMode { Html, Text, Source }

internal sealed class MainForm : Form
{
    // --- UI ---
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _btnOpen = new();
    private readonly ToolStripButton _btnPrev = new();
    private readonly ToolStripButton _btnNext = new();
    private readonly ToolStripLabel _navLabel = new();
    private readonly ToolStripButton _btnHtml = new();
    private readonly ToolStripButton _btnText = new();
    private readonly ToolStripButton _btnSource = new();
    private readonly ToolStripDropDownButton _btnTools = new();
    private readonly HeaderView _headerView = new();
    private readonly Panel _bodyHost = new();
    private readonly TextBox _textView = new();
    private readonly WebView2 _webView = new();
    private readonly Panel _attachBar = new();
    private readonly FlowLayoutPanel _attachFlow = new();

    // --- state ---
    private EmlDocument? _doc;
    private string? _currentPath;
    private string[] _siblings = Array.Empty<string>();
    private int _index = -1;
    private ViewMode _mode = ViewMode.Text;
    private bool _webViewReady;
    private int _loadGen;
    private string? _viewUri;
    private string? _viewHtml;

    // Rendered bodies are served straight from memory under this private origin
    // (.invalid is a reserved, never-resolvable TLD) and never touch the disk.
    // Only the exact single-use per-render URI is ever answered.
    private const string ViewOrigin = "https://wineml.invalid/";

    public MainForm(string? initialPath)
    {
        BuildUi();

        // Kick off the (slow) WebView2 engine init immediately so it overlaps
        // with parsing + first paint instead of stacking on top of them.
        _ = InitWebViewAsync();

        if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
            LoadFile(initialPath!);
        else
            ShowEmptyState();
    }

    // ---------------------------------------------------------------- UI build

    private void BuildUi()
    {
        Text = "WinEML";
        Width = 1000;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        BackColor = Color.White;
        try { Icon = SystemIcons.Application; } catch { /* non-fatal */ }

        // Toolbar
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.RenderMode = ToolStripRenderMode.System;
        _btnOpen.Text = "Open";
        _btnOpen.Click += (_, _) => OpenViaDialog();
        _btnPrev.Text = "◀ Prev";
        _btnPrev.Click += (_, _) => Navigate(-1);
        _btnNext.Text = "Next ▶";
        _btnNext.Click += (_, _) => Navigate(+1);
        _navLabel.Margin = new Padding(8, 1, 8, 2);
        _btnHtml.Text = "HTML";
        _btnHtml.Click += (_, _) => SetMode(ViewMode.Html, userInitiated: true);
        _btnText.Text = "Text";
        _btnText.Click += (_, _) => SetMode(ViewMode.Text, userInitiated: true);
        _btnSource.Text = "Source";
        _btnSource.Click += (_, _) => SetMode(ViewMode.Source, userInitiated: true);

        _btnTools.Text = "Tools ▾";
        _btnTools.ShowDropDownArrow = false;
        _btnTools.Alignment = ToolStripItemAlignment.Right;
        var miSetDefault = new ToolStripMenuItem("Set WinEML as default for .eml…");
        miSetDefault.Click += (_, _) => SetAsDefault();
        var miRemove = new ToolStripMenuItem("Remove .eml association");
        miRemove.Click += (_, _) => RemoveAssociation();
        _btnTools.DropDownItems.Add(miSetDefault);
        _btnTools.DropDownItems.Add(miRemove);

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _btnOpen, new ToolStripSeparator(),
            _btnPrev, _btnNext, _navLabel, new ToolStripSeparator(),
            _btnHtml, _btnText, _btnSource,
            _btnTools,
        });

        // Header block (custom-painted, instant, AOT-safe)
        _headerView.Dock = DockStyle.Top;

        // Body host with text + web views stacked
        _bodyHost.Dock = DockStyle.Fill;
        _textView.ReadOnly = true;
        _textView.Multiline = true;
        _textView.BorderStyle = BorderStyle.None;
        _textView.Dock = DockStyle.Fill;
        _textView.Font = new Font("Consolas", 10f);
        _textView.WordWrap = true;
        _textView.ScrollBars = ScrollBars.Vertical;
        _textView.MaxLength = 0; // no length cap — show the whole message
        _textView.BackColor = Color.White;
        _webView.Dock = DockStyle.Fill;
        _webView.Visible = false;
        _bodyHost.Controls.Add(_textView);
        _bodyHost.Controls.Add(_webView);

        // Attachment bar (hidden until there are attachments)
        _attachBar.Dock = DockStyle.Bottom;
        _attachBar.Height = 34;
        _attachBar.BackColor = Color.FromArgb(240, 240, 243);
        _attachBar.Visible = false;
        _attachFlow.Dock = DockStyle.Fill;
        _attachFlow.AutoScroll = true;
        _attachFlow.WrapContents = false;
        _attachFlow.Padding = new Padding(4, 4, 4, 4);
        _attachBar.Controls.Add(_attachFlow);

        Controls.Add(_bodyHost);
        Controls.Add(_attachBar);
        Controls.Add(_headerView);
        Controls.Add(_toolbar);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Shown += (_, _) => Telemetry.Mark("window-shown");

        // In bench mode, never let a run hang the harness.
        if (Telemetry.Bench) BenchExitAfter(10_000);
    }

    private void BenchExitAfter(int ms)
    {
        if (!Telemetry.Bench) return;
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); Close(); };
        t.Start();
    }

    // ------------------------------------------------------------ WebView init

    // Opt-in diagnostics: set WINEML_DEBUG=1 to trace startup to %TEMP%\WinEML\debug.log.
    private static readonly bool DiagEnabled =
        Environment.GetEnvironmentVariable("WINEML_DEBUG") is "1" or "true";

    private static void Diag(string msg)
    {
        if (!DiagEnabled) return;
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "WinEML", "debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    private async Task InitWebViewAsync()
    {
        Diag("InitWebViewAsync start");
        try
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinEML", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            var s = core.Settings;
            s.IsScriptEnabled = false;          // emails have no business running JS
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true; // keep copy/select-all

            // Block every remote resource (trackers, remote images, web fonts).
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += (_, e) =>
            {
                Telemetry.Mark("html-rendered", e.IsSuccess ? "ok" : $"failed:{e.WebErrorStatus}");
                Diag($"NavigationCompleted success={e.IsSuccess} status={e.WebErrorStatus}");
                BenchExitAfter(120); // small settle so the paint is real
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };

            _webViewReady = true;
            Telemetry.Mark("webview-ready");
            Diag($"WebView2 ready; mode={_mode} hasHtml={_doc?.HtmlBody is not null}");

            // If we were already asked to show HTML, render it now.
            if (_mode == ViewMode.Html && _doc?.HtmlBody is not null)
                SetMode(ViewMode.Html, userInitiated: false);
        }
        catch (Exception ex)
        {
            Diag("WebView2 init FAILED: " + ex);
            // No WebView2 runtime → degrade gracefully to text/source only.
            _webViewReady = false;
            BeginInvoke(() =>
            {
                _btnHtml.Enabled = false;
                if (_mode == ViewMode.Html) SetMode(ViewMode.Text, userInitiated: false);
            });
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return; // embedded inline content — allowed

        var env = _webView.CoreWebView2.Environment;
        if (_viewHtml is not null && _viewUri is not null &&
            string.Equals(uri, _viewUri, StringComparison.OrdinalIgnoreCase))
        {
            // Our own rendered body, served from memory. The CSP response *header*
            // governs the document no matter how mangled the email's markup is —
            // unlike the injected meta tag, which stays as defense-in-depth.
            var body = new MemoryStream(Encoding.UTF8.GetBytes(_viewHtml));
            e.Response = env.CreateWebResourceResponse(body, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Security-Policy: " + EmlDocument.CspPolicy);
            return;
        }

        // Block everything else: remote http/https, local files, and any
        // sub-resource the email reaches via a relative URL on our origin.
        e.Response = env.CreateWebResourceResponse(null, 403, "Blocked by WinEML", string.Empty);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Our own renders (and the engine's blank bootstrap) are allowed through.
        if (_viewUri is not null && string.Equals(e.Uri, _viewUri, StringComparison.OrdinalIgnoreCase))
            return;
        if (e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;

        // A relative link in the email resolves against our private origin —
        // nothing real lives there, so swallow it rather than open a browser tab.
        if (e.Uri.StartsWith(ViewOrigin, StringComparison.OrdinalIgnoreCase))
            return;

        // The user clicked a real link. Open it in the real browser.
        OpenExternal(e.Uri);
    }

    // ---------------------------------------------------------------- loading

    private async void LoadFile(string path)
    {
        // Each load gets a generation id; a slower earlier load that finishes after
        // a newer one must not stomp the newer content. (Critical for a viewer —
        // showing the wrong message would be a trust failure.)
        int gen = ++_loadGen;
        try
        {
            Text = Path.GetFileName(path) + " — WinEML";
            ComputeSiblings(path);
            var doc = await Task.Run(() => EmlDocument.Load(path));
            if (gen != _loadGen) return; // superseded by a newer open/navigate

            Telemetry.Mark("parsed", Path.GetFileName(path));
            _currentPath = path;
            _doc = doc;
            _headerView.SetMessage(doc);
            PopulateAttachments(doc);
            UpdateNavLabel();

            // HTML is preferred when present; falls back to text until the
            // engine is ready, then auto-upgrades.
            SetMode(doc.HtmlBody is not null ? ViewMode.Html : ViewMode.Text,
                    userInitiated: false);
            Telemetry.Mark("body-shown");

            // Text-only messages have no navigation event to close on.
            if (Telemetry.Bench && doc.HtmlBody is null) BenchExitAfter(200);
        }
        catch (Exception ex)
        {
            if (gen != _loadGen) return;
            // Point at the failing file (so F5 retries it and Source shows its raw
            // bytes, which often still open) and clear every remnant of the
            // previously shown message — stale headers/attachments next to an
            // error would misattribute them to this file.
            _currentPath = path;
            _doc = null;
            _headerView.Clear();
            _attachFlow.Controls.Clear();
            _attachBar.Visible = false;
            _webView.Visible = false;
            _textView.Visible = true;
            _textView.BringToFront();
            _textView.Text = $"Failed to open:\r\n{path}\r\n\r\n{ex.Message}";
            UpdateNavLabel();
            UpdateToggleButtons();
        }
    }

    private void ShowEmptyState()
    {
        _textView.Visible = true;
        _textView.Text =
            "WinEML — fast .eml viewer\r\n\r\n" +
            "• Open a file:  Ctrl+O,  or drag an .eml here\r\n" +
            "• Browse a folder:  ◀ / ▶  (Ctrl+Left / Ctrl+Right)\r\n" +
            "• Switch view:  HTML / Text / Source\r\n";
        UpdateNavLabel();
    }

    private void PopulateAttachments(EmlDocument doc)
    {
        _attachFlow.Controls.Clear();
        if (doc.Attachments.Count == 0)
        {
            _attachBar.Visible = false;
            return;
        }

        foreach (var att in doc.Attachments)
        {
            var btn = new Button
            {
                Text = $"📎 {att.FileName}  ({FormatSize(att.Size)})",
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(2, 2, 2, 2),
                Tag = att,
            };
            btn.Click += (_, _) => SaveAttachment(att);
            _attachFlow.Controls.Add(btn);
        }
        _attachBar.Visible = true;
    }

    // ------------------------------------------------------------- view modes

    private void SetMode(ViewMode mode, bool userInitiated)
    {
        if (_doc is null && mode != ViewMode.Source) { return; }
        _mode = mode;

        switch (mode)
        {
            case ViewMode.Html:
                if (_doc?.HtmlBody is null) { SetMode(ViewMode.Text, userInitiated); return; }
                if (!_webViewReady)
                {
                    // Interim: show text instantly; auto-upgrades when engine is ready.
                    _textView.Font = new Font("Consolas", 10f);
                    _textView.Text = string.IsNullOrEmpty(_doc.TextBody)
                        ? "Rendering rich content…" : _doc.TextBody;
                    _textView.Visible = true;
                    _textView.BringToFront();
                    _webView.Visible = false;
                }
                else
                {
                    RenderHtml(_doc.HtmlBody!);
                    _webView.Visible = true;
                    _webView.BringToFront();
                    _textView.Visible = false;
                }
                break;

            case ViewMode.Text:
                _textView.Font = new Font("Consolas", 10f);
                _textView.Text = NormalizeNewlines(_doc?.TextBody ?? string.Empty);
                if (_textView.TextLength == 0 && _doc?.HtmlBody is not null)
                    _textView.Text = "(This message has no plain-text part — view as HTML.)";
                _textView.Visible = true;
                _textView.BringToFront();
                _textView.Select(0, 0);
                _webView.Visible = false;
                break;

            case ViewMode.Source:
                _textView.Font = new Font("Consolas", 9.5f);
                _textView.Visible = true;
                _textView.BringToFront();
                _webView.Visible = false;
                LoadSourceAsync(_currentPath);
                break;
        }
        UpdateToggleButtons();
    }

    private void RenderHtml(string html)
    {
        // Serve from memory under a fresh single-use URI; OnWebResourceRequested
        // answers it with the body plus the authoritative CSP header. No body of
        // any size ever touches the disk.
        _viewHtml = html;
        _viewUri = $"{ViewOrigin}view/{Guid.NewGuid():N}";
        Diag($"RenderHtml len={html.Length} -> {_viewUri}");
        _webView.CoreWebView2.Navigate(_viewUri);
    }

    private async void LoadSourceAsync(string? path)
    {
        int gen = _loadGen;
        if (path is null || !File.Exists(path)) { _textView.Text = string.Empty; return; }
        try
        {
            string raw = await Task.Run(() => File.ReadAllText(path));
            if (gen != _loadGen || _mode != ViewMode.Source) return; // navigated/switched away
            _textView.Text = NormalizeNewlines(raw);
            _textView.Select(0, 0);
        }
        catch (Exception ex)
        {
            if (gen == _loadGen && _mode == ViewMode.Source)
                _textView.Text = $"Could not read source:\r\n{ex.Message}";
        }
    }

    private void UpdateToggleButtons()
    {
        _btnHtml.Enabled = _doc?.HtmlBody is not null;
        _btnHtml.Checked = _mode == ViewMode.Html;
        _btnText.Checked = _mode == ViewMode.Text;
        _btnSource.Checked = _mode == ViewMode.Source;
        bool hasFiles = _siblings.Length > 1;
        _btnPrev.Enabled = hasFiles;
        _btnNext.Enabled = hasFiles;
    }

    // ------------------------------------------------------------- navigation

    private void ComputeSiblings(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is null) { _siblings = new[] { path }; _index = 0; return; }
            _siblings = Directory.GetFiles(dir, "*.eml", SearchOption.TopDirectoryOnly);
            Array.Sort(_siblings, StringComparer.OrdinalIgnoreCase);
            _index = Array.FindIndex(_siblings,
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (_index < 0) { _siblings = new[] { path }; _index = 0; }
        }
        catch
        {
            _siblings = new[] { path };
            _index = 0;
        }
    }

    private void Navigate(int delta)
    {
        if (_siblings.Length <= 1) return;
        int next = _index + delta;
        if (next < 0 || next >= _siblings.Length) return; // stop at the ends
        _index = next;
        LoadFile(_siblings[_index]);
    }

    private void UpdateNavLabel()
    {
        _navLabel.Text = _siblings.Length > 1
            ? $"{_index + 1} / {_siblings.Length}"
            : (_currentPath is null ? "" : "1 / 1");
    }

    // ------------------------------------------------------------- file open

    private void OpenViaDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "E-mail files (*.eml)|*.eml|All files (*.*)|*.*",
            Title = "Open .eml file",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            LoadFile(dlg.FileName);
    }

    private void SetAsDefault()
    {
        try
        {
            FileAssociation.RegisterCore();
            if (_currentPath is not null && File.Exists(_currentPath))
            {
                // Pop Windows' own chooser; ticking WinEML there sets the default
                // the only way the OS permits.
                FileAssociation.ShowOpenWith(Handle, _currentPath);
            }
            else
            {
                MessageBox.Show(this,
                    "WinEML is now registered as an option for .eml files.\n\n" +
                    "To set it as the default, open an .eml file here and use this menu " +
                    "again (you'll get Windows' chooser), or right-click any .eml → " +
                    "Open with → WinEML, and tick \"Always use this app\".",
                    "WinEML", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WinEML",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveAssociation()
    {
        try
        {
            FileAssociation.UnregisterCore();
            MessageBox.Show(this, "WinEML has been removed as an .eml handler.",
                "WinEML", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WinEML",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveAttachment(EmlAttachment att)
    {
        using var dlg = new SaveFileDialog { FileName = att.FileName, Title = "Save attachment" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try { att.SaveTo(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    private static void OpenExternal(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
              uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
            return; // ignore javascript:, file:, etc.
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* user has no handler — nothing to do */ }
    }

    // ------------------------------------------------------------- shortcuts

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.O: OpenViaDialog(); return true;
            case Keys.Control | Keys.Left: Navigate(-1); return true;
            case Keys.Control | Keys.Right: Navigate(+1); return true;
            case Keys.Control | Keys.Home: if (_siblings.Length > 1) { _index = 0; LoadFile(_siblings[0]); } return true;
            case Keys.Control | Keys.End: if (_siblings.Length > 1) { _index = _siblings.Length - 1; LoadFile(_siblings[_index]); } return true;
            case Keys.F5: if (_currentPath is not null) LoadFile(_currentPath); return true;
            case Keys.Alt | Keys.H: SetMode(ViewMode.Html, true); return true;
            case Keys.Alt | Keys.T: SetMode(ViewMode.Text, true); return true;
            case Keys.Alt | Keys.U: SetMode(ViewMode.Source, true); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------- utilities

    private static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {units[u]}" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>
/// Lightweight owner-drawn header: bold subject + labelled fields. Avoids
/// RichTextBox (whose OLE/COM callback is incompatible with AOT) and paints
/// instantly with no control overhead.
/// </summary>
internal sealed class HeaderView : Control
{
    private string _subject = string.Empty;
    private (string Label, string Value)[] _fields = Array.Empty<(string, string)>();

    private static readonly Font SubjectFont = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font LabelFont = new("Segoe UI Semibold", 9f);
    private static readonly Font ValueFont = new("Segoe UI", 9.5f);
    private static readonly Color LabelColor = Color.FromArgb(120, 120, 128);
    private static readonly Color ValueColor = Color.FromArgb(24, 24, 28);

    private readonly ContextMenuStrip _menu = new();

    public HeaderView()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(247, 247, 249);
        Height = 96;
        var copy = new ToolStripMenuItem("Copy headers");
        copy.Click += (_, _) => CopyToClipboard();
        _menu.Items.Add(copy);
        ContextMenuStrip = _menu;
    }

    public void Clear()
    {
        _subject = string.Empty;
        _fields = Array.Empty<(string, string)>();
        Invalidate();
    }

    public void SetMessage(EmlDocument doc)
    {
        _subject = doc.Subject;
        var fields = new List<(string, string)>(5);
        if (!string.IsNullOrWhiteSpace(doc.From)) fields.Add(("From", doc.From));
        // A Reply-To that differs from From is worth surfacing — replies go
        // somewhere other than the apparent sender (a classic phishing tell).
        if (!string.IsNullOrWhiteSpace(doc.ReplyTo) &&
            !string.Equals(doc.ReplyTo, doc.From, StringComparison.OrdinalIgnoreCase))
            fields.Add(("Reply", doc.ReplyTo));
        if (!string.IsNullOrWhiteSpace(doc.To)) fields.Add(("To", doc.To));
        if (!string.IsNullOrWhiteSpace(doc.Cc)) fields.Add(("Cc", doc.Cc));
        if (!string.IsNullOrWhiteSpace(doc.Date)) fields.Add(("Date", doc.Date));
        _fields = fields.ToArray();

        int lineH = ValueFont.Height + 6;
        Height = Math.Clamp(14 + SubjectFont.Height + 4 + _fields.Length * lineH, 60, 220);
        Invalidate();
    }

    private void CopyToClipboard()
    {
        if (string.IsNullOrEmpty(_subject) && _fields.Length == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine(_subject);
        foreach (var (label, value) in _fields)
            sb.AppendLine($"{label}: {value}");
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int x = 12, y = 9;
        int width = Width - 24;

        TextRenderer.DrawText(g, _subject, SubjectFont,
            new Rectangle(x, y, width, SubjectFont.Height + 2), ValueColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        y += SubjectFont.Height + 6;

        int labelW = 44;
        int lineH = ValueFont.Height + 6;
        foreach (var (label, value) in _fields)
        {
            TextRenderer.DrawText(g, label, LabelFont,
                new Rectangle(x, y, labelW, lineH), LabelColor,
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, value, ValueFont,
                new Rectangle(x + labelW, y, width - labelW, lineH), ValueColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += lineH;
        }

        using var pen = new Pen(Color.FromArgb(224, 224, 228));
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
