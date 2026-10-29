# Docker

The Docker image contains the application and the pinned model manifest, but not
the model weights. On container startup, the entrypoint runs the existing Bash
downloader before starting `serve`.

## Start with Docker Compose

From the repository root:

```bash
docker compose up --build
```

On the first start, Compose downloads and verifies all five default model groups
(about **6.6 GB**) into the persistent `models` named volume. The server starts
only after all downloads pass size and SHA-256 verification. Later starts reuse
the volume and skip files already verified by the downloader. No host model
directory or separate host LLM service is required; the Compose configuration
uses the local TensorSharp GGUF model.

Connect a Realtime client to:

```text
ws://localhost:8765/v1/realtime
```

The container has no audio device, so use a client to stream audio over the
network. The models are downloaded from the fixed revisions and URLs in
[`../tools/models/models.tsv`](../tools/models/models.tsv). Check each model's
upstream license before use; downloading a model does not grant additional
rights.

## Custom model storage

The Compose `models` volume persists across container recreation. To use a
different model volume or a pre-populated host directory, override the Compose
volume mapping and mount it read-write at `/models`; the entrypoint verifies
the manifest files there before starting the service.

For a direct `docker run`, provide a writable volume mounted at `/models`.
The image defaults to `serve --host 0.0.0.0 --port 8765`; arguments after the
image name replace those defaults. The entrypoint always prepares the model
directory before executing the application.
