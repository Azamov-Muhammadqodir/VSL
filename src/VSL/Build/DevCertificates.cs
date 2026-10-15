using System.Text.Json;

namespace VSL.Build;

public enum DevCertificateTrust
{
    /// <summary>The check could not run (no SDK, unexpected output): nothing is offered.</summary>
    Unknown,
    Missing,
    NotTrusted,
    /// <summary>Trusted somewhere but not everywhere (e.g. OpenSSL without SSL_CERT_DIR).</summary>
    Partial,
    Trusted,
}

/// <summary>
/// The ASP.NET Core HTTPS development certificate. On Linux `dotnet dev-certs https --trust` puts it in
/// ~/.aspnet/dev-certs/trust (and in Chromium / Firefox NSS databases through certutil); OpenSSL, and so .NET
/// and Kestrel, trust it once SSL_CERT_DIR lists that folder, which VSL sets for everything it starts.
/// </summary>
public static class DevCertificates
{
    static readonly string[] SystemCertDirectories = ["/etc/ssl/certs", "/etc/pki/tls/certs", "/usr/lib/ssl/certs", "/var/lib/ca-certificates/openssl", "/etc/ssl"];

    public static string TrustDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspnet", "dev-certs", "trust");

    /// <summary>
    /// SSL_CERT_DIR with the dev-cert folder first and the system certificates after it (setting the variable
    /// replaces OpenSSL's default folder, so that must stay listed); null when there is nothing to add.
    /// </summary>
    public static string? CertDirectoryValue(string? current, Func<string, bool> directoryExists)
    {
        var trust = TrustDirectory;
        if (!directoryExists(trust))
            return null;
        var directories = (current ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (directories.Contains(trust))
            return null;
        if (directories.Count == 0)
            directories.Add(SystemCertDirectories.FirstOrDefault(directoryExists) ?? "/etc/ssl/certs");
        return string.Join(':', [trust, .. directories]);
    }

    /// <summary>Makes the processes VSL starts (run, debug, terminal) trust the development certificate.</summary>
    public static void ApplyToCurrentProcess()
    {
        if (CertDirectoryValue(Environment.GetEnvironmentVariable("SSL_CERT_DIR"), Directory.Exists) is { } value)
            Environment.SetEnvironmentVariable("SSL_CERT_DIR", value);
    }

    public static async Task<DevCertificateTrust> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, output) = await RunAsync(["dev-certs", "https", "--check-trust-machine-readable"], null, ct);
            return exit == 0 ? Parse(output) : DevCertificateTrust.Unknown;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return DevCertificateTrust.Unknown;
        }
    }

    /// <summary>The best trust level among the development certificates `--check-trust-machine-readable` lists.</summary>
    internal static DevCertificateTrust Parse(string output)
    {
        var start = output.IndexOf('[');
        if (start < 0)
            return DevCertificateTrust.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var levels = doc.RootElement.EnumerateArray()
                .Where(c => !c.TryGetProperty("IsHttpsDevelopmentCertificate", out var dev) || dev.ValueKind == JsonValueKind.True)
                .Select(c => c.TryGetProperty("TrustLevel", out var level) ? level.GetString() : null)
                .ToList();
            return levels.Count == 0 ? DevCertificateTrust.Missing
                : levels.Contains("Full") ? DevCertificateTrust.Trusted
                : levels.Contains("Partial") ? DevCertificateTrust.Partial
                : DevCertificateTrust.NotTrusted;
        }
        catch (JsonException)
        {
            return DevCertificateTrust.Unknown;
        }
    }

    /// <summary>`dotnet dev-certs https --trust` (creates the certificate if needed), then applies SSL_CERT_DIR.</summary>
    public static async Task<bool> TrustAsync(Action<string> log, CancellationToken ct = default)
    {
        log("> dotnet dev-certs https --trust");
        var (exit, _) = await RunAsync(["dev-certs", "https", "--trust"], log, ct);
        ApplyToCurrentProcess();
        return exit == 0;
    }

    static async Task<(int Exit, string Output)> RunAsync(IReadOnlyList<string> args, Action<string>? log, CancellationToken ct)
    {
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), line =>
        {
            lock (lines)
                lines.Add(line);
            log?.Invoke(line);
        });
        var exit = await runner.WaitAsync(ct);
        lock (lines)
            return (exit, string.Join('\n', lines));
    }
}
