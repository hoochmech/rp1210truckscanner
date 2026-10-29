using System;

namespace Rp1210Scanner.Core.J1939;

public sealed record J1939Message(int Pgn, byte Priority, byte Source, byte Destination, byte[] Data)
{
    public const byte GlobalAddress = 0xFF;

    /// <summary>For PDU1 PGNs (PF below 0xF0) the low byte is the destination, not part of the PGN.</summary>
    public static int NormalizePgn(int pgn) => ((pgn >> 8) & 0xFF) < 0xF0 ? pgn & 0x3FF00 : pgn & 0x3FFFF;

    /// <summary>
    /// RP1210 J1939 transmit layout: PGN (3 bytes, LSB first), how/priority, source, destination, data.
    /// Bit 7 of the how/priority byte clear means the driver uses RTS/CTS for long destination-specific messages.
    /// </summary>
    public byte[] ToRp1210Tx()
    {
        var buf = new byte[6 + Data.Length];
        buf[0] = (byte)(Pgn & 0xFF);
        buf[1] = (byte)((Pgn >> 8) & 0xFF);
        buf[2] = (byte)((Pgn >> 16) & 0x03);
        buf[3] = (byte)(Priority & 0x07);
        buf[4] = Source;
        buf[5] = Destination;
        Data.CopyTo(buf, 6);
        return buf;
    }

    /// <summary>
    /// RP1210 J1939 receive layout: 4-byte timestamp (big-endian), PGN (3 bytes, LSB first),
    /// how/priority, source, destination, data. Assumes echo of transmitted messages is off (the default).
    /// </summary>
    public static J1939Message? FromRp1210Rx(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 10)
            return null;
        var pgn = buf[4] | (buf[5] << 8) | ((buf[6] & 0x03) << 16);
        return new J1939Message(NormalizePgn(pgn), (byte)(buf[7] & 0x07), buf[8], buf[9], buf[10..].ToArray());
    }

    public static J1939Message Request(int requestedPgn, byte source, byte destination) =>
        new(Pgns.Request, 6, source, destination, new[]
        {
            (byte)(requestedPgn & 0xFF),
            (byte)((requestedPgn >> 8) & 0xFF),
            (byte)((requestedPgn >> 16) & 0x03),
        });
}

public static class Pgns
{
    public const int Request = 0xEA00;            // 59904
    public const int AddressClaimed = 0xEE00;     // 60928
    public const int EcuIdentification = 0xFDC5;  // 64965
    public const int SoftwareIdentification = 0xFEDA; // 65242
    public const int ComponentIdentification = 0xFEEB; // 65259
    public const int VehicleIdentification = 0xFEEC;   // 65260
}
