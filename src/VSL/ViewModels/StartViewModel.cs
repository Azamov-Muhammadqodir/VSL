using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Git;
using VSL.Services;
using VSL.Templates;

namespace VSL.ViewModels;

public enum StartPage
{
    Home,
    NewProject,
    Configure,
    AdditionalInformation,
    Clone,
}

/// <summary>A solution in the start window's "Open recent" list.</summary>
public sealed partial class RecentItem(string path, DateTime? opened, bool pinned) : ObservableObject
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public string Folder { get; } = System.IO.Path.GetDirectoryName(path) ?? "";

    public DateTime? Opened { get; } = opened;

    public string DateText => Opened is { } d ? d.ToString("dd.MM.yyyy HH:mm") : "";

    public bool Exists => File.Exists(Path);

    public bool IsPinned { get; } = pinned;

    public string IconKind => Workspace.SolutionLoader.IsProjectFile(Path) ? "Project" : "Solution";
}

public sealed record RecentGroup(string Header, IReadOnlyList<RecentItem> Items);

/// <summary>A template row; loads the template's own icon when it ships one.</summary>
public sealed class TemplateItem(ProjectTemplate template)
{
    Bitmap? _icon;
    bool _iconLoaded;

    public ProjectTemplate Template { get; } = template;

    public string Name => Template.Name;

    public string Description => Template.Description;

    public IReadOnlyList<string> Tags => Template.AllTags;

    public string? Language => Template.Language;

    public Bitmap? Icon
    {
        get
        {
            if (_iconLoaded)
                return _icon;
            _iconLoaded = true;
            try
            {
                _icon = Template.IconPath is { } path && File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception)
            {
                // Some packages ship icon formats the decoder does not read; the generic icon is shown instead.
                _icon = null;
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon is not null;

    /// <summary>The generic project icon for templates without their own.</summary>
    public string FallbackIcon => Language switch { "F#" => "FsProject", "VB" => "VbProject", _ => "Project" };
}

/// <summary>A template option on "Additional information": a combo box or a checkbox.</summary>
public sealed partial class OptionItem : ObservableObject
{
    public OptionItem(TemplateOption option)
    {
        Option = option;
        SelectedChoice = option.Choices.FirstOrDefault(c => c.Value == option.DefaultValue) ?? option.Choices.FirstOrDefault();
        IsChecked = option.DefaultChecked;
    }

    public TemplateOption Option { get; }

    public string Label => Option.Label;

    public string? Description => Option.Description;

    public bool IsChoice => Option.Kind == TemplateOptionKind.Choice;

    public bool IsCheckbox => Option.Kind == TemplateOptionKind.Bool;

    public IReadOnlyList<TemplateChoice> Choices => Option.Choices;

    [ObservableProperty]
    public partial TemplateChoice? SelectedChoice { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public string Value => IsChoice ? SelectedChoice?.Value ?? Option.DefaultValue ?? "" : IsChecked ? "true" : "false";

    /// <summary>Only options that differ from the template default are passed to dotnet new.</summary>
    public bool IsDefault => IsChoice ? Value == Option.DefaultValue : IsChecked == Option.DefaultChecked;
}

/// <summary>
/// Visual Studio's start window: open recent, open a project or folder, clone a repository, or create a new
/// project from the SDK's templates (template → configure → additional information).
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    public const string AllLanguages = "All languages";
    public const string AllPlatforms = "All platforms";
    public const string AllProjectTypes = "All project types";

    readonly Settings _settings;
    readonly Func<DateTime> _now;
    readonly Func<CancellationToken, Task<IReadOnlyList<ProjectTemplate>>> _loadTemplates;
    List<TemplateItem> _templates = [];
    Task? _templatesTask;
    bool _solutionNameEdited;
    bool _syncingSolutionName;
    bool _clonePathEdited;
    bool _syncingClonePath;

    public StartViewModel(Settings settings, Func<DateTime>? now = null,
        Func<CancellationToken, Task<IReadOnlyList<ProjectTemplate>>>? loadTemplates = null)
    {
        _settings = settings;
        _now = now ?? (() => DateTime.Now);
        _loadTemplates = loadTemplates ?? TemplateCatalog.LoadAsync;
        Location = settings.NewProjectLocation is { Length: > 0 } location ? location : DefaultReposDirectory;
        RefreshRecent();
    }

    public static string DefaultReposDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");

    /// <summary>The user picked a solution (opened, created or cloned).</summary>
    public event Action<string>? SolutionChosen;

    /// <summary>"Continue without code".</summary>
    public event Action? ContinueRequested;

    public Func<Task<string?>>? PickSolution { get; set; }

    public Func<string, Task<string?>>? PickFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsNewProject), nameof(IsConfigure), nameof(IsAdditionalInformation), nameof(IsClone), nameof(IsWizard), nameof(ShowNext))]
    public partial StartPage Page { get; set; }

    /// <summary>The wizard pages that continue with Next (the last one has Create instead).</summary>
    public bool ShowNext => Page is StartPage.NewProject or StartPage.Configure;

    public bool IsHome => Page == StartPage.Home;

    public bool IsNewProject => Page == StartPage.NewProject;

    public bool IsConfigure => Page == StartPage.Configure;

    public bool IsAdditionalInformation => Page == StartPage.AdditionalInformation;

    public bool IsClone => Page == StartPage.Clone;

    public bool IsWizard => Page is StartPage.Configure or StartPage.AdditionalInformation;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(CreateCommand), nameof(CloneCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    partial void OnPageChanged(StartPage value)
    {
        Message = null;
        NextCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
    }

    // ---- Open recent ---------------------------------------------------------------------------------------

    public ObservableCollection<RecentGroup> RecentGroups { get; } = [];

    public bool HasRecent => _settings.RecentSolutions.Count > 0;

    [ObservableProperty]
    public partial string RecentSearch { get; set; } = "";

    partial void OnRecentSearchChanged(string value) => RefreshRecent();

    /// <summary>Pinned first, then Today / Yesterday / This week / This month / Older, like VS.</summary>
    public void RefreshRecent()
    {
        RecentGroups.Clear();
        var today = _now().Date;
        var items = _settings.RecentSolutions
            .Where(p => RecentSearch.Length == 0 || p.Contains(RecentSearch, StringComparison.OrdinalIgnoreCase))
            .Select(p => new RecentItem(p, _settings.RecentOpened.TryGetValue(p, out var d) ? d : null, _settings.PinnedSolutions.Contains(p)))
            .ToList();
        void Group(string header, IEnumerable<RecentItem> group)
        {
            var list = group.ToList();
            if (list.Count > 0)
                RecentGroups.Add(new RecentGroup(header, list));
        }
        Group("Pinned", items.Where(i => i.IsPinned));
        var unpinned = items.Where(i => !i.IsPinned).ToList();
        Group("Today", unpinned.Where(i => i.Opened?.Date == today));
        Group("Yesterday", unpinned.Where(i => i.Opened?.Date == today.AddDays(-1)));
        Group("This week", unpinned.Where(i => i.Opened?.Date < today.AddDays(-1) && i.Opened?.Date >= today.AddDays(-7)));
        Group("This month", unpinned.Where(i => i.Opened?.Date < today.AddDays(-7) && i.Opened?.Date >= today.AddDays(-30)));
        Group("Older", unpinned.Where(i => i.Opened is null || i.Opened.Value.Date < today.AddDays(-30)));
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    void OpenRecent(RecentItem? item)
    {
        if (item is null)
            return;
        if (!item.Exists)
        {
            Message = $"\"{item.Path}\" could not be found. Right-click it to remove it from the list.";
            return;
        }
        Choose(item.Path);
    }

    [RelayCommand]
    void TogglePin(RecentItem? item)
    {
        if (item is null)
            return;
        if (!_settings.PinnedSolutions.Remove(item.Path))
            _settings.PinnedSolutions.Add(item.Path);
        _settings.Save();
        RefreshRecent();
    }

    [RelayCommand]
    void RemoveRecent(RecentItem? item)
    {
        if (item is null)
            return;
        _settings.RemoveRecent(item.Path);
        _settings.Save();
        RefreshRecent();
    }

    // ---- Get started ---------------------------------------------------------------------------------------

    [RelayCommand]
    async Task OpenProjectOrSolution()
    {
        if (PickSolution is not null && await PickSolution() is { } path)
            Choose(path);
    }

    [RelayCommand]
    async Task OpenFolder()
    {
        if (PickFolder is null || await PickFolder("Open Folder") is not { } folder)
            return;
        if (FindSolution(folder) is { } solution)
            Choose(solution);
        else
            Message = $"No solution or project file was found in \"{folder}\".";
    }

    [RelayCommand]
    void ShowClone() => Page = StartPage.Clone;

    [RelayCommand]
    void ShowNewProject()
    {
        Page = StartPage.NewProject;
        _ = EnsureTemplatesAsync();
    }

    [RelayCommand]
    void ContinueWithoutCode() => ContinueRequested?.Invoke();

    [RelayCommand]
    void Back() => Page = Page switch
    {
        StartPage.Configure => StartPage.NewProject,
        StartPage.AdditionalInformation => StartPage.Configure,
        _ => StartPage.Home,
    };

    void Choose(string path) => SolutionChosen?.Invoke(Path.GetFullPath(path));

    /// <summary>
    /// What to open for a folder: its solution (top level first, then the shallowest one below), or, like VS
    /// opening a project on its own, its project file when there is no solution. Build and VCS folders are skipped.
    /// </summary>
    public static string? FindSolution(string folder) =>
        Find(folder, Workspace.SolutionLoader.IsSolutionFile) ?? Find(folder, Workspace.SolutionLoader.IsProjectFile);

    static string? Find(string folder, Func<string, bool> match)
    {
        var level = new List<string> { folder };
        for (var depth = 0; depth < 4 && level.Count > 0; depth++)
        {
            var found = level.SelectMany(d => SafeEnumerate(() => Directory.EnumerateFiles(d))).Where(match).Order(StringComparer.Ordinal).FirstOrDefault();
            if (found is not null)
                return found;
            level = level.SelectMany(d => SafeEnumerate(() => Directory.EnumerateDirectories(d)))
                .Where(d => Path.GetFileName(d) is not (".git" or "bin" or "obj" or "node_modules" or ".vs" or ".idea"))
                .ToList();
        }
        return null;

        static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> list)
        {
            try
            {
                return list().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    // ---- Create a new project ------------------------------------------------------------------------------

    public ObservableCollection<TemplateItem> Templates { get; } = [];

    public ObservableCollection<TemplateItem> RecentTemplates { get; } = [];

    public ObservableCollection<string> Languages { get; } = [AllLanguages];

    public ObservableCollection<string> Platforms { get; } = [AllPlatforms];

    public ObservableCollection<string> ProjectTypes { get; } = [AllProjectTypes];

    [ObservableProperty]
    public partial bool TemplatesLoading { get; set; }

    [ObservableProperty]
    public partial string TemplateSearch { get; set; } = "";

    [ObservableProperty]
    public partial string LanguageFilter { get; set; } = AllLanguages;

    [ObservableProperty]
    public partial string PlatformFilter { get; set; } = AllPlatforms;

    [ObservableProperty]
    public partial string ProjectTypeFilter { get; set; } = AllProjectTypes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTemplateName), nameof(SelectedTemplateTags))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial TemplateItem? SelectedTemplate { get; set; }

    public string SelectedTemplateName => SelectedTemplate?.Name ?? "";

    public IReadOnlyList<string> SelectedTemplateTags => SelectedTemplate?.Tags ?? [];

    partial void OnTemplateSearchChanged(string value) => FilterTemplates();

    partial void OnLanguageFilterChanged(string value) => FilterTemplates();

    partial void OnPlatformFilterChanged(string value) => FilterTemplates();

    partial void OnProjectTypeFilterChanged(string value) => FilterTemplates();

    [RelayCommand]
    void ClearTemplateFilters()
    {
        TemplateSearch = "";
        LanguageFilter = AllLanguages;
        PlatformFilter = AllPlatforms;
        ProjectTypeFilter = AllProjectTypes;
    }

    public Task EnsureTemplatesAsync() => _templatesTask ??= LoadTemplatesAsync();

    async Task LoadTemplatesAsync()
    {
        TemplatesLoading = true;
        try
        {
            _templates = (await _loadTemplates(CancellationToken.None)).Select(t => new TemplateItem(t)).ToList();
            foreach (var language in _templates.Select(t => t.Language).OfType<string>().Distinct().OrderBy(LanguageRank))
                Languages.Add(language);
            foreach (var platform in _templates.SelectMany(t => t.Template.Platforms).Distinct().Order(StringComparer.OrdinalIgnoreCase))
                Platforms.Add(platform);
            foreach (var type in _templates.SelectMany(t => t.Template.Tags).Distinct().Order(StringComparer.OrdinalIgnoreCase))
                ProjectTypes.Add(type);
            foreach (var identity in _settings.RecentTemplates)
                if (_templates.FirstOrDefault(t => t.Template.Identity == identity) is { } recent)
                    RecentTemplates.Add(recent);
            FilterTemplates();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Message = "The project templates could not be loaded: " + ex.Message;
            _templatesTask = null;
        }
        finally
        {
            TemplatesLoading = false;
        }
    }

    static int LanguageRank(string language) => language switch { "C#" => 0, "F#" => 1, "VB" => 2, _ => 3 };

    void FilterTemplates()
    {
        var keep = SelectedTemplate;
        Templates.Clear();
        foreach (var t in _templates.Where(t =>
                     (LanguageFilter == AllLanguages || t.Language == LanguageFilter)
                     && (PlatformFilter == AllPlatforms || t.Template.Platforms.Contains(PlatformFilter))
                     && (ProjectTypeFilter == AllProjectTypes || t.Template.Tags.Contains(ProjectTypeFilter))
                     && t.Template.Matches(TemplateSearch)))
            Templates.Add(t);
        SelectedTemplate = keep is not null && Templates.Contains(keep) ? keep : null;
    }

    bool CanGoNext => !IsBusy && Page switch
    {
        StartPage.NewProject => SelectedTemplate is not null,
        StartPage.Configure => ConfigureError is null,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    void Next()
    {
        if (Page == StartPage.NewProject && SelectedTemplate is { } template)
        {
            ProjectName = NextFreeName(Location, template.Template.DefaultName);
            Options.Clear();
            foreach (var option in template.Template.Options)
                Options.Add(new OptionItem(option));
            Page = StartPage.Configure;
        }
        else if (Page == StartPage.Configure)
        {
            Page = StartPage.AdditionalInformation;
        }
    }

    /// <summary>VS names new projects ConsoleApp1, ConsoleApp2... after what already exists in the location.</summary>
    static string NextFreeName(string location, string defaultName)
    {
        // Some templates already end their default name with a number ("ConsoleApp1").
        var baseName = defaultName.TrimEnd("0123456789".ToCharArray());
        if (baseName.Length == 0)
            baseName = defaultName;
        for (var i = 1; ; i++)
            if (!Directory.Exists(Path.Combine(location, baseName + i)))
                return baseName + i;
    }

    // ---- Configure your new project ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigureError), nameof(CreatedInText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string ProjectName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigureError), nameof(CreatedInText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string Location { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigureError), nameof(CreatedInText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string SolutionName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigureError), nameof(CreatedInText), nameof(CanEditSolutionName))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool SolutionInSameDirectory { get; set; }

    public bool CanEditSolutionName => !SolutionInSameDirectory;

    partial void OnProjectNameChanged(string value)
    {
        if (_solutionNameEdited)
            return;
        _syncingSolutionName = true;
        SolutionName = value;
        _syncingSolutionName = false;
    }

    partial void OnSolutionNameChanged(string value)
    {
        if (!_syncingSolutionName)
            _solutionNameEdited = true;
    }

    public string? ConfigureError => Page == StartPage.NewProject && ProjectName.Length == 0
        ? null
        : ProjectCreator.Validate(ProjectName, Location, SolutionName, SolutionInSameDirectory);

    NewProjectRequest? Request => SelectedTemplate is { } t
        ? new NewProjectRequest(t.Template, ProjectName, Location, SolutionName, SolutionInSameDirectory,
            Options.Where(o => !o.IsDefault).Select(o => (o.Option, o.Value)).ToList())
        : null;

    public string CreatedInText => Request is { } r ? $"Project will be created in \"{r.ProjectDirectory}{Path.DirectorySeparatorChar}\"" : "";

    [RelayCommand]
    async Task BrowseLocation()
    {
        if (PickFolder is not null && await PickFolder("Project Location") is { } folder)
            Location = folder;
    }

    // ---- Additional information ----------------------------------------------------------------------------

    public ObservableCollection<OptionItem> Options { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    bool CanCreate => !IsBusy && Page == StartPage.AdditionalInformation;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    async Task Create()
    {
        if (Request is not { } request)
            return;
        if (ConfigureError is { } error)
        {
            Message = error;
            return;
        }
        IsBusy = true;
        Message = null;
        Log.Clear();
        BusyText = $"Creating {request.ProjectName}...";
        try
        {
            var solution = await ProjectCreator.CreateAsync(request, line => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log.Add(line)));
            _settings.NewProjectLocation = request.Location;
            _settings.AddRecentTemplate(request.Template.Identity);
            _settings.Save();
            Choose(solution);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Message = "The project could not be created: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Clone a repository --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloneError))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string CloneUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloneError))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string ClonePath { get; set; } = DefaultReposDirectory;

    partial void OnCloneUrlChanged(string value)
    {
        if (_clonePathEdited)
            return;
        _syncingClonePath = true;
        ClonePath = Path.Combine(DefaultReposDirectory, RepositoryName(value));
        _syncingClonePath = false;
    }

    partial void OnClonePathChanged(string value)
    {
        if (!_syncingClonePath)
            _clonePathEdited = true;
    }

    /// <summary>"https://github.com/org/App.git" → "App".</summary>
    internal static string RepositoryName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', ':', '\\']) + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public string? CloneError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CloneUrl))
                return "Enter a Git repository URL.";
            if (string.IsNullOrWhiteSpace(ClonePath) || !Path.IsPathRooted(ClonePath))
                return "Enter a full path to clone into.";
            if (Directory.Exists(ClonePath) && Directory.EnumerateFileSystemEntries(ClonePath).Any())
                return $"The folder \"{ClonePath}\" already exists and is not empty.";
            return null;
        }
    }

    bool CanClone => !IsBusy && CloneError is null;

    [RelayCommand(CanExecute = nameof(CanClone))]
    async Task Clone()
    {
        IsBusy = true;
        Message = null;
        Log.Clear();
        BusyText = $"Cloning {CloneUrl.Trim()}...";
        try
        {
            var result = await GitRepository.CloneAsync(CloneUrl.Trim(), ClonePath,
                line => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Log.Add(line);
                    BusyText = line;
                }));
            if (!result.Success)
            {
                Message = "Clone failed: " + (result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "git exited with an error.");
                return;
            }
            if (FindSolution(ClonePath) is { } solution)
                Choose(solution);
            else
                Message = $"Cloned into \"{ClonePath}\", but it has no solution or project file to open.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Message = "Clone failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CloneCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    async Task BrowseClonePath()
    {
        if (PickFolder is not null && await PickFolder("Clone Into") is { } folder)
            ClonePath = folder;
    }
}
