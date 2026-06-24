using System.Diagnostics;
using System.Windows.Forms;
using Valet.App;
using Valet.Kodi;
using Valet.Logging;
using Valet.Notify;
using Valet.Power;
using Valet.Server;

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
        using var powerEvents = new PowerEvents();
        using var lifecycle = new LifecycleStateMachine(config, kodi, steam, powerEvents);
        using var kodiRpc = new KodiJsonRpc();

        var auth = new Auth(config.AuthToken, config.AllowedCidr);
        var endpoints = new Endpoints(power, lifecycle, kodiRpc);

        using var server = new HttpServer(config.HttpPort, endpoints, auth);
        server.Start();

        using var tray = new TrayApplication(config, power);
        Application.Run(tray.MessageLoopContext);

        Log.Info("Valet stopping");
        return 0;
    }
}
