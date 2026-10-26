namespace SpeechToSpeech.Core.Text;

/// <summary>
/// 把流式 token 增量攒成完整的句子再交出去。
///
/// 为什么必须有这一层：TTS 按句合成才能让首段音频尽早开始。若把每个 token 直接
/// 丢给 TTS，就会退化成"每个碎片一次合成"，延迟和音质双输；若等整段生成完再合成，
/// 首字延迟又会与全文长度成正比。
///
/// 规则：只有落在句末标点之后的文本才被切出，末尾残句留着等后续 token 补齐。
/// 未闭合的 Markdown 围栏代码块整段留着——此时没有可靠的边界。
/// </summary>
public sealed class SentenceAccumulator
{
    private readonly System.Text.StringBuilder _buffer = new();

    /// <summary>喂入一个增量，返回本次可以安全切出的完整句子。</summary>
    public IReadOnlyList<string> Push(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        _buffer.Append(delta);
        return Drain(flush: false);
    }

    /// <summary>生成结束：把残留的一段也交出去。</summary>
    public IReadOnlyList<string> Flush() => Drain(flush: true);

    /// <summary>当前尚未切出的残留文本。</summary>
    public string Pending => _buffer.ToString();

    private IReadOnlyList<string> Drain(bool flush)
    {
        var text = _buffer.ToString();
        if (text.Length == 0)
        {
            return [];
        }

        var sentences = TextNormalizer.SentenceTokenizePreservingMarkdownCode(text);
        if (sentences.Count == 0)
        {
            return [];
        }

        List<string> completed;
        string remainder;

        if (flush)
        {
            completed = sentences;
            remainder = string.Empty;
        }
        else
        {
            // 最后一个句子可能是半句：只有在它以句末标点结尾时才认为完整
            var last = sentences[^1];
            if (EndsWithTerminator(last))
            {
                completed = sentences;
                remainder = string.Empty;
            }
            else
            {
                completed = sentences.Take(sentences.Count - 1).ToList();
                remainder = last;
            }
        }

        _buffer.Clear();
        _buffer.Append(remainder);
        return completed;
    }

    private static bool EndsWithTerminator(string sentence)
    {
        foreach (var ch in sentence.TrimEnd().Reverse())
        {
            if (ch is '.' or '!' or '?' or '。' or '！' or '？' or '…')
            {
                return true;
            }

            if (ch is '"' or '\'' or '”' or '’' or ')' or '）' or ';' or '；')
            {
                continue;
            }

            return false;
        }

        return false;
    }
}
