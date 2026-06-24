using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Valet.Logging;

namespace Valet.Notify;

internal static class Toast
{
    public const string Aumid = "io.github.yogiee.Valet";
    private const string AumidRegPath = @"Software\Classes\AppUserModelId\io.github.yogiee.Valet";

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

    public static bool Show(string title, string? body, string? icon = null, string? scenario = null, string? image = null)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title);
            if (!string.IsNullOrWhiteSpace(body))
            {
                builder.AddText(body);
            }

            // Hero image (large banner at the top of the toast) per
            // https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-schema#toastgenericheroimage
            // Accepts http(s) URLs and file:// paths. Other schemes are ignored to keep things predictable.
            if (!string.IsNullOrWhiteSpace(image)
                && Uri.TryCreate(image, UriKind.Absolute, out var imgUri)
                && (imgUri.Scheme == Uri.UriSchemeHttp ||
                    imgUri.Scheme == Uri.UriSchemeHttps ||
                    imgUri.Scheme == Uri.UriSchemeFile))
            {
                builder.AddHeroImage(imgUri);
            }
            else if (!string.IsNullOrWhiteSpace(image))
            {
                Log.Warn($"Toast image URI ignored (unsupported scheme or malformed): {image}");
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
            Log.Info($"Toast shown: {title}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Toast.Show failed", ex);
            return false;
        }
    }
}
