using System.Runtime.InteropServices;
using PcMieru.Core.Collection;
using Xunit.Abstractions;

namespace PcMieru.Core.Tests.Collection;

public sealed class SignatureVerifierTests(ITestOutputHelper output)
{
    private static string System32(string name) => Path.Combine(Environment.SystemDirectory, name);

    private static string DotnetHost()
    {
        // ...\dotnet\shared\Microsoft.NETCore.App\<version>\ -> ...\dotnet\dotnet.exe
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var host = Path.Combine(root, "dotnet.exe");
        Assert.True(File.Exists(host), $"dotnet.exe not found next to the runtime at {host}");
        return host;
    }

    [Fact]
    public void A_system_file_is_signed_by_Microsoft_whether_embedded_or_by_catalog()
    {
        var verifier = new SignatureVerifier();

        var result = verifier.Verify(System32("kernel32.dll"));

        output.WriteLine($"kernel32.dll: {result.Data!.State} / {result.Data.Signer} / {result.Detail}");
        Assert.True(result.IsOk, result.Detail);
        Assert.True(result.Data!.State is SignatureState.SignedEmbedded or SignatureState.SignedCatalog);
        Assert.Contains("Microsoft", result.Data.Signer);
        Assert.Equal(SignatureVerifier.Source, result.Source);
    }

    [Fact]
    public void A_file_with_an_embedded_signature_reports_the_signer()
    {
        var result = new SignatureVerifier().Verify(DotnetHost());

        output.WriteLine($"dotnet.exe: {result.Data!.State} / {result.Data.Signer}");
        Assert.Equal(SignatureState.SignedEmbedded, result.Data.State);

        // The signer is the certificate's own display name (what the file's Digital Signatures tab shows), not a company we infer.
        Assert.False(string.IsNullOrWhiteSpace(result.Data.Signer));
    }

    [Fact]
    public void A_catalog_signed_system_exe_is_found_through_the_catalog()
    {
        // Windows ships notepad.exe, cmd.exe ... with no embedded signature on most builds; at least one of these is catalog-signed.
        var verifier = new SignatureVerifier();
        var states = new[] { "cmd.exe", "conhost.exe", "svchost.exe", "services.exe", "lsass.exe", "smss.exe" }
            .Select(n => (n, verifier.Verify(System32(n))))
            .ToList();

        foreach (var (name, value) in states)
        {
            output.WriteLine($"{name}: {value.Status} {value.Data?.State} / {value.Data?.Signer}");
        }

        Assert.All(states, s => Assert.True(s.Item2.IsOk, s.n + ": " + s.Item2.Detail));
        Assert.All(states, s => Assert.True(s.Item2.Data!.State is SignatureState.SignedEmbedded or SignatureState.SignedCatalog, s.n));
    }

    [Fact]
    public void An_unsigned_file_is_Unsigned_not_Invalid()
    {
        var self = typeof(SignatureVerifier).Assembly.Location;

        var result = new SignatureVerifier().Verify(self);

        Assert.True(result.IsOk, result.Detail);
        Assert.Equal(SignatureState.Unsigned, result.Data!.State);
        Assert.Null(result.Data.Signer);
    }

    [Fact]
    public void A_signed_file_changed_after_signing_is_Invalid_and_says_why()
    {
        var folder = Directory.CreateTempSubdirectory("pcmieru-sig-");
        try
        {
            var copy = Path.Combine(folder.FullName, "tampered.exe");
            File.Copy(DotnetHost(), copy);
            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                // Somewhere inside the first section: covered by the signature, not part of the certificate table.
                stream.Position = 0x600;
                var original = stream.ReadByte();
                stream.Position = 0x600;
                stream.WriteByte((byte)(original ^ 0xFF));
            }

            var result = new SignatureVerifier().Verify(copy);

            output.WriteLine($"tampered: {result.Data?.State} / {result.Data?.Signer} / {result.Detail}");
            Assert.True(result.IsOk);
            Assert.Equal(SignatureState.Invalid, result.Data!.State);
            Assert.False(string.IsNullOrEmpty(result.Detail));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_a_failure_not_an_exception_and_is_not_remembered()
    {
        var verifier = new SignatureVerifier();

        var result = verifier.Verify(Path.Combine(Path.GetTempPath(), "pcmieru-no-such-file.exe"));

        Assert.Equal(ValueStatus.Error, result.Status);
        Assert.Equal(0, verifier.CacheCount);
    }

    [Fact]
    public void The_same_file_is_verified_once()
    {
        var verifier = new SignatureVerifier();
        var path = System32("kernel32.dll");

        var first = verifier.Verify(path);
        var second = verifier.Verify(path);

        Assert.Equal(1, verifier.Verifications);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_file_that_changes_is_verified_again()
    {
        var folder = Directory.CreateTempSubdirectory("pcmieru-sig-");
        try
        {
            var copy = Path.Combine(folder.FullName, "copy.exe");
            File.Copy(DotnetHost(), copy);
            var verifier = new SignatureVerifier();
            Assert.Equal(SignatureState.SignedEmbedded, verifier.Verify(copy).Data!.State);

            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = 0x600;
                var original = stream.ReadByte();
                stream.Position = 0x600;
                stream.WriteByte((byte)(original ^ 0xFF));
            }

            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(1));

            Assert.Equal(SignatureState.Invalid, verifier.Verify(copy).Data!.State);
            Assert.Equal(2, verifier.Verifications);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Verification_works_from_several_threads_at_once()
    {
        var verifier = new SignatureVerifier();
        var paths = new[] { "kernel32.dll", "user32.dll", "ntdll.dll", "cmd.exe" }.Select(System32).ToArray();

        var results = paths.AsParallel().WithDegreeOfParallelism(4).Select(p => verifier.Verify(p)).ToList();

        Assert.All(results, r => Assert.True(r.IsOk, r.Detail));
    }
}
