using System.IO;
using System.Text.Json;

namespace SerialBridge;

public sealed class DeviceState
{
    public string? LastPnpDeviceId { get; set; }
    public string? LastFriendlyName { get; set; }
    public string? LastPortName { get; set; }

    public static DeviceState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new DeviceState();
            var txt = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeviceState>(txt) ?? new DeviceState();
        }
        catch
        {
            return new DeviceState();
        }
    }

    public void Save(string path)
    {
        var txt = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, txt);
    }
}

