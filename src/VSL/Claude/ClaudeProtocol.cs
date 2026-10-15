using System.Text;
using System.Text.Json;

namespace VSL.Claude;

/// <summary>Something the CLI reported on its stream-json output (or a line of a saved transcript).</summary>
public abstract record ClaudeEvent;

/// <summary>system/init: the session started (or resumed).</summary>
public sealed record ClaudeInit(string SessionId, string? Model, string? PermissionMode, string? Cwd) : ClaudeEvent;

/// <summary>A content block started streaming (text, thinking or tool_use).</summary>
public sealed record ClaudeBlockStart(string MessageId, int Index, string BlockType) : ClaudeEvent;

/// <summary>Streamed text or thinking for an open block.</summary>
public sealed record ClaudeBlockDelta(string MessageId, int Index, string Kind, string Text) : ClaudeEvent;

/// <summary>Running output-token count of the current response, for the status line.</summary>
public sealed record ClaudeTokens(int OutputTokens) : ClaudeEvent;

/// <summary>A complete assistant message (the CLI sends one per content block).</summary>
public sealed record ClaudeAssistant(string MessageId, IReadOnlyList<ClaudeBlock> Blocks, string? ParentToolUseId) : ClaudeEvent;

/// <summary>Results of tool calls, as the user turn that follows them.</summary>
public sealed record ClaudeToolResults(IReadOnlyList<ClaudeToolResult> Results, string? ParentToolUseId) : ClaudeEvent;

/// <summary>A prompt the user typed (only in saved transcripts; live prompts are shown when sent).</summary>
public sealed record ClaudeUserText(string Text) : ClaudeEvent;

/// <summary>control_request can_use_tool: Claude needs approval (or answers, for AskUserQuestion / ExitPlanMode).</summary>
public sealed record ClaudePermissionRequest(
    string RequestId,
    string ToolName,
    string? DisplayName,
    JsonElement Input,
    string? Description,
    IReadOnlyList<JsonElement> Suggestions,
    string? ToolUseId) : ClaudeEvent;

/// <summary>The end of a turn.</summary>
public sealed record ClaudeResult(string Subtype, bool IsError, long DurationMs, double? CostUsd, string? Text, string? SessionId) : ClaudeEvent;

/// <summary>A system message worth showing (permission denied, compaction, API retry...).</summary>
public sealed record ClaudeNotice(string Text, bool IsError) : ClaudeEvent;

/// <summary>The CLI process ended.</summary>
public sealed record ClaudeExited(int ExitCode, string Stderr) : ClaudeEvent;

public abstract record ClaudeBlock;

public sealed record ClaudeTextBlock(string Text) : ClaudeBlock;

public sealed record ClaudeThinkingBlock(string Text) : ClaudeBlock;

public sealed record ClaudeToolUseBlock(string Id, string Name, JsonElement Input) : ClaudeBlock;

public sealed record ClaudeToolResult(string ToolUseId, string Content, bool IsError);

/// <summary>Turns stream-json lines into <see cref="ClaudeEvent"/>s.</summary>
public static class ClaudeProtocol
{
    public static IEnumerable<ClaudeEvent> Parse(JsonElement root)
    {
        switch (Str(root, "type"))
        {
            case "system":
                if (Str(root, "subtype") == "init")
                    yield return new ClaudeInit(Str(root, "session_id") ?? "", Str(root, "model"), Str(root, "permissionMode"), Str(root, "cwd"));
                else if (Str(root, "subtype") is "permission_denied" && Str(root, "message") is { } denied)
                    yield return new ClaudeNotice(denied, IsError: true);
                else if (Str(root, "subtype") is "compact_boundary")
                    yield return new ClaudeNotice("Conversation compacted", IsError: false);
                else if (Str(root, "subtype") is "thinking_tokens" && root.TryGetProperty("estimated_tokens", out var est) && est.TryGetInt32(out var thinking))
                    yield return new ClaudeTokens(thinking);
                break;

            case "stream_event":
                if (!root.TryGetProperty("event", out var ev) || root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
                    break;
                var messageId = Str(root, "api_message_id") ?? "";
                var index = ev.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
                switch (Str(ev, "type"))
                {
                    case "content_block_start" when ev.TryGetProperty("content_block", out var block):
                        yield return new ClaudeBlockStart(messageId, index, Str(block, "type") ?? "");
                        break;
                    case "content_block_delta" when ev.TryGetProperty("delta", out var delta):
                        if (Str(delta, "type") == "text_delta")
                            yield return new ClaudeBlockDelta(messageId, index, "text", Str(delta, "text") ?? "");
                        else if (Str(delta, "type") == "thinking_delta")
                            yield return new ClaudeBlockDelta(messageId, index, "thinking", Str(delta, "thinking") ?? "");
                        break;
                    case "message_delta" when ev.TryGetProperty("usage", out var usage) && usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var tokens):
                        yield return new ClaudeTokens(tokens);
                        break;
                }
                break;

            case "assistant" when root.TryGetProperty("message", out var message):
                var blocks = new List<ClaudeBlock>();
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in content.EnumerateArray())
                    {
                        switch (Str(b, "type"))
                        {
                            case "text":
                                blocks.Add(new ClaudeTextBlock(Str(b, "text") ?? ""));
                                break;
                            case "thinking":
                                blocks.Add(new ClaudeThinkingBlock(Str(b, "thinking") ?? ""));
                                break;
                            case "tool_use":
                                blocks.Add(new ClaudeToolUseBlock(Str(b, "id") ?? "", Str(b, "name") ?? "",
                                    b.TryGetProperty("input", out var input) ? input.Clone() : default));
                                break;
                        }
                    }
                }
                yield return new ClaudeAssistant(Str(message, "id") ?? "", blocks, ParentToolUseId(root));
                break;

            case "user" when root.TryGetProperty("message", out var user) && user.TryGetProperty("content", out var userContent):
                if (userContent.ValueKind == JsonValueKind.String)
                {
                    yield return new ClaudeUserText(userContent.GetString() ?? "");
                    break;
                }
                if (userContent.ValueKind != JsonValueKind.Array)
                    break;
                var results = new List<ClaudeToolResult>();
                var text = new StringBuilder();
                foreach (var b in userContent.EnumerateArray())
                {
                    if (Str(b, "type") == "tool_result")
                        results.Add(new ClaudeToolResult(Str(b, "tool_use_id") ?? "", ResultText(b), Bool(b, "is_error")));
                    else if (Str(b, "type") == "text")
                        text.Append(text.Length > 0 ? "\n" : "").Append(Str(b, "text"));
                }
                if (results.Count > 0)
                    yield return new ClaudeToolResults(results, ParentToolUseId(root));
                else if (text.Length > 0)
                    yield return new ClaudeUserText(text.ToString());
                break;

            case "control_request" when root.TryGetProperty("request", out var request) && Str(request, "subtype") == "can_use_tool":
                var suggestions = request.TryGetProperty("permission_suggestions", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(x => x.Clone()).ToList()
                    : [];
                yield return new ClaudePermissionRequest(Str(root, "request_id") ?? "", Str(request, "tool_name") ?? "", Str(request, "display_name"),
                    request.TryGetProperty("input", out var toolInput) ? toolInput.Clone() : default, Str(request, "description"), suggestions,
                    Str(request, "tool_use_id"));
                break;

            case "result":
                var resultText = Str(root, "result");
                if (resultText is null && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    resultText = string.Join("\n", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()));
                yield return new ClaudeResult(Str(root, "subtype") ?? "", Bool(root, "is_error"),
                    root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms) ? ms : 0,
                    root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cost) ? cost : null,
                    resultText, Str(root, "session_id"));
                break;
        }
    }

    /// <summary>A tool result's text: a plain string, or the text parts of a content array.</summary>
    static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return content.GetRawText();
        return string.Join("\n", content.EnumerateArray()
            .Select(part => Str(part, "type") == "text" ? Str(part, "text") : Str(part, "type") == "image" ? "[image]" : null)
            .OfType<string>());
    }

    static string? ParentToolUseId(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
