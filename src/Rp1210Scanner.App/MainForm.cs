using System.Text;
using Rp1210Scanner.Core.Bus;
using Rp1210Scanner.Core.Rp1210;
using Rp1210Scanner.Core.Scan;

namespace Rp1210Scanner.App;

/// <summary>Pick an adapter, scan, and show what the truck reports. Built in code; no designer file.</summary>
public sealed class MainForm : Form
{
    private sealed record AdapterChoice(string Label, Rp1210Vendor? Vendor, Rp1210Device? Device)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly CheckBox _j1587 = new() { Text = "J1587 fallback", Checked = true, AutoSize = true };
    private readonly Button _scan = new() { Text = "Scan truck", AutoSize = true };
    private readonly Button _copy = new() { Text = "Copy report", AutoSize = true, Enabled = false };
    private readonly ListView _summary = MakeList(("Field", 160), ("Value", 420));
    private readonly ListView _ecus = MakeList(("Bus", 55), ("Addr", 50), ("Module", 170), ("Make", 90), ("Model", 100),
        ("Serial", 110), ("ECU type", 90), ("Part #", 100), ("Software", 160), ("Mfr code", 65));
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
    private ScanResult? _last;

    public MainForm()
    {
        Text = "RP1210 Truck Scanner";
        Width = 1100;
        Height = 760;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        toolbar.Controls.AddRange(new Control[] { new Label { Text = "Adapter:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _adapters, _refresh, _j1587, _scan, _copy });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_summary);
        lower.Panel1.Controls.Add(_ecus);
        lower.Panel2.Controls.Add(_log);
        split.Panel2.Controls.Add(lower);

        Controls.Add(split);
        Controls.Add(toolbar);

        // Splitter distances must be set once the containers have a real size.
        Load += (_, _) => { split.SplitterDistance = 240; lower.SplitterDistance = 200; };

        _refresh.Click += (_, _) => LoadAdapters();
        _scan.Click += async (_, _) => await ScanAsync();
        _copy.Click += (_, _) => { if (_last != null) Clipboard.SetText(Report(_last)); };

        LoadAdapters();
    }

    private static ListView MakeList(params (string Name, int Width)[] columns)
    {
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        foreach (var (name, width) in columns)
            lv.Columns.Add(name, width);
        return lv;
    }

    private void LoadAdapters()
    {
        _adapters.Items.Clear();
        try
        {
            foreach (var vendor in Rp1210Ini.DiscoverVendors())
                foreach (var device in vendor.Devices)
                    _adapters.Items.Add(new AdapterChoice($"{vendor.VendorName} - {device.Name} (id {device.DeviceId}) [{vendor.ImplementationName}]", vendor, device));
        }
        catch (Exception ex)
        {
            Log($"Could not read RP1210.ini: {ex.Message}");
        }
        if (_adapters.Items.Count == 0)
            Log("No RP1210 drivers found in RP1210.ini. Install your adapter's driver, or use the simulated truck.");
        _adapters.Items.Add(new AdapterChoice("Simulated truck (demo, no hardware)", null, null));
        _adapters.SelectedIndex = 0;
    }

    private async Task ScanAsync()
    {
        if (_adapters.SelectedItem is not AdapterChoice choice)
            return;
        _scan.Enabled = false;
        _summary.Items.Clear();
        _ecus.Items.Clear();
        var useJ1587 = _j1587.Checked;
        try
        {
            _last = await Task.Run(() => RunScan(choice, useJ1587));
            ShowResult(_last);
            _copy.Enabled = true;
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _scan.Enabled = true;
        }
    }

    private ScanResult RunScan(AdapterChoice choice, bool useJ1587)
    {
        var scanner = new TruckScanner(log: Log);
        if (choice.Vendor == null || choice.Device == null)
        {
            Log("Scanning simulated truck...");
            using var sim = SimulatedJ1939Bus.TypicalTruck();
            return scanner.Scan(sim, null);
        }

        Log($"Loading {choice.Vendor.DllName}...");
        using var api = new Rp1210Api(choice.Vendor.DllName);
        var protocols = choice.Device.Protocols;
        bool Supports(string p) => protocols.Count == 0 || protocols.Any(x => x.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        IJ1939Bus? j1939 = null;
        IJ1708Bus? j1708 = null;
        try
        {
            if (Supports("J1939"))
            {
                try { j1939 = new Rp1210J1939Bus(api, choice.Device.DeviceId); Log("Connected J1939."); }
                catch (Exception ex) { Log("J1939 connect failed: " + ex.Message); }
            }
            if (useJ1587 && Supports("J1708"))
            {
                try { j1708 = new Rp1210J1708Bus(api, choice.Device.DeviceId); Log("Connected J1708."); }
                catch (Exception ex) { Log("J1708 connect failed: " + ex.Message); }
            }
            if (j1939 == null && j1708 == null)
                throw new InvalidOperationException("Could not open J1939 or J1708 on this adapter. Is it plugged into the truck with the key on?");
            return scanner.Scan(j1939, j1708);
        }
        finally
        {
            j1939?.Dispose();
            j1708?.Dispose();
        }
    }

    private void ShowResult(ScanResult r)
    {
        void Row(string k, string? v) => _summary.Items.Add(new ListViewItem(new[] { k, string.IsNullOrWhiteSpace(v) ? "(not reported)" : v }));
        Row("Truck make (from VIN)", r.TruckMake);
        Row("Model year (from VIN)", r.ModelYear?.ToString());
        Row("VIN", r.Vin == null ? null : r.Vin.Vin + (r.Vin.CheckDigitOk ? "" : "  (check digit mismatch)"));
        Row("Engine make", r.EngineMake);
        Row("Engine model", r.EngineModel);
        Row("Engine serial", r.EngineSerial);
        Row("ECM type", r.EcmType);
        Row("ECM part number", r.EcmPartNumber);
        Row("Engine software", r.EngineSoftware);

        foreach (var e in r.Ecus.OrderBy(e => e.Protocol).ThenBy(e => e.Address))
        {
            _ecus.Items.Add(new ListViewItem(new[]
            {
                e.Protocol, e.Protocol == "J1939" ? $"0x{e.Address:X2}" : e.Address.ToString(), e.Label,
                e.Component?.Make ?? "", e.Component?.Model ?? "", e.Component?.SerialNumber ?? e.EcuId?.SerialNumber ?? "",
                e.EcuId?.Type ?? "", e.EcuId?.PartNumber ?? "", string.Join(", ", e.SoftwareIds),
                e.ManufacturerCode?.ToString() ?? "",
            }));
        }
        Log($"Done. {r.Ecus.Count} module(s) answered.");
    }

    private static string Report(ScanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VIN: {r.Vin?.Vin}");
        sb.AppendLine($"Truck: {r.ModelYear} {r.TruckMake}");
        sb.AppendLine($"Engine: {r.EngineMake} {r.EngineModel}  S/N {r.EngineSerial}");
        sb.AppendLine($"ECM: {r.EcmType}  P/N {r.EcmPartNumber}  SW {r.EngineSoftware}");
        sb.AppendLine();
        foreach (var e in r.Ecus)
            sb.AppendLine($"[{e.Protocol} {e.Address}] {e.Label}: {e.Component?.Make} {e.Component?.Model} {e.Component?.SerialNumber} {e.EcuId?.Type} {string.Join(",", e.SoftwareIds)}");
        return sb.ToString();
    }

    private void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(line));
            return;
        }
        _log.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
    }
}
