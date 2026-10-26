using System.Runtime.CompilerServices;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 转写日志开关。
///
/// 上游默认不落转写文本（隐私优先），只有显式开启才记录，并在开启时打印告警横幅。
/// </summary>
public static class TranscriptLog
{
    private static readonly object Gate = new();
    private static bool _logTranscripts;

    public static bool Enabled
    {
        get { lock (Gate) { return _logTranscripts; } }
    }

    public static void SetLogTranscripts(bool enabled)
    {
        lock (Gate)
        {
            _logTranscripts = enabled;
        }
    }

    public static void WarnIfLogTranscriptsEnabled()
    {
        if (Enabled)
        {
            Console.Error.WriteLine(
                "warning: transcript logging is enabled — user speech and assistant replies will be written to the log");
        }
    }

    /// <summary>
    /// 记录一次异常，不向调用方抛出。
    ///
    /// 上游用同一入口把管线里的异常写进日志：处理线程绝不能因为一次失败而退出。
    /// </summary>
    public static void Error(string message, Exception exception, [CallerMemberName] string? caller = null)
        => Console.Error.WriteLine($"{caller}: {message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>记录转写内容；未开启时静默丢弃。</summary>
    public static void Transcript(string role, string text, [CallerMemberName] string? caller = null)
    {
        if (!Enabled)
        {
            return;
        }

        Console.Error.WriteLine($"[transcript] {caller}: {role}: {text}");
    }
}
