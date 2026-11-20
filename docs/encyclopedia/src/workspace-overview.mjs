const text = (value) => ({ type: 'text', value });
const element = (tagName, properties, children) => ({ type: 'element', tagName, properties, children });
const textContent = (node) => node.type === 'text' ? node.value : (node.children ?? []).map(textContent).join('');

export default function workspaceOverview({ fileURL }) {
  if (!fileURL?.pathname.endsWith('/workspaces.md')) return;
  const headings = [];
  return {
    name: 'workspace-overview',
    element: {
      filter: ['h2', 'h3', 'h4'],
      visit(node) {
        const id = node.properties.id || `workspace-section-${headings.length + 1}`;
        headings.push({ id, label: textContent(node), level: Number(node.tagName.slice(1)) });
        return { ...node, properties: { ...node.properties, id } };
      },
    },
    after(tree, context) {
      const figures = new Map([
        ['/images/workspace-charcoal-annotated.png', '碳灰主题下的界面布局'],
      ]);
      for (const [imagePath, fallbackCaption] of figures) {
        const imageIndex = tree.children.findIndex((node) => node.tagName === 'p'
          && node.children?.some((child) => child.tagName === 'a' && child.properties.href === imagePath));
        if (imageIndex < 0) continue;
        const paragraph = tree.children[imageIndex];
        const caption = tree.children[imageIndex + 2]?.tagName === 'p' ? tree.children[imageIndex + 2] : undefined;
        context.replaceNode(paragraph, element('figure', { className: ['wiki-layout-figure', 'not-content'] }, [
          ...paragraph.children,
          element('figcaption', {}, caption ? [...caption.children] : [text(fallbackCaption)]),
        ]));
        if (caption) context.removeNode(caption);
      }
      const menuFigures = [
        ['/images/main-menu-and-project-name.png', '主菜单与工程名称'],
        ['/images/file-menu-example.png', '“文件”菜单展开示例'],
      ].map(([src, caption]) => {
        const paragraph = tree.children.find((node) => node.tagName === 'p'
          && node.children?.some((child) => child.tagName === 'img' && child.properties.src === src));
        const index = tree.children.indexOf(paragraph);
        const captionNode = index >= 0 && tree.children[index + 2]?.tagName === 'p' ? tree.children[index + 2] : undefined;
        return { src, caption, paragraph, captionNode };
      });
      for (const { src, caption, paragraph, captionNode } of menuFigures) {
        if (!paragraph) continue;
        context.replaceNode(paragraph, element('aside', {
          className: ['wiki-section-images', 'not-content'],
          ariaLabel: caption,
        }, [element('figure', {}, [
          element('a', { href: src, target: '_blank', rel: ['noopener'], ariaLabel: `查看原图：${caption}` }, [
            element('img', { src, alt: caption, decoding: 'async' }, []),
          ]),
          element('figcaption', {}, [text(caption)]),
        ])]));
        if (captionNode) context.removeNode(captionNode);
      }
      const toolbarImagePath = '/images/tool-palette.png';
      const toolbarParagraph = tree.children.find((node) => node.tagName === 'p'
        && node.children?.some((child) => child.tagName === 'img' && child.properties.src === toolbarImagePath));
      if (toolbarParagraph) {
        const toolbarIndex = tree.children.indexOf(toolbarParagraph);
        const toolbarCaption = tree.children[toolbarIndex + 2]?.tagName === 'p'
          ? tree.children[toolbarIndex + 2]
          : undefined;
        context.replaceNode(toolbarParagraph, element('aside', {
          className: ['wiki-section-images', 'wiki-tool-images', 'not-content'],
          ariaLabel: '工具栏示例',
        }, [element('figure', {}, [
          element('a', { href: toolbarImagePath, target: '_blank', rel: ['noopener'], ariaLabel: '查看原图：左侧工具栏' }, [
            element('img', { src: toolbarImagePath, alt: '左侧工具栏', decoding: 'async' }, []),
          ]),
          element('figcaption', {}, toolbarCaption ? [...toolbarCaption.children] : [text('左侧工具栏')]),
        ])]));
        if (toolbarCaption) context.removeNode(toolbarCaption);
      }
      const sectionFigures = [
        { src: '/images/stage-area.png', caption: '全景图中的舞台区域', className: 'wiki-stage-images' },
        { src: '/images/scene-canvas-start-detail.png', caption: '场景画布设置入口', className: 'wiki-canvas-images' },
        { src: '/images/scene-canvas-dialog.png', caption: '画布与背景设置窗口', className: 'wiki-canvas-images' },
        { src: '/images/stage-context-menu.png', caption: '舞台右键菜单', className: 'wiki-menu-images' },
        { src: '/images/stage-context-menu-selection.png', caption: '选中对象后的舞台右键菜单', className: 'wiki-menu-images' },
        { src: '/images/stage-view-controls.png', caption: '网格、吸附与视图控制', className: 'wiki-controls-images' },
        { src: '/images/grid-cartesian.png', caption: '直角坐标网格', className: 'wiki-grid-images' },
        { src: '/images/grid-golden-spiral.png', caption: '黄金螺旋网格', className: 'wiki-grid-images' },
        { src: '/images/grid-polar.png', caption: '极坐标网格', className: 'wiki-grid-images' },
        { src: '/images/properties-panel.png', caption: '文本工具的属性子页面' },
        { src: '/images/timeline-panel.png', caption: '图层与时间轴', className: 'wiki-timeline-images' },
        { src: '/images/workspace-tabs.png', caption: '工作区与编辑范围切换' },
      ];
      for (const { src, caption, className } of sectionFigures) {
        const paragraph = tree.children.find((node) => node.tagName === 'p'
          && node.children?.some((child) => child.tagName === 'img' && child.properties.src === src));
        if (!paragraph) continue;
        const index = tree.children.indexOf(paragraph);
        const captionNode = tree.children[index + 2]?.tagName === 'p' ? tree.children[index + 2] : undefined;
        context.replaceNode(paragraph, element('aside', {
          className: ['wiki-section-images', ...(className ? [className] : []), 'not-content'],
          ariaLabel: caption,
        }, [element('figure', {}, [
          element('a', { href: src, target: '_blank', rel: ['noopener'], ariaLabel: `查看原图：${caption}` }, [
            element('img', { src, alt: caption, decoding: 'async' }, []),
          ]),
          element('figcaption', {}, captionNode ? [...captionNode.children] : [text(caption)]),
        ])]));
        if (captionNode) context.removeNode(captionNode);
      }
      const intro = tree.children.find((node) => node.tagName === 'p');
      if (!intro) return;
      const groups = [];
      let subsection;
      for (const heading of headings) {
        if (heading.level === 2 || !groups.length) {
          groups.push({ ...heading, children: [] });
          subsection = undefined;
        } else if (heading.level === 3) {
          subsection = { ...heading, children: [] };
          groups.at(-1).children.push(subsection);
        } else if (['舞台', '属性面板'].includes(subsection?.id)) {
          subsection.children.push(heading);
        }
      }
      const tocItem = ({ id, label, children = [] }) => element('li', {}, [
        element('a', { href: `#${id}` }, [text(label)]),
        ...(children.length ? [element('ol', {}, children.map(tocItem))] : []),
      ]);

      const images = [
        { src: '/manual/images/workspace.png', width: 1440, height: 920, caption: '基础绘制工作区' },
        { src: '/images/engine-startup.png', width: 620, height: 320, caption: '引擎启动画面' },
      ];
      const overview = element('div', { className: ['wiki-workspace-overview', 'not-content'], dataPagefindIgnore: true }, [
        element('nav', { className: ['wiki-article-toc'], ariaLabel: '本页目录' }, [
          element('details', { open: true }, [
            element('summary', {}, [text('目录')]),
            element('ol', {}, groups.map(tocItem)),
          ]),
        ]),
        element('aside', { className: ['wiki-article-images'], ariaLabel: '工作区与启动画面' }, images.map(({ src, width, height, caption }) =>
          element('figure', {}, [
            element('a', { href: src, target: '_blank', rel: ['noopener'], ariaLabel: `查看原图：${caption}` }, [
              element('img', { src, alt: caption, width, height, decoding: 'async' }, []),
            ]),
            element('figcaption', {}, [text(caption)]),
          ]),
        )),
      ]);
      context.insertAfter(intro, overview);
    },
  };
}
