using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Packages;
using VSL.Workspace;

namespace VSL.ViewModels;

public enum NuGetTab
{
    Browse,
    Installed,
    Updates,
}

/// <summary>A package row in the NuGet Package Manager list.</summary>
public sealed partial class PackageItem : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionText))]
    public partial string? LatestVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionText), nameof(IsInstalled))]
    public partial string? InstalledVersion { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Byline))]
    public partial string? Owners { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Byline), nameof(DownloadsText))]
    public partial long Downloads { get; set; }

    public string DownloadsText => Downloads > 0 ? Downloads.ToString("N0") : "";

    [ObservableProperty]
    public partial string? ProjectUrl { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    public bool HasUpdate => InstalledVersion is not null && LatestVersion is not null && NuGetVersions.Compare(LatestVersion, InstalledVersion) > 0;

    /// <summary>Installed version (VS shows it on the right; the latest one when not installed).</summary>
    public string VersionText => InstalledVersion ?? LatestVersion ?? "";

    public string Byline => string.Join(", ", new[]
    {
        string.IsNullOrEmpty(Owners) ? null : $"by {Owners}",
        Downloads > 0 ? $"{NuGetVersions.FormatCount(Downloads)} downloads" : null,
    }.Where(s => s is not null));
}

/// <summary>A project row in the solution-level manager: tick projects to install into.</summary>
public sealed partial class ProjectChoice : ObservableObject
{
    public required ProjectInfo Project { get; init; }

    public string Name => Project.Name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial string? InstalledVersion { get; set; }
}

public static class NuGetVersions
{
    public static int Compare(string a, string b)
    {
        static (Version Core, string Pre) Parse(string v)
        {
            var dash = v.IndexOfAny(['-', '+']);
            var core = dash < 0 ? v : v[..dash];
            var pre = dash < 0 ? "" : v[(dash + 1)..];
            return (Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0), pre);
        }
        var (ca, pa) = Parse(a);
        var (cb, pb) = Parse(b);
        var c = ca.CompareTo(cb);
        if (c != 0)
            return c;
        // A release is newer than any prerelease of the same version.
        return (pa.Length == 0, pb.Length == 0) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(pa, pb),
        };
    }

    public static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B",
        >= 1_000_000 => $"{n / 1e6:0.#}M",
        >= 1_000 => $"{n / 1e3:0.#}K",
        _ => n.ToString(),
    };
}

/// <summary>Visual Studio's NuGet Package Manager (Browse / Installed / Updates) for a project or the whole solution.</summary>
public sealed partial class NuGetViewModel : DocumentTab
{
    readonly NuGetService _nuget;
    readonly SolutionInfo _solution;
    readonly ProjectInfo? _project;
    readonly Func<Task> _packagesChanged;
    List<InstalledPackage> _installed = [];
    List<InstalledPackage> _outdated = [];
    CancellationTokenSource? _cts;

    public NuGetViewModel(SolutionInfo solution, ProjectInfo? project, Action<string> log, Func<Task> packagesChanged)
    {
        _solution = solution;
        _project = project;
        _packagesChanged = packagesChanged;
        _nuget = new NuGetService(solution.Directory, log);
        foreach (var p in solution.Projects)
            ProjectChoices.Add(new ProjectChoice { Project = p, IsChecked = project is null || p == project });
    }

    public override string Key => _project is null ? "nuget:solution" : "nuget:" + _project.FilePath;

    public override string Title => _project is null ? "NuGet - Solution" : $"NuGet: {_project.Name}";

    public override string ToolTip => _project is null ? $"NuGet Package Manager: {_solution.Name}" : $"NuGet Package Manager: {_project.Name}";

    public string Header => _project is null ? $"Manage Packages for Solution" : $"NuGet Package Manager: {_project.Name}";

    public bool IsSolutionScope => _project is null;

    public ObservableCollection<PackageItem> Packages { get; } = [];

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<ProjectChoice> ProjectChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseTab), nameof(IsInstalledTab), nameof(IsUpdatesTab))]
    public partial NuGetTab Tab { get; set; }

    public bool IsBrowseTab
    {
        get => Tab == NuGetTab.Browse;
        set
        {
            if (value)
                Tab = NuGetTab.Browse;
        }
    }

    public bool IsInstalledTab
    {
        get => Tab == NuGetTab.Installed;
        set
        {
            if (value)
                Tab = NuGetTab.Installed;
        }
    }

    public bool IsUpdatesTab
    {
        get => Tab == NuGetTab.Updates;
        set
        {
            if (value)
                Tab = NuGetTab.Updates;
        }
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludePrerelease { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(UpdateAllCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand))]
    public partial PackageItem? SelectedPackage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial string? SelectedVersion { get; set; }

    public bool HasSelection => SelectedPackage is not null;

    /// <summary>"Install", or "Update" when the selected package is already installed in the target project(s).</summary>
    public string InstallButtonText => SelectedPackage?.InstalledVersion is null ? "Install" : "Update";

    partial void OnTabChanged(NuGetTab value) => _ = LoadAsync();

    partial void OnIncludePrereleaseChanged(bool value) => _ = LoadAsync();

    partial void OnSelectedPackageChanged(PackageItem? value) => _ = LoadDetailsAsync(value);

    public async Task InitializeAsync()
    {
        await RefreshInstalledAsync();
        await LoadAsync();
    }

    [RelayCommand]
    Task Search() => LoadAsync();

    [RelayCommand]
    async Task Refresh()
    {
        await RefreshInstalledAsync();
        await LoadAsync();
    }

    string ListTarget => _project?.FilePath ?? _solution.FilePath;

    async Task RefreshInstalledAsync()
    {
        await BusyAsync("Reading installed packages...", async ct =>
        {
            _installed = (await _nuget.ListAsync(ListTarget, outdated: false, IncludePrerelease, ct)).ToList();
            _outdated = (await _nuget.ListAsync(ListTarget, outdated: true, IncludePrerelease, ct))
                .Where(p => p.LatestVersion is { Length: > 0 } latest && NuGetVersions.Compare(latest, p.ResolvedVersion) > 0).ToList();
            UpdateCount = _outdated.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        });
    }

    /// <summary>Installed version of a package in the scope (for the solution: the first project that has it).</summary>
    string? InstalledVersionOf(string id) =>
        _installed.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.ResolvedVersion;

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var selectedId = SelectedPackage?.Id;
        await BusyAsync(Tab == NuGetTab.Browse ? "Searching..." : "Loading...", async ct =>
        {
            List<PackageItem> items;
            switch (Tab)
            {
                case NuGetTab.Browse:
                    var results = await _nuget.SearchAsync(SearchText.Trim(), IncludePrerelease, 50, ct);
                    items = results.Select(r => new PackageItem
                    {
                        Id = r.Id,
                        LatestVersion = r.LatestVersion,
                        Description = r.Description,
                        Owners = r.Owners,
                        Downloads = r.TotalDownloads,
                        ProjectUrl = r.ProjectUrl,
                        InstalledVersion = InstalledVersionOf(r.Id),
                    }).ToList();
                    break;
                case NuGetTab.Installed:
                    items = _installed.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Key.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new PackageItem
                        {
                            Id = g.Key,
                            InstalledVersion = g.First().ResolvedVersion,
                            LatestVersion = _outdated.FirstOrDefault(o => o.Id.Equals(g.Key, StringComparison.OrdinalIgnoreCase))?.LatestVersion,
                        }).ToList();
                    break;
                default:
                    items = _outdated.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new PackageItem { Id = g.Key, InstalledVersion = g.First().ResolvedVersion, LatestVersion = g.First().LatestVersion })
                        .ToList();
                    break;
            }
            if (cts.IsCancellationRequested)
                return;
            Packages.Clear();
            foreach (var item in items)
                Packages.Add(item);
            SelectedPackage = Packages.FirstOrDefault(p => p.Id == selectedId) ?? Packages.FirstOrDefault();
            Status = Packages.Count == 0 ? (Tab == NuGetTab.Updates ? "All packages are up to date." : "No packages found.") : "";
        }, cts.Token);
    }

    async Task LoadDetailsAsync(PackageItem? item)
    {
        Versions.Clear();
        foreach (var choice in ProjectChoices)
            choice.InstalledVersion = null;
        if (item is null)
            return;
        foreach (var choice in ProjectChoices)
            choice.InstalledVersion = _installed.FirstOrDefault(p => p.ProjectPath == choice.Project.FilePath
                && p.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))?.ResolvedVersion;
        try
        {
            // Installed/Updates rows come from `dotnet list package`, which has no description: look it up.
            if (item.Description is null)
            {
                var info = (await _nuget.SearchAsync(item.Id, IncludePrerelease, 5, CancellationToken.None))
                    .FirstOrDefault(r => r.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
                if (info is not null)
                {
                    item.Description = info.Description;
                    item.Owners = info.Owners;
                    item.Downloads = info.TotalDownloads;
                    item.ProjectUrl = info.ProjectUrl;
                    item.LatestVersion ??= info.LatestVersion;
                }
            }
            var versions = await _nuget.VersionsAsync(item.Id, IncludePrerelease, CancellationToken.None);
            if (SelectedPackage != item)
                return;
            Versions.Clear();
            foreach (var v in versions)
                Versions.Add(v);
            SelectedVersion = Versions.FirstOrDefault();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Projects an action applies to: this project, or the ticked projects of the solution.</summary>
    IReadOnlyList<ProjectInfo> Targets => _project is not null ? [_project] : ProjectChoices.Where(c => c.IsChecked).Select(c => c.Project).ToList();

    bool CanInstall => !IsBusy && SelectedPackage is not null && SelectedVersion is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    async Task Install()
    {
        if (SelectedPackage is not { } package || SelectedVersion is not { } version)
            return;
        await ApplyAsync($"Installing {package.Id} {version}...", Targets, (project, ct) => _nuget.InstallAsync(project.FilePath, package.Id, version, ct));
    }

    bool CanUninstall => !IsBusy && SelectedPackage is { IsInstalled: true };

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    async Task Uninstall()
    {
        if (SelectedPackage is not { } package)
            return;
        var targets = Targets.Where(t => _installed.Any(p => p.ProjectPath == t.FilePath && p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        await ApplyAsync($"Uninstalling {package.Id}...", targets, (project, ct) => _nuget.UninstallAsync(project.FilePath, package.Id, ct));
    }

    bool CanUpdateAll => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    async Task UpdateAll()
    {
        var updates = _outdated.Where(o => o.LatestVersion is not null).ToList();
        await BusyAsync($"Updating {updates.Count} package(s)...", async ct =>
        {
            foreach (var u in updates)
            {
                var (ok, output) = await _nuget.InstallAsync(u.ProjectPath, u.Id, u.LatestVersion!, ct);
                if (!ok)
                {
                    Status = $"Updating {u.Id} failed: {LastLine(output)}";
                    return;
                }
            }
            Status = $"Updated {updates.Count} package(s).";
        });
        await AfterChangeAsync();
    }

    async Task ApplyAsync(string message, IReadOnlyList<ProjectInfo> targets, Func<ProjectInfo, CancellationToken, Task<(bool Success, string Output)>> action)
    {
        if (targets.Count == 0)
        {
            Status = "Select at least one project.";
            return;
        }
        var failed = false;
        await BusyAsync(message, async ct =>
        {
            foreach (var project in targets)
            {
                var (ok, output) = await action(project, ct);
                if (!ok)
                {
                    failed = true;
                    Status = $"{project.Name}: {LastLine(output)}";
                    return;
                }
            }
        });
        if (!failed)
            Status = "Done.";
        await AfterChangeAsync();
    }

    async Task AfterChangeAsync()
    {
        await RefreshInstalledAsync();
        await LoadAsync();
        await _packagesChanged();
    }

    static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim()
        ?? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "failed";

    async Task BusyAsync(string message, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        IsBusy = true;
        Status = message;
        try
        {
            await work(ct);
            if (Status == message)
                Status = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void Dispose() => _cts?.Cancel();
}
