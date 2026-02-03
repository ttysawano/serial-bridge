using System;

namespace SerialBridge;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return new BridgeSupervisor().Run(args);
        }
        catch (Exception ex)
        {
            // Last resort: show something even if logging failed.
            Console.Error.WriteLine($"FATAL: {ex}");
            return 1;
        }
    }
}

