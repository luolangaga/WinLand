# LocalSend 分享

将文件、文件夹、文本（含链接）、图片拖到灵动岛，移到「LocalSend 分享」卡片后松手。在本机 LocalSend 中选择接收设备完成发送。插件只负责交接，不表示传输已完成。

1.0.1：已有实例时直接调用 LocalSend 的本机 `/api/localsend/v2/show` 接口，使用其本地交接令牌和设备证书，不启动第二个进程。未运行时仅启动一个空实例，等待接口就绪再交接；接口失败会提示错误，不以新开实例兜底。支持自定义端口、HTTP/HTTPS 和便携配置；不改动 LocalSend 配置。LocalSend 凭据只在内存中使用，不写日志或插件包。

需要 WinIsland ≥ 2.4.0 和已安装的 LocalSend 桌面版。在设置 → LocalSend 分享中可填写便携版程序的完整路径；留空会查找运行中的程序、安装信息、常见安装位置和 PATH。

默认文本以 UTF-8 `.txt` 文件发送，保留全部文字；LocalSend ≥ 1.18 可启用原生文本（LocalSend 会裁掉首尾空白）。超长文本自动回退为文件。图片按文件头识别格式并保存原始字节，未知格式用 `.img`。

文件和文件夹直接交给 LocalSend，不复制、不改动源文件。空文件夹能否发送由 LocalSend 决定。Windows 命令行超限时会提示分批拖入。图片投放受宿主 32 MB 上限限制；更大的图片请从资源管理器以文件拖入。

临时文本/图片位于 `%LocalAppData%\WinIsland\localsend-share\outbox`。不会在停用时删除以免打断发送；超过 7 天的文件在下一次文本/图片分享时清理。

从本目录运行 `dotnet build`。项目引用仓库 SDK，构建会将插件复制到当前源码宿主的 Debug plugins 目录。安装包位于 `samples/dist/localsend-share.lwp`，在插件管理中选择「安装 .lwp」即可安装。

LocalSend 参数依据：[官方文件选择实现](https://github.com/localsend/localsend/blob/main/app/lib/provider/selection/selected_sending_files_provider.dart)。
