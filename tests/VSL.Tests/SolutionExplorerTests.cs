using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using VSL.ViewModels;
using VSL.Views;
using VSL.Workspace;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

public class ItemTemplateTests
{
    static string NewProject(string name, string? rootNamespace = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-items-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        var property = rootNamespace is null ? "" : $"<RootNamespace>{rootNamespace}</RootNamespace>";
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{property}</PropertyGroup></Project>");
        return Path.Combine(dir, name + ".csproj");
    }

    [Fact]
    public void Names_and_namespaces_follow_the_project_and_folder()
    {
        var csproj = NewProject("Shop.Api");
        var dir = Path.GetDirectoryName(csproj)!;
        var project = ProjectInfo.Load("Shop.Api", csproj);
        Assert.Equal("Shop.Api", ItemTemplates.Namespace(project, dir));
        Assert.Equal("Shop.Api.Services.Orders", ItemTemplates.Namespace(project, Path.Combine(dir, "Services", "Orders")));
        Assert.Equal("Shop.Api._2024_reports", ItemTemplates.Namespace(project, Path.Combine(dir, "2024 reports")));

        var declared = NewProject("Web", rootNamespace: "Acme.Web");
        Assert.Equal("Acme.Web.Pages", ItemTemplates.Namespace(ProjectInfo.Load("Web", declared), Path.Combine(Path.GetDirectoryName(declared)!, "Pages")));

        Assert.Equal("namespace Shop.Api;\n\npublic interface IOrders\n{\n}\n",
            ItemTemplates.Render(ItemTemplates.All.Single(t => t.Name == "Interface"), "IOrders.cs", dir, project));
        Assert.Equal("<h3>OrderList</h3>\n\n@code {\n\n}\n", ItemTemplates.Render(ItemTemplates.RazorComponent, "OrderList.razor", dir, project));

        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "");
        Assert.Equal("Class2.cs", ItemTemplates.SuggestName(ItemTemplates.Class, dir));
        Assert.Equal("Widget.razor", ItemTemplates.FileName(ItemTemplates.RazorComponent, "Widget"));
        Assert.Equal("notes.txt", ItemTemplates.FileName(ItemTemplates.Class, " notes.txt "));
        Assert.Contains("already exists", ItemTemplates.Validate(dir, "Class1.cs"));
        Assert.NotNull(ItemTemplates.Validate(dir, "a/b.cs"));
        Assert.Null(ItemTemplates.Validate(dir, "Order.cs"));
    }

    [Fact]
    public void Add_new_item_suggests_a_name_until_one_is_typed()
    {
        var dir = Path.GetDirectoryName(NewProject("App"))!;
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "");
        var vm = new AddItemViewModel(dir, ItemTemplates.Class);
        Assert.Equal("Class", vm.SelectedTemplate?.Name);
        Assert.Equal("Class2.cs", vm.Name);

        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Interface");
        Assert.Equal("Interface1.cs", vm.Name);
        vm.Name = "IOrders";
        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Record");
        Assert.Equal("IOrders", vm.Name);
        Assert.Equal(new NewItemChoice(ItemTemplates.All.Single(t => t.Name == "Record"), "IOrders"), vm.Result);

        vm.Search = "razor";
        Assert.Equal("Razor Component", Assert.Single(vm.Templates).Name);
        Assert.Equal("Razor Component", vm.SelectedTemplate?.Name);
        vm.Search = "";
        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Class");
        vm.Name = "Class1";
        Assert.False(vm.CanAdd);
        Assert.Null(vm.Result);
    }
}

[Collection(SampleSolutionCollection.Name)]
public class SolutionExplorerTests
{
    /// <summary>Dialog answers scripted by the test (the real ones are modal windows).</summary>
    sealed class ScriptedDialogs : IDialogService
    {
        public NewItemChoice? NextItem { get; set; }

        public IReadOnlyList<string> NextFiles { get; set; } = [];

        public List<string> Confirmations { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult<bool?>(true);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult(NextItem);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(NextFiles);
    }

    /// <summary>A copy of the sample sources (no restore needed: these tests only touch files).</summary>
    static string CopySample()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-explorer-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "BlazorSample");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return Path.Combine(dir, "BlazorSample.sln");
    }

    static IEnumerable<string> Headers(IEnumerable<object> items) => items.OfType<MenuItem>().Select(m => (string)m.Header!);

    [AvaloniaFact]
    public async Task Adds_renames_and_deletes_files_like_visual_studio()
    {
        var sln = CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new ScriptedDialogs();
        vm.Dialogs = dialogs;
        vm.MoveToTrash = path => Task.FromResult(false);
        var root = vm.ExplorerRoots[0];
        var web = root.Children.First(n => n.Name == "Web");
        web.IsExpanded = true;

        // VS's context menus: Add on projects and folders, Rename and Delete on folders and files.
        var projectMenu = window.ExplorerMenuItems(web);
        Assert.Contains("Edit Project File", Headers(projectMenu));
        var add = projectMenu.OfType<MenuItem>().Single(m => (string?)m.Header == "Add");
        Assert.Equal(["New Item...", "Existing Item...", "New Folder", "Class...", "Razor Component..."], Headers(add.Items.OfType<object>()));
        var program = web.Children.First(n => n.Name == "Program.cs");
        Assert.Contains("Rename", Headers(window.ExplorerMenuItems(program)));
        Assert.Contains("Delete", Headers(window.ExplorerMenuItems(program)));

        // Add > New Item: the chosen template becomes a file in the folder's namespace, shown, selected and opened.
        dialogs.NextItem = new NewItemChoice(ItemTemplates.Class, "OrderService");
        var created = await vm.AddNewItemAsync(web);
        Assert.Equal(Path.Combine(dir, "Web", "OrderService.cs"), created);
        Assert.Equal("namespace Web;\n\npublic class OrderService\n{\n}\n", File.ReadAllText(created!));
        Assert.Equal("OrderService.cs", vm.ActiveEditor?.FileName);
        Assert.Contains(web.Children, n => n.Name == "OrderService.cs");
        Assert.Equal("OrderService.cs", (window.ExplorerTree.SelectedItem as ExplorerNode)?.Name);

        // Add > New Folder makes NewFolder1 and renames it in place.
        var folder = vm.AddNewFolder(web)!;
        Assert.Equal("NewFolder1", folder.Name);
        TextBox? RenameBox(ExplorerNode node) => (window.ExplorerTree.TreeContainerFromItem(node) as Control)?
            .GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("rename") && b.IsVisible);
        await WaitUntil(() => RenameBox(folder) is { IsFocused: true }, TimeSpan.FromSeconds(5));
        var box = RenameBox(folder)!;
        Assert.Equal("NewFolder1", box.SelectedText);
        box.Text = "Models";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Assert.True(Directory.Exists(Path.Combine(dir, "Web", "Models")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Web", "NewFolder1")));
        var models = web.Children.Single(n => n.Name == "Models");
        Assert.False(models.IsRenaming);

        // A class in a folder gets the folder in its namespace; Add Existing Item copies files in.
        dialogs.NextItem = new NewItemChoice(ItemTemplates.Class, "Order");
        Assert.StartsWith("namespace Web.Models;", File.ReadAllText((await vm.AddNewItemAsync(models))!));
        var outside = Path.Combine(Path.GetTempPath(), "vsl-existing-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outside, "{}");
        dialogs.NextFiles = [outside];
        var copied = Assert.Single(await vm.AddExistingItemsAsync(models));
        Assert.Equal(Path.Combine(dir, "Web", "Models", Path.GetFileName(outside)), copied);
        Assert.Contains(models.Children, n => n.Path == copied);

        // Renaming a component takes its nested files along, and an open nested file keeps its unsaved edits.
        var components = root.Children.First(n => n.Name == "Components");
        components.IsExpanded = true;
        var greeting = components.Children.First(n => n.Name == "Greeting.razor");
        var codeBehind = vm.OpenDocument(Path.Combine(dir, "Components", "Greeting.razor.cs"))!;
        codeBehind.Host.Document.Insert(0, "// edited\n");
        Assert.True(vm.Rename(greeting, "Welcome.razor"));
        Assert.Equal(["Welcome.razor", "Welcome.razor.cs", "Welcome.razor.css"],
            Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Welcome*").Select(Path.GetFileName).Order());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Greeting*"));
        var moved = Assert.Single(vm.Editors, d => d.FileName == "Welcome.razor.cs");
        Assert.True(moved.IsDirty);
        Assert.StartsWith("// edited", moved.Host.Document.Text);
        Assert.DoesNotContain(vm.Editors, d => d.FileName == "Greeting.razor.cs");
        var welcome = components.Children.Single(n => n.Name == "Welcome.razor");
        Assert.Equal(["Welcome.razor.cs", "Welcome.razor.css"], welcome.Children.Select(c => c.Name).Order());

        // Delete asks first, then removes the file with its nested files and closes them.
        Assert.True(await vm.DeleteAsync(welcome));
        Assert.Equal(["'Welcome.razor' will be deleted."], dialogs.Confirmations);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "Components"), "Welcome*"));
        Assert.DoesNotContain(vm.Editors, d => d.FileName == "Welcome.razor.cs");
        Assert.DoesNotContain(components.Children, n => n.Name.StartsWith("Welcome", StringComparison.Ordinal));

        // Files created outside VSL appear by themselves; loaded folders keep their state.
        models.IsExpanded = true;
        File.WriteAllText(Path.Combine(dir, "Web", "Notes.md"), "# Notes\n");
        await WaitUntil(() => web.Children.Any(n => n.Name == "Notes.md"), TimeSpan.FromSeconds(10));
        Assert.Contains(web.Children, n => n.Name == "Notes.md");
        Assert.Same(models, web.Children.Single(n => n.Name == "Models"));
        Assert.True(models.IsExpanded);

        Assert.True(await vm.CloseSolutionCoreAsync());
    }
}
