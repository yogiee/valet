using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using Valet.Logging;
using Valet.Update;

namespace Valet.App;

internal sealed class SettingsForm : Form
{
    private readonly Config _config;

    // Launcher tab
    private CheckBox _launchAtLogon = null!;
    private TextBox _kodiPath = null!;
    private TextBox _kodiHttpUser = null!;
    private TextBox _kodiHttpPass = null!;
    private TextBox _steamPath = null!;
    private NumericUpDown _bootDelay = null!;
    private NumericUpDown _wakeDelay = null!;

    // Server tab
    private NumericUpDown _httpPort = null!;
    private TextBox _allowedCidr = null!;
    private TextBox _authToken = null!;

    // Auto Update tab
    private CheckBox _autoUpdateCheck = null!;
    private ComboBox _autoUpdateChannel = null!;

    public SettingsForm(Config config)
    {
        _config = config;
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        Text = "Valet Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(620, 480);
        try { Icon = AppResources.LoadTrayIcon(); } catch { /* fall back to system */ }

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(outer);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6),
        };
        tabs.TabPages.Add(BuildLauncherTab());
        tabs.TabPages.Add(BuildServerTab());
        tabs.TabPages.Add(BuildAutoUpdateTab());
        tabs.TabPages.Add(BuildAboutTab());
        outer.Controls.Add(tabs, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 8, 16, 16),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var save = NewButton("Save", OnSave);
        save.MinimumSize = new Size(96, 30);
        var cancel = NewButton("Cancel", (_, _) => Close());
        cancel.MinimumSize = new Size(96, 30);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        outer.Controls.Add(buttons, 0, 1);

        AcceptButton = save;
        CancelButton = cancel;
    }

    // ---------- Tabs ----------

    private TabPage BuildLauncherTab()
    {
        var page = NewTab("Launcher");
        var grid = NewGrid();
        var row = 0;

        _launchAtLogon = new CheckBox { Text = "Launch Valet at Windows logon", AutoSize = true };
        AddRow(grid, ref row, string.Empty, _launchAtLogon);
        AddSpacer(grid, ref row);

        _kodiPath = new TextBox();
        AddRow(grid, ref row, "Kodi exe:", _kodiPath, NewButton("Browse…", OnBrowseKodi));

        _kodiHttpUser = new TextBox();
        AddRow(grid, ref row, "Kodi HTTP user:", _kodiHttpUser, stretch: true);

        _kodiHttpPass = new TextBox { UseSystemPasswordChar = true };
        AddRow(grid, ref row, "Kodi HTTP password:", _kodiHttpPass, stretch: true);

        _steamPath = new TextBox();
        AddRow(grid, ref row, "Steam exe:", _steamPath, NewButton("Browse…", OnBrowseSteam));

        AddSpacer(grid, ref row);

        _bootDelay = NewNumeric(0, 60);
        AddRow(grid, ref row, "Boot delay (s):", _bootDelay);

        _wakeDelay = NewNumeric(0, 60);
        AddRow(grid, ref row, "Wake delay (s):", _wakeDelay);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildServerTab()
    {
        var page = NewTab("Server");
        var grid = NewGrid();
        var row = 0;

        _httpPort = NewNumeric(1, 65535);
        AddRow(grid, ref row, "HTTP port:", _httpPort);

        _allowedCidr = new TextBox();
        AddRow(grid, ref row, "Allowed CIDR:", _allowedCidr, stretch: true);

        AddSpacer(grid, ref row);

        _authToken = new TextBox();
        AddRow(grid, ref row, "Auth token:", _authToken, NewButton("Regenerate", OnRegenerateToken));

        AddSpacer(grid, ref row);

        var help = new Label
        {
            Text = "Status endpoints (/ and /status) are public on the LAN; sleep, " +
                   "sleep/cancel and notify require the token via X-Auth-Token header " +
                   "or /<token>/<path> URL.",
            AutoSize = false,
            Height = 60,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.TopLeft,
        };
        grid.SetColumnSpan(help, 2);
        grid.Controls.Add(help, 0, row);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildAutoUpdateTab()
    {
        var page = NewTab("Auto Update");
        var grid = NewGrid();
        var row = 0;

        _autoUpdateCheck = new CheckBox
        {
            Text = "Check for updates on Valet startup",
            AutoSize = true,
        };
        AddRow(grid, ref row, string.Empty, _autoUpdateCheck);

        _autoUpdateChannel = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
        };
        _autoUpdateChannel.Items.AddRange(new object[] { "stable", "beta" });
        AddRow(grid, ref row, "Update channel:", _autoUpdateChannel);

        AddSpacer(grid, ref row);

        var checkNowBtn = NewButton("Check for updates now", async (sender, _) => await CheckForUpdatesNowAsync(sender));
        checkNowBtn.AutoSize = true;
        AddRow(grid, ref row, string.Empty, checkNowBtn);

        AddSpacer(grid, ref row);

        var note = new Label
        {
            Text = "Updates are pulled from GitHub Releases. When a new version is published, " +
                   "Valet shows a toast notification — open this tab and click \"Check for updates now\" " +
                   "to install. The installer needs admin rights, so Windows will show a UAC prompt " +
                   "you'll need to approve.",
            AutoSize = false,
            Height = 64,
            ForeColor = SystemColors.GrayText,
        };
        grid.SetColumnSpan(note, 2);
        grid.Controls.Add(note, 0, row);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = NewTab("About");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            AutoSize = true,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pic = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(128, 128),
            Margin = new Padding(0, 0, 20, 0),
        };
        try { pic.Image = AppResources.LoadCoin128(); }
        catch (Exception ex) { Log.Warn($"About icon failed: {ex.Message}"); }
        root.Controls.Add(pic, 0, 0);
        root.SetRowSpan(pic, 5);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        var name = new Label { Text = "Valet", Font = new Font("Segoe UI Semibold", 18F), AutoSize = true };
        root.Controls.Add(name, 1, 0);

        var ver = new Label { Text = $"Version {version}", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 12) };
        root.Controls.Add(ver, 1, 1);

        var blurb = new Label
        {
            Text = "A focused HTPC companion for Windows 11.\n" +
                   "Launches Kodi, hands off to Steam Big Picture, and answers Home Assistant on the LAN.",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Margin = new Padding(3, 0, 3, 16),
        };
        root.Controls.Add(blurb, 1, 2);

        var links = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        };
        links.Controls.Add(NewLink("GitHub", (_, _) => OpenUrl("https://github.com/yogiee/valet")));
        links.Controls.Add(NewLink("Open log folder", (_, _) => OpenFolder(Log.Folder)));
        links.Controls.Add(NewLink("Open config folder", (_, _) => OpenFolder(Config.ConfigDir)));
        root.Controls.Add(links, 1, 3);

        var copyright = new Label
        {
            Text = "© Yogi Gharat (yogiee)",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 16, 3, 3),
        };
        root.Controls.Add(copyright, 1, 4);

        page.Controls.Add(root);
        return page;
    }

    // ---------- Builders ----------

    private static TabPage NewTab(string title) =>
        new() { Text = title, Padding = new Padding(0), UseVisualStyleBackColor = true };

    private static TableLayoutPanel NewGrid()
    {
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return g;
    }

    private static void AddRow(TableLayoutPanel grid, ref int row, string label, Control control, Button? extra = null, bool stretch = false)
    {
        var fullRow = string.IsNullOrEmpty(label);

        if (!fullRow)
        {
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 12, 6),
            };
            grid.Controls.Add(lbl, 0, row);
        }

        control.Margin = new Padding(0, 4, 0, 4);

        Control toPlace;
        if (extra is not null)
        {
            var combo = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 4),
            };
            combo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            combo.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            control.Margin = new Padding(0);
            control.Dock = DockStyle.Fill;
            extra.Margin = new Padding(8, 0, 0, 0);
            combo.Controls.Add(control, 0, 0);
            combo.Controls.Add(extra, 1, 0);
            toPlace = combo;
        }
        else
        {
            control.Anchor = stretch
                ? AnchorStyles.Left | AnchorStyles.Right
                : AnchorStyles.Left;
            toPlace = control;
        }

        if (fullRow)
        {
            grid.SetColumnSpan(toPlace, 2);
            grid.Controls.Add(toPlace, 0, row);
        }
        else
        {
            grid.Controls.Add(toPlace, 1, row);
        }

        row++;
    }

    private static void AddSpacer(TableLayoutPanel grid, ref int row, int height = 10)
    {
        var s = new Panel { Height = height, Margin = new Padding(0) };
        grid.SetColumnSpan(s, 2);
        grid.Controls.Add(s, 0, row);
        row++;
    }

    private static Button NewButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 26) };
        b.Click += onClick;
        return b;
    }

    private static NumericUpDown NewNumeric(int min, int max) =>
        new() { Minimum = min, Maximum = max, Width = 80 };

    private static LinkLabel NewLink(string text, EventHandler onClick)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        l.LinkClicked += (s, _) => onClick(s, EventArgs.Empty);
        return l;
    }

    // ---------- Load / Save ----------

    private void LoadValues()
    {
        _launchAtLogon.Checked = AutostartTask.IsInstalled();
        _kodiPath.Text = _config.KodiPath;
        _kodiHttpUser.Text = _config.KodiHttpUsername;
        _kodiHttpPass.Text = _config.KodiHttpPassword;
        _steamPath.Text = _config.SteamPath;
        _bootDelay.Value = ClampToRange(_config.BootDelaySec, _bootDelay);
        _wakeDelay.Value = ClampToRange(_config.WakeDelaySec, _wakeDelay);
        _httpPort.Value = ClampToRange(_config.HttpPort, _httpPort);
        _allowedCidr.Text = _config.AllowedCidr;
        _authToken.Text = _config.AuthToken;
        _autoUpdateCheck.Checked = _config.AutoUpdateCheckOnStartup;
        var idx = _autoUpdateChannel.Items.IndexOf(_config.AutoUpdateChannel);
        _autoUpdateChannel.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static decimal ClampToRange(int v, NumericUpDown nud)
    {
        if (v < nud.Minimum) return nud.Minimum;
        if (v > nud.Maximum) return nud.Maximum;
        return v;
    }

    private void OnBrowseKodi(object? sender, EventArgs e) =>
        Browse(_kodiPath, "kodi.exe", "Locate kodi.exe");

    private void OnBrowseSteam(object? sender, EventArgs e) =>
        Browse(_steamPath, "steam.exe", "Locate steam.exe");

    private void Browse(TextBox target, string fileName, string title)
    {
        using var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = $"{fileName}|{fileName}|All executables|*.exe",
            FileName = fileName,
            CheckFileExists = true,
        };
        var dir = Path.GetDirectoryName(target.Text);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;

        if (dlg.ShowDialog(this) == DialogResult.OK)
            target.Text = dlg.FileName;
    }

    private void OnRegenerateToken(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Regenerate the auth token?\n\nHome Assistant will need to be updated with the new token before it can call Valet again.",
            "Valet", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        _authToken.Text = Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("OpenFolder", ex);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("OpenUrl", ex);
        }
    }

    private async Task CheckForUpdatesNowAsync(object? sender)
    {
        var btn = sender as Button;
        var prevText = btn?.Text;
        if (btn is not null) { btn.Enabled = false; btn.Text = "Checking…"; }

        try
        {
            // Persist channel choice before checking, so the UI selection actually matters.
            _config.AutoUpdateChannel = _autoUpdateChannel.SelectedItem?.ToString() ?? "stable";

            using var checker = new UpdateChecker(_config);
            var result = await checker.CheckAsync().ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    MessageBox.Show(this,
                        $"You're on the latest version ({result.CurrentVersion}).",
                        "Valet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;

                case UpdateStatus.NoReleases:
                    MessageBox.Show(this,
                        "No releases on GitHub yet — there's nothing to update to.",
                        "Valet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;

                case UpdateStatus.Error:
                    MessageBox.Show(this,
                        $"Update check failed:\n\n{result.ErrorMessage}",
                        "Valet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;

                case UpdateStatus.Available:
                    var prompt = MessageBox.Show(this,
                        $"Valet {result.LatestTag} is available (you have {result.CurrentVersion}).\n\n" +
                        "Download and install now? Valet will close, install the new version, " +
                        "and restart automatically.",
                        "Valet", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (prompt != DialogResult.Yes) return;

                    if (btn is not null) btn.Text = "Downloading…";
                    var installerPath = await checker.DownloadAndInstallAsync(result).ConfigureAwait(true);
                    if (installerPath is null)
                    {
                        MessageBox.Show(this,
                            "Download or installer launch failed. See log for details.",
                            "Valet", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    // On success, the installer closes Valet via Restart Manager — no further action needed.
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("CheckForUpdatesNow", ex);
            MessageBox.Show(this, $"Unexpected error: {ex.Message}", "Valet",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (btn is not null) { btn.Enabled = true; btn.Text = prevText; }
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var cidr = _allowedCidr.Text.Trim();
        try { _ = IPNetwork.Parse(cidr); }
        catch
        {
            MessageBox.Show(this, $"Invalid CIDR: \"{cidr}\". Expected e.g. 192.168.69.0/24.",
                "Valet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _allowedCidr.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_authToken.Text))
        {
            MessageBox.Show(this, "Auth token cannot be empty. Use Regenerate to make a new one.",
                "Valet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _authToken.Focus();
            return;
        }

        var oldPort = _config.HttpPort;
        var oldToken = _config.AuthToken;
        var oldCidr = _config.AllowedCidr;
        var oldBoot = _config.BootDelaySec;
        var oldWake = _config.WakeDelaySec;

        _config.KodiPath = string.IsNullOrWhiteSpace(_kodiPath.Text) ? "auto" : _kodiPath.Text.Trim();
        _config.KodiHttpUsername = _kodiHttpUser.Text.Trim();
        _config.KodiHttpPassword = _kodiHttpPass.Text; // don't trim — passwords can have spaces
        _config.SteamPath = string.IsNullOrWhiteSpace(_steamPath.Text) ? "auto" : _steamPath.Text.Trim();
        _config.LaunchOnStartup = _launchAtLogon.Checked;
        _config.BootDelaySec = (int)_bootDelay.Value;
        _config.WakeDelaySec = (int)_wakeDelay.Value;
        _config.HttpPort = (int)_httpPort.Value;
        _config.AllowedCidr = cidr;
        _config.AuthToken = _authToken.Text.Trim();
        _config.AutoUpdateCheckOnStartup = _autoUpdateCheck.Checked;
        _config.AutoUpdateChannel = _autoUpdateChannel.SelectedItem?.ToString() ?? "stable";

        try { _config.Save(); }
        catch (Exception ex)
        {
            Log.Error("Settings save", ex);
            MessageBox.Show(this, $"Failed to save config: {ex.Message}", "Valet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            if (_config.LaunchOnStartup) AutostartTask.Install();
            else AutostartTask.Uninstall();
        }
        catch (Exception ex)
        {
            Log.Warn($"AutostartTask update failed: {ex.Message}");
            MessageBox.Show(this,
                $"Settings saved, but the startup task could not be updated:\n\n{ex.Message}\n\n" +
                "Creating a logon task with highest privileges typically requires administrator rights.",
                "Valet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var restartNeeded =
            _config.HttpPort != oldPort ||
            _config.AuthToken != oldToken ||
            _config.AllowedCidr != oldCidr ||
            _config.BootDelaySec != oldBoot ||
            _config.WakeDelaySec != oldWake;

        if (restartNeeded)
        {
            MessageBox.Show(this,
                "Some settings (port, token, CIDR, delays) take effect after the next Valet restart.\n\n" +
                "Use the tray menu → Exit and relaunch when convenient.",
                "Valet", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
