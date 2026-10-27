using System;
using System.Security.Cryptography;

namespace BroidShot.Modules.Clipboard.Storage;

/// <summary>Encrypts clipboard content at rest.</summary>
public interface IContentProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>Windows DPAPI, CurrentUser scope: only this Windows account on this machine can decrypt.</summary>
public sealed class DpapiContentProtector : IContentProtector
{
    // Fixed entropy separates BroidShot's blobs from other DPAPI users of the same account.
    private static readonly byte[] Entropy = "BroidShot.Clipboard.v1"u8.ToArray();

    public static DpapiContentProtector Instance { get; } = new();

    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}
