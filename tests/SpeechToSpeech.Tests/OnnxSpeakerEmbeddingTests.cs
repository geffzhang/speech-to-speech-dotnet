using SpeechToSpeech.Backends.SpeakerEmbedding;

namespace SpeechToSpeech.Tests;

/// <summary>
/// 声纹引擎的端到端测试，<b>需要真实权重</b>：<c>models/speaker-embedding/model.onnx</c>，
/// 或用 <c>SPEECH_TO_SPEECH_SPEAKER_EMBEDDING_MODEL</c> 指向别处。模型不入库，
/// 所以没有模型时这些用例直接返回——既不失败，也不假装跑过。
///
/// <para>断言的都是契约，不是准确率：维度来自 metadata、输出是单位向量、同一段音频两次结果相同、
/// 拒绝它服务不了的采样率与长度。准确率是模型的性质，由 <c>tools/DiarizationProbe</c> 在真实语音上观测。</para>
/// </summary>
public class OnnxSpeakerEmbeddingTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Produces_a_unit_vector_of_the_declared_width()
    {
        string? model = ResolveModel();
        if (model is null) return;

        using var engine = new OnnxSpeakerEmbedding(model);
        Assert.Equal(192, engine.EmbeddingDim);
        Assert.Equal(SampleRate, engine.SampleRate);

        var embedding = engine.Embed(Speech(2.0), SampleRate);

        Assert.Equal(engine.EmbeddingDim, embedding.Length);
        double norm = Math.Sqrt(embedding.Sum(v => (double)v * v));
        Assert.True(Math.Abs(norm - 1.0) < 1e-5, $"范数 {norm:F6}");
        Assert.Contains(embedding, v => Math.Abs(v) > 0.01f); // 不是全零向量
    }

    [Fact]
    public void Is_deterministic()
    {
        string? model = ResolveModel();
        if (model is null) return;

        using var engine = new OnnxSpeakerEmbedding(model);
        var audio = Speech(1.5);

        var first = engine.Embed(audio, SampleRate);
        var second = engine.Embed(audio, SampleRate);

        // 逐位相同：这是「同一段音频给出同一个 id」的地基。差一点点就会在阈值附近来回跳。
        Assert.Equal(first, second);
    }

    [Fact]
    public void Accepts_any_audio_length_but_the_same_width()
    {
        string? model = ResolveModel();
        if (model is null) return;

        using var engine = new OnnxSpeakerEmbedding(model);

        // 模型的 T 轴是动态的：0.6 秒与 4 秒都该给出同样宽度的向量。
        Assert.Equal(engine.EmbeddingDim, engine.Embed(Speech(0.6), SampleRate).Length);
        Assert.Equal(engine.EmbeddingDim, engine.Embed(Speech(4.0), SampleRate).Length);
    }

    [Fact]
    public void Rejects_a_sample_rate_it_cannot_serve()
    {
        string? model = ResolveModel();
        if (model is null) return;

        using var engine = new OnnxSpeakerEmbedding(model);

        // fbank 的帧长/帧移与 mel 频带都是按 16 kHz 定的，换采样率不是改一个数字的事。
        Assert.Throws<ArgumentException>(() => engine.Embed(Speech(1.0), 8000));
    }

    [Fact]
    public void Rejects_audio_shorter_than_one_frame()
    {
        string? model = ResolveModel();
        if (model is null) return;

        using var engine = new OnnxSpeakerEmbedding(model);

        // snip_edges=false 下不足一帧的界线是 shift/2 = 80 个样本（帧中心对齐，两端靠镜像补齐），
        // 不是 25 ms 的 400——100 个样本仍然算得出 1 帧。
        Assert.Throws<ArgumentException>(() => engine.Embed(new float[40], SampleRate));
        Assert.Throws<ArgumentException>(() => engine.Embed([], SampleRate));
    }

    /// <summary>确定性「语音状」信号：几个正弦叠加，够模型跑出稳定的向量。</summary>
    private static float[] Speech(double seconds)
    {
        int samples = (int)(seconds * SampleRate);
        var audio = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            double t = n / (double)SampleRate;
            audio[n] = (float)(0.25 * Math.Sin(2 * Math.PI * 180 * t)
                             + 0.15 * Math.Sin(2 * Math.PI * 640 * t)
                             + 0.05 * Math.Sin(2 * Math.PI * 1700 * t));
        }
        return audio;
    }

    private static string? ResolveModel()
    {
        string? env = Environment.GetEnvironmentVariable("SPEECH_TO_SPEECH_SPEAKER_EMBEDDING_MODEL");
        if (env is not null && File.Exists(env)) return env;

        // 仓库布局兜底：models/ 不入库，本地放过才有模型。
        var local = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "models", "speaker-embedding", "model.onnx"));

        return File.Exists(local) ? local : null;
    }
}
