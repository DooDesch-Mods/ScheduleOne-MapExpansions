/* MapExpansions editor docks. The mod holds all state; the page asks for it once and then re-renders on
 * `editor.changed`, which the mod pushes at most ten times a second and only when something moved. */

const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
};

function parse(raw) {
  if (!raw) return null;
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable editor state:', e.message); return null; }
}

const fmt = (v) => v ? v.map((n) => Number(n).toFixed(2)).join('  ') : '';

function renderToolbar(s) {
  const status = document.getElementById('status');
  status.textContent = s.dirty > 0 ? s.dirty + ' ungespeichert' : 'Alles gespeichert';
  status.className = s.dirty > 0 ? 'status changed' : 'status';
  document.getElementById('save').className = s.dirty > 0 ? 'tool primary' : 'tool';
}

function renderHierarchy(s) {
  const list = document.getElementById('list');
  list.replaceChildren();
  const selected = s.selected ? s.selected.key : null;
  let group = null;
  for (const c of s.copies) {
    const g = c.expansion + ' / ' + c.owner;
    if (g !== group) { group = g; list.appendChild(el('div', 'group', g)); }
    const row = el('div', c.key === selected ? 'item selected' : 'item');
    row.appendChild(el('div', 'name', c.id));
    if (c.dirty) row.appendChild(el('div', 'mark', 'geändert'));
    row.addEventListener('click', () => {
      const next = parse(s1.call('editor.select', c.key));
      if (next) render(next);
    });
    list.appendChild(row);
  }
  if (s.copies.length === 0) list.appendChild(el('div', 'group', 'Keine Kopien in dieser Welt.'));
}

function prop(box, k, v) {
  const row = el('div', 'prop');
  row.appendChild(el('div', 'k', k));
  row.appendChild(el('div', 'v', v));
  box.appendChild(row);
}

function renderInspector(s) {
  const box = document.getElementById('props');
  box.replaceChildren();
  const x = s.selected;
  if (!x) { box.appendChild(el('div', 'empty', 'Nichts ausgewählt.')); return; }
  prop(box, 'Id', x.id);
  prop(box, 'Erweiterung', x.expansion);
  prop(box, 'Ebene', x.owner);
  prop(box, 'Vorlage', x.template);
  if (x.onto) prop(box, 'Auf', x.onto);
  prop(box, 'Position', fmt(x.position));
  prop(box, 'Größe', fmt(x.size));
  prop(box, 'Status', x.dirty ? 'geändert' : 'gespeichert');
}

function renderConsole(s) {
  const box = document.getElementById('lines');
  box.replaceChildren();
  const lines = s.log || [];
  lines.forEach((l, i) => box.appendChild(el('div', i === lines.length - 1 ? 'l last' : 'l', l)));
  if (box.scrollToEnd) box.scrollToEnd();
}

function render(s) {
  if (!s) return;
  renderToolbar(s);
  renderHierarchy(s);
  renderInspector(s);
  renderConsole(s);
}

document.getElementById('save').addEventListener('click', () => render(parse(s1.call('editor.save', ''))));
document.getElementById('close').addEventListener('click', () => s1.call('editor.close', ''));

// A dock is a hit target only when the script listens to it; without this a click on an empty dock area would
// fall through to the world behind it.
for (const id of ['toolbar', 'hierarchy', 'inspector', 'console']) {
  document.getElementById(id).addEventListener('click', () => {});
}

s1.on('editor.changed', (payload) => render(parse(payload)));
render(parse(s1.call('editor.state', '')));
