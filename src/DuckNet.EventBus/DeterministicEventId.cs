using System.Security.Cryptography;
using System.Text;

namespace DuckNet.EventBus;

public static class DeterministicEventId
{
    public static Guid For(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
