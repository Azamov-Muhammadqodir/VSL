using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using VSL.Views;

namespace VSL.Tests;

public class MaterialIconTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("Web.csproj", "visualstudio")]
    [InlineData("BlazorSample.slnx", "visualstudio")]
    [InlineData("Index.razor", "razor")]
    [InlineData("_Imports.cshtml", "razor")]
    [InlineData(".gitignore", "git")]
    [InlineData("README.md", "readme")]
    [InlineData("SPEC.md", "markdown")]
    [InlineData("Dockerfile", "docker")]
    [InlineData("nuget.config", "nuget")]
    [InlineData("LICENSE", "license")]
    [InlineData("appsettings.Development.json", "json")]
    [InlineData("site.css", "css")]
    [InlineData("data.unknownextension", "file")]
    public void Files_get_the_vs_code_theme_icon(string file, string icon) => Assert.Equal(icon, MaterialIcons.ForFile(file));

    [Theory]
    [InlineData("Models", false, "folder-class")]
    [InlineData("Models", true, "folder-class-open")]
    [InlineData("Properties", false, "folder-config")]
    [InlineData("wwwroot", false, "folder-public")]
    [InlineData("tests", true, "folder-test-open")]
    [InlineData("Extensions", false, "folder-plugin")]
    [InlineData("SomethingElse", false, "folder")]
    [InlineData("SomethingElse", true, "folder-open")]
    public void Folders_get_icons_by_name(string folder, bool expanded, string icon) => Assert.Equal(icon, MaterialIcons.ForFolder(folder, expanded));

    [AvaloniaFact]
    public void Every_shipped_icon_draws()
    {
        var empty = new List<string>();
        foreach (var name in MaterialIcons.All)
        {
            var image = Assert.IsType<DrawingImage>(MaterialIcons.Image(name));
            // More than the transparent frame: at least one shape was understood.
            if (image.Drawing is not DrawingGroup { Children.Count: > 1 })
                empty.Add(name);
        }
        Assert.True(MaterialIcons.All.Count() > 1000);
        Assert.True(empty.Count == 0, $"{empty.Count} icons draw nothing: {string.Join(", ", empty.Take(40))}");
    }

    [AvaloniaFact]
    public void Gallery_of_common_icons()
    {
        string[] names =
        [
            "csharp", "visualstudio", "razor", "json", "xml", "settings", "markdown", "readme", "git", "docker", "nuget", "css",
            "javascript", "typescript", "html", "database", "yaml", "image", "svg", "console", "license", "file", "folder", "folder-open",
            "folder-class", "folder-config", "folder-public", "folder-test", "folder-src", "folder-components", "folder-views",
            "folder-controller", "folder-database", "folder-migrations", "folder-plugin", "folder-enum", "folder-dist", "folder-github",
        ];
        var grid = new WrapPanel { Width = 760, Background = new SolidColorBrush(Color.Parse("#252526")) };
        foreach (var name in names)
            grid.Children.Add(new StackPanel
            {
                Width = 95,
                Margin = new Thickness(0, 6),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Children =
                        {
                            new Image { Source = MaterialIcons.Image(name), Width = 16, Height = 16 },
                            new Image { Source = MaterialIcons.Image(name), Width = 32, Height = 32 },
                        },
                    },
                    new TextBlock { Text = name, FontSize = 10, Foreground = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                },
            });
        var window = new Window { Width = 760, Height = 380, Content = grid, Background = grid.Background };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(SampleSolution.ArtifactsDir, "material-icons.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }
}
