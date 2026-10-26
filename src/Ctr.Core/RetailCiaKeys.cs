using Ctr.Core.Containers.Cia;

namespace Ctr.Core;

/// <summary>
/// Retail common-key index 0 for the supported Japanese CIA.
/// </summary>
public static class RetailCiaKeys
{
    public static byte[] CreateNormalKey(byte index) => index switch
    {
        0 => [0x64, 0xC5, 0xFD, 0x55, 0xDD, 0x3A, 0xD9, 0x88, 0x32, 0x5B, 0xAA, 0xEC, 0x52, 0x43, 0xDB, 0x98],
        _ => throw new NotSupportedException($"No bundled retail CIA common key for index {index}."),
    };

    public static byte[] DecryptTitleKey(CiaTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        byte[] key = CreateNormalKey(ticket.CommonKeyIndex);
        try { return ticket.DecryptTitleKey(key); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }
}
