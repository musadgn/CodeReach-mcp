'use strict';

// CodeReach UI. Every answer comes from POST /api/call, the same tool registry MCP clients use.
// All page text is set through textContent: nothing from the index is ever parsed as HTML.

const TOKEN_KEY = 'codereach.token';
const MODES = ['trace', 'callers', 'callees', 'usages', 'hierarchy', 'di'];
const SYMBOL_TOOLS = {
  trace: 'trace_to_entrypoints',
  callers: 'callers',
  callees: 'callees',
  usages: 'find_usages',
  hierarchy: 'type_hierarchy',
  di: 'di_registrations',
};

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
const input = $('q');
const list = $('suggestions');
const out = $('out');

let mode = 'trace';
let picked = null; // the id behind the text in the box, when chosen from suggestions or a click
let suggestions = [];
let active = -1;
let sequence = 0;

// ---- tiny DOM helper -----------------------------------------------------------------------

function el(tag, props, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'style') node.style.cssText = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function reveal(nodes) {
  nodes.forEach((node, index) => {
    node.classList.add('reveal');
    node.style.setProperty('--i', String(Math.min(index, 20)));
  });
  return nodes;
}

// ---- API ---------------------------------------------------------------------------------------

async function call(tool, args) {
  const response = await fetch('/api/call', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CodeReach-Token': token },
    body: JSON.stringify({ tool, args: args || {} }),
  });
  if (!response.ok) {
    throw new Error(response.status === 401
      ? 'This page has no valid token. Open the address codereach printed in the terminal.'
      : `${response.status} ${await response.text()}`);
  }
  const result = await response.json();
  stamp(result.data && result.data.index);
  return result;
}

function stamp(index) {
  if (!index) return;
  const state = $('state');
  const completion = $('completion');
  state.textContent = index.notice ? 'STALE · refreshing' : `gen ${index.generation} · ${index.builtAt.slice(0, 19).replace('T', ' ')}Z`;
  state.className = index.notice ? 'state-stale' : '';
  completion.textContent = index.complete ? 'analysis complete' : 'PARTIAL — rows may be missing';
  completion.className = index.complete ? '' : 'state-partial';
  if (!index.complete) completion.title = index.completion;
}

// ---- shared pieces -------------------------------------------------------------------------------

function sym(symbol, label) {
  return el('button', {
    class: 'sym',
    type: 'button',
    title: symbol.id,
    text: label || symbol.display,
    onclick: () => ask(symbol.display, symbol.id),
  });
}

function loc(file, line, short) {
  if (!file) return el('span', { class: 'loc muted', text: '(no source)' });
  const where = line ? `${file}:${line}` : file;
  const shown = short ? where.slice(where.lastIndexOf('/') + 1) : where;
  const button = el('button', { class: 'loc', type: 'button', title: `Copy ${where}`, text: shown });
  button.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(where);
      button.classList.add('copied');
      button.textContent = 'copied';
      setTimeout(() => { button.classList.remove('copied'); button.textContent = shown; }, 900);
    } catch {
      button.title = where;
    }
  });
  return button;
}

function authTag(endpoint) {
  if (endpoint.authState === 'none') return el('span', { class: 'tag bad', text: 'no auth' });
  if (endpoint.authState === 'anonymous') return el('span', { class: 'tag bad', text: 'anonymous' });
  return el('span', { class: 'tag good', text: 'auth' });
}

// Long symbol names may break only after a dot, a comma or an opening parenthesis, never inside a word.
function breakable(text) {
  const parts = String(text).split(/(?<=[.,(<])/);
  return parts.flatMap((part, index) => (index < parts.length - 1 ? [part, document.createElement('wbr')] : [part]));
}

function headline(kicker, title) {
  return el('h1', { class: 'headline' }, el('small', { text: kicker }), ...breakable(title));
}

function section(title, count, options, ...body) {
  const classes = ['section'];
  if (options && options.problem && count > 0) classes.push('problem');
  if (options && options.problem && count === 0) classes.push('empty');
  return el('section', { class: classes.join(' ') },
    el('h2', {}, count === null || count === undefined ? null : el('span', { class: 'count', text: String(count) }), title),
    ...body);
}

function textBlock(text) {
  return el('pre', { class: 'text', text });
}

function failure(message) {
  return el('div', { class: 'error' }, el('strong', { text: 'Could not answer. ' }), message);
}

function show(...nodes) {
  out.replaceChildren(...nodes.flat().filter(Boolean));
}

// ---- renderers -------------------------------------------------------------------------------------

function renderPath(path) {
  const head = el('div', { class: 'entry-head' });
  if (path.endpoints && path.endpoints.length) {
    for (const endpoint of path.endpoints) {
      head.append(
        el('span', { class: 'tag ink', text: endpoint.minimal ? 'minimal api' : 'endpoint' }),
        el('span', { class: 'verb', text: endpoint.verb }),
        el('span', { class: 'route', text: endpoint.route }),
        authTag(endpoint),
        el('span', { class: 'auth', text: endpoint.authText }));
    }
  } else if (path.entryKind) {
    head.append(el('span', { class: 'tag ink', text: path.entryKind }), el('span', { class: 'route', text: path.entryLabel }));
  } else {
    head.append(el('span', { class: 'tag', text: 'unrooted' }), el('span', { class: 'muted', text: 'nothing in the solution calls this; reached by DI, reflection, or not at all' }));
  }

  const wire = el('ul', { class: 'wire' });
  path.hops.forEach(hop => {
    const via = hop.boundTo
      ? el('span', { class: 'via', text: `called at ${hop.callFile}:${hop.callLine} through ${hop.boundTo}${hop.candidates > 1 ? ` · 1 of ${hop.candidates} possible targets` : ''}` })
      : hop.callFile ? el('span', { class: 'via', text: `called at ${hop.callFile}:${hop.callLine}` }) : null;
    wire.append(el('li', {}, sym(hop), loc(hop.file, hop.line), via));
  });
  return el('article', { class: 'entry' }, head, wire);
}

function renderTrace(result) {
  const data = result.data;
  const kinds = {};
  data.rooted.forEach(p => { kinds[p.entryKind] = (kinds[p.entryKind] || 0) + 1; });
  const summary = Object.entries(kinds).map(([k, n]) => `${n} ${k}`).join(', ') || 'none';
  const nodes = [
    headline('What reaches', data.target),
    el('p', { class: 'summary', text: `${data.rootedTotal} entry points (${summary}); ${data.unrootedTotal} unrooted tops; ${data.visited} symbols visited.${data.hiddenTestSites ? ` ${data.hiddenTestSites} call sites in test projects hidden.` : ''}` }),
  ];
  if (data.note) nodes.push(el('p', { class: 'summary muted', text: data.note }));
  if (data.truncated) nodes.push(failure('The walk hit its depth or size cap, so entry points may be missing.'));
  nodes.push(section('Entry points', data.rootedTotal, null, ...reveal(data.rooted.map(renderPath))));
  if (data.unrootedTotal) {
    nodes.push(el('details', {}, el('summary', { text: `${data.unrootedTotal} unrooted chains (nothing calls their top)` }), ...data.unrooted.map(renderPath)));
  }
  show(nodes);
}

function renderRoutes(result, args) {
  const data = result.data;
  const current = args.auth || 'any';
  const chips = el('div', { class: 'chips', role: 'group', 'aria-label': 'Authorization filter' },
    ...[['any', `all ${data.total}`], ['none', `no auth ${data.none}`], ['anonymous', `anonymous ${data.anonymous}`], ['declared', `declared ${data.declared}`]]
      .map(([value, label]) => el('button', {
        class: 'chip', type: 'button', 'aria-pressed': String(value === current), text: label,
        onclick: () => run('routes', { ...args, auth: value }),
      })));
  const rows = data.endpoints.map(e => el('tr', { class: e.authState === 'declared' ? '' : 'bad' },
    el('td', { class: 'verb', text: e.verb }),
    el('td', { class: 'route', text: e.route }),
    el('td', {}, sym(e.handler), el('br'), loc(e.file, e.line, true)),
    el('td', { class: 'authcell' }, authTag(e), ' ', el('span', { class: 'auth', text: e.authState === 'declared' ? e.authText : '' }))));
  const nodes = [
    headline('Endpoints', args.filter ? `matching ${args.filter}` : 'All routes'),
    el('p', { class: 'summary', text: `${data.shown} shown of ${data.total}. ${data.declared} declare authorization, ${data.anonymous} allow anonymous access, ${data.none} declare none.` }),
    ...data.global.map(note => el('p', { class: 'summary', text: note })),
    section('Routes', data.shown, null, chips,
      el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, ...['Verb', 'Route', 'Handler', 'Auth'].map(h => el('th', { text: h })))),
        el('tbody', {}, ...reveal(rows))))),
  ];
  show(nodes);
}

function endpointList(endpoints) {
  return reveal(endpoints.map(e => el('div', { class: 'entry-head' },
    el('span', { class: 'verb', text: e.verb }),
    el('span', { class: 'route', text: e.route }),
    authTag(e),
    sym(e.handler),
    e.because ? el('span', { class: 'auth', text: `because ${e.because.join(', ')}` }) : null)));
}

function renderImpact(result) {
  const data = result.data;
  show(
    headline('Impact of', data.source),
    el('p', { class: 'summary', text: `${data.files} files, ${data.changed.length} changed symbols, ${data.endpoints.length} endpoints and ${data.entryPoints.length} other entry points affected.` }),
    data.unmapped.length ? el('p', { class: 'summary muted', text: `Not analysed (not indexed C# or deleted): ${data.unmapped.join(', ')}` }) : null,
    section('Affected endpoints', data.endpoints.length, null, ...endpointList(data.endpoints)),
    section('Affected jobs and other entry points', data.entryPoints.length, null,
      ...reveal(data.entryPoints.map(e => el('div', { class: 'entry-head' },
        el('span', { class: 'tag ink', text: e.entryKind }), sym(e, e.entryLabel), loc(e.file, e.line),
        el('span', { class: 'auth', text: `because ${e.because.join(', ')}` }))))),
    section('Changed symbols', data.changed.length, null,
      ...reveal(data.changed.map(s => el('div', {}, sym(s), loc(s.file, s.line))))),
    section('Reach no entry point', data.unreached.length, null,
      ...reveal(data.unreached.map(s => el('div', {}, sym(s), loc(s.file, s.line))))),
  );
}

function registrationRow(r) {
  return el('tr', {},
    el('td', { text: r.lifetime }),
    el('td', { text: r.service }),
    el('td', { text: r.implementation === r.service ? '' : r.implementation }),
    el('td', { class: 'detail auth', text: r.detail }),
    el('td', { class: 'nowrap' }, loc(r.file, r.line, true)));
}

function renderDi(result) {
  const data = result.data;
  show(
    headline('Dependency injection', `${data.total} registrations`),
    section('Captive dependencies: a singleton holding a scoped service', data.captive.length, { problem: true },
      ...reveal(data.captive.map(c => el('div', { class: 'entry-head' },
        el('span', { class: 'tag bad', text: 'captive' }),
        el('span', { class: 'route', text: c.singleton.implementation }),
        el('span', { class: 'auth', text: `${c.singleton.lifetime} takes ${c.dependency} (scoped)` }),
        sym(c.constructor), loc(c.constructor.file, c.constructor.line)))),
      data.captive.length ? el('p', { class: 'summary', text: 'Inject IServiceScopeFactory and resolve the scoped service inside a scope per unit of work, or change a lifetime.' }) : null),
    section('Injected but no registration found', data.unregistered.length, { problem: true },
      ...reveal(data.unregistered.map(u => el('div', { class: 'entry-head' },
        el('span', { class: 'tag bad', text: 'missing' }),
        el('span', { class: 'route', text: u.service }),
        el('span', { class: 'auth', text: 'asked for by' }),
        ...u.askedBy.map(c => sym(c))))),
      data.unregistered.length ? el('p', { class: 'summary muted', text: 'Not seen: assembly scanning (Scrutor, MediatR), registrations inside a library, or outside this solution.' }) : null),
    section('Registered more than once', data.duplicates.length, null,
      ...reveal(data.duplicates.map(d => el('div', { class: 'entry-head' },
        el('span', { class: 'route', text: d.service }),
        el('span', { class: 'auth', text: d.registrations.map(r => `${r.implementation} ${r.file}:${r.line}`).join(' · ') }))))),
    section('All registrations', data.registrations.length, null,
      el('div', { class: 'table-wrap' }, el('table', {},
        el('thead', {}, el('tr', {}, ...['Lifetime', 'Service', 'Implementation', 'Detail', 'Where'].map(h => el('th', { text: h })))),
        el('tbody', {}, ...data.registrations.map(registrationRow))))),
  );
}

function renderArchitecture(result) {
  const data = result.data;
  const most = Math.max(1, ...data.projects.flatMap(p => p.uses.map(u => u.references)));
  show(
    headline(data.solution, `${data.projects.length} projects`),
    el('p', { class: 'summary', text: `${data.documents} documents, ${data.symbols} symbols, ${data.references} bound references, ${data.endpoints} endpoints. Entry points: ${data.entryPoints.map(e => `${e.count} ${e.kind}`).join(', ')}.` }),
    section('How much each project really uses the others', null, null,
      ...reveal(data.projects.map(p => el('div', { class: 'entry' },
        el('div', { class: 'entry-head' },
          el('span', { class: 'route', text: p.name }),
          p.test ? el('span', { class: 'tag', text: 'test' }) : null,
          p.failed ? el('span', { class: 'tag bad', text: 'failed to load' }) : null,
          el('span', { class: 'auth', text: `${p.documents} documents` })),
        el('ul', { class: 'wire' }, ...(p.uses.length ? p.uses : [{ project: 'nothing in the solution', references: 0 }]).map(u => el('li', {},
          el('span', { text: `${u.project} ` }), el('span', { class: 'muted', text: u.references ? `${u.references} references` : '' }),
          u.references ? el('span', { class: `bar${p.test ? ' test' : ''}`, style: `transform: scaleX(${(u.references / most).toFixed(3)})` }) : null))))))),
    section('Most called (dispatch resolved)', data.mostCalled.length, null,
      ...reveal(data.mostCalled.map(m => el('div', {}, el('span', { class: 'verb', text: String(m.sites).padStart(5, ' ') + '  ' }), sym(m), loc(m.file, m.line))))),
    section('Most implemented interfaces', data.mostImplemented.length, null,
      ...reveal(data.mostImplemented.map(m => el('div', {}, el('span', { class: 'verb', text: String(m.implementations).padStart(5, ' ') + '  ' }), sym(m), loc(m.file, m.line))))),
  );
}

async function renderHome() {
  show(el('p', { class: 'loading', text: 'Reading the index' }));
  const [routes, di, impact, architecture] = await Promise.allSettled([
    call('routes', { limit: 1 }),
    call('di_registrations', { limit: 1 }),
    call('impact', { limit: 1 }),
    call('architecture', { limit: 8 }),
  ]);
  const value = r => (r.status === 'fulfilled' && !r.value.isError ? r.value.data : null);
  const alarm = (n, what, bad, onclick) => el('button', { class: `alarm ${n === null ? '' : bad && n > 0 ? 'bad' : 'good'}`, type: 'button', onclick },
    el('span', { class: 'n', text: n === null ? '—' : String(n) }), el('span', { class: 'what', text: what }));
  const r = value(routes);
  const d = value(di);
  const i = value(impact);
  const a = value(architecture);
  const alarms = el('div', { class: 'alarms' }, ...reveal([
    alarm(r ? r.none : null, 'endpoints declare no authorization', true, () => ask('routes', null, { auth: 'none' })),
    alarm(r ? r.anonymous : null, 'endpoints allow anonymous access', true, () => ask('routes', null, { auth: 'anonymous' })),
    alarm(d ? d.captive.length : null, 'captive dependencies', true, () => ask('di')),
    alarm(d ? d.unregistered.length : null, 'injected services with no registration', true, () => ask('di')),
    alarm(i ? i.endpoints.length + i.entryPoints.length : null, 'endpoints and jobs your uncommitted changes reach', false, () => ask('impact')),
  ]));
  const nodes = [headline('Health report', a ? a.solution : 'this solution'), alarms];
  if (a) {
    nodes.push(section('Most called methods', a.mostCalled.length, null,
      ...reveal(a.mostCalled.map(m => el('div', {}, el('span', { class: 'verb', text: String(m.sites).padStart(5, ' ') + '  ' }), sym(m), loc(m.file, m.line))))));
  }

  const failed = [routes, di, impact, architecture].find(x => x.status === 'fulfilled' && x.value.isError);
  if (failed && !a) nodes.push(failure(failed.value.text));
  show(nodes);
}

// ---- commands -----------------------------------------------------------------------------------

function parse(text) {
  const trimmed = text.trim();
  if (!trimmed) return { kind: 'home' };
  const [word, ...rest] = trimmed.split(/\s+/);
  const tail = rest.join(' ');
  const lower = word.toLowerCase();
  if (lower === 'routes' || lower === 'route') return { kind: 'routes', args: tail ? { filter: tail } : {} };
  if (trimmed.startsWith('/') || /^(GET|POST|PUT|DELETE|PATCH)$/i.test(word)) {
    return { kind: 'routes', args: { filter: trimmed.startsWith('/') ? trimmed : tail } };
  }
  if (lower === 'impact') return { kind: 'impact', args: tail ? { base: tail } : {} };
  if (lower === 'architecture' || lower === 'arch') return { kind: 'architecture', args: { limit: 15 } };
  if (lower === 'di' && !tail) return { kind: 'di', args: { limit: 1000 } };
  return { kind: 'symbol', text: trimmed };
}

async function run(kind, args, symbolText) {
  const ticket = ++sequence;
  show(el('p', { class: 'loading', text: 'Asking the index' }));
  try {
    let result;
    let render;
    if (kind === 'routes') { result = await call('routes', { limit: 1000, ...args }); render = () => renderRoutes(result, args); }
    else if (kind === 'impact') { result = await call('impact', { limit: 200, ...args }); render = () => renderImpact(result); }
    else if (kind === 'architecture') { result = await call('architecture', args); render = () => renderArchitecture(result); }
    else if (kind === 'di') { result = await call('di_registrations', args); render = () => renderDi(result); }
    else {
      const tool = SYMBOL_TOOLS[mode];
      result = await call(tool, { symbol: symbolText, limit: 200 });
      render = mode === 'trace' && result.data ? () => renderTrace(result) : () => show(textBlock(result.text));
    }

    if (ticket !== sequence) return;
    if (result.isError) show(failure(result.text));
    else render();
  } catch (error) {
    if (ticket === sequence) show(failure(error.message));
  }
}

function ask(text, id, extra) {
  input.value = text === 'routes' && extra && extra.filter ? `routes ${extra.filter}` : text || '';
  picked = id || null;
  closeSuggestions();
  const command = parse(input.value);
  const state = { q: input.value, id: picked, mode, extra: extra || null };
  history.pushState(state, '', `#q=${encodeURIComponent(input.value)}&m=${mode}`);
  execute(command, extra);
}

function execute(command, extra) {
  if (command.kind === 'home') return renderHome();
  if (command.kind === 'symbol') return run('symbol', null, picked || command.text);
  return run(command.kind, { ...(command.args || {}), ...(extra || {}) });
}

// ---- suggestions --------------------------------------------------------------------------------

let suggestTimer = 0;

function closeSuggestions() {
  list.hidden = true;
  input.setAttribute('aria-expanded', 'false');
  suggestions = [];
  active = -1;
}

function drawSuggestions() {
  list.replaceChildren(...suggestions.map((s, index) => el('li', {
    role: 'option',
    id: `s${index}`,
    'aria-selected': String(index === active),
    onmousedown: event => { event.preventDefault(); ask(s.display, s.id); },
  }, el('span', { class: 'kind', text: s.kind }), el('span', { text: s.display }), el('span', { class: 'where', text: s.file ? `${s.file}:${s.line}` : '' }))));
  list.hidden = suggestions.length === 0;
  input.setAttribute('aria-expanded', String(!list.hidden));
  if (active >= 0) input.setAttribute('aria-activedescendant', `s${active}`);
  else input.removeAttribute('aria-activedescendant');
}

input.addEventListener('input', () => {
  picked = null;
  clearTimeout(suggestTimer);
  const command = parse(input.value);
  if (command.kind !== 'symbol' || input.value.trim().length < 2) {
    closeSuggestions();
    return;
  }

  const query = input.value.trim();
  suggestTimer = setTimeout(async () => {
    try {
      const result = await call('find_symbol', { query, limit: 8 });
      if (input.value.trim() !== query || !result.data) return;
      suggestions = result.data.symbols;
      active = -1;
      drawSuggestions();
    } catch {
      closeSuggestions();
    }
  }, 160);
});

input.addEventListener('keydown', event => {
  if (event.key === 'ArrowDown' && suggestions.length) {
    event.preventDefault();
    active = (active + 1) % suggestions.length;
    drawSuggestions();
  } else if (event.key === 'ArrowUp' && suggestions.length) {
    event.preventDefault();
    active = (active - 1 + suggestions.length) % suggestions.length;
    drawSuggestions();
  } else if (event.key === 'Enter') {
    event.preventDefault();
    if (active >= 0) ask(suggestions[active].display, suggestions[active].id);
    else ask(input.value, picked);
  } else if (event.key === 'Escape') {
    closeSuggestions();
  } else if (event.key === 'Tab' && !event.shiftKey && list.hidden) {
    event.preventDefault();
    setMode(MODES[(MODES.indexOf(mode) + 1) % MODES.length]);
  }
});

input.addEventListener('blur', () => setTimeout(closeSuggestions, 100));

function setMode(next) {
  mode = next;
  document.querySelectorAll('.mode').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.mode === mode)));
  if (parse(input.value).kind === 'symbol') ask(input.value, picked);
}

document.querySelectorAll('.mode').forEach(button => button.addEventListener('click', () => setMode(button.dataset.mode)));

document.addEventListener('keydown', event => {
  if (event.key === '/' && document.activeElement !== input) {
    event.preventDefault();
    input.focus();
    input.select();
  }
});

window.addEventListener('popstate', event => {
  const state = event.state || {};
  input.value = state.q || '';
  picked = state.id || null;
  if (state.mode) {
    mode = state.mode;
    document.querySelectorAll('.mode').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.mode === mode)));
  }
  execute(parse(input.value), state.extra);
});

// ---- start --------------------------------------------------------------------------------------

(async () => {
  // A deep link (#q=...&m=...) runs that question; the token never stays in the address bar.
  const link = new URLSearchParams(location.hash.slice(1));
  const linked = link.get('q') || '';
  if (MODES.includes(link.get('m'))) {
    mode = link.get('m');
    document.querySelectorAll('.mode').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.mode === mode)));
  }
  history.replaceState({ q: linked, mode }, '', linked ? `#q=${encodeURIComponent(linked)}&m=${mode}` : location.pathname);
  try {
    const status = await call('status', {});
    if (status.data) $('solution').textContent = status.data.solution;
  } catch {
    // The answer below shows the real failure.
  }
  input.focus();
  input.value = linked;
  execute(parse(linked));
})();
