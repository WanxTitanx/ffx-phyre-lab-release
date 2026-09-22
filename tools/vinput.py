#!/usr/bin/env python3
"""vinput.py — virtual keyboard via /dev/uinput (kernel-level input).

Works under Wayland/X11 and reaches Wine/Proton games where xdotool XTEST
may not. Creates a uinput device, emits EV_KEY presses, exits.

Usage:
  vinput.py key <NAME> [NAME ...]   tap keys (80ms hold, 60ms gap)
  vinput.py hold <NAME> <ms>        hold a key for ms
  vinput.py type <text>             type ASCII characters
Key names: UP DOWN LEFT RIGHT Z X ENTER SPACE ESC F1 F2 F3 F4 W A S D
"""

import fcntl
import os
import struct
import sys
import time

# uinput ioctls
UI_DEV_CREATE = 0x5501
UI_DEV_DESTROY = 0x5502
UI_SET_EVBIT = 0x40045564
UI_SET_KEYBIT = 0x40045565
UI_DEV_SETUP = 0x405C5503

EV_SYN, EV_KEY = 0x00, 0x01
SYN_REPORT = 0
BUS_USB = 0x03

KEYS = {
    'ESC': 1, '1': 2, '2': 3, '3': 4, '4': 5, '5': 6, '6': 7, '7': 8,
    '8': 9, '9': 10, '0': 11, 'MINUS': 12, 'EQUAL': 13, 'BS': 14,
    'TAB': 15, 'Q': 16, 'W': 17, 'E': 18, 'R': 19, 'T': 20, 'Y': 21,
    'U': 22, 'I': 23, 'O': 24, 'P': 25, 'LBRACKET': 26, 'RBRACKET': 27,
    'ENTER': 28, 'CTRL': 29, 'A': 30, 'S': 31, 'D': 32, 'F': 33,
    'G': 34, 'H': 35, 'J': 36, 'K': 37, 'L': 38, 'SEMICOLON': 39,
    'APOSTROPHE': 40, 'GRAVE': 41, 'LSHIFT': 42, 'BACKSLASH': 43,
    'Z': 44, 'X': 45, 'C': 46, 'V': 47, 'B': 48, 'N': 49, 'M': 50,
    'COMMA': 51, 'DOT': 52, 'SLASH': 53, 'RSHIFT': 54, 'KPSTAR': 55,
    'ALT': 56, 'SPACE': 57, 'CAPSLOCK': 58,
    'F1': 59, 'F2': 60, 'F3': 61, 'F4': 62, 'F5': 63, 'F6': 64,
    'F7': 65, 'F8': 66, 'F9': 67, 'F10': 68,
    'UP': 103, 'LEFT': 105, 'RIGHT': 106, 'DOWN': 108,
    'HOME': 102, 'END': 107, 'PAGEUP': 104, 'PAGEDOWN': 109,
    'INSERT': 110, 'DELETE': 111,
}


def create():
    fd = os.open('/dev/uinput', os.O_WRONLY | os.O_NONBLOCK)
    fcntl.ioctl(fd, UI_SET_EVBIT, EV_KEY)
    for code in KEYS.values():
        fcntl.ioctl(fd, UI_SET_KEYBIT, code)
    # struct uinput_setup: char name[80]; __u16 bustype, vendor, product, version; __u32 ff_effects_max
    name = b'phyre-lab vinput'.ljust(80, b'\0')
    setup = name + struct.pack('<HHHHI', BUS_USB, 1, 1, 1, 0)
    fcntl.ioctl(fd, UI_DEV_SETUP, setup)
    fcntl.ioctl(fd, UI_DEV_CREATE)
    time.sleep(0.4)  # let compositor see the device
    return fd


def emit(fd, evtype, code, value):
    ev = struct.pack('<qqHHi', int(time.time()), 0, evtype, code, value)
    os.write(fd, ev)


def keypress(fd, code, hold_ms=80):
    emit(fd, EV_KEY, code, 1)
    emit(fd, EV_SYN, SYN_REPORT, 0)
    time.sleep(hold_ms / 1000.0)
    emit(fd, EV_KEY, code, 0)
    emit(fd, EV_SYN, SYN_REPORT, 0)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    fd = create()
    try:
        cmd = sys.argv[1]
        if cmd == 'key':
            for name in sys.argv[2:]:
                name = name.upper()
                if name not in KEYS:
                    print('unknown key %s' % name, file=sys.stderr)
                    return 2
                keypress(fd, KEYS[name])
                time.sleep(0.06)
        elif cmd == 'hold':
            name, ms = sys.argv[2].upper(), int(sys.argv[3])
            emit(fd, EV_KEY, KEYS[name], 1)
            emit(fd, EV_SYN, SYN_REPORT, 0)
            time.sleep(ms / 1000.0)
            emit(fd, EV_KEY, KEYS[name], 0)
            emit(fd, EV_SYN, SYN_REPORT, 0)
        elif cmd == 'type':
            for ch in sys.argv[2].upper():
                if ch == ' ':
                    keypress(fd, KEYS['SPACE'])
                elif ch in KEYS:
                    keypress(fd, KEYS[ch])
                time.sleep(0.03)
        else:
            print('unknown cmd', file=sys.stderr)
            return 2
    finally:
        time.sleep(0.15)
        fcntl.ioctl(fd, UI_DEV_DESTROY)
        os.close(fd)
    return 0


if __name__ == '__main__':
    sys.exit(main())
