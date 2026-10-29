#!/usr/bin/env bash
set -euo pipefail

model_dir="${MODEL_DIR:-/models}"
downloader="${MODEL_DOWNLOADER:-/usr/local/bin/download-models.sh}"
manifest="${MODEL_MANIFEST:-/usr/local/share/speech-to-speech/models.tsv}"

"$downloader" --models-dir "$model_dir" --manifest "$manifest"
exec dotnet /app/speech-to-speech.dll "$@"
