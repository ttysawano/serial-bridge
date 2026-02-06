using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SerialBridge;

public sealed class AppConfigFile
{
    public int Version { get; set; } = 2;
    public Dictionary<string, AppConfig> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static (AppConfig cfg, string cfgPath) LoadOrCreateForInstance(string baseDir, int instanceId)
    {
        var cfgPath = Path.Combine(baseDir, "serial-bridge.yml");
        var changed = false;

        var fileCfg = LoadFromDisk(cfgPath);
        if (fileCfg == null)
        {
            fileCfg = new AppConfigFile();
            changed = true;
        }

        fileCfg.Instances ??= new Dictionary<string, AppConfig>(StringComparer.OrdinalIgnoreCase);

        var key = instanceId.ToString();
        if (!fileCfg.Instances.TryGetValue(key, out var cfg) || cfg == null)
        {
            cfg = new AppConfig();
            fileCfg.Instances[key] = cfg;
            changed = true;
        }

        cfg.Sanitize();
        cfg.ApplyInstanceDefaults(instanceId);

        if (changed)
        {
            SaveToDisk(cfgPath, fileCfg);
        }

        return (cfg, cfgPath);
    }

    private static AppConfigFile? LoadFromDisk(string cfgPath)
    {
        if (!File.Exists(cfgPath)) return null;

        var text = File.ReadAllText(cfgPath);
        var des = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return des.Deserialize<AppConfigFile>(text);
    }

    private static void SaveToDisk(string cfgPath, AppConfigFile cfg)
    {
        var ser = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .Build();

        var text = ser.Serialize(cfg);
        File.WriteAllText(cfgPath, text);
    }
}

public sealed class AppConfig
{
    public TcpConfig Tcp { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public DeviceSelectConfig DeviceSelect { get; set; } = new();
    public ReconnectConfig Reconnect { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public void Sanitize()
    {
        Tcp ??= new TcpConfig();
        Serial ??= new SerialConfig();
        DeviceSelect ??= new DeviceSelectConfig();
        Reconnect ??= new ReconnectConfig();
        Logging ??= new LoggingConfig();
    }

    public void ApplyInstanceDefaults(int instanceId)
    {
        // Port is auto-resolved at runtime when null.
        if (string.IsNullOrWhiteSpace(DeviceSelect.StateFile))
            DeviceSelect.StateFile = "serial-bridge.state.json";

        if (string.IsNullOrWhiteSpace(Logging.HealthLog))
            Logging.HealthLog = "serial-bridge.health.jsonl";

        DeviceSelect.StateFile = ResolveInstancePath(DeviceSelect.StateFile, instanceId);
        Logging.HealthLog = ResolveInstancePath(Logging.HealthLog, instanceId);
    }

    private static string ResolveInstancePath(string path, int instanceId)
    {
        if (path.Contains("{instance}", StringComparison.OrdinalIgnoreCase))
        {
            return path.Replace("{instance}", instanceId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var dir = Path.GetDirectoryName(path);
        var ext = Path.GetExtension(path);
        var file = Path.GetFileNameWithoutExtension(path);
        if (file.EndsWith($"_{instanceId}", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var suffixed = string.IsNullOrEmpty(ext)
            ? $"{file}_{instanceId}"
            : $"{file}_{instanceId}{ext}";

        return string.IsNullOrEmpty(dir) ? suffixed : Path.Combine(dir, suffixed);
    }
}

public sealed class TcpConfig
{
    public string BindHost { get; set; } = "127.0.0.1";
    public int? Port { get; set; } = null;
}

public sealed class SerialConfig
{
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "none";       // none|odd|even|mark|space
    public int StopBits { get; set; } = 1;              // 1|2
    public string Handshake { get; set; } = "none";    // none|xonxoff|rtscts|dtrdsr
    public bool DtrEnable { get; set; } = false;
    public bool RtsEnable { get; set; } = false;
}

public sealed class DeviceSelectConfig
{
    public string[] PreferredKeywords { get; set; } = new[] { "CP210", "FTDI", "CH340", "USB Serial" };
    public string StateFile { get; set; } = "serial-bridge.state.json";
}

public sealed class ReconnectConfig
{
    public int InitialDelayMs { get; set; } = 500;
    public int MaxDelayMs { get; set; } = 10000;
    public double BackoffFactor { get; set; } = 2.0;
}

public sealed class LoggingConfig
{
    public string HealthLog { get; set; } = "serial-bridge.health.jsonl";
    public string Level { get; set; } = "info";
}
