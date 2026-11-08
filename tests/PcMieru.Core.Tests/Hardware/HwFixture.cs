namespace PcMieru.Core.Tests.Hardware;

/// <summary>Binary fixtures for the hardware parsers (scrubbed real dumps, see Fixtures/README in the commit message).</summary>
internal static class HwFixture
{
    public static string Path(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Fixture.Dir)!, "Hardware", "Fixtures", name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));
}
