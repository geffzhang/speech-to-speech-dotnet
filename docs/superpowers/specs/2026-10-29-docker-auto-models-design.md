# Docker 自动准备模型设计

## 目标

让用户通过 Docker Compose 启动后无需手工把模型放到 `./cache/models/`，服务端在启动前自动获取并校验所需默认模型；Compose 默认使用本地 GGUF LLM，不要求另行运行兼容的宿主机 LLM 服务。

## 现状

- `download-models.sh` 和 `download-models.ps1` 从 `tools/models/models.tsv` 获取固定版本模型，校验大小与 SHA-256，支持断点续传。
- Docker 运行镜像使用 Debian/Ubuntu 系 .NET ASP.NET 镜像，原先只包含发布的应用程序。
- Compose 原先把宿主机 `./cache/models/` 以只读方式挂到 `/models`，并将 LLM 指向宿主机 `host.docker.internal:8080`。
- 默认五组模型约 6.6 GB。

## 设计

在运行镜像内提供 Bash、curl、CA 证书和 SHA-256 工具，并复制既有 `download-models.sh` 与 `tools/models/models.tsv`。容器 entrypoint 在启动 `serve` 之前调用下载器，将五组模型写入 `/models`。下载继续使用固定 URL、大小和 SHA-256 校验及现有续传行为；任何下载或校验失败都以非零状态退出，避免服务处于缺模型状态。

Compose 将 `/models` 挂载为可写的 Docker named volume `models`，以便首启填充并在容器重建后保留。entrypoint 完成模型准备后以 `exec` 启动 `dotnet /app/speech-to-speech.dll`，默认命令为 `serve --host 0.0.0.0 --port 8765`，Compose 可通过 `command` 覆盖。Compose 的 LLM 配置改用 `tensorsharp` 和 `/models/llm/Qwen3.5-0.8B-Q4_K_M.gguf`，不再需要宿主机兼容服务或 `extra_hosts`。

首次启动会拉取清单中的全部五组模型；之后复用持久卷，并由下载器校验已存在文件后跳过。Docker 用户仍可通过挂载自有 `/models` 卷覆盖默认模型数据。镜像不内置模型权重。

## 组件与边界

- `Dockerfile`：在运行镜像中加入 Bash、curl、SHA-256 工具、模型下载脚本与 TSV 清单，并配置 entrypoint。
- 新增容器 entrypoint：负责准备 `/models` 并将信号/退出状态传递给 `serve` 进程；下载失败不得继续启动。
- `docker-compose.yml`：使用可写 named volume，配置默认本地 LLM 模型路径，删除仅服务外部 LLM 所需的 host-gateway 配置。
- `README.md`：更新 Docker 快速启动说明，说明约 6.6 GB 首次下载、后续缓存和许可证注意事项。
- `.dockerignore`：只为固定模型清单开放构建上下文，不开放其余 `tools/` 内容。
- Docker 相关自动化检查：覆盖镜像所需下载资产存在、compose 默认命令与卷配置，以及失败不启动服务的行为；下载校验本身复用现有测试。

## 错误处理与安全

- 继续只信任版本锁定的模型清单，并在模型落盘前校验 SHA-256 与字节数。
- 不在镜像层写入模型，不将模型打进仓库。
- `/models` 必须可写；权限或存储不足应导致清晰的启动错误。
- 下载或校验失败须保留下载器既有的部分文件续传语义，并让容器以非零状态退出。

## 验收与测试

- 构建运行镜像时，运行层包含下载所需脚本、清单及 Bash/curl/SHA-256 工具。
- 空 named volume 首次启动会下载所有五组模型并在成功后执行 `serve`。
- 重启或重建容器复用该卷时，已验证模型不重复下载。
- 模型下载失败时容器不启动 `serve`，并返回非零退出码。
- Compose 使用本地 GGUF/TensorSharp，不要求外部 LLM 服务。
- 更新后的 README 能指导用户仅通过 Docker Compose 启动，并准确说明首次下载体量与持久化行为。
- `.dockerignore` 仅允许 `tools/models/models.tsv` 进入构建上下文，模型权重仍不进入镜像。
