// rtltcp.server – Weboberfläche. Kein Framework, keine externen Skripte; DOM wird ohne innerHTML aufgebaut.
'use strict';

const app = document.getElementById('app');
const nav = document.getElementById('nav');
let session = null;
let pollTimer = null;
const rateSamples = {}; // port -> { bytes, time }

const SAMPLE_RATES = [250000, 1024000, 1536000, 1800000, 1920000, 2048000, 2400000, 2560000, 2880000, 3200000];
const GAINS = [0, 9, 14, 27, 37, 77, 87, 125, 144, 157, 166, 197, 207, 229, 254, 280, 297, 328, 338, 364, 372, 386,
  402, 421, 434, 439, 445, 480, 483, 488, 496];
const DEFAULT_NETS = ['127.0.0.0/8', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '::1/128', 'fc00::/7', 'fe80::/10'];

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

const pages = { status: renderStatus, devices: renderDevices, users: renderUsers, logs: renderLogs, settings: renderSettings };

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
    fill(head, h('h1', { text: 'Status' }),
      h('span', { class: 'badge ' + (s.authEnabled ? 'ok' : ''), text: s.authEnabled ? 'Anmeldung aktiv' : 'Anmeldung aus' }));

    const now = performance.now();
    const cards = s.devices.map((d) => {
      const prev = rateSamples[d.port];
      rateSamples[d.port] = { bytes: d.bytesStreamed, time: now };
      const rate = d.client && prev && d.bytesStreamed >= prev.bytes ? (d.bytesStreamed - prev.bytes) / 2 / ((now - prev.time) / 1000) : 0;
      const [cls, label] = d.client && d.state === 'ready' ? ['ok live', 'Streamt'] : (STATE[d.state] || ['', d.state]);
      return h('section', { class: 'card' },
        h('div', { class: 'card-head' },
          h('h3', {}, d.name, h('small', { text: `Port ${d.port}` })),
          h('span', { class: 'badge ' + cls, text: label })),
        h('dl', { class: 'facts' },
          h('dt', { text: 'Stick' }), h('dd', { text: (d.serial || 'erster freier') + (d.tuner ? ` · ${d.tuner}` : '') }),
          h('dt', { text: 'Client' }), h('dd', { text: d.client ? `${d.client} (${fmtSince(d.clientSince)})` : 'keiner' }),
          d.client ? [h('dt', { text: 'Frequenz' }), h('dd', { text: fmtMHz(d.frequency) })] : null,
          d.client ? [h('dt', { text: 'Samplerate' }), h('dd', { text: fmtRate(d.sampleRate) })] : null,
          d.client ? [h('dt', { text: 'Gain' }), h('dd', { text: fmtGain(d.gain) + (d.biasTee ? ' · Bias-Tee an' : '') })] : null,
          d.client ? [h('dt', { text: 'Datenrate' }), h('dd', { text: prev ? fmtRate(rate) : '…' })] : null,
          ['notready', 'lost', 'error'].includes(d.state) ? [h('dt', { text: 'Meldung' }), h('dd', { text: d.message })] : null),
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
      h('p', { class: 'muted' },
        s.remote.fingerprint ? `Teilen über das Internet: Fingerprint dieses Servers ${s.remote.fingerprint}` : null,
        s.remote.listen ? ` · Freigabe aktiv auf UDP ${s.remote.listen}` : null,
        s.remote.error ? ` · ${s.remote.error}` : null));
  };
  await refresh();
  pollTimer = setInterval(refresh, 2000);
}

// ------------------------------------------------------------------ devices

async function renderDevices() {
  let devices, status;
  try {
    [devices, status] = await Promise.all([api('GET', '/devices'), api('GET', '/status')]);
  } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }

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

  const num = (value, set, attrs) => h('input', { type: 'number', value, ...attrs, oninput: (e) => set(e.target.value === '' ? 0 : Number(e.target.value)) });

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
        d.remote
          ? field('Entfernter Stick', h('input', { type: 'text', disabled: true, value: `${d.remote.share} @ ${d.remote.host}:${d.remote.port}` }),
            'über das Internet, Eintrag in config.json (Testbetrieb)')
          : field('Stick', serial, 'über die Seriennummer zugeordnet'),
        field('Port', num(d.port, (v) => { d.port = v; }, { min: 1, max: 65535 })),
        field('Adresse', h('input', { type: 'text', value: d.bind, oninput: (e) => { d.bind = e.target.value.trim(); } }), '0.0.0.0 = alle Netzwerkkarten'),
        field('Stick abschalten nach', num(d.idleTimeoutSeconds, (v) => { d.idleTimeoutSeconds = v; }, { min: 0 }), 'Sekunden ohne Client, 0 = immer an'),
        field('v1-Client wartet auf Stick', num(d.v1WaitForDeviceSeconds, (v) => { d.v1WaitForDeviceSeconds = v; }, { min: 0, max: 300 }), 'Sekunden, danach wird getrennt')),
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
    list.replaceChildren(...devices.map(deviceCard));
  }
  draw();

  setPage(
    h('div', { class: 'page-head' }, h('h1', { text: 'Geräte' }),
      h('button', { class: 'btn', text: '+ Gerät hinzufügen', onclick: add })),
    h('p', { class: 'lead', text: 'Jeder Stick bekommt einen eigenen Port. Clients verbinden sich mit Server-Adresse und diesem Port (rtl_tcp).' }),
    status.devices.some((d) => d.client) ? h('div', { class: 'notice' },
      h('p', { text: 'Speichern startet die Stream-Server neu – verbundene Clients werden dabei getrennt.' })) : null,
    list,
    h('div', { class: 'buttons' }, save));
}

// ------------------------------------------------------------------ users

async function renderUsers() {
  let users, devices, auth;
  try {
    [users, devices, auth] = await Promise.all([api('GET', '/users'), api('GET', '/devices'), api('GET', '/settings/auth')]);
  } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }

  const deviceName = (serial) => devices.find((d) => d.serial === serial)?.name || serial;
  const editor = h('div');

  const openEditor = (user) => {
    const isNew = !user;
    const model = user ? { ...user, devices: [...user.devices], password: '' } : { name: '', enabled: true, devices: ['*'], password: '' };
    const name = h('input', { type: 'text', value: model.name, disabled: !isNew, maxlength: 64, autocomplete: 'off', oninput: (e) => { model.name = e.target.value.trim(); } });
    const pw = h('input', { type: 'password', autocomplete: 'new-password', oninput: (e) => { model.password = e.target.value; } });

    const checks = h('div', { class: 'checks' });
    const drawChecks = () => {
      const all = model.devices.includes('*');
      checks.replaceChildren(
        h('label', {}, h('input', { type: 'checkbox', checked: all, onchange: (e) => { model.devices = e.target.checked ? ['*'] : []; drawChecks(); } }), 'Alle Sticks'),
        ...devices.filter((d) => d.serial).map((d) => h('label', {},
          h('input', {
            type: 'checkbox', checked: all || model.devices.includes(d.serial), disabled: all,
            onchange: (e) => {
              model.devices = model.devices.filter((x) => x !== d.serial);
              if (e.target.checked) model.devices.push(d.serial);
            },
          }), `${d.name} (Port ${d.port})`)));
    };
    drawChecks();

    const save = h('button', {
      class: 'btn primary', type: 'submit', text: isNew ? 'Benutzer anlegen' : 'Speichern',
    });
    editor.replaceChildren(h('form', {
      class: 'card',
      onsubmit: (e) => {
        e.preventDefault();
        guarded(save, async () => {
          const body = { name: model.name, enabled: model.enabled, devices: model.devices, password: model.password || null };
          if (isNew) await api('POST', '/users', body);
          else await api('PUT', `/users/${encodeURIComponent(model.name)}`, body);
          toast(isNew ? 'Benutzer angelegt' : 'Gespeichert');
          renderUsers();
        });
      },
    },
    h('div', { class: 'card-head' }, h('h3', { text: isNew ? 'Neuer Stream-Benutzer' : `Benutzer „${model.name}“ bearbeiten` }),
      switchEl(model.enabled, (v) => { model.enabled = v; }, 'freigegeben')),
    h('div', { class: 'form-grid' },
      field('Benutzername', name),
      field(isNew ? 'Passwort' : 'Neues Passwort', pw, isNew ? 'mindestens 8 Zeichen' : 'leer lassen = unverändert')),
    h('h3', { class: 'section-title', text: 'Darf diese Sticks nutzen' }),
    checks,
    h('div', { class: 'buttons' }, save,
      h('button', { class: 'btn', type: 'button', text: 'Abbrechen', onclick: () => editor.replaceChildren() }))));
    (isNew ? name : pw).focus();
  };

  const rows = users.map((u) => h('tr', {},
    h('td', { text: u.name }),
    h('td', {}, h('span', { class: 'badge ' + (u.enabled ? 'ok' : ''), text: u.enabled ? 'freigegeben' : 'gesperrt' })),
    h('td', { text: u.devices.includes('*') ? 'alle' : (u.devices.map(deviceName).join(', ') || 'keine') }),
    h('td', { class: 'actions' },
      h('button', { class: 'btn small', text: 'Bearbeiten', onclick: () => openEditor(u) }), ' ',
      h('button', {
        class: 'btn small danger', text: 'Löschen',
        onclick: (e) => guarded(e.target, async () => {
          if (!confirm(`Benutzer „${u.name}“ löschen?`)) return;
          await api('DELETE', `/users/${encodeURIComponent(u.name)}`);
          toast('Benutzer gelöscht');
          renderUsers();
        }),
      }))));

  setPage(
    h('div', { class: 'page-head' }, h('h1', { text: 'Benutzer' }),
      h('button', { class: 'btn', text: '+ Benutzer anlegen', onclick: () => openEditor(null) })),
    h('p', { class: 'lead', text: 'Stream-Benutzer melden sich über rtltcp_v2 an (z. B. home.radio). Sie gelten nur, wenn die Anmeldung unter Einstellungen aktiv ist.' }),
    auth.enabled ? null : h('div', { class: 'notice' }, h('p', {}, 'Die Anmeldung ist derzeit ',
      h('strong', { text: 'aus' }), ' – alle Clients dürfen ohne Benutzer streamen. ', h('a', { href: '#/settings', text: 'Einstellungen' }))),
    editor,
    users.length
      ? h('div', { class: 'card table-scroll' }, h('table', {},
        h('thead', {}, h('tr', {}, h('th', { text: 'Name' }), h('th', { text: 'Status' }), h('th', { text: 'Sticks' }), h('th'))),
        h('tbody', {}, rows)))
      : h('div', { class: 'card muted', text: 'Noch keine Stream-Benutzer angelegt.' }));
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
  let auth, logging, update;
  try {
    [auth, logging, update] = await Promise.all([api('GET', '/settings/auth'), api('GET', '/settings/logging'), api('GET', '/update')]);
  } catch (e) { if (e.status !== 401) setPage(h('div', { class: 'notice err', text: e.message })); return; }

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

  const nets = h('textarea', { spellcheck: 'false' });
  nets.value = auth.trustedNetworks.join('\n');
  const saveAuth = h('button', { class: 'btn primary', type: 'submit', text: 'Speichern' });
  const authForm = h('form', {
    class: 'card',
    onsubmit: (e) => {
      e.preventDefault();
      guarded(saveAuth, async () => {
        await api('PUT', '/settings/auth', { enabled: auth.enabled, trustedNetworks: nets.value.split(/\s+/).filter(Boolean) });
        toast('Gespeichert – gilt ab der nächsten Verbindung');
      });
    },
  },
  h('div', { class: 'card-head' }, h('h3', { text: 'Anmeldung für Stream-Clients' }),
    switchEl(auth.enabled, (v) => { auth.enabled = v; }, 'verlangen')),
  h('p', { class: 'muted', text: 'Ist die Anmeldung aktiv, müssen sich Clients außerhalb der vertrauenswürdigen Netze mit einem Stream-Benutzer anmelden (rtltcp_v2). Alte rtl_tcp-Clients (v1) werden von dort abgewiesen, v2-Clients ohne Anmeldung bekommen die Meldung „Authentication required“.' }),
  field('Vertrauenswürdige Netze (ohne Anmeldung)', nets, 'ein Netz pro Zeile, z. B. 192.168.0.0/16 – leer = alle müssen sich anmelden'),
  h('div', { class: 'buttons' }, saveAuth,
    h('button', { class: 'btn', type: 'button', text: 'Private Netze (Standard)', onclick: () => { nets.value = DEFAULT_NETS.join('\n'); } })));

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

  setPage(h('div', { class: 'page-head' }, h('h1', { text: 'Einstellungen' })), updateCard(update), authForm, logForm,
    session.ingress ? null : pwForm);
}

// ------------------------------------------------------------------ start

loadSession().then(route).catch((e) => setPage(h('div', { class: 'notice err', text: 'Server nicht erreichbar: ' + e.message })));
