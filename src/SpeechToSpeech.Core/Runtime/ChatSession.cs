namespace SpeechToSpeech.Core.Runtime;

/// <summary>会话历史中的一条消息。</summary>
public sealed class ChatMessage
{
    /// <summary>线上会话项 id（<c>item_*</c>）。客户端按它删除或截断时用来定位。</summary>
    public string? Id { get; init; }

    public required string Role { get; init; }
    public string Content { get; init; } = "";

    /// <summary>助手的工具调用（仅 <c>assistant</c> 角色使用）。</summary>
    public List<ToolCallRef> ToolCalls { get; init; } = [];

    /// <summary>工具结果（仅 <c>tool</c> 角色使用）。</summary>
    public string? ToolCallId { get; init; }

    public static ChatMessage User(string content, string? id = null)
        => new() { Id = id, Role = "user", Content = content };

    public static ChatMessage Assistant(string content, List<ToolCallRef>? toolCalls = null, string? id = null)
        => new() { Id = id, Role = "assistant", Content = content, ToolCalls = toolCalls ?? [] };

    public static ChatMessage Tool(string toolCallId, string content, string? id = null)
        => new() { Id = id, Role = "tool", Content = content, ToolCallId = toolCallId };

    public static ChatMessage System(string content, string? id = null)
        => new() { Id = id, Role = "system", Content = content };
}

/// <summary>历史中记录的助手工具调用。</summary>
public sealed class ToolCallRef
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public string Arguments { get; init; } = "{}";
}

/// <summary>
/// 一次连接的会话状态：LLM 上下文 + 提交代际。
///
/// 这是 <see cref="RuntimeConfig"/> 里被多阶段共享的可变部分，也是"唯一写入口"：
/// 只有 realtime 服务（以及工具跟进）可以追加消息，管线线程只读（见差异账 C-02）。
/// </summary>
public sealed class ChatSession
{
    private readonly Lock _lock = new();
    private readonly List<ChatMessage> _messages = [];

    public ChatSession(string? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public string SessionId { get; }

    /// <summary>每次提交（完成一轮用户输入）递增，用于把迟到的完成分流到原输入项。</summary>
    public int CommitGeneration { get; private set; }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }

    public int Count
    {
        get { lock (_lock) { return _messages.Count; } }
    }

    public void Append(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
    }

    public void AppendRange(IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            _messages.AddRange(messages);
        }
    }

    /// <summary>提交当前对话轮次，返回新的提交代际。</summary>
    public int CommitTurn()
    {
        lock (_lock)
        {
            CommitGeneration++;
            return CommitGeneration;
        }
    }

    /// <summary>回滚到最后一次提交点之后追加的全部消息（不完整/失败响应使用）。</summary>
    public void RollbackToCommit()
    {
        lock (_lock)
        {
            while (_messages.Count > 0 && _messages[^1].Role is "assistant" or "tool")
            {
                _messages.RemoveAt(_messages.Count - 1);
            }
        }
    }

    /// <summary>
    /// 按会话项 id 删除一条消息，返回被删的那条（找不到为 null）。
    ///
    /// 客户端 <c>conversation.item.delete</c> 用：删掉之后模型下一轮就看不到它了，
    /// 所以是"从上下文里抹掉"而不是"仅在界面上隐藏"。
    /// </summary>
    public ChatMessage? Delete(string itemId)
    {
        lock (_lock)
        {
            var index = _messages.FindIndex(message => message.Id == itemId);
            if (index < 0)
            {
                return null;
            }

            var removed = _messages[index];
            _messages.RemoveAt(index);
            return removed;
        }
    }

    /// <summary>当前历史里所有已知的会话项 id，按时序。</summary>
    public IReadOnlyList<string> ItemIds()
    {
        lock (_lock)
        {
            return [.. _messages.Where(message => message.Id is not null).Select(message => message.Id!)];
        }
    }

    /// <summary>
    /// 当前上下文里所有工具结果（<c>function_call_output</c>）的 <c>call_id</c>，去重后按码点序。
    ///
    /// 语义是「哪些工具结果真的到了模型」，所以取的是**送进这次生成的历史快照**，
    /// 不是此刻的会话——生成期间客户端可能又追加了条目，那些没有到过模型，不能算。
    /// 上游 <c>ChatHistory.tool_output_call_ids()</c> 的等价物。
    /// </summary>
    public IReadOnlyList<string> ToolOutputCallIds() => ToolOutputCallIds(Snapshot());

    /// <summary>
    /// 会话是否在等任何函数调用的结果。
    ///
    /// 上游 <c>has_pending_tool_calls</c>：assistant 消息发起的 call_id 还没有
    /// 对应的 function_call_output 到达。与顺序无关——工具结果先于调用登记
    /// 出现时同样算已应答（上游用字典登记/摘除，这里用集合差，语义相同）。
    /// </summary>
    public bool HasPendingToolCalls() => HasPendingToolCalls(Snapshot());

    /// <summary>同上，但作用于任意消息序列——带内 <c>response.input</c> 要先与
    /// 会话合并判断（上游校验进 <c>candidate_chat</c> 副本）。</summary>
    public static bool HasPendingToolCalls(IEnumerable<ChatMessage> messages)
    {
        var answered = new HashSet<string>();
        foreach (var m in messages)
        {
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
            {
                answered.Add(m.ToolCallId);
            }
        }

        return messages.Any(m => m.ToolCalls.Any(t => !answered.Contains(t.CallId)));
    }

    /// <summary>
    /// 从任意消息序列里挑出工具结果的 <c>call_id</c>。
    ///
    /// 去重后再排序：同一个 <c>call_id</c> 被重复提交时，上游用的是集合语义，
    /// 列两遍会让客户端按值比对时对不上。
    /// </summary>
    public static IReadOnlyList<string> ToolOutputCallIds(IEnumerable<ChatMessage> messages)
        => [.. messages
            .Where(message => message.Role == "tool" && !string.IsNullOrEmpty(message.ToolCallId))
            .Select(message => message.ToolCallId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)];

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            CommitGeneration = 0;
        }
    }
}
