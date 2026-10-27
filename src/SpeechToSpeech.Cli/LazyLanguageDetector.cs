using SpeechToSpeech.Backends.Lingua;
using SpeechToSpeech.Core.Text;

namespace SpeechToSpeech.Cli;

/// <summary>
/// 按需构造 lingua 检测器。
///
/// 为什么懒构造：<c>LinguaDetectorBuilder(...).WithPreloadedLanguageModels()</c> 会把全部
/// 语言模型读进内存（几十 MB 起步）。只在真的要用检测时才付这笔钱，
/// 否则每个不关心语言的启动都白等一次。
///
/// 但「懒」不等于「可选」：只要配置存在，上游在关掉 <c>--detect_llm_output_language</c> 时
/// 仍会观察并把结果写回 <c>last_assistant_language</c>。所以这里始终提供一个可用的检测器，
/// 由调用方决定要不要拿结果去选本轮语言。
/// </summary>
internal sealed class LazyLanguageDetector : ILanguageDetector
{
    private readonly Lazy<ILanguageDetector> _inner;

    /// <param name="preload">
    /// 打开时立刻加载语言模型——对应上游在 handler 初始化时
    /// <c>warm_language_detector()</c>，把延迟挪出第一句回复。
    /// </param>
    public LazyLanguageDetector(bool preload)
        => _inner = new Lazy<ILanguageDetector>(() => new LinguaLanguageDetector(preload));

    public IReadOnlyList<LanguageCandidate> Rank(string text) => _inner.Value.Rank(text);
}
