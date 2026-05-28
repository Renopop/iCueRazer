"""
Lecture batterie Razer via hidapi.
Compatible Razer Synapse 3 : hidapi ouvre les devices en accès partagé
(FILE_SHARE_READ|FILE_SHARE_WRITE).
"""
import hid
import time

RAZER_VID = 0x1532

# ── Filtre des appareils affichés ────────────────────────────────────────────
ALLOWED_DEVICES = [
    'blackwidow v3 mini',
    'kraken v3 pro',
    'cobra pro',
    'cobra',        # fallback Cobra Pro dongle
]

def _is_allowed(name: str) -> bool:
    n = name.lower()
    return any(kw in n for kw in ALLOWED_DEVICES)


# ── Dongles dont hidapi ne remonte pas le product_string ─────────────────────
# Razer HyperSpeed dongles exposent un nom vide ; on les identifie par PID.
KNOWN_DONGLES: dict[int, str] = {
    0x0271: 'BlackWidow V3 Mini',   # dongle HyperSpeed BlackWidow V3 Mini
    0x0270: 'BlackWidow V3 Mini',   # variante firmware
    0x00A3: 'Razer Cobra Pro',      # Cobra Pro HyperSpeed dongle
    0x00A8: 'Razer Cobra Pro',      # variante
}


# ── Classification par type ──────────────────────────────────────────────────
_TYPE_KEYWORDS = {
    'mouse': [
        'mouse', 'deathadder', 'viper', 'basilisk', 'mamba', 'naga',
        'lancehead', 'atheris', 'orochi', 'abyssus', 'taipan', 'krait',
        'cobra', 'hyperpolling',
    ],
    'keyboard': [
        'keyboard', 'blackwidow', 'huntsman', 'ornata', 'cynosa',
        'tartarus', 'deathstalker',
    ],
    'headset': [
        'headset', 'kraken', 'barracuda', 'thresher', 'hammerhead',
        'electra', 'blackshark',
    ],
}

def _device_type(name: str) -> str:
    n = name.lower()
    for dtype, kws in _TYPE_KEYWORDS.items():
        if any(kw in n for kw in kws):
            return dtype
    return 'other'


# ── Protocole Razer ──────────────────────────────────────────────────────────

def _build_report(transaction_id: int, cmd_class: int, cmd_id: int,
                  args: tuple = ()) -> bytes:
    buf = bytearray(90)
    buf[0] = 0x00
    buf[1] = transaction_id
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


# Variantes : (transaction_id, cmd_class, cmd_id)
_VARIANTS = [
    (0x1F, 0x07, 0x80),   # Cobra Pro, BlackWidow V3 Mini, modèles 2021+
    (0xFF, 0x07, 0x80),   # Kraken V3 Pro et majorité Synapse 3
    (0x1F, 0x07, 0x02),
    (0xFF, 0x07, 0x02),
]


def _try_read_battery(path: bytes) -> dict | None:
    """
    Réponse 91 octets (report-ID en tête) :
      resp[1]  = status  (0x02 succès | 0x01 busy-mais-valide)
      resp[9]  = charging (0x01 = en charge)
      resp[10] = battery raw 0–255
    """
    dev = hid.device()
    try:
        dev.open_path(path)
        time.sleep(0.05)

        for (tid, cls_, cid) in _VARIANTS:
            try:
                report = _build_report(tid, cls_, cid, (0x01,))
                dev.send_feature_report(b'\x00' + report)
                time.sleep(0.25)
                resp = dev.get_feature_report(0x00, 91)

                if len(resp) < 11:
                    continue
                if resp[1] not in (0x01, 0x02):
                    continue

                raw = resp[10]
                if raw == 0:
                    continue

                charging = (resp[9] == 0x01)
                percent  = min(100, max(0, round(raw / 255 * 100)))
                return {'percent': percent, 'charging': charging}

            except Exception:
                continue

    except Exception:
        pass
    finally:
        try:
            dev.close()
        except Exception:
            pass

    return None


def get_all_devices() -> list[dict]:
    """
    Énumère les appareils Razer, filtre selon ALLOWED_DEVICES, lit la batterie.

    Ordre des interfaces essayées pour la batterie :
      1. usage_page=0x59  (interface propriétaire Razer → battery sur dongles)
      2. usage_page=0x0001 interface_number=0  (interface principale USB)
      3. Toutes les autres interfaces
    """
    result: list[dict] = []
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception:
        return result

    by_pid: dict[int, list] = {}
    for iface in all_ifaces:
        pid = iface['product_id']
        by_pid.setdefault(pid, []).append(iface)

    raw_entries: list[dict] = []

    for pid, ifaces in by_pid.items():
        # Résoudre le nom : product_string ou dongle connu ou PID hex
        name = (ifaces[0].get('product_string')
                or KNOWN_DONGLES.get(pid)
                or f'Razer 0x{pid:04X}')

        if not _is_allowed(name):
            continue

        dtype = _device_type(name)

        # Priorité d'interface pour les feature reports batterie :
        # 0x59 = interface propriétaire Razer (sur dongles HyperSpeed)
        # interface 0 en usage_page 0x1 = interface principale
        def iface_priority(x: dict) -> tuple:
            up = x.get('usage_page', 0)
            inum = x.get('interface_number', 99)
            if up == 0x59:
                return (0, inum)   # propriétaire Razer → priorité max
            if up == 0x0001 and inum == 0:
                return (1, inum)   # interface principale USB
            if up == 0x0001:
                return (2, inum)
            return (3, inum)

        ordered = sorted(ifaces, key=iface_priority)

        battery = None
        for iface in ordered:
            battery = _try_read_battery(iface['path'])
            if battery:
                break

        entry: dict = {'id': pid, 'name': name, 'type': dtype}
        if battery:
            entry.update(battery)
        else:
            entry['wired'] = True

        raw_entries.append(entry)

    # Dédupliquer par nom : si un appareil apparaît via USB et Bluetooth,
    # garder celui qui a des données de batterie, sinon le premier trouvé.
    seen: dict[str, dict] = {}
    for entry in raw_entries:
        n = entry['name']
        if n not in seen or 'percent' in entry:
            seen[n] = entry

    return list(seen.values())
