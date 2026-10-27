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

const on = (id, active) => { document.getElementById(id).className = active ? 'tool active' : 'tool'; };

function renderToolbar(s) {
  const status = document.getElementById('status');
  status.textContent = s.dirty > 0 ? s.dirty + ' ungespeichert' : 'Alles gespeichert';
  status.className = s.dirty > 0 ? 'status changed' : 'status';
  document.getElementById('save').className = s.dirty > 0 ? 'tool primary' : 'tool';
  on('mode-move', s.mode === 'Move');
  on('mode-rotate', s.mode === 'Rotate');
  on('mode-scale', s.mode === 'Scale');
  on('snap-grid', s.grid);
  on('snap-turn', s.turn);
  on('snap-edges', s.edges);
  document.getElementById('undo').className = s.undo > 0 ? 'tool' : 'tool off';
  document.getElementById('redo').className = s.redo > 0 ? 'tool' : 'tool off';
  const apply = document.getElementById('apply');
  apply.textContent = s.pending > 0 ? 'Anwenden (' + s.pending + ')' : 'Anwenden';
  apply.className = s.pending > 0 ? 'tool primary' : 'tool off';
}

const call = (name, arg) => render(parse(s1.call(name, arg)));

function row(list, text, mark, active, cls, onClick) {
  const r = el('div', (cls || 'item') + (active ? ' selected' : ''));
  r.appendChild(el('div', 'name', text));
  if (mark) r.appendChild(el('div', 'mark', mark));
  r.addEventListener('click', onClick);
  list.appendChild(r);
}

const effectNames = { Hide: 'ausblenden', Remove: 'entfernen', NoCollision: 'ohne Kollision', Hollow: 'hohl', Cut: 'schneiden' };

function renderHierarchy(s) {
  const list = document.getElementById('list');
  list.replaceChildren();
  const selected = new Set(s.keys || []);
  let group = null;
  for (const c of s.copies) {
    const g = c.expansion + ' / ' + c.owner;
    if (g !== group) { group = g; list.appendChild(el('div', 'group', g)); }
    const row = el('div', selected.has(c.key) ? 'item selected' : 'item');
    row.appendChild(el('div', 'name', c.id));
    if (c.dirty) row.appendChild(el('div', 'mark', 'geändert'));
    row.addEventListener('click', () => {
      const next = parse(s1.call('editor.select', c.key));
      if (next) render(next);
    });
    list.appendChild(row);
  }
  if (s.copies.length === 0) list.appendChild(el('div', 'group', 'Keine Kopien in dieser Welt.'));
  const areaKey = s.area ? s.area.key : null;
  group = null;
  for (const r of s.rules || []) {
    const g = r.expansion + ' / Regeln';
    if (g !== group) { group = g; list.appendChild(el('div', 'group', g)); }
    const mark = r.pending ? 'wartet' : r.stale ? 'Neuladen' : (effectNames[r.effect] || r.effect);
    row(list, r.id, mark, r.key === areaKey, 'item', () => call('editor.area', r.key));
    for (let i = 0; i < r.openings; i++) {
      const k = r.key + '/opening/' + i;
      row(list, 'Öffnung ' + (i + 1), '', k === areaKey, 'item sub', () => call('editor.area', k));
    }
  }
  group = null;
  for (const b of s.boxes || []) {
    const g = b.expansion + ' / Boxen';
    if (g !== group) { group = g; list.appendChild(el('div', 'group', g)); }
    row(list, b.copy + ' / ' + b.id, b.kind === 'Block' ? 'Blocker' : 'Panel', b.key === areaKey, 'item', () => call('editor.area', b.key));
  }
}

function field(box, k, value, name) {
  const r = el('div', 'prop');
  r.appendChild(el('div', 'k', k));
  const input = el('input', 'field v');
  input.value = value || '';
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') call(name, e.value !== undefined ? e.value : input.value); });
  r.appendChild(input);
  box.appendChild(r);
}

function renderArea(box, a) {
  prop(box, 'Auswahl', a.label);
  if (a.min) { prop(box, 'Min', fmt(a.min)); prop(box, 'Max', fmt(a.max)); }
  if (a.effect) {
    prop(box, 'Seite', a.side === 'Main' ? 'Hauptwelt' : 'Erweiterung');
    const r = el('div', 'prop');
    r.appendChild(el('div', 'k', 'Wirkung'));
    const choices = el('div', 'choices v');
    for (const e of Object.keys(effectNames)) {
      const c = el('div', e === a.effect ? 'choice active' : 'choice', effectNames[e]);
      c.addEventListener('click', () => call('editor.rule.effect', e));
      choices.appendChild(c);
    }
    if (!effectNames[a.effect]) choices.appendChild(el('div', 'choice active', a.effect));
    r.appendChild(choices);
    box.appendChild(r);
    field(box, 'Name', a.name, 'editor.rule.name');
    field(box, 'Eltern', a.parent, 'editor.rule.parent');
    if (a.pending) prop(box, 'Status', 'wartet auf Anwenden');
    else if (a.stale) prop(box, 'Status', 'alte Form bis zum Neuladen');
  }
  if (a.boxKind) {
    prop(box, 'Art', a.boxKind === 'Block' ? 'Blocker' : 'Panel');
    prop(box, 'Kopie', a.copy);
    if (a.boxKind === 'Panel') {
      prop(box, 'Material', a.material);
      field(box, 'Suche', a.query, 'editor.materials');
      const mats = el('div', 'mats');
      for (const m of a.materials || []) {
        const r = el('div', m.name === a.material ? 'mat active' : 'mat');
        const sw = el('div', 'swatch');
        sw.style.backgroundColor = m.color;
        r.appendChild(sw);
        r.appendChild(el('div', 'name', m.name));
        r.addEventListener('click', () => call('editor.box.material', m.name));
        mats.appendChild(r);
      }
      box.appendChild(mats);
    }
  }
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
  if (s.area) { renderArea(box, s.area); return; }
  const x = s.selected;
  if (!x) { box.appendChild(el('div', 'empty', 'Nichts ausgewählt.')); return; }
  const n = (s.keys || []).length;
  if (n > 1) prop(box, 'Auswahl', n + ' Kopien');
  prop(box, 'Id', x.id);
  prop(box, 'Erweiterung', x.expansion);
  prop(box, 'Ebene', x.owner);
  prop(box, 'Vorlage', x.template);
  if (x.onto) prop(box, 'Auf', x.onto);
  prop(box, 'Position', fmt(x.position));
  prop(box, 'Drehung', Number(x.yaw).toFixed(1) + '°');
  prop(box, 'Skalierung', Number(x.scale).toFixed(2));
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
const act = (id, name, arg) => document.getElementById(id).addEventListener('click', () => render(parse(s1.call(name, arg))));
act('mode-move', 'editor.mode', 'Move');
act('mode-rotate', 'editor.mode', 'Rotate');
act('mode-scale', 'editor.mode', 'Scale');
act('snap-grid', 'editor.snap', 'grid');
act('snap-turn', 'editor.snap', 'turn');
act('snap-edges', 'editor.snap', 'edges');
act('undo', 'editor.undo', '');
act('redo', 'editor.redo', '');
document.getElementById('play').addEventListener('click', () => s1.call('editor.play', ''));
act('new-rule', 'editor.rule.new', 'Remove');
act('new-block', 'editor.box.new', 'Block');
act('new-panel', 'editor.box.new', 'Panel');
act('new-opening', 'editor.opening.new', '');
act('apply', 'editor.apply', '');

// A dock is a hit target only when the script listens to it; without this a click on an empty dock area would
// fall through to the world behind it.
for (const id of ['toolbar', 'hierarchy', 'inspector', 'console']) {
  document.getElementById(id).addEventListener('click', () => {});
}

s1.on('editor.changed', (payload) => render(parse(payload)));
render(parse(s1.call('editor.state', '')));
