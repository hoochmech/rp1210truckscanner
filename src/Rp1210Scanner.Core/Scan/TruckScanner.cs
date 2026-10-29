using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Rp1210Scanner.Core.Bus;
using Rp1210Scanner.Core.J1587;
using Rp1210Scanner.Core.J1939;
using Rp1210Scanner.Core.Vin;

namespace Rp1210Scanner.Core.Scan;

public sealed class EcuReport
{
    public string Protocol { get; init; } = "J1939";
    public int Address { get; init; }
    public string Label { get; init; } = "";
    public ComponentId? Component { get; set; }
    public EcuIdentification? EcuId { get; set; }
    public List<string> SoftwareIds { get; } = new();
    public int? ManufacturerCode { get; set; }
    public string? Vin { get; set; }
}

public sealed class ScanResult
{
    public VinInfo? Vin { get; set; }
    public List<EcuReport> Ecus { get; } = new();

    public EcuReport? Engine => Ecus.FirstOrDefault(e =>
        (e.Protocol == "J1939" && e.Address == SourceAddresses.Engine1) ||
        (e.Protocol == "J1587" && e.Address == J1587Decoder.MidEngine));

    public string TruckMake => Vin?.Manufacturer ?? "";
    public int? ModelYear => Vin?.ModelYear;
    public string EngineMake => EngineMakes.Normalize(Engine?.Component?.Make ?? Engine?.EcuId?.ManufacturerName ?? "");
    public string EngineModel => Engine?.Component?.Model ?? "";
    public string EngineSerial => Engine?.Component?.SerialNumber ?? Engine?.EcuId?.SerialNumber ?? "";
    /// <summary>ECM hardware type: the ECU Identification "type" field when present (e.g. CM2350), else its part number.</summary>
    public string EcmType => FirstNonEmpty(Engine?.EcuId?.Type, Engine?.EcuId?.PartNumber, Engine?.EcuId?.HardwareId);
    public string EcmPartNumber => Engine?.EcuId?.PartNumber ?? "";
    public string EngineSoftware => string.Join(", ", Engine?.SoftwareIds ?? new List<string>());

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}

public sealed class ScanOptions
{
    /// <summary>How long to keep listening after each request. Multi-packet (BAM) replies take ~50 ms per frame.</summary>
    public TimeSpan ListenWindow { get; init; } = TimeSpan.FromMilliseconds(1500);
}

/// <summary>Requests identification data over J1939, then falls back to J1587 when the engine did not answer.</summary>
public sealed class TruckScanner
{
    private static readonly int[] J1939IdPgns =
    {
        Pgns.AddressClaimed, Pgns.VehicleIdentification, Pgns.ComponentIdentification,
        Pgns.EcuIdentification, Pgns.SoftwareIdentification,
    };

    private readonly ScanOptions _options;
    private readonly Action<string> _log;

    public TruckScanner(ScanOptions? options = null, Action<string>? log = null)
    {
        _options = options ?? new ScanOptions();
        _log = log ?? (_ => { });
    }

    public ScanResult Scan(IJ1939Bus? j1939, IJ1708Bus? j1708, CancellationToken ct = default)
    {
        var result = new ScanResult();

        if (j1939 != null)
            ScanJ1939(j1939, result, ct);

        if (result.Engine?.Component == null && j1708 != null)
            ScanJ1587(j1708, result, ct);
        else if (j1708 != null)
            _log("Engine answered on J1939; skipping J1587.");

        var vin = result.Ecus.Select(e => e.Vin).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (vin != null)
            result.Vin = VinDecoder.Decode(vin);
        return result;
    }

    private void ScanJ1939(IJ1939Bus bus, ScanResult result, CancellationToken ct)
    {
        foreach (var pgn in J1939IdPgns)
        {
            ct.ThrowIfCancellationRequested();
            _log($"J1939 request PGN {pgn} (0x{pgn:X4}) to global");
            bus.Send(J1939Message.Request(pgn, bus.OwnAddress, J1939Message.GlobalAddress));
            Listen(() => bus.Receive(), m => HandleJ1939(m, result), ct);
        }

        // Some engines ignore global requests for these; ask the engine directly if it stayed quiet.
        foreach (var pgn in new[] { Pgns.VehicleIdentification, Pgns.ComponentIdentification, Pgns.EcuIdentification })
        {
            var engine = result.Engine;
            var have = pgn switch
            {
                Pgns.VehicleIdentification => engine?.Vin != null,
                Pgns.ComponentIdentification => engine?.Component != null,
                _ => engine?.EcuId != null,
            };
            if (have)
                continue;
            ct.ThrowIfCancellationRequested();
            _log($"J1939 request PGN {pgn} (0x{pgn:X4}) to engine (0x00)");
            bus.Send(J1939Message.Request(pgn, bus.OwnAddress, SourceAddresses.Engine1));
            Listen(() => bus.Receive(), m => HandleJ1939(m, result), ct);
        }
    }

    private void HandleJ1939(J1939Message m, ScanResult result)
    {
        if (!J1939IdPgns.Contains(m.Pgn))
            return;
        var ecu = GetOrAdd(result, "J1939", m.Source, SourceAddresses.Describe(m.Source));
        switch (m.Pgn)
        {
            case Pgns.AddressClaimed:
                ecu.ManufacturerCode = J1939Decoder.DecodeName(m.Data).ManufacturerCode;
                break;
            case Pgns.VehicleIdentification:
                ecu.Vin = J1939Decoder.DecodeVin(m.Data);
                break;
            case Pgns.ComponentIdentification:
                ecu.Component = J1939Decoder.DecodeComponentId(m.Data);
                break;
            case Pgns.EcuIdentification:
                ecu.EcuId = J1939Decoder.DecodeEcuId(m.Data);
                break;
            case Pgns.SoftwareIdentification:
                ecu.SoftwareIds.Clear();
                ecu.SoftwareIds.AddRange(J1939Decoder.DecodeSoftwareIds(m.Data));
                break;
        }
        _log($"  <- 0x{m.Source:X2} PGN {m.Pgn}: {BitConverter.ToString(m.Data)}");
    }

    private void ScanJ1587(IJ1708Bus bus, ScanResult result, CancellationToken ct)
    {
        foreach (var pid in new[] { J1587Decoder.PidVin, J1587Decoder.PidComponentId, J1587Decoder.PidSoftwareId })
        {
            ct.ThrowIfCancellationRequested();
            _log($"J1587 request PID {pid}");
            bus.Send(J1587Decoder.BuildRequest(pid));
            Listen(() => bus.Receive(), m => HandleJ1587(m, result), ct);
        }
    }

    private void HandleJ1587(J1587Message m, ScanResult result)
    {
        foreach (var p in m.Parameters)
        {
            if (p.Pid is not (J1587Decoder.PidVin or J1587Decoder.PidComponentId or J1587Decoder.PidSoftwareId))
                continue;
            var ecu = GetOrAdd(result, "J1587", m.Mid, J1587Decoder.DescribeMid(m.Mid));
            switch (p.Pid)
            {
                case J1587Decoder.PidVin:
                    ecu.Vin = J1587Decoder.Ascii(p.Data);
                    break;
                case J1587Decoder.PidComponentId:
                    var (make, model, serial) = J1587Decoder.DecodeComponentId(p.Data);
                    ecu.Component = new ComponentId(make, model, serial, "");
                    break;
                case J1587Decoder.PidSoftwareId:
                    ecu.SoftwareIds.Clear();
                    ecu.SoftwareIds.AddRange(J1587Decoder.Ascii(p.Data).Split('*', StringSplitOptions.RemoveEmptyEntries));
                    break;
            }
            _log($"  <- MID {m.Mid} PID {p.Pid}: {J1587Decoder.Ascii(p.Data)}");
        }
    }

    private static EcuReport GetOrAdd(ScanResult result, string protocol, int address, string label)
    {
        var ecu = result.Ecus.FirstOrDefault(e => e.Protocol == protocol && e.Address == address);
        if (ecu == null)
        {
            ecu = new EcuReport { Protocol = protocol, Address = address, Label = label };
            result.Ecus.Add(ecu);
        }
        return ecu;
    }

    private void Listen<T>(Func<T?> receive, Action<T> handle, CancellationToken ct) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < _options.ListenWindow)
        {
            ct.ThrowIfCancellationRequested();
            var m = receive();
            if (m == null)
            {
                Thread.Sleep(5);
                continue;
            }
            handle(m);
        }
    }
}
