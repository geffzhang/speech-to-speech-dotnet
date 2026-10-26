using SpeechToSpeech.Core.Runtime;
namespace SpeechToSpeech.Core.Pipeline;

/// <summary>一个口型形状及其触发时刻。</summary>
public sealed record Viseme(string Value, double OffsetSeconds, double DurationSeconds);

/// <summary>说话人归属。</summary>
public sealed record SpeakerAttribution(string SpeakerId, string? Label = null);

/// <summary>待定的说话人归属（流式说话人分离在转写完成前尚不确定）。</summary>
public sealed class PendingSpeakerAttribution
{
    public required string Placeholder { get; init; }
    public string? ResolvedSpeakerId { get; set; }
    public bool IsResolved => ResolvedSpeakerId is not null;
}

/// <summary>
/// 一次助手函数调用（等价 OpenAI 的 <c>ResponseFunctionToolCall</c>）。
/// </summary>
public sealed class ToolCall
{
    public required string CallId { get; init; }
    public required string Name { get; init; }

    /// <summary>JSON 字符串形式的参数。</summary>
    public string Arguments { get; init; } = "{}";

    public string Type => "function_call";
}

/// <summary>
/// 响应历史句柄。
///
/// 上游用它把"历史提交"与"响应完成"解耦：LLM 产出 <see cref="EndOfResponse"/> 后，
/// 由服务决定何时 <see cref="Resolve"/>，从而让逻辑完成绕开慢速 TTS。
/// </summary>
public sealed class ResponseHistory
{
    private readonly ManualResetEventSlim _resolved = new(false);
    private volatile bool _isResolved;

    public ResponseHistory(ChatSession chat, string? turnId = null, int? turnRevision = null)
    {
        Chat = chat;
        TurnId = turnId;
        TurnRevision = turnRevision;
    }

    public ChatSession Chat { get; }

    public string? TurnId { get; }

    public int? TurnRevision { get; }

    public bool IsResolved => _isResolved;

    /// <summary>等待服务决定该历史，超时返回 false。</summary>
    public bool WaitUntilResolved(TimeSpan timeout)
    {
        if (_isResolved)
        {
            return true;
        }

        return _resolved.Wait(timeout);
    }

    public void Resolve()
    {
        if (_isResolved)
        {
            return;
        }

        _isResolved = true;
        _resolved.Set();
    }
}
