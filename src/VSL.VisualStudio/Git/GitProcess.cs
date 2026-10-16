using System.Diagnostics;
using System.Text;

namespace VSL.VisualStudio.Git;

/// <summary>What a git (or claude) run printed, and whether it succeeded.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The message to show when it failed: git's own words, without its hints.</summary>
    public string Problem => string.Join("\n", (Error.Trim().Length > 0 ? Error : Output).Split('\n')
        .Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("hint:", StringComparison.Ordinal)).Take(6));
}

/// <summary>
/// Runs git as the user: their PATH git (or the one Visual Studio ships), their git config, their credentials.
/// Nothing here adds an author, a co-author or any other trailer.
/// </summary>
public static class GitProcess
{
    static string? _git;

    /// <summary>git from PATH, else Visual Studio's own copy.</summary>
    static string Git => _git ??= FindGit();

    static string FindGit()
    {
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim().Trim('"'), "git.exe"))
            .FirstOrDefault(File.Exists);
        if (onPath is not null)
            return onPath;
        var ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";
        var bundled = Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
        return File.Exists(bundled) ? bundled : "git.exe";
    }

    public static Task<ProcessResult> RunGitAsync(string directory, string arguments, string? input = null) =>
        RunAsync(Git, "-c core.quotepath=off " + arguments, directory, input, environment =>
        {
            // Never wait on a prompt nobody sees: credentials come from the credential manager's own window.
            environment["GIT_TERMINAL_PROMPT"] = "0";
            environment["GIT_EDITOR"] = "true";
            environment["GIT_MERGE_AUTOEDIT"] = "no";
        });

    public static Task<ProcessResult> RunAsync(string file, string arguments, string directory, string? input, Action<IDictionary<string, string?>>? environment = null) =>
        Task.Run(async () =>
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            environment?.Invoke(psi.Environment);
            try
            {
                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                // Input as UTF-8 without a byte order mark (a commit message, a prompt).
                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    if (input is not null)
                        await stdin.WriteAsync(input);
                }
                var text = await output;
                var problems = await error;
                process.WaitForExit();
                return new ProcessResult(process.ExitCode, text, problems);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new ProcessResult(-1, "", $"Could not start {Path.GetFileName(file)}: {ex.Message}");
            }
        });
}
