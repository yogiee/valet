using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Valet.Logging;

namespace Valet.Notify;

internal static class Toast
{
    public const string Aumid = "io.github.yogiee.Valet";
    private const string AumidRegPath = @"Software\Classes\AppUserModelId\io.github.yogiee.Valet";

    private static readonly string ImageCacheDir = Path.Combine(Path.GetTempPath(), "Valet", "toast-images");
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    // Without an HKCU AppUserModelId registration, Windows won't attribute toasts to "Valet"
    // (they'd appear under PowerShell / "Notifications"). Call once at startup.
    public static void EnsureRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AumidRegPath);
            key?.SetValue("DisplayName", "Valet");
        }
        catch (Exception ex)
        {
            Log.Warn($"AUMID registry write failed: {ex.Message}");
        }

        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(Aumid);
            if (hr == 0) Log.Info($"AUMID set: {Aumid}");
            else Log.Warn($"SetCurrentProcessExplicitAppUserModelID hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log.Error("SetCurrentProcessExplicitAppUserModelID failed", ex);
        }
    }

    public static bool Show(
        string title,
        string? body,
        string? icon = null,
        string? scenario = null,
        string? image = null,
        string? imagePlacement = null)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrWhiteSpace(body))
            {
                builder.AddText(body);
            }

            // Image placements per the Microsoft schema:
            //   inline (default) — full image in body, aspect ratio preserved
            //   hero            — banner at top, forcibly cropped to ~2:1 by Windows
            //   logo            — small circular icon to the left of the text
            // https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-content
            //
            // For unpackaged Win32 apps, the ToastNotificationManager doesn't fetch http(s)
            // URIs for image src — local file paths only. So http(s) gets downloaded first.
            var localImage = ResolveImageToLocalPath(image);
            if (localImage is not null)
            {
                var imgUri = new Uri(localImage);
                switch ((imagePlacement ?? "inline").ToLowerInvariant())
                {
                    case "hero":
                        builder.AddHeroImage(imgUri);
                        break;
                    case "logo":
                    case "applogo":
                        builder.AddAppLogoOverride(imgUri, ToastGenericAppLogoCrop.Circle);
                        break;
                    default:
                        builder.AddInlineImage(imgUri);
                        break;
                }
            }

            if (string.Equals(scenario, "alarm", StringComparison.OrdinalIgnoreCase))
            {
                builder.SetToastScenario(ToastScenario.Alarm);
            }
            else if (string.Equals(scenario, "reminder", StringComparison.OrdinalIgnoreCase))
            {
                builder.SetToastScenario(ToastScenario.Reminder);
            }

            builder.Show();
            Log.Info($"Toast shown: {title}{(localImage is null ? "" : $" (image={imagePlacement ?? "inline"})")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Toast.Show failed", ex);
            return false;
        }
    }

    private static string? ResolveImageToLocalPath(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        if (!Uri.TryCreate(image, UriKind.Absolute, out var imgUri))
        {
            Log.Warn($"Toast image URI is not absolute, ignoring: {image}");
            return null;
        }

        if (imgUri.Scheme == Uri.UriSchemeFile)
        {
            return File.Exists(imgUri.LocalPath) ? imgUri.LocalPath : null;
        }

        if (imgUri.Scheme != Uri.UriSchemeHttp && imgUri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn($"Toast image URI scheme '{imgUri.Scheme}' not supported: {image}");
            return null;
        }

        try
        {
            Directory.CreateDirectory(ImageCacheDir);

            // Stable filename per URL so repeated notifications reuse the same file
            // (Windows toast renderer caches by path).
            var ext = Path.GetExtension(imgUri.LocalPath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imgUri.AbsoluteUri)))[..16].ToLowerInvariant();
            var path = Path.Combine(ImageCacheDir, $"img-{hash}{ext}");

            // Always re-download — a stable URL can still serve fresh content (camera snapshots etc.).
            var bytes = ImageHttp.GetByteArrayAsync(imgUri).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            Log.Info($"Toast image downloaded: {imgUri} → {path} ({bytes.Length} bytes)");
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Toast image download failed ({image}): {ex.Message}");
            return null;
        }
    }
}
