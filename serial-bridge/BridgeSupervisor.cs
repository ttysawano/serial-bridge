using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace SerialBridge;

public sealed class BridgeSupervisor
{
    public int Run(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;

        var (cfg, cfgPath) = AppConfig.LoadOrCreateDefault(baseDir);

        var healthPath = Path.Combine(baseDir, cfg.Logging.HealthLog);
        using var log = new HealthLog(healthPath);

        log.Info("startup", new { baseDir, cfgPath, dotnet = Environment.Version.ToString() });

        if (!File.Exists(cfgPath))
        {
            Console.WriteLine($"Config not found: {cfgPath}");
            Console.WriteLine("Put serial-bridge.yml (or serial-bridge.yml.example) next to the exe.");
        }

        var statePath = Path.Combine(baseDir, cfg.DeviceSelect.StateFile);
        var state = DeviceState.Load(statePath);

        var locator = new DeviceLocator();

        // Start TCP listener once.
        var listener = StartListener(cfg, log);

        // Main loop: keep serial open even if client disconnects.
        while (true)
        {
            using var serial = EnsureSerialConnected(cfg, state, statePath, locator, log);

            // Client loop: serial stays open across client sessions.
            while (serial.IsOpen)
            {
                Console.WriteLine($"Waiting for TCP client on {cfg.Tcp.BindHost}:{cfg.Tcp.Port} ...");
                log.Info("tcp_wait_client", new { cfg.Tcp.BindHost, cfg.Tcp.Port });

                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    log.Error("tcp_accept_failed", new { ex = ex.ToString() });
                    continue;
                }

                log.Info("tcp_client_connected", new { remote = client.Client.RemoteEndPoint?.ToString() });
                Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");

                var sessionCts = new CancellationTokenSource();

                try
                {
                    RunBridgeSession(serial, client, sessionCts.Token, log);
                }
                catch (SerialDisconnectedException sdx)
                {
                    log.Warn("serial_disconnected_in_session", new { ex = sdx.InnerException?.ToString() ?? sdx.ToString() });
                    Console.WriteLine("Serial disconnected. Reconnecting...");
                    // Break client loop; outer loop will reconnect serial.
                    break;
                }
                catch (Exception ex)
                {
                    log.Warn("session_error", new { ex = ex.ToString() });
                }
                finally
                {
                    sessionCts.Cancel();
                    client.Close();
                    log.Info("tcp_client_disconnected");
                    Console.WriteLine("Client disconnected.");
                }
            }

            // If we got here, serial is not open; loop will reconnect.
        }
    }

    private static TcpListener StartListener(AppConfig cfg, HealthLog log)
    {
        var ip = IPAddress.Parse(cfg.Tcp.BindHost);
        var listener = new TcpListener(ip, cfg.Tcp.Port);
        listener.Start();
        log.Info("tcp_listening", new { cfg.Tcp.BindHost, cfg.Tcp.Port });
        Console.WriteLine($"TCP listening: {cfg.Tcp.BindHost}:{cfg.Tcp.Port}");
        return listener;
    }

private static SerialPort EnsureSerialConnected(
    AppConfig cfg,
    DeviceState state,
    string statePath,
    DeviceLocator locator,
    HealthLog log)
{
    var delay = cfg.Reconnect.InitialDelayMs;

    bool announcedLast = false;

    while (true)
    {
        if (!announcedLast && !string.IsNullOrWhiteSpace(state.LastFriendlyName))
        {
            Console.WriteLine($"Previous target: {state.LastFriendlyName}");
            Console.WriteLine("Searching for the same device...");
            announcedLast = true;
        }

        var devices = locator.ListDevices();
        log.Info("serial_scan", new { count = devices.Count });

        if (devices.Count == 0)
        {
            Console.WriteLine("No COM ports found. Plug the USB-Serial device and wait...");
            log.Warn("serial_no_ports");
            SleepBackoff(cfg, ref delay, log);
            continue;
        }

        SerialDeviceInfo? selected = null;

        // (A) Best: exact PNP match (survives COM number changes)
        selected = locator.FindByPnpId(devices, state.LastPnpDeviceId);

        // (B) Fallback: match by normalized friendly name (COM number may differ)
        selected ??= FindByNormalizedFriendlyName(devices, state.LastFriendlyName);

        // (C) Fallback: keyword match
        selected ??= locator.FindByKeywords(devices, cfg.DeviceSelect.PreferredKeywords);

        // If we found an auto-candidate, offer a short window to change
        if (selected != null)
        {
            Console.WriteLine($"Found: {selected.FriendlyName}");
            Console.WriteLine("Auto-connecting. Press 'c' within 3 seconds to change the target...");

            log.Info("auto_candidate", new { selected.PortName, selected.FriendlyName, selected.PnpDeviceId });

            if (WaitForChangeKey(3000))
            {
                Console.WriteLine("Switch requested. Showing the port list...");
                log.Info("user_requested_change");

                // Show selection list
                var pr = PromptSelect(devices);
                if (pr.Action == PromptAction.Quit)
                {
                    log.Warn("user_exit");
                    Environment.Exit(0);
                }
                if (pr.Action == PromptAction.Rescan)
                {
                    // go back to while loop and rescan
                    continue;
                }
                selected = pr.Device;
            }
        }

        // If still none, force selection
        if (selected == null)
        {
            Console.WriteLine("No suitable device was auto-selected. Please choose a port.");
            var pr = PromptSelect(devices);

            if (pr.Action == PromptAction.Quit)
            {
                log.Warn("user_exit");
                Environment.Exit(0);
            }
            if (pr.Action == PromptAction.Rescan)
            {
                continue;
            }

            selected = pr.Device;
        }

        // Persist state (candidate that we *intend* to use)
        state.LastPortName = selected!.PortName;
        state.LastFriendlyName = selected.FriendlyName;
        state.LastPnpDeviceId = selected.PnpDeviceId;
        state.Save(statePath);

        log.Info("serial_selected", new { selected.PortName, selected.FriendlyName, selected.PnpDeviceId });

        try
        {
            var sp = OpenSerial(selected.PortName, cfg.Serial, log);
            Console.WriteLine($"Serial connected: {selected.FriendlyName}");
            log.Info("serial_connected", new { selected.PortName });

            delay = cfg.Reconnect.InitialDelayMs; // reset backoff
            return sp;
        }
        catch (Exception ex)
        {
            log.Warn("serial_open_failed", new { selected.PortName, ex = ex.ToString() });
            Console.WriteLine($"Failed to open {selected.PortName}. Retrying...");
            SleepBackoff(cfg, ref delay, log);
        }
    }
}

private static bool WaitForChangeKey(int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (Console.KeyAvailable)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.KeyChar == 'c' || k.KeyChar == 'C') return true;
            if (k.KeyChar == 'q' || k.KeyChar == 'Q') Environment.Exit(0);
        }
        Thread.Sleep(50);
    }
    return false;
}

private static SerialDeviceInfo? FindByNormalizedFriendlyName(
    System.Collections.Generic.List<SerialDeviceInfo> devices,
    string? lastFriendlyName)
{
    if (string.IsNullOrWhiteSpace(lastFriendlyName)) return null;

    static string Norm(string s) =>
        Regex.Replace(s, @"\s*\(COM\d+\)\s*$", "", RegexOptions.IgnoreCase).Trim();

    var key = Norm(lastFriendlyName);
    return devices.FirstOrDefault(d => string.Equals(Norm(d.FriendlyName), key, StringComparison.OrdinalIgnoreCase));
}



private enum PromptAction
{
    Selected,
    Rescan,
    Quit
}

private sealed record PromptResult(PromptAction Action, SerialDeviceInfo? Device);

private static PromptResult PromptSelect(System.Collections.Generic.List<SerialDeviceInfo> devices)
{
    while (true)
    {
        Console.WriteLine("Select COM port:");
        for (int i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"  [{i}] {devices[i].FriendlyName}");
        }
        Console.Write("Enter number (or 'r' to rescan, 'q' to quit): ");
        var s = Console.ReadLine()?.Trim();

        if (string.Equals(s, "q", StringComparison.OrdinalIgnoreCase))
            return new PromptResult(PromptAction.Quit, null);

        if (string.Equals(s, "r", StringComparison.OrdinalIgnoreCase))
            return new PromptResult(PromptAction.Rescan, null);

        if (int.TryParse(s, out var idx) && idx >= 0 && idx < devices.Count)
            return new PromptResult(PromptAction.Selected, devices[idx]);

        Console.WriteLine("Invalid input.");
    }
}
    

    private static SerialPort OpenSerial(string portName, SerialConfig cfg, HealthLog log)
    {
        var parity = cfg.Parity.ToLowerInvariant() switch
        {
            "none" => Parity.None,
            "odd" => Parity.Odd,
            "even" => Parity.Even,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None
        };

        var stopBits = cfg.StopBits switch
        {
            2 => StopBits.Two,
            _ => StopBits.One
        };

        var handshake = cfg.Handshake.ToLowerInvariant() switch
        {
            "xonxoff" => Handshake.XOnXOff,
            "rtscts" => Handshake.RequestToSend,
            "dtrdsr" => Handshake.RequestToSendXOnXOff, // closest; usually not used here
            _ => Handshake.None
        };

        var sp = new SerialPort(portName)
        {
            BaudRate = cfg.BaudRate,
            DataBits = cfg.DataBits,
            Parity = parity,
            StopBits = stopBits,
            Handshake = handshake,
            DtrEnable = cfg.DtrEnable,
            RtsEnable = cfg.RtsEnable,

            // Important: allow cancel-ish by polling
            ReadTimeout = 500,
            WriteTimeout = 2000
        };

        sp.Open();
        return sp;
    }

    private static void RunBridgeSession(SerialPort serial, TcpClient client, CancellationToken token, HealthLog log)
    {
        using var ns = client.GetStream();

        // Pump Serial -> TCP (sync read with timeout)
        var t1 = Task.Run(async () =>
        {
            var buf = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = serial.Read(buf, 0, buf.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    throw new SerialDisconnectedException(ex);
                }

                if (n > 0)
                {
                    await ns.WriteAsync(buf.AsMemory(0, n), token);
                }
            }
        }, token);

        // Pump TCP -> Serial
        var t2 = Task.Run(async () =>
        {
            var buf = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                int n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), token);
                if (n == 0) break; // client closed
                try
                {
                    serial.Write(buf, 0, n);
                }
                catch (Exception ex)
                {
                    throw new SerialDisconnectedException(ex);
                }
            }
        }, token);

        try
        {
            Task.WaitAny(t1, t2);
        }
        catch { /* ignore */ }

        // best-effort join
        try { Task.WaitAll(new[] { t1, t2 }, 1500); } catch { /* ignore */ }
    }

    private static void SleepBackoff(AppConfig cfg, ref int delayMs, HealthLog log)
    {
        log.Info("reconnect_wait", new { delayMs });
        Thread.Sleep(delayMs);
        var next = (int)Math.Round(delayMs * cfg.Reconnect.BackoffFactor);
        delayMs = Math.Min(cfg.Reconnect.MaxDelayMs, Math.Max(cfg.Reconnect.InitialDelayMs, next));
    }
}

public sealed class SerialDisconnectedException : Exception
{
    public SerialDisconnectedException(Exception inner) : base("Serial disconnected", inner) { }
}

