# SenseVoice as the Default STT

## Intent

Improve the default Chinese speech-recognition path by using SenseVoice instead of the current Whisper base INT8 model, while retaining Whisper and Paraformer as selectable backends.

## Behavior

- The CLI default STT backend becomes `sense-voice`.
- `serve` and `local` automatically prepare the selected built-in local STT model group.
- The default SenseVoice model is the pinned INT8 export from `csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17`, using its `model.int8.onnx` and `tokens.txt` artifacts.
- Whisper and Paraformer remain available by explicit `--stt whisper` and `--stt paraformer` selection.
- Model preparation supports distinct `sense-voice`, `paraformer`, and `whisper` groups. Explicit custom model directories remain user-managed and validated before the listener starts.
- No fallback to another STT backend occurs if the selected backend's files are missing or invalid.
- Documentation states the new default, download size, alternate backend options, and that SenseVoice weights follow the FunASR model license; users must review the source license.

## Architecture

Reuse `LocalModelPreparation` and the existing model manifest/downloader. Add per-backend model groups and default paths; set the shared CLI STT default to `sense-voice`. Keep backend inference implementation unchanged.

## Model provenance and constraints

The selected pinned export provides the `model.int8.onnx` and `tokens.txt` layout expected by the current ManySpeech adapter. Its source license points to the FunASR model license; do not describe the weights as Apache-2.0. The INT8 model is about 228.5 MiB, plus a small token file.

## Acceptance

- Omitting `--stt` selects `sense-voice`; explicitly selecting `whisper` or `paraformer` continues to work.
- Default preparation selects SenseVoice assets and does not select Whisper assets.
- Explicit backend choices prepare only their own model group.
- Custom STT model paths remain untouched and missing required files fail before listening.
- Downloader manifest and scripts accept the new model groups and validate downloaded files.
- README clearly documents size, license caveat, defaults, and backend override commands.
