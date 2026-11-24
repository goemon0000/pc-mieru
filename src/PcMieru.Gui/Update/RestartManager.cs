namespace PcMieru.Gui;

internal static class RestartManager
{
    internal readonly record struct Holder(int Pid, string Name);

    public static IReadOnlyList<Holder>? ReadHolders(string gateFile) =>
        PcMieru.Update.Transaction.RestartManager.ReadHolders(gateFile)?.Select(holder => new Holder(holder.Pid, holder.Name)).ToArray();

    internal static GateHolders Classify(IReadOnlyList<Holder>? holders, IReadOnlySet<int> ownPids)
    {
        if (holders is null)
        {
            return GateHolders.Unknown;
        }

        var others = holders.Where(holder => !ownPids.Contains(holder.Pid)).ToArray();
        return others.Any(holder => holder.Name.Equals("pcmi", StringComparison.OrdinalIgnoreCase))
            ? GateHolders.Pcmi : others.Length > 0 ? GateHolders.Other : GateHolders.Clear;
    }

}
