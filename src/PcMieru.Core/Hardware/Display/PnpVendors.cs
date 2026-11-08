namespace PcMieru.Core.Hardware.Display;

/// <summary>
/// Monitor manufacturer ids (the three letters in an EDID) for the brands people actually own. This is a lookup of ids from the
/// public UEFI PNP registry; an id that is not listed is shown as the raw three letters, never guessed.
/// </summary>
internal static class PnpVendors
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer",
        ["AOC"] = "AOC",
        ["APP"] = "Apple",
        ["AUS"] = "ASUS",
        ["AUO"] = "AU Optronics",
        ["BNQ"] = "BenQ",
        ["BOE"] = "BOE",
        ["CMN"] = "Innolux",
        ["CMO"] = "Chi Mei",
        ["DEL"] = "Dell",
        ["EIZ"] = "EIZO",
        ["ENC"] = "EIZO",
        ["FUS"] = "Fujitsu Siemens",
        ["GBT"] = "Gigabyte",
        ["GSM"] = "LG Electronics",
        ["HPN"] = "HP",
        ["HSD"] = "Hannspree",
        ["HWP"] = "HP",
        ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display",
        ["LPL"] = "LG Philips",
        ["MAG"] = "MAG InnoVision",
        ["MEI"] = "Panasonic",
        ["MSI"] = "MSI",
        ["NEC"] = "NEC",
        ["PHL"] = "Philips",
        ["SAM"] = "Samsung",
        ["SDC"] = "Samsung Display",
        ["SEC"] = "Seiko Epson",
        ["SHP"] = "Sharp",
        ["SNY"] = "Sony",
        ["TOS"] = "Toshiba",
        ["TSB"] = "Toshiba",
        ["VSC"] = "ViewSonic",
        ["WAC"] = "Wacom",
        ["XMI"] = "Xiaomi",
        ["KDS"] = "Korea Data Systems",
        ["PLN"] = "Planar",
        ["IOD"] = "I-O DATA",
    };

    public static string? Name(string pnpId) => Names.GetValueOrDefault(pnpId);
}
