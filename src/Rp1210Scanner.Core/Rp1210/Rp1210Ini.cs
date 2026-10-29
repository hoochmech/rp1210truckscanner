using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rp1210Scanner.Core.Rp1210;

/// <summary>A device exposed by one vendor's RP1210 driver (a [DeviceInformationN] section).</summary>
public sealed record Rp1210Device(short DeviceId, string Name, string Description, IReadOnlyList<string> Protocols);

/// <summary>One installed RP1210 driver, listed in RP1210.ini under APIImplementations.</summary>
public sealed record Rp1210Vendor(string ImplementationName, string VendorName, string DllName, IReadOnlyList<Rp1210Device> Devices)
{
    public override string ToString() => $"{VendorName} ({ImplementationName})";
}

/// <summary>
/// Reads the RP1210 driver registry that adapter vendors install into %WINDIR%:
/// RP1210.ini lists implementation names, and each NAME.ini describes devices and protocols for NAME.dll.
/// </summary>
public static class Rp1210Ini
{
    public static string WindowsDir =>
        Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static IReadOnlyList<Rp1210Vendor> DiscoverVendors() => DiscoverVendors(WindowsDir);

    public static IReadOnlyList<Rp1210Vendor> DiscoverVendors(string iniDirectory)
    {
        var rootPath = Path.Combine(iniDirectory, "RP1210.ini");
        if (!File.Exists(rootPath))
            return Array.Empty<Rp1210Vendor>();

        var root = IniFile.Parse(File.ReadAllText(rootPath));
        var implementations = root.Get("RP1210Support", "APIImplementations") ?? "";

        var vendors = new List<Rp1210Vendor>();
        foreach (var name in implementations.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vendorPath = Path.Combine(iniDirectory, name + ".ini");
            if (!File.Exists(vendorPath))
                continue;
            vendors.Add(ParseVendor(name, File.ReadAllText(vendorPath)));
        }
        return vendors;
    }

    public static Rp1210Vendor ParseVendor(string implementationName, string iniText)
    {
        var ini = IniFile.Parse(iniText);
        var vendorName = ini.Get("VendorInformation", "Name") ?? implementationName;

        // Protocol sections list which device ids support them, e.g. Devices=1,2,3 (or "All").
        var protocolsByDevice = new Dictionary<short, List<string>>();
        var allDeviceProtocols = new List<string>();
        foreach (var section in ini.SectionNames.Where(s => s.StartsWith("ProtocolInformation", StringComparison.OrdinalIgnoreCase)))
        {
            var protocol = ini.Get(section, "ProtocolString");
            if (string.IsNullOrWhiteSpace(protocol))
                continue;
            var devices = ini.Get(section, "Devices") ?? "";
            if (devices.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                allDeviceProtocols.Add(protocol.Trim());
                continue;
            }
            foreach (var d in devices.Split(','))
            {
                if (!short.TryParse(d.Trim(), out var id))
                    continue;
                if (!protocolsByDevice.TryGetValue(id, out var list))
                    protocolsByDevice[id] = list = new List<string>();
                list.Add(protocol.Trim());
            }
        }

        var devicesOut = new List<Rp1210Device>();
        foreach (var section in ini.SectionNames.Where(s => s.StartsWith("DeviceInformation", StringComparison.OrdinalIgnoreCase)))
        {
            if (!short.TryParse(ini.Get(section, "DeviceID"), out var id))
                continue;
            var protocols = protocolsByDevice.TryGetValue(id, out var p) ? p.Concat(allDeviceProtocols) : allDeviceProtocols;
            devicesOut.Add(new Rp1210Device(
                id,
                ini.Get(section, "DeviceName") ?? $"Device {id}",
                ini.Get(section, "DeviceDescription") ?? "",
                protocols.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new Rp1210Vendor(implementationName, vendorName, implementationName + ".dll", devicesOut);
    }
}

/// <summary>Minimal case-insensitive INI reader; RP1210 files are plain key=value sections.</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public IEnumerable<string> SectionNames => _order;

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                var name = line[1..^1].Trim();
                if (!ini._sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ini._sections[name] = current;
                    ini._order.Add(name);
                }
                continue;
            }
            var eq = line.IndexOf('=');
            if (current == null || eq <= 0)
                continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return ini;
    }
}
