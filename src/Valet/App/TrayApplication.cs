using System.Diagnostics;
using System.Windows.Forms;
using Valet.Kodi;
using Valet.Logging;
using Valet.Power;

namespace Valet.App;

internal sealed class TrayApplication : IDisposable
{
    private readonly Config _config;
    private readonly PowerActions _power;
    private readonly KodiController _kodi;
    private readonly NotifyIcon _notifyIcon;
    private readonly ApplicationContext _context;
    private SettingsForm? _settingsForm;

    public TrayApplication(Config config, PowerActions power, KodiController kodi)
    {
        _config = config;
        _power = power;
        _kodi = kodi;
        _context = new ApplicationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Valet").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Launch Kodi", image: null, (_, _) => _kodi.Launch());
        menu.Items.Add("Launch Steam (Big Picture)", image: null, (_, _) => SteamLauncher.LaunchBigPicture(_config.SteamPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sleep now", image: null, (_, _) => _power.SuspendNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", image: null, (_, _) => OpenSettings());
        menu.Items.Add("Open log folder", image: null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", image: null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            Icon = AppResources.LoadTrayIcon(),
            Text = "Valet",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    public ApplicationContext MessageLoopContext => _context;

    private void OpenSettings()
    {
        if (_settingsForm is not null && !_settingsForm.IsDisposed)
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_config);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private static void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Log.Folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("OpenLogFolder", ex);
        }
    }

    private void ExitApplication()
    {
        _notifyIcon.Visible = false;
        _context.ExitThread();
    }

    public void Dispose()
    {
        _notifyIcon.Dispose();
        _context.Dispose();
        _settingsForm?.Dispose();
    }
}
