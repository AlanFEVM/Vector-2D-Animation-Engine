# Codex / MCP 接口

在 `Settings → Codex / MCP` 启用 `Enable MCP integration / 启用 MCP 接口`，点 `Save` 后启动本地服务。默认关闭接口；`Allow editor changes / 允许修改编辑器` 默认关闭，启用后客户端才能切换工具、改设置、操作工程。

## 连接信息

- 服务地址默认 `http://127.0.0.1:43521/mcp/`；可改本地端口，设可选 Bearer 令牌。
- 连接只接受本机原生客户端，拒绝带浏览器 Origin 的请求；令牌不通过工具结果/配置文本/日志输出。
- 编辑器关闭时服务停止；重建或重启时连接短暂中断，窗口就绪后重新监听。服务不负责启动编辑器。
- 在 Codex 中添加 **Streamable HTTP** MCP 服务器，或把配置加入 `config.toml`：

```toml
[mcp_servers.vector2d]
url = "http://127.0.0.1:43521/mcp/"
# bearer_token_env_var = "VECTOR2D_MCP_TOKEN"
```

接口支持 JSON-RPC 2.0 与 MCP Streamable HTTP 的 JSON 响应模式，提供 `initialize`、`ping`、`tools/list`、`tools/call`、`resources/list`、`resources/read`。通知返回 HTTP 202；不提供 SSE 推送、资源订阅或任意脚本执行。工具参数/类型/范围都可通过 `tools/list` 发现。

## 核心工具

| 工具 | 作用 |
| --- | --- |
| `editor_get_state` | 读工作区/工具/帧/播放/选择/忙碌状态/修改权限；`render` 给最近帧耗时、D2D 命令/Present、缓存、GPU 状态 |
| `project_get_info` | 读工程 ID、保存路径、未保存标记、场景、元件 |
| `tools_get` / `tool_set_active` | 获取绘图工具及其可用性 / 切换工具 |
| `timeline_set_frame` / `timeline_set_playback` | 定位帧（0 起，须在播放范围内）/ 播放或停止 |
| `editor_undo` | 撤销当前上下文最近编辑 |
| `settings_get` / `settings_update` | 读设置 Schema / 部分更新并持久化语言、主题、HSB、格尺寸、Auto Key、背景色 |
| `project_open` / `project_save` / `project_new` | 打开/保存/新建工程 |
| `animation_import_bundle` | 从绝对 JSON 路径导入 SVG 帧、嵌套元件、实例动画，建新工程 |

## 图层 / 元件 / 场景工具

`workspace_set_active`（BasicDrawing/SceneEditor）、`layers_get`、`layer_create`/`layer_update`/`layer_set_active`/`layer_reorder`/`layer_remove`、`symbols_get`、`symbol_create`/`symbol_duplicate`/`symbol_rename`/`symbol_remove`/`symbol_set_active`、`scene_create`/`scene_rename`/`scene_remove`/`scene_set_active`、`scene_instances_get`、`scene_instance_create`/`scene_instance_remove`。

## 灯光 / 3D 工具

`scene_set_dimension`（TwoD/ThreeD）、`scene_lights_get`、`scene_light_create`（Directional/Point/Area/Ambient）、`scene_light_update`/`scene_light_remove`、`scene_instance_set_transform`、`scene_camera_get`/`scene_camera_update`、`scene_view_get`/`scene_view_update`。

## 使用要点

- 先 `project_get_info`/`layers_get`/`symbols_get` 读取 ID，再调用写入工具；名称用于显示，ID 用于定位。
- 场景相机与编辑器视角是两个独立接口：`scene_camera_update` 修改并保存相机数据，`scene_view_update` 只调参考视角、不标未保存；`scene_set_dimension` 保存场景维度。坐标用世界单位，灯光/实例旋转用角度，参考视角 `yaw`/`pitch` 用弧度；`scaleZ` 是挤出厚度（可为 0）。
- **暂不支持撤销**：元件/场景的创建、重命名、删除，以及相机参数和场景维度修改；删除前请保存工程。实例变换与灯光参数更新遵循当前帧/Auto Key/曝光规则。
- 自由 `yaw`/`pitch` 要求 3D 视图，不能与 `viewDirection` 同次调用。
- 所有编辑器工具在 UI 线程执行；拖拽/文本编辑/模态窗口打开期间返回忙碌错误。打开/新建遇未保存内容默认拒绝，应先保存或显式 `discardUnsaved`。

PowerShell 读取状态示例：

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:43521/mcp/' -Method Post `
  -ContentType 'application/json' `
  -Headers @{ Accept = 'application/json, text/event-stream' } `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"editor_get_state","arguments":{}}}'
```

`animation_import_bundle` 的版本 1 文件含 `version`/`name`/`fps`/`frameCount`/`width`/`height`/`symbols`/`scene`；SVG 帧须位于 JSON 目录内，不允许目录逃逸/重解析点/脚本/外部引用；导入完成仍需 `project_save` 存为原生 `.v2dProject`。
