using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace VSL.Debugging;

/// <summary>
/// Finds or installs Samsung's netcoredbg (MIT), the open-source .NET debugger that speaks DAP.
/// Microsoft's vsdbg cannot be used: its license limits it to Visual Studio / VS Code.
/// </summary>
public static class NetcoredbgLocator
{
    public const string Version = "3.2.0-1092";
    const string Url = "https://github.com/Samsung/netcoredbg/releases/download/" + Version + "/netcoredbg-linux-amd64.tar.gz";
    const string Sha256 = "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa";

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSL", "netcoredbg", Version);

    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("VSL_NETCOREDBG");
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        var exe = Path.Combine(InstallRoot, "netcoredbg", "netcoredbg");
        return File.Exists(exe) ? exe : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;

        log($"Downloading netcoredbg {Version} ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(Url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != Sha256)
            throw new InvalidOperationException($"netcoredbg download checksum mismatch ({hash}).");

        var staging = InstallRoot + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        await using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
        var exe = Path.Combine(staging, "netcoredbg", "netcoredbg");
        File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        if (Directory.Exists(InstallRoot))
            Directory.Delete(InstallRoot, recursive: true);
        Directory.Move(staging, InstallRoot);
        log($"netcoredbg installed into {InstallRoot}");
        return Find()!;
    }
}
