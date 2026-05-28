"""
Lecture batterie Razer — deux canaux :
  1. HID (hidapi) : appareils USB / dongles HyperSpeed
  2. WinRT GATT   : appareils Bluetooth LE (Cobra Pro, etc.)
Compatible Razer Synapse 3.
"""
import hid
import sys
import time
import asyncio

RAZER_VID = 0x1532

# ── Filtre ───────────────────────────────────────────────────────────────────
ALLOWED_DEVICES = [
    'blackwidow v3 mini',
    'kraken v3 pro',
    'cobra pro',
    'cobra',
]

def _is_allowed(name: str) -> bool:
    n = name.lower()
    return any(kw in n for kw in ALLOWED_DEVICES)


# ── Dongles multi-appareils HyperSpeed ───────────────────────────────────────
# Un seul PID USB peut gérer 2 appareils : slot 0x01 = clavier, 0x02 = souris.
MULTI_DEVICE_DONGLES: dict[int, list[dict]] = {
    0x0271: [
        {'name': 'BlackWidow V3 Mini', 'type': 'keyboard', 'slot': 0x01},
        {'name': 'Razer Cobra Pro',    'type': 'mouse',    'slot': 0x02},
    ],
    0x0270: [
        {'name': 'BlackWidow V3 Mini', 'type': 'keyboard', 'slot': 0x01},
        {'name': 'Razer Cobra Pro',    'type': 'mouse',    'slot': 0x02},
    ],
}

KNOWN_DONGLES: dict[int, str] = {
    0x00A3: 'Razer Cobra Pro',
    0x00A8: 'Razer Cobra Pro',
}


# ── Classification ───────────────────────────────────────────────────────────
_TYPE_KEYWORDS = {
    'mouse':    ['mouse','deathadder','viper','basilisk','mamba','naga',
                 'lancehead','atheris','orochi','abyssus','taipan','krait',
                 'cobra','hyperpolling'],
    'keyboard': ['keyboard','blackwidow','huntsman','ornata','cynosa',
                 'tartarus','deathstalker'],
    'headset':  ['headset','kraken','barracuda','thresher','hammerhead',
                 'electra','blackshark'],
}

def _device_type(name: str) -> str:
    n = name.lower()
    for dtype, kws in _TYPE_KEYWORDS.items():
        if any(kw in n for kw in kws):
            return dtype
    return 'other'


# ══════════════════════════════════════════════════════════════════════════════
# Canal 1 — HID (USB / HyperSpeed dongle)
# ══════════════════════════════════════════════════════════════════════════════

def _build_report(tid: int, cmd_class: int, cmd_id: int, args: tuple = ()) -> bytes:
    buf = bytearray(90)
    buf[0] = 0x00
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


_VARIANTS = [
    (0x1F, 0x07, 0x80),
    (0xFF, 0x07, 0x80),
    (0x1F, 0x07, 0x02),
    (0xFF, 0x07, 0x02),
]


def _try_hid_battery(path: bytes, slot: int = 0x01) -> dict | None:
    dev = hid.device()
    try:
        dev.open_path(path)
        time.sleep(0.05)
        for (tid, cls_, cid) in _VARIANTS:
            try:
                dev.send_feature_report(b'\x00' + _build_report(tid, cls_, cid, (slot,)))
                time.sleep(0.25)
                resp = dev.get_feature_report(0x00, 91)
                if len(resp) < 11 or resp[1] not in (0x01, 0x02):
                    continue
                raw = resp[10]
                if raw == 0:
                    continue
                return {
                    'percent':  min(100, max(0, round(raw / 255 * 100))),
                    'charging': resp[9] == 0x01,
                }
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


def _iface_priority(x: dict) -> tuple:
    up, inum = x.get('usage_page', 0), x.get('interface_number', 99)
    if up == 0x59:                    return (0, inum)
    if up == 0x0001 and inum == 0:    return (1, inum)
    if up == 0x0001:                  return (2, inum)
    return (3, inum)


def _get_hid_devices() -> list[dict]:
    result: dict[str, dict] = {}
    try:
        all_ifaces = hid.enumerate(RAZER_VID)
    except Exception:
        return []

    by_pid: dict[int, list] = {}
    for iface in all_ifaces:
        by_pid.setdefault(iface['product_id'], []).append(iface)

    def _add(name: str, dtype: str, pid: int, ifaces: list, slot: int = 0x01) -> None:
        if not _is_allowed(name):
            return
        ordered = sorted(ifaces, key=_iface_priority)
        battery = None
        for iface in ordered:
            battery = _try_hid_battery(iface['path'], slot)
            if battery:
                break
        entry: dict = {'id': pid, 'name': name, 'type': dtype}
        entry.update(battery or {'wired': True})
        if name not in result or 'percent' in entry:
            result[name] = entry

    for pid, ifaces in by_pid.items():
        if pid in MULTI_DEVICE_DONGLES:
            for dev_info in MULTI_DEVICE_DONGLES[pid]:
                _add(dev_info['name'], dev_info['type'], pid, ifaces, dev_info['slot'])
        else:
            name = (ifaces[0].get('product_string')
                    or KNOWN_DONGLES.get(pid)
                    or f'Razer 0x{pid:04X}')
            _add(name, _device_type(name), pid, ifaces)

    return list(result.values())


# ══════════════════════════════════════════════════════════════════════════════
# Canal 2 — Bluetooth LE via WinRT (GATT Battery Service UUID 0x180F)
# Utilisé pour les appareils BT qui n'apparaissent pas dans hid.enumerate()
# ══════════════════════════════════════════════════════════════════════════════

# UUID standard Bluetooth : Battery Service + Battery Level characteristic
_BATT_SVC  = '0000180f-0000-1000-8000-00805f9b34fb'
_BATT_CHAR = '00002a19-0000-1000-8000-00805f9b34fb'


async def _get_ble_devices_async() -> list[dict]:
    """Énumère les appareils Bluetooth LE appairés et lit leur batterie GATT."""
    try:
        from winrt.windows.devices.bluetooth import BluetoothLEDevice
        from winrt.windows.devices.bluetooth.genericattributeprofile import (
            GattCommunicationStatus,
        )
        from winrt.windows.devices.enumeration import DeviceInformation
    except ImportError:
        return []

    result: list[dict] = []
    try:
        selector = BluetoothLEDevice.get_device_selector_from_pairing_state(True)
        devices  = await DeviceInformation.find_all_async(selector)
    except Exception:
        return []

    for dev_info in devices:
        name = dev_info.name or ''
        if 'razer' not in name.lower():
            continue
        if not _is_allowed(name):
            continue

        entry: dict = {
            'id':   dev_info.id,
            'name': name,
            'type': _device_type(name),
        }

        try:
            ble = await BluetoothLEDevice.from_id_async(dev_info.id)
            svc_res = await ble.get_gatt_services_for_uuid_async(_BATT_SVC)
            if svc_res.status == GattCommunicationStatus.SUCCESS and svc_res.services:
                chr_res = await svc_res.services[0].get_characteristics_for_uuid_async(
                    _BATT_CHAR
                )
                if chr_res.status == GattCommunicationStatus.SUCCESS and chr_res.characteristics:
                    val_res = await chr_res.characteristics[0].read_value_async()
                    if val_res.status == GattCommunicationStatus.SUCCESS:
                        percent = bytes(val_res.value)[0]   # 0–100
                        entry['percent']  = percent
                        entry['charging'] = False           # BAS ne donne pas l'état de charge
        except Exception:
            pass

        if 'percent' not in entry:
            entry['wired'] = True

        result.append(entry)

    return result


def _get_ble_devices() -> list[dict]:
    """Wrapper synchrone pour _get_ble_devices_async."""
    if sys.platform != 'win32':
        return []
    try:
        loop = asyncio.new_event_loop()
        return loop.run_until_complete(_get_ble_devices_async())
    except Exception:
        return []
    finally:
        try:
            loop.close()
        except Exception:
            pass


# ══════════════════════════════════════════════════════════════════════════════
# Point d'entrée public
# ══════════════════════════════════════════════════════════════════════════════

def get_all_devices() -> list[dict]:
    """
    Combine les appareils HID (USB/HyperSpeed) et Bluetooth LE.
    En cas de doublon par nom, préférer l'entrée qui a les données de batterie.
    """
    merged: dict[str, dict] = {}

    for entry in _get_hid_devices():
        name = entry['name']
        if name not in merged or 'percent' in entry:
            merged[name] = entry

    for entry in _get_ble_devices():
        name = entry['name']
        if name not in merged or 'percent' in entry:
            merged[name] = entry

    return list(merged.values())
