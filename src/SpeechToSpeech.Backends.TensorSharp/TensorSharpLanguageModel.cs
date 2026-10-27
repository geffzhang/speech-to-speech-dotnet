using SpeechToSpeech.Core.Backends;
using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;
using SpeechToSpeech.Core.Text;
using TensorSharp.Server;
using TSChatMessage = TensorSharp.Runtime.ChatMessage;

namespace SpeechToSpeech.Backends.TensorSharp;

/// <summary>
/// 本地 GGUF 推理后端（TensorSharp）。
///
/// 对应上游的 <c>--llm_backend transformers</c>：两边都是"在本机把模型跑起来"，
/// 只是运行时不同（Python/Transformers ↔ .NET/GGUF）。
///
/// 两点必须说清的取舍：
/// <list type="number">
/// <item><b>单并发。</b>TensorSharp 的 <see cref="ModelService"/> 是内在单会话的，
///       文档明确要求调用落在它的推理队列里。这里用信号量把所有生成串行化，
///       代价是多连接时会排队（见差异账 D007）。</item>
/// <item><b>句级切分在适配器里做。</b>模型按 token 吐，TTS 要按句吃；
///       中间这层缓冲是延迟与音质的折中点，放在后端最合适——换后端不必重写策略。</item>
/// </list>
/// </summary>
public sealed class TensorSharpLanguageModel : ILanguageModelBackend
{
    private readonly LlmOptions _options;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private ModelService? _service;
    private bool _disposed;

    public TensorSharpLanguageModel(LlmOptions options)
    {
        _options = options;
    }

    public string Name => "tensorsharp";

    private ModelService EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_service is not null)
        {
            return _service;
        }

        if (string.IsNullOrWhiteSpace(_options.ModelPath) || !File.Exists(_options.ModelPath))
        {
            throw new BackendUnavailableException(
                Name, $"未找到 GGUF 模型文件：{_options.ModelPath ?? "(未指定 --llm_model_path)"}");
        }

        var service = new ModelService();
        service.LoadModel(_options.ModelPath, _options.MmProjPath ?? string.Empty, _options.Backend ?? "cpu");
        _service = service;
        return service;
    }

    public async IAsyncEnumerable<LlmResponseChunk> GenerateAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var service = EnsureLoaded();

        var history = BuildHistory(request);
        var maxTokens = request.MaxOutputTokens ?? _options.MaxNewTokens;
        var accumulator = new SentenceAccumulator();

        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = service.ChatStreamAsync(history, maxTokens, cancellationToken);

            await foreach (var token in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var sentence in accumulator.Push(token))
                {
                    yield return Chunk(sentence, request);
                }
            }

            foreach (var sentence in accumulator.Flush())
            {
                yield return Chunk(sentence, request);
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private static LlmResponseChunk Chunk(string sentence, LlmRequest request)
        => new LlmResponseChunk
        {
            Parts = [new AssistantTextPart { Text = sentence }],
            SelectedLanguage = null,
        }.Normalize();

    /// <summary>
    /// 把会话历史翻译成 TensorSharp 的 <see cref="ChatMessage"/> 列表。
    ///
    /// 指令（instructions）在 TensorSharp 侧没有独立字段，按 chat 模板惯例作为
    /// system 消息放在最前——这与 OpenAI 兼容语义一致。
    /// </summary>
    private static List<TSChatMessage> BuildHistory(LlmRequest request)
    {
        var history = new List<TSChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            history.Add(new TSChatMessage { Role = "system", Content = request.Instructions });
        }

        foreach (var message in request.History)
        {
            var role = message.Role switch
            {
                "tool" => "user",
                _ => message.Role,
            };

            var content = message.Role == "tool"
                ? $"tool result ({message.ToolCallId}): {message.Content}"
                : message.Content;

            history.Add(new TSChatMessage { Role = role, Content = content });
        }

        return history;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _service?.UnloadModel();
            _service?.Dispose();
        }
        catch (Exception ex)
        {
            TranscriptLog.Error("failed to dispose TensorSharp model service", ex);
        }

        _inferenceLock.Dispose();
    }
}
