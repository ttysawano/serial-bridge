using System;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SerialBridge;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public TcpConfig Tcp { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public DeviceSelectConfig DeviceSelect { get; set; } = new();
    public ReconnectConfig Reconnect { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public static (AppConfig cfg, string cfgPath) LoadOrCreateDefault(string baseDir)
    {
        var cfgPath = Path.Combine(baseDir, "serial-bridge.yml");
        var examplePath = Path.Combine(baseDir, "serial-bridge.yml.example");

        if (!File.Exists(cfgPath) && File.Exists(examplePath))
        {
            File.Copy(examplePath, cfgPath);
        }

        if (!File.Exists(cfgPath))
        {
            // Minimal fallback (no file). We still run, but warn in console later.
            return (new AppConfig(), cfgPath);
        }

        var text = File.ReadAllText(cfgPath);
        var des = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var cfg = des.Deserialize<AppConfig>(text) ?? new AppConfig();
        cfg.Sanitize();
        return (cfg, cfgPath);
    }

    private void Sanitize()
    {
        Tcp ??= new TcpConfig();
        Serial ??= new SerialConfig();
        DeviceSelect ??= new DeviceSelectConfig();
        Reconnect ??= new ReconnectConfig();
        Logging ??= new LoggingConfig();
    }
}

public sealed class TcpConfig
{
    public string BindHost { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7000;
}

public sealed class SerialConfig
{
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "none";       // none|odd|even|mark|space
    public int StopBits { get; set; } = 1;             // 1|2
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

