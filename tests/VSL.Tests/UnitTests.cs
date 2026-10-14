using System.Text.Json;
using VSL.Build;
using VSL.Editor;
using VSL.Lsp;
using VSL.Workspace;

namespace VSL.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("**/*{.cs,.cshtml,.razor}", "Pages/Index.razor", true)]
    [InlineData("**/*{.cs,.cshtml,.razor}", "Program.cs", true)]
    [InlineData("**/*{.cs,.cshtml,.razor}", "wwwroot/site.css", false)]
    [InlineData("Server.csproj", "Server.csproj", true)]
    [InlineData("Server.csproj", "sub/Server.csproj", false)]
    [InlineData("**/*.dll", "a/b/c.dll", true)]
    [InlineData("project.assets.json", "project.assets.json", true)]
    [InlineData("*.json", "a/b.json", false)]
    [InlineData("[!a]bc", "xbc", true)]
    [InlineData("[!a]bc", "abc", false)]
    public void Matches(string glob, string path, bool expected) =>
        Assert.Equal(expected, Glob.ToRegex(glob).IsMatch(path));
}

public class FileWatchBridgeTests
{
    [Fact]
    public void Matches_only_registrations_inside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsl-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Client"));
        using var bridge = new FileWatchBridge(root, _ => Task.CompletedTask);
        var options = JsonSerializer.SerializeToElement(new
        {
            watchers = new object[]
            {
                new { globPattern = new { baseUri = new Uri(Path.Combine(root, "Client")).AbsoluteUri, pattern = "**/*{.cs,.razor}" } },
                new { globPattern = new { baseUri = "file:///opt/nuget/packages/x/lib", pattern = "X.dll" } },
            },
        });
        bridge.Register("1", options);
        Assert.Equal(1, bridge.WatcherCount);
        Assert.True(bridge.Matches(Path.Combine(root, "Client", "Pages", "A.razor"), 1));
        Assert.False(bridge.Matches(Path.Combine(root, "Client", "site.css"), 1));
        Assert.False(bridge.Matches(Path.Combine(root, "Server", "B.cs"), 1));
        Directory.Delete(root, true);
    }
}

public class SnippetTests
{
    [Theory]
    [InlineData("Foo($0)", "Foo()", 4)]
    [InlineData("for (int ${1:i} = 0; $1 < ${2:length}; $1++)$0", "for (int i = 0;  < length; ++)", 9)]
    [InlineData("plain", "plain", -1)]
    [InlineData(@"cost \$5", "cost $5", -1)]
    public void Expands(string snippet, string text, int caret)
    {
        var (t, c) = Snippet.Expand(snippet);
        Assert.Equal(text, t);
        Assert.Equal(caret, c);
    }
}

public class FileNestingTests
{
    [Fact]
    public void Nests_like_visual_studio()
    {
        var result = FileNesting.Nest([
            "Counter.razor", "Counter.razor.cs", "Counter.razor.css",
            "appsettings.json", "appsettings.Development.json",
            "SharedResource.resx", "SharedResource.Designer.cs",
            "Program.cs", "Home.razor",
        ]).ToDictionary(x => x.Name, x => x.Children);

        Assert.Equal(["Counter.razor.cs", "Counter.razor.css"], result["Counter.razor"]);
        Assert.Equal(["appsettings.Development.json"], result["appsettings.json"]);
        Assert.Equal(["SharedResource.Designer.cs"], result["SharedResource.resx"]);
        Assert.Empty(result["Program.cs"]);
        Assert.False(result.ContainsKey("Counter.razor.cs"));
        Assert.Equal(5, result.Count);
    }
}

public class BuildOutputParserTests
{
    [Fact]
    public void Parses_compiler_error_with_project()
    {
        var d = BuildOutputParser.Parse("/src/Client/Pages/Index.razor(12,5): error CS0103: The name 'x' does not exist in the current context [/src/Client/Client.csproj]");
        Assert.NotNull(d);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("CS0103", d.Code);
        Assert.Equal("/src/Client/Pages/Index.razor", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(5, d.Column);
        Assert.Equal("The name 'x' does not exist in the current context", d.Message);
        Assert.Equal("/src/Client/Client.csproj", d.Project);
    }

    [Fact]
    public void Parses_nuget_warning_without_location()
    {
        var d = BuildOutputParser.Parse("/src/Server/Server.csproj : warning NU1603: Server depends on X (>= 1.0) but X 1.0 was not found. [/src/App.sln]");
        Assert.NotNull(d);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("NU1603", d.Code);
        Assert.Equal(0, d.Line);
    }

    [Fact]
    public void Ignores_normal_lines() =>
        Assert.Null(BuildOutputParser.Parse("  Shared -> /src/Shared/bin/Debug/net9.0/Shared.dll"));
}

public class LspParseTests
{
    [Fact]
    public void Completion_uses_item_defaults()
    {
        var json = JsonDocument.Parse("""
            {"isIncomplete":false,
             "itemDefaults":{"editRange":{"start":{"line":1,"character":4},"end":{"line":1,"character":6}},"data":{"id":7}},
             "items":[{"label":"Console","kind":7},{"label":"Foo","kind":2,"textEditText":"Foo()","insertTextFormat":2}]}
            """).RootElement;
        var list = LspParse.Completion(json);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(new LspPosition(1, 4), list.Items[0].TextEdit!.Range.Start);
        Assert.Equal("Console", list.Items[0].TextEdit!.NewText);
        Assert.Equal("Foo()", list.Items[1].TextEdit!.NewText);
        Assert.True(list.Items[1].IsSnippet);
        Assert.True(list.Items[0].NeedsResolve);
    }
}
