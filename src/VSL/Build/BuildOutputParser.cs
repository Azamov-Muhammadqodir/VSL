using System.Text.RegularExpressions;
using VSL.Lsp;

namespace VSL.Build;

public sealed record BuildDiagnostic(DiagnosticSeverity Severity, string Code, string Message, string? File, int Line, int Column, string? Project);

/// <summary>Parses MSBuild's canonical "file(line,col): error CODE: message [project]" console format.</summary>
public static partial class BuildOutputParser
{
    [GeneratedRegex(@"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\[\]]+)\])?\s*$")]
    private static partial Regex WithLocation();

    [GeneratedRegex(@"^\s*(?<file>.+?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\[\]]+)\])?\s*$")]
    private static partial Regex WithoutLocation();

    public static BuildDiagnostic? Parse(string line)
    {
        var m = WithLocation().Match(line);
        var hasLocation = m.Success;
        if (!hasLocation)
            m = WithoutLocation().Match(line);
        if (!m.Success)
            return null;

        var file = m.Groups["file"].Value.Trim();
        if (file is "MSBUILD" or "CSC" or "EXEC")
            file = "";
        var project = m.Groups["proj"].Success ? m.Groups["proj"].Value : null;
        return new BuildDiagnostic(
            m.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            m.Groups["code"].Value,
            m.Groups["msg"].Value,
            file.Length == 0 ? null : file,
            hasLocation ? int.Parse(m.Groups["line"].Value) : 0,
            hasLocation ? int.Parse(m.Groups["col"].Value) : 0,
            project);
    }
}
