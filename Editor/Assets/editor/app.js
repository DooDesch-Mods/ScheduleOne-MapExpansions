/* MapExpansions editor docks. The mod holds all editor state; the page asks for it once and then re-renders on
 * `editor.changed`, which the mod pushes at most ten times a second and only when something changed. What only
 * the page cares about (open groups, the filter, the left tab, the log pane) lives in `ui` below. */

const ui = {
  tab: 'scene',
  leftOpen: true,
  logOpen: false,
  filter: '',
  open: { copies: true, rules: false, boxes: true, points: true },
  pointMenu: false,
  editing: false,
  last: null,
};

// The tree is rebuilt on every change, so a long unfiltered group shows its head only.
const MAX_ROWS = 120;

const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
};

const icon = (name, cls) => {
  const img = el('img', cls ? 'ico ' + cls : 'ico');
  img.src = 'i-' + name + '.png';
  return img;
};

function parse(raw) {
  if (!raw) return null;
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable editor state:', e.message); return null; }
}

const call = (name, arg) => render(parse(s1.call(name, arg === undefined ? '' : String(arg))));

const num = (v, digits) => Number(v).toFixed(digits).replace('.', ',');
const size = (v) => v ? v.map((n) => num(n, 2)).join(' × ') + ' m' : '';

/* Tooltips: one floating box, placed under or over the hovered control. */

let tipNode = null;
function hideTip() { if (tipNode) { tipNode.remove(); tipNode = null; } }

function tip(anchor, text, key) {
  anchor.addEventListener('mouseenter', () => {
    hideTip();
    const r = anchor.rect ? anchor.rect() : anchor.getBoundingClientRect();
    if (!r || !r.height) return;
    const node = el('div', 'tip');
    node.appendChild(el('div', '', text));
    if (key) node.appendChild(el('div', 'key', key));
    const w = Math.min(320, 22 + (text.length + (key ? key.length + 2 : 0)) * 6.6);
    const page = document.getElementById('editor');
    const pr = page.rect ? page.rect() : page.getBoundingClientRect();
    let left = r.x + r.width / 2 - w / 2;
    if (left < 6) left = 6;
    if (pr && pr.width && left + w > pr.width - 6) left = pr.width - 6 - w;
    const below = r.y < 80;
    node.style.left = Math.round(left) + 'px';
    node.style.top = Math.round(below ? r.y + r.height + 6 : r.y - 32) + 'px';
    node.style.width = Math.round(w) + 'px';
    document.body.appendChild(node);
    tipNode = node;
  });
  anchor.addEventListener('mouseleave', hideTip);
  anchor.addEventListener('click', hideTip);
}

/* A toolbar button: icon, optional label and value, a tooltip with the key. */
function btn(parent, o) {
  const b = el('div', 'btn' + (o.label || o.val ? '' : ' icon') + (o.cls ? ' ' + o.cls : ''));
  b.appendChild(icon(o.icon));
  if (o.label) b.appendChild(el('div', '', o.label));
  if (o.val) b.appendChild(el('div', 'val', o.val));
  if (o.badge) b.appendChild(el('div', 'badge', o.badge));
  if (o.tip) tip(b, o.tip, o.key);
  b.addEventListener('click', o.click);
  parent.appendChild(b);
  return b;
}

function fill(id) {
  const box = document.getElementById(id);
  box.replaceChildren();
  return box;
}

function renderToolbar(s) {
  const file = fill('file-tools');
  btn(file, {
    icon: 'save', cls: s.dirty > 0 ? 'primary' : '', label: s.dirty > 0 ? 'Speichern' : '',
    badge: s.dirty > 0 ? s.dirty : '', tip: s.dirty > 0 ? s.dirty + ' Änderungen speichern' : 'Alles gespeichert',
    key: 'Strg+S', click: () => call('editor.save'),
  });
  btn(file, { icon: 'undo', cls: s.undo > 0 ? '' : 'off', tip: 'Rückgängig', key: 'Strg+Z', click: () => call('editor.undo') });
  btn(file, { icon: 'redo', cls: s.redo > 0 ? '' : 'off', tip: 'Wiederholen', key: 'Strg+Y', click: () => call('editor.redo') });

  const mode = fill('mode-tools');
  for (const [m, ic, name, key] of [['Move', 'move', 'Verschieben', 'W'], ['Rotate', 'rotate', 'Drehen', 'E'], ['Scale', 'scale', 'Skalieren', 'R']])
    btn(mode, { icon: ic, cls: s.mode === m ? 'on' : '', tip: name, key, click: () => call('editor.mode', m) });

  const snap = fill('snap-tools');
  btn(snap, { icon: 'grid', val: '0,5 m', cls: s.grid ? 'on' : '', tip: 'Am Raster einrasten', click: () => call('editor.snap', 'grid') });
  btn(snap, { icon: 'turn', val: '15°', cls: s.turn ? 'on' : '', tip: 'In 15°-Schritten drehen', click: () => call('editor.snap', 'turn') });
  btn(snap, { icon: 'edges', cls: s.edges ? 'on' : '', tip: 'An Kanten anderer Objekte einrasten', click: () => call('editor.snap', 'edges') });

  const add = fill('add-tools');
  btn(add, { icon: 'rule', label: 'Entfernen', tip: 'Regel, die Objekte im Bereich entfernt', click: () => call('editor.rule.new', 'Remove') });
  btn(add, { icon: 'block', label: 'Blocker', tip: 'Unsichtbare Wand vor der Fläche im Blick', click: () => call('editor.box.new', 'Block') });
  btn(add, { icon: 'panel', label: 'Panel', tip: 'Sichtbare Platte vor der Fläche im Blick', click: () => call('editor.box.new', 'Panel') });
  btn(add, { icon: 'opening', label: 'Öffnung', tip: 'Durchgang ins ausgehöhlte Objekt im Blick', click: () => call('editor.opening.new') });
  btn(add, {
    icon: 'apply', label: 'Anwenden', badge: s.pending > 0 ? s.pending : '', cls: s.pending > 0 ? 'primary' : 'off',
    tip: s.pending > 0 ? s.pending + ' Regeln neu bauen' : 'Keine Regel wartet', click: () => call('editor.apply'),
  });

  const end = fill('end-tools');
  btn(end, { icon: 'play', label: 'Spielen', tip: 'Ab der Kamera spielen', key: 'Tab', click: () => s1.call('editor.play', '') });
  btn(end, { icon: 'close', tip: 'Editor schließen', key: 'F4', click: () => s1.call('editor.close', '') });
}

/* Left dock: scene tree and catalog */

const effects = {
  Hide: ['hide', 'Ausblenden'], Remove: ['remove', 'Entfernen'], NoCollision: ['nocollision', 'Ohne Kollision'],
  Hollow: ['hollow', 'Aushöhlen'], Cut: ['cut', 'Schneiden'],
};
const pointKinds = {
  locations: ['location', 'Ort'], dealLocations: ['deal', 'Deal-Ort'], parkingLots: ['parking', 'Parkplatz'],
  buildings: ['building', 'Gebäude'], regions: ['region', 'Region'], shops: ['shop', 'Laden'],
};
const copyIcon = (c) => c.kind === 'model' ? 'model' : c.kind === 'prefab' ? 'prefab' : 'copy-kind';

function renderTabs(s) {
  const tabs = fill('left-tabs');
  const total = s.copies.length + (s.rules || []).length + (s.boxes || []).length + (s.points || []).length;
  for (const [id, ic, name, count] of [['scene', 'source', 'Szene', total], ['catalog', 'prefab', 'Katalog', '']]) {
    const t = el('div', ui.tab === id ? 'tab active' : 'tab');
    t.appendChild(icon(ic));
    t.appendChild(el('div', '', name));
    if (count !== '') t.appendChild(el('div', 'count', count));
    t.addEventListener('click', () => { ui.tab = id; render(ui.last); });
    tabs.appendChild(t);
  }
  tabs.appendChild(el('div', 'spacer'));
  btn(tabs, { icon: 'collapse', tip: 'Leiste einklappen', click: () => { ui.leftOpen = false; render(ui.last); } });
  document.getElementById('scene').className = ui.tab === 'scene' ? 'pane' : 'pane hidden';
  document.getElementById('catalog').className = ui.tab === 'catalog' ? 'pane' : 'pane hidden';

  const left = document.getElementById('left');
  const rail = document.getElementById('left-collapsed');
  left.className = ui.leftOpen ? 'side dock' : 'side dock hidden';
  rail.className = ui.leftOpen ? 'collapsed dock hidden' : 'collapsed dock';
  rail.replaceChildren();
  btn(rail, { icon: 'expand', tip: 'Leiste ausklappen', click: () => { ui.leftOpen = true; render(ui.last); } });
  btn(rail, { icon: 'source', tip: 'Szene', click: () => { ui.leftOpen = true; ui.tab = 'scene'; render(ui.last); } });
  btn(rail, { icon: 'prefab', tip: 'Katalog', click: () => { ui.leftOpen = true; ui.tab = 'catalog'; render(ui.last); } });
}

function groupHead(tree, id, name, count, extra) {
  const filtering = ui.filter.length > 0;
  const open = filtering || ui.open[id];
  const h = el('div', 'ghead');
  h.appendChild(icon(open ? 'down' : 'right', 'small muted'));
  h.appendChild(el('div', 'title', name));
  h.appendChild(el('div', 'count', count));
  h.appendChild(el('div', 'spacer'));
  if (extra) extra(h);
  h.addEventListener('click', () => { ui.open[id] = !ui.open[id]; render(ui.last); });
  tree.appendChild(h);
  return open;
}

function item(tree, o) {
  const r = el('div', 'item' + (o.selected ? ' selected' : '') + (o.deep ? ' deep' : ''));
  r.appendChild(icon(o.icon, 'small'));
  if (o.sub) {
    const l = el('div', 'label');
    l.appendChild(el('div', 'name', o.name));
    l.appendChild(el('div', 'sub', o.sub));
    r.appendChild(l);
  } else {
    r.appendChild(el('div', 'name', o.name));
  }
  if (o.tag) r.appendChild(el('div', o.warn ? 'tag warn' : 'tag', o.tag));
  if (o.dirty) r.appendChild(el('div', 'dot'));
  r.addEventListener('click', o.click);
  tree.appendChild(r);
}

const matches = (text) => !ui.filter || String(text).toLowerCase().indexOf(ui.filter) >= 0;

const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

// The expansion's own layer first, then the shops and other layers by name.
function byGroupThenName(a, b) {
  return cmp(a.expansion, b.expansion)
    || (a.owner === 'expansion' ? 0 : 1) - (b.owner === 'expansion' ? 0 : 1)
    || cmp(a.owner, b.owner)
    || cmp(a.id.toLowerCase(), b.id.toLowerCase());
}

const sideName = (side) => (side === 'Main' ? 'Hauptwelt' : 'Erweiterung');

/* Where a rule looks: the parent it needs, and whether a box limits it. */
function ruleWhere(r) {
  const parts = [];
  if (r.parent) parts.push('in ' + r.parent);
  if (r.bounded) parts.push('mit Bereich');
  return parts.join(', ');
}

function capped(tree, list, each) {
  const shown = ui.filter ? list : list.slice(0, MAX_ROWS);
  shown.forEach(each);
  if (shown.length < list.length) tree.appendChild(el('div', 'sub-head', (list.length - shown.length) + ' weitere, Filter nutzen'));
}

function renderTree(s) {
  const tree = fill('tree');
  const selected = new Set(s.keys || []);
  const areaKey = s.area ? s.area.key : null;
  const pointEntry = s.point ? s.point.entry : null;

  // The mod lists a copy last once it is rebuilt, so an edit would make it jump; group and name keep rows still.
  const copies = s.copies.filter((c) => matches(c.id) || matches(c.owner)).sort(byGroupThenName);
  if (groupHead(tree, 'copies', 'Objekte', copies.length)) {
    let owner = null;
    capped(tree, copies, (c) => {
      const g = c.expansion + ' / ' + c.owner;
      if (g !== owner) { owner = g; tree.appendChild(el('div', 'sub-head', g)); }
      item(tree, { icon: copyIcon(c), name: c.id, selected: selected.has(c.key), dirty: c.dirty, click: () => call('editor.select', c.key) });
    });
  }

  const ruleOrder = new Map((s.rules || []).map((r, i) => [r, i]));
  const rules = (s.rules || []).filter((r) => matches(r.id)).sort((a, b) =>
    cmp(a.expansion, b.expansion) || cmp(a.side, b.side) || ruleOrder.get(a) - ruleOrder.get(b));
  if (groupHead(tree, 'rules', 'Regeln', rules.length)) {
    let group = null;
    capped(tree, rules, (r) => {
      const g = r.expansion + ' / ' + sideName(r.side);
      if (g !== group) { group = g; tree.appendChild(el('div', 'sub-head', g)); }
      const e = effects[r.effect] || ['rule', r.effect];
      item(tree, {
        icon: e[0], name: r.name || r.id, sub: ruleWhere(r), selected: r.key === areaKey,
        tag: r.pending ? 'wartet' : r.stale ? 'Neuladen' : '', warn: r.pending || r.stale,
        click: () => call('editor.area', r.key),
      });
      for (let i = 0; i < r.openings; i++) {
        const k = r.key + '/opening/' + i;
        item(tree, { icon: 'opening', name: 'Öffnung ' + (i + 1), deep: true, selected: k === areaKey, click: () => call('editor.area', k) });
      }
    });
  }

  const boxes = (s.boxes || []).filter((b) => matches(b.id) || matches(b.copy));
  if (groupHead(tree, 'boxes', 'Boxen', boxes.length)) {
    capped(tree, boxes, (b) => item(tree, {
      icon: b.kind === 'Block' ? 'block' : 'panel', name: b.copy + ' / ' + b.id, selected: b.key === areaKey,
      click: () => call('editor.area', b.key),
    }));
  }

  const points = (s.points || []).filter((p) => matches(p.id) || matches(p.label));
  const open = groupHead(tree, 'points', 'Punkte', points.length, (h) => {
    const plus = btn(h, { icon: 'plus', cls: 'add' + (ui.pointMenu ? ' on' : ''), tip: 'Punkt anlegen', click: () => {} });
    plus.addEventListener('click', (e) => { e.stopPropagation(); ui.pointMenu = !ui.pointMenu; ui.open.points = true; render(ui.last); });
  });
  if (open) {
    if (ui.pointMenu) {
      const kinds = el('div', 'kinds');
      for (const k of ['locations', 'dealLocations', 'parkingLots', 'buildings', 'regions']) {
        const c = el('div', 'chip');
        c.appendChild(icon(pointKinds[k][0], 'small'));
        c.appendChild(el('div', '', pointKinds[k][1]));
        c.addEventListener('click', () => { ui.pointMenu = false; call('editor.point.new', k); });
        kinds.appendChild(c);
      }
      tree.appendChild(kinds);
    }
    capped(tree, points, (p) => {
      const entry = p.key.slice(0, p.key.lastIndexOf('/'));
      item(tree, {
        icon: (pointKinds[p.kind] || ['location'])[0], name: p.id, tag: p.label, selected: entry === pointEntry,
        dirty: p.layer, click: () => call('editor.point', p.key),
      });
    });
  }
}

const sources = { Main: ['main', 'Hauptwelt'], Source: ['source', 'Quellszene'], Prefabs: ['prefab', 'Prefabs'], Models: ['model', 'Modelle'] };

function renderCatalog(s) {
  const c = s.catalog;
  if (!c) return;
  const box = fill('sources');
  for (const t of Object.keys(sources)) {
    const b = el('div', t === c.tab ? 'source active' : 'source');
    b.appendChild(icon(sources[t][0]));
    b.appendChild(el('div', '', sources[t][1]));
    b.addEventListener('click', () => call('editor.catalog.tab', t));
    box.appendChild(b);
  }
  const search = document.getElementById('cat-search');
  if (!ui.editing && c.query !== undefined && search.value !== c.query) search.value = c.query;
  document.getElementById('cat-hint').textContent = c.hint || (c.results.length + ' Treffer, Klick setzt vor die Kamera');
  const list = fill('results');
  const ic = sources[c.tab] ? sources[c.tab][0] : 'copy-kind';
  for (const r of c.results) {
    const e = el('div', 'result');
    e.appendChild(icon(ic));
    const text = el('div', 'text');
    text.appendChild(el('div', 'name', r.label));
    if (r.detail) text.appendChild(el('div', 'detail', r.detail));
    e.appendChild(text);
    e.appendChild(icon('plus', 'small place'));
    e.addEventListener('click', () => call('editor.catalog.place', r.i));
    list.appendChild(e);
  }
}

/* Inspector */

function head(box, ic, name, kind, state) {
  const h = el('div', 'ihead');
  const t = el('div', 'itile');
  t.appendChild(icon(ic));
  h.appendChild(t);
  const title = el('div', 'ititle');
  title.appendChild(el('div', 'name', name));
  title.appendChild(el('div', 'kind', kind));
  h.appendChild(title);
  if (state !== undefined) {
    const st = el('div', state ? 'state changed' : 'state');
    if (state) st.appendChild(el('div', 'dot'));
    st.appendChild(el('div', '', state ? 'ungespeichert' : 'gespeichert'));
    h.appendChild(st);
  }
  box.appendChild(h);
}

function section(box, title) {
  const s = el('div', 'section');
  if (title) s.appendChild(el('div', 'stitle', title));
  box.appendChild(s);
  return s;
}

/* A number field: Enter sends `field=value` to the mod, which turns it into one undo step. */
function numField(parent, axis, axisCls, value, digits, unit, onSet) {
  const n = el('div', onSet ? 'num' : 'num readonly');
  n.appendChild(el('div', 'axis ' + axisCls, axis));
  if (!onSet) {
    n.appendChild(el('div', 'val', num(value, digits)));
    if (unit) n.appendChild(el('div', 'unit', unit));
    parent.appendChild(n);
    return;
  }
  const input = el('input', '');
  input.value = num(value, digits);
  watchFocus(input);
  input.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter') return;
    const raw = String(e.value !== undefined ? e.value : input.value).replace(',', '.').trim();
    if (raw === '' || isNaN(Number(raw))) {
      console.warn('editor: "' + raw + '" is no number; ' + axis + ' stays');
      n.className = 'num bad';
      return;
    }
    n.className = 'num';
    ui.editing = false;
    onSet(Number(raw));
  });
  n.appendChild(input);
  if (unit) n.appendChild(el('div', 'unit', unit));
  parent.appendChild(n);
}

function textField(parent, label, value, onSet) {
  const r = el('div', 'kv');
  r.appendChild(el('div', 'k', label));
  const input = el('input', 'field');
  input.value = value === undefined || value === null ? '' : String(value);
  watchFocus(input);
  input.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter') return;
    ui.editing = false;
    onSet(e.value !== undefined ? e.value : input.value);
  });
  r.appendChild(input);
  parent.appendChild(r);
}

function kv(parent, k, v) {
  if (v === undefined || v === null || v === '') return;
  const r = el('div', 'kv');
  r.appendChild(el('div', 'k', k));
  r.appendChild(el('div', 'v', v));
  parent.appendChild(r);
}

function switchRow(parent, on, main, sub, onClick, reset) {
  const r = el('div', 'switch-row');
  const label = el('div', 'label');
  label.appendChild(el('div', 'main', main));
  if (sub) label.appendChild(el('div', 'sub', sub));
  r.appendChild(label);
  if (reset) {
    const l = el('div', 'link', 'Standard');
    l.addEventListener('click', reset);
    r.appendChild(l);
  }
  const sw = el('div', on ? 'switch on' : 'switch');
  sw.appendChild(el('div', 'knob'));
  sw.addEventListener('click', onClick);
  r.appendChild(sw);
  parent.appendChild(r);
}

function actions(box, list) {
  const a = el('div', 'actions');
  for (const [ic, label, key, onClick, danger] of list) {
    const b = el('div', danger ? 'act danger' : 'act');
    b.appendChild(icon(ic, 'small'));
    b.appendChild(el('div', 'label', label));
    if (key) tip(b, label, key);
    b.addEventListener('click', onClick);
    a.appendChild(b);
  }
  box.appendChild(a);
}

function note(parent, ic, text) {
  const n = el('div', 'note');
  n.appendChild(icon(ic, 'small'));
  n.appendChild(el('div', '', text));
  parent.appendChild(n);
}

const copyActions = [
  ['frame', 'Fokus', 'F', () => call('editor.frame')],
  ['drop', 'Auf Boden', 'Ende', () => call('editor.drop')],
  ['duplicate', 'Duplizieren', 'Strg+D', () => call('editor.dup')],
  ['delete', 'Löschen', 'Entf', () => call('editor.del'), true],
];

function renderCopy(box, s, x) {
  const n = (s.keys || []).length;
  const kind = x.template && x.template.indexOf('Modell') === 0 ? 'model' : x.template && x.template.indexOf('Prefab') === 0 ? 'prefab' : 'copy-kind';
  if (n > 1) {
    head(box, kind, n + ' Objekte', 'Mehrfachauswahl, zuletzt ' + x.id);
  } else {
    head(box, kind, x.id, x.expansion + ' / ' + x.owner, x.dirty);
    const lage = section(box, 'LAGE');
    const v = el('div', 'vec');
    ['x', 'y', 'z'].forEach((a, i) => numField(v, a.toUpperCase(), a, x.position[i], 2, '', (val) => call('editor.set', a + '=' + val)));
    lage.appendChild(v);
    const t = el('div', 'vec');
    numField(t, 'Drehung', 'n', x.yaw, 1, '°', (val) => call('editor.set', 'yaw=' + val));
    numField(t, 'Größe', 'n', x.scale, 2, '×', (val) => call('editor.set', 'scale=' + val));
    lage.appendChild(t);
    kv(lage, 'Maße', size(x.size));
  }
  const nav = section(box, 'NAVIGATION');
  switchRow(nav, x.obstacle, x.obstacle ? 'NPCs gehen drumherum' : 'NPCs gehen hindurch',
    x.obstacleSet ? 'Eigene Einstellung' : 'Standard für diese Art Objekt',
    () => call('editor.obstacle', x.obstacle ? 'off' : 'on'),
    x.obstacleSet ? () => call('editor.obstacle', 'default') : null);
  if (n <= 1) {
    const from = section(box, 'HERKUNFT');
    kv(from, 'Vorlage', x.template);
    kv(from, 'Auf', x.onto);
    kv(from, 'Ebene', x.owner);
  }
  actions(box, copyActions);
}

function renderArea(box, a) {
  if (a.effect) {
    const e = effects[a.effect] || ['rule', a.effect];
    head(box, e[0], a.name || a.label, e[1] + ', ' + sideName(a.side) + (a.parent ? ', in ' + a.parent : ''));
    if (a.pending || a.stale) {
      const st = section(box);
      note(st, 'warn', a.pending ? 'Wartet auf Anwenden.' : 'Alte Form bis zum Neuladen.');
    }
    const w = section(box, 'WIRKUNG');
    const choices = el('div', 'choices');
    for (const k of Object.keys(effects)) {
      const c = el('div', k === a.effect ? 'chip active' : 'chip');
      c.appendChild(icon(effects[k][0], 'small'));
      c.appendChild(el('div', '', effects[k][1]));
      c.addEventListener('click', () => call('editor.rule.effect', k));
      choices.appendChild(c);
    }
    w.appendChild(choices);
    const f = section(box, 'TRIFFT');
    textField(f, 'Name', a.name, (v) => call('editor.rule.name', v));
    textField(f, 'Eltern', a.parent, (v) => call('editor.rule.parent', v));
    kv(f, 'Kennung', a.key.slice(a.key.indexOf('/rule/') + 6));
    kv(f, 'Erweiterung', a.expansion);
  } else if (a.boxKind) {
    head(box, a.boxKind === 'Block' ? 'block' : 'panel', a.label, (a.boxKind === 'Block' ? 'Blocker' : 'Panel') + ' an ' + a.copy);
  } else {
    head(box, 'opening', a.label, 'Öffnung');
  }
  if (a.min) {
    const b = section(box, 'BEREICH');
    kv(b, 'Von', a.min.map((n) => num(n, 2)).join('  '));
    kv(b, 'Bis', a.max.map((n) => num(n, 2)).join('  '));
  }
  if (a.boxKind === 'Panel') {
    const m = section(box, 'MATERIAL');
    kv(m, 'Aktuell', a.material || 'keins');
    textField(m, 'Suche', a.query, (v) => call('editor.materials', v));
    const mats = el('div', 'mats');
    for (const mat of a.materials || []) {
      const r = el('div', mat.name === a.material ? 'mat active' : 'mat');
      const sw = el('div', 'swatch');
      sw.style.backgroundColor = mat.color;
      r.appendChild(sw);
      r.appendChild(el('div', '', mat.name));
      r.addEventListener('click', () => call('editor.box.material', mat.name));
      mats.appendChild(r);
    }
    m.appendChild(mats);
  }
}

const pointFields = {
  dealLocations: [['name', 'Name'], ['description', 'Beschreibung'], ['region', 'Region']],
  parkingLots: [['name', 'Name']],
  buildings: [['name', 'Name']],
  shops: [['seller', 'Verkäufer'], ['choiceText', 'Dialogzeile'], ['counterReach', 'Reichweite']],
  regions: [['standIn', 'Vertretung']],
};

function subLabel(kind, i) {
  if (kind === 'parkingLots') return i === 0 ? 'Zufahrt' : 'Stellplatz ' + i;
  if (kind === 'buildings') return 'Tür ' + (i + 1);
  if (kind === 'regions') return 'Ecke ' + (i + 1);
  if (kind === 'shops') return 'Theke';
  return 'Punkt';
}

function subCount(p) {
  const f = p.fields || {};
  if (p.kind === 'regions') return (f.polygon || []).length;
  if (p.kind === 'parkingLots') return 1 + (f.spots || []).length;
  if (p.kind === 'buildings') return (f.doors || []).length;
  return 1;
}

function renderPoint(box, p) {
  const k = pointKinds[p.kind] || ['location', p.kind];
  head(box, k[0], p.label, k[1] + (p.layer ? ', in der Ebene geändert' : ', aus dem Code'));
  const st = section(box);
  note(st, 'warn', 'Wirkt nach dem nächsten Laden.');

  const n = subCount(p);
  const where = section(box, n > 1 ? subLabel(p.kind, p.sub).toUpperCase() : 'LAGE');
  if (n > 1) {
    const subs = el('div', 'choices');
    for (let i = 0; i < n; i++) {
      const c = el('div', i === p.sub ? 'chip active' : 'chip', subLabel(p.kind, i));
      c.addEventListener('click', () => call('editor.point', p.entry + '/' + i));
      subs.appendChild(c);
    }
    where.appendChild(subs);
  }
  if (p.position) {
    const v = el('div', 'vec');
    ['x', 'y', 'z'].forEach((a, i) => numField(v, a.toUpperCase(), a, p.position[i], 2, '', null));
    where.appendChild(v);
    kv(where, 'Richtung', num(p.yaw, 1) + '°');
  }

  const f = p.fields || {};
  const fields = pointFields[p.kind] || [];
  if (fields.length || p.kind === 'regions') {
    const d = section(box, 'ANGABEN');
    for (const [key, label] of fields) textField(d, label, f[key], (v) => call('editor.point.set', key + '=' + v));
    if (p.kind === 'regions')
      switchRow(d, !!f.unlocked, f.unlocked ? 'Von Anfang an offen' : 'Muss freigeschaltet werden', '',
        () => call('editor.point.set', 'unlocked=' + (f.unlocked ? 'nein' : 'ja')), null);
  }

  const list = [['here', 'Zur Blickmitte', '', () => call('editor.point.here')]];
  if (p.kind === 'regions' || p.kind === 'parkingLots' || p.kind === 'buildings') {
    list.push(['plus', subLabel(p.kind, 1).split(' ')[0], '', () => call('editor.point.add')]);
    list.push(['minus', subLabel(p.kind, p.sub) + ' weg', '', () => call('editor.point.remove')]);
  }
  if (p.kind !== 'shops') list.push(['delete', 'Löschen', '', () => call('editor.point.delete'), true]);
  actions(box, list);
}

const keys = [
  ['Klick', 'Objekt wählen'],
  ['Strg+Klick', 'zur Auswahl hinzufügen'],
  ['Ziehen', 'Rahmen um mehrere'],
  ['Rechte Maus', 'umsehen, mit WASD fliegen'],
  ['Q / E', 'ab und auf'],
  ['Shift', 'schneller fliegen'],
  ['W E R', 'verschieben, drehen, skalieren'],
  ['F', 'Auswahl in den Blick'],
  ['Alt+Ziehen', 'um die Auswahl kreisen'],
  ['Strg+S', 'speichern'],
  ['Tab', 'spielen und zurück'],
];

function renderEmpty(box) {
  const e = el('div', 'empty');
  e.appendChild(el('div', 'lead', 'Nichts ausgewählt.'));
  const list = el('div', 'keys');
  for (const [k, what] of keys) {
    const r = el('div', 'keyrow');
    r.appendChild(el('div', 'kbd', k));
    r.appendChild(el('div', 'what', what));
    list.appendChild(r);
  }
  e.appendChild(list);
  box.appendChild(e);
}

function renderInspector(s) {
  const box = fill('props');
  if (s.point) renderPoint(box, s.point);
  else if (s.area) renderArea(box, s.area);
  else if (s.selected) renderCopy(box, s, s.selected);
  else renderEmpty(box);
}

/* Log and status bar */

function renderLog(s) {
  const lines = s.log || [];
  document.getElementById('logpane').className = ui.logOpen ? 'logpane dock' : 'logpane dock hidden';
  if (ui.logOpen) {
    const box = fill('lines');
    lines.forEach((l, i) => box.appendChild(el('div', i === lines.length - 1 ? 'l last' : 'l', l)));
    if (box.scrollToEnd) box.scrollToEnd();
  }
  const status = fill('status-log');
  status.appendChild(icon('log', 'small muted'));
  status.appendChild(el('div', 'text', lines.length ? lines[lines.length - 1] : 'Noch keine Meldung.'));
  const right = fill('status-right');
  const n = (s.keys || []).length;
  if (n > 0) right.appendChild(el('div', 'on', n === 1 ? '1 Objekt gewählt' : n + ' Objekte gewählt'));
  right.appendChild(el('div', '', 'Kamera ' + num(s.speed || 0, 1) + ' m/s'));
}

function render(s) {
  if (!s) return;
  ui.last = s;
  hideTip();
  renderToolbar(s);
  renderTabs(s);
  renderTree(s);
  renderCatalog(s);
  if (!ui.editing) renderInspector(s);
  renderLog(s);
}

/* A field being typed in keeps its inspector: a re-render would throw the typed text away. */
function watchFocus(input) {
  input.addEventListener('focus', () => { ui.editing = true; });
  input.addEventListener('blur', () => {
    ui.editing = false;
    if (ui.last) renderInspector(ui.last);
  });
}

const filter = document.getElementById('filter');
filter.addEventListener('input', (e) => {
  ui.filter = String(e.value !== undefined ? e.value : filter.value).trim().toLowerCase();
  render(ui.last);
});

const catSearch = document.getElementById('cat-search');
catSearch.addEventListener('focus', () => { ui.editing = true; });
catSearch.addEventListener('blur', () => { ui.editing = false; });
catSearch.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') call('editor.catalog.search', e.value !== undefined ? e.value : catSearch.value);
});

document.getElementById('status-log').addEventListener('click', () => { ui.logOpen = !ui.logOpen; render(ui.last); });

// A dock is a hit target only when the script listens to it; without this a click on an empty dock area would
// fall through to the world behind it.
for (const id of ['toolbar', 'left', 'left-collapsed', 'inspector', 'logpane', 'statusbar']) {
  document.getElementById(id).addEventListener('click', () => {});
}

s1.on('editor.changed', (payload) => render(parse(payload)));
render(parse(s1.call('editor.state', '')));
