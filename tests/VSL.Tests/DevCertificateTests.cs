using Avalonia.Headless.XUnit;
using VSL.Build;
using VSL.ViewModels;
using VSL.Workspace;

namespace VSL.Tests;

public class DevCertificateTests
{
    [Theory]
    [InlineData("""[{"Thumbprint":"A","IsHttpsDevelopmentCertificate":true,"TrustLevel":"None"}]""", DevCertificateTrust.NotTrusted)]
    [InlineData("""[{"IsHttpsDevelopmentCertificate":true,"TrustLevel":"Partial"}]""", DevCertificateTrust.Partial)]
    [InlineData("""[{"IsHttpsDevelopmentCertificate":true,"TrustLevel":"None"},{"IsHttpsDevelopmentCertificate":true,"TrustLevel":"Full"}]""", DevCertificateTrust.Trusted)]
    [InlineData("[]", DevCertificateTrust.Missing)]
    [InlineData("No valid certificate found.", DevCertificateTrust.Unknown)]
    public void Reads_the_trust_level(string output, DevCertificateTrust expected) => Assert.Equal(expected, DevCertificates.Parse(output));

    [Fact]
    public void Ssl_cert_dir_lists_the_dev_cert_folder_before_the_system_certificates()
    {
        var trust = DevCertificates.TrustDirectory;
        Assert.Equal($"{trust}:/etc/ssl/certs", DevCertificates.CertDirectoryValue(null, d => d == trust || d == "/etc/ssl/certs"));
        Assert.Equal($"{trust}:/etc/pki/tls/certs", DevCertificates.CertDirectoryValue("", d => d == trust || d == "/etc/pki/tls/certs"));
        Assert.Equal($"{trust}:/custom/certs", DevCertificates.CertDirectoryValue("/custom/certs", _ => true));
        // Already there, or nothing trusted yet: leave the variable alone.
        Assert.Null(DevCertificates.CertDirectoryValue($"{trust}:/etc/ssl/certs", _ => true));
        Assert.Null(DevCertificates.CertDirectoryValue(null, d => d != trust));
    }

    sealed class Dialogs(bool? answer) : IDialogService
    {
        public List<string> Asked { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Asked.Add(title);
            return Task.FromResult(answer);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, VSL.Workspace.ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static LaunchProfile Profile(string url) => new("Server", url, true, null, null, new Dictionary<string, string>());

    [AvaloniaFact]
    public async Task Offers_to_trust_the_certificate_once_for_https_profiles()
    {
        SampleSolution.EnsureConfigIsolated();
        var dialogs = new Dialogs(answer: true);
        var trusted = false;
        var vm = new MainViewModel
        {
            Dialogs = dialogs,
            CheckDevCertificate = _ => Task.FromResult(trusted ? DevCertificateTrust.Trusted : DevCertificateTrust.NotTrusted),
            TrustDevCertificateTool = (log, _) =>
            {
                log("Trusting the HTTPS development certificate was requested.");
                trusted = true;
                return Task.FromResult(true);
            },
        };

        await vm.OfferDevCertificateTrustAsync(Profile("http://localhost:5280"));
        Assert.Empty(dialogs.Asked);

        await vm.OfferDevCertificateTrustAsync(Profile("https://localhost:7042;http://localhost:5280"));
        Assert.Equal(["Trust the ASP.NET Core SSL certificate?"], dialogs.Asked);
        Assert.True(trusted);
        Assert.Contains("The ASP.NET Core development certificate is trusted.", vm.DebugOutput.Document.Text);

        // Once per session.
        await vm.OfferDevCertificateTrustAsync(Profile("https://localhost:7042"));
        Assert.Single(dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task Declining_points_to_the_tools_command()
    {
        SampleSolution.EnsureConfigIsolated();
        var dialogs = new Dialogs(answer: false);
        var trustCalls = 0;
        var vm = new MainViewModel
        {
            Dialogs = dialogs,
            CheckDevCertificate = _ => Task.FromResult(DevCertificateTrust.NotTrusted),
            TrustDevCertificateTool = (_, _) =>
            {
                trustCalls++;
                return Task.FromResult(true);
            },
        };
        await vm.OfferDevCertificateTrustAsync(Profile("https://localhost:7042"));
        Assert.Single(dialogs.Asked);
        Assert.Equal(0, trustCalls);
        Assert.Contains("Tools > Trust ASP.NET Core Development Certificate", vm.DebugOutput.Document.Text);
    }
}
