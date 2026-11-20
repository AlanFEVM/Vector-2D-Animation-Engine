// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { wikiSections } from './src/wiki-sections.mjs';
import workspaceOverview from './src/workspace-overview.mjs';

export default defineConfig({
	server: { host: '127.0.0.1', port: 4321 },
	integrations: [
		starlight({
			title: 'Vector 2D 机制百科',
			favicon: '/vector2d.ico',
			defaultLocale: 'root',
			locales: { root: { label: '简体中文', lang: 'zh-CN' } },
			customCss: ['./src/styles/wiki.css'],
			components: {
				Header: './src/components/WikiHeader.astro',
				Sidebar: './src/components/WikiSidebar.astro',
				PageTitle: './src/components/WikiPageTitle.astro',
				TwoColumnContent: './src/components/WikiContent.astro',
			},
			sidebar: [
				{ label: '百科首页', slug: '' },
				{ label: '常用页面', items: wikiSections.map(({ title, slug }) => ({ label: title, slug })) },
				{ label: '词条模板', slug: 'entry-template' },
				{ label: '图文使用手册', link: '/manual/' },
			],
		}),
		{
			name: 'workspace-overview',
			hooks: {
				'astro:config:setup': ({ config }) => {
					config.markdown.processor.options.hastPlugins.push(workspaceOverview);
				},
			},
		},
	],
});
