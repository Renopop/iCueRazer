"""
Lecture batterie Razer via hidapi.
Compatible Razer Synapse 3 : hidapi ouvre les devices en accès partagé
(FILE_SHARE_READ|FILE_SHARE_WRITE) — Synapse continue de fonctionner normalement.

Protocole Razer propriétaire 90 octets.
"""
import hid
import time

RAZER_VID = 0x1532

# ── Filtre des appareils affichés ────────────────────────────────────────────
# Seuls les appareils dont le nom contient l'une de ces chaînes (insensible à
# la casse) sont remontés dans le widget.
ALLOWED_DEVICES = [
    'blackwidow v3 mini',
    'kraken v3 pro',
    'cobra pro',
    'cobra',        # fallback si le dongle du Cobra Pro reporte un nom court
]

def _is_allowed(name: str) -> bool:
    n = name.lower()
    return any(kw in n for kw in ALLOWED_DEVICES)


# ── Classification par type ──────────────────────────────────────────────────
_TYPE_KEYWORDS = {
    'mouse': [
        'mouse', 'deathadder', 'viper', 'basilisk', 'mamba', 'naga',
        'lancehead', 'atheris', 'orochi', 'abyssus', 'taipan', 'krait',
        'cobra', 'hyperpolling',
    ],
    'keyboard': [
        'keyboard', 'blackwidow', 'huntsman', 'ornata', 'cynosa',
        'tartarus', 'deathstalker', 'blade keyboard',
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
    buf[0] = 0x00                  # status: new command
    buf[1] = transaction_id
    buf[5] = len(args) + 2         # data_size
    buf[6] = cmd_class
    buf[7] = cmd_id
    for i, a in enumerate(args):
        buf[8 + i] = a
    crc = 0
    for b in buf[2:88]:
        crc ^= b
    buf[88] = crc
    return bytes(buf)


# Variantes à essayer : (transaction_id, cmd_class, cmd_id)
# Couvre BlackWidow V3 Mini, Kraken V3 Pro, Cobra Pro et toutes
# les générations de produits Synapse 3 (2019-2024).
_VARIANTS = [
    (0x1F, 0x07, 0x80),   # Cobra Pro, BlackWidow V3 Mini, modèles 2021-2024
    (0xFF, 0x07, 0x80),   # Kraken V3 Pro, majorité des Synapse 3
    (0x1F, 0x07, 0x02),   # variante firmware alternatif
    (0xFF, 0x07, 0x02),
]


def _try_read_battery(path: bytes) -> dict | None:
    """
    Tente de lire la batterie depuis un chemin d'interface HID.

    Structure de la réponse (91 octets, report-ID en tête) :
      resp[0]  = report-ID 0x00  (ajouté par hidapi sur Windows)
      resp[1]  = status          0x02 succès | 0x01 busy-mais-valide
      resp[9]  = charging        0x01 = en charge
      resp[10] = battery raw     0–255  →  /255*100 = %

    Note : on ne vérifie PAS resp[2] (transaction_id) car Synapse 3
    peut le modifier dans sa réponse.
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

                status = resp[1]
                # Accepter 0x02 (succès) et 0x01 (busy, données souvent valides)
                if status not in (0x01, 0x02):
                    continue

                raw = resp[10]
                if raw == 0:
                    continue   # réponse vide, on essaie la variante suivante

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
    Énumère tous les appareils Razer, filtre selon ALLOWED_DEVICES,
    et lit la batterie de chacun.
    Un appareil expose plusieurs interfaces HID ; on les essaie toutes
    par ordre d'interface_number (interface 0 = principale en général).
    """
    result: list[dict] = []
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception:
        return result

    # Regrouper par product_id
    by_pid: dict[int, list] = {}
    for iface in all_ifaces:
        pid = iface['product_id']
        by_pid.setdefault(pid, []).append(iface)

    for pid, ifaces in by_pid.items():
        name = ifaces[0].get('product_string') or f'Razer 0x{pid:04X}'
        if not _is_allowed(name):
            continue
        dtype = _device_type(name)

        # Trier par numéro d'interface (0 en premier = interface principale)
        ordered = sorted(ifaces, key=lambda x: x.get('interface_number', 99))

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

        result.append(entry)

    return result
