namespace PcMieru.SensorHost;

/// <summary>
/// One number as a vendor library gave it. <see cref="Code"/> 0 means it was read; <see cref="PcMieru.Core.Hardware.Sensors.SensorMapping.FunctionMissing"/>
/// means this driver does not have the function; anything else is the vendor's own result code. The number is only meaningful when Code is 0.
/// </summary>
internal readonly record struct VendorNumber(double Number, int Code)
{
    public static VendorNumber Missing => new(double.NaN, -1);

    public static VendorNumber Failed(int code) => new(double.NaN, code);

    public static VendorNumber Of(double number) => new(number, 0);

    public bool Ok => Code == 0;
}

/// <summary>A text (VBIOS version, driver version) with the same result code convention as <see cref="VendorNumber"/>.</summary>
internal readonly record struct VendorText(string? Text, int Code)
{
    public static VendorText Missing => new(null, -1);

    public static VendorText Failed(int code) => new(null, code);

    public static VendorText Of(string? text) => new(text, 0);
}
