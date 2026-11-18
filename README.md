# LMT · Listen Music Together

把 Windows 音乐软件正在播放的声音，实时分享给打开网页的另一位听众。

房主使用桌面客户端选择音乐进程、开始捕获并创建房间，再把邀请链接发给对方。听众无需安装软件，打开链接并点击“开始收听”即可。

当前版本支持 **1 位房主 + 1 位听众**，桌面端和网页端均采用浅色界面。

## 功能

- **指定程序音频**：使用 Windows Process Loopback 捕获选中的进程及其子进程，不使用整个系统的混音回环。
- **快速选择来源**：支持刷新进程、显示全部进程、仅显示有音频活动的进程树。
- **歌曲信息同步**：通过 Windows SMTC 获取标题、艺术家、专辑、播放状态和封面，支持自动匹配与手动选择媒体会话。
- **浏览器实时收听**：使用 WebRTC 和 Opus，提供本地播放/暂停、音量、连接状态和重连按钮，适配电脑及手机。
- **独立房间**：随机房间 ID，房主和听众使用不同令牌；房间到期或房主断开后关闭。
- **不落盘保存音频**：桌面端不保存捕获的原始音频；Go 服务端不接收或保存音频内容，也不保存歌曲历史。

网页上的暂停和音量只影响听众本地，不会控制房主的播放器。

## 工作原理

```text
Windows 音乐软件及其子进程
          │ 指定进程树音频
          ▼
      LMT 桌面端 ────── WebRTC / Opus ──────► 听众浏览器
          │                                      │
          └──── WSS：连接协商、歌曲信息、状态 ──────┘
                              │
                         Go 房间服务
                              │
                       Nginx 或 Caddy
                         HTTPS / WSS
```

音频优先在房主和听众之间直传。配置 TURN 后，无法直连时可使用 TURN 中继；Go 服务端和 Nginx 仍不转发音频媒体流。

音频捕获格式为 48 kHz、16-bit、立体声，使用 Opus 128 kbps 编码，每帧 20 ms。实际延迟受系统、网络及浏览器缓冲影响，不保证固定延迟或严格同时播放。

## 环境要求

| 用途 | 要求 |
| --- | --- |
| 运行桌面端 | Windows x64，系统 Build 20348 或更高；推荐 Windows 11 |
| 桌面运行时 | 当前发布配置需要安装 .NET 10 Desktop Runtime x64 |
| 构建桌面端 | VS2026、“.NET 桌面开发”和“使用 C++ 的桌面开发”工作负载、v145 工具集、Windows SDK 22621 或更高 |
| 构建服务端 | Go 1.24 或更高 |
| 构建网页 | Node.js 22.12 或更高、pnpm 12.16.1 |
| 部署服务端 | 目标为 Debian 13 x64；服务器无需安装 Go、Node.js 或 pnpm |
| 公网收听 | HTTPS 域名、有效证书，以及双方可达的 STUN/TURN 网络 |

普通 Windows 10 22H2 的 Build 19045 不满足进程回环捕获要求。当前不提供 x86 或 ARM64 桌面端及原生 DLL。

## 快速开始

1. 按下文构建并部署服务端和网页，准备好 HTTPS 地址及房间创建密钥。
2. 在 VS2026 中发布桌面客户端，运行发布目录中的 `desktop.exe`。
3. 播放音乐，刷新来源列表，选中音乐软件对应的进程并点击“开始捕获”。
4. 检查歌曲信息；未自动匹配时，可手动选择 SMTC 媒体会话。
5. 填写服务端地址，例如 `https://listen.example.com`，以及服务端设置的创建密钥，然后创建房间。
6. 复制完整邀请链接发给听众。听众打开链接后点击“开始收听”。

邀请链接中的 `#token=...` 是访问凭据的一部分，应完整保留。房主退出、房间过期或服务端重启后，需要重新创建房间。

## 项目结构

```text
LMT/
├── src/
│   ├── desktop/                C# / WPF 桌面客户端
│   │   ├── Native/             C++ 进程音频捕获 DLL
│   │   ├── Services/           音频、SMTC、WebRTC 和房间连接
│   │   ├── Assets/             Windows 图标与 WPF 矢量图标
│   │   ├── Themes/             桌面浅色主题
│   │   └── Properties/PublishProfiles/
│   │                           VS 文件夹发布配置
│   ├── server/                 Go 网页、房间与 WebSocket 服务
│   └── web/                    React + TypeScript + Vite 网页
├── deploy/                     Linux 部署示例
├── scripts/                    Windows 构建和开发脚本
├── licenses/                   第三方许可证
├── artifacts/                  自动生成的发布包，不提交 Git
├── .gitattributes              Linux 部署文件的 Git 换行规则
├── .gitignore                  编译产物及本地文件忽略规则
├── LICENSE                     项目许可证
└── THIRD_PARTY_NOTICES.md       第三方组件说明
```

## 构建与发布

### 桌面客户端

使用 VS2026 打开 `src/desktop/desktop.slnx`，选择 `Release`，右键 `desktop` 项目 →“发布”→ `Windows-x64`。项目固定生成 x64 程序，默认输出：

```text
src/desktop/bin/publish/win-x64/
```

路径基于 `$(MSBuildProjectDirectory)` 计算，不依赖仓库所在的盘符或文件夹。普通生成和发布都会自动调用 VS2026 的 MSBuild 构建 C++ DLL。

当前配置为依赖框架、托管程序集单文件发布。**`LmtCapture.dll` 会独立复制到 `desktop.exe` 同目录，分发时必须保留。** 请分发整个发布目录及其中的第三方许可证。原生 DLL 使用静态 C++ 运行库。

也可以从仓库根目录用命令行发布：

```powershell
dotnet publish src/desktop/desktop.csproj -c Release -p:PublishProfile=Windows-x64
```

### Debian x64 服务端与网页

在 Windows 仓库根目录运行：

```powershell
.\scripts\build.ps1
```

脚本会安装前端锁定依赖、构建网页、运行 Go 测试，然后交叉编译 Linux x64 后端。它不会发布桌面端。输出为：

```text
artifacts/server/linux-amd64/
├── lmt-server
├── SHA256SUMS
├── web/
├── deploy/
├── licenses/
├── README.md
└── THIRD_PARTY_NOTICES.md
```

编译参数为 `GOOS=linux GOARCH=amd64 GOAMD64=v1 CGO_ENABLED=0`，产物是静态 Linux x86-64 可执行文件。部署时同时上传 `web/` 和其他随包文件。

## Linux 部署

以下步骤以 Debian 13、systemd、Nginx 和域名 `listen.example.com` 为例。将示例域名替换成自己的域名，配置对应 DNS A/AAAA 记录；只有服务器实际具备公网 IPv6 时才设置 AAAA。

### 1. 上传并配置 LMT

把发布包的内容上传到 `/opt/LMT-linux-amd64/`，目录布局如下：

```text
/opt/LMT-linux-amd64/
├── lmt-server
├── SHA256SUMS
├── web/
├── config/
│   └── lmt.env                 实际运行配置，包含创建密钥
├── deploy/                     可复制使用的部署模板
├── licenses/
└── ...
```

LMT 程序、网页和运行配置集中在这个目录；systemd 服务文件、Nginx 站点配置和 Certbot 证书使用各自的系统标准位置。

在 SSH 终端执行：

```bash
cd /opt/LMT-linux-amd64
sha256sum -c SHA256SUMS
sudo chown -R root:root /opt/LMT-linux-amd64
sudo chmod 755 /opt/LMT-linux-amd64 /opt/LMT-linux-amd64/lmt-server
sudo chmod -R a+rX /opt/LMT-linux-amd64/web
sudo install -d -m 700 /opt/LMT-linux-amd64/config

# 仅首次部署时复制模板；更新时保留已有运行配置。
sudo install -m 600 deploy/server.env.example config/lmt.env
sudo editor config/lmt.env
```

至少修改以下两项：

```dotenv
LMT_PUBLIC_URL=https://listen.example.com
LMT_CREATE_KEY=替换成随机生成的长密钥
```

用 `openssl rand -hex 32` 可生成创建密钥，在桌面端填写相同值。模板中的运行路径已使用 `/opt/LMT-linux-amd64/`，默认后端仅监听本地双栈 `127.0.0.1:18080` 和 `[::1]:18080`。

真实 `config/lmt.env`、备份、SSH 私钥和证书私钥不应提交到 GitHub。仓库中的 `server.env.example` 仅使用占位创建密钥。

### 2. 启用 systemd 服务

```bash
sudo install -m 644 /opt/LMT-linux-amd64/deploy/lmt.service /etc/systemd/system/lmt.service
sudo systemctl daemon-reload
sudo systemctl enable --now lmt.service
sudo systemctl status lmt.service --no-pager
curl --fail http://127.0.0.1:18080/healthz
curl --noproxy '*' --fail 'http://[::1]:18080/healthz'
```

健康检查应返回 `{"status":"ok"}`。服务使用 systemd 的动态用户，无需手动建立固定账号；环境配置由 systemd 读取，保持 `root:root`、`600` 权限即可。

### 3. 配置 Nginx 并申请证书

安装组件，确认域名可访问服务器的 TCP 80/443 端口：

```bash
sudo apt update
sudo apt install nginx certbot python3-certbot-nginx
```

证书尚不存在时，先创建只监听 HTTP 的站点：

```bash
sudo tee /etc/nginx/sites-available/lmt > /dev/null <<'NGINX'
server {
    listen 80;
    listen [::]:80;
    server_name listen.example.com;
    location / {
        proxy_pass http://127.0.0.1:18080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
NGINX

# 已有同名站点链接时跳过这一行。
sudo ln -s /etc/nginx/sites-available/lmt /etc/nginx/sites-enabled/lmt
sudo nginx -t
sudo systemctl reload nginx
sudo certbot --nginx -d listen.example.com --redirect
```

按 Certbot 提示完成申请。证书签发后，使用完整反代模板：

```bash
sudo install -m 644 /opt/LMT-linux-amd64/deploy/nginx.conf.example /etc/nginx/sites-available/lmt
sudo editor /etc/nginx/sites-available/lmt
sudo nginx -t
sudo systemctl reload nginx
sudo systemctl enable --now certbot.timer
sudo certbot renew --cert-name listen.example.com --dry-run
curl --fail https://listen.example.com/healthz
```

编辑时替换所有 `listen.example.com`，确认两个 `ssl_certificate` 配置项指向 Certbot 实际生成的证书。已有同名证书时，目录名可能带数字后缀，应以 `sudo certbot certificates` 的输出为准；续期命令中的 `--cert-name` 也使用实际证书名称。

模板提供 HTTPS 跳转、IPv4/IPv6、HTTP/2、WebSocket Upgrade 和本地双栈 upstream。`http2 on;` 需要 Nginx 1.25.1 或更高且包含 HTTP/2 模块。`map` 和 `upstream` 必须在 Nginx 的 `http {}` 上下文加载，Debian 默认的 `sites-enabled/*` 满足这一要求。

保留 `LMT_BEHIND_PROXY=1`。后端不用开放到公网，网页、API 和 WSS 共用同一 HTTPS 域名，不要改写浏览器发送的 Origin。服务器禁用 IPv6 时，需要同时调整 `LMT_ADDR` 和 Nginx 中的 IPv6 监听及 upstream 地址。

### Caddy 替代方案

也可使用 [deploy/Caddyfile](deploy/Caddyfile)：替换域名，合并到现有 Caddy 配置并重新加载，由 Caddy 提供 HTTPS 和反向代理。同样设置 `LMT_BEHIND_PROXY=1`。同一组 80/443 端口上选择 Nginx 或 Caddy 中的一种。

### 管理与更新

```bash
sudo systemctl status lmt.service --no-pager
sudo journalctl -u lmt.service -f
sudo systemctl restart lmt.service
```

更新发布包时先停止 LMT，再替换后端、`web/` 和相关发布文件，校验 `SHA256SUMS` 后启动服务。**保留已有 `config/lmt.env`，不要用模板覆盖创建密钥或 ICE 配置。** 修改 service 文件后先执行 `systemctl daemon-reload`。

房间仅存在于内存中，服务重启后原邀请链接失效。

## STUN 与 TURN

- **STUN**：帮助双方发现公网映射地址，提高直连成功率，本身不转发音频。
- **TURN**：无法直连时中继音频，需要自行准备可用的 TURN 服务和账号。

部署模板默认使用 Google 公共 STUN：

```dotenv
LMT_ICE_SERVERS=[{"urls":["stun:stun.l.google.com:19302"]}]
```

公共 STUN 的可达性取决于双方网络，不保证所有 NAT 和防火墙环境都能连接。房间连接成功但音频直连失败时，可增加 TURN：

```dotenv
LMT_ICE_SERVERS=[{"urls":["stun:stun.l.google.com:19302"]},{"urls":["turn:turn.example.com:3478?transport=udp","turn:turn.example.com:3478?transport=tcp"],"username":"your-user","credential":"your-password"}]
```

替换 TURN 域名和凭据，放通该服务所需的监听及中继端口。ICE 配置会下发给房间双方，TURN 账号应设置使用限制。修改运行配置后重启 LMT，并重新创建房间。

## 服务端配置

服务端读取环境变量。systemd 从 `/opt/LMT-linux-amd64/config/lmt.env` 注入配置；直接执行二进制不会自动读取该文件。

| 变量 | 程序默认值或用途 |
| --- | --- |
| `LMT_ADDR` | `127.0.0.1:18080`；支持逗号分隔多个地址，部署模板使用 `127.0.0.1:18080,[::1]:18080` |
| `LMT_PUBLIC_URL` | `http://localhost:18080`；用于邀请链接和浏览器 Origin 检查，公网填写 HTTPS 源站，不包含业务路径、查询参数或令牌 |
| `LMT_WEB_DIR` | `../web/dist`；部署模板使用 `/opt/LMT-linux-amd64/web` |
| `LMT_ROOM_TTL` | `4h`；允许 `1m` 至 `24h` |
| `LMT_CREATE_KEY` | 默认不设；配置后桌面端必须提供相同密钥才能创建房间 |
| `LMT_ICE_SERVERS` | 默认 `[]`；STUN/TURN JSON 数组，部署模板预设 Google STUN |
| `LMT_BEHIND_PROXY` | TLS 由反代提供时设为 `1`，后端应监听本地地址 |
| `LMT_TLS_CERT` / `LMT_TLS_KEY` | Go 直接提供 TLS 时同时设置，并取消反代模式；运行账号必须有权读取证书及私钥 |

## 本地开发与验证

以下 Windows 命令均从仓库根目录开始执行。

### 运行完整网页

```powershell
.\scripts\build.ps1
.\scripts\dev.ps1
```

浏览器和桌面端均使用 `http://localhost:18080`。这仅用于本机开发，分享给其他人时使用公网 HTTPS 域名。

### 前端热更新

在第一个终端运行 Go：

```powershell
cd src/server
$env:LMT_ADDR = '127.0.0.1:18080'
$env:LMT_PUBLIC_URL = 'http://localhost:5173'
go run .
```

在第二个终端运行 Vite：

```powershell
cd src/web
pnpm install --frozen-lockfile
pnpm dev
```

打开 `http://localhost:5173`；桌面端创建房间仍连接 `http://localhost:18080`。Vite 将 `/api` 和 WebSocket 代理至 Go，生成的邀请链接使用 5173 端口。若改用其他 Vite 端口，也要同步修改 `LMT_PUBLIC_URL`。

前端仅使用 pnpm。更新依赖时一并提交 `pnpm-lock.yaml`；NuGet 和 Go 分别使用 `packages.lock.json`、`go.sum` 固定依赖。

### 可执行的检查

```powershell
# 网页类型检查和生产构建。
pnpm --dir src/web build

# 桌面构建，自动构建原生 DLL。
dotnet build src/desktop/desktop.csproj -c Release

# Go 测试和静态检查。
cd src/server
go test -count=1 ./...
go vet ./...
```

可额外运行 `go test -race -count=1 ./...` 检查 Go 并发问题；Windows 下需要可用的 C 编译器及 `CGO_ENABLED=1`。上述普通构建和检查不会主动播放测试音。

### 图标与 Git 换行

网页图标为 `src/web/public/favicon.svg`。修改后运行以下脚本，同步生成桌面 ICO 和 WPF 矢量资源：

```powershell
.\scripts\update-desktop-icon.ps1
```

`.gitattributes` 为 `deploy/` 下四个文件设置 `text eol=lf`，Windows 启用 `core.autocrlf` 时这些文件也保持 LF。检查规则：

```powershell
git check-attr text eol -- deploy/Caddyfile deploy/lmt.service deploy/nginx.conf.example deploy/server.env.example
```

对于已被 Git 跟踪的旧版部署文件，应用规则后可用 `git add --renormalize -- deploy/` 规范化部署目录。`bin/`、`obj/`、`node_modules/`、`dist/`、`artifacts/` 等生成目录已在 `.gitignore` 中排除。

## 范围与限制

- 一间房间只允许一位听众；多人收听需要进一步引入 SFU 或媒体中继架构。
- 不提供网页录音、下载、切歌或控制房主电脑的功能，但无法阻止听众用其他工具录制自己听到的声音。
- 房间、当前歌曲及状态只保存在服务端内存中，未连接房主的新房间会自动清理。
- 不同播放器的 SMTC、受保护内容和不同网络的穿透能力需要实际环境验证。

## 许可证

项目许可证见 [LICENSE](LICENSE)。第三方组件保留各自的许可证与使用条件，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [licenses/](licenses/)。分发发布包时保留这些说明文件。
