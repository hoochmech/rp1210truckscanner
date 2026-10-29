using System;
using System.Collections.Generic;
using Rp1210Scanner.Core.J1587;
using Rp1210Scanner.Core.J1939;
using Rp1210Scanner.Core.Rp1210;

namespace Rp1210Scanner.Core.Bus;

public interface IJ1939Bus : IDisposable
{
    byte OwnAddress { get; }
    void Send(J1939Message message);
    /// <summary>Returns the next received message, or null when none is waiting.</summary>
    J1939Message? Receive();
}

public interface IJ1708Bus : IDisposable
{
    void Send(byte[] rp1210Frame);
    J1587Message? Receive();
}

/// <summary>J1939 client on a real RP1210 adapter.</summary>
public sealed class Rp1210J1939Bus : IJ1939Bus
{
    private readonly Rp1210Api _api;
    private readonly short _client;
    private readonly byte[] _rx = new byte[2048];

    public byte OwnAddress { get; }

    // Arbitrary-address-capable, function 129 (off-board diagnostic-service tool), industry group 0.
    private const ulong ToolName = 0x8000_8100_0000_1210UL;

    public Rp1210J1939Bus(Rp1210Api api, short deviceId, byte ownAddress = SourceAddresses.OffboardDiagnosticTool1)
    {
        _api = api;
        OwnAddress = ownAddress;
        _client = ConnectWithFallback(api, deviceId, "J1939:Baud=Auto", "J1939:Baud=500", "J1939:Baud=250", "J1939");

        _api.SendCommand(Rp1210Commands.SetAllFiltersStatesToPass, _client, Array.Empty<byte>());

        // Claim an address so ECUs can answer us with RTS/CTS transport sessions.
        var claim = new byte[10];
        claim[0] = ownAddress;
        BitConverter.GetBytes(ToolName).CopyTo(claim, 1);
        claim[9] = 0; // block until the claim completes
        var rc = _api.SendCommand(Rp1210Commands.ProtectJ1939Address, _client, claim);
        if (rc != 0)
            throw new Rp1210Exception(rc, _api.ErrorText(rc), "RP1210_Protect_J1939_Address");
    }

    /// <summary>Drivers differ in which J1939 protocol strings they accept (Baud=Auto is RP1210C).</summary>
    internal static short ConnectWithFallback(Rp1210Api api, short deviceId, params string[] protocols)
    {
        Rp1210Exception? last = null;
        foreach (var p in protocols)
        {
            try { return api.ClientConnect(deviceId, p); }
            catch (Rp1210Exception ex) { last = ex; }
        }
        throw last!;
    }

    public void Send(J1939Message message)
    {
        var rc = _api.SendMessage(_client, message.ToRp1210Tx());
        if (rc != 0)
            throw new Rp1210Exception(rc, _api.ErrorText(rc), "RP1210_SendMessage");
    }

    public J1939Message? Receive()
    {
        var n = _api.ReadMessage(_client, _rx);
        return n > 0 ? J1939Message.FromRp1210Rx(_rx.AsSpan(0, n)) : null;
    }

    public void Dispose() => _api.ClientDisconnect(_client);
}

/// <summary>J1708/J1587 client on a real RP1210 adapter.</summary>
public sealed class Rp1210J1708Bus : IJ1708Bus
{
    private readonly Rp1210Api _api;
    private readonly short _client;
    private readonly byte[] _rx = new byte[512];

    public Rp1210J1708Bus(Rp1210Api api, short deviceId)
    {
        _api = api;
        _client = api.ClientConnect(deviceId, "J1708");
        _api.SendCommand(Rp1210Commands.SetAllFiltersStatesToPass, _client, Array.Empty<byte>());
    }

    public void Send(byte[] rp1210Frame)
    {
        var rc = _api.SendMessage(_client, rp1210Frame);
        if (rc != 0)
            throw new Rp1210Exception(rc, _api.ErrorText(rc), "RP1210_SendMessage");
    }

    public J1587Message? Receive()
    {
        var n = _api.ReadMessage(_client, _rx);
        return n > 0 ? J1587Decoder.FromRp1210Rx(_rx.AsSpan(0, n)) : null;
    }

    public void Dispose() => _api.ClientDisconnect(_client);
}

/// <summary>
/// A fake truck that answers identification requests, so the UI and scanner can be exercised without an adapter.
/// </summary>
public sealed class SimulatedJ1939Bus : IJ1939Bus
{
    private readonly Queue<J1939Message> _pending = new();
    private readonly Dictionary<(byte Sa, int Pgn), byte[]> _responses = new();

    public byte OwnAddress => SourceAddresses.OffboardDiagnosticTool1;

    public static SimulatedJ1939Bus TypicalTruck()
    {
        var bus = new SimulatedJ1939Bus();
        bus.Add(0x00, Pgns.VehicleIdentification, "1XKYD49X7LJ123456*");
        bus.Add(0x00, Pgns.ComponentIdentification, "CMMNS*X15*79912345*EN-1*");
        bus.Add(0x00, Pgns.EcuIdentification, "5579408*GZ19123456*Engine*CM2350*Cummins*A029P123*");
        bus.Add(0x00, Pgns.SoftwareIdentification, new byte[] { 1 }, "00003501540001*");
        bus.Add(0x03, Pgns.ComponentIdentification, "ALLSN*4000RDS*6510123456*TR-1*");
        bus.Add(0x0B, Pgns.ComponentIdentification, "BNDWS*EC-80*JB123456**");
        bus.Add(0x00, Pgns.AddressClaimed, BitConverter.GetBytes(0x0000_0000_01E0_0001UL)); // mfr code 15
        return bus;
    }

    public void Add(byte sa, int pgn, string ascii) => Add(sa, pgn, Array.Empty<byte>(), ascii);

    public void Add(byte sa, int pgn, byte[] prefix, string ascii)
    {
        var body = new byte[prefix.Length + ascii.Length];
        prefix.CopyTo(body, 0);
        for (var i = 0; i < ascii.Length; i++)
            body[prefix.Length + i] = (byte)ascii[i];
        Add(sa, pgn, body);
    }

    public void Add(byte sa, int pgn, byte[] data) => _responses[(sa, pgn)] = data;

    public void Send(J1939Message message)
    {
        if (message.Pgn != Pgns.Request || message.Data.Length < 3)
            return;
        var pgn = message.Data[0] | (message.Data[1] << 8) | (message.Data[2] << 16);
        foreach (var ((sa, p), data) in _responses)
        {
            if (p != pgn || (message.Destination != J1939Message.GlobalAddress && message.Destination != sa))
                continue;
            _pending.Enqueue(new J1939Message(pgn, 6, sa, J1939Message.GlobalAddress, data));
        }
    }

    public J1939Message? Receive() => _pending.Count > 0 ? _pending.Dequeue() : null;

    public void Dispose() { }
}
