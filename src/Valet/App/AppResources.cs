using System.Drawing;
using System.IO;
using System.Reflection;

namespace Valet.App;

internal static class AppResources
{
    private static readonly Assembly Asm = typeof(AppResources).Assembly;

    public static Icon LoadTrayIcon()
    {
        using var s = Asm.GetManifestResourceStream("Valet.tray.ico")
            ?? throw new InvalidOperationException("Embedded resource 'Valet.tray.ico' not found");
        return new Icon(s);
    }

    public static Image LoadCoin128()
    {
        using var s = Asm.GetManifestResourceStream("Valet.coin128.png")
            ?? throw new InvalidOperationException("Embedded resource 'Valet.coin128.png' not found");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return Image.FromStream(ms);
    }
}
