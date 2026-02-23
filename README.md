# VPet-AIGF  AI 女朋友插件

基于 [VPet-Simulator](https://github.com/LorisYounworker/VPet) 的 AI 伴侣插件，使用 **GLM-4.7** 大模型驱动，让桌面宠物拥有真实的 AI 灵魂。

##  功能一览

###  智能对话
基于 GLM 的自然语言对话，支持 Function Calling（插件可自动执行操作并把结果回传给模型）。

###  长期记忆（RAG）
所有聊天持久化到 `chat_history.json`，并构建 Embedding 向量索引（`embeddings.json`）用于余弦相似度检索（Top-50，阈值 0.35）。回退策略：若 embedding API 不可用，自动切换到关键词检索。

###  情绪与动画
- `show_emotion`：4 种说话表情（shy/serious/shining/self）
- `play_animation`：肢体/Idle 动画（bubbles/yawning/squat/boring/meow/tennis/think_happy/think_normal/think_sad/music 等）
- `react_to_emotion`：根据情绪值自动选择合适的窗口效果

###  图片发送与视觉理解
- 聊天窗输入栏右侧新增  按钮，选择本地图片后在输入框上方**实时预览**（含文件名），点击发送时图片+文字一并发出
- **支持 Ctrl+V 粘贴图片**：截图工具截图后、浏览器/QQ 复制图片后、文件管理器复制图片文件后，均可在聊天窗口直接 Ctrl+V 粘贴，自动挂起为待发送图片（预览区显示"📋 粘贴的图片"），输入文字后点击发送
- 图片消息以气泡形式显示在聊天记录中，重启后自动恢复
- 后端自动切换到 **glm-4v-flash** VLM 进行视觉理解，其他流程（RAG、工具、好感度计算）保持一致

###  ~~AI 图像生成~~（已移除）
- ~~原 `generate_image` 工具已移除~~

###  红包互动
- **主人给宠物发红包**：聊天窗口左下角🧧按钮，输入金额和祝福语，宠物收到后会感动回复
- **宠物给主人发红包**：AI 可调用 `give_money` 工具，从自己零花钱中扣款，以左侧粉红气泡显示

###  语音唤醒

- 启动后自动监听麦克风（使用 Windows 内置语音识别引擎，无需联网）
- 当检测到喊出**宠物名**时，自动播放桌宠动画 + 弹出/抖动聊天窗口 + 光标聚焦到输入框
- 5 秒冷却防止重复触发
- 支持中文语音识别（`zh-CN`），置信度阈值 0.5
- 录音指示器（输入栏右侧红点）可点击，立即停止语音识别
- 声纹注册后会自动保存，下次重启后自动识别“已注册”状态，无需重复注册

###  窗口互动特效
- `shake_window`：撒娇或情绪强烈时抖动窗口（自动恢复，冷却节流）
- `minimize_window`：短暂最小化后自动弹回
- `drag_window`：拖动窗口到特定位置，宠物同时移动到聊天窗边缘并播放 Raised_Dynamic 动画
- 上述特效均由 AI **自行生成行为描述**（`description` 参数），回退到随机预设文案；消息以  系统提示形式展示在聊天中，不显示函数名前缀

###  限时工作/学习/玩耍
`start_work / start_study / start_play` 支持 `duration_minutes` 参数，例如"玩一小时"传 60，插件会按指定分钟数倒计时并在到期时通知。

---

## 主要改动说明

### 1) RAG 向量检索

- Endpoint: `https://open.bigmodel.cn/api/paas/v4/embeddings`，Model: `embedding-3`，推荐 256 维
- 启动时增量更新 `embeddings.json`；新消息异步补充 embedding
- 验证：查看 `debug_log.txt` 中 `[RAG]` 前缀的行

### 2) Function Calling 参数速查

| 工具 | 新增参数 | 说明 |
|---|---|---|
| `feed_pet / give_drink / give_snack / give_gift / take_medicine` | `name` | 指定具体物品名，未命中则随机 |
| `start_work / start_study / start_play` | `name`, `duration_minutes` | 指定项目与时长（分钟） |
| `shake_window / minimize_window / drag_window` | `description` | AI 自生成行为描述，回退到随机池 |
| `give_money` | `amount`, `blessing` | 宠物给主人发红包（从零花钱扣款） |

### 3) 动画与情绪

- `show_emotion(emotion)`：说话表情，支持 `shy/serious/shining/self`
- `play_animation(animation)`：肢体/Idle 动画，如 `bubbles/yawning/squat/boring/meow/tennis/think_happy/think_normal/think_sad/music`

### 4) 自动弹窗与主动搭话

- 插件加载后自动弹出聊天窗口（略有延迟等待主窗口就绪）
- 主动搭话间隔 10~15 分钟，结合 RAG 上下文生成内容
- 关键事件写入 `debug_log.txt`（工具执行、RAG、动画、API 摘要等）

### 5) 语音唤醒

- 使用 `System.Speech.Recognition`（Windows 内置语音识别，**无需联网**，无需额外模型）
- 启动后自动监听默认麦克风，识别中文语音（`zh-CN`）
- 唤醒词 = 宠物名（`Save.Name` 或 `ChatName`），改名后自动重载语法
- 声纹注册：点击聊天窗口右下角“声纹”按钮，按提示朗读三句话即可。注册后只有你的声音能唤醒宠物。
- 重启后声纹状态会自动识别，无需重复注册。
- 触发效果：桌宠播放 Raised_Dynamic 动画 + 气泡说话 + 弹出/抖动聊天窗口 + 输入框自动聚焦
- 冷却时间 5 秒，置信度阈值 0.5
- 实现文件：`VoiceWakeup.cs`（独立模块，`IDisposable`）

---

## 编译与运行

```powershell
# 编译整个解决方案
dotnet build VPet.sln /p:Platform=x64

# 启动模拟器（调试用）
& "d:\VPET_GF\VPet-Simulator.Windows\bin\x64\Debug\net8.0-windows\VPet-Simulator.Windows.exe"
```

> 注意：模拟器运行时构建可能因文件锁定失败。建议先关闭模拟器再编译，或手动将 DLL 复制到 `mod/0000_core/plugin`。

---

## GLM-4 配置与模型/依赖放置路径

- **GLM API Key**
  - 插件通过 `https://open.bigmodel.cn/api/paas/v4/chat/completions` / `embeddings` 调用 GLM-4 与 embedding 接口。
  - 在运行前，务必在系统环境变量中设置：

    ```powershell
    $env:GLM_API_KEY = "YOUR_GLM_API_KEY"
    ```

  - 插件会从 `GLM_API_KEY` 读取 Key；若为空，则内部值为 `"YOUR_GLM_API_KEY"`，实际请求会被服务端拒绝。

- **插件 DLL 与运行目录**
  - 推荐放置路径：

    ```text
    VPet-Simulator.Windows/
      mod/
        0000_AIGF/
          info.lps
          plugin/
            VPet.Plugin.AIGF.dll
            load.lps
    ```

  - `AIPlugin` 会以 **`VPet.Plugin.AIGF.dll` 所在目录** 作为基准读写运行期文件：
    - `chat_history.json` / `embeddings.json`
    - `debug_log.txt` / `api_call_log.txt`
    - `config.json`
    - `voiceprint.json`

- **Whisper 语音模型**
  - 语音唤醒与 STT 使用 `Whisper.net`，模型文件名为：

    ```text
    ggml-tiny.bin
    ```

  - 默认放置路径（与插件 DLL 同目录）：

    ```text
    VPet-Simulator.Windows/mod/0000_AIGF/plugin/ggml-tiny.bin
    ```

  - 第一次运行时，若本地不存在该文件，插件会尝试通过 `WhisperGgmlDownloader` 自动下载。
  - 若自动下载失败，可根据 `debug_log.txt` 中 `[Whisper]` 日志提示，手动从 whisper.cpp 仓库或其镜像下载对应 GGML 模型，并重命名为 `ggml-tiny.bin` 放到上述目录。

---

## 调试与故障排查

- 日志位置：`debug_log.txt`（DLL 同目录）
- RAG：搜索 `[RAG]` 标签，可看到 embedding 构建、查询与匹配日志
- Embedding 缓存：`embeddings.json`，可删除后重建索引
- 若 embedding API 报错或不可用，系统会自动回退到关键词检索并在日志中记录

## 隐私与成本提示

- 聊天历史会被发送给第三方模型（智谱 AI）用于生成回复与 embedding，请注意隐私信息不要泄露到对话中。
- Embedding 与聊天的 API 调用都会产生请求计费，使用时请留意账户余额与配额。

> 提示：仓库根目录的 `.gitignore` 已默认忽略 `chat_history.json` / `embeddings.json` / `debug_log.txt` / `api_call_log.txt` / `voiceprint.json` 等运行期文件，适合直接开源本插件源码而不泄露本地数据。
