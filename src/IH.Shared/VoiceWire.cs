using System.Buffers.Binary;

namespace IH.Shared;

/// <summary>One bounded 20 ms audio frame, sent without retransmission.</summary>
public static class VoiceWire
{
    public const int SampleRate = 16000, Samples = 320, PcmBytes = Samples * 2;
    private const int Header = 11;
    public const int PacketBytes = Header + PcmBytes;

    public static byte[] Encode(int game, int speaker, uint sequence, ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != PcmBytes || speaker is < 0 or > byte.MaxValue)
            throw new ArgumentException("Invalid voice frame");
        var packet = new byte[PacketBytes];
        packet[0] = (byte)MsgType.Voice;
        packet[1] = 0;
        packet[2] = (byte)speaker;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(3), game);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), sequence);
        pcm.CopyTo(packet.AsSpan(Header));
        return packet;
    }

    public static bool TryRead(byte[] packet, out int game, out int speaker, out uint sequence, out ReadOnlyMemory<byte> pcm)
    {
        game = speaker = 0; sequence = 0; pcm = default;
        if (packet.Length != PacketBytes || packet[0] != (byte)MsgType.Voice || packet[1] != 0) return false;
        speaker = packet[2];
        game = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(3));
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7));
        pcm = packet.AsMemory(Header);
        return true;
    }
}
