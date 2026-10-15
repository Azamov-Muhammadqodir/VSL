using VSL.Services;
using VSL.Templates;
using VSL.ViewModels;
using VSL.Workspace;

namespace VSL.Tests;

public class TemplateCatalogTests
{
    /// <summary>A templatecache.json in the template engine's format, with one package mounted from a folder.</summary>
    static string Cache(string mountPoint) => """
        {
          "Version": "1.0.0.0",
          "TemplateInfo": [
            {
              "Identity": "Sample.Web.CSharp.9.0", "GroupIdentity": "Sample.Web", "Name": "Sample Web App", "ShortNameList": ["sampleweb"],
              "Precedence": 9000, "Description": "Older version", "Classifications": ["Web"], "MountPointUri": "MOUNT_POINT",
              "ConfigPlace": "/content/Web/.template.config/template.json", "TagsCollection": { "language": "C#", "type": "project" },
              "Parameters": []
            },
            {
              "Identity": "Sample.Web.CSharp.10.0", "GroupIdentity": "Sample.Web", "Name": "Sample Web App", "ShortNameList": ["sampleweb"],
              "Precedence": 10000, "Description": "A web app.", "Author": "Tests", "DefaultName": "WebApp",
              "Classifications": ["Common", "Web"], "MountPointUri": "MOUNT_POINT",
              "ConfigPlace": "/content/Web/.template.config/template.json", "TagsCollection": { "language": "C#", "type": "project" },
              "HostData": "{\"symbolInfo\":{\"UseProgramMain\":{\"longName\":\"use-program-main\"},\"Framework\":{\"longName\":\"framework\"},\"Secret\":{\"isHidden\":true},\"NoHttps\":{\"longName\":\"no-https\"}}}",
              "Parameters": [
                { "Name": "name", "DataType": "string" },
                { "Name": "Framework", "DataType": "choice", "DefaultValue": "net10.0", "Description": "The target framework.",
                  "Choices": { "net10.0": { "Description": "Target net10.0" }, "net9.0": { "Description": "Target net9.0" } } },
                { "Name": "auth", "DataType": "choice", "DefaultValue": "None", "Choices": { "None": {}, "Individual": {} } },
                { "Name": "NoHttps", "DataType": "bool", "DefaultValue": "false" },
                { "Name": "UseProgramMain", "DataType": "bool", "DefaultValue": "false", "DisplayName": "Do not use _top-level statements" },
                { "Name": "DisableOpenAPI", "DataType": "bool", "DefaultValue": "false" },
                { "Name": "Secret", "DataType": "bool", "DefaultValue": "false" },
                { "Name": "skipRestore", "DataType": "bool", "DefaultValue": "false" }
              ]
            },
            {
              "Identity": "Sample.Lib.FSharp", "GroupIdentity": "Sample.Lib", "Name": "Sample Library", "ShortNameList": ["samplelib"],
              "Precedence": 100, "Description": "A library.", "Classifications": ["Library"], "MountPointUri": "/nowhere.nupkg",
              "ConfigPlace": "/content/Lib/.template.config/template.json", "TagsCollection": { "language": "F#", "type": "project" },
              "Parameters": [ { "Name": "EnablePack", "DataType": "bool", "DefaultValue": "false", "DisplayName": "Enable pack" } ]
            },
            {
              "Identity": "Sample.Item", "Name": "Sample Item", "ShortNameList": ["item"], "MountPointUri": "/nowhere.nupkg",
              "ConfigPlace": "/c/template.json", "TagsCollection": { "language": "C#", "type": "item" }, "Parameters": []
            }
          ]
        }
        """.Replace("MOUNT_POINT", mountPoint);

    static string MountPoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsl-templates-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "content", "Web", ".template.config");
        Directory.CreateDirectory(Path.Combine(config, "ide"));
        File.WriteAllText(Path.Combine(config, "ide.host.json"), """
            {
              "order": 200,
              "icon": "ide/icon.png",
              "disableHttpsSymbol": "NoHttps",
              "tags": [ { "type": "platform", "add": [ "Linux", "Windows" ] }, { "type": "projectType", "remove": [ "Common" ] } ],
              "symbolInfo": [
                { "id": "DisableOpenAPI", "name": { "text": "Enable _OpenAPI support" }, "invertBoolean": true, "isVisible": true },
                { "id": "UseProgramMain", "isVisible": true },
                { "id": "Secret", "isVisible": true }
              ]
            }
            """);
        File.WriteAllBytes(Path.Combine(config, "ide", "icon.png"), [0x89, 0x50, 0x4E, 0x47]);
        return root;
    }

    [Fact]
    public void Parses_templates_with_visual_studio_options()
    {
        var mount = MountPoint();
        var icons = Path.Combine(mount, "icons");
        var templates = TemplateCatalog.Parse(Cache(mount), icons);

        // Item templates are skipped; of two versions of a template the higher precedence wins.
        Assert.Equal(["Sample Web App", "Sample Library"], templates.Select(t => t.Name));
        var web = templates[0];
        Assert.Equal("A web app.", web.Description);
        Assert.Equal("WebApp", web.DefaultName);
        Assert.Equal(["C#", "Linux", "Windows", "Web"], web.AllTags);
        Assert.True(File.Exists(web.IconPath));

        // Framework, authentication and HTTPS first, then the symbols ide.host.json makes visible.
        Assert.Equal(["Framework", "Authentication type", "Configure for HTTPS", "Enable OpenAPI support", "Do not use top-level statements"],
            web.Options.Select(o => o.Label));
        var framework = web.Options[0];
        Assert.Equal("--framework", framework.CliName);
        Assert.Equal([".NET 10.0 (Long Term Support)", ".NET 9.0 (Standard Term Support)"], framework.Choices.Select(c => c.Label));
        Assert.Equal("Individual Accounts", web.Options[1].Choices[1].Label);

        // Inverted checkboxes: checked by default, and unchecking sets the symbol.
        var https = web.Options[2];
        Assert.True(https.DefaultChecked);
        Assert.Equal(["--no-https", "true"], https.Arguments("false"));
        var openApi = web.Options[3];
        Assert.Equal("--DisableOpenAPI", openApi.CliName);
        Assert.True(openApi.DefaultChecked);
        var programMain = web.Options[4];
        Assert.False(programMain.DefaultChecked);
        Assert.Equal(["--use-program-main", "true"], programMain.Arguments("true"));

        // Without ide.host.json the template's own switches are offered.
        var lib = templates[1];
        Assert.Equal("F#", lib.Language);
        Assert.Equal(["Enable pack"], lib.Options.Select(o => o.Label));
        Assert.Null(lib.IconPath);
        Assert.True(lib.Matches("library f#"));
        Assert.False(lib.Matches("web"));
    }

    [Fact]
    public async Task Reads_the_installed_sdk_templates()
    {
        var templates = await TemplateCatalog.LoadAsync(TestContext.Current.CancellationToken);
        var console = Assert.Single(templates, t => t.ShortName == "console" && t.Language == "C#");
        Assert.Equal("Console App", console.Name);
        Assert.Contains(console.Options, o => o.Symbol == "Framework" && o.Choices.Count > 0);
        Assert.Contains(console.Options, o => o.Symbol == "UseProgramMain" && o.CliName == "--use-program-main");
        var blazor = Assert.Single(templates, t => t.ShortName == "blazor" && t.Language == "C#");
        Assert.Contains(blazor.Options, o => o.Label == "Interactive render mode");
        Assert.Contains(blazor.Options, o => o.Label == "Configure for HTTPS" && o.Invert);
        // VS lists the everyday templates first.
        Assert.Equal("console", templates[0].ShortName);
    }
}

public class ProjectCreatorTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Validates_names_and_location()
    {
        var dir = TempDir();
        Assert.Null(ProjectCreator.Validate("App", dir, "App", false));
        Assert.Equal("Enter a project name.", ProjectCreator.Validate("", dir, "App", false));
        Assert.NotNull(ProjectCreator.Validate("A/B", dir, "App", false));
        Assert.NotNull(ProjectCreator.Validate("App", "relative/path", "App", false));
        Assert.Null(ProjectCreator.Validate("App", dir, "", true));
        Directory.CreateDirectory(Path.Combine(dir, "Taken"));
        File.WriteAllText(Path.Combine(dir, "Taken", "x.txt"), "");
        Assert.Contains("already exists", ProjectCreator.Validate("Taken", dir, "Taken", true));
    }

    [Fact]
    public async Task Creates_a_console_app_in_a_new_solution()
    {
        var templates = await TemplateCatalog.LoadAsync(TestContext.Current.CancellationToken);
        var console = templates.Single(t => t.ShortName == "console" && t.Language == "C#");
        var programMain = console.Options.Single(o => o.Symbol == "UseProgramMain");
        var dir = TempDir();
        var request = new NewProjectRequest(console, "Hello", dir, "HelloSolution", false, [(programMain, "true")]);

        var solution = await ProjectCreator.CreateAsync(request, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(dir, "HelloSolution", "HelloSolution.slnx"), solution);
        Assert.Contains("static void Main", File.ReadAllText(Path.Combine(dir, "HelloSolution", "Hello", "Program.cs")));
        var loaded = await SolutionLoader.LoadAsync(solution, TestContext.Current.CancellationToken);
        Assert.Equal("Hello", Assert.Single(loaded.Projects).Name);
    }

    [Fact]
    public async Task Multi_project_template_keeps_its_own_solution_in_the_same_directory()
    {
        var templates = await TemplateCatalog.LoadAsync(TestContext.Current.CancellationToken);
        var blazor = templates.Single(t => t.ShortName == "blazor" && t.Language == "C#");
        var mode = blazor.Options.Single(o => o.Symbol == "InteractivityPlatform");
        var dir = TempDir();
        var request = new NewProjectRequest(blazor, "Shop", dir, "Shop", true, [(mode, "Auto")]);

        var solution = await ProjectCreator.CreateAsync(request, _ => { }, TestContext.Current.CancellationToken, restore: false);

        // Server + WebAssembly client projects, in the template's own solution.
        Assert.Equal(Path.Combine(dir, "Shop"), Path.GetDirectoryName(solution));
        var loaded = await SolutionLoader.LoadAsync(solution, TestContext.Current.CancellationToken);
        Assert.Equal(["Shop", "Shop.Client"], loaded.Projects.Select(p => p.Name).Order());
    }
}

public class StartViewModelTests
{
    static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0);

    [Fact]
    public void Groups_recent_solutions_like_visual_studio()
    {
        var settings = new Settings();
        foreach (var (path, opened) in new[]
                 {
                     ("/src/Today.sln", Now.AddHours(-1)), ("/src/Yesterday.slnx", Now.AddDays(-1)), ("/src/Week.sln", Now.AddDays(-4)),
                     ("/src/Month.sln", Now.AddDays(-20)), ("/src/Old.sln", Now.AddDays(-90)), ("/src/Pinned.sln", Now.AddDays(-200)),
                 })
        {
            settings.RecentSolutions.Add(path);
            settings.RecentOpened[path] = opened;
        }
        settings.RecentSolutions.Add("/src/NoDate.csproj");
        settings.PinnedSolutions.Add("/src/Pinned.sln");

        var vm = new StartViewModel(settings, () => Now, _ => Task.FromResult<IReadOnlyList<ProjectTemplate>>([]));
        Assert.Equal(["Pinned", "Today", "Yesterday", "This week", "This month", "Older"], vm.RecentGroups.Select(g => g.Header));
        Assert.Equal(["Old.sln", "NoDate.csproj"], vm.RecentGroups[^1].Items.Select(i => i.Name));
        Assert.Equal("Project", vm.RecentGroups[^1].Items[1].IconKind);

        vm.RecentSearch = "week";
        Assert.Equal("Week.sln", Assert.Single(Assert.Single(vm.RecentGroups).Items).Name);
        vm.RecentSearch = "";

        var today = vm.RecentGroups.Single(g => g.Header == "Today").Items[0];
        vm.TogglePinCommand.Execute(today);
        Assert.Equal(["Pinned.sln", "Today.sln"], vm.RecentGroups[0].Items.Select(i => i.Name).Order());
        vm.RemoveRecentCommand.Execute(vm.RecentGroups[0].Items.First(i => i.Name == "Today.sln"));
        Assert.DoesNotContain("/src/Today.sln", settings.RecentSolutions);
        Assert.DoesNotContain("/src/Today.sln", settings.PinnedSolutions);
    }

    [Fact]
    public void Recent_list_keeps_pinned_entries_when_trimming()
    {
        var settings = new Settings();
        settings.AddRecent("/p/Pinned.sln");
        settings.PinnedSolutions.Add("/p/Pinned.sln");
        for (var i = 0; i < 30; i++)
            settings.AddRecent($"/p/S{i}.sln");
        Assert.Equal(20, settings.RecentSolutions.Count);
        Assert.Contains("/p/Pinned.sln", settings.RecentSolutions);
        Assert.Equal("/p/S29.sln", settings.RecentSolutions[0]);
    }

    [Fact]
    public void Finds_the_solution_or_project_in_a_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsl-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "App", "bin"));
        File.WriteAllText(Path.Combine(root, "src", "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "src", "App", "bin", "Copy.sln"), "");
        // No solution (the one under bin/ does not count): the project opens on its own.
        Assert.Equal(Path.Combine(root, "src", "App", "App.csproj"), StartViewModel.FindSolution(root));
        File.WriteAllText(Path.Combine(root, "src", "All.slnx"), "<Solution />");
        Assert.Equal(Path.Combine(root, "src", "All.slnx"), StartViewModel.FindSolution(root));
        Assert.Null(StartViewModel.FindSolution(Path.Combine(root, "src", "App", "bin", "missing")));
    }

    [Theory]
    [InlineData("https://github.com/owner/App.git", "App")]
    [InlineData("https://github.com/owner/App/", "App")]
    [InlineData("git@github.com:owner/Tools.git", "Tools")]
    [InlineData("/home/me/repos/Local", "Local")]
    public void Names_the_clone_folder_after_the_repository(string url, string name) =>
        Assert.Equal(name, StartViewModel.RepositoryName(url));
}
