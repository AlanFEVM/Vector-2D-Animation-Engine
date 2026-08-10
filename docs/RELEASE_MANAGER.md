# Release Manager

[文档索引](README.md) · [当前用户指南](USER_GUIDE.md)

Release Manager 是源码工作区中的独立发布工具，用于管理版本号、双语 Release Notes、程序内公告可见性和正式单 EXE 发布。它使用与编辑器一致的 WinForms 主题与控件，但不会进入正式编辑器载荷。

## 启动

需要 `global.json` 指定的 .NET 8 SDK。在仓库根目录运行：

```powershell
scripts\run-release-manager.ps1
```

需要一个可复制的独立应用时运行：

```powershell
scripts\publish-release-manager.ps1
```

默认生成 `artifacts\release-manager-exe\ReleaseManager.exe` 及同目录的 `ReleaseManager.runtimeconfig.json`。这是源码管理工具的自包含 x64 应用，不会替换仓库根目录的编辑器开发启动器。

工具会自动定位当前仓库并读取：

- `Directory.Build.props`：当前源码和程序集版本。
- `release/release-notes.json`：全部版本的双语程序内公告。
- `scripts/publish-single-exe.ps1`：唯一正式发布入口。

## 管理版本

左侧版本列表按版本号从新到旧排列。选择版本后可编辑：

- `Version`：严格使用 `major.minor.patch` 三段数字格式。
- `Release date`：使用 `yyyy-MM-dd`。
- `Show in application`：控制该版本是否出现在程序内 Release Notes 查看器中。
- `English / 简体中文`：分别编辑标题、摘要、分区标题和分区条目。

`New version` 以当前最高版本的下一个补丁号建立草稿，并生成中英文占位内容。添加、删除和移动分区时会同步维护中英文结构；在条目编辑框中增加或删除行时，另一语言会补齐或移除对应位置的占位条目。两个语言必须拥有相同数量的分区和条目；标题、摘要、分区及条目都不能为空。草稿可以保存，但包含占位内容时不能正式发布。

`Delete` 只修改当前草稿状态，点击 `Save Version` 后才写入源码。关闭带 `*` 的窗口前会要求确认是否放弃未保存修改。

## 保存与显示

`Save Version` 会执行一次带校验与异常回滚的暂存保存：

1. 校验版本唯一性、日期和双语结构。
2. 把全部公告按版本号降序写回 `release/release-notes.json`。
3. 把所选版本设为当前源码版本，同时更新 `Version`、`AssemblyVersion`、`FileVersion` 和 `InformationalVersion`。
4. 回读两个文件；进程内任一步骤失败时尽力恢复保存前内容。

保存和发布前都会检查两个源码文件是否在工具打开后被其他程序修改；检测到外部变更时会拒绝覆盖或发布，并要求重新启动工具载入最新内容。发布脚本还会在构建开始与正式提交前核对管理器传入的两个 SHA-256，避免预览内容与实际打包内容不一致。两个文件分别替换，因此断电或进程被强制结束不属于跨文件原子事务保证范围。

程序只显示满足以下条件的公告：

- `Show in application` 已开启。
- 公告版本不高于当前程序集版本。

因此可以隐藏当前版本公告，也可以提前管理未来版本，而不会让较旧的正式包显示未来公告。`Preview` 使用正式编辑器的 `ReleaseNotesPanel` 渲染所选语言，预览结果与发布包内一致。

## 发布版本

进入 `Publish` 页选择输出目录。只有所选版本已经保存并等于当前源码版本时，`Publish selected version` 才可用。

发布流程会：

1. 校验源码版本、公告版本和四个程序集版本属性一致。
2. 构建编辑器并实际加载嵌入的 Release Notes 清单。
3. 构建引导程序，校验应用 DLL 与引导 EXE 的产品版本和文件版本。
4. 执行内嵌载荷自检并强制检查 EXE 小于 5 MiB。
5. 生成唯一的 `VectorAnimationEngine-<version>-win-x64.exe`，再校验路径、大小和 SHA-256。

默认输出为 `artifacts\release`。自定义输出目录必须为空；仓库根目录始终被拒绝，因为根目录 `VectorAnimationEngine.exe` 只属于源码开发启动器。

发布时编辑功能会锁定，输出和错误会实时显示在日志区。点击 `Cancel` 会发出协作式取消请求；当前构建或自检步骤完成后，脚本会在正式文件提交前停止。进入最终提交后不会强制中断，而是完成文件替换、校验和结果回传，避免留下部分提交的发布目录。新包完成全部校验前不会替换已有成功产物。

## 数据职责

- `release/release-notes.json` 是程序内双语公告的唯一数据源，并作为资源嵌入正式编辑器。
- `docs/RELEASE_NOTES_<version>.md` 是对应历史发布包的阅读文档，不由 Release Manager 自动覆盖。
- 正式发布始终通过 `scripts/publish-single-exe.ps1`，不得把其他构建结果复制到发布目录冒充正式包。
