# VSL — Visual Studio for Linux

A native Linux IDE for C#, ASP.NET Core and Blazor with a Visual Studio look and workflow.
Built with Avalonia; IntelliSense comes from Microsoft's Roslyn language server (the same engine
Visual Studio uses), including cohosted Razor support.

## Install

```bash
./scripts/install.sh          # publishes to ~/.local/share/VSL/app, adds `vsl` and an app-menu entry
vsl path/to/App.sln           # or open VSL from the applications menu
```

Requires the .NET 10 SDK (`~/.dotnet` or `DOTNET_ROOT`). On first solution open VSL installs
`roslyn-language-server` (nuget.org, MIT) into `~/.local/share/VSL/roslyn/`.

## What works

| Area | Details |
|---|---|
| Solutions | `.sln` / `.slnx`, solution folders, Solution Explorer with VS file nesting (`X.razor.cs`, `X.razor.css`, `appsettings.*.json`, `*.Designer.cs`), Dependencies (frameworks, packages, projects), search (Ctrl+;) |
| Editor | Syntax highlighting (VS Dark), C# and Razor IntelliSense, parameter info, Quick Info hover, squiggles, Go To Definition / Implementation, Find All References, Rename, Format Document, comment/uncomment, find (Ctrl+F) and Find in Files |
| Build | Build / Rebuild / Clean via `dotnet`, Output window, Error List (Build + IntelliSense) |
| Run | Startup project and `launchSettings.json` profiles, Start Without Debugging (Ctrl+F5), Stop; opens the browser for `launchBrowser` profiles |
| Debug | F5 with netcoredbg (MIT, installed on first use): breakpoints (F9 / margin click, kept per solution and moved with edits), current-statement arrow, Continue, Break All, Restart, Step Over/Into/Out (F10/F11/Shift+F11), Locals, Watch, Call Stack, hover data tips |
| Session | Restores open documents, startup project, profile and configuration per solution |

Not yet: conditional breakpoints, exception settings, Immediate window, debugging Blazor WebAssembly code
(it runs in the browser), lightbulb code actions (Ctrl+.), Test Explorer, NuGet package manager UI,
Git integration, WinForms/WPF designers (Windows-only).

## Keyboard shortcuts

Help → Keyboard Shortcuts lists them; they follow Visual Studio's defaults (F12, Shift+F12,
Ctrl+Shift+B, Ctrl+F5, Ctrl+K Ctrl+D, Ctrl+K Ctrl+C/U, F2, Ctrl+Space, Ctrl+Shift+Space).

## Layout

```
src/VSL/
  Lsp/         JSON-RPC, Roslyn client, client-side file watching, HTML service for Razor cohosting
  Workspace/   solution/project loading and the Solution Explorer tree
  Build/       dotnet process runner and MSBuild output parser
  Debugging/   DAP client, netcoredbg installer, debug session, breakpoint store
  Editor/      AvaloniaEdit host: sync, completion, hover, squiggles, TextMate theme
  ViewModels/  main window state, documents, Error List, Output
  Views/       main window (XAML), icons, dialogs
tests/VSL.Tests/
  unit tests, Roslyn integration tests and headless UI tests against TestAssets/BlazorSample
```

Run the tests with `dotnet build VSL.slnx && dotnet tests/VSL.Tests/bin/Debug/net10.0/VSL.Tests.dll`.
Headless UI tests write screenshots to `$TMPDIR/vsl-test-artifacts`.

### Notes on the language server

- Roslyn is started with `--csharpDesignTimePath` pointing at the design-time targets it ships with;
  Razor support is built in, so the Razor extension must not be passed again with `--extension`.
- VSL implements `workspace/didChangeWatchedFiles` itself (one recursive watcher). Otherwise Roslyn
  creates hundreds of watchers and runs into the default inotify limit of 128 instances per user.
- Razor cohosting sends `razor/updateHtml` plus HTML `textDocument/*` requests to the client;
  `HtmlLanguageService` answers them (HTML tags/attributes) so Razor can merge in component completions.

## License

VSL is released under the [MIT License](LICENSE).

## Third-party

Cascadia Mono font (SIL OFL 1.1, `src/VSL/Assets/Fonts`), roslyn-language-server and netcoredbg (MIT, downloaded at runtime),
Avalonia, AvaloniaEdit and TextMateSharp (MIT).
