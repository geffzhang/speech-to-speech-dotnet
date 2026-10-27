using SpeechToSpeech.Evals.BigBenchAudio;

// Big Bench Audio vibe check CLI 入口：
//   EvalProbe run --out main.json
//   EvalProbe run --out pr.json --compare main.json
//   EvalProbe compare main.json pr.json
return BigBenchAudioCli.Main(args);
