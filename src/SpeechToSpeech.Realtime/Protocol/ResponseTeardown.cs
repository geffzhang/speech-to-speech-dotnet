namespace SpeechToSpeech.Realtime.Protocol;

/// <summary>一次响应收尾时可上线的事件种类。枚举顺序即渲染契约顺序。</summary>
public enum TeardownEvent
{
    OutputAudioDone,
    OutputAudioTranscriptDone,
    OutputTextDone,
    ContentPartDone,
    OutputItemDone,
}

/// <summary>
/// 一次响应收尾要发哪些事件、按什么顺序发。
///
/// 上游 <c>finish_response</c> 把两件事分开判断，这里照搬同样两个独立条件：
/// <list type="bullet">
/// <item>消息项开过（有文本片或音频片）→ 关 <c>content_part</c> 与 <c>item</c>；</item>
/// <item>真的发过音频增量 → 才关 <c>output_audio</c>。</item>
/// </list>
///
/// 把两者合并成一个「产出过内容」的判断会同时出两种错：只出音频不出文本的响应，
/// 音频流永不关闭（客户端一直等下一段，回合结束不下来）；纯工具调用的响应则反过来
/// 凭空多一个空音频流（客户端以为收到一段静音）。所以这条判断独立成纯函数，
/// 由单元测试锁定，不让它埋在长方法里被顺手改掉。
/// </summary>
public static class ResponseTeardown
{
    /// <summary>规划收尾事件序列。空列表表示只需 <c>response.done</c>。</summary>
    /// <param name="itemOpened">本次响应是否已发过 <c>output_item.added</c>。</param>
    /// <param name="wantsAudio">本次响应是否出音频（由 <c>output_modalities</c> 决定）。</param>
    /// <param name="audioStreamed">是否真的发过至少一个 <c>output_audio.delta</c>。</param>
    /// <param name="hasText">是否有非空助手文本。</param>
    public static IReadOnlyList<TeardownEvent> Plan(
        bool itemOpened,
        bool wantsAudio,
        bool audioStreamed,
        bool hasText)
    {
        if (!itemOpened)
        {
            // 纯工具调用或空响应：没有消息项可关，直接接 response.done
            return [];
        }

        var plan = new List<TeardownEvent>(4);

        if (wantsAudio)
        {
            if (audioStreamed)
            {
                plan.Add(TeardownEvent.OutputAudioDone);
            }

            // 没文本时不能发空的 transcript.done——那会让客户端渲染一条空转写
            if (hasText)
            {
                plan.Add(TeardownEvent.OutputAudioTranscriptDone);
            }
        }
        else
        {
            plan.Add(TeardownEvent.OutputTextDone);
        }

        plan.Add(TeardownEvent.ContentPartDone);
        plan.Add(TeardownEvent.OutputItemDone);
        return plan;
    }
}
