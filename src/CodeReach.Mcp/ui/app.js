'use strict';

// CodeReach explorer. Data comes from the local server: /api/call (the tool registry) and /api/explore/*
// (the same graph). Every string from the index is set with textContent; nothing is parsed as HTML.

const SVG = 'http://www.w3.org/2000/svg';
const TOKEN_KEY = 'codereach.token';
const EDGE_KINDS = ['calls', 'new', 'reads', 'writes', 'uses-type', 'implements', 'overrides', 'inherits', 'member'];
const DEFAULT_KINDS = ['calls', 'new', 'implements', 'overrides', 'inherits'];

const token = (() => {
  const fromHash = new URLSearchParams(location.hash.slice(1)).get('t');
  try {
    if (fromHash) sessionStorage.setItem(TOKEN_KEY, fromHash);
    return fromHash || sessionStorage.getItem(TOKEN_KEY) || '';
  } catch {
    return fromHash || '';
  }
})();

const $ = id => document.getElementById(id);

// ---- helpers ----------------------------------------------------------------------------------

function el(tag, props, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'style') node.style.cssText = value; // through the CSSOM: the page's CSP forbids style attributes
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function svg(tag, attrs) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs || {})) node.setAttribute(key, String(value));
  return node;
}

function loc(file, line) {
  if (!file) return el('span', { class: 'loc', text: 'framework or library' });
  const where = line ? `${file}:${line}` : file;
  const button = el('button', { class: 'loc', type: 'button', title: 'Copy location', text: where });
  button.addEventListener('click', async event => {
    event.stopPropagation();
    try {
      await navigator.clipboard.writeText(where);
      button.classList.add('copied');
      button.textContent = 'copied';
      setTimeout(() => { button.classList.remove('copied'); button.textContent = where; }, 900);
    } catch {
      // Clipboard refused: the text is still selectable.
    }
  });
  return button;
}

function symLink(symbol, label) {
  return el('button', { class: 'link', type: 'button', title: symbol.id, text: label || symbol.display, onclick: () => openGraph(symbol.id) });
}

function authPill(state) {
  if (state === 'none') return el('span', { class: 'pill bad', text: 'no auth' });
  if (state === 'anonymous') return el('span', { class: 'pill bad', text: 'anonymous' });
  return el('span', { class: 'pill good', text: 'auth' });
}

function errorBox(message) {
  return el('div', { class: 'error', text: message });
}

// ---- API --------------------------------------------------------------------------------------

async function call(tool, args) {
  const response = await fetch('/api/call', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CodeReach-Token': token },
    body: JSON.stringify({ tool, args: args || {} }),
  });
  if (!response.ok) throw new Error(response.status === 401 ? 'No valid token: open the address codereach printed.' : await response.text());
  const result = await response.json();
  if (result.data && result.data.index) stamp(result.data.index);
  if (result.isError) throw new Error(result.text);
  return result;
}

async function explore(what, params) {
  const query = new URLSearchParams(params || {}).toString();
  const response = await fetch(`/api/explore/${what}${query ? '?' + query : ''}`, { headers: { 'X-CodeReach-Token': token } });
  const body = await response.json().catch(() => ({ error: `${response.status}` }));
  if (!response.ok) throw new Error(body.error || `${response.status}`);
  if (body.index) stamp(body.index);
  return body;
}

function stamp(index) {
  const dot = $('dot');
  if (index.notice) {
    dot.className = 'dot warn';
    $('index-text').textContent = 'refreshing index';
  } else if (!index.complete) {
    dot.className = 'dot bad';
    $('index-text').textContent = 'partial analysis';
  } else {
    dot.className = 'dot ok';
    $('index-text').textContent = 'index fresh';
  }
  $('status-left').textContent = `server ready · index generation ${index.generation}`;
  $('status-right').textContent = `built ${String(index.builtAt).slice(0, 19).replace('T', ' ')} UTC · ${index.complete ? 'analysis complete' : 'analysis PARTIAL: rows may be missing'}`;
}

// ---- graph renderer ---------------------------------------------------------------------------

class GraphView {
  constructor(host, options) {
    this.host = host;
    this.options = options || {};
    this.scale = 1;
    this.tx = 0;
    this.ty = 0;
    this.nodes = [];
    this.edges = [];
    this.selected = null;
    this.svg = svg('svg', { role: 'img', 'aria-label': 'Graph' });
    const defs = svg('defs');
    for (const [id, color] of [['arrow', '#5a6270'], ['arrow-hot', '#e6a84b']]) {
      const marker = svg('marker', { id: `${id}-${this.options.name}`, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 6, markerHeight: 6, orient: 'auto-start-reverse' });
      marker.append(svg('path', { d: 'M 0 0 L 10 5 L 0 10 z', fill: color }));
      defs.append(marker);
    }
    this.viewport = svg('g');
    this.svg.append(defs, this.viewport);
    host.replaceChildren(this.svg);
    this.bindPanZoom();
  }

  bindPanZoom() {
    let drag = null;
    this.svg.addEventListener('pointerdown', event => {
      if (event.target.closest('.node')) return;
      drag = { x: event.clientX, y: event.clientY, tx: this.tx, ty: this.ty };
      this.host.classList.add('panning');
      this.svg.setPointerCapture(event.pointerId);
    });
    this.svg.addEventListener('pointermove', event => {
      if (!drag) return;
      this.tx = drag.tx + event.clientX - drag.x;
      this.ty = drag.ty + event.clientY - drag.y;
      this.apply();
    });
    const end = () => { drag = null; this.host.classList.remove('panning'); };
    this.svg.addEventListener('pointerup', end);
    this.svg.addEventListener('pointercancel', end);
    this.svg.addEventListener('wheel', event => {
      event.preventDefault();
      const rect = this.svg.getBoundingClientRect();
      const mx = event.clientX - rect.left;
      const my = event.clientY - rect.top;
      const factor = Math.exp(-event.deltaY * 0.0015);
      const next = Math.min(4, Math.max(0.15, this.scale * factor));
      this.tx = mx - (mx - this.tx) * (next / this.scale);
      this.ty = my - (my - this.ty) * (next / this.scale);
      this.scale = next;
      this.apply();
    }, { passive: false });
  }

  apply() {
    this.viewport.setAttribute('transform', `translate(${this.tx},${this.ty}) scale(${this.scale})`);
  }

  empty(message) {
    this.nodes = [];
    this.edges = [];
    this.viewport.replaceChildren();
    this.host.querySelector('.empty')?.remove();
    this.host.append(el('div', { class: 'empty', text: message }));
  }

  set(data, layout) {
    this.host.querySelector('.empty')?.remove();
    this.center = data.center;
    this.nodes = data.nodes.map(n => ({ ...n }));
    this.byId = new Map(this.nodes.map(n => [n.id, n]));
    this.edges = data.edges.filter(e => this.byId.has(e.from) && this.byId.has(e.to)).map(e => ({ ...e }));
    if (layout === 'tree') this.layoutTree(); else this.layoutForce();
    this.draw();
    this.fit();
  }

  layoutForce() {
    const nodes = this.nodes;
    const byLayer = {};
    nodes.forEach(n => { (byLayer[n.layer] = byLayer[n.layer] || []).push(n); n.side = undefined; });
    for (const [layer, members] of Object.entries(byLayer)) {
      members.forEach((n, i) => {
        const angle = (i / members.length) * Math.PI * 2 + Number(layer) * 0.7;
        const radius = (Number(layer) || 1) * 190;
        n.x = n.id === this.center ? 0 : Math.cos(angle) * radius;
        n.y = n.id === this.center ? 0 : Math.sin(angle) * radius;
      });
    }
    const index = new Map(nodes.map((n, i) => [n.id, i]));
    const links = this.edges.map(e => [index.get(e.from), index.get(e.to)]);
    const steps = nodes.length > 250 ? 140 : 260;
    for (let step = 0; step < steps; step++) {
      const cool = 1 - step / steps;
      const fx = new Float64Array(nodes.length);
      const fy = new Float64Array(nodes.length);
      for (let i = 0; i < nodes.length; i++) {
        for (let j = i + 1; j < nodes.length; j++) {
          let dx = nodes[i].x - nodes[j].x;
          let dy = nodes[i].y - nodes[j].y;
          let d2 = dx * dx + dy * dy;
          if (d2 < 1) { dx = (i % 7) - 3; dy = (j % 5) - 2; d2 = dx * dx + dy * dy || 1; }
          const d = Math.sqrt(d2);
          const force = 14000 / d2;
          fx[i] += (dx / d) * force; fy[i] += (dy / d) * force;
          fx[j] -= (dx / d) * force; fy[j] -= (dy / d) * force;
        }
      }
      for (const [a, b] of links) {
        const dx = nodes[b].x - nodes[a].x;
        const dy = nodes[b].y - nodes[a].y;
        const d = Math.sqrt(dx * dx + dy * dy) || 1;
        const force = (d - 150) * 0.04;
        fx[a] += (dx / d) * force; fy[a] += (dy / d) * force;
        fx[b] -= (dx / d) * force; fy[b] -= (dy / d) * force;
      }
      nodes.forEach((n, i) => {
        if (n.id === this.center) return;
        fx[i] -= n.x * 0.004;
        fy[i] -= n.y * 0.004;
        const limit = 30 * cool + 1;
        n.x += Math.max(-limit, Math.min(limit, fx[i] * 0.1));
        n.y += Math.max(-limit, Math.min(limit, fy[i] * 0.1));
      });
    }
  }

  // Layered: what the centre uses to the right, what uses the centre to the left, one column per layer.
  layoutTree() {
    const forward = new Set([this.center]);
    const backward = new Set([this.center]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const e of this.edges) {
        if (forward.has(e.from) && !forward.has(e.to)) { forward.add(e.to); grew = true; }
        if (backward.has(e.to) && !backward.has(e.from)) { backward.add(e.from); grew = true; }
      }
    }
    const columns = new Map();
    for (const n of this.nodes) {
      n.side = n.id === this.center ? 0 : forward.has(n.id) && !backward.has(n.id) ? 1 : backward.has(n.id) ? -1 : 1;
      n.column = n.id === this.center ? 0 : n.side * Math.max(1, n.layer);
      if (!columns.has(n.column)) columns.set(n.column, []);
      columns.get(n.column).push(n);
    }
    for (const [column, members] of columns) {
      members.sort((a, b) => Number(b.entry) - Number(a.entry) || String(a.display).localeCompare(String(b.display)));
      members.forEach((n, i) => {
        n.x = column * 320;
        n.y = (i - (members.length - 1) / 2) * 30;
      });
    }
  }

  draw() {
    const edgeLayer = svg('g');
    const labelLayer = svg('g');
    const nodeLayer = svg('g');
    const showLabels = this.edges.length <= 90;
    const tree = this.nodes.some(n => n.side !== undefined);
    for (const e of this.edges) {
      const a = this.byId.get(e.from);
      const b = this.byId.get(e.to);
      const path = tree
        ? `M ${a.x} ${a.y} C ${(a.x + b.x) / 2} ${a.y}, ${(a.x + b.x) / 2} ${b.y}, ${b.x} ${b.y}`
        : `M ${a.x} ${a.y} L ${b.x} ${b.y}`;
      const line = svg('path', { d: path, class: `edge ${e.kind}`, 'marker-end': `url(#arrow-${this.options.name})` });
      e.element = line;
      edgeLayer.append(line);
      if (showLabels) {
        const label = svg('text', { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 - 4, 'text-anchor': 'middle', class: 'edge-label' });
        label.textContent = `${e.kind}${e.count > 1 ? ' ×' + e.count : ''}`;
        labelLayer.append(label);
      }
    }
    for (const n of this.nodes) {
      const project = n.kind === 'project' || n.kind === 'test project';
      const classes = ['node', n.kind === 'type' ? 'type' : '', n.external ? 'external' : '', n.test ? 'test' : '', n.entry ? 'entry' : '', n.id === this.center ? 'center' : '', project ? 'project' : ''];
      const group = svg('g', { class: classes.filter(Boolean).join(' '), transform: `translate(${n.x},${n.y})`, tabindex: 0, role: 'button', 'aria-label': n.display });
      const radius = n.id === this.center ? 9 : n.entry || n.kind === 'type' || project ? 7 : 5;
      group.append(svg('circle', { r: radius }));
      const left = n.side === -1;
      const text = svg('text', { x: left ? -radius - 6 : radius + 6, y: 4, 'text-anchor': left ? 'end' : 'start' });
      text.textContent = n.label || n.display;
      group.append(text);
      const title = svg('title');
      title.textContent = `${n.display}\n${n.kind}${n.file ? `\n${n.file}:${n.line}` : ''}${n.entryLabel ? `\n${n.entryKind}: ${n.entryLabel}` : ''}`;
      group.append(title);
      group.addEventListener('click', event => { event.stopPropagation(); this.select(n.id); });
      group.addEventListener('dblclick', event => { event.stopPropagation(); this.options.onOpen?.(n.id); });
      group.addEventListener('keydown', event => {
        if (event.key === 'Enter') this.options.onOpen?.(n.id);
        if (event.key === ' ') { event.preventDefault(); this.select(n.id); }
      });
      n.element = group;
      nodeLayer.append(group);
    }
    this.viewport.replaceChildren(edgeLayer, labelLayer, nodeLayer);
  }

  select(id) {
    this.selected = id;
    for (const n of this.nodes) n.element.classList.toggle('selected', n.id === id);
    for (const e of this.edges) {
      const hot = e.from === id || e.to === id;
      e.element.classList.toggle('hot', hot);
      e.element.setAttribute('marker-end', `url(#${hot ? 'arrow-hot' : 'arrow'}-${this.options.name})`);
    }
    this.options.onSelect?.(id);
  }

  fit() {
    if (!this.nodes.length) return;
    const rect = this.svg.getBoundingClientRect();
    const width = rect.width || 800;
    const height = rect.height || 600;
    // Labels hang off the side of each node, so measure them into the box.
    const labelWidth = n => String(n.label || n.display).length * 7.4 + 20;
    const ys = this.nodes.map(n => n.y);
    const minX = Math.min(...this.nodes.map(n => n.x - (n.side === -1 ? labelWidth(n) : 20)));
    const maxX = Math.max(...this.nodes.map(n => n.x + (n.side === -1 ? 20 : labelWidth(n))));
    const minY = Math.min(...ys) - 40;
    const maxY = Math.max(...ys) + 40;
    this.scale = Math.min(1.4, Math.max(0.15, Math.min(width / (maxX - minX), height / (maxY - minY))));
    this.tx = (width - (maxX - minX) * this.scale) / 2 - minX * this.scale;
    this.ty = (height - (maxY - minY) * this.scale) / 2 - minY * this.scale;
    this.apply();
  }
}

// ---- symbol search (graph toolbar) -------------------------------------------------------------

function attachSearch(input, list, onPick) {
  let items = [];
  let active = -1;
  let timer = 0;
  const close = () => { list.hidden = true; input.setAttribute('aria-expanded', 'false'); items = []; active = -1; };
  const draw = () => {
    list.replaceChildren(...items.map((s, i) => el('li', {
      role: 'option', 'aria-selected': String(i === active),
      onmousedown: event => { event.preventDefault(); close(); onPick(s); },
    }, el('span', { class: 'k', text: s.kind }), el('span', { class: 'n', text: s.display }), el('span', { class: 'w', text: s.file ? `${s.file}:${s.line}` : '' }))));
    list.hidden = items.length === 0;
    input.setAttribute('aria-expanded', String(!list.hidden));
  };
  input.addEventListener('input', () => {
    clearTimeout(timer);
    const query = input.value.trim();
    if (query.length < 2) { close(); return; }
    timer = setTimeout(async () => {
      try {
        const result = await call('find_symbol', { query, limit: 12 });
        if (input.value.trim() !== query) return;
        items = result.data.symbols;
        active = -1;
        draw();
      } catch {
        close();
      }
    }, 150);
  });
  input.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' && items.length) { event.preventDefault(); active = (active + 1) % items.length; draw(); }
    else if (event.key === 'ArrowUp' && items.length) { event.preventDefault(); active = (active - 1 + items.length) % items.length; draw(); }
    else if (event.key === 'Enter') {
      event.preventDefault();
      const pick = items[Math.max(active, 0)];
      close();
      if (pick) onPick(pick); else if (input.value.trim()) onPick({ id: input.value.trim(), display: input.value.trim() });
    } else if (event.key === 'Escape') close();
  });
  input.addEventListener('blur', () => setTimeout(close, 120));
}

// ---- Graph tab ----------------------------------------------------------------------------------

const graphState = { center: null, dir: 'out', depth: 1, layout: 'force', kinds: new Set(DEFAULT_KINDS), external: false, back: [], forward: [] };
let mainGraph = null;

function setupGraphTab() {
  mainGraph = new GraphView($('g-canvas'), { name: 'main', onSelect: showDetails, onOpen: id => openGraph(id) });
  mainGraph.empty('Search for a method, type or property above, or open one from Explore, Routes or Overview.');
  attachSearch($('g-search'), $('g-suggest'), symbol => openGraph(symbol.id));
  $('g-kinds-body').replaceChildren(...EDGE_KINDS.map(kind => el('label', { class: 'check' },
    el('input', { type: 'checkbox', checked: graphState.kinds.has(kind), onchange: event => {
      if (event.target.checked) graphState.kinds.add(kind); else graphState.kinds.delete(kind);
      loadGraph();
    } }), kind)));
  $('g-dir').addEventListener('change', event => { graphState.dir = event.target.value; loadGraph(); });
  $('g-less').addEventListener('click', () => { graphState.depth = Math.max(1, graphState.depth - 1); loadGraph(); });
  $('g-more').addEventListener('click', () => { graphState.depth = Math.min(4, graphState.depth + 1); loadGraph(); });
  $('g-force').addEventListener('click', () => setLayout('force'));
  $('g-tree').addEventListener('click', () => setLayout('tree'));
  $('g-external').addEventListener('change', event => { graphState.external = event.target.checked; loadGraph(); });
  $('g-fit').addEventListener('click', () => mainGraph.fit());
  $('g-trace').addEventListener('click', loadTrace);
  $('g-back').addEventListener('click', () => {
    if (!graphState.back.length) return;
    graphState.forward.push(graphState.center);
    graphState.center = graphState.back.pop();
    loadGraph();
  });
  $('g-forward').addEventListener('click', () => {
    if (!graphState.forward.length) return;
    graphState.back.push(graphState.center);
    graphState.center = graphState.forward.pop();
    loadGraph();
  });
  $('legend').replaceChildren(
    el('span', {}, el('i', { style: 'background:#e6a84b' }), 'centre'),
    el('span', {}, el('i', { style: 'background:#e5674a' }), 'endpoint or job'),
    el('span', {}, el('i', { style: 'background:#9fb4c8' }), 'type'),
    el('span', {}, el('i', { style: 'background:#c8ccd4' }), 'member'),
    el('span', {}, el('i', { style: 'border:1px solid #5b616c' }), 'framework'),
    el('span', { text: 'click: details · double-click: centre on it · drag: pan · wheel: zoom' }));
}

function setLayout(layout) {
  graphState.layout = layout;
  $('g-force').setAttribute('aria-pressed', String(layout === 'force'));
  $('g-tree').setAttribute('aria-pressed', String(layout === 'tree'));
  if (mainGraph.lastData) {
    mainGraph.set(mainGraph.lastData, layout);
    if (mainGraph.selected) mainGraph.select(mainGraph.selected);
  }
}

function openGraph(id) {
  if (graphState.center && graphState.center !== id) {
    graphState.back.push(graphState.center);
    graphState.forward = [];
  }
  graphState.center = id;
  switchTab('graph');
  loadGraph();
}

async function loadGraph() {
  if (!graphState.center) return;
  $('g-back').disabled = graphState.back.length === 0;
  $('g-forward').disabled = graphState.forward.length === 0;
  $('g-depth').textContent = `${graphState.depth} layer${graphState.depth > 1 ? 's' : ''}`;
  $('g-count').textContent = 'loading…';
  try {
    const data = await explore('graph', {
      id: graphState.center, dir: graphState.dir, depth: graphState.depth,
      kinds: [...graphState.kinds].join(','), external: graphState.external ? '1' : '0',
    });
    graphState.center = data.center;
    const center = data.nodes.find(n => n.id === data.center);
    $('g-search').value = center ? center.display : '';
    mainGraph.lastData = data;
    mainGraph.set(data, graphState.layout);
    mainGraph.select(data.center);
    $('g-count').textContent = `${data.nodes.length} nodes · ${data.edges.length} edges${data.truncated ? ' · capped' : ''}`;
  } catch (error) {
    $('g-count').textContent = '';
    mainGraph.empty(error.message);
  }
}

// The trace tool's call paths drawn as one graph: entry points on the left, the symbol on the right.
async function loadTrace() {
  if (!graphState.center) return;
  $('g-count').textContent = 'tracing…';
  try {
    const data = (await call('trace_to_entrypoints', { symbol: graphState.center, limit: 200 })).data;
    const nodes = new Map();
    const edges = new Map();
    for (const path of [...data.rooted, ...data.unrooted]) {
      path.hops.forEach((hop, i) => {
        const distance = path.hops.length - 1 - i;
        const existing = nodes.get(hop.id);
        if (!existing || existing.layer < distance) {
          nodes.set(hop.id, {
            ...hop, layer: distance, entry: i === 0 && Boolean(path.entryKind), entryKind: path.entryKind, entryLabel: path.entryLabel,
            label: i === 0 && path.endpoints.length ? `${path.endpoints[0].verb} ${path.endpoints[0].route}` : hop.display,
          });
        }
        if (i > 0) edges.set(`${path.hops[i - 1].id}>${hop.id}`, { from: path.hops[i - 1].id, to: hop.id, kind: 'calls', count: 1 });
      });
    }
    const target = [...nodes.values()].find(n => n.layer === 0);
    const graphData = { center: target ? target.id : graphState.center, nodes: [...nodes.values()], edges: [...edges.values()] };
    if (!graphData.nodes.length) {
      mainGraph.empty('Nothing in the solution calls this.');
      $('g-count').textContent = '';
      return;
    }
    mainGraph.lastData = graphData;
    setLayout('tree');
    $('g-count').textContent = `${data.rootedTotal} entry points · ${data.unrootedTotal} unrooted · ${nodes.size} symbols`;
  } catch (error) {
    $('g-count').textContent = '';
    mainGraph.empty(error.message);
  }
}

async function showDetails(id) {
  const panel = $('details');
  panel.hidden = false;
  panel.replaceChildren(el('div', { class: 'loading', text: 'Loading…' }));
  try {
    const d = await explore('node', { id });
    const stat = (n, label) => el('div', { class: 'stat' }, el('b', { text: String(n) }), el('span', { text: label }));
    const children = [
      el('button', { class: 'btn close', type: 'button', text: '×', 'aria-label': 'Close details', onclick: () => { panel.hidden = true; } }),
      el('div', { class: 'kind', text: `${d.kind}${d.project ? ' · ' + d.project : ''}${d.test ? ' · test' : ''}` }),
      el('h3', { text: d.display }),
      loc(d.file, d.line),
      el('div', { class: 'stats' },
        stat(d.callSites, 'call sites'), stat(d.callers, 'callers'),
        stat(d.callees, 'callees'), stat(d.usages, 'references'),
        d.kind === 'type' ? stat(d.members, 'members') : stat(d.testCallSites, 'test call sites'),
        d.kind === 'type' ? stat(d.derived, 'derived types') : null),
    ];
    if (d.endpoints.length) {
      children.push(el('h4', { text: 'Endpoint' }), ...d.endpoints.map(e => el('div', { class: 'row' },
        el('b', { class: 'mono', text: e.verb }), el('span', { class: 'mono', text: e.route }), authPill(e.authState),
        el('div', { class: 'muted', text: e.authText }))));
    } else if (d.entryPoints.length) {
      children.push(el('h4', { text: 'Entry point' }), ...d.entryPoints.map(e => el('div', { class: 'row' }, el('span', { class: 'pill entry', text: e.kind }), e.label)));
    }
    if (d.implements.length) children.push(el('h4', { text: 'Implements / overrides' }), ...d.implements.map(s => el('div', { class: 'row' }, symLink(s))));
    if (d.implementations.length) children.push(el('h4', { text: 'Implementations' }), ...d.implementations.map(s => el('div', { class: 'row' }, symLink(s), loc(s.file, s.line))));
    if (d.registrations && d.registrations.length) {
      children.push(el('h4', { text: 'Dependency injection' }), ...d.registrations.map(r => el('div', { class: 'row' }, el('span', { class: 'pill', text: r.lifetime }), loc(r.file, r.line))));
    }
    children.push(el('div', { class: 'actions' },
      el('button', { class: 'btn', type: 'button', text: 'Centre graph here', onclick: () => openGraph(d.id) }),
      el('button', { class: 'btn', type: 'button', text: 'Path to entry points', onclick: () => { graphState.center = d.id; switchTab('graph'); loadTrace(); } }),
      d.file ? el('button', { class: 'btn', type: 'button', text: 'Open source', onclick: () => openFile(d.file, d.line, d.id) }) : null));
    panel.replaceChildren(...children.filter(Boolean));
  } catch (error) {
    panel.replaceChildren(errorBox(error.message));
  }
}

// ---- Explore tab --------------------------------------------------------------------------------

const exploreState = { files: null, path: null, symbols: [], selected: null, expanded: new Set() };
let sideGraph = null;

function setupExploreTab() {
  sideGraph = new GraphView($('side-canvas'), { name: 'side', onSelect: id => { exploreState.selected = id; }, onOpen: id => openGraph(id) });
  sideGraph.empty('Click a declaration (●) in the code to see what it connects to.');
  $('file-filter').addEventListener('input', drawTree);
  $('side-dir').addEventListener('change', () => { if (exploreState.selected) loadSideGraph(exploreState.selected); });
  $('side-open').addEventListener('click', () => { if (exploreState.selected) openGraph(exploreState.selected); });
}

async function loadFiles() {
  if (exploreState.files) return;
  $('tree').replaceChildren(el('div', { class: 'loading', text: 'Loading files…' }));
  try {
    exploreState.files = await explore('files');
    $('solution').textContent = exploreState.files.solution;
    drawTree();
  } catch (error) {
    $('tree').replaceChildren(errorBox(error.message));
  }
}

function drawTree() {
  const data = exploreState.files;
  if (!data) return;
  const filter = $('file-filter').value.trim().toLowerCase();
  const out = [];
  for (const project of data.projects) {
    const files = project.files.filter(f => !filter || f.path.toLowerCase().includes(filter));
    if (!files.length) continue;
    out.push(el('div', { class: 'proj' }, project.name, project.test ? el('span', { class: 'pill', text: 'test' }) : null, project.failed ? el('span', { class: 'pill bad', text: 'failed' }) : null));
    const prefix = commonFolder(project.files.map(f => f.path));
    const root = { folders: new Map(), files: [] };
    for (const file of files) {
      const parts = file.path.slice(prefix.length).split('/');
      let node = root;
      for (const part of parts.slice(0, -1)) {
        if (!node.folders.has(part)) node.folders.set(part, { folders: new Map(), files: [] });
        node = node.folders.get(part);
      }
      node.files.push({ ...file, name: parts[parts.length - 1] });
    }
    drawFolder(root, `${project.name}/`, 0, out, Boolean(filter));
  }
  $('tree').replaceChildren(...(out.length ? out : [el('div', { class: 'loading', text: 'No files match.' })]));
}

function drawFolder(folder, key, depth, out, open) {
  for (const [name, child] of [...folder.folders].sort((a, b) => a[0].localeCompare(b[0]))) {
    const id = key + name + '/';
    const expanded = open || exploreState.expanded.has(id);
    out.push(el('button', {
      class: 'folder', type: 'button', role: 'treeitem', 'aria-expanded': String(expanded), style: `padding-left:${10 + depth * 14}px`,
      onclick: () => { if (exploreState.expanded.has(id)) exploreState.expanded.delete(id); else exploreState.expanded.add(id); drawTree(); },
    }, name));
    if (expanded) drawFolder(child, id, depth + 1, out, open);
  }
  for (const file of folder.files.sort((a, b) => a.name.localeCompare(b.name))) {
    out.push(el('button', {
      class: `file${file.errors ? ' err' : ''}`, type: 'button', role: 'treeitem', 'aria-selected': String(file.path === exploreState.path),
      style: `padding-left:${14 + depth * 14}px`, title: file.errors ? `${file.path}: ${file.errors} binding errors` : file.path,
      onclick: () => openFile(file.path),
    }, el('span', { class: 'name', text: file.name }), el('span', { class: 'n', text: String(file.symbols) })));
  }
}

function commonFolder(paths) {
  if (!paths.length) return '';
  let prefix = paths[0].slice(0, paths[0].lastIndexOf('/') + 1);
  for (const p of paths) while (prefix && !p.startsWith(prefix)) prefix = prefix.slice(0, prefix.slice(0, -1).lastIndexOf('/') + 1);
  return prefix;
}

async function openFile(path, line, symbolId) {
  switchTab('explore');
  await loadFiles();
  exploreState.path = path;
  const project = exploreState.files && exploreState.files.projects.find(p => p.files.some(f => f.path === path));
  if (project) {
    const prefix = commonFolder(project.files.map(f => f.path));
    let key = `${project.name}/`;
    for (const part of path.slice(prefix.length).split('/').slice(0, -1)) { key += part + '/'; exploreState.expanded.add(key); }
  }
  drawTree();
  $('code').replaceChildren(el('div', { class: 'loading', text: 'Loading…' }));
  try {
    const file = await explore('file', { path });
    exploreState.symbols = file.symbols;
    const segments = path.split('/');
    $('crumbs').replaceChildren(...segments.flatMap((s, i) => (i < segments.length - 1 ? [s, ' › '] : [el('b', { text: s })])));
    const impact = file.impact;
    $('file-impact').replaceChildren(...[
      el('span', {}, el('b', { text: String(impact.dependentFiles) }), ' files depend on it'),
      el('span', {}, el('b', { text: String(impact.callSites) }), ' call sites from outside'),
      el('span', {}, el('b', { text: String(impact.typeUses) }), ' type references'),
      el('span', {}, el('b', { text: String(impact.testSites) }), ' test call sites'),
      file.errors ? el('span', { class: 'pill bad', text: `${file.errors} binding errors` }) : null].filter(Boolean));
    drawCode(file);
    const target = symbolId ? file.symbols.find(s => s.id === symbolId) : file.symbols.find(s => s.kind === 'type') || file.symbols[0];
    if (target) selectDeclaration(target);
    if (line) focusLine(line);
  } catch (error) {
    $('code').replaceChildren(errorBox(error.message));
  }
}

const KEYWORDS = new Set(('abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in init int interface internal is lock long namespace new null object operator out override params partial private protected public readonly record ref required return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile when where while with yield get set value nameof file global').split(' '));

function highlight(line, state) {
  const parts = [];
  let plain = '';
  const flush = () => { if (plain) { parts.push(document.createTextNode(plain)); plain = ''; } };
  const span = (cls, text) => { flush(); parts.push(el('span', { class: cls, text })); };
  let i = 0;
  while (i < line.length) {
    const rest = line.slice(i);
    if (state.comment) {
      const end = rest.indexOf('*/');
      const length = end < 0 ? rest.length : end + 2;
      span('tok-c', rest.slice(0, length));
      i += length;
      if (end >= 0) state.comment = false;
      continue;
    }
    if (rest.startsWith('//')) { span('tok-c', rest); break; }
    if (rest.startsWith('/*')) { state.comment = true; continue; }
    const string = /^(?:\$@|@\$|\$|@)?"(?:[^"\\]|\\.|"")*"?/.exec(rest) || /^'(?:[^'\\]|\\.)'/.exec(rest);
    if (string && string[0].length > 1) { span('tok-s', string[0]); i += string[0].length; continue; }
    const word = /^[A-Za-z_][A-Za-z0-9_]*/.exec(rest);
    if (word) {
      const w = word[0];
      if (KEYWORDS.has(w)) span('tok-k', w);
      else if (/^[A-Z]/.test(w) && !/^\s*\(/.test(rest.slice(w.length))) span('tok-t', w);
      else plain += w;
      i += w.length;
      continue;
    }
    const number = /^\d[\d_]*(?:\.\d+)?[fFdDmMuUlL]*/.exec(rest);
    if (number) { span('tok-n', number[0]); i += number[0].length; continue; }
    plain += line[i];
    i++;
  }
  flush();
  return parts;
}

function drawCode(file) {
  const code = $('code');
  if (file.text === null || file.text === undefined) {
    code.replaceChildren(errorBox('The file could not be read from disk.'));
    return;
  }
  const declarations = new Map();
  for (const s of file.symbols) if (s.line && !declarations.has(s.line)) declarations.set(s.line, s);
  const state = { comment: false };
  const lines = file.text.replace(/\r\n?/g, '\n').split('\n');
  const fragment = document.createDocumentFragment();
  lines.forEach((text, index) => {
    const number = index + 1;
    const declared = declarations.get(number);
    const row = el('div', { class: `ln${declared ? ' decl' : ''}${declared && declared.entry ? ' entry' : ''}`, id: `L${number}`, title: declared ? `${declared.kind} ${declared.display}` : null },
      el('span', { class: 'no', text: String(number) }), el('span', { class: 'mark' }), el('span', {}, ...highlight(text, state)));
    row.addEventListener('click', () => {
      const inner = innermost(number);
      if (inner) selectDeclaration(inner);
    });
    fragment.append(row);
  });
  code.replaceChildren(fragment);
}

function innermost(line) {
  return exploreState.symbols
    .filter(s => s.start <= line && line <= s.end)
    .sort((a, b) => (a.end - a.start) - (b.end - b.start))[0];
}

function focusLine(line) {
  document.querySelectorAll('.code .ln.focus').forEach(n => n.classList.remove('focus'));
  const row = $(`L${line}`);
  if (row) {
    row.classList.add('focus');
    row.scrollIntoView({ block: 'center' });
  }
}

function selectDeclaration(symbol) {
  exploreState.selected = symbol.id;
  document.querySelectorAll('.code .ln.inside').forEach(n => n.classList.remove('inside'));
  for (let line = symbol.start; line <= symbol.end; line++) $(`L${line}`)?.classList.add('inside');
  $('side-title').textContent = symbol.display;
  loadSideGraph(symbol.id);
}

async function loadSideGraph(id) {
  try {
    const data = await explore('graph', { id, dir: $('side-dir').value, depth: 1, kinds: DEFAULT_KINDS.concat(['member']).join(',') });
    sideGraph.set(data, 'tree');
  } catch (error) {
    sideGraph.empty(error.message);
  }
}

// ---- Overview, Routes, DI, Architecture ---------------------------------------------------------

async function loadOverview() {
  const view = $('view-overview');
  view.replaceChildren(el('div', { class: 'loading', text: 'Reading the index…' }));
  const [routes, di, impact, arch] = await Promise.allSettled([
    call('routes', { limit: 1 }), call('di_registrations', { limit: 1 }), call('impact', { limit: 50 }), call('architecture', { limit: 12 }),
  ]);
  const ok = r => (r.status === 'fulfilled' ? r.value.data : null);
  const r = ok(routes);
  const d = ok(di);
  const i = ok(impact);
  const a = ok(arch);
  if (a) $('solution').textContent = a.solution;
  const card = (n, label, bad, onclick) => el('button', { class: `card ${n === null ? '' : bad && n > 0 ? 'bad' : 'good'}`, type: 'button', onclick },
    el('b', { text: n === null ? '—' : String(n) }), el('span', { text: label }));
  const page = el('div', { class: 'page' },
    el('h1', { text: a ? a.solution : 'Overview' }),
    el('p', { class: 'sub', text: a ? `${a.projects.length} projects · ${a.documents} files · ${a.symbols} symbols · ${a.references} bound references · ${a.endpoints} endpoints` : '' }),
    el('div', { class: 'cards' },
      card(r ? r.none : null, 'endpoints without authorization', true, () => showRoutes('none')),
      card(r ? r.anonymous : null, 'endpoints allowing anonymous access', true, () => showRoutes('anonymous')),
      card(d ? d.captive.length : null, 'captive dependencies', true, () => switchTab('di')),
      card(d ? d.unregistered.length : null, 'injected services never registered', true, () => switchTab('di')),
      card(i ? i.endpoints.length + i.entryPoints.length : null, 'endpoints and jobs your uncommitted changes reach', false, () => $('impact-block')?.scrollIntoView({ behavior: 'smooth' }))));
  if (i) page.append(impactBlock(i));
  if (a) {
    page.append(listBlock('Most called methods', a.mostCalled, m => m.sites));
    page.append(listBlock('Most implemented interfaces', a.mostImplemented, m => m.implementations));
  }
  const failed = [routes, di, arch].find(x => x.status === 'rejected');
  if (failed) page.append(errorBox(failed.reason.message));
  view.replaceChildren(page);
}

function listBlock(title, items, count) {
  return el('section', { class: 'block' }, el('h2', {}, title, el('span', { class: 'n', text: String(items.length) })),
    el('div', { class: 'body list' }, ...items.map(m => el('div', { class: 'item' }, el('span', { class: 'num', text: String(count(m)) }), symLink(m), loc(m.file, m.line)))));
}

function impactBlock(i) {
  const total = i.endpoints.length + i.entryPoints.length;
  return el('section', { class: 'block', id: 'impact-block' }, el('h2', {}, 'Your uncommitted changes reach', el('span', { class: 'n', text: String(total) })),
    el('div', { class: 'body list' },
      total === 0 ? el('div', { class: 'muted', text: `${i.changed.length} changed symbols; none of them reaches an endpoint or job.` }) : null,
      ...i.endpoints.map(e => el('div', { class: 'item' }, el('b', { class: 'mono', text: e.verb }), el('span', { class: 'mono', text: e.route }), authPill(e.authState), symLink(e.handler), el('span', { class: 'muted', text: `via ${e.because.join(', ')}` }))),
      ...i.entryPoints.map(e => el('div', { class: 'item' }, el('span', { class: 'pill entry', text: e.entryKind }), symLink(e, e.entryLabel), el('span', { class: 'muted', text: `via ${e.because.join(', ')}` })))));
}

let routesAuth = 'any';

function showRoutes(auth) {
  routesAuth = auth || 'any';
  switchTab('routes');
}

async function loadRoutes() {
  const view = $('view-routes');
  view.replaceChildren(el('div', { class: 'loading', text: 'Loading routes…' }));
  try {
    const data = (await call('routes', { limit: 1000, auth: routesAuth })).data;
    const filter = el('input', { class: 'field', type: 'search', placeholder: 'Filter by route, handler or permission', style: 'width:320px' });
    const body = el('tbody');
    const count = el('span', { class: 'muted' });
    const draw = () => {
      const text = filter.value.trim().toLowerCase();
      const rows = data.endpoints.filter(e => !text || `${e.verb} ${e.route} ${e.handler.display} ${e.authText}`.toLowerCase().includes(text));
      body.replaceChildren(...rows.map(e => el('tr', { class: e.authState === 'declared' ? '' : 'bad' },
        el('td', { class: 'verb', text: e.verb }),
        el('td', { class: 'route', text: e.route }),
        el('td', {}, symLink(e.handler), el('br'), loc(e.file, e.line)),
        el('td', {}, authPill(e.authState), ' ', el('span', { class: 'muted', text: e.authState === 'declared' ? e.authText : '' })))));
      count.textContent = `${rows.length} shown`;
    };
    filter.addEventListener('input', draw);
    const chip = (value, label) => el('button', { class: 'btn', type: 'button', 'aria-pressed': String(routesAuth === value), text: label, onclick: () => { routesAuth = value; loadRoutes(); } });
    view.replaceChildren(el('div', { class: 'page' },
      el('h1', { text: 'Routes' }),
      el('p', { class: 'sub', text: `${data.total} endpoints: ${data.declared} declare authorization, ${data.anonymous} allow anonymous access, ${data.none} declare none. Click a handler to see its graph.` }),
      ...data.global.map(g => el('p', { class: 'sub', text: g })),
      el('div', { class: 'filters' }, filter,
        chip('any', `All ${data.total}`), chip('none', `No auth ${data.none}`), chip('anonymous', `Anonymous ${data.anonymous}`), chip('declared', `Declared ${data.declared}`), count),
      el('section', { class: 'block' }, el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, ...['Verb', 'Route', 'Handler', 'Authorization'].map(h => el('th', { text: h })))), body)))));
    draw();
  } catch (error) {
    view.replaceChildren(errorBox(error.message));
  }
}

async function loadDi() {
  const view = $('view-di');
  view.replaceChildren(el('div', { class: 'loading', text: 'Reading registrations…' }));
  try {
    const data = (await call('di_registrations', { limit: 1000 })).data;
    const block = (title, n, bad, ...content) => el('section', { class: 'block' },
      el('h2', {}, title, el('span', { class: `n${bad && n > 0 ? ' bad' : ''}`, text: String(n) })), ...content);
    view.replaceChildren(el('div', { class: 'page' },
      el('h1', { text: 'Dependency injection' }),
      el('p', { class: 'sub', text: `${data.total} registrations read from the code.` }),
      block('Captive dependencies (a singleton holding a scoped service)', data.captive.length, true,
        el('div', { class: 'body list' }, ...data.captive.map(c => el('div', { class: 'item' },
          el('span', { class: 'pill bad', text: 'captive' }), el('b', { class: 'mono', text: c.singleton.implementation }),
          el('span', { class: 'muted', text: `${c.singleton.lifetime} takes ${c.dependency} (scoped)` }), symLink(c.constructor), loc(c.constructor.file, c.constructor.line)))),
        data.captive.length ? el('div', { class: 'note', text: 'Inject IServiceScopeFactory and resolve the scoped service inside a scope per unit of work, or change a lifetime.' }) : null),
      block('Injected but never registered', data.unregistered.length, true,
        el('div', { class: 'body list' }, ...data.unregistered.map(u => el('div', { class: 'item' },
          el('span', { class: 'pill bad', text: 'missing' }), el('b', { class: 'mono', text: u.service }), el('span', { class: 'muted', text: 'asked for by' }), ...u.askedBy.map(c => symLink(c))))),
        el('div', { class: 'note', text: 'Not seen: assembly scanning (Scrutor, MediatR), registrations inside a library, or outside this solution.' })),
      block('Registered more than once', data.duplicates.length, false,
        el('div', { class: 'body list' }, ...data.duplicates.map(g => el('div', { class: 'item' },
          el('b', { class: 'mono', text: g.service }), el('span', { class: 'muted', text: g.registrations.map(r => `${r.implementation} ${r.file}:${r.line}`).join(' · ') }))))),
      block('All registrations', data.registrations.length, false, el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, ...['Lifetime', 'Service', 'Implementation', 'Detail', 'Where'].map(h => el('th', { text: h })))),
        el('tbody', {}, ...data.registrations.map(r => el('tr', {},
          el('td', { text: r.lifetime }), el('td', { class: 'mono', text: r.service }),
          el('td', { class: 'mono', text: r.implementation === r.service ? '' : r.implementation }),
          el('td', { class: 'muted', text: r.detail }), el('td', {}, loc(r.file, r.line))))))))));
  } catch (error) {
    view.replaceChildren(errorBox(error.message));
  }
}

async function loadArchitecture() {
  const view = $('view-architecture');
  const canvas = el('div', { class: 'canvas' });
  const side = el('div', { class: 'side' }, el('div', { class: 'loading', text: 'Loading…' }));
  view.replaceChildren(canvas, side);
  const archGraph = new GraphView(canvas, { name: 'arch' });
  try {
    const [projects, arch] = await Promise.all([explore('projects'), call('architecture', { limit: 15 })]);
    const a = arch.data;
    archGraph.set({
      center: null,
      nodes: projects.nodes.map(n => ({ ...n, label: `${n.display}  (${n.documents} files)` })),
      edges: projects.edges.map(e => ({ ...e, kind: `${e.count} refs`, count: 1 })),
    }, 'force');
    side.replaceChildren(
      el('h3', { class: 'mono', text: a.solution }),
      el('p', { class: 'muted', text: `${a.documents} files · ${a.symbols} symbols · ${a.references} references. Arrows: bound references from one project into another.` }),
      listBlock('Most called methods', a.mostCalled, m => m.sites),
      listBlock('Most implemented interfaces', a.mostImplemented, m => m.implementations));
  } catch (error) {
    side.replaceChildren(errorBox(error.message));
  }
}

// ---- tabs ---------------------------------------------------------------------------------------

const loaded = new Set();

function switchTab(tab) {
  document.querySelectorAll('.tabs button').forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  document.querySelectorAll('.view').forEach(v => { v.hidden = v.id !== `view-${tab}`; });
  if (tab === 'overview' && !loaded.has(tab)) { loaded.add(tab); loadOverview(); }
  if (tab === 'explore') loadFiles();
  if (tab === 'routes') loadRoutes();
  if (tab === 'di' && !loaded.has(tab)) { loaded.add(tab); loadDi(); }
  if (tab === 'architecture' && !loaded.has(tab)) { loaded.add(tab); requestAnimationFrame(loadArchitecture); }
  if (tab === 'graph' && mainGraph && mainGraph.nodes.length) requestAnimationFrame(() => mainGraph.fit());
  try { history.replaceState(null, '', `#${tab}`); } catch { /* ignore */ }
}

document.querySelectorAll('.tabs button').forEach(b => b.addEventListener('click', () => {
  if (b.dataset.tab === 'routes') routesAuth = 'any';
  switchTab(b.dataset.tab);
}));

setupGraphTab();
setupExploreTab();
call('status', {}).then(result => { if (result.data) $('solution').textContent = result.data.solution; }).catch(() => { /* shown by the first view */ });
// Deep links: #tab=graph&id=..., #tab=explore&file=...&line=..., or a plain #routes.
const link = new URLSearchParams(location.hash.slice(1));
const tabs = ['overview', 'explore', 'graph', 'routes', 'di', 'architecture'];
const firstTab = tabs.find(t => location.hash === `#${t}`) || (tabs.includes(link.get('tab')) ? link.get('tab') : 'overview');
if (link.get('id')) openGraph(link.get('id'));
else if (link.get('file')) openFile(link.get('file'), Number(link.get('line')) || null);
else switchTab(firstTab);
