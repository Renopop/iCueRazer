"""
Razer Battery Monitor — serveur local HTTPS invisible (Windows).
Sert le widget iCUE Dashboard sur https://localhost:8765/
Lance avec : pythonw server.py  OU  RazerBattery.exe (build PyInstaller)
"""
import sys
import os
import threading
import time
import logging
from pathlib import Path

# ── Masquer la console Windows immédiatement ────────────────────────────────
if sys.platform == 'win32':
    try:
        import ctypes
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if hwnd:
            ctypes.windll.user32.ShowWindow(hwnd, 0)
    except Exception:
        pass

logging.disable(logging.CRITICAL)

from flask import Flask, jsonify, Response
from flask_cors import CORS
import razer_hid

# ── Certificat SSL ───────────────────────────────────────────────────────────

def _cert_dir() -> Path:
    base = os.environ.get('APPDATA') or str(Path.home())
    return Path(base) / 'RazerBattery'


def _ensure_cert(cert_path: Path, key_path: Path) -> None:
    """Génère un certificat auto-signé valable 10 ans si absent."""
    if cert_path.exists() and key_path.exists():
        return

    from cryptography import x509
    from cryptography.x509.oid import NameOID
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    import datetime, ipaddress

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'localhost')])
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.datetime.utcnow())
        .not_valid_after(datetime.datetime.utcnow() + datetime.timedelta(days=3650))
        .add_extension(
            x509.SubjectAlternativeName([
                x509.DNSName('localhost'),
                x509.IPAddress(ipaddress.IPv4Address('127.0.0.1')),
            ]),
            critical=False,
        )
        .sign(key, hashes.SHA256())
    )

    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption(),
    ))


# ── Widget HTML embarqué ─────────────────────────────────────────────────────
WIDGET_HTML = """<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8">
<title>Razer Battery</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{
    background:#0d0d0d;color:#e0e0e0;
    font-family:'Segoe UI',Arial,sans-serif;
    padding:14px;
  }
  header{
    display:flex;align-items:center;justify-content:space-between;
    margin-bottom:12px;padding-bottom:10px;border-bottom:1px solid #1a1a1a;
  }
  .logo{font-size:13px;color:#00ff41;letter-spacing:3px;text-transform:uppercase;font-weight:700}
  .logo small{color:#333;font-size:9px;letter-spacing:1px;text-transform:none;margin-left:3px}
  .refresh{background:none;border:none;color:#2a2a2a;font-size:15px;cursor:pointer;transition:color .15s;padding:2px 6px}
  .refresh:hover{color:#00ff41}
  #list{display:flex;flex-direction:column;gap:8px}
  .card{
    background:#141414;border:1px solid #1e1e1e;border-radius:7px;
    padding:10px 12px;display:flex;align-items:center;gap:10px;
  }
  .ico{font-size:20px;flex-shrink:0}
  .info{flex:1;min-width:0}
  .name{font-size:11px;color:#777;margin-bottom:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .row{display:flex;align-items:center;gap:7px}
  .pct{font-size:22px;font-weight:700;min-width:52px;line-height:1}
  .g{color:#00ff41}.y{color:#ffaa00}.r{color:#ff3333}
  .track{flex:1;height:5px;background:#0a0a0a;border-radius:3px;overflow:hidden}
  .fill{height:100%;border-radius:3px;transition:width .6s ease}
  .fill.g{background:#00ff41}.fill.y{background:#ffaa00}.fill.r{background:#ff3333}
  .plug{font-size:13px;animation:blink 1.4s ease-in-out infinite}
  @keyframes blink{0%,100%{opacity:1}50%{opacity:.25}}
  .wired{font-size:11px;color:#2e2e2e;font-style:italic}
  .msg{text-align:center;color:#333;padding:22px 0;font-size:12px;line-height:1.9}
  footer{margin-top:10px;text-align:right;font-size:9px;color:#1e1e1e}
</style>
</head>
<body>
<header>
  <div class="logo">RAZER<small>battery</small></div>
  <button class="refresh" title="Rafraîchir" onclick="refresh()">&#8635;</button>
</header>
<div id="list"></div>
<footer id="ts"></footer>
<script>
const ICO={mouse:'&#128433;',keyboard:'&#9000;',headset:'&#127911;',other:'&#127918;'};
function cls(p){return p>=60?'g':p>=30?'y':'r'}

function render(devices){
  const el=document.getElementById('list');
  if(!devices.length){
    el.innerHTML='<div class="msg">Aucun appareil Razer détecté.<br>Vérifiez que les appareils sont allumés.</div>';
    return;
  }
  el.innerHTML=devices.map(d=>{
    const ico=ICO[d.type]||ICO.other;
    if(d.wired) return `<div class="card"><span class="ico">${ico}</span>
      <div class="info"><div class="name">${d.name}</div><div class="wired">Filaire</div></div></div>`;
    const c=cls(d.percent),plug=d.charging?'<span class="plug">&#9889;</span>':'';
    return `<div class="card"><span class="ico">${ico}</span>
      <div class="info">
        <div class="name">${d.name}</div>
        <div class="row">
          <span class="pct ${c}">${d.percent}%</span>
          <div class="track"><div class="fill ${c}" style="width:${d.percent}%"></div></div>
          ${plug}
        </div>
      </div></div>`;
  }).join('');
}

async function refresh(){
  try{
    const r=await fetch('/api/devices');
    const data=await r.json();
    render(data.devices||[]);
    document.getElementById('ts').textContent='Mis à jour : '+new Date().toLocaleTimeString();
  }catch(e){
    document.getElementById('list').innerHTML='<div class="msg">Serveur indisponible.<br>RazerBattery.exe doit être lancé.</div>';
  }
}

refresh();
setInterval(refresh,30000);
</script>
</body>
</html>"""

# ── Flask app ────────────────────────────────────────────────────────────────
app = Flask(__name__)
CORS(app)

_cache: dict = {'devices': [], 'ts': 0.0}
_lock = threading.Lock()


def _refresh() -> None:
    devices = razer_hid.get_all_devices()
    with _lock:
        _cache['devices'] = devices
        _cache['ts'] = time.time()


def _loop() -> None:
    while True:
        try:
            _refresh()
        except Exception:
            pass
        time.sleep(30)


@app.route('/')
def index():
    return Response(WIDGET_HTML, mimetype='text/html; charset=utf-8')


@app.route('/api/devices')
def api_devices():
    with _lock:
        return jsonify({'devices': _cache['devices'], 'updated': _cache['ts']})


@app.route('/api/debug')
def api_debug():
    """
    Diagnostic : liste TOUS les appareils Razer HID détectés (sans filtre),
    avec leurs interfaces et les noms tels que vus par hidapi.
    Accède à https://localhost:8765/api/debug pour diagnostiquer.
    """
    import hid
    RAZER_VID = 0x1532
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception as e:
        return jsonify({'error': str(e), 'devices': []})

    by_pid: dict = {}
    for iface in all_ifaces:
        pid = iface['product_id']
        by_pid.setdefault(pid, []).append({
            'path':        iface.get('path', b'').decode('utf-8', errors='replace'),
            'usage_page':  hex(iface.get('usage_page', 0)),
            'usage':       hex(iface.get('usage', 0)),
            'interface':   iface.get('interface_number', -1),
        })

    devices = [
        {
            'product_id':   hex(pid),
            'product_name': ifaces_raw[0].get('product_string', '?')
                            if all_ifaces else '?',
            'interfaces':   by_pid[pid],
        }
        for pid, ifaces_raw in [
            (pid, [i for i in all_ifaces if i['product_id'] == pid])
            for pid in by_pid
        ]
    ]

    return jsonify({'razer_devices_found': len(devices), 'devices': devices})


@app.route('/api/battery_debug')
def api_battery_debug():
    """
    Diagnostic batterie : teste chaque interface HID Razer avec plusieurs
    variantes de commande et affiche les octets bruts reçus.
    Aussi : lit System.Devices.BatteryStrengthPercent pour les appareils BT.
    """
    import hid, sys

    RAZER_VID = 0x1532

    def _build(tid, cmd_class, cmd_id, args=()):
        buf = bytearray(90)
        buf[1] = tid
        buf[5] = len(args) + 2
        buf[6] = cmd_class
        buf[7] = cmd_id
        for i, a in enumerate(args):
            buf[8 + i] = a
        crc = 0
        for b in buf[2:88]:
            crc ^= b
        buf[88] = crc
        return bytes(buf)

    # 3 variantes suffisent pour le diagnostic — délai réduit à 0.08s
    VARIANTS = [
        ('tid=0xFF/0x80', 0xFF, 0x07, 0x80, ()),
        ('tid=0x1F/0x80', 0x1F, 0x07, 0x80, ()),
        ('tid=0xFF/0x02', 0xFF, 0x07, 0x02, ()),
    ]

    hid_results = []
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception as e:
        all_ifaces = []
        hid_results.append({'error': str(e)})

    for iface in all_ifaces:
        pid  = iface['product_id']
        path = iface['path']
        entry = {
            'pid':        hex(pid),
            'name':       iface.get('product_string') or f'Razer 0x{pid:04X}',
            'usage_page': hex(iface.get('usage_page', 0)),
            'interface':  iface.get('interface_number', -1),
            'variants':   [],
        }
        dev = hid.device()
        try:
            dev.open_path(path)
            time.sleep(0.02)
            for label, tid, cls_, cid, args in VARIANTS:
                try:
                    dev.send_feature_report(b'\x00' + _build(tid, cls_, cid, args))
                    time.sleep(0.08)
                    resp = dev.get_feature_report(0x00, 91)
                    entry['variants'].append({
                        'variant':       label,
                        'bytes_0_15':    list(resp[:16]),
                        'status_byte':   hex(resp[1]) if len(resp) > 1 else None,
                        'charging_byte': resp[9]  if len(resp) > 9  else None,
                        'battery_byte':  resp[10] if len(resp) > 10 else None,
                    })
                except Exception as ex:
                    entry['variants'].append({'variant': label, 'error': str(ex)})
        except Exception as ex:
            entry['open_error'] = str(ex)
        finally:
            try:
                dev.close()
            except Exception:
                pass
        hid_results.append(entry)

    # ── Bluetooth : sélecteurs natifs BluetoothDevice / BluetoothLEDevice ──────
    bt_results: list = []
    if sys.platform == 'win32':
        try:
            import asyncio
            from winrt.windows.devices.enumeration import DeviceInformation
            from winrt.windows.devices.bluetooth import BluetoothDevice, BluetoothLEDevice
            BT_PROP = 'System.Devices.BatteryStrengthPercent'

            async def _bt():
                out = []
                for label, aqs in [
                    ('classic', BluetoothDevice.get_device_selector()),
                    ('ble',     BluetoothLEDevice.get_device_selector()),
                ]:
                    try:
                        devs = await DeviceInformation.find_all_async(aqs)
                    except Exception as ex:
                        out.append({'error': f'{label} find_all_async: {ex}'})
                        continue
                    for d in devs:
                        n = d.name or ''
                        if 'razer' not in n.lower():
                            continue
                        try:
                            d2 = await DeviceInformation.create_from_id_async(d.id, [BT_PROP])
                            raw = d2.properties[BT_PROP]
                        except Exception as ex:
                            raw = f'error: {ex}'
                        out.append({'bt_type': label, 'name': n, 'id': str(d.id), BT_PROP: raw})
                return out

            loop = asyncio.new_event_loop()
            bt_results = loop.run_until_complete(_bt())
            loop.close()
        except Exception as ex:
            bt_results = [{'error': str(ex)}]

    return jsonify({'hid': hid_results, 'bluetooth': bt_results})


@app.route('/api/bt_ps_debug')
def api_bt_ps_debug():
    """
    Diagnostic PowerShell : liste la propriété BatteryStrengthPercent de TOUS
    les appareils PnP (pas seulement Razer) pour vérifier ce que Windows sait.
    Accède à https://localhost:8765/api/bt_ps_debug
    """
    import subprocess
    ps = (
        "Get-PnpDevice | ForEach-Object {"
        "  try {"
        "    $p = Get-PnpDeviceProperty -InstanceId $_.InstanceId"
        "    -KeyName 'System.Devices.BatteryStrengthPercent' -ErrorAction Stop;"
        "    if ($null -ne $p.Data) {"
        "      [PSCustomObject]@{Name=$_.FriendlyName;InstanceId=$_.InstanceId;Battery=$p.Data} | ConvertTo-Json -Compress"
        "    }"
        "  } catch {} }"
    )
    try:
        r = subprocess.run(
            ['powershell', '-NoProfile', '-NonInteractive', '-Command', ps],
            capture_output=True, text=True, timeout=30,
            creationflags=0x08000000,
        )
        lines = [l.strip() for l in r.stdout.strip().splitlines() if l.strip()]
        import json
        devices = []
        for l in lines:
            try:
                devices.append(json.loads(l))
            except Exception:
                devices.append({'raw': l})
        return jsonify({
            'devices_with_battery': devices,
            'stderr': r.stderr.strip() or None,
        })
    except Exception as e:
        return jsonify({'error': str(e)})


# ── Démarrage ────────────────────────────────────────────────────────────────
def _log(msg: str) -> None:
    """Écrit dans %APPDATA%\RazerBattery\server.log pour le diagnostic."""
    try:
        log_file = _cert_dir() / 'server.log'
        with open(log_file, 'a', encoding='utf-8') as f:
            f.write(f'[{time.strftime("%Y-%m-%d %H:%M:%S")}] {msg}\n')
    except Exception:
        pass


if __name__ == '__main__':
    d = _cert_dir()
    d.mkdir(parents=True, exist_ok=True)
    _log('Démarrage RazerBattery')

    cert_file = d / 'cert.pem'
    key_file  = d / 'key.pem'
    ssl_context = None

    try:
        _ensure_cert(cert_file, key_file)
        ssl_context = (str(cert_file), str(key_file))
        _log('SSL OK — https://localhost:8765/')
    except Exception as e:
        # Fallback HTTP si la génération du certificat échoue
        _log(f'SSL ERREUR ({e}) — fallback HTTP sur http://localhost:8765/')

    try:
        _refresh()
    except Exception as e:
        _log(f'Erreur lecture HID initiale : {e}')

    threading.Thread(target=_loop, daemon=True).start()

    try:
        _log(f'Flask bind 127.0.0.1:8765 (ssl={ssl_context is not None})')
        app.run(
            host='127.0.0.1',
            port=8765,
            ssl_context=ssl_context,
            debug=False,
            threaded=True,
        )
    except Exception as e:
        _log(f'Flask ERREUR : {e}')
