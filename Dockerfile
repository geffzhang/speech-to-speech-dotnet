# ── 构建阶段 ─────────────────────────────────────────────────────
# 与上游 Dockerfile 的差异：上游在 CUDA 运行时镜像里用 uv 装整个 Python
# 运行时；C# 侧产物是自包含发布目录，runtime 只需要 ASP.NET Core。
# 全链路推理都是 CPU（VAD/STT/SmartTurn 走 ONNX Runtime，LLM 走
# TensorSharp，TTS 走 QwenTTS），不需要 CUDA 基础镜像。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先拷工程文件还原依赖——源码变更时不打翻 NuGet 缓存层。
# 只对 Cli 还原（slnx 还会拉 tests/tools，构建上下文里没有）：publish Cli
# 会传递还原全部 ProjectReference。
COPY Directory.Build.props ./
COPY ["src/SpeechToSpeech.Cli/SpeechToSpeech.Cli.csproj", "src/SpeechToSpeech.Cli/"]
COPY ["src/SpeechToSpeech.Core/SpeechToSpeech.Core.csproj", "src/SpeechToSpeech.Core/"]
COPY ["src/SpeechToSpeech.Realtime/SpeechToSpeech.Realtime.csproj", "src/SpeechToSpeech.Realtime/"]
COPY ["src/SpeechToSpeech.Backends.Lingua/SpeechToSpeech.Backends.Lingua.csproj", "src/SpeechToSpeech.Backends.Lingua/"]
COPY ["src/SpeechToSpeech.Backends.ManySpeech/SpeechToSpeech.Backends.ManySpeech.csproj", "src/SpeechToSpeech.Backends.ManySpeech/"]
COPY ["src/SpeechToSpeech.Backends.OpenAiCompatible/SpeechToSpeech.Backends.OpenAiCompatible.csproj", "src/SpeechToSpeech.Backends.OpenAiCompatible/"]
COPY ["src/SpeechToSpeech.Backends.QwenTts/SpeechToSpeech.Backends.QwenTts.csproj", "src/SpeechToSpeech.Backends.QwenTts/"]
COPY ["src/SpeechToSpeech.Backends.SmartTurn/SpeechToSpeech.Backends.SmartTurn.csproj", "src/SpeechToSpeech.Backends.SmartTurn/"]
COPY ["src/SpeechToSpeech.Backends.TensorSharp/SpeechToSpeech.Backends.TensorSharp.csproj", "src/SpeechToSpeech.Backends.TensorSharp/"]
RUN dotnet restore src/SpeechToSpeech.Cli/SpeechToSpeech.Cli.csproj

COPY src/ src/
RUN dotnet publish src/SpeechToSpeech.Cli -c Release -o /app --no-restore

# ── 运行阶段 ─────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# 模型目录约定：挂载到 /models（vad/whisper/llm/tts/smart-turn 各一个子目录，
# 或按需覆盖启动参数）。容器里没有音频设备，serve 模式（客户端经 WebSocket
# 推流）是唯一用法。
VOLUME ["/models"]

ENV DOTNET_gcServer=1
EXPOSE 8765/tcp

COPY --from=build /app ./

# AssemblyName 是 speech-to-speech（与上游可执行名一致），发布产物不含 Cli 后缀。
ENTRYPOINT ["dotnet", "speech-to-speech.dll", "serve", "--host", "0.0.0.0", "--port", "8765"]
