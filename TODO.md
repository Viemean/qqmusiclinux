# QQ Music TUI - 综合架构重构与开发规划清单 (TODO)

本文档将项目近期讨论确认的全部技术任务，按 **【Bug 修复与隐患治理】**、**【功能修改与底层重构】**、**【全新功能构建】** 以及 **【体验打磨与细节改进】** 四大工程维度进行严格分类与结构化归档，并制定明确的施工依赖流水线、代码契约与验收门禁，确保 AI 与开发者能够无歧义、步步为营地推进修改。

---

## 一、 任务分类全局矩阵

```text
├── 1. Bug 修复与隐患治理 (Defect Fixes)
│   ├── [Fix 1] 核心播放队列与浏览视图强耦合导致切歌错乱 (P0 架构级缺陷)
│   ├── [Fix 2] 切歌瞬间数字爆音 (Click/Pop 缺陷修复，150ms 软 Crossfade)
│   ├── [Fix 3] 弱网抖动与合盖唤醒后播放器假死/停止 (网络断点自愈看门狗)
│   └── [Fix 4] 外部 ffmpeg 子进程异常退出与管道死锁/僵尸进程隐患
│
├── 2. 功能修改与底层重构 (Refactoring & Modifications)
│   ├── [Mod 1] 听歌识曲录音模块原生化 (ffmpeg 替换为 libpulse-simple P/Invoke)
│   ├── [Mod 2] 本地音乐元数据与封面提取原生化 (ffprobe/ffmpeg 替换为 ATL.NET 内存直读)
│   ├── [Mod 3] 封面圆角抗锯齿处理原生化 (magick/ffmpeg 替换为纯 C# 算法 + StbImageSharp)
│   └── [Mod 4] 构建系统与软件包依赖解耦 (PKGBUILD/打包脚本彻底移除 ffmpeg)
│
├── 3. 全新功能构建 (New Features)
│   ├── [Feat 1] WebDAV 远程私有云音乐库与层级浏览 (F 管理 / A 导入 / D 切换模式)
│   ├── [Feat 2] 主列表内即时查找与行去重跳转 (G 键单键浮窗 / 行粒度去重 / 连续 Enter)
│   ├── [Feat 3] 本地与 WebDAV 在线歌词智能匹配与升级 (Y 键常驻 / 时长容差防误匹配)
│   └── [Feat 4] 歌曲插队播放 (Play Next, N 键) 与播放队列抽屉视图 (Queue Drawer)
│
└── 4. 体验打磨与细节改进 (Enhancements & Improvements)
    ├── [Enh 1] Linux 原生桌面切歌通知 (D-Bus 带封面气泡弹窗，必须可配置/可快捷键随时关闭)
    ├── [Enh 2] 歌词时间轴实时微调与偏好持久化 ([ / ] 快捷键微调 ±0.5s)
    └── [Enh 3] 离线音频标签就地注入与自包含无损歌曲导出 (ATL.NET 读写闭环)
```

---

## 二、 结构化实施流水线与前后依赖图 (Milestone Roadmap)

为了杜绝 AI 乱序施工导致编译或运行时依赖缺失，所有任务**必须严格按照以下 4 个阶段依序推进**：

```text
[Milestone 1: 底层解耦与技术栈原生化] (彻底消灭 ffmpeg 依赖)
   Step 1.1: [Mod-01] 对接 libpulse-simple 录音
   Step 1.2: [Mod-03] 引入 StbImageSharp 并手写几何圆角算法
   Step 1.3: [Mod-02] 引入 ATL.NET 内存直读本地元数据与封面
   Step 1.4: [Mod-04] 清理 PKGBUILD，执行 Native AOT 编译验证
      │
      ▼
[Milestone 2: 核心播放稳定性与架构治理] (治本基石)
   Step 2.1: [Fix-01] 独立 PlaybackQueueService，隔离视图与待播队列
   Step 2.2: [Feat-04] 实现插队播放 (Play Next) 与队列抽屉
   Step 2.3: [Fix-02] GStreamer 150ms 软淡入淡出防爆音
   Step 2.4: [Fix-03] 弱网合盖重连断点看门狗
      │
      ▼
[Milestone 3: 私有云与视听交互新功能] (功能落地)
   Step 3.1: [Feat-02] G 键主列表内快速查找与行去重跳转
   Step 3.2: [Feat-01] WebDAV 远程音乐库与目录浏览 (基于 HttpClient+XDocument)
   Step 3.3: [Feat-03] Y 键歌词智能匹配升级 (依赖 M1 的准确 Duration)
      │
      ▼
[Milestone 4: 系统桌面级体验打磨] (细节雕琢)
   Step 4.1: [Enh-01] Linux 原生切歌通知 (D-Bus 弹窗，严格支持配置/快捷键关闭)
   Step 4.2: [Enh-02] 歌词微调 ([ / ]) 与偏好持久化
   Step 4.3: [Enh-03] 离线音频标签就地注入与自包含导出
```

---

## 三、 实施任务执行清单 (Checklist)

### 1. Bug 修复与隐患治理 (Bug Fixes)

- [x] **[Fix-01] 核心播放队列与浏览视图解耦治理 (P0 核心修复)**
  - [x] 问题根因：当前切歌强依赖 `_songListView.Songs`，用户浏览搜索结果或歌手页时，原歌单播放完毕会被错误篡改为新浏览页的歌曲。
  - [x] 治理方案：建立独立的 `PlaybackQueueService`，将活跃待播队列与临时渲染视图物理隔离，浏览任何页面均不破坏正在播放的歌单流。
- [x] **[Fix-02] 切歌瞬间数字爆音治理**
  - [x] 治理方案：在 GStreamer 管道中引入 `volume` 线性渐变控制，切歌瞬间在 150ms 内执行极速软淡出，切入新流后 150ms 软淡入，消除硬切引起的数字破音。
- [x] **[Fix-03] 弱网断线与笔记本合盖唤醒恢复缺陷治理**
  - [x] 治理方案：监听 GStreamer Buffer 枯竭与 Socket 断连事件，建立自动重连看门狗（Watchdog），支持最多 3 次带指数退避（1s, 2s, 4s）的微秒断点自动续播。
- [x] **[Fix-04] 子进程管道死锁与僵尸进程隐患治理**
  - [x] 治理方案：废除录音与元数据提取时使用 `Process.Start` 启动外部进程的方式，消除 SIGPIPE 信号丢失与管道阻塞导致的进程假死。

---

### 2. 功能修改与底层重构 (Refactoring & Modifications)

- [x] **[Mod-01] 录音模块原生重构：对接 `libpulse-simple.so.0`**
  - [x] 在 `Services/AudioRecordingService.cs` 中增加 PulseAudio Simple API 的 `[LibraryImport]` P/Invoke 声明。
  - [x] 后台非阻塞线程直接拉取 16000Hz S16LE 单声道 PCM，驱动层自动重采样，零外部子进程。
  - [x] 修改 `UI/AudioRecognitionDialog.cs` 报错文本（移除提示安装 ffmpeg 的说明）。
- [x] **[Mod-02] 本地音乐解析重构：迁移至纯托管 `ATL.NET`**
  - [x] 在 `QQMusic.Tui.csproj` 引入 `z440.atl.core`。
  - [x] 重写 `Services/LocalMusicService.cs`：内存直读 MP3/FLAC/M4A 头部 Tag 与精确 Duration，彻底淘汰 `ffprobe`。
  - [x] 通过 `Picture.Data` 内存直读内嵌封面字节流，淘汰 `ffmpeg -an -vcodec copy` 抽取逻辑。
- [x] **[Mod-03] 封面处理重构：自研纯 C# 几何圆角抗锯齿算法 + `StbImageSharp`**
  - [x] 引入 `StbImageSharp`（解码为裸 RGBA 数组）与 `StbImageWriteSharp`（编码为 PNG）。
  - [x] 在 `UI/TerminalImageHelper.cs` 实现纯 C# 几何圆角判定与抗锯齿平滑插值算法，内存就地修改 Alpha 通道。
  - [x] 移除 `magick` 外部系统调用及 `ffmpeg` fallback 转码分支。
- [x] **[Mod-04] 构建依赖清理：移除 `ffmpeg` 依赖**
  - [x] 修改 `PKGBUILD`、`build-pkg.sh`、`build-deb.sh`，移除 `optdepend = ffmpeg`。
  - [x] 执行 `dotnet publish -c Release -r linux-x64` 进行 Native AOT 全裁剪（`TrimMode=full`）编译验证。

---

### 3. 全新功能构建 (New Features)

- [ ] **[Feat-01] WebDAV 远程音乐库与目录浏览**
  - [ ] 编写 `Services/WebDavService.cs`：基于原生 `HttpClient` 发送 `PROPFIND`，通过 `XDocument` 解析目录树，支持跳过自签名证书。
  - [ ] 编写 `UI/WebdavManageDialog.cs`：站点新增/修改/测试连接模态弹窗。
  - [ ] 导航栏装配与视图模式流转（`ViewMode.WebDav`）：
    - 界面私有快捷键：`F`（管理站点）、`A`（导入当前目录至曲库）、`D`（目录树与平铺曲库无缝轮转）、`R`（增量刷新）。
    - 列表标题栏动态展示对应模式的按键提示（完全对标本地音乐规范）。
  - [ ] 接入 `AudioCacheService` 边播边存，向 GStreamer/D-Bus 传递本地缓存路径，彻底防止 NAS 密码在 D-Bus 广播中泄露。
- [x] **[Feat-02] 主列表内即时查找与行去重跳转 (G 键浮窗)**
  - [x] 全局单键绑定：在 `MainWindow.cs` 全局按键中捕获 `c == 'G'` 唤出/收起浮动查找窗。
  - [x] 悬浮居中面板装配：宽 52 列、高 3 行，翡翠绿半透明边框，内置 `TextField` 与匹配计数 `[ X / Y ]`。
  - [x] 行粒度去重算法：以歌曲条目行索引（`Row Index`）为唯一录入单位，遍历比对 `Title || Artist || Album`，单行内多字段命中绝不重复计次。
  - [x] 连续 Enter 环形跳转状态机：首次 Enter 定位第一项，后续 Enter 顺序跳转并在末尾折返，`Shift+Enter`/`Up` 反向跳跃，`Esc` 退出保留光标位置。
  - [x] 视口居中与平滑定位联动。
- [ ] **[Feat-03] 本地与 WebDAV 在线歌词智能匹配与升级 (Y 键)**
  - [ ] 按钮装配：大播放界面（`NowPlayingView.cs`）沉浸按钮旁常驻放置 `[ 匹配 (Y) ]`（仅本地/WebDAV 播放时显示）。
  - [ ] 精简检索词：仅以 `"{song.Title} {song.Artist}"` 检索，排除不规范 `Album` 字段干扰。
  - [ ] 核心防误匹配机制：比对候选歌曲时长与本地真实时长，优先匹配时长误差在 **$\pm 3\text{s}$ 以内** 的项；非 Live 歌曲自动过滤现场/演唱会版候选项。
  - [ ] 本地缓存持久化与可逆撤销（支持再次按 `Y` 恢复文件内嵌原始歌词）。
- [x] **[Feat-04] 插队播放 (Play Next) 与播放队列抽屉 (Queue Drawer)**
  - [x] 在列表条目上按快捷键 `N`，将选定歌曲直接插队至当前播放队列的下一顺位。
  - [x] 呼出独立播放队列抽屉，支持查看后续待播歌曲清单与移除指定条目。

---

### 4. 体验打磨与细节改进 (Enhancements & Improvements)

- [ ] **[Enh-01] Linux 原生桌面切歌通知 (D-Bus Notifications，严格支持可关闭)**
  - [ ] 纯 C# 原生调用 D-Bus `org.freedesktop.Notifications` 接口；
  - [ ] 切歌时弹出气泡，展示歌名、歌手、音质徽标及已缓存的封面缩略图；
  - [ ] **必须具备完全可关闭性**：
    - 在用户配置文件（`~/.config/qqmusic-tui/config.json`）中提供 `"enable_song_switch_notification": true/false`；
    - 支持 CLI 启动参数 `--no-notify` 快速静音覆盖；
    - 在快捷键或菜单中提供全局一键开/关切歌通知，避免在免打扰、全屏编码或打游戏时弹窗干扰。
- [ ] **[Enh-02] 歌词时间轴实时微调与偏好持久化**
  - [ ] 在大播放界面支持 `[`（歌词提前 0.5s）与 `]`（歌词延后 0.5s）实时调整；
  - [ ] 将微调的 offset 值持久化到本地歌曲配置，下次播放自动对齐。
- [ ] **[Enh-03] 离线音频标签就地回写与自包含无损歌曲导出**
  - [ ] 命中本地无损缓存后执行零开销文件复制；
  - [ ] 依托 `ATL.NET` 的原生写入能力（`track.Save()`），将云端拉取的高清封面、双语歌词与元数据就地写入文件头，导出全自包含的独立无损音频。

---

## 四、 关键技术契约与设计规范 (Contracts & Specifications)

### 1. 录音 P/Invoke 精确代码契约 (对应 Mod-01)

AI 编码时必须严格采用以下结构体内存布局，杜绝在 Native AOT 下发生段错误：

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct pa_sample_spec
{
    public int format;   // PA_SAMPLE_S16LE = 3
    public uint rate;    // 16000
    public byte channels;// 1
}

public enum pa_stream_direction_t
{
    PA_STREAM_NODIRECTION = 0,
    PA_STREAM_PLAYBACK = 1,
    PA_STREAM_RECORD = 2,
    PA_STREAM_UPLOAD = 3
}

// 采用 [LibraryImport("libpulse-simple.so.0")]
[LibraryImport("libpulse-simple.so.0", EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
public static partial IntPtr pa_simple_new(
    string? server,
    string name,
    pa_stream_direction_t dir,
    string? dev,
    string stream_name,
    in pa_sample_spec ss,
    IntPtr map,
    IntPtr attr,
    out int error);

[LibraryImport("libpulse-simple.so.0", EntryPoint = "pa_simple_read")]
public static partial int pa_simple_read(IntPtr s, byte[] data, nuint bytes, out int error);

[LibraryImport("libpulse-simple.so.0", EntryPoint = "pa_simple_free")]
public static partial void pa_simple_free(IntPtr s);
```

### 2. 图像处理与自研圆角算法规范 (对应 Mod-03)

- **调用链**：`输入字节 -> StbImageSharp 解码 -> 纯 C# 就地改 Alpha -> StbImageWriteSharp 写 PNG`；
- **几何抗锯齿核心公式**：
  ```csharp
  // 遍历四个角的 R x R 矩形，(cx, cy) 为该角内切圆心
  float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
  if (d > radius + 0.5f) {
      pixel[3] = 0; // 完全透明
  } else if (d > radius - 0.5f) {
      float alphaFactor = radius + 0.5f - d; // 平滑抗锯齿线性过渡
      pixel[3] = (byte)(pixel[3] * Math.Clamp(alphaFactor, 0f, 1f));
  }
  ```

### 3. 播放队列解耦架构规范 (对应 Fix-01 / Feat-04)

- `PlaybackQueueService` 是常驻单例，维护全局唯一的真实播放队列：
  - `IReadOnlyList<Song> ActiveSongs`: 正在播放的真实歌单；
  - `int CurrentIndex`: 当前播放的歌曲序号；
  - `int ShufflePointer`: 随机播放指针；
- **核心契约**：
  - `SetQueue(List<Song> songs, int startIndex)`: 仅在用户回车起播新列表时调用；
  - `InsertNext(Song song)`: 插队至 `CurrentIndex + 1`；
  - `GetNextSong()` / `GetPrevSong()`: 严格在 `ActiveSongs` 内部推演，严禁读取 `_songListView.Songs`。

### 4. Linux 桌面切歌通知与可关闭性规范 (对应 Enh-01)

- **通知触发前提**：
  ```csharp
  if (!UserConfig.Current.EnableSongSwitchNotification) return;
  ```
- **关闭途径**：
  1. 配置文件持久化：`~/.config/qqmusic-tui/config.json` -> `"enable_song_switch_notification": false`；
  2. 命令行快速覆盖：启动时带 `--no-notify`；
  3. 快捷键或设置弹窗：提供一键切换开关，保证不产生多余弹窗干扰。

---

## 五、 自动化验收门禁 (Verification Gate)

AI 在完成每个 Milestone 的任务后，**必须严格执行以下三道门禁检查**：

1. **增量语法与编译检查**：
   ```fish
   dotnet build
   ```
   - 预期结果：编译退出码为 0，零 Warning，零 Error。
2. **Native AOT 激进裁剪构建检查**：
   ```fish
   dotnet publish -c Release -r linux-x64 QQMusic.Tui.csproj
   ```
   - 预期结果：ILC 静态分析无反射丢失或裁剪警告，单一二进制产物生成成功。
3. **运行时日志核对**：
   - 检查 `/tmp/qqmusic_debug.log`，验证无未捕获异常或 SIGSEGV。
