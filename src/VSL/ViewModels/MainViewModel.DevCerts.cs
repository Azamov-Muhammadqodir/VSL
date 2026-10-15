using CommunityToolkit.Mvvm.Input;
using VSL.Build;
using VSL.Workspace;

namespace VSL.ViewModels;

/// <summary>Trusting the ASP.NET Core HTTPS development certificate, as Visual Studio offers on the first HTTPS run.</summary>
public partial class MainViewModel
{
    bool _devCertificateOffered;

    /// <summary>The certificate check and trust commands (replaced in tests so they never touch the real trust store).</summary>
    public Func<CancellationToken, Task<DevCertificateTrust>> CheckDevCertificate { get; set; } = DevCertificates.CheckAsync;

    public Func<Action<string>, CancellationToken, Task<bool>> TrustDevCertificateTool { get; set; } = DevCertificates.TrustAsync;

    /// <summary>
    /// Before running a profile that listens on https: if the development certificate is not trusted, ask once per
    /// session whether to trust it (VS: "Trust the ASP.NET Core SSL certificate?").
    /// </summary>
    internal async Task OfferDevCertificateTrustAsync(LaunchProfile? profile)
    {
        if (_devCertificateOffered || Dialogs is null || profile?.ApplicationUrl?.Contains("https://", StringComparison.OrdinalIgnoreCase) != true)
            return;
        _devCertificateOffered = true;
        var trust = await CheckDevCertificate(CancellationToken.None);
        if (trust is DevCertificateTrust.Trusted or DevCertificateTrust.Unknown)
            return;
        var answer = await Dialogs.ConfirmAsync("Trust the ASP.NET Core SSL certificate?",
            "This project is configured to use SSL. To avoid certificate warnings in the browser and from Kestrel, you can trust "
            + "the self-signed development certificate that ASP.NET Core created.\n\nWould you like to trust the ASP.NET Core SSL certificate?\n\n"
            + "(VSL runs: dotnet dev-certs https --trust)");
        if (answer == true)
            await TrustDevCertificateAsync();
        else
            DebugOutput.Append("The ASP.NET Core development certificate is not trusted. Tools > Trust ASP.NET Core Development Certificate trusts it later.");
    }

    /// <summary>Tools > Trust ASP.NET Core Development Certificate.</summary>
    [RelayCommand]
    public async Task TrustDevCertificateAsync()
    {
        SelectedOutput = DebugOutput;
        ShowToolWindow(ToolWindows.Output);
        SetStatus("Trusting the ASP.NET Core development certificate...", StatusKind.Busy);
        var ok = await TrustDevCertificateTool(DebugOutput.Append, CancellationToken.None);
        var trust = ok ? await CheckDevCertificate(CancellationToken.None) : DevCertificateTrust.Unknown;
        var message = trust switch
        {
            DevCertificateTrust.Trusted => "The ASP.NET Core development certificate is trusted. Restart browsers that were open to pick it up.",
            DevCertificateTrust.Partial => "The development certificate is trusted for .NET; a browser may still warn (install libnss3-tools so certutil can add it to Chrome and Firefox).",
            _ => "Trusting the development certificate did not complete; see the Output window.",
        };
        DebugOutput.Append(message);
        SetStatus(message, Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
    }
}
