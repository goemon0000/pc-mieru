using PcMieru.Core.Hardware.Native;

namespace PcMieru.Core.Hardware.Collectors;

/// <summary>Finds the PCI function a device hangs off and reads its negotiated and maximum link state from the PnP database.</summary>
internal sealed record PciNode(uint DevInst, string InstanceId, string? DriverVersion, string? DriverService)
{
    private const string Prefix = "setupapi:DEVPKEY_PciDevice_";

    public static PciNode? FindFrom(uint devInst)
    {
        string? version = null;
        string? service = null;
        var current = (uint?)devInst;
        for (var depth = 0; depth < 8 && current is { } cur; depth++)
        {
            version ??= DevInstProps.GetString(cur, DevKeys.DriverVersion);
            service ??= DevInstProps.GetString(cur, DevKeys.Service);
            var id = DevInstProps.InstanceId(cur);
            if (id is not null && id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            {
                return new PciNode(cur, id, version, service);
            }

            current = DevInstProps.Parent(cur);
        }

        return null;
    }

    private Value<int> Prop(DevPropKey key, string name, Func<long, bool> valid) =>
        DevInstProps.GetInteger(DevInst, key) is { } v && valid(v)
            ? Value.Ok((int)v, Prefix + name)
            : Value.Unsupported<int>(Prefix + name, "the PnP database has no value for this device");

    public Value<int> CurrentGeneration => Prop(DevKeys.PciCurrentLinkSpeed, "CurrentLinkSpeed", v => v is >= 1 and <= 8);

    public Value<int> CurrentWidth => Prop(DevKeys.PciCurrentLinkWidth, "CurrentLinkWidth", v => v is >= 1 and <= 32);

    public Value<int> MaxGeneration => Prop(DevKeys.PciMaxLinkSpeed, "MaxLinkSpeed", v => v is >= 1 and <= 8);

    public Value<int> MaxWidth => Prop(DevKeys.PciMaxLinkWidth, "MaxLinkWidth", v => v is >= 1 and <= 32);
}
