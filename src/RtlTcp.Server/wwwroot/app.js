// rtltcp.server – Weboberfläche. Kein Framework, keine externen Skripte; DOM wird ohne innerHTML aufgebaut.
'use strict';

const app = document.getElementById('app');
const nav = document.getElementById('nav');
let session = null;
let pollTimer = null;
const rateSamples = {}; // port -> { stick, sent, client, since, time }

const SAMPLE_RATES = [250000, 1024000, 1536000, 1800000, 1920000, 2048000, 2400000, 2560000, 2880000, 3200000];
const GAINS = [0, 9, 14, 27, 37, 77, 87, 125, 144, 157, 166, 197, 207, 229, 254, 280, 297, 328, 338, 364, 372, 386,
  402, 421, 434, 439, 445, 480, 483, 488, 496];

// ------------------------------------------------------------------ helpers

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function api(method, path, body) {
  const headers = { 'X-RTLTCP': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch('api' + path, {
    method, headers, credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const isJson = (res.headers.get('content-type') || '').includes('json');
  const data = isJson ? await res.json() : null;
  if (res.status === 401 && !['/login', '/setup'].includes(path)) {
    session.loggedIn = false;
    route();
    throw new ApiError('Bitte erneut anmelden', 401);
  }
  if (!res.ok) throw new ApiError((data && data.error) || `Fehler ${res.status}`, res.status);
  return data;
}

let toastTimer;
function toast(message, isError) {
  document.querySelector('.toast')?.remove();
  clearTimeout(toastTimer);
  const t = h('div', { class: 'toast' + (isError ? ' err' : ''), role: 'status', text: message });
  document.body.append(t);
  toastTimer = setTimeout(() => t.remove(), isError ? 6000 : 3000);
}

async function guarded(button, action) {
  if (button) button.disabled = true;
  try { await action(); }
  catch (e) { if (e.status !== 401) toast(e.message, true); }
  finally { if (button) button.disabled = false; }
}

const nf = (digits) => new Intl.NumberFormat('de-AT', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const fmtMHz = (hz) => hz ? nf(3).format(hz / 1e6) + ' MHz' : '–';
const fmtRate = (sps) => sps ? nf(3).format(sps / 1e6) + ' MS/s' : '–';
const fmtMbit = (v) => v === null || v === undefined ? '…' : nf(1).format(v) + ' Mbit/s';
const fmtGain = (g) => g === null || g === undefined ? 'automatisch' : nf(1).format(g / 10) + ' dB';

function fmtSince(iso) {
  if (!iso) return '';
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `seit ${s} s`;
  if (s < 3600) return `seit ${Math.floor(s / 60)} min`;
  return `seit ${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`;
}

function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', { text: label }), input, hint ? h('small', { text: hint }) : null);
}

function switchEl(checked, onchange, label) {
  return h('label', { class: 'switch' },
    h('input', { type: 'checkbox', checked, onchange: (e) => onchange(e.target.checked) }), h('i'), label ? h('span', { text: label }) : null);
}

/** Like replaceChildren, but skips null/false (replaceChildren would insert the text "null"). */
function fill(el, ...children) {
  el.replaceChildren(...children.flat(Infinity).filter((c) => c !== null && c !== undefined && c !== false));
}

function setPage(...children) {
  fill(app, ...children);
}

// ------------------------------------------------------------------ routing

const pages = { status: renderStatus, devices: renderDevices, logs: renderLogs, settings: renderSettings };

async function loadSession() {
  session = await api('GET', '/session');
  document.getElementById('version').textContent = session.version;
}

function route() {
  clearInterval(pollTimer);
  pollTimer = null;
  if (!session.loggedIn) { nav.hidden = true; return renderLogin(); }
  if (session.mustChangePassword) { nav.hidden = true; return renderForcePasswordChange(); }
  nav.hidden = false;
  document.getElementById('logout').hidden = !!session.ingress; // in Home Assistant, HA handles the login
  const page = (location.hash.match(/^#\/(\w+)/) || [])[1];
  const name = pages[page] ? page : 'status';
  for (const a of nav.querySelectorAll('a')) a.toggleAttribute('aria-current', a.getAttribute('href') === '#/' + name);
  for (const a of nav.querySelectorAll('a[aria-current]')) a.setAttribute('aria-current', 'page');
  pages[name]();
}

window.addEventListener('hashchange', route);
document.getElementById('logout').addEventListener('click', () => guarded(null, async () => {
  await api('POST', '/logout');
  session.loggedIn = false;
  route();
}));

// ------------------------------------------------------------------ login & first password

let lastLoginPassword = null; // only kept in memory to make the forced change a one-step form

function renderForcePasswordChange() {
  const cur = h('input', { type: 'password', autocomplete: 'current-password', required: true });
  const pw = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 8 });
  const pw2 = h('input', { type: 'password', autocomplete: 'new-password', required: true });
  const submit = h('button', { class: 'btn primary', type: 'submit', text: 'Passwort speichern' });
  const form = h('form', {
    onsubmit: (e) => {
      e.preventDefault();
      if (pw.value !== pw2.value) return toast('Die Passwörter stimmen nicht überein', true);
      guarded(submit, async () => {
        await api('PUT', '/admin/password', { current: lastLoginPassword ?? cur.value, new: pw.value });
        lastLoginPassword = null;
        await loadSession();
        location.hash = '#/status';
        route();
        toast('Passwort geändert – willkommen!');
      });
    },
  },
  lastLoginPassword === null ? field('Aktuelles Passwort', cur) : null,
  field('Neues Passwort', pw, 'mindestens 8 Zeichen'), field('Neues Passwort wiederholen', pw2), submit);

  setPage(h('div', { class: 'auth-box card' },
    h('img', { class: 'logo', src: 'logo.svg', alt: '' }),
    h('h1', { text: 'Neues Passwort' }),
    h('p', { class: 'lead', text: `Du bist mit dem Standardpasswort angemeldet. Bitte lege jetzt ein eigenes Passwort für „${session.user}“ fest.` }),
    form));
  (lastLoginPassword === null ? cur : pw).focus();
}

function renderLogin() {
  const user = h('input', { type: 'text', autocomplete: 'username', required: true });
  const pw = h('input', { type: 'password', autocomplete: 'current-password', required: true });
  const submit = h('button', { class: 'btn primary', type: 'submit', text: 'Anmelden' });
  const form = h('form', {
    onsubmit: (e) => {
      e.preventDefault();
      guarded(submit, async () => {
        await api('POST', '/login', { user: user.value.trim(), password: pw.value });
        await loadSession();
        lastLoginPassword = session.mustChangePassword ? pw.value : null;
        route();
      });
    },
  }, field('Benutzername', user), field('Passwort', pw), submit);
  setPage(h('div', { class: 'auth-box card' },
    h('img', { class: 'logo', src: 'logo.svg', alt: '' }),
    h('h1', { text: 'Anmelden' }),
    h('p', { class: 'lead', text: 'rtltcp.server – Verwaltung' }),
    session.defaultLoginHint ? h('div', { class: 'notice ok' }, h('p', {}, 'Erste Anmeldung: Benutzer ',
      h('code', { text: 'admin' }), ', Passwort ', h('code', { text: 'admin' }), '. Danach legst du ein eigenes Passwort fest.')) : null,
    form));
  user.focus();
}

// ------------------------------------------------------------------ status

const STATE = {
  ready: ['ok', 'Bereit'],
  idle: ['', 'Ruht – Stick aus'],
  starting: ['warn', 'Startet'],
  notready: ['warn', 'Stick nicht bereit'],
  lost: ['err', 'Stick getrennt – verbinde neu'],
  disabled: ['', 'Deaktiviert'],
  error: ['err', 'Fehler'],
};

async function renderStatus() {
  const head = h('div', { class: 'page-head' }, h('h1', { text: 'Status' }));
  const body = h('div');
  setPage(head, body);

  const refresh = async () => {
    let s;
    try { s = await api('GET', '/status'); } catch (e) { if (e.status !== 401) body.replaceChildren(h('div', { class: 'notice err', text: e.message })); return; }
    fill(head, h('h1', { text: 'Status' }));

    const now = performance.now();
    const cards = s.devices.map((d) => {
      // real data rates from the byte counters between two polls (Mbit/s); null until two samples exist
      const prev = rateSamples[d.port];
      rateSamples[d.port] = { stick: d.bytesStreamed, sent: d.clientBytesSent, client: d.client, since: d.clientSince, time: now };
      const secs = prev ? (now - prev.time) / 1000 : 0;
      const same = prev && prev.client === d.client && prev.since === d.clientSince && secs > 0.5;
      const mbit = (cur, old) => same && cur >= old ? (cur - old) * 8 / secs / 1e6 : null;
      const inRate = mbit(d.bytesStreamed, prev && prev.stick);
      const outRate = mbit(d.clientBytesSent, prev && prev.sent);
      const expected = d.sampleRate ? d.sampleRate * 16 / 1e6 : null; // 8-bit I + 8-bit Q per sample
      const rateEl = (v) => h('span', { class: v !== null && expected && v < expected * 0.9 ? 'rate-low' : null, text: fmtMbit(v) });
      const [cls, label] = d.client && d.state === 'ready' ? ['ok live', 'Streamt'] : (STATE[d.state] || ['', d.state]);
      return h('section', { class: 'card' },
        h('div', { class: 'card-head' },
          h('h3', {}, d.name, h('small', { text: `Port ${d.port}` })),
          h('span', { class: 'badge ' + cls, text: label })),
        h('dl', { class: 'facts' },
          h('dt', { text: 'Stick' }), h('dd', { text: (d.origin ? d.origin + (d.serial ? ` · ${d.serial}` : '') : (d.serial || 'erster freier')) + (d.tuner ? ` · ${d.tuner}` : '') }),
          d.location || d.antenna ? [h('dt', { text: 'Standort' }), h('dd', { text: [d.location, d.antenna].filter(Boolean).join(' · ') })] : null,
          h('dt', { text: 'Client' }), h('dd', { text: d.client ? `${d.client} (${fmtSince(d.clientSince)})${d.clientProtocol ? ' · ' + d.clientProtocol : ''}` : 'keiner' }),
          d.client ? [h('dt', { text: 'Frequenz' }), h('dd', { text: fmtMHz(d.frequency) })] : null,
          d.client ? [h('dt', { text: 'Samplerate' }), h('dd', { text: fmtRate(d.sampleRate) })] : null,
          d.client ? [h('dt', { text: 'Gain' }), h('dd', { text: fmtGain(d.gain) + (d.biasTee ? ' · Bias-Tee an' : '') })] : null,
          d.client ? [h('dt', { text: 'Datenrate erwartet' }), h('dd', { text: expected ? `${fmtMbit(expected)} (${fmtRate(d.sampleRate)} × 2 Byte)` : '–' })] : null,
          d.client ? [h('dt', { text: d.isRemote ? 'Empfangen (Internet)' : 'Vom Stick' }), h('dd', {}, rateEl(inRate))] : null,
          d.client ? [h('dt', { text: d.clientIsRemoteSite ? 'Gesendet (Internet)' : 'An den Client' }), h('dd', {}, rateEl(outRate))] : null,
          d.client && d.clientDropped ? [h('dt', { text: 'Verworfen' }),
            h('dd', { class: 'rate-low', text: `${d.clientDropped} Puffer – die Verbindung schafft die Datenrate nicht` })] : null,
          ['notready', 'lost', 'error'].includes(d.state) ? [h('dt', { text: 'Meldung' }), h('dd', { text: d.message })] : null),
        d.client && (d.isRemote || d.clientIsRemoteSite) ? h('p', { class: 'muted small',
          text: 'Weniger Datenrate übers Internet: im Client eine kleinere Samplerate wählen, z. B. 1,024 MS/s ≈ 16 Mbit/s.' }) : null,
        d.client ? h('div', { class: 'buttons' }, h('button', {
          class: 'btn small danger', text: 'Client trennen',
          onclick: (e) => guarded(e.target, async () => {
            if (!confirm(`Client ${d.client} von „${d.name}“ trennen?`)) return;
            await api('POST', `/devices/${d.port}/kick`);
            toast('Client getrennt');
            refresh();
          }),
        })) : null);
    });

    const fresh = s.sticks.filter((x) => x.serial && !x.configured);
    fill(body,
      s.updateVersion ? h('div', { class: 'notice ok' },
        h('p', {}, h('strong', { text: `Update verfügbar: Version ${s.updateVersion}` }), ` (installiert: ${s.version}). `,
          s.updateCanInstall
            ? h('button', { class: 'btn small primary', text: 'Jetzt aktualisieren', onclick: (e) => startUpdate(e.target) })
            : h('a', { href: '#/settings', text: 'Details' }))) : null,
      fresh.length ? h('div', { class: 'notice' },
        h('p', {}, h('strong', { text: 'Neuer Stick erkannt: ' }), fresh.map((x) => `${x.name} (${x.serial})`).join(', '), ' – ',
          h('a', { href: '#/devices', text: 'unter Geräte eintragen' }))) : null,
      s.devices.length ? h('div', { class: 'cards' }, cards)
        : h('div', { class: 'notice', text: 'Noch kein Gerät eingetragen.' }),
      s.remote.error ? h('p', { class: 'muted', text: `Teilen über das Internet: ${s.remote.error}` }) : null);
  };
  await refresh();
  pollTimer = setInterval(refresh, 2000);
}

// ------------------------------------------------------------------ devices

async function renderDevices() {
  let devices, status, instances;
  try {
    let inst;
    [devices, status, inst] = await Promise.all([api('GET', '/devices'), api('GET', '/status'), api('GET', '/remote/instances')]);
    instances = inst.instances;
  } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }
  const INST_LABEL = { connecting: 'verbindet …', disconnected: 'getrennt', disabled: 'deaktiviert', revoked: 'widerrufen', stopped: 'gestoppt' };
  for (const d of devices) if (d.remote && d.remote.instance === undefined) d.remote.instance = '';

  const sticks = status.sticks.filter((s) => s.serial);
  const list = h('div');

  const nextPort = () => Math.max(1233, ...devices.map((d) => d.port)) + 1;
  const add = () => {
    const free = sticks.find((s) => !devices.some((d) => d.serial === s.serial));
    devices.push({
      serial: free ? free.serial : '', name: free ? `RTL-SDR ${free.serial}` : 'RTL-SDR', enabled: true,
      bind: '0.0.0.0', port: nextPort(), idleTimeoutSeconds: 30, v1WaitForDeviceSeconds: 10,
      defaults: { frequency: 100000000, sampleRate: 2048000, gain: null, ppm: 0, biasTee: false },
    });
    draw();
  };
  const SHARE_STATE = { ready: 'bereit', busy: 'belegt', idle: 'ruht', device_not_ready: 'nicht bereit', device_lost: 'getrennt' };
  const instLabel = (i) => i.state === 'connected' ? 'verbunden' : INST_LABEL[i.state] || i.state;
  const shareText = (s) => [s.location, s.antenna, s.tuner, s.online ? SHARE_STATE[s.state] || s.state : 'offline'].filter(Boolean).join(' · ');
  const fromShare = (d, s) => { // name, location and antenna as the providing server announces them
    d.name = `${s.name} @ ${s.provider}`;
    d.location = s.location || '';
    d.antenna = s.antenna || '';
  };

  // "+ Entfernter Stick": 1. instance, 2. one of the sticks shared there
  let picker = null; // { instance, share }
  const addRemote = () => {
    const first = instances.find((i) => i.state === 'connected') || instances[0];
    picker = { instance: first ? first.id : '', share: '' };
    draw();
    list.scrollIntoView({ behavior: 'smooth' });
  };
  const pickerCard = () => {
    const p = picker;
    const cancel = h('button', { class: 'btn', type: 'button', text: 'Abbrechen', onclick: () => { picker = null; draw(); } });
    const head = h('div', { class: 'card-head' }, h('h3', { text: 'Entfernten Stick hinzufügen' }));
    if (!instances.length) {
      return h('section', { class: 'card' }, head,
        h('div', { class: 'notice' }, h('p', {}, 'Noch keine Instanz von rtl.over.internet eingetragen – ',
          h('a', { href: '#/settings', text: 'unter Einstellungen hinzufügen' }), '.')),
        h('div', { class: 'buttons' }, cancel));
    }
    const inst = h('select', { onchange: (e) => { p.instance = e.target.value; p.share = ''; draw(); } },
      instances.map((i) => h('option', { value: i.id, text: `${i.name || i.url} (${instLabel(i)})` })));
    inst.value = p.instance;
    const i = instances.find((x) => x.id === p.instance);
    const taken = new Set(devices.filter((d) => d.remote).map((d) => d.remote.share));
    const shares = (i ? i.shares : []).filter((s) => !s.own);
    const ok = h('button', { class: 'btn primary', type: 'button', text: 'Stick übernehmen', disabled: !p.share, onclick: () => {
      const s = shares.find((x) => x.shareId === p.share);
      if (!s) return;
      const d = {
        serial: '', enabled: true, bind: '0.0.0.0', port: nextPort(), idleTimeoutSeconds: 30, v1WaitForDeviceSeconds: 10,
        defaults: { frequency: 100000000, sampleRate: 2048000, gain: null, ppm: 0, biasTee: false },
        remote: { instance: p.instance, share: s.shareId },
      };
      fromShare(d, s);
      devices.push(d);
      picker = null;
      draw();
      toast('Übernommen – zum Aktivieren unten speichern');
    } });
    let body;
    if (!i || i.state !== 'connected') {
      body = h('div', { class: 'notice', text: `Instanz ${instLabel(i || {})}: die freigegebenen Sticks sind erst sichtbar, wenn sie verbunden ist.` });
    } else if (!shares.length) {
      body = h('div', { class: 'notice', text: 'In dieser Instanz teilt noch kein anderer Server einen Stick.' });
    } else {
      body = h('div', { class: 'share-list' }, shares.map((s) => h('label', { class: 'share-item' + (s.online ? '' : ' off') },
        h('input', { type: 'radio', name: 'pick-share', value: s.shareId, checked: p.share === s.shareId, disabled: taken.has(s.shareId),
          onchange: () => { p.share = s.shareId; ok.disabled = false; } }),
        h('span', {}, h('strong', { text: s.name }), ` @ ${s.provider}`,
          h('small', { text: shareText(s) + (taken.has(s.shareId) ? ' · schon eingetragen' : '') })))));
    }
    return h('section', { class: 'card' }, head,
      h('div', { class: 'form-grid' }, field('1. Instanz', inst, 'von rtl.over.internet')),
      h('h3', { class: 'section-title', text: '2. Stick auswählen' }), body,
      h('div', { class: 'buttons' }, ok, cancel));
  };

  // an existing remote stick: instance and stick can be changed
  const remoteEditor = (d) => {
    const r = d.remote;
    if (!r.instance) {
      return h('div', { class: 'notice err', text: 'Dieser Stick nutzt die Direktverbindung (Testbetrieb), die es nicht mehr gibt. Bitte das Gerät entfernen und über „+ Entfernter Stick“ neu anlegen.' });
    }
    const box = h('div');
    const render = () => {
      const mode = h('select', { onchange: (e) => { r.instance = e.target.value; r.share = ''; render(); } },
        instances.map((i) => h('option', { value: i.id, text: `${i.name || i.url} (${instLabel(i)})` })));
      if (!instances.some((i) => i.id === r.instance)) mode.append(h('option', { value: r.instance, text: 'Instanz entfernt' }));
      mode.value = r.instance;
      const i = instances.find((x) => x.id === r.instance);
      const shares = (i ? i.shares : []).filter((s) => !s.own);
      const pick = h('select', { onchange: (e) => {
        r.share = e.target.value;
        const s = shares.find((x) => x.shareId === r.share);
        if (s) { fromShare(d, s); draw(); }
      } },
      h('option', { value: '', text: shares.length ? 'Stick auswählen …' : 'In dieser Instanz ist nichts freigegeben' }),
      shares.map((s) => h('option', { value: s.shareId, text: `${s.name} @ ${s.provider}${s.online ? '' : ' (offline)'}` })));
      if (r.share && !shares.some((s) => s.shareId === r.share)) pick.append(h('option', { value: r.share, text: `${r.share} (derzeit nicht freigegeben)` }));
      pick.value = r.share;
      fill(box, h('h3', { text: 'Entfernter Stick', class: 'section-title' }),
        h('div', { class: 'form-grid' }, field('Instanz', mode),
          field('Freigegebener Stick', pick, i && i.state !== 'connected' ? `Instanz ${instLabel(i)}` : 'Liste kommt von rtl.over.internet')));
    };
    render();
    return box;
  };

  const num =(value, set, attrs) => h('input', { type: 'number', value, ...attrs, oninput: (e) => set(e.target.value === '' ? 0 : Number(e.target.value)) });

  const deviceCard = (d, i) => {
    const serialOptions = [h('option', { value: '', text: 'Erster freier Stick' })];
    for (const s of sticks) serialOptions.push(h('option', { value: s.serial, text: `${s.serial} – ${s.name}` }));
    if (d.serial && !sticks.some((s) => s.serial === d.serial)) serialOptions.push(h('option', { value: d.serial, text: `${d.serial} (nicht verbunden)` }));
    const serial = h('select', { onchange: (e) => { d.serial = e.target.value; } }, serialOptions);
    serial.value = d.serial;

    const rate = h('select', { onchange: (e) => { d.defaults.sampleRate = Number(e.target.value); } },
      SAMPLE_RATES.map((r) => h('option', { value: r, text: fmtRate(r) })));
    rate.value = d.defaults.sampleRate;
    if (rate.value === '') rate.append(h('option', { value: d.defaults.sampleRate, text: fmtRate(d.defaults.sampleRate), selected: true }));

    const gain = h('select', { onchange: (e) => { d.defaults.gain = e.target.value === '' ? null : Number(e.target.value); } },
      h('option', { value: '', text: 'Automatisch (AGC)' }), GAINS.map((g) => h('option', { value: g, text: fmtGain(g) })));
    gain.value = d.defaults.gain === null ? '' : d.defaults.gain;

    return h('section', { class: 'card' },
      h('div', { class: 'card-head' },
        h('h3', { text: d.name || 'Neues Gerät' }),
        switchEl(d.enabled, (v) => { d.enabled = v; }, 'aktiv')),
      h('div', { class: 'form-grid' },
        field('Name', h('input', { type: 'text', value: d.name, maxlength: 64, oninput: (e) => { d.name = e.target.value; } })),
        field('Standort', h('input', { type: 'text', value: d.location || '', maxlength: 100, placeholder: 'z. B. Wien Dach', oninput: (e) => { d.location = e.target.value; } }),
          d.remote ? 'vom teilenden Server übernommen; seine Angabe hat Vorrang' : 'sehen die Clients, z. B. home.radio'),
        field('Antenne', h('input', { type: 'text', value: d.antenna || '', maxlength: 100, placeholder: 'z. B. DAB-Rundstrahler, Discone', oninput: (e) => { d.antenna = e.target.value; } }),
          d.remote ? 'vom teilenden Server übernommen; seine Angabe hat Vorrang' : 'Antennentyp, frei wählbar'),
        d.remote ? null : field('Stick', serial, 'über die Seriennummer zugeordnet'),
        field('Port', num(d.port, (v) => { d.port = v; }, { min: 1, max: 65535 })),
        field('Adresse', h('input', { type: 'text', value: d.bind, oninput: (e) => { d.bind = e.target.value.trim(); } }), '0.0.0.0 = alle Netzwerkkarten'),
        field('Stick abschalten nach', num(d.idleTimeoutSeconds, (v) => { d.idleTimeoutSeconds = v; }, { min: 0 }), 'Sekunden ohne Client, 0 = immer an'),
        field('v1-Client wartet auf Stick', num(d.v1WaitForDeviceSeconds, (v) => { d.v1WaitForDeviceSeconds = v; }, { min: 0, max: 300 }), 'Sekunden, danach wird getrennt')),
      d.remote ? remoteEditor(d) : null,
      h('h3', { text: 'Standardwerte bei jeder neuen Verbindung', class: 'section-title' }),
      h('div', { class: 'form-grid' },
        field('Frequenz (MHz)', h('input', {
          type: 'number', step: '0.001', min: 24, max: 1766, value: d.defaults.frequency / 1e6,
          oninput: (e) => { d.defaults.frequency = Math.round(Number(e.target.value) * 1e6); },
        })),
        field('Samplerate', rate),
        field('Gain', gain, 'bei starkem Sender in der Nähe fest einstellen'),
        field('Korrektur (ppm)', num(d.defaults.ppm, (v) => { d.defaults.ppm = Math.round(v); }, { min: -1000, max: 1000 }))),
      h('div', { class: 'buttons' },
        switchEl(d.defaults.biasTee, (v) => { d.defaults.biasTee = v; }, 'Bias-Tee (Antennenspeisung)'),
        h('button', { class: 'btn small danger', type: 'button', text: 'Gerät entfernen', onclick: () => {
          if (confirm(`„${d.name}“ entfernen?`)) { devices.splice(i, 1); draw(); }
        } })));
  };

  const save = h('button', {
    class: 'btn primary', text: 'Speichern',
    onclick: (e) => guarded(e.target, async () => {
      await api('PUT', '/devices', devices);
      toast('Gespeichert – Stream-Server neu gestartet');
      renderDevices();
    }),
  });

  function draw() {
    list.replaceChildren(...(picker ? [pickerCard()] : []), ...devices.map(deviceCard));
  }
  draw();

  setPage(
    h('div', { class: 'page-head' }, h('h1', { text: 'Geräte' }),
      h('div', { class: 'buttons' },
        h('button', { class: 'btn', text: '+ Entfernter Stick', onclick: addRemote }),
        h('button', { class: 'btn', text: '+ Gerät hinzufügen', onclick: add }))),
    h('p', { class: 'lead', text: 'Jeder Stick bekommt einen eigenen Port. Clients verbinden sich mit Server-Adresse und diesem Port (rtl_tcp).' }),
    status.devices.some((d) => d.client) ? h('div', { class: 'notice' },
      h('p', { text: 'Speichern startet die Stream-Server neu – verbundene Clients werden dabei getrennt.' })) : null,
    list,
    h('div', { class: 'buttons' }, save));
}

// ------------------------------------------------------------------ logs

async function renderLogs() {
  let files;
  try { files = await api('GET', '/logs'); } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }
  if (!files.length) return setPage(h('h1', { text: 'Protokoll' }), h('div', { class: 'card muted', text: 'Noch keine Protokolldateien.' }));

  const state = { file: files[0].name, level: 'all', search: '', lines: 500, live: true };
  const fmtSize = (b) => b < 1024 ? `${b} B` : b < 1048576 ? `${nf(1).format(b / 1024)} KB` : `${nf(1).format(b / 1048576)} MB`;

  const file = h('select', { onchange: (e) => { state.file = e.target.value; state.live = state.file === files[0].name; liveSwitch.querySelector('input').checked = state.live; load(); } },
    files.map((f) => h('option', { value: f.name, text: `${f.name.slice(7, 17)} (${fmtSize(f.size)})` })));
  const level = h('select', { onchange: (e) => { state.level = e.target.value; draw(); } },
    h('option', { value: 'all', text: 'Alle' }), h('option', { value: 'warn', text: 'Warnungen und Fehler' }),
    h('option', { value: 'error', text: 'Nur Fehler' }));
  const lines = h('select', { onchange: (e) => { state.lines = Number(e.target.value); load(); } },
    [200, 500, 2000, 5000].map((n) => h('option', { value: n, text: `${n} Zeilen` })));
  lines.value = 500;
  const search = h('input', { type: 'text', placeholder: 'z. B. IP, Port, Fehlertext', oninput: (e) => { state.search = e.target.value.toLowerCase(); draw(); } });
  const liveSwitch = switchEl(true, (v) => { state.live = v; }, 'live (alle 3 s)');
  const download = h('a', { class: 'btn small', text: 'Herunterladen' });
  const pre = h('pre', { class: 'log' });
  let raw = [];

  function draw() {
    const filtered = raw.filter((l) => {
      if (state.level === 'warn' && !/ (WARN|ERROR|CRIT) /.test(l)) return false;
      if (state.level === 'error' && !/ (ERROR|CRIT) /.test(l)) return false;
      return !state.search || l.toLowerCase().includes(state.search);
    });
    const atBottom = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 30;
    fill(pre, filtered.length ? filtered.map((l) => h('span', {
      class: / (ERROR|CRIT) /.test(l) ? 'l-err' : / WARN /.test(l) ? 'l-warn' : / DEBUG /.test(l) ? 'l-debug' : null,
      text: l + '\n',
    })) : h('span', { class: 'muted', text: 'Keine passenden Einträge.' }));
    if (atBottom) pre.scrollTop = pre.scrollHeight;
  }

  async function load() {
    download.href = `api/logs/${encodeURIComponent(state.file)}?download=true`;
    const res = await fetch(`api/logs/${encodeURIComponent(state.file)}?tail=${state.lines}`, { credentials: 'same-origin' });
    if (res.status === 401) { session.loggedIn = false; return route(); }
    raw = (await res.text()).split('\n').filter(Boolean);
    draw();
  }

  setPage(
    h('div', { class: 'page-head' }, h('h1', { text: 'Protokoll' }), download),
    h('div', { class: 'card' },
      h('div', { class: 'form-grid' }, field('Tag', file), field('Stufe', level), field('Umfang', lines), field('Suche', search)),
      h('div', { class: 'buttons' }, liveSwitch)),
    pre);
  await load();
  pre.scrollTop = pre.scrollHeight;
  pollTimer = setInterval(() => { if (state.live) load().catch(() => {}); }, 3000);
}

// ------------------------------------------------------------------ updates

/** Starts the update, then waits until the restarted server answers with a new version and reloads. */
async function startUpdate(button) {
  if (!confirm('Update jetzt installieren? Der Server startet dabei neu, verbundene Clients werden kurz getrennt.')) return;
  await guarded(button, async () => {
    const before = session.version;
    await api('POST', '/update/install');
    clearInterval(pollTimer);
    pollTimer = null;
    setPage(h('div', { class: 'auth-box card' },
      h('img', { class: 'logo', src: 'logo.svg', alt: '' }),
      h('h1', { text: 'Update läuft …' }),
      h('p', { class: 'lead', text: 'Das neue Paket wird geladen und installiert, danach startet der Server neu. Diese Seite lädt sich automatisch neu.' })));
    const started = Date.now();
    const wait = async () => {
      try {
        const res = await fetch('api/session', { credentials: 'same-origin' });
        if (res.ok) {
          const s = await res.json();
          if (s.version !== before) { location.reload(); return; }
          const u = s.loggedIn ? await (await fetch('api/update', { credentials: 'same-origin' })).json() : null;
          if (u && u.state === 'failed') { toast(u.message, true); session = s; return route(); }
        }
      } catch { /* server restarting */ }
      if (Date.now() - started > 15 * 60 * 1000) { toast('Das Update dauert ungewöhnlich lange – siehe Protokoll.', true); return route(); }
      setTimeout(wait, 3000);
    };
    setTimeout(wait, 5000);
  });
}

function updateCard(u) {
  const card = h('section', { class: 'card' });
  const time = h('input', { type: 'time', value: u.time, required: true });
  const model = { autoUpdate: u.autoUpdate };
  const modeText = {
    windows: 'Updates werden mit dem Windows-Installer eingespielt, der Dienst startet dabei kurz neu.',
    linux: 'Updates werden als Paket installiert (rtltcp-server-update), der Dienst startet dabei kurz neu.',
    homeassistant: 'Updates für das Add-on zeigt und installiert Home Assistant (Einstellungen → Updates).',
    manual: 'Diese Installation (z. B. aus dem Quellcode gestartet) kann sich nicht selbst aktualisieren – Updates werden nur angezeigt.',
  }[u.mode];

  const check = h('button', {
    class: 'btn', type: 'button', text: 'Jetzt prüfen',
    onclick: (e) => guarded(e.target, async () => {
      const r = await api('POST', '/update/check');
      toast(r.available ? `Version ${r.latest} verfügbar` : 'Keine neue Version gefunden');
      renderSettings();
    }),
  });
  const save = h('button', {
    class: 'btn primary', type: 'button', text: 'Speichern',
    onclick: (e) => guarded(e.target, async () => {
      await api('PUT', '/settings/update', { autoUpdate: model.autoUpdate, time: time.value });
      toast('Gespeichert');
    }),
  });

  const stateNotice = u.state === 'failed' ? h('div', { class: 'notice err', text: u.message })
    : u.state === 'done' ? h('div', { class: 'notice ok', text: u.message })
      : ['installing', 'downloading'].includes(u.state) ? h('div', { class: 'notice', text: u.message }) : null;

  fill(card,
    h('div', { class: 'card-head' }, h('h3', { text: 'Updates' }),
      u.canInstall ? switchEl(u.autoUpdate, (v) => { model.autoUpdate = v; }, 'automatisch') : null),
    stateNotice,
    h('dl', { class: 'facts' },
      h('dt', { text: 'Installiert' }), h('dd', { text: u.current }),
      h('dt', { text: 'Neueste' }), h('dd', {}, u.latest ? `${u.latest}` : '–', u.available ? h('span', { class: 'badge ok', text: 'Update verfügbar', style: null }) : null),
      h('dt', { text: 'Geprüft' }), h('dd', { text: u.lastCheck ? new Date(u.lastCheck).toLocaleString('de-AT') : 'noch nicht' })),
    h('p', { class: 'muted', text: modeText }),
    u.canInstall ? h('div', { class: 'form-grid' },
      field('Uhrzeit für automatische Updates', time, 'täglich; läuft gerade ein Client, wird bis zu 1 Stunde gewartet')) : null,
    u.mode === 'homeassistant' ? null : h('div', { class: 'buttons' },
      u.canInstall ? save : null, check,
      u.available && u.canInstall ? h('button', { class: 'btn primary', type: 'button', text: `Version ${u.latest} installieren`, onclick: (e) => startUpdate(e.target) }) : null,
      u.available && !u.canInstall ? h('a', { class: 'btn', href: 'https://github.com/JayLo1401/rtltcp.server/releases/latest', target: '_blank', rel: 'noopener', text: 'Download-Seite' }) : null));
  return card;
}

// ------------------------------------------------------------------ settings

async function renderSettings() {
  let logging, update, remote, devices, inst;
  try {
    [logging, update, remote, devices, inst] = await Promise.all([api('GET', '/settings/logging'),
      api('GET', '/update'), api('GET', '/settings/remote'), api('GET', '/devices'), api('GET', '/remote/instances')]);
  } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }

  // ---- instances on rtl.over.internet
  const usbSticks = devices.filter((d) => !d.remote && d.serial);
  const INST_STATE = { connected: ['ok', 'verbunden'], connecting: ['', 'verbinde …'], disconnected: ['warn', 'getrennt'],
    disabled: ['warn', 'deaktiviert'], revoked: ['err', 'widerrufen'], stopped: ['', 'gestoppt'] };
  const instCard = (i) => {
    const [cls, label] = INST_STATE[i.state] || ['', i.state];
    const shared = new Set(i.devices);
    const save = h('button', { class: 'btn small primary', type: 'button', text: 'Freigaben speichern', onclick: (e) => guarded(e.target, async () => {
      await api('PUT', `/remote/instances/${i.id}`, { enabled: i.enabled, devices: [...shared] });
      toast('Gespeichert – Stream-Server neu gestartet');
      renderSettings();
    }) });
    const others = i.shares.filter((s) => !s.own);
    return h('div', { class: 'peer' },
      h('div', { class: 'card-head' },
        h('h3', {}, i.name || i.url, h('small', { text: i.url })),
        h('span', { class: 'badge ' + cls, text: label })),
      i.message ? h('p', { class: 'muted', text: i.message }) : null,
      h('p', { class: 'muted', text: 'In dieser Instanz freigeben:' }),
      h('div', { class: 'buttons' },
        usbSticks.length ? usbSticks.map((d) => switchEl(shared.has(d.serial), (v) => { if (v) shared.add(d.serial); else shared.delete(d.serial); },
          `${d.name} (${d.serial})`)) : h('span', { class: 'muted', text: 'Keine Sticks mit Seriennummer eingetragen.' })),
      h('p', { class: 'muted' }, 'Von anderen freigegeben: ',
        others.length ? others.map((s) => `${s.name} @ ${s.provider}${s.online ? '' : ' (offline)'}`).join(', ') : 'nichts',
        others.length ? ' – unter Geräte → „+ Entfernter Stick“ einbinden' : null),
      h('div', { class: 'buttons' }, save,
        h('button', { class: 'btn small danger', type: 'button', text: 'Instanz entfernen', onclick: (e) => {
          if (!confirm(`Instanz „${i.name}“ entfernen? Freigaben und entfernte Sticks daraus funktionieren dann nicht mehr.`)) return;
          guarded(e.target, async () => { await api('DELETE', `/remote/instances/${i.id}`); toast('Entfernt'); renderSettings(); });
        } })));
  };
  const enUrl = h('input', { type: 'url', placeholder: 'https://mediator.example.com', required: true });
  const enCode = h('input', { type: 'text', placeholder: 'K7QM-4XPD', required: true, autocomplete: 'off' });
  const enPw = h('input', { type: 'password', required: true, autocomplete: 'new-password' });
  const enBtn = h('button', { class: 'btn primary', type: 'submit', text: 'Instanz hinzufügen' });
  // credentials file from the instance page of rtl.over.internet (docs/protocol.md §1.1 there): fills the fields
  const credInfo = h('div');
  const credFile = h('input', { type: 'file', accept: '.json,application/json', onchange: async (e) => {
    const file = e.target.files[0];
    credInfo.replaceChildren();
    if (!file) return;
    try {
      let c;
      try { c = JSON.parse(await file.text()); } catch { throw new Error('Die Datei ist kein gültiges JSON.'); }
      if (!c || c.format !== 'rtl.over.internet/credentials') throw new Error('Das ist keine Zugangsdatei von rtl.over.internet.');
      if (c.version !== 1) throw new Error(`Zugangsdatei Version ${c.version} wird nicht unterstützt – bitte den Server aktualisieren.`);
      if (typeof c.server !== 'string' || typeof c.instance_code !== 'string' || typeof c.password !== 'string')
        throw new Error('In der Zugangsdatei fehlen Adresse, Instanz-Code oder Passwort.');
      enUrl.value = c.server; enCode.value = c.instance_code; enPw.value = c.password;
      const name = c.instance && c.instance.name ? `„${c.instance.name}“ ` : '';
      credInfo.append(h('div', { class: 'notice ok', text: `Zugangsdaten für Instanz ${name}übernommen. Mit „Instanz hinzufügen“ anmelden.` }));
    } catch (err) {
      credInfo.append(h('div', { class: 'notice err', text: err.message }));
    } finally {
      e.target.value = ''; // the file (with the password) is not kept
    }
  } });
  const srvName = h('input', { type: 'text', value: remote.serverName, placeholder: inst.serverName, maxlength: 64 });
  const instForm = h('form', {
    class: 'card',
    onsubmit: (e) => {
      e.preventDefault();
      guarded(enBtn, async () => {
        await api('PUT', '/settings/remote', { serverName: srvName.value.trim() });
        await api('POST', '/remote/instances', { url: enUrl.value.trim(), code: enCode.value, password: enPw.value });
        toast('Instanz hinzugefügt');
        renderSettings();
      });
    },
  },
  h('div', { class: 'card-head' }, h('h3', { text: 'Sticks teilen über rtl.over.internet' })),
  h('p', { class: 'muted', text: 'Ohne VPN und ohne Portfreigabe: rtl.over.internet vermittelt nur, der Stream läuft direkt zwischen den Servern. Code und Passwort der Instanz bekommst du vom Betreiber; gespeichert wird nur ein Token.' }),
  inst.unavailable ? h('div', { class: 'notice err', text: inst.unavailable }) : null,
  inst.instances.map(instCard),
  h('h3', { class: 'section-title', text: 'Instanz hinzufügen' }),
  h('div', { class: 'form-grid' },
    field('Zugangsdatei', credFile, 'optional: rtl-over-internet-….json von der Instanzseite, füllt die Felder darunter aus')),
  credInfo,
  h('div', { class: 'form-grid' },
    field('Adresse', enUrl, 'deines rtl.over.internet (selbst gehostet)'), field('Instanz-Code', enCode), field('Passwort', enPw, 'der Instanz'),
    field('Name dieses Servers', srvName, 'sehen die anderen Standorte, leer = Computername')),
  h('div', { class: 'buttons' }, enBtn));

  const days = h('input', { type: 'number', min: 1, max: 365, value: logging.retentionDays, required: true });
  const saveLog = h('button', { class: 'btn primary', type: 'submit', text: 'Speichern' });
  const logForm = h('form', {
    class: 'card',
    onsubmit: (e) => {
      e.preventDefault();
      guarded(saveLog, async () => {
        await api('PUT', '/settings/logging', { retentionDays: Number(days.value), debug: logging.debug });
        toast('Gespeichert');
      });
    },
  },
  h('div', { class: 'card-head' }, h('h3', { text: 'Protokoll' }),
    switchEl(logging.debug, (v) => { logging.debug = v; }, 'Details (Debug)')),
  h('p', { class: 'muted' }, 'Eine Datei pro Tag im Datenverzeichnis unter ', h('code', { text: 'logs/' }),
    '. Ältere Dateien werden automatisch gelöscht. Details nur zur Fehlersuche einschalten.'),
  h('div', { class: 'form-grid' }, field('Aufbewahren (Tage)', days, '1–365, Standard 14')),
  h('div', { class: 'buttons' }, saveLog));

  const cur = h('input', { type: 'password', autocomplete: 'current-password', required: true });
  const pw = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 8 });
  const pw2 = h('input', { type: 'password', autocomplete: 'new-password', required: true });
  const savePw = h('button', { class: 'btn primary', type: 'submit', text: 'Passwort ändern' });
  const pwForm = h('form', {
    class: 'card',
    onsubmit: (e) => {
      e.preventDefault();
      if (pw.value !== pw2.value) return toast('Die neuen Passwörter stimmen nicht überein', true);
      guarded(savePw, async () => {
        await api('PUT', '/admin/password', { current: cur.value, new: pw.value });
        e.target.reset();
        toast('Passwort geändert');
      });
    },
  },
  h('div', { class: 'card-head' }, h('h3', { text: `Administrator „${session.user}“` })),
  h('div', { class: 'form-grid' },
    field('Aktuelles Passwort', cur), field('Neues Passwort', pw, 'mindestens 8 Zeichen'), field('Wiederholen', pw2)),
  h('div', { class: 'buttons' }, savePw));

  setPage(h('div', { class: 'page-head' }, h('h1', { text: 'Einstellungen' })), updateCard(update), instForm, logForm,
    session.ingress ? null : pwForm);
}

// ------------------------------------------------------------------ start

loadSession().then(route).catch((e) => setPage(h('div', { class: 'notice err', text: 'Server nicht erreichbar: ' + e.message })));
