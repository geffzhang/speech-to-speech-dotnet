using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Core.Pipeline;

/// <summary>
/// 一次响应最终定下的四个语言字段。对应上游 <c>TTSInput</c> 的四项：
/// <c>language_code</c> / <c>selected_language</c> / <c>tts_language_code</c> /
/// <c>response_assistant_language_code</c>。
///
/// 分开四个字段而不是一个，因为它们的消费方不同：前两个是「这段文本是什么语言」，
/// 后两个是「合成器该按什么语言发音」。二者在 <c>auto</c> 模式下会分叉——
/// 文本是中文，但合成器可能还要沿用上一轮语言。
/// </summary>
public readonly record struct TtsLanguageResolution(
    string? LanguageCode,
    string? SelectedLanguage,
    string? TtsLanguageCode,
    string? ResponseAssistantLanguageCode);

/// <summary>
/// 响应级语言解析。上游 <c>LMOutputProcessor</c> 里 <c>_response_language_resolved</c> 那段分支的等价物。
///
/// 关键在「一轮只解析一次」：同一响应内先出现的文本决定 TTS 语言，后续文本不再改。
/// 否则流式输出里前后句子语言一有波动，同一段回复就会换音色。
///
/// 为什么不放进 <see cref="TtsInput"/> 的计算属性：那段逻辑要跨分片保持状态，
/// 而 <see cref="TtsInput"/> 是逐片的不可变消息。状态放在这里，消息保持纯值。
/// </summary>
public sealed class ResponseLanguageResolver
{
    private bool _resolved;
    private string? _selected;
    private string? _ttsLanguage;

    /// <summary>本轮已定下的选择语言（<c>null</c> 表示不指定）。</summary>
    public string? Selected => _selected;

    /// <summary>本轮已定下的 TTS 语言。</summary>
    public string? TtsLanguage => _ttsLanguage;

    /// <summary>
    /// 解析一片文本对应的语言四元组。
    /// </summary>
    /// <param name="chunkLanguageCode">模型/STT 给出的语言。</param>
    /// <param name="chunkSelectedLanguage">本片携带的语言选择（null 视为未指定）。</param>
    /// <param name="configSelectedLanguage">会话配置里的语言选择。</param>
    /// <param name="configLastAssistantLanguage">上一轮助手的语言，仅在选择为 auto/未指定时兜底。</param>
    /// <param name="detected">本轮观察到的语言（未检测或未判出时为 null）。</param>
    /// <param name="detectEnabled">是否启用助手语言检测。</param>
    public TtsLanguageResolution Resolve(
        string? chunkLanguageCode,
        string? chunkSelectedLanguage,
        string? configSelectedLanguage,
        string? configLastAssistantLanguage,
        string? detected,
        bool detectEnabled)
    {
        // 未开始检测时，语言就是上游传下来的那个；一旦启用检测，就以本轮观察结果为准。
        var languageCode = detectEnabled ? detected : chunkLanguageCode;

        if (!_resolved)
        {
            _selected = chunkSelectedLanguage ?? configSelectedLanguage;
            var selected = _selected;

            if (string.Equals(selected, "auto", StringComparison.Ordinal)
                || (selected is null && detectEnabled))
            {
                // auto / 未指定：用本轮判出的语言，判不出就沿用上一轮助手语言。
                // 这条兜底是「连续同一语言对话」不换音色的原因。
                _ttsLanguage = detected ?? configLastAssistantLanguage;
            }
            else if (selected is not null)
            {
                // 显式选择优先，但本轮判出的语言更可信时以判出为准（检测关闭则不参与）。
                _ttsLanguage = (detectEnabled ? detected : null) ?? selected;
            }

            _resolved = true;
        }

        var currentSelected = _selected;

        return new TtsLanguageResolution(
            languageCode,
            currentSelected,
            // 有选择时用本轮定下的 TTS 语言；没有选择时合成语言跟随文本语言。
            currentSelected is not null ? _ttsLanguage : languageCode,
            // 只在「没有选择且启用了检测」时上报助手语言——否则这个字段没有信息量。
            currentSelected is null && detectEnabled ? _ttsLanguage : null);
    }

    /// <summary>新一轮响应：重新开始解析。上游 <c>_reset_response</c> 同规则。</summary>
    public void Reset()
    {
        _resolved = false;
        _selected = null;
        _ttsLanguage = null;
    }
}
