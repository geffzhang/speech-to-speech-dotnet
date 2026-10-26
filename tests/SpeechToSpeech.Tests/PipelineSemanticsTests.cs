using SpeechToSpeech.Core.Pipeline;
using SpeechToSpeech.Core.Runtime;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 取消代际语义。
///
/// 这一组测试锁的是上游最容易被"优化"掉的部分：取消不是一个布尔开关，
/// 而是"代际递增 + 丢弃保护 + 迟到完成不得清保护"三件事的组合。
/// </summary>
public sealed class CancelScopeTests
{
    [Fact]
    public void NewScope_StartsAtGenerationZero_AndNotDiscarding()
    {
        var scope = new CancelScope();

        Assert.Equal(0, scope.Generation);
        Assert.False(scope.Discarding);
    }

    [Fact]
    public void Cancel_IncrementsGeneration_AndEnablesDiscardGuard()
    {
        var scope = new CancelScope();

        scope.Cancel();

        Assert.Equal(1, scope.Generation);
        Assert.True(scope.Discarding);
    }

    [Fact]
    public void IsStale_TrueOnlyForSupersededGeneration()
    {
        var scope = new CancelScope();
        var captured = scope.Generation;

        scope.Cancel();

        Assert.True(scope.IsStale(captured));
        Assert.False(scope.IsStale(scope.Generation));
    }

    [Fact]
    public void ResponseDone_ClearsDiscardGuard()
    {
        var scope = new CancelScope();
        var captured = scope.Generation;
        scope.Cancel();

        scope.ResponseDone(captured);

        Assert.False(scope.Discarding);
    }

    [Fact]
    public void ResponseDone_IgnoresLateCompletionForAnOlderGeneration()
    {
        // 场景：响应 A 被取消，随后响应 B 开始；A 的迟到完成通知不得清掉 B 的保护
        var scope = new CancelScope();
        var generationA = scope.Generation;
        scope.Cancel();
        scope.NewResponse();

        scope.Cancel();
        scope.ResponseDone(generationA);

        Assert.True(scope.Discarding);
    }

    [Fact]
    public void NewResponse_ClearsDiscardGuardWithoutTouchingGeneration()
    {
        var scope = new CancelScope();
        scope.Cancel();
        var generation = scope.Generation;

        scope.NewResponse();

        Assert.False(scope.Discarding);
        Assert.Equal(generation, scope.Generation);
    }

    [Fact]
    public void Cancel_WrapsAroundAt32Bits()
    {
        // 上游注释里那句 "after 4 billion generations, we'll wrap around xD" 的行为
        var scope = new CancelScope();

        for (var i = 0; i < 3; i++)
        {
            scope.Cancel();
        }

        Assert.Equal(3, scope.Generation);
    }
}

/// <summary>
/// 投机预取事务。
///
/// 核心不变量：被丢弃的预取永远不写历史；只有被认领之后清理才会执行。
/// 这条不变量一旦破，客户端会看到"没说过的话出现在上下文里"。
/// </summary>
public sealed class ResponsePrefetchTransactionTests
{
    [Fact]
    public void Complete_WithoutClaim_StoresCleanupUntilClaimed()
    {
        var transaction = new ResponsePrefetchTransaction();
        var ran = false;

        transaction.Complete(() => ran = true);

        Assert.False(ran);
        Assert.True(transaction.Claim());
        Assert.True(ran);
    }

    [Fact]
    public void Complete_AfterClaim_RunsCleanupImmediately()
    {
        var transaction = new ResponsePrefetchTransaction();
        var ran = false;

        Assert.True(transaction.Claim());
        transaction.Complete(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Discard_PreventsCleanupFromRunning()
    {
        var transaction = new ResponsePrefetchTransaction();
        var ran = false;

        transaction.Discard();
        transaction.Complete(() => ran = true);

        Assert.True(transaction.Discarded);
        Assert.False(transaction.Claimed);
        Assert.False(ran);
        Assert.False(transaction.Claim());
    }

    [Fact]
    public void RegisterAbort_AfterDiscard_RunsImmediately()
    {
        var transaction = new ResponsePrefetchTransaction();
        transaction.Discard();

        var aborted = false;
        transaction.RegisterAbort(() => aborted = true);

        Assert.True(aborted);
    }

    [Fact]
    public void Discard_RunsRegisteredAborts()
    {
        var transaction = new ResponsePrefetchTransaction();
        var aborted = false;
        transaction.RegisterAbort(() => aborted = true);

        transaction.Discard();

        Assert.True(aborted);
    }

    [Fact]
    public void Claim_FailingCleanup_DiscardsTransactionAndWakesWaiters()
    {
        var transaction = new ResponsePrefetchTransaction();
        transaction.Complete(() => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => transaction.Claim());
        Assert.True(transaction.Discarded);
        Assert.True(transaction.WaitUntilResolved(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void WaitUntilResolved_ReturnsFalseWhilePending()
    {
        var transaction = new ResponsePrefetchTransaction();

        Assert.False(transaction.WaitUntilResolved(TimeSpan.FromMilliseconds(10)));
    }
}

/// <summary>文本快照规则。</summary>
public sealed class TtsInputTests
{
    [Fact]
    public void SelectedLanguageAuto_ProducesNullTtsLanguage()
    {
        var config = new RuntimeConfig();
        var input = TtsInput.Create("hello", config, languageCode: "en", selectedLanguage: "auto");

        Assert.Equal("auto", input.SelectedLanguage);
        Assert.Null(input.TtsLanguageCode);
    }

    [Fact]
    public void ExplicitSelectedLanguage_WinsOverLanguageCode()
    {
        var input = TtsInput.Create("你好", languageCode: "en", selectedLanguage: "zh");

        Assert.Equal("zh", input.TtsLanguageCode);
    }

    [Fact]
    public void NoSelection_FallsBackToLanguageCode()
    {
        var input = TtsInput.Create("bonjour", languageCode: "fr");

        Assert.Equal("fr", input.TtsLanguageCode);
    }
}

/// <summary>会话配置的深合并语义。</summary>
public sealed class RuntimeConfigTests
{
    [Fact]
    public void ApplySessionUpdate_OnlyMergesExplicitFields()
    {
        var config = new RuntimeConfig();
        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject { ["instructions"] = "first" });

        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
        {
            ["audio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["output"] = new System.Text.Json.Nodes.JsonObject { ["voice"] = "serena" },
            },
        });

        Assert.Equal("first", config.Instructions);
        Assert.Equal("serena", config.Voice);
    }

    [Fact]
    public void ApplySessionUpdate_PreservesUntouchedNestedFields()
    {
        var config = new RuntimeConfig();
        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
        {
            ["audio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["input"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["format"] = new System.Text.Json.Nodes.JsonObject { ["rate"] = 16000 },
                },
            },
        });

        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
        {
            ["audio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["output"] = new System.Text.Json.Nodes.JsonObject { ["voice"] = "ryan" },
            },
        });

        Assert.Equal(16_000, config.InputSampleRate);
        Assert.Equal("ryan", config.Voice);
    }

    [Fact]
    public void SelectedLanguage_AutoIsPreserved()
    {
        var config = new RuntimeConfig();
        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
        {
            ["audio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["input"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["transcription"] = new System.Text.Json.Nodes.JsonObject { ["language"] = "AUTO" },
                },
            },
        });

        Assert.Equal("auto", config.SelectedLanguage);
    }

    [Fact]
    public void TurnDetectionDefaults_MatchOpenAiServerVad()
    {
        var config = new RuntimeConfig();

        Assert.True(config.CreateResponseEnabled);
        Assert.True(config.InterruptResponseEnabled);
        Assert.Equal(0.5, config.VadThreshold);
    }

    [Fact]
    public void SnapshotsEnabled_OnlyWhenExtensionAdvertised()
    {
        var config = new RuntimeConfig();
        Assert.False(config.InputAudioTranscriptionSnapshotsEnabled);

        config.ApplySessionUpdate(new System.Text.Json.Nodes.JsonObject
        {
            ["extensions"] = new System.Text.Json.Nodes.JsonArray(
                "speech_to_speech.input_audio_transcription.snapshot"),
        });

        Assert.True(config.InputAudioTranscriptionSnapshotsEnabled);
    }

    // output_modalities 的判定直接决定发哪种 delta、item 的 content 是 output_audio
    // 还是 output_text。判错不会报错，只会让客户端渲染成空消息——所以逐值锁死。

    [Fact]
    public void WantsAudio_AbsentModalitiesMeansAudio()
    {
        Assert.True(new RealtimeResponseCreateParams().WantsAudio);
    }

    [Fact]
    public void WantsAudio_EmptyModalitiesStillMeansAudio()
    {
        // 空数组与缺省等价：OpenAI 语义里"没声明"就是默认音频
        Assert.True(new RealtimeResponseCreateParams { OutputModalities = [] }.WantsAudio);
    }

    [Fact]
    public void WantsAudio_ExplicitAudioMeansAudio()
    {
        Assert.True(new RealtimeResponseCreateParams { OutputModalities = ["audio"] }.WantsAudio);
    }

    [Fact]
    public void WantsAudio_TextOnlySuppressesAudio()
    {
        // "audio" 已隐含转写，所以 text-only 是唯一的分支
        Assert.False(new RealtimeResponseCreateParams { OutputModalities = ["text"] }.WantsAudio);
    }
}
