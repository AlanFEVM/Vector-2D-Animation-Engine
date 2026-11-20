import http from 'node:http';
import { createHash, randomUUID } from 'node:crypto';
import { readFile, rename, unlink, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import sirv from 'sirv';
import { fromMarkdown } from 'mdast-util-from-markdown';
import { wikiSections } from '../src/wiki-sections.mjs';

const root = new URL('../', import.meta.url);
const port = Number(process.env.WIKI_PORT || 4321);
const host = `127.0.0.1:${port}`;
const serve = sirv(fileURLToPath(new URL('dist/', root)), { dev: true });
const pages = new Map([['/', 'index.md'], ['/edit-guide/', 'edit-guide.md'], ['/entry-template/', 'entry-template.md']]);
for (const { slug } of wikiSections) pages.set(`/${slug}/`, `${slug}.md`);
const revision = (s) => createHash('sha256').update(s).digest('hex');
const run = promisify(execFile);
let busy = false;

function reply(res, code, data) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(data));
}

async function replace(file, value) {
  const temp = new URL(`.${randomUUID()}.tmp`, file);
  try { await writeFile(temp, value, { flag: 'wx' }); await rename(temp, file); }
  finally { await unlink(temp).catch(() => {}); }
}

function validMarkdown(node) {
  if (node.type === 'html' || (node.url && /^\s*(?:javascript|vbscript|data):/i.test(node.url))) return false;
  return !node.children || node.children.every(validMarkdown);
}

http.createServer(async (req, res) => {
  if (req.headers.host !== host) return reply(res, 403, { error: '仅允许本机访问。' });
  const url = new URL(req.url, `http://${host}`);
  if (url.pathname !== '/_wiki/article') return serve(req, res);
  const name = pages.get(url.searchParams.get('page'));
  if (!name) return reply(res, 404, { error: '此页面不可编辑。' });
  const file = new URL(`src/content/docs/${name}`, root);
  let locked = false;
  try {
    if (req.method !== 'GET') {
      if (req.method !== 'PUT' || req.headers.origin !== `http://${host}` || req.headers['content-type'] !== 'application/json') return reply(res, 403, { error: '请求来源无效。' });
      if (busy) return reply(res, 409, { error: '另一页正在保存，请稍后重试。' });
      busy = locked = true;
    }
    const original = await readFile(file, 'utf8');
    const header = /^---\r?\n[\s\S]*?\r?\n---\r?\n(?:\r?\n)?/.exec(original)?.[0];
    if (!header) throw Error('文档头格式异常。');
    if (req.method === 'GET') return reply(res, 200, { body: original.slice(header.length), revision: revision(original) });
    let bytes = 0; const chunks = [];
    for await (const chunk of req) {
      if ((bytes += chunk.length) > 512 * 1024) return reply(res, 413, { error: '正文过长。' });
      chunks.push(chunk);
    }
    let data;
    try { data = JSON.parse(Buffer.concat(chunks)); } catch { return reply(res, 400, { error: '数据格式错误。' }); }
    if (typeof data?.body !== 'string' || !validMarkdown(fromMarkdown(data.body))) return reply(res, 400, { error: '正文须为 Markdown，不支持嵌入 HTML 或脚本。' });
    if (data.revision !== revision(original) || await readFile(file, 'utf8') !== original) return reply(res, 409, { error: '词条已有其他修改。请保留草稿，重新打开后合并。' });
    const updated = header + data.body;
    if (updated !== original) {
      await replace(file, updated);
      try { await run(process.execPath, ['node_modules/astro/bin/astro.mjs', 'build'], { cwd: fileURLToPath(root), windowsHide: true }); }
      catch (error) {
        console.error(error.stderr || error.message);
        if (await readFile(file, 'utf8') === updated) await replace(file, original);
        throw Error('页面构建失败，请检查正文后重试。');
      }
    }
    reply(res, 200, { revision: revision(updated) });
  } catch (error) { console.error(error.message); reply(res, 500, { error: '保存或读取失败，请检查服务日志。' }); }
  finally { if (locked) busy = false; }
}).listen(port, '127.0.0.1', () => console.log(`Wiki: http://${host}/`));
