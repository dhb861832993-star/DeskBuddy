# DeskBuddy 功能地图（权威索引）

> 版本：2026-10-08 · 每次新增功能必须更新本表 · 开发时先来这里定位

## 一、总览：五个用户可见的「页面/窗口」

```
┌────────────────────────────────────────────────────┐
│ 系统托盘 ☁（TrayService）                           │
│   右键：显示菜单 / 设置 / 编辑配置 / 重载 / 开机自启 / 退出 │
└────────────────────────────────────────────────────┘
        │ 双击 Alt（默认）呼出
        ▼
╔══════════════╗  ┌──────────────┐  ╔══════════════╗
║ 🧰 工具箱面板  ║  │   📌 主面板   │  ║ 📝 备忘录面板 ║
║ ToolsPanel   │  │   RootCard   │  │ MemoPanel    │
║（左·250px）  │◄─┤  启动器搜索框  ├─►│（右·310px）  │
╚══════════════╝  │  应用/文件网格 │  ╚══════════════╝
                  └──────┬───────┘
                         │ 底部 💬 AI 按钮 / 🔍 搜索结果顶部「问 AI」
                         ▼
              ╔════════════════╗      ╔═════════════════╗
              ║ 💬 AI 对话窗口   ║      ║ ⚙ 设置窗口        ║
              ║ ChatWindow     ║      ║ SettingsWindow  ║
              ╚════════════════╝      ╚═════════════════╝
                         │ F1（可改组合键）独立触发
                         ▼
              ╔══════════════╗    ┌──────────────┐
              ║ ✂ 截图覆盖层    ║───►║ 📌 贴图窗口    │
              ║SnipOverlayWin ║    │ PinWindow    │
              ╚══════════════╝    └──────────────┘
独立小窗：📝条目编辑器 ItemEditorWindow / ♻重命名 RenameDialogWindow / 📊索引进度 IndexProgressWindow
```

## 二、每页功能明细

### 1. 🧰 工具箱面板（主界面左侧）
| 代码 | `MainWindow.xaml` L299 `ToolsPanel` · `MainWindow.xaml.cs` `OnToolsToggle` |
|---|---|
| 开关 | 底部 🧰 图标（状态记忆：`ToolsEnabled`=`ToolsPanelOpen` 统一字段） |
| 内容 | **截图**（✂ 按钮 `SnipToolBtn` → `App.StartSnip()`） |
| 状态 | 每次呼出菜单按上次开合恢复 |

### 2. 📌 主面板启动器（中间）
| 代码 | `MainWindow.xaml` L359 `RootCard` |
|---|---|
| 搜索框 | `SearchBox` → 应用清单/文件搜索（`FileSearcher`） |
| 结果 | 应用网格 + 文件列表 + 顶部「问 AI」 |
| 呼出 | 双击修饰键 / 托盘 / MCP `show` |
| 隐藏 | Esc / 失焦自动隐藏 |

### 3. 📝 备忘录面板（右侧）
| 代码 | `MainWindow.xaml` L541 `MemoPanel` · `MemoArchives`（`.memo.json`） |
|---|---|
| 开关 | 底部 📝 图标（状态记忆同上） |
| 功能 | 待办三态(未完成/进行中/完成) · 气泡提示 · 拖拽排序 · 双击编辑 · 历史清理（设置里） |

### 4. 💬 AI 对话窗口
| 代码 | `ChatWindow.xaml(.cs)` · 服务 `HarnessClient`+`DshAuth`+`AiClient` |
|---|---|
| 模式 | **harness**（本机 DSH，带鉴权）/ openai（API key） |
| 会话 | 列表/新建/历史载入（`session/page` 分页） · 桌面分组「桌面助手」 |
| 流式 | 回答 token 流 · 工具调用状态 · 授权/提问交互面板 |
| 入口 | 主面板聊天按钮 · 搜索「问 AI」· 托盘 |

### 5. ⚙ 设置窗口（左侧目录五页签）
| 页签 | 代码 `Panel*` | 功能 |
|---|---|---|
| **通用** | `PanelGeneral` L313 | 呼出键捕获 · 双击间隔 · 主题 · 面板开关(与图标同步) · 备忘录清理 · **截图热键**（点按钮按组合键） |
| **搜索** | `PanelSearch` L409 | 文件搜索开关 · 根目录 · 后端(Windows Search/内置) |
| **AI** | `PanelAi` L438 | Harness 地址/会话策略 · 显示全部会话 · 或 OpenAI 模式(baseurl/key/model) |
| **MCP** | `PanelMcp` L478 | MCP 管道开关（AI 工具管理菜单） |
| **菜单项** | `PanelItems` L494 | 启动项增删改/分组 |

### 6. ✂ 截图系统（独立全屏，不依赖主面板）
| 代码 | `SnipOverlayWindow.xaml(.cs)` `PinWindow.cs` · 挂接 `App.StartSnip` |
|---|---|
| 触发 | 组合热键（默认 F1，设置→通用→截图热键自定义）· 工具箱 ✂ 按钮 |
| 流程 | 冻结双屏(每屏一窗,物理像素) → 拖选/8控制点/方向键微调 → 9×9 放大镜取色(HEX/RGB) |
| 操作条 | 选区右下 ●复制(蓝) ●保存(绿) ●贴图(橙)；Enter=复制 Ctrl+S=保存 Esc=取消 |
| 贴图 | `PinWindow`：置顶悬浮 · 淡绿渐变描边 · 拖动(零抖动) · 滚轮以鼠标为锚缩放 · 双击/Esc 关 |
| 输出 | 原始物理像素裁剪（4K 不糊） |

### 7. 独立小窗
| 窗口 | 用途 | 触发 |
|---|---|---|
| `ItemEditorWindow` | 启动项编辑（名称/路径/参数） | 主面板右键→编辑 |
| `RenameDialogWindow` | 重命名启动项 | 列表右键→重命名 |
| `IndexProgressWindow` | 文件索引进度 | 搜索根目录变更后重建 |

## 三、全局热键表（唯一入口 `App.OnGlobalKeyDown`）

| 键 | 功能 | 配置字段 |
|---|---|---|
| 双击 Alt（默认） | 呼出主面板 | `Hotkey` + `DoubleTapIntervalMs` |
| `SnipHotkey`（默认 F1） | 截图 | `SnipHotkey`（支持 Ctrl/Alt/Shift+任意键） |
| Esc（全局） | 退出当前层：截图→右键菜单→AI→编辑器→设置→主面板 | — |

## 四、数据/配置文件

| 文件 | 内容 | 位置 |
|---|---|---|
| `DeskBuddy.config.json` | 全部设置+启动项+面板状态 | `%LOCALAPPDATA%\Programs\QuickMenu` |
| `DeskBuddy.memo.json` | 备忘录数据 | 同上 |
| `~/.dsh/.credentials.yaml` | DSH 鉴权 secret（`DshAuth` 读） | 用户目录 |

## 五、开发接入点速查

| 要做 | 去哪 |
|---|---|
| 加启动器功能/面板 | `MainWindow.xaml`（三栏结构）+ `.xaml.cs` |
| 加设置项 | `SettingsWindow.xaml` 对应 `Panel*` 页签 + `.xaml.cs` 读写（605 行附近保存块） |
| 挂全局热键 | `App.xaml.cs` `OnGlobalKeyDown` + `ParseSnipHotkey` 模式 |
| 截图功能迭代 | `SnipOverlayWindow.xaml.cs`（覆盖层/选区/操作条） `RenderSelection/BuildAllAndShow` |
| 贴图迭代 | `PinWindow.cs`（`MovePhys`拖动 / `SetScale`锚点缩放） |
| AI 对话迭代 | `ChatWindow.xaml.cs` + `HarnessClient.cs`（`RpcAsync` 信封/`AskAsync` WS/`session/page` 分页） |
| DSH 鉴权 | `DshAuth.cs`（自签 cookie；注意 DSH 升级可能变协议） |
| 备忘录 | `MainWindow.xaml.cs`（Memo 相关 region）|
| 主题 | `Theme.cs` + 各窗 `ApplyTheme` |

## 六、构建/安装/调试

```powershell
cd H:\工作-deepseek\DeskBuddy
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish "src\DeskBuddy\DeskBuddy.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "dist\app"
# 杀进程→复制 dist\app\DeskBuddy.exe 到 %LOCALAPPDATA%\Programs\QuickMenu\ → 重启
# 调试：设 QM_DEBUG=1 启动 → 写 deskbuddy_trigger.txt（show/hide/snip/settings…）→看 deskbuddy_debug.log
# git：add → commit → push origin main
```

## 附：命名规范（以后新增沿用）
- 面板/容器：`XxxPanel` · 卡片：`XxxCard` · 按钮：`XxxBtn` · 输入：`XxxBox`
- 设置页签容器：`Panel<分类>`（General/Search/Ai/Mcp/Items）
- 服务：动词/名词直接英文（`KeyboardHook` `FileSearcher`）· 窗口：`XxxWindow`
- XAML 控件名=英文名；对用户展示名=中文（本表用「emoji 中文名」格式索引）