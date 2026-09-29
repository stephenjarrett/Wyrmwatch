'use strict';
let key = '', busy = false, pending = false, polling = false, historyKey = '';
const cards = new Map(), $ = id => document.getElementById(id);
const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; };
async function api(path, body) {
  const requestKey = key;
  const response = await fetch(path, { method: body ? 'POST' : 'GET', headers: { Authorization: `Bearer ${key}`, ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined, cache: 'no-store' });
  if (requestKey !== key) throw new Error('Access changed. Reconnect to continue.');
  if (response.status === 401) { signOut(); throw new Error('This key is invalid, expired, or revoked.'); }
  if (!response.ok) { const error = await response.json().catch(() => ({})); throw new Error(error.message || (response.status === 403 ? 'This key does not permit that action.' : `Request failed (${response.status}).`)); }
  const data = await response.json();
  if (requestKey !== key) throw new Error('Access changed. Reconnect to continue.');
  return data;
}
function signOut() { key = ''; historyKey = ''; busy = pending = false; $('key').value = ''; $('login').hidden = false; $('workspace').hidden = true; $('logout').hidden = true; $('servers').replaceChildren(); $('history').replaceChildren(); $('notice').textContent = ''; cards.clear(); }
$('logout').addEventListener('click', signOut);
$('login-form').addEventListener('submit', async event => { event.preventDefault(); if (polling) return; key = $('key').value.trim(); try { await refresh(); if (!key) return; $('key').value = ''; $('login').hidden = true; $('workspace').hidden = false; $('logout').hidden = false; $('notice').textContent = ''; } catch (error) { $('notice').textContent = error.message; } });
function card(server) {
  const root = element('section', undefined, 'card');
  const top = element('div', undefined, 'server-top'), title = element('h2', server.name), state = element('span', 'Checking', 'pill'); top.append(title, state);
  const metrics = element('div', undefined, 'metrics'), numbers = {};
  for (const label of ['PLAYERS', 'CPU', 'MEMORY']) { const metric = element('div', undefined, 'metric'); numbers[label] = element('strong', '—'); metric.append(element('span', label), numbers[label]); metrics.append(metric); }
  const actions = element('div', undefined, 'actions'), buttons = new Map();
  for (const [action, label] of [['start','Start'],['backup','Back up now'],['restart','Restart'],['stop','Stop'],['check','Check updates'],['update','Update']]) {
    const button = element('button', label, action === 'backup' ? 'primary' : ''); button.addEventListener('click', () => run(server.id, action)); actions.append(button); buttons.set(action, button);
  }
  const build = element('p', '', 'build');
  const backupPanel = element('div', undefined, 'backup-panel'), backupButton = element('button', 'Recovery points', 'subtle'), select = element('select'); select.hidden = true; select.setAttribute('aria-label', 'Recovery point');
  const backupActions = element('div', undefined, 'backup-actions'); backupActions.hidden = true;
  const verify = element('button', 'Verify'), restore = element('button', 'Restore'); backupActions.append(verify, restore);
  backupButton.addEventListener('click', async () => { try { const backups = await api(`/api/servers/${encodeURIComponent(server.id)}/backups`); select.replaceChildren(); for (const backup of backups) { const option = element('option', `${new Date(backup.created).toLocaleString()} · ${(backup.bytes / 1048576).toFixed(1)} MB`); option.value = backup.id; select.append(option); } select.hidden = false; backupActions.hidden = !backups.length; if (!backups.length) $('notice').textContent = 'No recovery points yet.'; } catch(error) { $('notice').textContent = error.message; } });
  verify.addEventListener('click', () => run(server.id, 'verify', select.value)); restore.addEventListener('click', () => run(server.id, 'restore', select.value));
  backupPanel.append(backupButton, select, backupActions); root.append(top, metrics, actions, build, backupPanel); $('servers').append(root);
  return { root, title, state, numbers, buttons, build, server, verify, restore };
}
async function run(id, action, archive) {
  if (busy) return;
  const server = cards.get(id).server;
  if (['stop', 'restart'].includes(action) && !confirm(`${action === 'stop' ? 'Stop' : 'Restart'} ${server.name}? Players will disconnect. A backup is required first.`)) return;
  let confirmation;
  if (action === 'restore') { confirmation = prompt(`Restore the selected backup? The server must be stopped. Current saves will be backed up and retained. Type ${server.name} to confirm.`); if (confirmation !== server.name) return; }
  pending = true; busy = true; $('notice').textContent = 'Working…'; updateButtons();
  try { const result = await api(`/api/servers/${encodeURIComponent(id)}/actions`, { action, archive, confirmation }); $('notice').textContent = result.message; }
  catch (error) { $('notice').textContent = error.message; }
  finally { pending = false; busy = false; await refresh().catch(error => { $('notice').textContent = error.message; }); }
}
function updateButtons() { for (const view of cards.values()) { for (const [action, button] of view.buttons) { button.hidden = !view.server.actions.includes(action); button.disabled = busy; } view.verify.disabled = busy; view.restore.disabled = busy; view.verify.hidden = !view.server.actions.includes('verify'); view.restore.hidden = !view.server.actions.includes('restore'); } }
async function refresh() {
  if (!key || polling) return;
  polling = true;
  try {
    const data = await api('/api/status'); busy = pending || data.busy;
    $('summary').textContent = busy ? 'Maintenance in progress' : `${data.servers.length} shared · ${data.servers.filter(s => s.state.running).length} online`;
    for (const [id, view] of cards) if (!data.servers.some(s => s.id === id)) { view.root.remove(); cards.delete(id); }
    for (const server of data.servers) {
      let view = cards.get(server.id); if (!view) { view = card(server); cards.set(server.id, view); }
      view.server = server; view.title.textContent = server.name; view.state.textContent = !server.state.accessible ? 'Needs attention' : server.state.running ? 'Online' : 'Stopped';
      view.numbers.PLAYERS.textContent = server.state.players ?? '?'; view.numbers.CPU.textContent = `${server.state.cpuPercent.toFixed(1)}%`; view.numbers.MEMORY.textContent = `${(server.state.memoryBytes / 1073741824).toFixed(1)} GB`; view.build.textContent = `Build ${server.build || 'unknown'} · ${server.state.activityReason}`;
    }
    updateButtons();
    const nextHistory = JSON.stringify(data.operations);
    if (nextHistory !== historyKey) { historyKey = nextHistory; $('history').replaceChildren(); for (const operation of data.operations.slice(0, 12)) { const row = element('div', undefined, 'history-row'); row.append(element('span', `${operation.serverName} · ${operation.action} · ${operation.status}`), element('small', new Date(operation.started).toLocaleString())); $('history').append(row); } if (!data.operations.length) $('history').append(element('p', 'Completed operations will appear here.')); }
    if (!data.servers.length) $('notice').textContent = 'No servers are shared with this key. Ask the host to check its scope.';
  } finally { polling = false; }
}
setInterval(() => { if (key) refresh().catch(error => { $('notice').textContent = error.message; }); }, 3000);
