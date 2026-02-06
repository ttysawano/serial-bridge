using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SerialBridge;

public sealed class InstanceLease : IDisposable
{
    private const int MaxSlots = 256;

    private readonly Mutex _slotMutex;
    private bool _ownsMutex;

    public int InstanceId { get; }

    private InstanceLease(int instanceId, Mutex slotMutex, bool ownsMutex)
    {
        InstanceId = instanceId;
        _slotMutex = slotMutex;
        _ownsMutex = ownsMutex;
    }

    public static InstanceLease Acquire(string scopeSeed)
    {
        var scope = ComputeScope(scopeSeed);

        for (var id = 1; id <= MaxSlots; id++)
        {
            var slotName = $"Local\\SerialBridge_Instance_{scope}_{id}";
            var mutex = new Mutex(initiallyOwned: true, name: slotName, out var createdNew);

            if (createdNew)
            {
                return new InstanceLease(id, mutex, ownsMutex: true);
            }

            try
            {
                if (mutex.WaitOne(0))
                {
                    return new InstanceLease(id, mutex, ownsMutex: true);
                }
            }
            catch (AbandonedMutexException)
            {
                return new InstanceLease(id, mutex, ownsMutex: true);
            }

            mutex.Dispose();
        }

        throw new InvalidOperationException($"No free instance slot found (1..{MaxSlots}).");
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _slotMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore release failures during process teardown.
            }

            _ownsMutex = false;
        }

        _slotMutex.Dispose();
    }

    private static string ComputeScope(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
