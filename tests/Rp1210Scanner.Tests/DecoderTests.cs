using System.Linq;
using System.Text;
using Rp1210Scanner.Core.Bus;
using Rp1210Scanner.Core.J1587;
using Rp1210Scanner.Core.J1939;
using Rp1210Scanner.Core.Rp1210;
using Rp1210Scanner.Core.Scan;
using Rp1210Scanner.Core.Vin;
using Xunit;

namespace Rp1210Scanner.Tests;

public class J1939Tests
{
    [Fact]
    public void Request_is_encoded_in_rp1210_layout()
    {
        var tx = J1939Message.Request(Pgns.VehicleIdentification, 0xF9, 0x00).ToRp1210Tx();
        Assert.Equal(new byte[] { 0x00, 0xEA, 0x00, 0x06, 0xF9, 0x00, 0xEC, 0xFE, 0x00 }, tx);
    }

    [Fact]
    public void Received_frame_is_decoded_after_timestamp()
    {
        var rx = new byte[] { 0, 0, 0, 1, 0xEC, 0xFE, 0x00, 0x06, 0x00, 0xFF }.Concat(Encoding.ASCII.GetBytes("1XKYD49X7LJ123456*")).ToArray();
        var m = J1939Message.FromRp1210Rx(rx)!;
        Assert.Equal(Pgns.VehicleIdentification, m.Pgn);
        Assert.Equal(0x00, m.Source);
        Assert.Equal("1XKYD49X7LJ123456", J1939Decoder.DecodeVin(m.Data));
    }

    [Fact]
    public void Pdu1_pgn_drops_destination_byte()
    {
        Assert.Equal(Pgns.AddressClaimed, J1939Message.NormalizePgn(0xEEFF));
        Assert.Equal(Pgns.ComponentIdentification, J1939Message.NormalizePgn(0xFEEB));
    }

    [Fact]
    public void Component_id_splits_star_fields_and_ignores_padding()
    {
        var data = Encoding.ASCII.GetBytes("CMMNS*X15*79912345*EN-1*").Concat(new byte[] { 0xFF, 0xFF }).ToArray();
        var c = J1939Decoder.DecodeComponentId(data);
        Assert.Equal(new ComponentId("CMMNS", "X15", "79912345", "EN-1"), c);
    }

    [Fact]
    public void Software_id_honours_field_count()
    {
        var data = new byte[] { 2 }.Concat(Encoding.ASCII.GetBytes("ABC123*DEF456*")).ToArray();
        Assert.Equal(new[] { "ABC123", "DEF456" }, J1939Decoder.DecodeSoftwareIds(data));
    }

    [Fact]
    public void Name_manufacturer_code_is_bits_21_to_31()
    {
        var name = J1939Decoder.DecodeName(System.BitConverter.GetBytes(15UL << 21 | 0x1234));
        Assert.Equal(15, name.ManufacturerCode);
        Assert.Equal(0x1234u, name.IdentityNumber);
    }
}

public class J1587Tests
{
    [Fact]
    public void Parses_variable_length_vin_pid()
    {
        var vin = Encoding.ASCII.GetBytes("1FUJGLDR5CSBM1234");
        var frame = new byte[] { 128, 237, (byte)vin.Length }.Concat(vin).ToArray();
        var m = J1587Decoder.Parse(frame)!;
        Assert.Equal(128, m.Mid);
        Assert.Equal("1FUJGLDR5CSBM1234", J1587Decoder.Ascii(m.Parameters.Single(p => p.Pid == 237).Data));
    }

    [Fact]
    public void Parses_mixed_length_pids()
    {
        // PID 84 (1 byte), PID 190 (2 bytes), PID 243 (variable).
        var comp = new byte[] { 0x01 }.Concat(Encoding.ASCII.GetBytes("DDC*S60*06R0123456")).ToArray();
        var frame = new byte[] { 128, 84, 0x50, 190, 0x10, 0x27, 243, (byte)comp.Length }.Concat(comp).ToArray();
        var m = J1587Decoder.Parse(frame)!;
        Assert.Equal(new[] { 84, 190, 243 }, m.Parameters.Select(p => p.Pid));
        Assert.Equal(("DDC", "S60", "06R0123456"), J1587Decoder.DecodeComponentId(m.Parameters[2].Data));
    }

    [Fact]
    public void Request_frame_has_priority_and_tool_mid()
    {
        Assert.Equal(new byte[] { 8, 172, 0, 237 }, J1587Decoder.BuildRequest(J1587Decoder.PidVin));
    }
}

public class VinTests
{
    [Fact]
    public void Decodes_make_year_and_check_digit()
    {
        var v = VinDecoder.Decode("1XKYD49X7LJ123456", currentYear: 2026);
        Assert.True(v.IsValidFormat);
        Assert.True(v.CheckDigitOk);
        Assert.Equal("Kenworth", v.Manufacturer);
        Assert.Equal(2020, v.ModelYear);
    }

    [Fact]
    public void Flags_bad_check_digit()
    {
        Assert.False(VinDecoder.Decode("1FUJGLDR5CSBM1234").CheckDigitOk);
    }

    [Theory]
    [InlineData('A', 2026, 2010)]
    [InlineData('T', 2026, 2026)]
    [InlineData('V', 2025, 1997)]
    [InlineData('V', 2026, 2027)]
    [InlineData('5', 2026, 2005)]
    public void Model_year_picks_most_recent_cycle(char code, int now, int expected)
    {
        Assert.Equal(expected, VinDecoder.ModelYear(code, now));
    }

    [Theory]
    [InlineData("CMMNS", "Cummins")]
    [InlineData("DDC", "Detroit Diesel")]
    [InlineData("PACCR", "PACCAR")]
    [InlineData("Weird", "Weird")]
    public void Normalizes_engine_make(string raw, string expected) => Assert.Equal(expected, EngineMakes.Normalize(raw));
}

public class IniTests
{
    private const string VendorIni = """
        [VendorInformation]
        Name=Acme Adapters
        [DeviceInformation1]
        DeviceID=1
        DeviceDescription=Acme USB-Link
        DeviceName=USB
        [ProtocolInformation1]
        ProtocolString=J1939
        Devices=1
        [ProtocolInformation2]
        ProtocolString=J1708
        Devices=All
        """;

    [Fact]
    public void Parses_devices_and_protocols()
    {
        var v = Rp1210Ini.ParseVendor("ACME32", VendorIni);
        Assert.Equal("Acme Adapters", v.VendorName);
        Assert.Equal("ACME32.dll", v.DllName);
        var d = Assert.Single(v.Devices);
        Assert.Equal((short)1, d.DeviceId);
        Assert.Equal(new[] { "J1939", "J1708" }, d.Protocols);
    }
}

public class ScannerTests
{
    [Fact]
    public void Simulated_truck_yields_full_identification()
    {
        var scanner = new TruckScanner(new ScanOptions { ListenWindow = System.TimeSpan.FromMilliseconds(20) });
        using var bus = SimulatedJ1939Bus.TypicalTruck();
        var r = scanner.Scan(bus, null);

        Assert.Equal("1XKYD49X7LJ123456", r.Vin!.Vin);
        Assert.Equal("Kenworth", r.TruckMake);
        Assert.Equal("Cummins", r.EngineMake);
        Assert.Equal("X15", r.EngineModel);
        Assert.Equal("CM2350", r.EcmType);
        Assert.Equal(15, r.Engine!.ManufacturerCode);
        Assert.Contains(r.Ecus, e => e.Address == 0x03 && e.Component!.Make == "ALLSN");
    }
}
