using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO.Ports;
using System.Management;

namespace SerialBridge;

public sealed record SerialDeviceInfo(string PortName, string FriendlyName, string? PnpDeviceId);

public sealed class DeviceLocator
{
    private static readonly Regex ComRegex = new(@"\(COM\d+\)$", RegexOptions.Compiled);

    public List<SerialDeviceInfo> ListDevices()
    {
        // First try WMI for friendly names + PNPDeviceID.
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var list = new List<SerialDeviceInfo>();

                // Win32_PnPEntity Name often contains "(COMx)"
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var nameObj = mo["Name"]?.ToString();
                    var pnpObj = mo["PNPDeviceID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(nameObj)) continue;

                    var m = Regex.Match(nameObj, @"\((COM\d+)\)\s*$");
                    if (!m.Success) continue;

                    var portName = m.Groups[1].Value;
                    list.Add(new SerialDeviceInfo(portName, nameObj, pnpObj));
                }

                // Ensure unique by PortName
                return list
                    .GroupBy(x => x.PortName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(x => x.PortName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch
        {
            // fall back below
        }

        // Fallback: only port names.
        return SerialPort.GetPortNames()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SerialDeviceInfo(p, p, null))
            .ToList();
    }

    public SerialDeviceInfo? FindByPnpId(List<SerialDeviceInfo> devices, string? pnpId)
    {
        if (string.IsNullOrWhiteSpace(pnpId)) return null;
        return devices.FirstOrDefault(d => string.Equals(d.PnpDeviceId, pnpId, StringComparison.OrdinalIgnoreCase));
    }

    public SerialDeviceInfo? FindByKeywords(List<SerialDeviceInfo> devices, IEnumerable<string> keywords)
    {
        foreach (var k in keywords)
        {
            var hit = devices.FirstOrDefault(d => d.FriendlyName.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }
}

