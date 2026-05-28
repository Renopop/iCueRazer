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


# ── Démarrage ────────────────────────────────────────────────────────────────
if __name__ == '__main__':
    d = _cert_dir()
    d.mkdir(parents=True, exist_ok=True)
    cert_file = d / 'cert.pem'
    key_file  = d / 'key.pem'
    _ensure_cert(cert_file, key_file)

    _refresh()
    threading.Thread(target=_loop, daemon=True).start()

    app.run(
        host='127.0.0.1',
        port=8765,
        ssl_context=(str(cert_file), str(key_file)),
        debug=False,
        threaded=True,
    )
