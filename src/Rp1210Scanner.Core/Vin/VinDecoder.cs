using System;
using System.Collections.Generic;

namespace Rp1210Scanner.Core.Vin;

public sealed record VinInfo(string Vin, bool IsValidFormat, bool CheckDigitOk, string Manufacturer, int? ModelYear);

/// <summary>
/// Offline VIN decoding: WMI to truck make, model-year character, and the North American check digit.
/// The WMI table is a starter set of common heavy-truck makers; extend it as you meet new ones.
/// </summary>
public static class VinDecoder
{
    private static readonly Dictionary<string, string> Wmi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1FU"] = "Freightliner", ["1FV"] = "Freightliner", ["3AK"] = "Freightliner", ["3AL"] = "Freightliner",
        ["1XK"] = "Kenworth", ["1NK"] = "Kenworth", ["2NK"] = "Kenworth",
        ["1XP"] = "Peterbilt", ["1NP"] = "Peterbilt", ["2NP"] = "Peterbilt",
        ["4V4"] = "Volvo", ["4V5"] = "Volvo", ["4VG"] = "Volvo",
        ["1M1"] = "Mack", ["1M2"] = "Mack",
        ["1HT"] = "International", ["1HS"] = "International", ["3HS"] = "International",
        ["5KJ"] = "Western Star", ["5KK"] = "Western Star",
    };

    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY123456789"; // 1980..2009, repeating every 30 years

    public static VinInfo Decode(string vin, int? currentYear = null)
    {
        vin = (vin ?? "").Trim().ToUpperInvariant();
        var validFormat = vin.Length == 17 && vin.IndexOfAny(new[] { 'I', 'O', 'Q' }) < 0 && IsAlnum(vin);
        if (!validFormat)
            return new VinInfo(vin, false, false, "", null);

        var maker = Wmi.TryGetValue(vin[..3], out var m) ? m : "";
        return new VinInfo(vin, true, CheckDigitOk(vin), maker, ModelYear(vin[9], currentYear ?? DateTime.Now.Year));
    }

    /// <summary>The year code repeats every 30 years; pick the most recent year not after next year.</summary>
    public static int? ModelYear(char code, int currentYear)
    {
        var idx = YearCodes.IndexOf(char.ToUpperInvariant(code));
        if (idx < 0)
            return null;
        var year = 1980 + idx;
        while (year + 30 <= currentYear + 1)
            year += 30;
        return year;
    }

    public static bool CheckDigitOk(string vin)
    {
        int[] weights = { 8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2 };
        var sum = 0;
        for (var i = 0; i < 17; i++)
            sum += Transliterate(vin[i]) * weights[i];
        var r = sum % 11;
        return vin[8] == (r == 10 ? 'X' : (char)('0' + r));
    }

    private static int Transliterate(char c)
    {
        if (char.IsDigit(c))
            return c - '0';
        return c switch
        {
            'A' or 'J' => 1, 'B' or 'K' or 'S' => 2, 'C' or 'L' or 'T' => 3, 'D' or 'M' or 'U' => 4,
            'E' or 'N' or 'V' => 5, 'F' or 'W' => 6, 'G' or 'P' or 'X' => 7, 'H' or 'Y' => 8, 'R' or 'Z' => 9,
            _ => 0,
        };
    }

    private static bool IsAlnum(string s)
    {
        foreach (var c in s)
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        return true;
    }
}

/// <summary>Turns the abbreviated make codes ECUs report (e.g. "CMMNS") into readable names.</summary>
public static class EngineMakes
{
    private static readonly (string Token, string Name)[] Known =
    {
        ("CMMNS", "Cummins"), ("CUMMINS", "Cummins"),
        ("DDC", "Detroit Diesel"), ("DETROIT", "Detroit Diesel"), ("DDEC", "Detroit Diesel"),
        ("PACCR", "PACCAR"), ("PACCAR", "PACCAR"),
        ("VOLVO", "Volvo"), ("MACK", "Mack"),
        ("NAVSTR", "Navistar"), ("NAVISTAR", "Navistar"), ("INTL", "Navistar"),
        ("CAT", "Caterpillar"),
        ("ALLSN", "Allison"), ("ALLISON", "Allison"), ("EATON", "Eaton"),
        ("BNDWS", "Bendix"), ("BENDIX", "Bendix"), ("WABCO", "WABCO"),
    };

    public static string Normalize(string make)
    {
        if (string.IsNullOrWhiteSpace(make))
            return "";
        var upper = make.Trim().ToUpperInvariant();
        foreach (var (token, name) in Known)
            if (upper.StartsWith(token, StringComparison.Ordinal))
                return name;
        return make.Trim();
    }
}
