using System.Diagnostics;
using System.Windows.Forms;
using Valet.App;
using Valet.Kodi;
using Valet.Logging;
using Valet.Notify;
using Valet.Osd;
using Valet.Power;
using Valet.Server;
using Valet.Update;

namespace Valet;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Global\Valet.SingleInstance";

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);
        if (!createdNew) return 0;

        Log.Info($"Valet starting (pid={Process.GetCurrentProcess().Id})");

        Toast.EnsureRegistered();

        ApplicationConfiguration.Initialize();

        var config = Config.Load();
        Log.Info($"Config loaded from {Config.ConfigPath} (port={config.HttpPort}, cidr={config.AllowedCidr})");

        var power = new PowerActions();

        var kodi = new KodiController(() => config.KodiPath);
        using var steam = new SteamWatcher();
        using var steamGame = new SteamRunningGame();
        using var powerEvents = new PowerEvents();
        using var lifecycle = new LifecycleStateMachine(config, kodi, steam, steamGame, powerEvents);
        using var kodiRpc = new KodiJsonRpc(config);
        using var osd = new OsdController(config);

        var auth = new Auth(config.AuthToken, config.AllowedCidr);
        var endpoints = new Endpoints(power, lifecycle, kodiRpc, osd, steamGame);

        using var server = new HttpServer(config.HttpPort, endpoints, auth);
        server.Start();

        if (config.AutoUpdateCheckOnStartup)
        {
            _ = Task.Run(() => StartupUpdateCheckAsync(config));
        }

        using var tray = new TrayApplication(config, power, kodi);
        Application.Run(tray.MessageLoopContext);

        Log.Info("Valet stopping");
        return 0;
    }

    private static async Task StartupUpdateCheckAsync(Config config)
    {
        try
        {
            // Give the rest of startup time to settle, and avoid hammering GitHub at exactly logon time.
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            using var checker = new UpdateChecker(config);
            var result = await checker.CheckAsync().ConfigureAwait(false);

            switch (result.Status)
            {
                case UpdateStatus.Available:
                    Log.Info($"Update available: {result.LatestTag} (current {result.CurrentVersion}); notifying user");
                    Toast.Show(
                        $"Valet update available: {result.LatestTag}",
                        "Right-click the tray icon → Settings → Auto Update → Check for updates now to install.",
                        scenario: "reminder");
                    break;

                case UpdateStatus.UpToDate:
                    Log.Info($"Up to date ({result.CurrentVersion} == {result.LatestTag})");
                    break;

                case UpdateStatus.NoReleases:
                    Log.Info("No releases on GitHub yet — auto-update inactive");
                    break;

                case UpdateStatus.Error:
                    Log.Warn($"Startup update check error: {result.ErrorMessage}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("StartupUpdateCheck", ex);
        }
    }
}
