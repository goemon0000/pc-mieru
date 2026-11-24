using System.Diagnostics;
using PcMieru.Wire.Gate;

namespace PcMieru.Gate.Tests;

/// <summary>
/// The one thing in the gate that depends on who the program runs as: the Helper (and the network meter) can be started by Windows with other credentials than the user of the app
/// (an elevated Helper of an administrator account that is not the account that unzipped the files). They still have to be able to READ the gate, or every elevated start would be refused
/// as "Denied". This test checks it with a real second account, which only the owner of a machine can set up, so it does nothing unless the environment says so.
///
/// To run it: set PCMIERU_ALT_CREDENTIAL_TEST=1 and PCMIERU_ALT_USER (DOMAIN\name or .\name) and PCMIERU_ALT_PASSWORD of an account that has no rights on the scratch folder other than what
/// a user normally has on a folder under another account's temp folder (nothing is granted for it here: that is what is tested), then run this test only. It is NOT part of any default run.
///
/// What it checks: the Helper and the NetMeter, started with those credentials from a copy under a scratch folder whose gate file inherits the scratch folder's rights, end with their own
/// "no arguments" exit (2) and not with the gate's Denied (Helper 4, NetMeter 5): the gate file of an install is readable by the programs of the install whoever runs them.
/// </summary>
public sealed class GateAltCredentialTests : IClassFixture<Boxes>
{
    private readonly Boxes _boxes;

    public GateAltCredentialTests(Boxes boxes) => _boxes = boxes;

    [AltCredentialTheory]
    [InlineData(Exe.Helper)]
    [InlineData(Exe.NetMeter)]
    public void A_program_started_with_other_credentials_can_still_read_the_gate_of_the_install(Exe exe)
    {
        var box = _boxes.Get(exe);
        var user = Environment.GetEnvironmentVariable("PCMIERU_ALT_USER");
        var password = Environment.GetEnvironmentVariable("PCMIERU_ALT_PASSWORD");
        Assert.False(string.IsNullOrEmpty(user) || password is null, "PCMIERU_ALT_USER and PCMIERU_ALT_PASSWORD are needed");

        var domain = ".";
        var name = user!;
        var slash = user!.IndexOf('\\', StringComparison.Ordinal);
        if (slash > 0)
        {
            domain = user[..slash];
            name = user[(slash + 1)..];
        }

        var info = new ProcessStartInfo(box.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = box.Folder,
            UserName = name,
            Domain = domain,
            LoadUserProfile = false,
        };
        info.PasswordInClearText = password;
        var ran = BoundedProcess.Run(info, TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(InstallGate.ReasonOf(GateOutcome.Denied), ran.Output, StringComparison.Ordinal);
        Assert.Equal(2, ran.ExitCode); // "no arguments": past the gate, into its own check
    }
}

/// <summary>A theory that runs only when PCMIERU_ALT_CREDENTIAL_TEST=1 (the owner's own machine, with a second account): skipped with that reason otherwise.</summary>
public sealed class AltCredentialTheoryAttribute : TheoryAttribute
{
    public AltCredentialTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("PCMIERU_ALT_CREDENTIAL_TEST") != "1")
        {
            Skip = "needs a second Windows account: set PCMIERU_ALT_CREDENTIAL_TEST=1, PCMIERU_ALT_USER and PCMIERU_ALT_PASSWORD (not run by default)";
        }
    }
}
