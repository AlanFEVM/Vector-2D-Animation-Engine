import { defaultMarkdownParser as parser, defaultMarkdownSerializer as serializer, schema } from 'prosemirror-markdown';
import { EditorState } from 'prosemirror-state';
import { EditorView } from 'prosemirror-view';
import { baseKeymap, setBlockType, toggleMark } from 'prosemirror-commands';
import { history, undo, redo } from 'prosemirror-history';
import { keymap } from 'prosemirror-keymap';
import { DOMSerializer } from 'prosemirror-model';
import 'prosemirror-view/style/prosemirror.css';

export async function openEditor(api: string, initial: string) {
  const dialog = document.querySelector<HTMLDialogElement>('#wiki-editor')!;
  if (dialog.open) return;
  const get = <T extends HTMLElement>(s: string) => dialog.querySelector<T>(s)!;
  const source = get<HTMLTextAreaElement>('[data-source]');
  const rich = get('[data-rich]'), preview = get('[data-preview]'), format = get('[data-format]');
  const status = get('output'), save = get<HTMLButtonElement>('[data-save]');
  let mode = '', body = '', original = '', revision = '', richChanged = false, saving = false;
  let view: EditorView | undefined;
  const events = new AbortController();
  const opts = { signal: events.signal };
  const current = () => mode === 'source' ? source.value : mode === 'rich' && richChanged && view ? serializer.serialize(view.state.doc) + '\n' : body;
  const dirty = () => !!revision && current() !== original;
  const close = () => { if (!saving && (!dirty() || confirm('放弃本次未保存的修改？'))) dialog.close(); };
  function setMode(next: string) {
    if (mode === next) return;
    body = current(); mode = next; richChanged = false;
    view?.destroy(); view = undefined;
    source.hidden = next !== 'source'; rich.hidden = format.hidden = next !== 'rich'; preview.hidden = next !== 'preview';
    dialog.querySelectorAll<HTMLButtonElement>('[data-mode]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.mode === next)));
    if (next === 'source') { source.value = body; source.focus(); return; }
    const doc = parser.parse(body);
    if (next === 'preview') { preview.replaceChildren(DOMSerializer.fromSchema(schema).serializeFragment(doc.content)); return; }
    const bold = toggleMark(schema.marks.strong), italic = toggleMark(schema.marks.em);
    view = new EditorView(rich, {
      state: EditorState.create({ doc, plugins: [history(), keymap({'Mod-z':undo,'Mod-y':redo,'Mod-Shift-z':redo,'Mod-b':bold,'Mod-i':italic}),keymap(baseKeymap)] }),
      attributes: { role:'textbox', 'aria-label':'词条正文', 'aria-multiline':'true' },
      dispatchTransaction(tr) { if (tr.docChanged) richChanged = true; this.updateState(this.state.apply(tr)); },
    });
    view.focus();
  }
  dialog.addEventListener('cancel', e => { e.preventDefault(); close(); }, opts);
  dialog.addEventListener('close', () => { events.abort(); view?.destroy(); }, {once:true});
  window.addEventListener('beforeunload', e => { if (dirty()) e.preventDefault(); }, opts);
  dialog.addEventListener('click', async e => {
    const b = (e.target as Element).closest('button'); if (!b || saving) return;
    if (b.matches('[data-close],[data-cancel]')) return close();
    if (!revision) return;
    if (b.dataset.mode) return setMode(b.dataset.mode);
    if (view && b.dataset.command) {
      const key = b.dataset.command;
      const cmd = key === 'strong' || key === 'em' ? toggleMark(schema.marks[key]) : key === 'heading' ? setBlockType(schema.nodes.heading,{level:2}) : setBlockType(schema.nodes.paragraph);
      cmd(view.state,view.dispatch); view.focus();
    }
    if (b === save) {
      const content = current(); saving = save.disabled = true; status.textContent = '正在保存并更新页面…';
      source.readOnly = true; view?.setProps({ editable: () => false });
      try {
        const r = await fetch(api,{ method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({body:content,revision}) });
        const result = await r.json(); if (!r.ok) throw Error(result.error);
        original = content; dialog.close(); location.reload();
      } catch (error) { status.textContent = String(error); }
      finally { saving = save.disabled = source.readOnly = false; view?.setProps({editable:()=>true}); }
    }
  }, opts);
  source.hidden = rich.hidden = format.hidden = preview.hidden = true;
  save.disabled = true; status.textContent = '正在读取…'; dialog.showModal();
  try {
    const response = await fetch(api,{signal:events.signal}); const data = await response.json();
    if (!response.ok) throw Error(data.error);
    body = original = data.body; revision = data.revision; status.textContent = ''; save.disabled = false; setMode(initial);
  } catch (error) { if (!events.signal.aborted) status.textContent = String(error); }
}
