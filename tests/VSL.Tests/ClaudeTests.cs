using System.Collections.Concurrent;
using System.Text.Json;
using VSL.Claude;
using VSL.ViewModels;
using VSL.Views;

namespace VSL.Tests;

/// <summary>
/// Points VSL at the fake Claude Code CLI (TestAssets/FakeClaude/claude) and a throwaway config folder, so no test
/// ever starts the real CLI or reads the user's ~/.claude.
/// </summary>
public static class FakeClaude
{
    static FakeClaude()
    {
        Executable = Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeClaude", "claude");
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        ConfigDirectory = Path.Combine(Path.GetTempPath(), "vsl-claude-config-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("VSL_CLAUDE_PATH", Executable);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", ConfigDirectory);
    }

    public static string Executable { get; }

    public static string ConfigDirectory { get; }

    public static void Ensure() => _ = Executable;

    /// <summary>A folder with a Program.cs for the fake to work in, and the log of what it received.</summary>
    public static (string Dir, string Log) Workspace()
    {
        Ensure();
        var dir = Path.Combine(Path.GetTempPath(), "vsl-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "Console.WriteLine(\"Hello\");\n");
        return (dir, dir + ".fake-claude.log");
    }

    /// <summary>Entries the fake logged: ("argv" | "in" | "decision", data).</summary>
    public static List<(string Kind, JsonElement Data)> Log(string log) =>
        File.Exists(log)
            ? File.ReadAllLines(log).Select(l => JsonDocument.Parse(l).RootElement).Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("data"))).ToList()
            : [];

    public static JsonElement LastDecision(string log) => Log(log).Last(e => e.Kind == "decision").Data;

    /// <summary>A saved transcript in the CLI's format, as Claude Code writes them under ~/.claude/projects.</summary>
    public static string WriteTranscript(string workingDirectory, string sessionId)
    {
        var dir = ClaudeCli.ProjectDirectory(workingDirectory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sessionId + ".jsonl");
        string[] lines =
        [
            """{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Fix the build"},{"type":"text","text":"<ide_context>\nThe user has /x/Program.cs open in the editor.\n</ide_context>"}]}}""",
            """{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"text","text":"Let me check Program.cs."}]}}""",
            """{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/x/Program.cs"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"1\tConsole.WriteLine();"}]}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"a subagent prompt"}}""",
            """{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"text","text":"Fixed."}]}}""",
        ];
        File.WriteAllLines(path, lines);
        return path;
    }
}

public class ClaudeProtocolTests
{
    static List<ClaudeEvent> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeProtocol.Parse(doc.RootElement).ToList();
    }

    [Fact]
    public void Parses_what_the_cli_streams()
    {
        var init = Assert.IsType<ClaudeInit>(Assert.Single(Parse(
            """{"type":"system","subtype":"init","cwd":"/w","session_id":"abc","model":"claude-opus-5-5","permissionMode":"default"}""")));
        Assert.Equal(("abc", "claude-opus-5-5", "default"), (init.SessionId, init.Model, init.PermissionMode));

        var delta = Assert.IsType<ClaudeBlockDelta>(Assert.Single(Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hi"}},"parent_tool_use_id":null,"api_message_id":"msg_1"}""")));
        Assert.Equal(("msg_1", 1, "text", "Hi"), (delta.MessageId, delta.Index, delta.Kind, delta.Text));
        // Subagent streams are not shown inline.
        Assert.Empty(Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}},"parent_tool_use_id":"toolu_1"}"""));

        var assistant = Assert.IsType<ClaudeAssistant>(Assert.Single(Parse(
            """{"type":"assistant","message":{"id":"msg_2","role":"assistant","content":[{"type":"tool_use","id":"toolu_9","name":"Write","input":{"file_path":"/w/a.txt","content":"hi\n"}}]},"parent_tool_use_id":null}""")));
        var tool = Assert.IsType<ClaudeToolUseBlock>(Assert.Single(assistant.Blocks));
        Assert.Equal(("toolu_9", "Write", "/w/a.txt"), (tool.Id, tool.Name, tool.Input.GetProperty("file_path").GetString()));

        // Captured from Claude Code 2.1.300 with --permission-prompt-tool stdio.
        var request = Assert.IsType<ClaudePermissionRequest>(Assert.Single(Parse(
            """{"type":"control_request","request_id":"3b0e4f6c","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"/w/hello.txt","content":"hi\n"},"description":"hello.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_01"}}""")));
        Assert.Equal(("3b0e4f6c", "Write", "toolu_01"), (request.RequestId, request.ToolName, request.ToolUseId));
        Assert.Equal("setMode", Assert.Single(request.Suggestions).GetProperty("type").GetString());

        var results = Assert.IsType<ClaudeToolResults>(Assert.Single(Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"a","content":"done"},{"type":"tool_result","tool_use_id":"b","content":[{"type":"text","text":"x"},{"type":"text","text":"y"}],"is_error":true}]},"parent_tool_use_id":null}""")));
        Assert.Equal([new ClaudeToolResult("a", "done", false), new ClaudeToolResult("b", "x\ny", true)], results.Results);

        var result = Assert.IsType<ClaudeResult>(Assert.Single(Parse(
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":4057,"total_cost_usd":0.0084,"result":"Done.","session_id":"abc"}""")));
        Assert.Equal(("success", 4057L, "Done."), (result.Subtype, result.DurationMs, result.Text));

        Assert.True(Assert.IsType<ClaudeNotice>(Assert.Single(Parse(
            """{"type":"system","subtype":"permission_denied","tool_name":"Write","message":"Claude requested permissions to write to /w/a.txt"}"""))).IsError);
    }

    [Fact]
    public void Starts_the_cli_like_the_agent_sdk_does()
    {
        var args = ClaudeSession.Arguments(new ClaudeStartOptions("claude", "/w", "opus", "plan", "abc"));
        Assert.Equal(
            ["--output-format", "stream-json", "--verbose", "--input-format", "stream-json", "--include-partial-messages",
             "--permission-prompt-tool", "stdio", "--permission-mode", "plan", "--model", "opus", "--resume", "abc"], args);
        var defaults = ClaudeSession.Arguments(new ClaudeStartOptions("claude", "/w", "default", "default"));
        Assert.DoesNotContain("--model", defaults);
        Assert.DoesNotContain("--permission-mode", defaults);
    }

    [Fact]
    public void Permission_decisions_have_the_sdk_shape()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        using var input = JsonDocument.Parse("""{"command":"ls"}""");
        Assert.Equal("""{"behavior":"allow","updatedInput":{"command":"ls"}}""",
            JsonSerializer.Serialize(ClaudePermissionDecision.AllowOnce(input.RootElement).ToResponse(), options));
        Assert.Equal("""{"behavior":"deny","message":"no","interrupt":true}""",
            JsonSerializer.Serialize(new ClaudePermissionDecision(false, Message: "no", Interrupt: true).ToResponse(), options));
    }

    [Fact]
    public void Inline_diff_shows_changed_lines_with_context()
    {
        var diff = UnifiedDiff.Compute("a\nb\nc\nd\ne\nf\ng\nh\n", "a\nB\nc\nd\ne\nf\ng\nh\ni\n", context: 1);
        Assert.Equal(
            [" a", "-b", "+B", " c", " ... 4 unchanged lines", " h", "+i"],
            diff.Select(d => (d.IsGap ? " " : d.Prefix) + d.Text));
        Assert.All(UnifiedDiff.Added("x\ny"), d => Assert.True(d.IsAdded));
    }

    [Fact]
    public void Edit_previews_and_permission_wording()
    {
        using var edit = JsonDocument.Parse("""{"old_string":"a","new_string":"b","replace_all":true}""");
        Assert.Equal("b b b", ClaudeViewModel.ApplyEdits("a a a", edit.RootElement, "Edit"));
        using var multi = JsonDocument.Parse("""{"edits":[{"old_string":"one","new_string":"1"},{"old_string":"two","new_string":"2"}]}""");
        Assert.Equal("1 2 one", ClaudeViewModel.ApplyEdits("one two one", multi.RootElement, "MultiEdit"));

        using var suggestions = JsonDocument.Parse("""
            [[{"type":"setMode","mode":"acceptEdits","destination":"session"}],
             [{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"}],
             [{"type":"addDirectories","directories":["/data"],"destination":"session"}]]
            """);
        Assert.Equal(
            ["Yes, allow all edits during this session", "Yes, and don't ask again for Bash(npm test:*) in this project", "Yes, and allow access to /data"],
            suggestions.RootElement.EnumerateArray().Select(s => ClaudeViewModel.SessionLabel(s.EnumerateArray().ToList())));
        Assert.Null(ClaudeViewModel.SessionLabel([]));
    }

    [Fact]
    public void Context_block_carries_the_selection()
    {
        Assert.Equal("<ide_context>\nThe user has /w/A.cs open in the editor and selected lines 3-4:\n```\nint x;\nint y;\n```\n</ide_context>",
            ClaudeViewModel.ContextBlock(new ClaudeEditorContext("/w/A.cs", 3, 4, "int x;\nint y;\n")));
        Assert.Equal("<ide_context>\nThe user has /w/A.cs open in the editor.\n</ide_context>",
            ClaudeViewModel.ContextBlock(new ClaudeEditorContext("/w/A.cs", null, null, null)));
        Assert.Equal("A.cs:3-4", new ClaudeEditorContext("/w/A.cs", 3, 4, "x").Label);
    }

    [Fact]
    public void Markdown_blocks()
    {
        var blocks = MarkdownParser.Parse("# Title\n\nSome *text*\nwrapped.\n\n- one\n  - nested\n- [x] done\n\n2. two\n3. three\n\n> quote\n\n```cs\nvar x = 1;\n```\n\n| A | B |\n|---|:-:|\n| 1 | 2 |\n\n---\n\n```\nunterminated");
        Assert.Equal(new MdHeading(1, "Title"), blocks[0]);
        Assert.Equal(new MdParagraph("Some *text* wrapped."), blocks[1]);
        var list = Assert.IsType<MdList>(blocks[2]);
        Assert.Equal([new MdListItem(0, "one", null), new MdListItem(1, "nested", null), new MdListItem(0, "done", true)], list.Items);
        var ordered = Assert.IsType<MdList>(blocks[3]);
        Assert.True(ordered.Ordered);
        Assert.Equal(2, ordered.Start);
        Assert.Equal(new MdQuote("quote"), blocks[4]);
        Assert.Equal(new MdCode("cs", "var x = 1;"), blocks[5]);
        Assert.Equal([["A", "B"], ["1", "2"]], Assert.IsType<MdTable>(blocks[6]).Rows);
        Assert.IsType<MdRule>(blocks[7]);
        Assert.Equal(new MdCode("", "unterminated"), blocks[8]);
    }

    [Fact]
    public void Finds_transcripts_the_way_claude_code_names_them()
    {
        FakeClaude.Ensure();
        Assert.Equal("-tmp-claude-1000--home-nbk-x-y", Path.GetFileName(ClaudeCli.ProjectDirectory("/tmp/claude-1000/-home-nbk/x.y")));
        Assert.Equal(Path.Combine(FakeClaude.ConfigDirectory, "projects"), Path.GetDirectoryName(ClaudeCli.ProjectDirectory("/a")));

        var (dir, _) = FakeClaude.Workspace();
        FakeClaude.WriteTranscript(dir, "session-1");
        var conversation = Assert.Single(ClaudeHistory.List(dir));
        Assert.Equal(("session-1", "Fix the build"), (conversation.SessionId, conversation.Title));

        var events = ClaudeHistory.Load(conversation.FilePath);
        Assert.Equal(5, events.Count);
        Assert.StartsWith("Fix the build", Assert.IsType<ClaudeUserText>(events[0]).Text);
        Assert.Equal("Read", Assert.IsType<ClaudeToolUseBlock>(Assert.IsType<ClaudeAssistant>(events[2]).Blocks[0]).Name);
        Assert.IsType<ClaudeToolResults>(events[3]);
    }
}

public class ClaudeSessionTests
{
    [Fact]
    public async Task Runs_a_turn_with_a_permission_prompt()
    {
        var (dir, log) = FakeClaude.Workspace();
        await using var session = await ClaudeSession.StartAsync(new ClaudeStartOptions(FakeClaude.Executable, dir), TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Initialization.GetProperty("commands").GetArrayLength());

        var events = new ConcurrentQueue<ClaudeEvent>();
        session.Event += events.Enqueue;
        await session.SendUserMessageAsync("write a file");
        await Eventually(() => events.OfType<ClaudePermissionRequest>().Any());
        var request = events.OfType<ClaudePermissionRequest>().Single();
        Assert.Equal("Write", request.ToolName);
        Assert.False(File.Exists(Path.Combine(dir, "hello.txt")));

        await session.RespondToPermissionAsync(request.RequestId, ClaudePermissionDecision.AllowOnce(request.Input));
        await Eventually(() => events.OfType<ClaudeResult>().Any());
        Assert.Equal("hi\nthere\n", File.ReadAllText(Path.Combine(dir, "hello.txt")));
        Assert.Equal("fake-session-1", session.SessionId);
        Assert.Equal("allow", FakeClaude.LastDecision(log).GetProperty("behavior").GetString());

        await session.SetPermissionModeAsync("acceptEdits");
        Assert.Contains(FakeClaude.Log(log), e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "control_request"
            && e.Data.GetProperty("request").GetProperty("subtype").GetString() == "set_permission_mode");
    }

    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
