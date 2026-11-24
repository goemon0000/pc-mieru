using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>The name rules of DESIGN-B 3.2 (the table and list item 2), accept list and reject list, with the rule that holds for each rejection.</summary>
public class BundleNamesTests
{
    [Theory]
    // root exes
    [InlineData("pcmi.exe")]
    [InlineData("PCMieru.exe")]
    [InlineData("PCMieru.SensorHost.exe")]
    [InlineData("PCMieru.Helper.exe")]
    [InlineData("PCMieru.NetMeter.exe")]
    [InlineData("PCMieru.Swap.exe")]
    [InlineData("PCMieru.Party.exe")] // "passes this rule in v0.5" (the table says so)
    [InlineData("createdump.exe")]
    // root dlls
    [InlineData("PcMieru.Core.dll")]
    [InlineData("System.Runtime.dll")]
    [InlineData("Microsoft.Win32.Primitives.dll")]
    [InlineData("a.dll")]
    [InlineData("1.dll")]
    [InlineData("x_y-z.dll")]
    [InlineData("COM10.dll")] // not a device name (COM0 and LPT0 are: see the refused list)
    [InlineData("CONSOLE.dll")]
    [InlineData("LPT10.txt")]
    // side files
    [InlineData("PCMieru.deps.json")]
    [InlineData("PCMieru.runtimeconfig.json")]
    [InlineData("PCMieru.bundle.json")]
    [InlineData("PCMieru.gate.txt")]
    [InlineData("NOTICE.txt")]
    [InlineData("README.md")]
    [InlineData("PCMieru.pdb")]
    [InlineData("LICENSE")]
    [InlineData("LICENSE.txt")]
    // satellites
    [InlineData("ja/PcMieru.Core.resources.dll")]
    [InlineData("en/x.resources.dll")]
    public void Allowed_names_pass(string name)
    {
        var verdict = BundleNames.Check(name);
        Assert.True(verdict.Ok, verdict.ToString());
    }

    [Theory]
    [InlineData("", BundleFault.EmptyName)]
    // a backslash: what a ZIP made by .NET Framework's CreateFromDirectory (Windows PowerShell 5.1) has, so v0.2.0's zip is refused
    [InlineData("a\\b.dll", BundleFault.Backslash)]
    [InlineData("PCMieru-0.2.0-win-x64\\pcmi.exe", BundleFault.Backslash)]
    [InlineData("ja\\x.resources.dll", BundleFault.Backslash)]
    [InlineData("\\pcmi.exe", BundleFault.Backslash)]
    // absolute, drive, stream
    [InlineData("/pcmi.exe", BundleFault.LeadingSlash)]
    [InlineData("C:/x.dll", BundleFault.DriveName)]
    [InlineData("c:x.dll", BundleFault.DriveName)]
    [InlineData("a.dll:x", BundleFault.Colon)]
    [InlineData("a.dll::$DATA", BundleFault.Colon)]
    [InlineData("ja/a.resources.dll:x", BundleFault.Colon)]
    // empty, dot and dot-dot elements
    [InlineData("a//b.dll", BundleFault.EmptySegment)]
    [InlineData("ja/", BundleFault.EmptySegment)]
    [InlineData(".", BundleFault.DotSegment)]
    [InlineData("..", BundleFault.DotSegment)]
    [InlineData("./a.dll", BundleFault.DotSegment)]
    [InlineData("../a.dll", BundleFault.DotSegment)]
    [InlineData("ja/../a.dll", BundleFault.DotSegment)]
    // control characters
    [InlineData("a\u0001.dll", BundleFault.ControlCharacter)]
    [InlineData("a\u007f.dll", BundleFault.ControlCharacter)]
    [InlineData("a\u0085.dll", BundleFault.ControlCharacter)]
    [InlineData("a\n.dll", BundleFault.ControlCharacter)]
    [InlineData("a\0.dll", BundleFault.ControlCharacter)]
    // trailing dot or space (Windows drops it)
    [InlineData("a.", BundleFault.TrailingDotOrSpace)]
    [InlineData("a.dll.", BundleFault.TrailingDotOrSpace)]
    [InlineData("a ", BundleFault.TrailingDotOrSpace)]
    [InlineData("a.dll ", BundleFault.TrailingDotOrSpace)]
    [InlineData("ja /x.resources.dll", BundleFault.TrailingDotOrSpace)]
    // DOS device names, with or without an extension, in any case
    [InlineData("CON", BundleFault.DeviceName)]
    [InlineData("con.txt", BundleFault.DeviceName)]
    [InlineData("NUL.dll", BundleFault.DeviceName)]
    [InlineData("nul", BundleFault.DeviceName)]
    [InlineData("AUX.json", BundleFault.DeviceName)]
    [InlineData("PRN.md", BundleFault.DeviceName)]
    [InlineData("com1.dll", BundleFault.DeviceName)]
    [InlineData("COM9", BundleFault.DeviceName)]
    [InlineData("LPT1.txt", BundleFault.DeviceName)]
    [InlineData("lpt9", BundleFault.DeviceName)]
    [InlineData("Con.Dll", BundleFault.DeviceName)]
    [InlineData("COM0", BundleFault.DeviceName)] // Windows reserves COM0 and LPT0 as well (Naming Files, Paths, and Namespaces)
    [InlineData("com0.dll", BundleFault.DeviceName)]
    [InlineData("Com0.Txt", BundleFault.DeviceName)]
    [InlineData("COM0.resources.dll", BundleFault.DeviceName)]
    [InlineData("LPT0", BundleFault.DeviceName)]
    [InlineData("lpt0.txt", BundleFault.DeviceName)]
    [InlineData("LPt0.json", BundleFault.DeviceName)]
    [InlineData("LPT0 .txt", BundleFault.DeviceName)]
    [InlineData("ja/COM0.resources.dll", BundleFault.DeviceName)]
    [InlineData("ja/LPT0.x.resources.dll", BundleFault.DeviceName)]
    [InlineData("NUL.resources.dll", BundleFault.DeviceName)]
    [InlineData("NUL .txt", BundleFault.DeviceName)]
    [InlineData("ja/NUL.resources.dll", BundleFault.DeviceName)]
    [InlineData("ja/NUL.x.resources.dll", BundleFault.DeviceName)]
    // well-formed but not one of the allowed shapes
    [InlineData("sub/x.dll", BundleFault.NotAllowed)]
    [InlineData("fr/x.resources.dll", BundleFault.NotAllowed)]
    [InlineData("ja/x.dll", BundleFault.NotAllowed)]
    [InlineData("ja/sub/x.resources.dll", BundleFault.NotAllowed)]
    [InlineData("JA/x.resources.dll", BundleFault.NotAllowed)]
    [InlineData("ja/x.resources.DLL", BundleFault.NotAllowed)]
    [InlineData("ja/.resources.dll", BundleFault.NotAllowed)]
    [InlineData("x.exe", BundleFault.NotAllowed)]
    [InlineData("pcmieru.exe", BundleFault.NotAllowed)]
    [InlineData("PCMIERU.exe", BundleFault.NotAllowed)]
    [InlineData("PCMieru..exe", BundleFault.NotAllowed)]
    [InlineData("Setup.exe", BundleFault.NotAllowed)]
    [InlineData("x.DLL", BundleFault.NotAllowed)]
    [InlineData("x.Json", BundleFault.NotAllowed)]
    [InlineData("x.xml", BundleFault.NotAllowed)]
    [InlineData("x.bin", BundleFault.NotAllowed)]
    [InlineData("x.ps1", BundleFault.NotAllowed)]
    [InlineData("x.bat", BundleFault.NotAllowed)]
    [InlineData(".dll", BundleFault.NotAllowed)]
    [InlineData("noextension", BundleFault.NotAllowed)]
    [InlineData("license", BundleFault.NotAllowed)]
    [InlineData("-x.dll", BundleFault.NotAllowed)]
    [InlineData("_x.dll", BundleFault.NotAllowed)]
    [InlineData(".x.dll", BundleFault.NotAllowed)]
    [InlineData("a b.dll", BundleFault.NotAllowed)]
    [InlineData("a+b.dll", BundleFault.NotAllowed)]
    [InlineData("\u00e9.dll", BundleFault.NotAllowed)]
    [InlineData("\u65e5\u672c.dll", BundleFault.NotAllowed)]
    public void Refused_names_say_which_rule(string name, BundleFault expected)
    {
        var verdict = BundleNames.Check(name);
        Assert.False(verdict.Ok);
        Assert.Equal(expected, verdict.Fault);
        Assert.False(string.IsNullOrEmpty(verdict.Detail));
    }

    [Fact]
    public void Every_name_of_the_designed_floor_passes_the_rules()
    {
        foreach (var name in BundleFloor.Full)
        {
            Assert.True(BundleNames.Check(name).Ok, name);
        }
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void Only_the_two_satellite_folders_are_directories(string name)
    {
        Assert.True(BundleNames.CheckDirectory(name).Ok);
        Assert.True(BundleNames.IsSatelliteFolder(name));
    }

    [Theory]
    [InlineData("JA", BundleFault.DirectoryNotAllowed)]
    [InlineData("fr", BundleFault.DirectoryNotAllowed)]
    [InlineData("sub", BundleFault.DirectoryNotAllowed)]
    [InlineData("ja/x", BundleFault.DirectoryNotAllowed)]
    [InlineData("", BundleFault.EmptyName)]
    [InlineData("ja\\", BundleFault.Backslash)]
    [InlineData("CON", BundleFault.DeviceName)]
    [InlineData("..", BundleFault.DotSegment)]
    public void Other_directories_are_refused(string name, BundleFault expected)
    {
        var verdict = BundleNames.CheckDirectory(name);
        Assert.False(verdict.Ok);
        Assert.Equal(expected, verdict.Fault);
    }

    [Fact]
    public void The_character_rules_alone_do_not_judge_the_table()
    {
        Assert.True(BundleNames.CheckSyntax("PCMieru-0.5.0-win-x64/anything.xyz").Ok);
        Assert.Equal(BundleFault.Backslash, BundleNames.CheckSyntax("a\\b").Fault);
    }
}
