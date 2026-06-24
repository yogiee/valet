using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valet.App;

internal sealed class Config
{
    public string KodiPath { get; set; } = "auto";
    public string SteamPath { get; set; } = "auto";
    public bool LaunchOnStartup { get; set; } = true;
    public int BootDelaySec { get; set; } = 4;
    public int WakeDelaySec { get; set; } = 4;
    public int HttpPort { get; set; } = 5009;
    public string AuthToken { get; set; } = "";
    public string AllowedCidr { get; set; } = "192.168.69.0/24";
    public bool AutoUpdateCheckOnStartup { get; set; } = true;
    public string AutoUpdateChannel { get; set; } = "stable"; // stable | beta

    public bool OsdEnabled { get; set; } = true;
    public string OsdPosition { get; set; } = "bottom-right"; // top-center | bottom-center | top-right | bottom-right
    public int OsdTimeoutMs { get; set; } = 2000;
    public double OsdScale { get; set; } = 1.0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Valet");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static Config Load()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(ConfigPath))
        {
            var fresh = new Config { AuthToken = GenerateToken() };
            fresh.Save();
            return fresh;
        }

        var json = File.ReadAllText(ConfigPath);
        var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts) ?? new Config();

        if (string.IsNullOrWhiteSpace(cfg.AuthToken))
        {
            cfg.AuthToken = GenerateToken();
            cfg.Save();
        }

        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    private static string GenerateToken()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
