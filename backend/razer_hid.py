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


def _try_hid_battery(path: bytes, slot: int = 0x01, usage_page: int = 0) -> dict | None:
    # Les interfaces 0x59 (HyperSpeed propriétaire) peuvent nécessiter report ID 0x01
    rep_ids = (0x00, 0x01) if usage_page == 0x59 else (0x00,)
    dev = hid.device()
    try:
        dev.open_path(path)
        time.sleep(0.05)
        for (tid, cls_, cid) in _VARIANTS:
            for rep_id in rep_ids:
                try:
                    dev.send_feature_report(bytes([rep_id]) + _build_report(tid, cls_, cid, (slot,)))
                    time.sleep(0.25)
                    resp = dev.get_feature_report(rep_id, 91)
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
            battery = _try_hid_battery(iface['path'], slot, iface.get('usage_page', 0))
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
# Canal 2 — Bluetooth universel via WinRT DeviceInformation
# Couvre : Bluetooth classique (BlackWidow V3 Mini) + BLE (Cobra Pro)
# Lit la propriété Windows System.Devices.BatteryStrengthPercent que Windows
# remplit automatiquement depuis les rapports HID ou le profil GATT Battery.
# ══════════════════════════════════════════════════════════════════════════════

_BT_BATTERY_PROP = 'System.Devices.BatteryStrengthPercent'

# UUID standard GATT Battery Service (fallback BLE si la propriété est vide)
_BATT_SVC  = '0000180f-0000-1000-8000-00805f9b34fb'
_BATT_CHAR = '00002a19-0000-1000-8000-00805f9b34fb'


async def _gatt_battery(device_id: str) -> int | None:
    """Fallback GATT BAS pour appareils BLE si la propriété Windows est vide."""
    try:
        from winrt.windows.devices.bluetooth import BluetoothLEDevice
        from winrt.windows.devices.bluetooth.genericattributeprofile import (
            GattCommunicationStatus,
        )
        ble = await BluetoothLEDevice.from_id_async(device_id)
        svc = await ble.get_gatt_services_for_uuid_async(_BATT_SVC)
        if svc.status != GattCommunicationStatus.SUCCESS or not svc.services:
            return None
        ch = await svc.services[0].get_characteristics_for_uuid_async(_BATT_CHAR)
        if ch.status != GattCommunicationStatus.SUCCESS or not ch.characteristics:
            return None
        val = await ch.characteristics[0].read_value_async()
        if val.status != GattCommunicationStatus.SUCCESS:
            return None
        return bytes(val.value)[0]   # 0–100
    except Exception:
        return None


async def _get_bt_devices_async() -> list[dict]:
    """
    Énumère les appareils Bluetooth appairés en deux passes :
      1. BluetoothDevice.get_device_selector()   → BT classique (BlackWidow V3 Mini)
      2. BluetoothLEDevice.get_device_selector() → BLE (Cobra Pro)
    Ces sélecteurs génèrent un AQS valide sans nécessiter DeviceInformationKind.
    Battery : System.Devices.BatteryStrengthPercent via create_from_id_async,
    puis fallback GATT BAS pour les appareils BLE.
    """
    try:
        from winrt.windows.devices.enumeration import DeviceInformation
        from winrt.windows.devices.bluetooth import BluetoothDevice, BluetoothLEDevice
    except ImportError:
        return []

    merged: dict[str, dict] = {}

    async def _scan(aqs: str, gatt_fallback: bool) -> None:
        try:
            devs = await DeviceInformation.find_all_async(aqs)
        except Exception:
            return
        for dev in devs:
            name = dev.name or ''
            if 'razer' not in name.lower() or not _is_allowed(name):
                continue
            entry: dict = {'id': dev.id, 'name': name, 'type': _device_type(name)}
            # Propriété Windows battery
            try:
                dev2 = await DeviceInformation.create_from_id_async(
                    dev.id, [_BT_BATTERY_PROP]
                )
                raw = dev2.properties[_BT_BATTERY_PROP]
                if raw is not None and int(raw) > 0:
                    entry['percent']  = int(raw)
                    entry['charging'] = False
            except Exception:
                pass
            # Fallback GATT BAS (BLE uniquement)
            if 'percent' not in entry and gatt_fallback:
                pct = await _gatt_battery(dev.id)
                if pct is not None and pct > 0:
                    entry['percent']  = pct
                    entry['charging'] = False
            if 'percent' not in entry:
                entry['wired'] = True
            if name not in merged or 'percent' in entry:
                merged[name] = entry

    await _scan(BluetoothDevice.get_device_selector(),   gatt_fallback=False)
    await _scan(BluetoothLEDevice.get_device_selector(), gatt_fallback=True)
    return list(merged.values())


def _get_bt_devices() -> list[dict]:
    """Wrapper synchrone pour _get_bt_devices_async."""
    if sys.platform != 'win32':
        return []
    try:
        loop = asyncio.new_event_loop()
        return loop.run_until_complete(_get_bt_devices_async())
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

    for entry in _get_bt_devices():
        name = entry['name']
        if name not in merged or 'percent' in entry:
            merged[name] = entry

    return list(merged.values())
