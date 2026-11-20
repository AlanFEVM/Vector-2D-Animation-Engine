# Vector 2D 机制百科

使用 Astro Starlight 官方模板。首页介绍 Vector 2D 的作者与贡献、项目来历、定位、理念和当前阶段；工作区与界面词条包含界面说明与配图，其余分类保留待完善的目录。正文配图支持页内放大、实际大小查看与打开原图，按 Esc 或点击关闭返回原来的阅读位置。

外观参考用户提供的中文 Minecraft Wiki 截图及 Weird Gloop 的 Vector legacy 源码结构：深色分组侧栏、页面标签、浅蓝正文、目录和信息框。样式与 Astro 组件为本项目编写，未复制 Minecraft 素材或导入 MediaWiki PHP 后端。上游审阅地址为 https://github.com/weirdgloop/mediawiki-skins-Vector ，其许可为 GPL-2.0-or-later；当前静态站仍使用 Starlight。

在百科目录执行：

```powershell
npm.cmd ci
npm.cmd run build
npm.cmd run preview
```

本地入口为 `http://127.0.0.1:4321/`，手册位于 `/manual/`。`npm.cmd run preview` 启动本机编辑服务：百科正文支持普通编辑、Markdown 源码编辑、预览和保存，保存时重建静态页面与搜索索引。单独部署 `dist/` 或运行 `astro dev` 时不提供写入入口。

开发或构建前会从同级 `manual/` 同步手册和截图。手册原文件仍可单独离线阅读；从离线手册打开百科需要上述本地预览正在运行。

修改 `src/workspace-overview.mjs` 等 Markdown 处理插件后，请运行 `npm.cmd run build -- --force`，刷新内容集合缓存，使生成的目录与配图结构生效。
