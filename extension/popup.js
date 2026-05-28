'use strict';

const RAZER_VID = 0x1532;

// Razer HID protocol constants
const TRANSACTION_ID = 0xFF;
const STATUS_SUCCESS  = 0x02;
const CMD_CLASS_PWR   = 0x07;
const CMD_GET_BATTERY = 0x80;
const CMD_GET_CHARGE  = 0x84;

// ── Device type detection ────────────────────────────────────────────────────

const TYPE_PATTERNS = {
  mouse:    /mouse|deathadder|viper|basilisk|mamba|naga|lancehead|atheris|orochi|abyssus|taipan|imperator|krait|deathadder v\d|pro wireless/i,
  keyboard: /keyboard|blackwidow|huntsman|ornata|cynosa|tartarus|turret|deathstalker/i,
  headset:  /headset|kraken|barracuda|thresher|hammerhead|electra/i,
};

const ICONS = { mouse: '🖱️', keyboard: '⌨️', headset: '🎧', default: '🎮' };

function deviceType(name) {
  for (const [type, re] of Object.entries(TYPE_PATTERNS)) {
    if (re.test(name)) return type;
  }
  return 'default';
}

// ── Razer HID protocol ───────────────────────────────────────────────────────

function buildReport(cmdClass, cmdId, args = []) {
  const buf = new Uint8Array(90);
  buf[0] = 0x00;              // status (0 = new command)
  buf[1] = TRANSACTION_ID;
  // buf[2..4] = 0x00 (remaining packets, protocol type)
  buf[5] = args.length + 2;  // data_size
  buf[6] = cmdClass;
  buf[7] = cmdId;
  for (let i = 0; i < args.length; i++) buf[8 + i] = args[i];
  // CRC = XOR of bytes index 2..87
  let crc = 0;
  for (let i = 2; i < 88; i++) crc ^= buf[i];
  buf[88] = crc;
  return buf;
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

async function sendRecv(device, report) {
  await device.sendFeatureReport(0x00, report);
  await sleep(100);
  return device.receiveFeatureReport(0x00);
}

/**
 * Returns { percent: 0-100, charging: bool } or { error: string }.
 * Tries two command variants for compatibility across device generations.
 */
async function readBattery(device) {
  try {
    if (!device.opened) await device.open();

    const resp = await sendRecv(device, buildReport(CMD_CLASS_PWR, CMD_GET_BATTERY, [0x01]));

    // resp is a DataView of the 90-byte report (report ID stripped by WebHID)
    const status = resp.getUint8(0);
    if (status !== STATUS_SUCCESS) {
      return { error: `no_battery` };
    }

    // args[0] = charging state (1 = charging), args[1] = battery raw (0–255)
    const charging   = resp.getUint8(8) === 0x01;
    const batteryRaw = resp.getUint8(9);
    const percent    = Math.min(100, Math.max(0, Math.round(batteryRaw / 255 * 100)));

    return { percent, charging };
  } catch (e) {
    return { error: e.message };
  }
}

// ── UI helpers ───────────────────────────────────────────────────────────────

function colorClass(pct) {
  if (pct >= 60) return 'green';
  if (pct >= 30) return 'yellow';
  return 'red';
}

function renderCard(device, data) {
  const name = device.productName || `Device 0x${device.productId.toString(16).toUpperCase()}`;
  const icon = ICONS[deviceType(name)];

  let inner;
  if (!data) {
    inner = `<span class="no-battery loading-dots">Lecture</span>`;
  } else if (data.error === 'no_battery') {
    inner = `<span class="no-battery">Pas de batterie (filaire)</span>`;
  } else if (data.error) {
    inner = `<span class="no-battery">Erreur : ${data.error}</span>`;
  } else {
    const cls  = colorClass(data.percent);
    const plug  = data.charging ? '<span class="charging-icon" title="En charge">⚡</span>' : '';
    inner = `
      <div class="battery-row">
        <span class="battery-pct ${cls}">${data.percent}%</span>
        <div class="bar-wrap"><div class="bar ${cls}" style="width:${data.percent}%"></div></div>
        ${plug}
      </div>`;
  }

  const el = document.createElement('div');
  el.className = 'device-card';
  el.dataset.productId = device.productId;
  el.innerHTML = `
    <span class="device-icon">${icon}</span>
    <div class="device-info">
      <div class="device-name" title="${name}">${name}</div>
      ${inner}
    </div>`;
  return el;
}

// ── Main ─────────────────────────────────────────────────────────────────────

const listEl   = document.getElementById('deviceList');
const footerEl = document.getElementById('footer');

function deduplicate(devices) {
  // A single Razer device can expose multiple HID interfaces; pick one per productId
  const seen = new Set();
  return devices.filter(d => {
    if (seen.has(d.productId)) return false;
    seen.add(d.productId);
    return true;
  });
}

async function load() {
  listEl.innerHTML = '';

  let allDevices;
  try {
    allDevices = await navigator.hid.getDevices();
  } catch (e) {
    listEl.innerHTML = `<div class="error-box">WebHID indisponible.<br>${e.message}</div>`;
    return;
  }

  const razer = deduplicate(allDevices.filter(d => d.vendorId === RAZER_VID));

  if (razer.length === 0) {
    listEl.innerHTML = `<div class="empty-state">
      <span class="snake">🐍</span>
      Aucun appareil Razer associé.<br>
      Clique <strong style="color:#00ff41">+ Associer</strong> pour commencer.
    </div>`;
    return;
  }

  // Insert skeleton cards immediately
  const cards = razer.map(d => {
    const card = renderCard(d, null);
    listEl.appendChild(card);
    return { device: d, card };
  });

  footerEl.textContent = `Mis à jour : ${new Date().toLocaleTimeString()}`;

  // Read battery in parallel and update each card as results arrive
  await Promise.all(cards.map(async ({ device, card }) => {
    const data = await readBattery(device);
    const updated = renderCard(device, data);
    listEl.replaceChild(updated, card);

    // Cache last reading
    const key = `batt_${device.productId}`;
    chrome.storage.local.set({ [key]: { ...data, name: device.productName, ts: Date.now() } });
  }));

  footerEl.textContent = `Mis à jour : ${new Date().toLocaleTimeString()}`;
}

// Refresh button
document.getElementById('refreshBtn').addEventListener('click', async function () {
  this.classList.add('spinning');
  await load();
  this.classList.remove('spinning');
});

// Add / pair new device
document.getElementById('addBtn').addEventListener('click', async () => {
  try {
    const granted = await navigator.hid.requestDevice({
      filters: [{ vendorId: RAZER_VID }]
    });
    if (granted.length > 0) await load();
  } catch (e) {
    if (e.name !== 'NotAllowedError') {
      alert(`Impossible d'associer : ${e.message}`);
    }
  }
});

// Auto-refresh every 30 s while popup is open
let autoRefreshTimer = setInterval(load, 30_000);
window.addEventListener('unload', () => clearInterval(autoRefreshTimer));

// Initial load
load();
