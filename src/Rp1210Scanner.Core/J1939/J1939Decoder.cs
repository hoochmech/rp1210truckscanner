using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Rp1210Scanner.Core.J1939;

public sealed record ComponentId(string Make, string Model, string SerialNumber, string UnitNumber);

public sealed record EcuIdentification(string PartNumber, string SerialNumber, string Location, string Type, string ManufacturerName, string HardwareId);

/// <summary>Fields of the 64-bit J1939 NAME carried in the Address Claimed message.</summary>
public sealed record J1939Name(ulong Raw)
{
    public uint IdentityNumber => (uint)(Raw & 0x1FFFFF);
    public int ManufacturerCode => (int)((Raw >> 21) & 0x7FF);
    public int EcuInstance => (int)((Raw >> 32) & 0x07);
    public int FunctionInstance => (int)((Raw >> 35) & 0x1F);
    public int Function => (int)((Raw >> 40) & 0xFF);
    public int VehicleSystem => (int)((Raw >> 49) & 0x7F);
    public int IndustryGroup => (int)((Raw >> 60) & 0x07);
    public bool ArbitraryAddressCapable => (Raw >> 63) != 0;
}

/// <summary>Decoders for the identification PGNs. Text PGNs are ASCII fields separated by '*'.</summary>
public static class J1939Decoder
{
    public static string DecodeVin(byte[] data) => SplitFields(data).FirstOrDefault() ?? "";

    public static ComponentId DecodeComponentId(byte[] data)
    {
        var f = SplitFields(data);
        return new ComponentId(At(f, 0), At(f, 1), At(f, 2), At(f, 3));
    }

    /// <summary>PGN 65242: byte 0 is the number of fields, then '*'-terminated software ids.</summary>
    public static IReadOnlyList<string> DecodeSoftwareIds(byte[] data)
    {
        if (data.Length < 2)
            return Array.Empty<string>();
        var count = data[0];
        var fields = SplitFields(data[1..]);
        return fields.Take(count == 0 || count == 0xFF ? fields.Count : count).Where(s => s.Length > 0).ToList();
    }

    public static EcuIdentification DecodeEcuId(byte[] data)
    {
        var f = SplitFields(data);
        return new EcuIdentification(At(f, 0), At(f, 1), At(f, 2), At(f, 3), At(f, 4), At(f, 5));
    }

    public static J1939Name DecodeName(byte[] data)
    {
        ulong raw = 0;
        for (var i = 0; i < Math.Min(8, data.Length); i++)
            raw |= (ulong)data[i] << (8 * i);
        return new J1939Name(raw);
    }

    internal static List<string> SplitFields(byte[] data)
    {
        // Strip padding (0xFF / 0x00) and non-printables, then split on the '*' delimiter.
        var text = new string(data.Select(b => b >= 0x20 && b < 0x7F ? (char)b : '\0').Where(c => c != '\0').ToArray());
        var parts = text.Split('*').Select(s => s.Trim()).ToList();
        if (parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    private static string At(List<string> f, int i) => i < f.Count ? f[i] : "";
}

/// <summary>Preferred J1939 source addresses (J1939 Appendix B), enough to label the usual ECUs on a truck.</summary>
public static class SourceAddresses
{
    public const byte Engine1 = 0x00;
    public const byte OffboardDiagnosticTool1 = 0xF9;

    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x00] = "Engine #1",
        [0x01] = "Engine #2",
        [0x03] = "Transmission #1",
        [0x0B] = "Brakes - System Controller (ABS)",
        [0x0F] = "Retarder - Engine",
        [0x10] = "Retarder - Driveline",
        [0x11] = "Cruise Control",
        [0x17] = "Instrument Cluster #1",
        [0x19] = "Passenger-Operator Climate Control #1",
        [0x1E] = "Electrical System",
        [0x21] = "Body Controller",
        [0x27] = "Management Computer #1",
        [0x2A] = "Headway Controller",
        [0x31] = "Cab Controller - Primary",
        [0x3D] = "Exhaust Emission Controller",
        [0xEE] = "Tachograph",
    };

    public static string Describe(byte sa) => Names.TryGetValue(sa, out var n) ? n : $"ECU @ 0x{sa:X2}";
}
