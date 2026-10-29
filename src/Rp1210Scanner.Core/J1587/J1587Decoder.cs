using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rp1210Scanner.Core.J1587;

public sealed record J1587Parameter(int Pid, byte[] Data);

public sealed record J1587Message(byte Mid, IReadOnlyList<J1587Parameter> Parameters);

/// <summary>J1708/J1587 framing for older (pre-2007ish) trucks that lack J1939 identification.</summary>
public static class J1587Decoder
{
    public const byte MidEngine = 128;
    public const byte MidTransmission = 130;
    public const byte MidBrakes = 136;
    public const byte MidOffboardDiagnostics1 = 172;

    public const int PidRequestParameter = 0;
    public const int PidSoftwareId = 234;
    public const int PidVin = 237;
    public const int PidComponentId = 243;

    /// <summary>
    /// RP1210 J1708 transmit layout: priority (1-8), then MID and data. The driver appends the checksum.
    /// PID 0 asks every module that supports the requested PID to broadcast it.
    /// </summary>
    public static byte[] BuildRequest(int requestedPid) =>
        new byte[] { 8, MidOffboardDiagnostics1, PidRequestParameter, (byte)requestedPid };

    /// <summary>RP1210 J1708 receive layout: 4-byte timestamp, then MID and data (checksum stripped by most drivers).</summary>
    public static J1587Message? FromRp1210Rx(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 6)
            return null;
        return Parse(buf[4..].ToArray());
    }

    public static J1587Message? Parse(byte[] frame)
    {
        if (frame.Length < 2)
            return null;
        var mid = frame[0];
        var parameters = new List<J1587Parameter>();
        var i = 1;
        var page = 0;
        while (i < frame.Length)
        {
            int pid = frame[i++];
            if (pid == 255)
            {
                // Page extension: the next PID is in the 256-511 range.
                page = 256;
                continue;
            }
            int len;
            if (pid < 128)
                len = 1;
            else if (pid < 192)
                len = 2;
            else if (pid < 254)
            {
                if (i >= frame.Length)
                    break;
                len = frame[i++];
            }
            else
                break; // 254 = data link escape; not used for identification

            if (i + len > frame.Length)
                break; // truncated, or a trailing checksum byte
            parameters.Add(new J1587Parameter(pid + page, frame[i..(i + len)]));
            i += len;
            page = 0;
        }
        return new J1587Message(mid, parameters);
    }

    public static string Ascii(byte[] data) =>
        new string(data.Where(b => b >= 0x20 && b < 0x7F).Select(b => (char)b).ToArray()).Trim();

    /// <summary>PID 243 data starts with a device id byte, then make*model*serial*unit.</summary>
    public static (string Make, string Model, string Serial) DecodeComponentId(byte[] data)
    {
        var text = Ascii(data.Length > 0 && data[0] < 0x20 ? data[1..] : data);
        var f = text.Split('*');
        return (f.ElementAtOrDefault(0)?.Trim() ?? "", f.ElementAtOrDefault(1)?.Trim() ?? "", f.ElementAtOrDefault(2)?.Trim() ?? "");
    }

    public static string DescribeMid(byte mid) => mid switch
    {
        128 => "Engine #1",
        130 => "Transmission",
        136 => "Brakes (ABS)",
        140 => "Instrument Cluster",
        _ => $"MID {mid}",
    };
}
