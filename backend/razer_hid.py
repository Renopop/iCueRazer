"""
Lecture batterie Razer via hidapi.
Compatible Razer Synapse 3 : hidapi ouvre les devices en accès partagé
(FILE_SHARE_READ|FILE_SHARE_WRITE) — Synapse continue de fonctionner normalement.

Protocole Razer propriétaire 90 octets.
On essaie deux variantes de transaction_id et deux commandes batterie
pour couvrir toutes les générations de produits Synapse 3.
"""
import hid
import time

RAZER_VID = 0x1532

# ── Filtre des appareils affichés ────────────────────────────────────────────
# Seuls les appareils dont le nom contient l'une de ces chaînes (insensible à
# la casse) sont remontés. Modifie cette liste pour ajouter/retirer un modèle.
ALLOWED_DEVICES = [
    'blackwidow v3 mini',
    'kraken v3 pro',
    'cobra pro',
]

def _is_allowed(name: str) -> bool:
    n = name.lower()
    return any(allowed in n for allowed in ALLOWED_DEVICES)


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


# Variantes à essayer par ordre de priorité.
# Couvre : Deathadder V2/V3, Viper V2, Basilisk V3, BlackWidow V3/V4,
#           Huntsman V2/V3, Kraken V3/V4, Barracuda, Cobra…
_VARIANTS = [
    (0xFF, 0x07, 0x80),   # protocole standard – majorité des Synapse 3
    (0x1F, 0x07, 0x80),   # certains modèles 2021-2023
    (0xFF, 0x07, 0x02),   # anciens firmware
    (0x1F, 0x07, 0x02),
]


def _try_read_battery(path: bytes) -> dict | None:
    """
    Tente de lire la batterie depuis un chemin d'interface HID.
    Essaie plusieurs variantes de commande pour la compatibilité Synapse 3.
    Retourne {'percent': int, 'charging': bool} ou None si non supporté.

    Réponse sur 91 octets (report-ID inclus) :
      resp[0]  = report-ID (0x00)
      resp[1]  = status   (0x02 succès, 0x01 busy-but-valid, 0x04 timeout)
      resp[2]  = transaction_id  ← validé pour confirmer que c'est notre réponse
      resp[9]  = args[0] = état de charge (0x01 = en charge)
      resp[10] = args[1] = niveau batterie brut 0–255
    """
    dev = hid.device()
    try:
        dev.open_path(path)
        time.sleep(0.05)   # laisse Synapse finir une transaction en cours

        for (tid, cls_, cid) in _VARIANTS:
            try:
                report = _build_report(tid, cls_, cid, (0x01,))
                dev.send_feature_report(b'\x00' + report)
                time.sleep(0.25)                      # 250 ms (Synapse peut être lent)
                resp = dev.get_feature_report(0x00, 91)

                if len(resp) < 11:
                    continue

                status = resp[1]
                # 0x02 = succès, 0x01 = busy (données souvent valides quand même)
                # 0x00 / 0x04 / 0x05 = pas de données exploitables
                if status not in (0x01, 0x02):
                    continue

                # Valider le transaction_id pour éviter de lire une réponse Synapse
                if resp[2] != tid:
                    continue

                charging = (resp[9] == 0x01)
                raw      = resp[10]            # 0–255
                if raw == 0 and status != 0x02:
                    continue                   # données probablement parasites
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
    Énumère tous les appareils Razer et lit leur batterie.
    Synapse 3 peut être actif en parallèle : hidapi utilise FILE_SHARE_*.
    Un appareil expose souvent plusieurs interfaces HID ; on les essaie toutes.
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
        name  = ifaces[0].get('product_string') or f'Razer 0x{pid:04X}'
        if not _is_allowed(name):
            continue
        dtype = _device_type(name)

        # Priorité : interface Usage Page 0x0001 (Generic Desktop) d'abord,
        # puis les interfaces vendor-defined (0xFF00+), car Synapse 3 peut
        # monopoliser certaines interfaces vendor — on tente quand même toutes.
        ordered = sorted(ifaces, key=lambda x: (
            0 if x.get('usage_page', 0) == 0x0001 else 1
        ))

        battery = None
        for iface in ordered:
            battery = _try_read_battery(iface['path'])
            if battery:
                break

        entry: dict = {'id': pid, 'name': name, 'type': dtype}
        if battery:
            entry.update(battery)
        else:
            entry['wired'] = True  # filaire OU protocole non supporté

        result.append(entry)

    return result
