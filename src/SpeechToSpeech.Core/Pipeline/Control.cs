namespace SpeechToSpeech.Core.Pipeline;

/// <summary>控制消息类型。</summary>
public enum ControlKind
{
    /// <summary>会话结束：重置每会话状态但不终止 handler 线程。</summary>
    SessionEnd,
}

/// <summary>
/// 管线控制消息。
///
/// <paramref name="SessionId"/> 记录入队方：池化的 realtime 发送循环可据此忽略来自
/// 被强制释放会话的 <see cref="ControlKind.SessionEnd"/>，避免它满足后来认领该单元的会话的 drain 等待。
/// </summary>
public sealed record PipelineControlMessage(ControlKind Kind, string? SessionId = null);

/// <summary>控制消息辅助。</summary>
public static class PipelineControl
{
    /// <summary>对应上游模块级常量 <c>SESSION_END</c>。</summary>
    public static readonly PipelineControlMessage SessionEnd = new(ControlKind.SessionEnd);

    public static bool IsControlMessage(object? message, ControlKind? kind = null)
        => message is PipelineControlMessage control && (kind is null || control.Kind == kind);
}
