using VSL.Lsp;
using VSL.Workspace;

namespace VSL.Tests;

/// <summary>Talks to the real Roslyn language server about the Blazor sample solution.</summary>
public class RoslynIntegrationTests
{
    static LspPosition PositionOf(string text, string needle, int extra = 1)
    {
        var i = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{needle}' not found");
        var line = text[..i].Count(c => c == '\n');
        var col = i - (i == 0 ? 0 : text.LastIndexOf('\n', i - 1) + 1) + extra;
        return new LspPosition(line, col);
    }

    [Fact]
    public async Task Csharp_and_razor_intellisense_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var sln = await SampleSolution.GetAsync();
        var install = await RoslynServerLocator.EnsureInstalledAsync(_ => { }, ct);
        var ready = new TaskCompletionSource();
        await using var lsp = await RoslynClient.StartAsync(install, sln, Path.Combine(Path.GetTempPath(), "vsl-test-logs"), ct);
        lsp.ProjectsLoaded += () => ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);

        // Razor: hover and go to definition on a component from the same library.
        var home = SampleSolution.PathOf(sln, "Components/Home.razor");
        var homeText = File.ReadAllText(home);
        await lsp.DidOpenAsync(home, homeText);
        await Task.Delay(2000, ct);
        var hover = await lsp.HoverAsync(home, PositionOf(homeText, "<Greeting", 2), ct);
        Assert.Contains("Components.Greeting", hover);
        var def = await lsp.DefinitionAsync(home, PositionOf(homeText, "<Greeting", 2), ct);
        Assert.Contains(def, l => l.FilePath.EndsWith("Greeting.razor", StringComparison.Ordinal));
        var completion = await lsp.CompletionAsync(home, PositionOf(homeText, "@count", 2), null, ct);
        Assert.Contains(completion.Items, i => i.Label == "count");

        // Razor diagnostics: an unknown symbol in markup is reported against the .razor file.
        var broken = homeText.Replace("@count times", "@noSuchSymbol times");
        await lsp.DidChangeAsync(home, 2, [new LspContentChange(null, broken)]);
        var razorDiagnostics = await lsp.DiagnosticsAsync(home, ct);
        Assert.Contains(razorDiagnostics, d => d.Code == "CS0103");

        // C#: injected errors show up as diagnostics without touching the file on disk.
        var cs = SampleSolution.PathOf(sln, "Components/Greeting.razor.cs");
        var csText = File.ReadAllText(cs);
        var brace = csText.IndexOf('{', csText.IndexOf("class ", StringComparison.Ordinal)) + 1;
        var withError = csText[..brace] + "\n    int __bad = \"abc\";\n" + csText[brace..];
        await lsp.DidOpenAsync(cs, withError);
        var diagnostics = await lsp.DiagnosticsAsync(cs, ct);
        Assert.Contains(diagnostics, d => d.Code == "CS0029" && d.Severity == DiagnosticSeverity.Error);

        // Incremental edit: remove the bad line again and the error goes away.
        var badLine = PositionOf(withError, "    int __bad", 0).Line;
        await lsp.DidChangeAsync(cs, 2, [new LspContentChange(new LspRange(new LspPosition(badLine, 0), new LspPosition(badLine + 1, 0)), "")]);
        diagnostics = await lsp.DiagnosticsAsync(cs, ct);
        Assert.DoesNotContain(diagnostics, d => d.Code == "CS0029");

        // Member completion after "System.Console.".
        const string method = "    void __M() { System.Console.";
        await lsp.DidChangeAsync(cs, 3, [new LspContentChange(new LspRange(new LspPosition(badLine, 0), new LspPosition(badLine, 0)), method + " }\n")]);
        var members = await lsp.CompletionAsync(cs, new LspPosition(badLine, method.Length), ".", ct);
        Assert.Contains(members.Items, i => i.Label == "WriteLine");

        Assert.True(lsp.WatcherCount > 0, "server should delegate file watching to the client");
    }
}

public class SolutionLoaderTests
{
    [Fact]
    public async Task Loads_solution_tree_like_visual_studio()
    {
        var ct = TestContext.Current.CancellationToken;
        var sln = await SampleSolution.GetAsync();
        var solution = await SolutionLoader.LoadAsync(sln, ct);

        Assert.Equal(["Components", "Web"], solution.Projects.Select(p => p.Name).Order());
        var web = solution.Projects.Single(p => p.Name == "Web");
        Assert.True(web.IsWeb);
        Assert.True(web.IsRunnable);
        Assert.Equal(["http"], web.LaunchProfiles.Select(p => p.Name));
        Assert.Equal("http://localhost:5280", web.LaunchProfiles[0].ApplicationUrl);
        Assert.False(solution.Projects.Single(p => p.Name == "Components").IsRunnable);

        var root = solution.Root;
        var items = root.Children.Single(n => n.Kind == NodeKind.SolutionFolder);
        Assert.Equal("Solution Items", items.Name);
        Assert.Contains(items.Children, n => n.Name == "README.md");

        var webNode = root.Children.Single(n => n.Name == "Web");
        webNode.EnsureLoaded();
        Assert.Equal(["Dependencies", "Properties"], webNode.Children.Take(2).Select(n => n.Name));
        Assert.DoesNotContain(webNode.Children, n => n.Name is "bin" or "obj" or "Web.csproj");
        var appsettings = webNode.Children.Single(n => n.Name == "appsettings.json");
        Assert.Equal(["appsettings.Development.json"], appsettings.Children.Select(c => c.Name));

        var greeting = root.FindByPath(SampleSolution.PathOf(sln, "Components/Greeting.razor"));
        Assert.NotNull(greeting);
        Assert.Equal(["Greeting.razor.cs", "Greeting.razor.css"], greeting.Children.Select(c => c.Name));
    }
}
