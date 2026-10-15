# VSL — Visual Studio for Linux

A native Linux IDE for C#, ASP.NET Core and Blazor with a Visual Studio look and workflow.
Built with Avalonia; IntelliSense comes from Microsoft's Roslyn language server (the same engine
Visual Studio uses), including cohosted Razor support.

## Install

From a [release](https://github.com/nbkabdulakhadov/VSL/releases) (Linux x64, no .NET runtime needed to run VSL):

```bash
sudo apt install ./vsl_0.1.0_amd64.deb                 # Debian/Ubuntu: `vsl` command and menu entry
chmod +x VSL-0.1.0-x86_64.AppImage && ./VSL-0.1.0-x86_64.AppImage   # any distribution
tar xzf vsl-0.1.0-linux-x64.tar.gz && vsl-0.1.0/VSL     # unpack anywhere
```

From source:

```bash
./scripts/install.sh          # publishes to ~/.local/share/VSL/app, adds `vsl` and an app-menu entry
vsl path/to/App.sln           # opens it directly (a .slnx or .csproj works too)
vsl                           # start window: recent, clone, open, create a new project
```

Building and running solutions needs the .NET 10 SDK (`~/.dotnet`, `DOTNET_ROOT` or on `PATH`). On first
solution open VSL installs `roslyn-language-server` (nuget.org, MIT) into `~/.local/share/VSL/roslyn/`.

### Releasing

CI (`.github/workflows/ci.yml`) builds and runs the tests on every push and pull request. A version tag
publishes a release with the `.deb`, `.AppImage` and `.tar.gz` (`.github/workflows/release.yml`):

```bash
git tag v0.1.0 && git push origin v0.1.0
```

Running the release workflow by hand (Actions > release > Run workflow) builds the packages as artifacts
without releasing. `deploy/linux/package.sh` builds the same packages locally.

## What works

| Area | Details |
|---|---|
| Start window | Shown at launch like VS: Open recent (search, Today / This week groups, pin, remove), Clone a repository, Open a project or solution, Open a local folder, Create a new project, Continue without code; also File > Start Window, File > New > Project (Ctrl+Shift+N), File > Clone Repository |
| New project | Every project template installed in the .NET SDK (`dotnet new`), with its own icon, search and language / platform / project type filters, recent templates; Configure (name, location, solution, same directory) and Additional information (framework, authentication, HTTPS and the options VS shows for that template); creates the solution and opens it |
| Solutions | `.sln` / `.slnx`, or a project file on its own (as VS opens a .csproj), solution folders, Solution Explorer with Material Icon Theme file and folder icons (by file name, extension and folder name, as in VS Code), with VS file nesting (`X.razor.cs`, `X.razor.css`, `appsettings.*.json`, `*.Designer.cs`), Dependencies (frameworks, packages, projects), search (Ctrl+;) |
| Editor | Syntax highlighting (VS Dark), C# and Razor IntelliSense, parameter info, Quick Info hover, squiggles, Quick Actions and Refactorings (Ctrl+. lightbulb: add using, generate type/method, refactorings), Go To Definition / Implementation, Find All References, Rename, Format Document, comment/uncomment, find (Ctrl+F) and Find in Files, outlining (collapse / expand methods, types, #regions, doc comments and blocks from Roslyn's folding ranges, with a scan for JSON, XML/csproj and other files; +/- margin, Ctrl+M Ctrl+M / Ctrl+M Ctrl+L / Ctrl+M Ctrl+O / Ctrl+M Ctrl+X, Edit > Outlining), VS-style editor context menu, zoom (Ctrl+wheel, Ctrl+Shift+. / Ctrl+Shift+, , Ctrl+0, 20–400 % picker) with the document health indicator |
| Build | Build / Rebuild / Clean via `dotnet`, Output window, Error List (Build + IntelliSense) |
| Run | Startup project and `launchSettings.json` profiles, Start Without Debugging (Ctrl+F5), Stop; opens the browser for `launchBrowser` profiles |
| Debug | F5 with netcoredbg (MIT, installed on first use): breakpoints (F9 / margin click, kept per solution and moved with edits), current-statement arrow, Continue, Break All, Restart, Step Over/Into/Out (F10/F11/Shift+F11), Locals, Watch, Call Stack, hover data tips |
| Test | Test Explorer on the SDK's vstest (xUnit, NUnit, MSTest): build + discover, Run All / Run / Run Failed / Repeat Last / Cancel, Project › Namespace › Class › Method tree with theory rows, live outcomes and durations, error message and stack trace, double-click to source; Debug All / Debug Selected stop at breakpoints in tests (vstest attaches netcoredbg to the test host); Ctrl+R A, Ctrl+R L, Ctrl+R Ctrl+A, Ctrl+E T |
| Git | Git Changes window (staged / unstaged lists, stage, unstage, undo, Commit All / Commit Staged, Fetch, Pull, Push, Sync; follows commits, checkouts and edits made outside VSL, e.g. in the terminal), side-by-side diff (double-click a change or Compare with Unmodified: aligned rows, word-level highlights, both sides scroll together, next/previous change), branch and pending-change count in the status bar with branch switch / new branch, M/A/D/U badges in Solution Explorer, live change bars in the editor margin against HEAD, Git menu |
| Terminal | Integrated terminal (View > Terminal, Ctrl+`): your `$SHELL` in a real pty, started in the solution folder with VSL's dotnet on PATH; truecolor, scrollback, selection, search; New / Kill; keys go to the shell (Ctrl+R, Ctrl+E...) |
| NuGet | Package Manager for a project or the whole solution: Browse (search across your configured feeds, prerelease, version picker, Install), Installed (Uninstall, change version), Updates (Update / Update All); runs `dotnet add/remove/list package`, so NuGet.Config feeds and credentials apply |
| Claude Code | A Claude Code tool window like the VS Code extension (View > Claude Code, Ctrl+Esc, editor > Ask Claude): it drives your installed `claude` CLI in its documented headless stream-json mode in the solution folder, so your login, settings, tools, MCP servers and slash commands apply. Streams answers (Markdown), shows tool calls with inline diffs, todo lists and results, asks before edits and commands (Yes / don't ask again / No with feedback, side-by-side diff of the proposed change), answers Claude's clarifying questions and plans, a Modes menu (Manual, Edit automatically, Plan, Auto; Shift+Tab) with an effort slider (low ... max, as the model allows), Account & Usage (plan, session / weekly limits with resets, what drives usage; /usage), MCP servers by scope with their state, sign-in, retry, enable / disable and Add server (/mcp), model picker, current file and selection as context, `/` command and `@` file completion, images attached by button, paste (Ctrl+V) or drag and drop (other files become @ mentions), subagent steps nested under their Agent call, clickable links (web links open the browser, file references like `src/App.cs:42` open the editor at that line), stop (Esc), past conversations of the folder (resume) |
| Layout | Drag a tool window tab onto the Dock Left / Right / Bottom guides (or use its context menu) to move it; empty groups collapse, the layout is remembered; Tools > Reset Window Layout |
| Session | Restores open documents, startup project, profile and configuration per solution |

Not yet: conditional breakpoints, exception settings, Immediate window, debugging Blazor WebAssembly code
(it runs in the browser), Fix All for quick actions, floating tool windows, history/blame, merge conflict editor,
WinForms/WPF designers (Windows-only).

## Keyboard shortcuts

Help → Keyboard Shortcuts lists them; they follow Visual Studio's defaults (F12, Shift+F12,
Ctrl+Shift+B, F5/F9/F10/F11, Ctrl+F5, Ctrl+., Ctrl+K Ctrl+D, Ctrl+K Ctrl+C/U, F2, Ctrl+Space, Ctrl+Shift+Space).

## Layout

```
src/VSL/
  Lsp/         JSON-RPC, Roslyn client, client-side file watching, HTML service for Razor cohosting
  Workspace/   solution/project loading and the Solution Explorer tree
  Build/       dotnet process runner and MSBuild output parser
  Debugging/   DAP client, netcoredbg installer, debug session, breakpoint store
  Testing/     vstest.console host (Microsoft.TestPlatform.TranslationLayer)
  Terminal/    integrated terminal session (Exclr8.Terminal control + Porta.Pty)
  Git/         git CLI wrapper (porcelain v2 status, stage/commit/sync/branches) and line diff for the margin
  Packages/    NuGet search/list/install/uninstall through the dotnet CLI
  Claude/      Claude Code CLI session (stream-json, permission prompts), transcripts, inline diff
  Templates/   SDK project templates (template engine cache + ide.host.json) and dotnet new project creation
  Editor/      AvaloniaEdit host: sync, completion, hover, squiggles, TextMate theme, side-by-side diff view
  ViewModels/  main window state, documents, Error List, Output
  Views/       main window (XAML), tool window docking, NuGet manager, icons, dialogs
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
Avalonia, AvaloniaEdit, TextMateSharp, Exclr8.Terminal and Porta.Pty (MIT).
Solution Explorer's file and folder icons are the [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme)
(MIT, Copyright (c) 2025 Material Extensions; `src/VSL/Assets/MaterialIcons`, refreshed with `scripts/update-material-icons.sh`).
