# 0.2.7 — 2026-10-27

## 中文

- 单文件工程保存失败时恢复图片与 SVG 资源路径，允许解除文件占用后重试保存。
- 从 `.v2d` 另存为文件夹工程失败后，保留原保存目标及未保存状态，避免后续保存误写内部工作区。
- 选中图层或灯光补间自动显示“属性”中的曲线，相机补间继续显示“相机”曲线。
- 转换为元件接受顶部连续对象，保留与未选图形及已有嵌套实例的遮挡关系。
- 遮挡填色切换透明度后，重新计算可见选框，避免复用旧的不透明遮挡结果。
- 发布成功后的备份清理失败不再回滚新包；恢复失败保留可恢复的旧产物备份。
- 正式启动器检查关键运行时文件非空，并识别缺失的 `hostpolicy.dll`。

## English

- Failed package saves restore image and SVG source bindings so saving can be retried.
- Failed folder Save As preserves the original `.v2d` target and unsaved state.
- Layer and light tween selection reveals Properties; camera tween curves remain in Camera.
- Convert to Symbol accepts the topmost contiguous local objects and preserves stacking relative to existing instances.
- Selection outlines refresh when covering paint switches between opaque and transparent.
- Post-commit backup cleanup failures do not roll back new releases; incomplete rollback retains recoverable backups.
- Distribution startup checks required runtime files are nonempty, including `hostpolicy.dll`.

## 验收说明 / Acceptance

本文件记录源码变更，不代表所有设备、驱动和输入均无缺陷。具体执行的构建、回归、视觉检查与正式产物信息见本次发布验收报告。

These notes describe source changes, not a guarantee across every device, driver or input. See the release acceptance report for executed checks and package details.
