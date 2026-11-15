using System.Buffers.Binary;

namespace IH.Shared;

public static class LanDiscovery
{
    public static byte[] Create(bool response, int nonce)
    {
        byte[] message = new byte[12];
        "IHK"u8.CopyTo(message);
        message[3] = response ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(4), Codec.ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), nonce);
        return message;
    }

    public static bool TryRead(ReadOnlySpan<byte> message, bool response, out int nonce)
    {
        nonce = 0;
        if (message.Length != 12 || !message[..3].SequenceEqual("IHK"u8) || message[3] != (response ? 1 : 0)
            || BinaryPrimitives.ReadInt32LittleEndian(message[4..]) != Codec.ProtocolVersion) return false;
        nonce = BinaryPrimitives.ReadInt32LittleEndian(message[8..]);
        return true;
    }
}
