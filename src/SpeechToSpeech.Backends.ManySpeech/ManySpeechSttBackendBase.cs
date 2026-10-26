using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;

namespace SpeechToSpeech.Backends.ManySpeech;

/// <summary>
/// 基于 ManySpeech 的离线 STT 后端基类。
///
/// 为什么用离线识别而不是流式：VAD 已经把语音切成了完整句段，整段送进去既省一次
/// 状态机维护，又能拿到更好的准确率。真正的增量转写需要子句级切分 + 流式解码器，
/// 属于第二阶段（见 <c>porting/03-acceptance.md</c> 未闭合项）。
/// </summary>
public abstract class ManySpeechSttBackendBase : ISttBackend
{
    private readonly Lock _lock = new();

    public abstract string Name { get; }

    /// <summary>加载模型；由子类在构造期调用。</summary>
    protected abstract void Load();

    /// <summary>对一整段 16 kHz 单声道音频识别，返回文本与语言。</summary>
    protected abstract (string Text, string? Language) Recognize(float[] samples);

    protected bool IsLoaded { get; private set; }

    protected void EnsureLoaded()
    {
        if (IsLoaded)
        {
            return;
        }

        Load();
        IsLoaded = true;
    }

    public IEnumerable<PipelineMessage> Transcribe(VadAudio audio, CancellationToken cancellationToken)
    {
        EnsureLoaded();

        (string Text, string? Language) result;
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = Recognize(audio.Audio);
        }

        var text = (result.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            // 空转写不能产出一个空用户消息——那会让 LLM 对着空气回答
            yield return new TranscriptionFailure
            {
                Message = "no speech recognized",
                TurnId = audio.TurnId,
                TurnRevision = audio.TurnRevision,
                SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
            };
            yield break;
        }

        yield return new Transcription
        {
            Text = text,
            LanguageCode = result.Language,
            TurnId = audio.TurnId,
            TurnRevision = audio.TurnRevision,
            SpeechStoppedAtSeconds = audio.SpeechEndAtSeconds,
        };
    }

    public abstract void Dispose();
}
