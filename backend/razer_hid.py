"""
Lecture batterie Razer via hidapi.
Protocole propriétaire Razer : reports de 90 octets, commande 0x07/0x80.
"""
import hid
import time

RAZER_VID = 0x1532

_TYPE_KEYWORDS = {
    'mouse':    ['mouse', 'deathadder', 'viper', 'basilisk', 'mamba', 'naga',
                 'lancehead', 'atheris', 'orochi', 'abyssus', 'taipan', 'krait'],
    'keyboard': ['keyboard', 'blackwidow', 'huntsman', 'ornata', 'cynosa', 'tartarus'],
    'headset':  ['headset', 'kraken', 'barracuda', 'thresher', 'hammerhead', 'electra'],
}

def _device_type(name: str) -> str:
    n = name.lower()
    for dtype, kws in _TYPE_KEYWORDS.items():
        if any(kw in n for kw in kws):
            return dtype
    return 'other'


def _build_report(cmd_class: int, cmd_id: int, args: tuple = ()) -> bytes:
    """Construit un report Razer de 90 octets avec CRC."""
    buf = bytearray(90)
    buf[0] = 0x00          # status: new command
    buf[1] = 0xFF          # transaction_id
    buf[5] = len(args) + 2 # data_size
    buf[6] = cmd_class
    buf[7] = cmd_id
    for i, a in enumerate(args):
        buf[8 + i] = a
    crc = 0
    for b in buf[2:88]:
        crc ^= b
    buf[88] = crc
    return bytes(buf)


def _read_battery_from_path(path: bytes) -> dict | None:
    """
    Tente de lire la batterie depuis une interface HID précise.
    Retourne {'percent': int, 'charging': bool} ou None.
    """
    dev = hid.device()
    try:
        dev.open_path(path)
        report = _build_report(0x07, 0x80, (0x01,))
        # HID feature report : prepend report-ID 0x00
        dev.send_feature_report(b'\x00' + report)
        time.sleep(0.12)
        # Réponse : 91 octets (1 report-ID + 90 data)
        resp = dev.get_feature_report(0x00, 91)

        if len(resp) < 11:
            return None
        # resp[0] = report-ID, resp[1] = status (0x02 = succès)
        if resp[1] != 0x02:
            return None

        charging = (resp[9] == 0x01)  # args[0]
        raw      = resp[10]            # args[1] : 0–255
        percent  = min(100, max(0, round(raw / 255 * 100)))
        return {'percent': percent, 'charging': charging}

    except Exception:
        return None
    finally:
        try:
            dev.close()
        except Exception:
            pass


def get_all_devices() -> list[dict]:
    """
    Énumère tous les appareils Razer détectés et lit leur batterie.
    Un appareil peut exposer plusieurs interfaces HID ; on essaie chacune.
    """
    result = []
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception:
        return result

    # Regrouper les interfaces par product_id
    by_pid: dict[int, list] = {}
    for iface in all_ifaces:
        pid = iface['product_id']
        by_pid.setdefault(pid, []).append(iface)

    for pid, ifaces in by_pid.items():
        name  = ifaces[0].get('product_string') or f'Razer 0x{pid:04X}'
        dtype = _device_type(name)

        battery = None
        for iface in ifaces:
            battery = _read_battery_from_path(iface['path'])
            if battery:
                break

        entry: dict = {'id': pid, 'name': name, 'type': dtype}
        if battery:
            entry.update(battery)
        else:
            entry['wired'] = True  # filaire ou protocole non supporté

        result.append(entry)

    return result
