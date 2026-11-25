"""cbmplay.py - Commodore tape player backend (C64, VIC-20, C16/Plus4).

Plays a tape image out of the sound card, block by block, and is driven by the
same control-file protocol as uefplay.py:

    pause | resume | stop | rewind[:N] | fastforward[:N] | jump:N

Blocks are numbered from zero. current_block, total_blocks and next_block are
written next to the control file.

Formats: TAP (v0/v1/v2), T64, TZX (and CDT/TSX), CSW, PRG, P00, WAV, MP3, FLAC,
OGG and anything else ffmpeg can read. Any of these may be inside a .gz or .zip.

Decoders for compressed audio are tried in this order: miniaudio, soundfile,
ffmpeg (ffmpeg.exe next to this script, or on PATH). WAV needs none of them.
"""
import argparse
import copy
import hashlib
import gzip
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zipfile
import zlib

import numpy as np

# --- Timing ----------------------------------------------------------------
# TAP values are CPU cycles / 8, at the clock of the machine that made the tape.
CLOCKS = {
    'c64': {0: 985248, 1: 1022730, 2: 1022730, 3: 1023440},
    'vic20': {0: 1108405, 1: 1022727},
    'c16': {0: 886724, 1: 894886},
}
MACHINE_BY_PLATFORM = {0: 'c64', 1: 'vic20', 2: 'c16'}
MACHINE_NAMES = {'c64': 'C64', 'vic20': 'VIC-20', 'c16': 'C16/Plus4'}
VIDEO_NAMES = {0: 'PAL', 1: 'NTSC', 2: 'NTSC', 3: 'PAL-N'}
TZX_CLOCK = 3500000.0            # TZX timings are Spectrum T-states

DEFAULT_RATES = (96000, 48000, 44100)
WRITE_SIZE = 1024                # samples between control-file checks
RENDER_PULSES = 8192             # pulses rendered per step
PCM_STEP = 16384                 # samples yielded per step for audio files
AMPLITUDE = 0.8

# A pulse longer than this is a pause, so it is drawn as one short wave then
# silence. That keeps the loader's edge in the right place without long DC.
LONG_PULSE = 0.004
LONG_TAIL = 0.0005

# The C64 and VIC-20 time one edge only. With a 50% square wave the opposite
# edge lands mid-pulse and its spacing is wrong, so a polarity flip in the audio
# path corrupts every bit. Holding the first part of each pulse to a fixed width
# gives both edges the correct spacing, so either polarity loads.
EDGE_WIDTH = 0.00012

# Block detection
SILENT_PULSE = 0.003
GAP_SECONDS = 0.2
PILOT_SECONDS = 0.5
PILOT_RATIO = (0.85, 1.18)
MIN_BLOCK_PULSES = 64

# How long the data leader lasts on tapes built from T64/PRG. The machine stops
# the motor at FOUND but audio keeps running, so the leader has to outlast the wait.
FOUND_WAIT_SECONDS = 12.0

# Leader tone before the header on tapes we build. The KERNAL needs only a second
# or two to lock on, so 3 seconds is enough as long as the machine is already
# waiting (SHIFT+RUN/STOP pressed) when playback starts. Original TAP, TZX and
# audio files are played as recorded and keep their own leaders.
HEADER_LEADER_SECONDS = 3.0

# Short Leader (Tape Options, for testing): the leader tone before each standard
# file is cut to this share of its length, but never below LEADER_FLOOR_SECONDS.
# Leaders that cover a wait (FOUND on the C64 and C16, or time to type RUN before
# a later part) are left alone.
LEADER_SCALE = 1.0
LEADER_FLOOR_SECONDS = 0.5

AUDIO_EXTENSIONS = {'.wav', '.mp3', '.flac', '.ogg', '.oga', '.aif', '.aiff',
                    '.m4a', '.aac', '.wma', '.opus'}
IMAGE_EXTENSIONS = {'.tap', '.t64', '.tzx', '.cdt', '.tsx', '.csw', '.prg', '.p00'}


class TapeError(Exception):
    """A problem worth showing to the user as it stands."""


class Tape:
    """A tape in one of two shapes: timed pulses, or recorded samples."""

    def __init__(self, description):
        self.description = description
        self.pulses = None       # seconds per pulse
        self.levels = None       # +1/-1/0 per half-wave, None if pulses are whole cycles
        self.edge_timed = False  # whole cycles for a machine that times one edge only
        self.edge_widths = {}    # per-block override of EDGE_WIDTH
        self.counter_seconds = None   # normal-speed tape time at each block edge
        self.needs_rate = 0      # sample rate the tape cannot play without
        self.pcm = None          # int16 samples
        self.pcm_rate = 0
        self.starts = [0]        # first pulse (or sample) of each block
        self.names = []
        self.pause_before = set()    # blocks playback stops before, until Play
        self.header_clock = None     # clock to find KERNAL headers by (not the C16)

    @property
    def total_blocks(self):
        return len(self.starts)

    def block_range(self, index):
        end = self.starts[index + 1] if index + 1 < len(self.starts) else self.length
        return self.starts[index], end

    @property
    def length(self):
        return len(self.pcm) if self.pcm is not None else len(self.pulses)


# --- Block detection -------------------------------------------------------
def runs_of(mask):
    """Return (starts, ends) of each True run."""
    padded = np.concatenate(([0], mask.astype(np.int8), [0]))
    delta = np.diff(padded)
    return np.flatnonzero(delta == 1), np.flatnonzero(delta == -1)


def find_block_starts(durations, levels=None):
    """Split a pulse stream at pauses and at the start of each leader tone."""
    count = len(durations)
    if count == 0:
        return [0]
    cumulative = np.concatenate(([0.0], np.cumsum(durations)))
    cuts = []

    silent = durations > SILENT_PULSE
    if levels is not None:
        silent |= levels == 0
    begin, end = runs_of(silent)
    long_gaps = cumulative[end] - cumulative[begin] >= GAP_SECONDS
    cuts.extend(int(e) for e in end[long_gaps] if e < count)

    if count > 1:
        ratio = durations[1:] / np.maximum(durations[:-1], 1e-9)
        steady = (ratio > PILOT_RATIO[0]) & (ratio < PILOT_RATIO[1]) & ~silent[1:] & ~silent[:-1]
        begin, end = runs_of(steady)
        long_tones = cumulative[end + 1] - cumulative[begin] >= PILOT_SECONDS
        cuts.extend(int(b) for b in begin[long_tones])

    starts = [0]
    for cut in sorted(set(cuts)):
        if cut - starts[-1] >= MIN_BLOCK_PULSES and count - cut >= MIN_BLOCK_PULSES:
            starts.append(cut)
    return starts


# --- Binary helpers --------------------------------------------------------
def u16(data, offset):
    return struct.unpack_from('<H', data, offset)[0]


def u24(data, offset):
    return data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16


def u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]


def expand_overflow(data, width):
    """Decode TAP or CSW style data, where a zero byte introduces a longer count.

    Returns (values, exact). Plain bytes are returned as they are, and exact
    marks the values that came from the multi-byte form. width is the size of
    that form (3 for TAP, 4 for CSW). A count cut off by the end of the file is dropped.
    """
    values, flags = [], []
    position = 0
    while position < len(data):
        zero = data.find(0, position)
        if zero < 0:
            zero = len(data)
        if zero > position:
            run = np.frombuffer(data, np.uint8, zero - position, position).astype(np.float64)
            values.append(run)
            flags.append(np.zeros(len(run), bool))
        if zero + width >= len(data):
            break
        count = int.from_bytes(data[zero + 1:zero + 1 + width], 'little')
        values.append(np.array([float(count)]))
        flags.append(np.ones(1, bool))
        position = zero + 1 + width
    if not values:
        return np.zeros(0), np.zeros(0, bool)
    return np.concatenate(values), np.concatenate(flags)


def alternating(count, first):
    levels = np.empty(count, np.int8)
    levels[0::2] = first
    levels[1::2] = -first
    return levels


# --- TAP -------------------------------------------------------------------
def path_hint(path):
    """Guess the machine from folder or file names, for files that do not say."""
    text = path.lower().replace('\\', '/')
    if re.search(r'(^|[^a-z0-9])(c16|c116|plus4|plus-4|plus 4|264)([^a-z0-9]|$)', text):
        return 'c16'
    if re.search(r'(^|[^a-z0-9])(vic|vic20|vic-20|vc20|vc-20)([^a-z0-9]|$)', text):
        return 'vic20'
    return None


# A check reads the same TAP once for each speed and again for its memory: the
# decoded tape and what was found on it are kept for the rest of the run, and
# each caller gets its own copy of the tape.
_tap_cache = {}


def tap_remembered(key, make):
    if key not in _tap_cache:
        if len(_tap_cache) > 32:
            _tap_cache.clear()
        _tap_cache[key] = make()
    value = _tap_cache[key]
    return value if isinstance(value, Tape) else copy.deepcopy(value)


def copy_tape(tape):
    other = Tape(tape.description)
    for name, value in vars(tape).items():
        if isinstance(value, np.ndarray):
            value = value.copy()
        elif isinstance(value, (list, dict, set)):
            value = type(value)(value)
        setattr(other, name, value)
    return other


def parse_tap(data, path, options):
    if len(data) < 20:
        raise TapeError('TAP file is too short')
    version, platform, video = data[12], data[13], data[14]
    if version > 2:
        raise TapeError(f'TAP version {version} is not supported')
    if data[:3] == b'C16' and platform not in MACHINE_BY_PLATFORM:
        platform = 2

    machine = MACHINE_BY_PLATFORM.get(platform, 'c64')
    if data[:3] == b'C16':
        machine = 'c16'
    elif version == 0 and platform == 0:
        machine = path_hint(path) or 'c64'
    if options.machine != 'auto':
        machine = options.machine
    if options.video is not None:
        video = options.video
    clock = CLOCKS[machine].get(video, CLOCKS[machine][0])

    key = (hashlib.blake2b(bytes(data), digest_size=16).digest(), machine, video)
    base = tap_remembered(('tape',) + key, lambda: decode_tap(data, machine, version, video, clock))
    tape = copy_tape(base)

    if options.turbo:
        found, reason = tap_remembered(('program',) + key, lambda: read_kernal_program(
            tape, clock, version == 2, machine))
        if found is None and reason == 'several programs' and machine == 'vic20':
            # A first program that LOADs the rest: fast loaded part by part
            parts, why = tap_remembered(('parts',) + key, lambda: read_kernal_parts(
                tape, clock, version == 2, machine))
            refusal = vicm_refusal(parts) if parts else why
            if refusal is None:
                try:
                    return build_vicm_tape(parts, video, options.turbo, tape.description)
                except TapeError as problem:
                    refusal = str(problem)
            reason = refusal
        if found is None:
            tape.description += f', normal speed ({reason})'
        else:
            result = program_tape([found[:3]], machine, video, tape.description, options,
                                  relocate=found[3] == 1)
            if result.counter_seconds is not None:
                result.counter_seconds *= float(tape.pulses.sum()) / result.counter_seconds[-1]
            return result
    return tape


def decode_tap(data, machine, version, video, clock):
    """The tape as it is, at normal speed (parse_tap's first step)."""
    size = u32(data, 16)
    body = bytes(data[20:20 + size]) if 0 < size <= len(data) - 20 else bytes(data[20:])

    if version == 0:
        cycles = np.frombuffer(body, np.uint8).astype(np.float64) * 8.0
        cycles[cycles == 0] = 256.0 * 8.0
    else:
        values, exact = expand_overflow(body, 3)
        cycles = values.copy()
        cycles[~exact] *= 8.0
    if len(cycles) == 0:
        raise TapeError('TAP file holds no pulses')

    tape = Tape(f'{MACHINE_NAMES[machine]} {VIDEO_NAMES.get(video, "PAL")} TAP v{version}')
    tape.pulses = cycles / clock
    tape.edge_timed = machine != 'c16' and version < 2
    tape.header_clock = clock if machine != 'c16' else None
    if version == 2:
        # Each wave is low then high, the same falling-edge start as whole cycles.
        tape.levels = alternating(len(cycles), -1)
    tape.starts = find_block_starts(tape.pulses, tape.levels)

    if machine == 'vic20' and version < 2:
        give_later_parts_time(tape, clock)
        tape.starts = find_block_starts(tape.pulses, tape.levels)
    return tape


# --- Building tapes from programs (T64 / PRG / P00) ------------------------
class RomEncoder:
    """Writes files in the format each machine's KERNAL expects."""

    def __init__(self, machine, video):
        self.machine = machine
        self.clock = CLOCKS[machine].get(video, CLOCKS[machine][0])
        if machine == 'c16':
            short, medium, long_ = 0x35 * 8, 0x6A * 8, 0xD4 * 8
            self.end_marker = [medium]
            self.end_marker_tail = 194
            self.header_leader, self.data_leader = 16382, 16382
            self.repeat_leader, self.trailer = 243, 0
        else:
            short, medium, long_ = 0x30 * 8, 0x42 * 8, 0x56 * 8
            self.end_marker = [long_, short]
            self.end_marker_tail = 0
            self.header_leader, self.data_leader = 0x6A00, 0x1A00
            self.repeat_leader, self.trailer = 0x4F, 0x4E
        self.short = short
        marker = [long_, medium]
        zero, one = [short, medium], [medium, short]
        table = np.zeros((256, 20), np.float64)
        for byte in range(256):
            cells = list(marker)
            parity = 1
            for bit in range(8):
                value = (byte >> bit) & 1
                parity ^= value
                cells += one if value else zero
            cells += one if parity else zero
            table[byte] = cells
        self.table = table

    def encode_bytes(self, values):
        return self.table[np.frombuffer(bytes(values), np.uint8)].ravel()

    def tone(self, count):
        return np.full(count, float(self.short))

    def record(self, payload, leader):
        checksum = 0
        for byte in payload:
            checksum ^= byte
        parts = []
        for first, lead in ((0x89, leader), (0x09, self.repeat_leader)):
            parts.append(self.tone(lead))
            parts.append(self.encode_bytes(range(first, first - 9, -1)))
            parts.append(self.encode_bytes(payload))
            parts.append(self.encode_bytes([checksum]))
            parts.append(np.array(self.end_marker, np.float64))
            parts.append(self.tone(self.end_marker_tail))
        parts.append(self.tone(self.trailer))
        return np.concatenate(parts) / self.clock


def is_basic_start(machine, address):
    return address in {'c64': {0x0801}, 'vic20': {0x0401, 0x1001, 0x1201},
                       'c16': {0x1001}}[machine]


def cbm_name(name):
    if isinstance(name, str):
        name = name.upper().encode('ascii', 'replace')
    name = bytes(0x20 if b in (0x00, 0xA0) else b for b in name[:16])
    return name.ljust(16, b' ')


LATER_PART_GAP_SECONDS = 3.0      # silence before each later part, for typing RUN


def build_program_tape(files, machine, video, description):
    """files is a list of (name, load address, program bytes)."""
    encoder = RomEncoder(machine, video)
    gap = np.array([LATER_PART_GAP_SECONDS])
    short_gap = np.array([0.5])
    wait_cycles = int(FOUND_WAIT_SECONDS * encoder.clock / encoder.short)
    floor = int(LEADER_FLOOR_SECONDS * encoder.clock / encoder.short)
    if machine == 'vic20':
        # The VIC carries on straight after FOUND
        data_leader = max(int(encoder.data_leader * LEADER_SCALE), min(floor, encoder.data_leader))
    else:
        data_leader = max(encoder.data_leader, wait_cycles)

    parts, starts, names, position = [], [], [], 0
    def add(chunk):
        nonlocal position
        parts.append(chunk)
        position += len(chunk)

    for index, entry in enumerate(files):
        name, address, program = entry[:3]
        extra = entry[3] if len(entry) > 3 else b''      # code carried in the header
        end = (address + len(program)) & 0xFFFF
        file_type = 1 if is_basic_start(machine, address) else 3
        header = bytes([file_type]) + struct.pack('<HH', address, end) + cbm_name(name) + extra
        header = header.ljust(191 if machine == 'c16' else 192, b' ')   # the C16's buffer holds 191

        label = name if isinstance(name, str) else name.decode('latin-1')
        label = label.strip()
        starts.append(position)
        names.append(f'{label} (header)')
        if index == 0:
            leader = max(int(HEADER_LEADER_SECONDS * LEADER_SCALE * encoder.clock / encoder.short), floor)
        else:
            # A later part is asked for by the program after RUN, so give the full
            # KERNAL leader (about 10 seconds) for that to happen.
            leader = encoder.header_leader
        add(encoder.record(header, leader))
        add(short_gap)

        starts.append(position)
        names.append(f'{label} (data)')
        add(encoder.record(program, data_leader))
        if index < len(files) - 1:
            add(gap)

    tape = Tape(description)
    tape.pulses = np.concatenate(parts)
    tape.edge_timed = machine != 'c16'
    tape.starts = starts
    tape.names = names
    return tape


def choose_machine(options, address, path):
    if options.machine != 'auto':
        return options.machine
    if address == 0x0801:
        return 'c64'
    if address in (0x1201, 0x0401) or 0xA000 <= address <= 0xBFFF:
        return 'vic20'
    if address == 0x1001:
        return path_hint(path) or 'vic20'
    return path_hint(path) or 'c64'


def describe_built(machine, video, kind):
    return f'{MACHINE_NAMES[machine]} {VIDEO_NAMES.get(video, "PAL")} {kind}'


def parse_t64(data, path, options):
    if len(data) < 64:
        raise TapeError('T64 file is too short')
    entries = max(u16(data, 0x22), u16(data, 0x24), 1)
    records = []
    for slot in range(min(entries, (len(data) - 64) // 32)):
        at = 64 + slot * 32
        if data[at] != 1:
            continue
        records.append((u32(data, at + 8), u16(data, at + 2), u16(data, at + 4),
                        bytes(data[at + 16:at + 32])))
    if not records:
        raise TapeError('T64 file has no programs in it')

    # Many T64 files carry a wrong end address, so the real extent comes from
    # where the next program begins.
    records.sort(key=lambda record: record[0])
    files = []
    for index, (offset, start, end, name) in enumerate(records):
        limit = records[index + 1][0] if index + 1 < len(records) else len(data)
        limit = min(limit, len(data))
        stated = (end - start) & 0xFFFF
        length = limit - offset
        if index + 1 == len(records) and 0 < stated < length:
            length = stated
        if length > 0:
            files.append((name, start, bytes(data[offset:offset + length])))
    if not files:
        raise TapeError('T64 directory points outside the file')

    video = 0 if options.video is None else options.video
    machine = options.machine if options.machine != 'auto' else 'c64'
    return program_tape(files, machine, video, describe_built(machine, video, 'T64'), options)


def program_name(path):
    return re.sub(r'[^A-Za-z0-9 ]', ' ', os.path.splitext(os.path.basename(path))[0]).strip()


def parse_prg(data, path, options):
    if len(data) < 3:
        raise TapeError('PRG file is too short')
    address = u16(data, 0)
    machine = choose_machine(options, address, path)
    video = 0 if options.video is None else options.video
    return program_tape([(program_name(path), address, bytes(data[2:]))],
                        machine, video, describe_built(machine, video, 'PRG'), options)


def parse_p00(data, path, options):
    if len(data) < 29:
        raise TapeError('P00 file is too short')
    name = bytes(data[8:24]).split(b'\0')[0] or program_name(path).encode('ascii', 'replace')
    address = u16(data, 26)
    machine = choose_machine(options, address, path)
    video = 0 if options.video is None else options.video
    return program_tape([(name, address, bytes(data[28:]))],
                        machine, video, describe_built(machine, video, 'P00'), options)


# --- Fast loading (C64) -----------------------------------------------------
# On the fly, a program is replaced by a tiny BASIC-started loader sent at normal
# KERNAL speed, followed by the program itself as short numbered blocks at high
# speed. One pulse is one bit. A bad block turns the border red (and only a bad
# block: good ones cycle black, white, purple and green) and the loader
# keeps waiting for it, so rewinding a block and pressing Play retries it.
TURBO_LEVELS = {            # pulse lengths in TAP units (8 cycles): zero, one
    1: (0x1A, 0x2A),
    2: (0x12, 0x1E),
    3: (0x0E, 0x18),
    4: (0x0A, 0x14),
}
TURBO_CHUNK = 4096
TURBO_LOADER_BASE = 0x0340
TURBO_PILOT = 1200
TURBO_FIRST_PILOT_SECONDS = 1.0
TURBO_GAP_SECONDS = 0.5
HEADER_CODE = 0x0351      # free part of the header block, after type, addresses and name
DONE_CODE = 0x0400        # screen memory, unused while the screen is blank

COUNTS_PER_SECOND = 0.5   # keeps a 30-minute tape side within the 3-digit counter

OPCODES = {
    'adc': {'imm': 0x69}, 'and': {'imm': 0x29}, 'asl': {'imp': 0x0A},
    'bcc': {'rel': 0x90}, 'bcs': {'rel': 0xB0}, 'beq': {'rel': 0xF0}, 'bne': {'rel': 0xD0},
    'bit': {'abs': 0x2C}, 'lsr': {'imp': 0x4A}, 'clc': {'imp': 0x18}, 'cli': {'imp': 0x58}, 'cmp': {'imm': 0xC9, 'zp': 0xC5},
    'dec': {'zp': 0xC6}, 'dex': {'imp': 0xCA}, 'dey': {'imp': 0x88}, 'eor': {'imm': 0x49, 'zp': 0x45},
    'inc': {'zp': 0xE6, 'abs': 0xEE}, 'jmp': {'abs': 0x4C, 'ind': 0x6C}, 'jsr': {'abs': 0x20},
    'iny': {'imp': 0xC8}, 'inx': {'imp': 0xE8},
    'lda': {'imm': 0xA9, 'zp': 0xA5, 'abs': 0xAD, 'absx': 0xBD, 'absy': 0xB9, 'indy': 0xB1},
    'bpl': {'rel': 0x10},
    'ldx': {'imm': 0xA2, 'zp': 0xA6, 'abs': 0xAE}, 'ldy': {'imm': 0xA0, 'zp': 0xA4},
    'cpx': {'imm': 0xE0}, 'cpy': {'imm': 0xC0},
    'ora': {'imm': 0x09, 'zp': 0x05}, 'rol': {'zp': 0x26},
    'rts': {'imp': 0x60}, 'sei': {'imp': 0x78},
    'sta': {'zp': 0x85, 'abs': 0x8D, 'absx': 0x9D, 'absy': 0x99, 'indy': 0x91},
    'stx': {'zp': 0x86, 'abs': 0x8E}, 'sty': {'zp': 0x84},
}
MODE_SIZE = {'imp': 1, 'imm': 2, 'zp': 2, 'rel': 2, 'indy': 2, 'abs': 3, 'absx': 3, 'absy': 3, 'ind': 3}


def assemble(source, origin, symbols, labels_out=None):
    """A small 6502 assembler, enough for the loader below. labels_out, if given,
    gets the address of each label."""
    def value(text, labels):
        text = text.strip()
        if text.startswith('$'):
            return int(text[1:], 16)
        if text.lstrip('-').isdigit():
            return int(text)
        for name, known in ((text, symbols), (text, labels)):
            if name in known:
                return known[name]
        return None

    parsed, labels, address = [], {}, origin
    for raw in source.splitlines():
        line = raw.split(';')[0].strip()
        if not line:
            continue
        if ':' in line:
            label, line = line.split(':', 1)
            labels[label.strip()] = address
            line = line.strip()
            if not line:
                continue
        mnemonic, _, operand = line.partition(' ')
        operand = operand.strip()
        modes = OPCODES[mnemonic]
        if not operand:
            mode = 'imp'
        elif operand.startswith('#'):
            mode, operand = 'imm', operand[1:]
        elif operand.endswith(',y') and operand.startswith('('):
            mode, operand = 'indy', operand[1:-3]
        elif operand.startswith('(') and operand.endswith(')'):
            mode, operand = 'ind', operand[1:-1]
        elif operand.endswith(',x'):
            mode, operand = 'absx', operand[:-2]
        elif operand.endswith(',y'):
            mode, operand = 'absy', operand[:-2]
        elif 'rel' in modes:
            mode = 'rel'
        else:
            known = value(operand, {})
            mode = 'zp' if known is not None and known < 256 and 'zp' in modes else 'abs'
        parsed.append((mnemonic, mode, operand, address))
        address += MODE_SIZE[mode]

    if labels_out is not None:
        labels_out.update(labels)
    output = bytearray()
    for mnemonic, mode, operand, at in parsed:
        output.append(OPCODES[mnemonic][mode])
        if mode == 'imp':
            continue
        number = value(operand, labels)
        if number is None:
            raise TapeError(f'Fast loader: unknown symbol {operand}')
        if mode == 'rel':
            offset = number - (at + 2)
            if not -128 <= offset <= 127:
                raise TapeError(f'Fast loader: branch to {operand} is out of range')
            output.append(offset & 0xFF)
        elif MODE_SIZE[mode] == 2:
            output.append(number & 0xFF)
        else:
            output += struct.pack('<H', number)
    return bytes(output)


TURBO_INIT = """
        sei
        lda #$7f
        sta $dc0d
        sta $dd0d
        lda $dc0d
        lda $dd0d
        lda #$0b        ; blank the screen: no bad lines to spoil timing
        sta $d011
        lda $01         ; motor on
        and #$1f
        sta $01
        lda #7          ; timer A divides the clock by 8
        sta $dd04
        lda #0
        sta $dd05
        lda #$11
        sta $dd0e
        lda #$ff
        sta $dd06
        sta $dd07
        ldx #$59        ; X stays $59 throughout: restarts timer B
        stx $dd0f
        lda #0
        sta expect
        lda #STARTLO
        sta ptr
        lda #STARTHI
        sta ptrhi
        jmp HEADCODE
"""

# Runs once everything has loaded. Kept apart so the rest fits in the header.
TURBO_DONE = """
        lda $01         ; motor off, screen on
        ora #$20
        sta $01
        lda #$1b
        sta $d011
        lda #ENDLO      ; tell BASIC where the program ends, then RUN it
        sta $2d
        lda #ENDHI
        sta $2e
        jsr $ff84
        jsr $ff8a
        cli
        jsr $a533
        jsr $a659
        jmp $a7ae
"""

# Runs from the cassette buffer, clear of everything the game overwrites.
TURBO_SOURCE = """
; Block limits are set between blocks, so that reading the block number and
; the first data bit has nothing slow in between.
setup:  lda ptr
        sta bstlo
        sta limlo
        lda ptrhi
        sta bsthi
        clc
        adc #16
        sta limhi
        lda expect
        cmp #LAST
        bne next
        lda #ENDLO
        sta limlo
        lda #ENDHI
        sta limhi
next:   lda #32         ; need 32 zero bits of pilot
        sta zeros
s1:     jsr getbit
        bcs next
        dec zeros
        bne s1
s2:     jsr getbit      ; then a one marks the start
        bcc s2
        jsr getbyte
        cmp expect
        bne next
        ldy #0
        sty sum
loop:   jsr getbyte
        sta (ptr),y
        eor sum
        sta sum
        inc ptr
        bne same
        inc ptrhi
same:   lda ptr
        cmp limlo
        bne loop
        lda ptrhi
        cmp limhi
        bne loop
        jsr getbyte
        cmp sum
        beq good
        lda #2          ; red border: bad block, wait for it again
        sta $d020
        lda bstlo
        sta ptr
        lda bsthi
        sta ptrhi
        jmp next
good:   inc expect
        lda expect
        and #$05        ; black, white, purple or green: never red, which is kept for a bad block
        sta $d020
        lda expect
        cmp #COUNT
        bne again
        jmp DONE
again:  jmp setup
getbyte: lda #1
        sta cur
gb1:    jsr getbit
        rol cur
        bcc gb1
        lda cur
        rts
getbit: lda #$10
gw:     bit $dc0d
        beq gw
        lda $dd06
        stx $dd0f
        eor #$ff
        cmp #THRESH
        rts
"""


def build_turbo_boot(start, length, chunks, threshold):
    end = start + length
    symbols = {
        'ptr': 0xFB, 'ptrhi': 0xFC, 'cur': 0xFD, 'sum': 0xFE, 'expect': 0x02,
        'limlo': 0xA3, 'limhi': 0xA4, 'zeros': 0xA5, 'bstlo': 0xA6, 'bsthi': 0xA7,
        'STARTLO': start & 0xFF, 'STARTHI': start >> 8,
        'ENDLO': end & 0xFF, 'ENDHI': end >> 8,
        'LAST': chunks - 1, 'COUNT': chunks, 'THRESH': threshold,
    }
    symbols.update(HEADCODE=HEADER_CODE, DONE=DONE_CODE)
    # The loader travels inside the header block, which is sent anyway, so the
    # boot program that follows at normal speed is only about 100 bytes.
    loader = assemble(TURBO_SOURCE, HEADER_CODE, symbols)
    if len(loader) > 192 - (HEADER_CODE - 0x033C):
        raise TapeError('Fast loader does not fit the header block')
    finish = assemble(TURBO_DONE, DONE_CODE, symbols)
    stub = bytes.fromhex('0b08 0000 9e 32303631 00 0000')   # 0 SYS2061
    code = 0x0801 + len(stub)
    copier_size = 11
    setup = assemble(TURBO_INIT, code + copier_size, symbols)
    copier = assemble(f"""
        ldx #{len(finish)}
copy:   lda ${code + copier_size + len(setup) - 1:04x},x
        sta ${DONE_CODE - 1:04x},x
        dex
        bne copy
""", code, {})
    assert len(copier) == copier_size
    return loader, stub + copier + setup + finish


# --- Fast loading (VIC-20) --------------------------------------------------
# The same tape format as the C64 (but a bad block turns the screen red, and
# the border changes colour as each good block arrives), read through VIA 2: the tape line sets the CA1
# flag on each edge, and timer 2 is restarted on every edge with the threshold, so
# its flag being set at the next edge means a long pulse. The VIC chip never
# stops the processor, so the screen stays on. Everything, start-up included,
# lives in the header block; the program sent at normal speed is just "0 SYS849",
# so it works wherever BASIC starts, whatever RAM is fitted.
VIC_TURBO_INIT = """
        sei
        lda $911c       ; motor on (VIA 1 CA2 low)
        and #$fd
        sta $911c
        lda #TLO        ; threshold into the timer 2 low latch
        sta $9128
        ldx #THI        ; X stays THI throughout: restarts timer 2
        stx $9129
        lda $900f
        sta border
        lda #0
        sta expect
START
        clc
        lda ptr
        adc #LENLO
        sta endlo
        lda ptrhi
        adc #LENHI
        sta endhi
        jmp SETUP
"""

VIC_TURBO_DONE = """
        lda $911c       ; motor off
        ora #$02
        sta $911c
        lda border
        sta $900f
        lda #$22        ; no timer 2 or CA1 interrupts
        sta $912e
        lda $9128       ; clear both flags before interrupts come back
        bit $9121
        lda endlo       ; tell BASIC where the program ends, then RUN it
        sta $2d
        lda endhi
        sta $2e
        cli
        jsr $c533
        jsr $c659
        jmp $c7ae
"""

# In the header block. Block limits are set between blocks, so that reading the
# block number and the first data bit has nothing slow in between.
VIC_TURBO_SOURCE = """
setup:  lda ptr
        sta bstlo
        sta limlo
        lda ptrhi
        sta bsthi
        clc
        adc #16
        sta limhi
        lda expect
        cmp #LAST
        bne next
        lda endlo
        sta limlo
        lda endhi
        sta limhi
next:   lda #32         ; need 32 zero bits of pilot
        sta zeros
s1:     jsr getbit
        bcs next
        dec zeros
        bne s1
s2:     jsr getbit      ; then a one marks the start
        bcc s2
        jsr getbyte
        cmp expect
        bne next
        ldy #0
        sty sum
loop:   jsr getbyte
        sta (ptr),y
        eor sum
        sta sum
        inc ptr
        bne same
        inc ptrhi
same:   lda ptr
        cmp limlo
        bne loop
        lda ptrhi
        cmp limhi
        bne loop
        jsr getbyte
        cmp sum
        beq good
        lda border      ; red screen: bad block, wait for it again
        and #$0f
        ora #$20
        sta $900f
        lda bstlo
        sta ptr
        lda bsthi
        sta ptrhi
        jmp next
good:   inc expect
        lda expect
        and #$05        ; black, white, purple or green border: never red, which is kept for a bad block
        eor border      ; into the border bits only: screen colour and mode stay as they were
        and #$07
        eor border
        sta $900f
        lda expect
        cmp #COUNT
        beq fin
        jmp setup
fin:    jmp DONE
getbyte: lda #1
        sta cur
gb1:    jsr getbit
        rol cur
        bcc gb1
        lda cur
        rts
getbit: lda $912d       ; CA1 (an edge) into carry, timer 2 flag to bit 3
        lsr
        lsr
        bcc getbit
        stx $9129       ; restart timer 2, clearing its flag
        bit $9121       ; clears the CA1 flag, leaves A alone
        and #$08
        cmp #$04        ; carry set: timer ran out, a long pulse
        rts
"""

# Straight after the SYS line, wherever BASIC loaded it (so no absolute addresses
# of its own): copies the start-up and finish code to LOW, clear of the game.
VIC_TURBO_COPIER = """
        clc
        lda $2b
        adc #STUB
        sta ptr
        lda $2c
        adc #0
        sta ptrhi
        ldy #COPYLAST
copy:   lda (ptr),y
        sta LOW,y
        dey
        bpl copy
        jmp LOW
"""
VIC_TURBO_LATENCY = 12      # cycles from reading the edge to restarting timer 2
VIC_LOW_CODE = 0x02A1       # unused RAM up to $02FF


def build_vic_turbo_boot(relocate, start, length, chunks, threshold):
    """(header code, boot program). The boot program is "0 SYS<entry>" then the
    start-up and finish code, which the header code copies to VIC_LOW_CODE."""
    if relocate:
        # Loaded where BASIC starts, just as LOAD would put it
        place = """
        lda $2b
        sta ptr
        lda $2c
        sta ptrhi"""
    else:
        place = f"""
        lda #{start & 0xFF}
        sta ptr
        lda #{start >> 8}
        sta ptrhi"""
    symbols = {
        'ptr': 0xFB, 'ptrhi': 0xFC, 'cur': 0xFD, 'sum': 0xFE, 'expect': 0x02,
        'limlo': 0xA3, 'limhi': 0xA4, 'zeros': 0xA5, 'bstlo': 0xA6, 'bsthi': 0xA7,
        'border': 0xA8, 'endlo': 0xA9, 'endhi': 0xAA,
        'LENLO': length & 0xFF, 'LENHI': length >> 8,
        'TLO': threshold & 0xFF, 'THI': threshold >> 8,
        'LAST': chunks - 1, 'COUNT': chunks, 'SETUP': HEADER_CODE, 'LOW': VIC_LOW_CODE,
    }
    init = assemble(VIC_TURBO_INIT.replace('START', place), VIC_LOW_CODE, symbols)
    symbols['DONE'] = VIC_LOW_CODE + len(init)
    done = assemble(VIC_TURBO_DONE, symbols['DONE'], symbols)
    low = init + done
    if VIC_LOW_CODE + len(low) > 0x0300:
        raise TapeError('VIC-20 fast loader start-up code is too long')
    symbols['COPYLAST'] = len(low) - 1
    loader = assemble(VIC_TURBO_SOURCE, HEADER_CODE, symbols)
    if len(loader) > 192 - (HEADER_CODE - 0x033C):
        raise TapeError(f'VIC-20 fast loader does not fit the header block ({len(loader)} bytes)')
    stub = vic_sys_stub(start, 0)
    stub = vic_sys_stub(start, len(stub))      # same length: the offset is two digits either way
    symbols['STUB'] = 0
    copier_size = len(assemble(VIC_TURBO_COPIER, 0, symbols))
    symbols['STUB'] = len(stub) + copier_size
    copier = assemble(VIC_TURBO_COPIER, 0, symbols)
    return loader, stub + copier + low


def vic_sys_stub(load_address, offset):
    """0 SYSPEEK(43)+256*PEEK(44)+offset: runs the code just after this line, wherever
    BASIC starts. Linked for where it is first loaded; LOAD relinks it anyway."""
    offset = max(offset, 10)
    text = (b'\x9e\xc2(43)\xaa256\xac\xc2(44)\xaa' + str(offset).encode() + b'\0')
    link = load_address + 4 + len(text)
    return struct.pack('<HH', link, 0) + text + b'\0\0'


# --- Fast loading VIC-20 tapes with several parts -----------------------------
# Many VIC-20 tapes are a small first program that loads the rest with LOAD. The
# first part is fast loaded as above; the loader then stays in memory and takes
# over the KERNAL's LOAD vector ($0330), so every later LOAD from tape prints
# PRESS PLAY ON TAPE and fast loads the next part on the tape instead. The player
# pauses before each later part; Play sends it when the game asks for it.
#
# Each part is one fast block: pilot, a one, a sync byte, the data and an XOR
# checksum. A bad part turns the screen red and waits for it again (Rewind, Play).
# Later parts always go to the address in their own header, as LOAD"",1,1 would.
#
# Memory, none of it the game's: the block reader and the parts table in the
# tape header block ($0351-$03FB), LOAD's replacement in $02A1-$02FF, its message
# and variables in $0334-$0350 (free once the first part is in), and the one-off
# finish for the first part in the BASIC input buffer at $0200. The zero page it
# uses is the KERNAL's own tape area.
VICM_READER = """
start:  lda ptr
        sta bstlo
        lda ptrhi
        sta bsthi
next:   lda #32         ; need 32 zero bits of pilot
        sta zeros
s1:     jsr getbit
        bcs next
        dec zeros
        bne s1
s2:     jsr getbit      ; then a one marks the start
        bcc s2
        jsr getbyte
        cmp #SYNC
        bne next
        ldy #0
        sty sum
loop:   jsr getbyte
        sta (ptr),y
        eor sum
        sta sum
        inc ptr
        bne same
        inc ptrhi
same:   lda ptr
        cmp endlo
        bne loop
        lda ptrhi
        cmp endhi
        bne loop
        jsr getbyte
        cmp sum
        beq fin
        lda border      ; red screen: bad part, wait for it again
        and #$0f
        ora #$20
        sta $900f
        lda bstlo
        sta ptr
        lda bsthi
        sta ptrhi
        jmp next
fin:    lda $911c       ; motor off
        ora #$02
        sta $911c
        lda border
        sta $900f
        lda #$22        ; no timer 2 or CA1 interrupts
        sta $912e
        lda $9128       ; clear both flags before interrupts come back
        bit $9121
        jmp (DONEVEC)
getbyte: lda #1
        sta cur
gb1:    jsr getbit
        rol cur
        bcc gb1
        lda cur
        rts
getbit: lda $912d       ; CA1 (an edge) into carry, timer 2 flag to bit 3
        lsr
        lsr
        bcc getbit
        stx $9129       ; restart timer 2, clearing its flag
        bit $9121       ; clears the CA1 flag, leaves A alone
        and #$08
        cmp #$04        ; carry set: timer ran out, a long pulse
        rts
"""

# LOAD's replacement. The KERNAL arrives here from $FFD5 with A = 0 to load (1 to
# verify), the device in $BA. Anything but a load from tape, or a LOAD after the
# last part, goes to the KERNAL as usual.
VICM_LOW = """
begin:  sei
        lda $911c       ; motor on
        and #$fd
        sta $911c
        lda #TLO        ; threshold into the timer 2 low latch
        sta $9128
        ldx #THI        ; X stays THI throughout: restarts timer 2
        stx $9129
        lda $900f
        sta border
        jmp START
later:  ldx endlo       ; as LOAD returns: end address in X/Y, status 0
        ldy endhi
        lda #0
        sta $90
        clc
        cli
        rts
hook:   ldx $ba
        cpx #1
        bne orig
        cmp #0
        bne orig
        ldx PARTOFF
        cpx #TEND
        bcs orig
        ldy #0
pm:     lda MSG,y
        beq pmd
        jsr $ffd2       ; keeps Y, as the usual print loop relies on
        iny
        bne pm
pmd:    ldx PARTOFF
        ldy #0
row:    lda TABLE,x     ; start and end of this part into ptr/end
        sta $00ac,y
        inx
        iny
        cpy #4
        bne row
        stx PARTOFF
        jmp begin
orig:   jmp (OLDVEC)
"""

# Once, when the first part is in: RUN it, as the single-part loader does
VICM_FIRST = """
        lda endlo
        sta $2d
        lda endhi
        sta $2e
        lda #LATERLO
        sta DONEVEC
        lda #LATERHI
        sta DONEVEC1
        cli
        jsr $c533
        jsr $c659
        jmp $c7ae
"""

# In the boot program after its SYS line, so position-independent: copies the
# resident parts into place, takes over LOAD and starts on the first part.
VICM_BOOT = """
        clc
        lda $2b
        adc #OFFALO
        sta $fb
        lda $2c
        adc #OFFAHI
        sta $fc
        ldy #LENA
ca:     lda ($fb),y
        sta LOWA,y
        dey
        bpl ca
        clc
        lda $2b
        adc #OFFBLO
        sta $fb
        lda $2c
        adc #OFFBHI
        sta $fc
        ldy #LENB
cb:     lda ($fb),y
        sta LOWB,y
        dey
        bpl cb
        clc
        lda $2b
        adc #OFFFLO
        sta $fb
        lda $2c
        adc #OFFFHI
        sta $fc
        ldy #LENF
cf:     lda ($fb),y
        sta LOWF,y
        dey
        bpl cf
        lda $0330       ; take over LOAD
        sta OLDVEC
        lda $0331
        sta OLDVEC1
        lda #HOOKLO
        sta $0330
        lda #HOOKHI
        sta $0331
PLACE
        clc
        lda ptr
        adc #LENLO
        sta endlo
        lda ptrhi
        adc #LENHI
        sta endhi
        lda #FIRSTLO
        sta DONEVEC
        lda #FIRSTHI
        sta DONEVEC1
        jmp BEGIN
"""
VICM_LOW_A = 0x02A1          # to $02FF
VICM_LOW_B = 0x0334          # to $0350
VICM_LOW_F = 0x0200          # BASIC input buffer, needed only until the first part runs
VICM_SYNC = 0x5A
VICM_MESSAGE = b'\rPRESS PLAY ON TAPE\r\0'


def vicm_refusal(parts):
    """Why a VIC-20 tape's parts cannot be fast loaded this way, or None."""
    if len(parts) < 2:
        return 'one program'
    first = parts[0]
    if first[1] not in (0x0401, 0x1001, 0x1201):
        return 'first part is not a BASIC program'
    for name, address, program, kind in parts:
        if address < 0x0400:
            return "a part loads into the loader's memory"
        if address + len(program) > 0x8000:
            return 'a part is too long'
    return None


def build_vicm_boot(parts, threshold):
    """(header code, boot program) for a VIC-20 tape of several parts."""
    first_name, first_address, first_program, first_type = parts[0]
    relocate = first_type == 1
    symbols = {
        'ptr': 0xAC, 'ptrhi': 0xAD, 'endlo': 0xAE, 'endhi': 0xAF,
        'cur': 0xA3, 'sum': 0xA4, 'zeros': 0xA5, 'bstlo': 0xA6, 'bsthi': 0xA7, 'border': 0xA8,
        'SYNC': VICM_SYNC, 'TLO': threshold & 0xFF, 'THI': threshold >> 8,
    }
    # B: the message, then the variables
    msg = VICM_MESSAGE
    symbols['MSG'] = VICM_LOW_B
    symbols['PARTOFF'] = VICM_LOW_B + len(msg)
    symbols['OLDVEC'], symbols['OLDVEC1'] = symbols['PARTOFF'] + 1, symbols['PARTOFF'] + 2
    symbols['DONEVEC'], symbols['DONEVEC1'] = symbols['PARTOFF'] + 3, symbols['PARTOFF'] + 4

    # C: the block reader, then the table of later parts (start, end)
    reader_size = len(assemble(VICM_READER, HEADER_CODE, symbols))
    symbols['TABLE'] = HEADER_CODE + reader_size
    table = b''.join(struct.pack('<HH', address, address + len(program))
                     for name, address, program, kind in parts[1:])
    symbols['TEND'] = len(table)
    room = 192 - (HEADER_CODE - 0x033C)
    if reader_size + len(table) > room:
        raise TapeError(f'VIC-20 fast loader: too many parts ({len(parts)})')

    # A: begin, later, hook
    symbols['START'] = HEADER_CODE
    offsets = {}
    low = assemble(VICM_LOW, VICM_LOW_A, symbols, offsets)
    symbols['BEGIN'] = offsets['begin']
    symbols['LATERLO'], symbols['LATERHI'] = offsets['later'] & 0xFF, offsets['later'] >> 8
    symbols['HOOKLO'], symbols['HOOKHI'] = offsets['hook'] & 0xFF, offsets['hook'] >> 8
    if VICM_LOW_A + len(low) > 0x0300:
        raise TapeError(f'VIC-20 fast loader: LOAD code too long ({len(low)} bytes)')
    variables = bytes([0]) + b'\0\0' + struct.pack('<H', offsets['later'])
    part_b = msg + variables
    if VICM_LOW_B + len(part_b) > 0x0351:
        raise TapeError('VIC-20 fast loader: message and variables too long')

    first = assemble(VICM_FIRST, VICM_LOW_F, symbols)
    symbols['FIRSTLO'], symbols['FIRSTHI'] = VICM_LOW_F & 0xFF, VICM_LOW_F >> 8
    reader = assemble(VICM_READER, HEADER_CODE, symbols)

    if relocate:
        place = """
        lda $2b
        sta ptr
        lda $2c
        sta ptrhi"""
    else:
        place = f"""
        lda #{first_address & 0xFF}
        sta ptr
        lda #{first_address >> 8}
        sta ptrhi"""
    length = len(first_program)
    symbols.update({'LOWA': VICM_LOW_A, 'LOWB': VICM_LOW_B, 'LOWF': VICM_LOW_F,
                    'LENA': len(low) - 1, 'LENB': len(part_b) - 1, 'LENF': len(first) - 1,
                    'LENLO': length & 0xFF, 'LENHI': length >> 8})
    source = VICM_BOOT.replace('PLACE', place)
    # The offsets of the copies depend on the boot code's own length: two passes
    for name in ('OFFALO', 'OFFAHI', 'OFFBLO', 'OFFBHI', 'OFFFLO', 'OFFFHI'):
        symbols.setdefault(name, 0)
    stub = vic_sys_stub(first_address, 0)
    stub = vic_sys_stub(first_address, len(stub))
    boot_code = assemble(source, 0, symbols)
    offset_a = len(stub) + len(boot_code)
    offset_b = offset_a + len(low)
    offset_f = offset_b + len(part_b)
    symbols.update({'OFFALO': offset_a & 0xFF, 'OFFAHI': offset_a >> 8,
                    'OFFBLO': offset_b & 0xFF, 'OFFBHI': offset_b >> 8,
                    'OFFFLO': offset_f & 0xFF, 'OFFFHI': offset_f >> 8})
    boot_code = assemble(source, 0, symbols)
    boot_program = stub + boot_code + low + part_b + first
    return reader + table, boot_program


def build_vicm_tape(parts, video, level, description):
    """A fast-load tape for a VIC-20 tape of several parts, pausing before each
    later part."""
    encoder = RomEncoder('vic20', video)
    zero_units, one_units = TURBO_LEVELS[level]
    scale = encoder.clock / CLOCKS['c64'][0]
    zero, one = zero_units * 8.0 * scale, one_units * 8.0 * scale
    threshold = int((zero + one) / 2) - VIC_TURBO_LATENCY
    header_code, boot_program = build_vicm_boot(parts, threshold)
    name = parts[0][0]
    boot = build_program_tape([(name, 0x1001, boot_program, header_code)], 'vic20', video, description)
    pieces = [boot.pulses, np.array([TURBO_GAP_SECONDS])]
    position = len(boot.pulses) + 1
    starts, names = list(boot.starts), list(boot.names)
    pilot = int(TURBO_FIRST_PILOT_SECONDS * encoder.clock / zero)
    pause_before = set()
    for index, (part_name, address, program, kind) in enumerate(parts):
        checksum = 0
        for byte in program:
            checksum ^= byte
        bits = np.unpackbits(np.frombuffer(bytes([VICM_SYNC]) + bytes(program) + bytes([checksum]), np.uint8))
        cycles = np.concatenate((
            np.full(pilot, zero), [one],
            np.where(bits == 1, one, zero).astype(np.float64),
            np.full(8, zero),
        )) / encoder.clock
        if index > 0:
            pause_before.add(len(starts))
        starts.append(position)
        names.append(f'Fast part {index + 1} of {len(parts)}')
        pieces.append(cycles)
        position += len(cycles)

    tape = Tape(f'{description}, fast load {level}, {len(parts)} parts (pauses for each part)')
    tape.pulses = np.concatenate(pieces)
    tape.edge_timed = True
    tape.starts = starts
    tape.names = names
    tape.pause_before = pause_before
    width = 0.4 * zero / encoder.clock
    tape.edge_widths = {block: width for block in range(len(boot.starts), len(starts))}
    return tape


# --- Fast loading (C16 and Plus/4) ----------------------------------------
# The same tape format again. The TED machines have no chip that flags an edge,
# so the loader watches the tape line (bit 4 of $01) for each fall, and TED timer
# 2, restarted at every edge with the threshold, has its flag set by the next
# edge when the pulse was a long one. The screen is blanked while loading, which
# lets the processor run at full speed; the border shows progress and turns red
# on a bad block. (The timer's count is read, not its interrupt flag: the TED
# data sheet does not say the flag is set while that interrupt is switched off.) The program sent at normal speed is "0 SYS PEEK(43)+..." and a
# copier: it copies the loader into screen memory, out of the game's way. At the
# end a small finish routine, copied into the tape buffer, clears the screen (and
# the loader with it), puts RUN in the keyboard buffer and goes back to BASIC.
C16_TURBO_MAIN = """
        sei
        lda $ff06       ; blank the screen
        sta saved
        and #$ef
        sta $ff06
        lda $ff19
        sta border
        lda $01         ; motor on (bit 3 low)
        and #$f7
        sta $01
        lda #0
        sta expect
START
        clc
        lda ptr
        adc #LENLO
        sta endlo
        lda ptrhi
        adc #LENHI
        sta endhi
setup:  lda ptr
        sta bstlo
        sta limlo
        lda ptrhi
        sta bsthi
        clc
        adc #16
        sta limhi
        lda expect
        cmp #LAST
        bne next
        lda endlo
        sta limlo
        lda endhi
        sta limhi
next:   lda #32         ; need 32 zero bits of pilot
        sta zeros
s1:     jsr getbit
        bcs next
        dec zeros
        bne s1
s2:     jsr getbit      ; then a one marks the start
        bcc s2
        jsr getbyte     ; a sync byte, the block number and its complement, so
        cmp #SYNC       ; a run of zeros in game data is not taken for the start
        bne next        ; of a block
        jsr getbyte
        cmp expect
        bne next
        jsr getbyte
        eor #$ff
        cmp expect
        bne next
        ldy #0
        sty sum
loop:   jsr getbyte
        sta (ptr),y
        eor sum
        sta sum
        inc ptr
        bne same
        inc ptrhi
same:   lda ptr
        cmp limlo
        bne loop
        lda ptrhi
        cmp limhi
        bne loop
        jsr getbyte
        cmp sum
        beq good
        lda #$32        ; red border: bad block, wait for it again
        sta $ff19
        lda bstlo
        sta ptr
        lda bsthi
        sta ptrhi
        jmp next
good:   inc expect
        lda expect
        and #$05
        ora #$58        ; black, white, purple or green for each block: never red
        sta $ff19
        lda expect
        cmp #COUNT
        beq done
        jmp setup
getbyte: lda #1
        sta cur
gb1:    jsr getbit
        rol cur
        bcc gb1
        lda cur
        rts
getbit: lda $01         ; wait for the tape line to be high
        and #$10
        beq getbit
gb2:    lda $01         ; then for it to fall: an edge
        and #$10
        bne gb2
        lda $ff03       ; timer 2's high byte: $FF once it has counted past zero,
        ldx #TLO        ; which it does only in a long pulse (the threshold is
        stx $ff02       ; under 256). Restart it at the threshold: writing the
        ldx #0          ; low byte stops it, the high byte starts it again
        stx $ff03
        asl             ; carry set: a long pulse
        rts
done:   lda $01         ; motor off
        ora #$08
        sta $01
        lda saved       ; screen and border back
        sta $ff06
        lda border
        sta $ff19
        lda endlo       ; tell BASIC where the program ends
        sta $2d
        lda endhi
        sta $2e
        ldx #FINLAST
dc:     lda FINSRC,x
        sta $0333,x
        dex
        bpl dc
        jmp $0333
"""

C16_TURBO_FINISH = """
        lda #$ff        ; clear the TED interrupt flags
        sta $ff09
        lda #147        ; clear the screen, and the loader in it
        jsr $ffd2
        lda #$52        ; RUN and Return in the keyboard buffer
        sta $0527
        lda #$55
        sta $0528
        lda #$4e
        sta $0529
        lda #13
        sta $052a
        lda #4
        sta $ef
        cli
        jmp ($0302)     ; back to BASIC, which runs the game
"""

# Wherever BASIC loaded the SYS line: copies two pages from just after itself to
# the loader's place in screen memory, then starts it.
C16_TURBO_COPIER = """
        clc
        lda $2b
        adc #OFF
        sta $d8
        lda $2c
        adc #0
        sta $d9
        lda #0
        sta $da
        lda #HOMEHI
        sta $db
        ldx #2
        ldy #0
copy:   lda ($d8),y
        sta ($da),y
        iny
        bne copy
        inc $d9
        inc $db
        dex
        bne copy
        jmp HOME
"""
C16_TURBO_HOME = 0x0E00         # screen memory, $0C00-$0FFF; blank while loading
C16_TURBO_SYNC = 0xD2           # follows the start bit on the C16's fast blocks
C16_TURBO_LATENCY = 6           # timer ticks from reading the edge to restarting timer 2
C16_FIRST_PILOT_SECONDS = 6.0   # time to type RUN after the first part loads


def build_c16_turbo_boot(relocate, start, length, chunks, threshold):
    """The boot program: SYS line, copier, then the loader and its finish routine."""
    place = """
        lda $2b
        sta ptr
        lda $2c
        sta ptrhi""" if relocate else f"""
        lda #{start & 0xFF}
        sta ptr
        lda #{start >> 8}
        sta ptrhi"""
    symbols = {
        # $D8-$E8 is the zero page left for applications
        'ptr': 0xD8, 'ptrhi': 0xD9, 'cur': 0xDA, 'sum': 0xDB, 'expect': 0xDC,
        'limlo': 0xDD, 'limhi': 0xDE, 'zeros': 0xDF, 'bstlo': 0xE0, 'bsthi': 0xE1,
        'border': 0xE2, 'endlo': 0xE3, 'endhi': 0xE4, 'flag': 0xE5, 'saved': 0xE6,
        'LENLO': length & 0xFF, 'LENHI': length >> 8,
        'TLO': threshold & 0xFF, 'THI': threshold >> 8,
        'LAST': chunks - 1, 'COUNT': chunks, 'HOME': C16_TURBO_HOME, 'HOMEHI': C16_TURBO_HOME >> 8,
        'SYNC': C16_TURBO_SYNC,
    }
    finish = assemble(C16_TURBO_FINISH, 0x0333, symbols)
    symbols['FINLAST'] = len(finish) - 1
    source = C16_TURBO_MAIN.replace('START', place)
    symbols['FINSRC'] = C16_TURBO_HOME          # same size either way; set properly below
    main = assemble(source, C16_TURBO_HOME, symbols)
    symbols['FINSRC'] = C16_TURBO_HOME + len(main)
    main = assemble(source, C16_TURBO_HOME, symbols)
    loader = main + finish
    if len(loader) > 0x1000 - C16_TURBO_HOME - 24 or len(finish) > 191:
        raise TapeError(f'C16 fast loader is too long ({len(loader)} bytes)')

    stub = vic_sys_stub(0x1001, 0)
    stub = vic_sys_stub(0x1001, len(stub))      # same length: the offset is two digits
    symbols['OFF'] = 0
    copier_size = len(assemble(C16_TURBO_COPIER, 0, symbols))
    symbols['OFF'] = len(stub) + copier_size
    copier = assemble(C16_TURBO_COPIER, 0, symbols)
    return stub + copier + loader


def turbo_refusal(machine, address, length):
    """Why a program cannot be fast loaded, or None if it can."""
    if machine == 'c16':
        if address != 0x1001:
            return 'not a BASIC start program'
        if address + length > 0xFD00:
            return 'too long'
        return None
    if machine == 'vic20':
        if address not in (0x0401, 0x1001, 0x1201):
            return 'not a BASIC start program'
        if address + length > 0x8000:
            return 'too long'
        return None
    if machine != 'c64':
        return 'not available for this machine'
    if address != 0x0801:
        return 'not a $0801 program'
    if address + length > 0xD000:
        return 'uses I/O area'
    return None


def build_turbo_tape(name, address, program, video, level, description, machine='c64', relocate=None):
    encoder = RomEncoder(machine, video)
    zero_units, one_units = TURBO_LEVELS[level]
    chunks = [program[at:at + TURBO_CHUNK] for at in range(0, len(program), TURBO_CHUNK)]
    if machine == 'vic20':
        # Same lengths in time as the C64, which suits the sound card; in cycles
        # that is a little longer on the faster VIC-20.
        scale = encoder.clock / CLOCKS['c64'][0]
        zero, one = zero_units * 8.0 * scale, one_units * 8.0 * scale
        threshold = int((zero + one) / 2) - VIC_TURBO_LATENCY
        if relocate is None:
            relocate = is_basic_start('vic20', address)
        loader, boot_program = build_vic_turbo_boot(relocate, address, len(program), len(chunks), threshold)
        boot_address = 0x1001       # always a BASIC program, loaded wherever BASIC starts
    elif machine == 'c16':
        # Same lengths in time as the C64 again; TED timer 2 counts at the TAP clock
        scale = encoder.clock / CLOCKS['c64'][0]
        zero, one = zero_units * 8.0 * scale, one_units * 8.0 * scale
        threshold = int((zero + one) / 2) - C16_TURBO_LATENCY
        if threshold > 255:
            raise TapeError('C16 fast load threshold must be under 256')
        if relocate is None:
            relocate = True
        boot_program = build_c16_turbo_boot(relocate, address, len(program), len(chunks), threshold)
        loader = b''
        boot_address = 0x1001
    else:
        zero, one = zero_units * 8.0, one_units * 8.0
        threshold = (zero_units + one_units) // 2
        loader, boot_program = build_turbo_boot(address, len(program), len(chunks), threshold)
        boot_address = 0x0801
    boot = build_program_tape([(name, boot_address, boot_program, loader)] if loader else
                              [(name, boot_address, boot_program)], machine, video, description)
    parts = [boot.pulses, np.array([TURBO_GAP_SECONDS])]
    position = len(boot.pulses) + 1
    starts, names = list(boot.starts), list(boot.names)
    first_pilot = int((C16_FIRST_PILOT_SECONDS if machine == 'c16' else TURBO_FIRST_PILOT_SECONDS)
                      * encoder.clock / zero)

    # The C16 gets every block twice: once through, then all again. A block it
    # misreads is picked up on the second pass instead of needing a rewind. In
    # passes, not each block twice running, so that after a good block the next
    # thing it hears is the next block's pilot rather than a repeat's data. Its
    # blocks also start with a sync byte after the start bit.
    passes = 2 if machine == 'c16' else 1
    sync = bytes([C16_TURBO_SYNC]) if machine == 'c16' else b''
    block_shares = []
    share = np.cumsum([0] + [len(chunk) for chunk in chunks]) / len(program)
    for copy in range(passes):
        for index, chunk in enumerate(chunks):
            checksum = 0
            for byte in chunk:
                checksum ^= byte
            check = bytes([index ^ 0xFF]) if sync else b''
            bits = np.unpackbits(np.frombuffer(sync + bytes([index]) + check + chunk + bytes([checksum]), np.uint8))
            cycles = np.concatenate((
                np.full(first_pilot if index == 0 and copy == 0 else TURBO_PILOT, zero),
                [one],
                np.where(bits == 1, one, zero).astype(np.float64),
                np.full(8, zero),
            )) / encoder.clock
            starts.append(position)
            names.append(f'Fast block {index}' + (f' (pass {copy + 1})' if passes > 1 else ''))
            parts.append(cycles)
            position += len(cycles)
            # The counter runs through the program once per pass
            block_shares.append((copy + share[index]) / passes)
    block_shares.append(1.0)

    # The counter shows how far along the equivalent normal-speed tape we are,
    # so it runs faster during the fast blocks and ends on the same number.
    normal = build_program_tape([(name, address, program)], machine, video, description)
    header_end = normal.pulses[:normal.starts[1]].sum()
    normal_end = float(normal.pulses.sum())
    data_leader = encoder.data_leader if machine in ('vic20', 'c16') else \
        max(encoder.data_leader, int(FOUND_WAIT_SECONDS * encoder.clock / encoder.short))
    leader_end = header_end + data_leader * encoder.short / encoder.clock
    counter_seconds = np.concatenate(([0.0, header_end], leader_end + (normal_end - leader_end) * np.array(block_shares)))

    tape = Tape(f'{description}, fast load {level}')
    tape.counter_seconds = counter_seconds
    tape.pulses = np.concatenate(parts)
    tape.edge_timed = machine != 'c16'      # the C16's own part is an even square wave
    tape.starts = starts
    tape.names = names
    # Every pulse starts with a part of fixed width, so both of its edges are the
    # same distance apart as the pulses and either polarity times correctly. The
    # C16 polls its tape line rather than latching an edge, so its fixed part is
    # half a short pulse, the widest it can be.
    width = (0.5 if machine == 'c16' else 0.4) * zero / encoder.clock
    tape.edge_widths = {block: width for block in range(len(boot.starts), len(starts))}
    return tape


def program_tape(files, machine, video, description, options, relocate=None):
    """Fast load if asked for and possible, otherwise the normal KERNAL tape.

    relocate: whether the program goes where BASIC starts (a type 1 header), as
    the tape's own header says; otherwise worked out from its address."""
    if options.turbo:
        if len(files) != 1:
            reason = 'several programs'
        else:
            reason = turbo_refusal(machine, files[0][1], len(files[0][2]))
        if reason is None:
            name, address, program = files[0]
            return build_turbo_tape(name, address, program, video, options.turbo, description, machine, relocate)
        description = f'{description}, normal speed ({reason})'
    return build_program_tape(files, machine, video, description)


def whole_cycles(pulses):
    """TAP v2 half waves joined back into whole cycles."""
    count = len(pulses) // 2 * 2
    return pulses[0:count:2] + pulses[1:count:2]


# Between short and medium, and medium and long, in TAP units (whole cycles)
KERNAL_CUTS = {'c64': (0x39, 0x4C), 'vic20': (0x39, 0x4C), 'c16': (0x4F, 0x9F)}


def kernal_records(units, cuts=KERNAL_CUTS['c64']):
    """Every KERNAL record on the tape, as lists of bytes (countdown included),
    plus how many stray medium pulses lie outside them."""
    kinds = np.where(units < cuts[0], 0, np.where(units < cuts[1], 1, 2)).astype(np.int8)
    count = len(kinds)
    limit = count - 20
    if limit <= 0:
        return [], 0
    # Every place a byte could start: a long-medium marker, then nine bit pairs
    # (short-medium or medium-short) with odd parity. Checked for all at once.
    marks = np.flatnonzero((kinds[:limit + 1] == 2) & (kinds[1:limit + 2] == 1))
    frame_ok = np.zeros(count + 1, bool)
    value = np.zeros(count + 1, np.int32)
    if len(marks):
        cells = kinds[marks[:, None] + np.arange(2, 20)]
        firsts, seconds = cells[:, 0::2], cells[:, 1::2]
        good = np.all(firsts + seconds == 1, axis=1) & np.all(firsts <= 1, axis=1)
        good &= firsts.sum(axis=1) % 2 == 1
        frame_ok[marks] = good
        value[marks] = (firsts[:, :8].astype(np.int32) << np.arange(8)).sum(axis=1)
    # How many good bytes follow on from each place, 20 pulses apart
    chain = np.zeros(count + 21, np.int32)
    for phase in range(20):
        lane = frame_ok[phase::20]
        if not lane.any():
            continue
        # For each place, how far it is to the next bad one
        bad = np.append(np.flatnonzero(~lane), len(lane))
        places = np.arange(len(lane))
        run = (bad[np.searchsorted(bad, places)] - places).astype(np.int32)
        chain[phase:phase + 20 * len(lane):20] = run
    longs = np.flatnonzero(kinds[:limit] == 2)
    not_long = np.flatnonzero(kinds != 2)
    mediums = np.concatenate(([0], np.cumsum(kinds == 1)))   # mediums before each pulse

    records, stray, index = [], 0, 0
    while index < limit:
        # Up to the next long pulse, every medium one is stray
        found = np.searchsorted(longs, index)
        nxt = int(longs[found]) if found < len(longs) else limit
        stray += int(mediums[nxt] - mediums[index])
        if stray > 400:
            return records, 401
        index = nxt
        if index >= limit:
            break
        following = kinds[index + 1]
        if following == 2:
            # A run of long pulses, stepped through two at a time: go straight
            # to the last place in it that is landed on
            found = np.searchsorted(not_long, index)
            end = int(not_long[found]) if found < len(not_long) else count
            index += 2 * ((end - 1 - index) // 2)
            if index >= limit:
                break
            following = kinds[index + 1]
            if following != 1:
                index += 2
                continue
        elif following != 1:
            index += 2                  # end-of-data marker
            continue
        length = int(chain[index])
        if length == 0:
            index += 2
            stray += 1
        else:
            records.append(value[index:index + 20 * length:20].tolist())
            index += 20 * length
        if stray > 400:
            break
    return records, stray


def good_record(payload):
    """The bytes of a KERNAL record whose checksum is right, else None."""
    if len(payload) < 11 or payload[0] not in (0x89, 0x09):
        return None
    checksum = 0
    for byte in payload[9:-1]:
        checksum ^= byte
    return bytes(payload[9:-1]) if checksum == payload[-1] else None


def read_kernal_program(tape, clock, half_waves=False, machine='c64'):
    """((name, address, program, header type), None) if a TAP holds one KERNAL
    program, else (None, reason). One good copy of each record is enough, as it
    is for the KERNAL, and an end-of-tape marker after it is allowed."""
    if half_waves:
        # Half waves are paired into whole cycles; try both pairings, as the
        # file may start on either half
        best = (None, 'own loader')
        for offset in (0, 1):
            found = read_kernal_pulses(whole_cycles(tape.pulses[offset:]), clock, machine)
            if found[0] is not None:
                return found
            if best[1] == 'own loader':
                best = found
        return best
    return read_kernal_pulses(tape.pulses, clock, machine)


def read_kernal_parts(tape, clock, half_waves=False, machine='c64'):
    """([(name, address, program, header type), ...], None) for every KERNAL program
    on a TAP, in order, or (None, reason)."""
    if half_waves:
        best = (None, 'own loader')
        for offset in (0, 1):
            found = kernal_files(whole_cycles(tape.pulses[offset:]), clock, machine)
            if found[0] is not None:
                return found
            if best[1] == 'own loader':
                best = found
        return best
    return kernal_files(tape.pulses, clock, machine)


def c16_cuts(units):
    """Cut-offs for a C16 tape from its own leader. Its pulses are 1:2:4, and real
    tapes run anywhere from $26 to $40 for a short one, too wide a spread for fixed
    cut-offs to sort the long ones from the medium."""
    candidates = units[(units > 0x18) & (units < 0x60)]
    if len(candidates) < 1000:
        return KERNAL_CUTS['c16']
    counts = np.bincount(np.round(candidates).astype(np.int64))
    short = float(np.argmax(counts))        # the leader is by far the commonest pulse
    return (short * 1.5, short * 3.0)


def leader_cut_records(units):
    """KERNAL records read with cut-offs taken from the lead-in tone before each
    one, for tapes whose later parts were recorded a little faster or slower than
    the first (so fixed cut-offs misread them). Short pulses run about 1 : 1.375 :
    1.79 to medium and long ones."""
    tone = ((units > 30) & (units < 54)).view(np.int8)
    edges = np.flatnonzero(np.diff(np.concatenate(([0], tone, [0]))))
    begins, ends = edges[0::2], edges[1::2]
    keep = (ends - begins) >= 300
    runs = list(zip(begins[keep].tolist(), ends[keep].tolist()))
    records, stray = [], 0
    for index, (begin, finish) in enumerate(runs):
        short = float(np.median(units[begin:finish]))
        stop = runs[index + 1][0] if index + 1 < len(runs) else len(units)
        found, extra = kernal_records(units[begin:stop], (short * 1.19, short * 1.6))
        records += found
        stray += extra
    return records, stray


_kernal_files_cache = {}


def kernal_files(pulses, clock, machine):
    """([(name, address, program, header type), ...], None): every KERNAL program on
    the tape in order, each with a good copy of its data; or (None, reason).
    The same tape is read many times by a check (once for each speed), so the
    answer is kept for the rest of the run."""
    key = (hashlib.blake2b(np.ascontiguousarray(pulses).tobytes(), digest_size=16).digest(), clock, machine)
    if key not in _kernal_files_cache:
        if len(_kernal_files_cache) > 64:
            _kernal_files_cache.clear()
        _kernal_files_cache[key] = read_kernal_files(pulses, clock, machine)
    found, reason = _kernal_files_cache[key]
    return (list(found) if found is not None else None), reason


def read_kernal_files(pulses, clock, machine):
    units = pulses * clock / 8.0
    cuts = c16_cuts(units) if machine == 'c16' else KERNAL_CUTS[machine]
    records, stray = kernal_records(units, cuts)
    if machine != 'c16':
        others, other_stray = leader_cut_records(units)
        count = lambda found: sum(good_record(payload) is not None for payload in found)
        if count(others) > count(records):
            records, stray = others, other_stray
    if stray > 400:
        return None, 'own loader'
    header_size = 191 if machine == 'c16' else 192
    files, unmatched, last_header = [], 0, None
    for payload in records:
        body = good_record(payload)
        if body is None:
            continue
        if len(body) == header_size and body[0] in (1, 3, 4, 5):
            if body[0] == 5:            # end of tape
                continue
            if body[0] == 4:            # a data file, not a program
                return None, 'own loader'
            if body != last_header:
                last_header = body
                address = u16(body, 1)
                files.append({'name': body[5:21], 'address': address, 'type': body[0],
                              'length': (u16(body, 3) - address) & 0xFFFF, 'data': None})
        elif files and len(body) == files[-1]['length']:
            if files[-1]['data'] is None:
                files[-1]['data'] = body
        else:
            unmatched += 1
    if not files:
        return None, 'own loader'
    if unmatched > len(files):
        return None, 'own loader'      # more on the tape than its programs
    if any(found['data'] is None for found in files):
        return None, 'own loader' if len(files) == 1 else 'a part could not be read'
    return [(found['name'], found['address'], found['data'], found['type']) for found in files], None


def read_kernal_pulses(pulses, clock, machine):
    files, reason = kernal_files(pulses, clock, machine)
    if files is None:
        return None, reason
    if len(files) > 1:
        return None, 'several programs'
    return files[0], None


# Silence and leader tone before each later part, so the first part has time to
# load and be RUN before it asks for the next one (a real deck stops the motor).
LATER_PART_WAIT_SECONDS = 12.0


def kernal_header_leads(tape, clock):
    """(leader start, quiet start, header start) for each KERNAL file header on a
    tape of whole-cycle pulses, in order. quiet start is where any silence before
    the leader begins."""
    if tape.pulses is None or len(tape.pulses) < 8000 or tape.levels is not None:
        return []
    units = tape.pulses * clock / 8.0
    kinds = np.where(units < 0x39, 0, np.where(units < 0x4C, 1, 2)).astype(np.int8)
    marks = np.flatnonzero((kinds[:-21] == 2) & (kinds[1:-20] == 1))
    if len(marks) == 0:
        return []
    cells = kinds[marks[:, None] + np.arange(2, 20)]
    firsts = cells[:, 0::2]
    good = np.all((cells[:, 0::2] + cells[:, 1::2]) == 1, axis=1) & np.all(firsts <= 1, axis=1)
    good &= (firsts.sum(axis=1) % 2) == 1
    values = (firsts[:, :8].astype(np.int32) << np.arange(8)).sum(axis=1)
    # A header's first copy starts $89 $88 ... $81 then type 1 or 3
    starts = []
    for at in np.flatnonzero(good & (values == 0x89)):
        run = marks[at:at + 10]
        if len(run) == 10 and np.all(np.diff(run) == 20) and np.all(good[at:at + 10]) \
                and list(values[at:at + 9]) == list(range(0x89, 0x80, -1)) and values[at + 9] in (1, 3):
            starts.append(int(marks[at]))
    tone = (units > 0x28) & (units < 0x39)
    quiet = units > 0x100                                  # silence already there
    not_tone = np.flatnonzero(~tone)
    not_quiet = np.flatnonzero(~quiet)
    found = []
    for start in starts:
        # Back to the start of the leader tone, then of any silence before it
        at = np.searchsorted(not_tone, start) - 1
        lead = int(not_tone[at]) + 1 if at >= 0 and start > 0 else 0
        if lead > start:
            lead = start
        at = np.searchsorted(not_quiet, lead) - 1
        before = int(not_quiet[at]) + 1 if at >= 0 and lead > 0 else 0
        if before > lead:
            before = lead
        found.append((lead, before, start))
    return found


def give_later_parts_time(tape, clock):
    """Lengthen short gaps before the second and later KERNAL file headers."""
    inserts = []
    for lead, before, start in kernal_header_leads(tape, clock)[1:]:
        waited = float(tape.pulses[before:start].sum())
        if lead == start or waited >= LATER_PART_WAIT_SECONDS:
            continue
        inserts.append((before, LATER_PART_WAIT_SECONDS - waited))
    if inserts:
        at = [place for place, _ in inserts]
        tape.pulses = np.insert(tape.pulses, at, [seconds for _, seconds in inserts])


def kernal_countdowns(tape, clock):
    """(leader start, countdown start, is a header) for the first copy of every
    KERNAL record on a tape of whole-cycle pulses."""
    if tape.pulses is None or len(tape.pulses) < 8000 or tape.levels is not None:
        return []
    units = tape.pulses * clock / 8.0
    kinds = np.where(units < 0x39, 0, np.where(units < 0x4C, 1, 2)).astype(np.int8)
    marks = np.flatnonzero((kinds[:-21] == 2) & (kinds[1:-20] == 1))
    if len(marks) == 0:
        return []
    cells = kinds[marks[:, None] + np.arange(2, 20)]
    firsts = cells[:, 0::2]
    good = np.all((cells[:, 0::2] + cells[:, 1::2]) == 1, axis=1) & np.all(firsts <= 1, axis=1)
    good &= (firsts.sum(axis=1) % 2) == 1
    values = (firsts[:, :8].astype(np.int32) << np.arange(8)).sum(axis=1)
    tone = (units > 0x28) & (units < 0x39)
    not_tone = np.flatnonzero(~tone)
    found = []
    for at in np.flatnonzero(good & (values == 0x89)):
        run = marks[at:at + 10]
        if len(run) < 10 or not np.all(np.diff(run) == 20) or not np.all(good[at:at + 10]) \
                or list(values[at:at + 9]) != list(range(0x89, 0x80, -1)):
            continue
        start = int(marks[at])
        index = np.searchsorted(not_tone, start) - 1
        lead = int(not_tone[index]) + 1 if index >= 0 else 0
        found.append((min(lead, start), start, int(values[at + 9]) in (1, 3)))
    return found


def shorten_leaders(tape, clock, machine, later_parts):
    """Short Leader for an original tape played at normal speed: cuts the leader
    before the first header, before every VIC-20 data record (the VIC carries
    straight on after FOUND), and before later headers when the tape stops there
    anyway (later_parts). Own-loader parts are not touched."""
    if LEADER_SCALE >= 1.0 or machine not in ('c64', 'vic20'):
        return
    keep = np.ones(len(tape.pulses), bool)
    floor = LEADER_FLOOR_SECONDS
    headers_seen = 0
    changed = False
    for lead, start, is_header in kernal_countdowns(tape, clock):
        if is_header:
            headers_seen += 1
            if headers_seen > 1 and not later_parts:
                continue
        elif machine != 'vic20':
            continue
        length = float(tape.pulses[lead:start].sum())
        wanted = max(length * LEADER_SCALE, min(floor, length))
        if wanted >= length:
            continue
        # Keep the end of the leader, next to the data
        times = np.cumsum(tape.pulses[lead:start][::-1])
        kept = int(np.searchsorted(times, wanted)) + 1
        keep[lead:start - kept] = False
        changed = True
    if not changed:
        return
    tape.pulses = tape.pulses[keep]
    tape.counter_seconds = None
    tape.starts = find_block_starts(tape.pulses, tape.levels)


# --- Stopping the tape between parts -----------------------------------------
# Where the original tape was stopped, a recording has a long silence: the motor
# was off. Playback stops there, as a Datasette would, until Play is pressed when
# the game asks for it. The silence is cut to a second, so the tape is stopped by
# the time the game is ready. Later standard (KERNAL) parts stop the same way,
# silence or not.
STOP_SILENCE_SECONDS = 2.0     # a silence this long is where the tape was stopped
STOP_KEEP_SECONDS = 1.0        # what is left of it before the tape stops
STOP_STRAY_PULSES = 4          # pulses inside a silence that do not end it
STOP_LEAD_IN_SECONDS = 1.0     # data before a silence for it to count, so the
                               # quiet at the very start of a tape is not a stop


def silence_runs(durations, levels=None):
    """(first pulse, end pulse) of each silence long enough to be a stop."""
    silent = durations > SILENT_PULSE
    if levels is not None:
        silent |= levels == 0
    # A stray pulse or two (a click, or a cycle drawn at the start of a long
    # pause) does not end a silence
    hole_begin, hole_end = runs_of(~silent)
    for first, last in zip(hole_begin, hole_end):
        if 0 < first and last < len(silent) and last - first <= STOP_STRAY_PULSES:
            silent[first:last] = True
    begin, end = runs_of(silent)
    if len(begin) == 0:
        return []
    cumulative = np.concatenate(([0.0], np.cumsum(durations)))
    total = cumulative[-1]
    runs = []
    for first, last in zip(begin, end):
        quiet = cumulative[last] - cumulative[first]
        if quiet < STOP_SILENCE_SECONDS or last >= len(durations) - MIN_BLOCK_PULSES:
            continue
        # Some sound before it (not just noise), and some after
        if cumulative[first] < STOP_LEAD_IN_SECONDS or total - cumulative[last] < STOP_LEAD_IN_SECONDS:
            continue
        if first < MIN_BLOCK_PULSES:
            continue
        runs.append((int(first), int(last)))
    return runs


def shorten_silence(durations, first, last, keep):
    """Pulses to drop from a silence run so about keep seconds is left, and the
    new length of the pulse where the cut falls. An even number is dropped, so
    the high and low halves still alternate."""
    total = 0.0
    at = first
    while at < last and total + durations[at] < keep:
        total += durations[at]
        at += 1
    if at >= last:
        return None
    cut_length = max(keep - total, 0.05)
    drop_from = at + 1
    if (last - drop_from) % 2:
        drop_from += 1
    if drop_from > last:
        drop_from = last
    return at, cut_length, drop_from


def add_tape_stops(tape, clock=None):
    """Stops playback where the original tape was stopped. Changes the tape."""
    if tape.pause_before:
        return                                   # built with its own stops
    if tape.pcm is not None:
        add_recording_stops(tape)
        return
    if tape.pulses is None or len(tape.pulses) < 2 * MIN_BLOCK_PULSES:
        return
    durations = tape.pulses
    levels = tape.levels

    # Later standard parts, found by their headers
    points = set()
    if clock is not None:
        for lead, before, start in kernal_header_leads(tape, clock)[1:]:
            if lead < start:
                points.add(before)

    # Long silences, cut to a second; points move as pulses are dropped
    keep_mask = np.ones(len(durations), bool)
    new_lengths = {}
    silence_points = set()
    for first, last in silence_runs(durations, levels):
        cut = shorten_silence(durations, first, last, STOP_KEEP_SECONDS)
        if cut is not None:
            at, length, drop_from = cut
            new_lengths[at] = length
            keep_mask[drop_from:last] = False
        silence_points.add(last)
        # A header just after this silence stops here, not twice
        points = {p for p in points if not (first <= p <= last + 50)}
    points |= silence_points
    if not points:
        return

    pulses = durations.copy()
    for at, length in new_lengths.items():
        pulses[at] = length
    new_index = np.cumsum(keep_mask) - 1          # old pulse -> new pulse
    remapped = set()
    for point in points:
        while point < len(keep_mask) and not keep_mask[point]:
            point += 1
        if point < len(keep_mask):
            remapped.add(int(new_index[point]))
    tape.pulses = pulses[keep_mask]
    if levels is not None:
        tape.levels = levels[keep_mask]
    if tape.counter_seconds is not None:
        tape.counter_seconds = None

    starts = find_block_starts(tape.pulses, tape.levels)
    pause_before = set()
    for point in sorted(remapped):
        if point < MIN_BLOCK_PULSES or len(tape.pulses) - point < MIN_BLOCK_PULSES:
            continue
        near = [s for s in starts if abs(s - point) <= 64]
        if near:
            point = near[0]
        else:
            starts.append(point)
            starts.sort()
        pause_before.add(starts.index(point))
    tape.starts = starts
    tape.pause_before = {block for block in pause_before if block > 0}


def add_recording_stops(tape):
    """add_tape_stops for a WAV, MP3 or other recording."""
    rate = tape.pcm_rate
    falls, _ = falling_edges(tape.pcm)
    if len(falls) < 3:
        return
    keep_samples = int(STOP_KEEP_SECONDS * rate)
    margin = int(0.02 * rate)
    pieces, stops, taken, at = [], [], 0, 0
    for first, last in silence_runs(np.diff(falls) / rate):
        quiet_from, quiet_to = int(falls[first]), int(falls[last])
        cut_from = quiet_from + keep_samples
        cut_to = quiet_to - margin
        if cut_to <= cut_from or cut_from < at:
            continue
        pieces.append(tape.pcm[at:cut_from])
        stops.append(cut_from - taken)            # where the data now starts
        taken += cut_to - cut_from
        at = cut_to
    if not stops:
        return
    pieces.append(tape.pcm[at:])
    tape.pcm = np.concatenate(pieces)
    falls, _ = falling_edges(tape.pcm)
    starts = [0]
    if len(falls) > 2:
        cuts = find_block_starts(np.diff(falls) / rate)
        for cut in cuts[1:]:
            position = max(int(falls[cut]) - margin, 0)
            if position > starts[-1]:
                starts.append(position)
    pause_before = set()
    for stop in stops:
        near = [s for s in starts if abs(s - stop) <= 2 * margin]
        if near:
            stop = near[0]
        else:
            starts.append(stop)
            starts.sort()
        pause_before.add(starts.index(stop))
    tape.starts = starts
    tape.pause_before = {block for block in pause_before if block > 0}


# --- CSW -------------------------------------------------------------------
def decode_csw_runs(payload, compression):
    if compression == 2:
        try:
            payload = zlib.decompress(payload)
        except zlib.error as error:
            raise TapeError(f'CSW data is damaged: {error}')
    elif compression != 1:
        raise TapeError(f'CSW compression type {compression} is not supported')
    values, _ = expand_overflow(payload, 4)
    return values


def parse_csw(data, path, options):
    major = data[0x17]
    if major == 1:
        rate = u16(data, 0x19)
        compression, flags = data[0x1B], data[0x1C]
        body = 0x20
    elif major == 2:
        rate = u32(data, 0x19)
        compression, flags = data[0x21], data[0x22]
        body = 0x34 + data[0x23]
    else:
        raise TapeError(f'CSW version {major} is not supported')
    if rate == 0:
        raise TapeError('CSW file has no sample rate')
    runs = decode_csw_runs(bytes(data[body:]), compression)
    if len(runs) == 0:
        raise TapeError('CSW file holds no pulses')
    tape = Tape(f'CSW v{major}')
    tape.pulses = runs / rate
    tape.levels = alternating(len(runs), 1 if flags & 1 else -1)
    tape.starts = find_block_starts(tape.pulses, tape.levels)
    return tape


# --- TZX -------------------------------------------------------------------
class TzxStream:
    """Collects half-wave pulses with the signal level each one is played at."""

    def __init__(self):
        self.durations, self.levels_out = [], []
        self.level = -1

    def pulses(self, tstates):
        lengths = np.asarray(tstates, np.float64)
        if len(lengths) == 0:
            return
        self.durations.append(lengths / TZX_CLOCK)
        levels = alternating(len(lengths), -self.level)
        self.levels_out.append(levels)
        self.level = int(levels[-1])

    def raw(self, seconds, levels):
        if len(seconds):
            self.durations.append(seconds)
            self.levels_out.append(levels)
            last = int(levels[-1])
            if last:
                self.level = last

    def forced(self, tstates, level):
        """One pulse at an explicit level, for generalised data symbols."""
        self.raw(np.array([tstates / TZX_CLOCK]), np.array([level], np.int8))
        self.level = level

    def pause(self, milliseconds):
        if milliseconds <= 0:
            return
        self.raw(np.array([0.001]), np.array([-self.level], np.int8))
        self.raw(np.array([milliseconds / 1000.0]), np.array([0], np.int8))
        self.level = -1

    def data_bits(self, data, zero, one, used):
        if len(data) == 0:
            return
        bits = np.unpackbits(np.frombuffer(bytes(data), np.uint8))
        if 0 < used < 8:
            bits = bits[:-(8 - used)]
        lengths = np.where(bits == 1, one, zero).astype(np.float64)
        self.pulses(np.repeat(lengths, 2))


def tzx_block_size(block, data, at):
    """Bytes in the block body at `at`, excluding the id byte."""
    fixed = {0x12: 4, 0x20: 2, 0x22: 0, 0x23: 2, 0x24: 2, 0x25: 0, 0x27: 0,
             0x2A: 4, 0x2B: 5, 0x34: 8, 0x5A: 9}
    if block in fixed:
        return fixed[block]
    if block == 0x10:
        return 4 + u16(data, at + 2)
    if block == 0x11:
        return 0x12 + u24(data, at + 0x0F)
    if block == 0x13:
        return 1 + 2 * data[at]
    if block == 0x14:
        return 0x0A + u24(data, at + 7)
    if block == 0x15:
        return 8 + u24(data, at + 5)
    if block in (0x16, 0x17):
        return u32(data, at)
    if block == 0x21 or block == 0x30:
        return 1 + data[at]
    if block == 0x26:
        return 2 + 2 * u16(data, at)
    if block == 0x28 or block == 0x32:
        return 2 + u16(data, at)
    if block == 0x31:
        return 2 + data[at + 1]
    if block == 0x33:
        return 1 + 3 * data[at]
    if block == 0x35:
        return 0x14 + u32(data, at + 0x10)
    if block == 0x40:
        return 4 + u24(data, at + 1)
    return 4 + u32(data, at)             # 18, 19 and unknown ids per the spec


def tzx_general(stream, data, at):
    pause = u16(data, at + 4)
    total_pilot, npp, asp = u32(data, at + 6), data[at + 10], data[at + 11] or 256
    total_data, npd, asd = u32(data, at + 12), data[at + 16], data[at + 17] or 256
    cursor = at + 18

    def read_symbols(alphabet, width):
        nonlocal cursor
        table = []
        for _ in range(alphabet):
            flag = data[cursor] & 3
            lengths = struct.unpack_from(f'<{width}H', data, cursor + 1)
            cursor += 1 + 2 * width
            table.append((flag, [v for v in lengths if v] if 0 not in lengths else
                          list(lengths[:lengths.index(0)])))
        return table

    def play(symbol):
        flag, lengths = symbol
        for index, length in enumerate(lengths):
            if index == 0:
                level = {0: -stream.level, 1: stream.level, 2: -1, 3: 1}[flag]
            else:
                level = -stream.level
            stream.forced(length, level)

    if total_pilot:
        table = read_symbols(asp, npp)
        for _ in range(total_pilot):
            symbol, repeats = data[cursor], u16(data, cursor + 1)
            cursor += 3
            for _ in range(repeats):
                play(table[symbol])
    if total_data:
        table = read_symbols(asd, npd)
        width = 0
        while (1 << width) < asd:
            width += 1
        if width:
            needed = (width * total_data + 7) // 8
            bits = np.unpackbits(np.frombuffer(bytes(data[cursor:cursor + needed]), np.uint8))
            for index in range(total_data):
                value = 0
                for bit in bits[index * width:(index + 1) * width]:
                    value = value << 1 | int(bit)
                play(table[value])
        else:
            for _ in range(total_data):
                play(table[0])
    stream.pause(pause)


def tzx_c64_rom(stream, data, at):
    """Block 0x16. Every wave is two pulses and starts low."""
    (pilot, pilot_waves, sync1, sync2, zero1, zero2, one1, one2) = struct.unpack_from('<8H', data, at + 4)
    xor_mode = data[at + 0x14]
    finish_byte = struct.unpack_from('<2H', data, at + 0x15)
    finish_data = struct.unpack_from('<2H', data, at + 0x19)
    trail, trail_waves = struct.unpack_from('<2H', data, at + 0x1D)
    used, flags = data[at + 0x21], data[at + 0x22]
    pause = u16(data, at + 0x23)
    size = u24(data, at + 0x25)
    payload = bytes(data[at + 0x28:at + 0x28 + size])
    pulses = [pilot] * (pilot_waves * 2) if pilot else []

    def wave(length, into):
        if length:
            into.extend((length, length))

    wave(sync1, pulses)
    wave(sync2, pulses)
    for index, byte in enumerate(payload):
        last = index == len(payload) - 1
        count = used if last and 0 < used < 8 else 8
        order = range(7, 7 - count, -1) if flags & 1 else range(count)
        checksum = xor_mode if xor_mode != 0xFF else 0
        for bit in order:
            value = (byte >> bit) & 1
            checksum ^= value
            wave(one1 if value else zero1, pulses)
            wave(one2 if value else zero2, pulses)
        if xor_mode != 0xFF:
            wave(one1 if checksum else zero1, pulses)
            wave(one2 if checksum else zero2, pulses)
        if not last:
            wave(finish_byte[0], pulses)
            wave(finish_byte[1], pulses)
    wave(finish_data[0], pulses)
    wave(finish_data[1], pulses)
    if trail:
        pulses.extend([trail] * (trail_waves * 2))
    stream.level = 1
    stream.pulses(pulses)
    stream.pause(pause)


def tzx_c64_turbo(stream, data, at):
    """Block 0x17. Each bit is one wave of two pulses."""
    zero, one = u16(data, at + 4), u16(data, at + 6)
    extra = data[at + 8]
    lead_count, lead_byte = u16(data, at + 9), data[at + 0x0B]
    used, flags = data[at + 0x0C], data[at + 0x0D]
    trail_count, trail_byte = u16(data, at + 0x0E), data[at + 0x10]
    pause = u16(data, at + 0x11)
    size = u24(data, at + 0x13)
    payload = bytes(data[at + 0x16:at + 0x16 + size])
    extra_bits = [(extra >> 3) & 1] * (extra & 3)
    extra_after = bool(extra & 4)
    msb_first = bool(flags & 1)

    def bits_of(byte, count=8):
        order = range(7, 7 - count, -1) if msb_first else range(count)
        return [(byte >> bit) & 1 for bit in order]

    bits = []
    for _ in range(lead_count):
        bits += bits_of(lead_byte)
    for index, byte in enumerate(payload):
        count = used if index == len(payload) - 1 and 0 < used < 8 else 8
        if not extra_after:
            bits += extra_bits
        bits += bits_of(byte, count)
        if extra_after:
            bits += extra_bits
    for _ in range(trail_count):
        bits += bits_of(trail_byte)
    stream.pulses([one if b else zero for b in bits for _ in range(2)])
    stream.pause(pause)


def tzx_direct(stream, data, at):
    tstates = u16(data, at)
    pause = u16(data, at + 2)
    used = data[at + 4]
    size = u24(data, at + 5)
    bits = np.unpackbits(np.frombuffer(bytes(data[at + 8:at + 8 + size]), np.uint8))
    if 0 < used < 8 and len(bits) >= 8:
        bits = bits[:-(8 - used)]
    if len(bits):
        change = np.flatnonzero(np.diff(bits)) + 1
        edges = np.concatenate(([0], change, [len(bits)]))
        runs = np.diff(edges).astype(np.float64) * tstates / TZX_CLOCK
        levels = np.where(bits[edges[:-1]] == 1, 1, -1).astype(np.int8)
        stream.raw(runs, levels)
    stream.pause(pause)


def tzx_csw(stream, data, at):
    size = u32(data, at)
    pause = u16(data, at + 4)
    rate = u24(data, at + 6)
    compression = data[at + 9]
    runs = decode_csw_runs(bytes(data[at + 14:at + 4 + size]), compression)
    if len(runs) and rate:
        stream.raw(runs / rate, alternating(len(runs), -stream.level))
    stream.pause(pause)


def parse_tzx(data, path, options):
    blocks = []
    at = 10
    while at < len(data):
        block = data[at]
        try:
            size = tzx_block_size(block, data, at + 1)
        except (IndexError, struct.error):
            break
        if at + 1 + size > len(data):
            break
        blocks.append((block, at + 1, size))
        at += 1 + size
    if not blocks:
        raise TapeError('TZX file holds no blocks')

    stream = TzxStream()
    index, steps, loop_start, loop_left, calls, stops = 0, 0, 0, 0, [], 0
    while 0 <= index < len(blocks) and steps < 2000000:
        steps += 1
        block, body, size = blocks[index]
        following = index + 1
        if block == 0x10:
            pause, length = u16(data, body), u16(data, body + 2)
            payload = bytes(data[body + 4:body + 4 + length])
            count = 8063 if payload and payload[0] < 128 else 3223
            stream.pulses(np.full(count, 2168.0))
            stream.pulses([667, 735])
            stream.data_bits(payload, 855, 1710, 8)
            stream.pause(pause)
        elif block == 0x11:
            (pilot, sync1, sync2, zero, one, count) = struct.unpack_from('<6H', data, body)
            used, pause = data[body + 0x0C], u16(data, body + 0x0D)
            length = u24(data, body + 0x0F)
            stream.pulses(np.full(count, float(pilot)))
            stream.pulses([sync1, sync2])
            stream.data_bits(data[body + 0x12:body + 0x12 + length], zero, one, used)
            stream.pause(pause)
        elif block == 0x12:
            stream.pulses(np.full(u16(data, body + 2), float(u16(data, body))))
        elif block == 0x13:
            stream.pulses(struct.unpack_from(f'<{data[body]}H', data, body + 1))
        elif block == 0x14:
            zero, one, used = u16(data, body), u16(data, body + 2), data[body + 4]
            pause, length = u16(data, body + 5), u24(data, body + 7)
            stream.data_bits(data[body + 0x0A:body + 0x0A + length], zero, one, used)
            stream.pause(pause)
        elif block == 0x15:
            tzx_direct(stream, data, body)
        elif block == 0x16:
            tzx_c64_rom(stream, data, body)
        elif block == 0x17:
            tzx_c64_turbo(stream, data, body)
        elif block == 0x18:
            tzx_csw(stream, data, body)
        elif block == 0x19:
            tzx_general(stream, data, body)
        elif block == 0x20:
            pause = u16(data, body)
            if pause == 0:          # "stop the tape": leave a gap the user can act in
                stops += 1
                pause = 2000
            stream.pause(pause)
        elif block == 0x23:
            following = index + struct.unpack_from('<h', data, body)[0]
        elif block == 0x24:
            loop_start, loop_left = index + 1, u16(data, body)
        elif block == 0x25:
            loop_left -= 1
            if loop_left > 0:
                following = loop_start
        elif block == 0x26:
            offsets = struct.unpack_from(f'<{u16(data, body)}h', data, body + 2)
            if offsets:
                calls.append((index, offsets, 1))
                following = index + offsets[0]
        elif block == 0x27:
            if calls:
                origin, offsets, position = calls.pop()
                if position < len(offsets):
                    calls.append((origin, offsets, position + 1))
                    following = origin + offsets[position]
                else:
                    following = origin + 1
        elif block == 0x28:
            if data[body + 2]:
                following = index + struct.unpack_from('<h', data, body + 3)[0]
        elif block == 0x2B:
            stream.level = 1 if data[body + 4] else -1
        index = following

    if not stream.durations:
        raise TapeError('TZX file holds no tape signal')
    if stops:
        print(f'Note: {stops} stop-the-tape marker(s) played as 2 second gaps', flush=True)
    tape = Tape(f'TZX v{data[8]}.{data[9]:02d}')
    tape.pulses = np.concatenate(stream.durations)
    tape.levels = np.concatenate(stream.levels_out)
    tape.starts = find_block_starts(tape.pulses, tape.levels)
    return tape


# --- Audio files -----------------------------------------------------------
def to_mono_int16(samples, channels):
    """Pick the strongest channel. Mixing can cancel out-of-phase recordings."""
    if channels > 1:
        frames = samples.reshape(-1, channels)
        peak = [float(np.abs(frames[:2000000, c].astype(np.float64)).mean()) for c in range(channels)]
        samples = frames[:, int(np.argmax(peak))]
    return samples


def parse_wav_bytes(data):
    if data[:4] != b'RIFF' or data[8:12] != b'WAVE':
        raise TapeError('Not a valid WAV file')
    at, fmt, payload = 12, None, None
    while at + 8 <= len(data):
        chunk, size = bytes(data[at:at + 4]), u32(data, at + 4)
        begin = at + 8
        if chunk == b'fmt ':
            fmt = bytes(data[begin:begin + size])
        elif chunk == b'data':
            payload = (begin, min(size, len(data) - begin))
            if fmt is not None:
                break
        at = begin + size + (size & 1)
    if fmt is None or payload is None:
        raise TapeError('WAV file is missing its format or data section')

    tag, channels, rate = struct.unpack_from('<HHI', fmt)
    bits = struct.unpack_from('<H', fmt, 14)[0]
    if tag == 0xFFFE and len(fmt) >= 26:
        tag = struct.unpack_from('<H', fmt, 24)[0]
    begin, size = payload
    raw = bytes(data[begin:begin + size])
    if tag == 1 and bits == 8:
        samples = (np.frombuffer(raw, np.uint8).astype(np.int16) - 128) << 8
    elif tag == 1 and bits == 16:
        samples = np.frombuffer(raw[:len(raw) & ~1], '<i2')
    elif tag == 1 and bits in (24, 32):
        width = bits // 8
        frame = np.frombuffer(raw[:len(raw) - len(raw) % width], np.uint8).reshape(-1, width)
        samples = (frame[:, width - 1].astype(np.int8).astype(np.int16) << 8) | frame[:, width - 2]
    elif tag == 3 and bits in (32, 64):
        floats = np.frombuffer(raw[:len(raw) - len(raw) % (bits // 8)], f'<f{bits // 8}')
        samples = (np.clip(floats, -1.0, 1.0) * 32767).astype(np.int16)
    else:
        raise TapeError(f'WAV encoding {tag} at {bits} bits is not supported')
    return to_mono_int16(samples, channels), rate


def decode_with_miniaudio(path):
    import miniaudio
    decoded = miniaudio.decode_file(path, output_format=miniaudio.SampleFormat.SIGNED16)
    samples = np.frombuffer(decoded.samples, np.int16)
    return to_mono_int16(samples, decoded.nchannels), decoded.sample_rate


def decode_with_soundfile(path):
    import soundfile
    samples, rate = soundfile.read(path, dtype='int16', always_2d=True)
    return to_mono_int16(samples.ravel(), samples.shape[1]), rate


def decode_with_ffmpeg(path):
    local = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ffmpeg.exe')
    program = local if os.path.exists(local) else shutil.which('ffmpeg')
    if not program:
        raise FileNotFoundError('ffmpeg')
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    result = subprocess.run([program, '-v', 'error', '-i', path, '-vn', '-c:a', 'pcm_s16le',
                             '-f', 'wav', '-'], capture_output=True, creationflags=flags)
    if result.returncode != 0 or len(result.stdout) < 44:
        raise TapeError('ffmpeg could not read this audio file')
    return parse_wav_bytes(result.stdout)


def decode_compressed_audio(data, path):
    suffix = os.path.splitext(path)[1] or '.mp3'
    handle, temporary = tempfile.mkstemp(suffix=suffix)
    try:
        with os.fdopen(handle, 'wb') as output:
            output.write(data)
        for decoder in (decode_with_miniaudio, decode_with_soundfile, decode_with_ffmpeg):
            try:
                return decoder(temporary)
            except (ImportError, FileNotFoundError):
                continue
            except TapeError:
                raise
            except Exception:
                continue
    finally:
        try:
            os.remove(temporary)
        except OSError:
            pass
    raise TapeError('No audio decoder for this file. Run "pip install miniaudio" '
                    'for the bundled Python, or put ffmpeg.exe next to cbmplay.py')


def falling_edges(samples):
    """Falling edges with hysteresis, so low-level noise does not count."""
    peak = float(np.percentile(np.abs(samples[::7].astype(np.float64)), 99.9)) if len(samples) else 0.0
    if peak < 16:
        return np.zeros(0, np.int64), 1.0
    threshold = peak * 0.1
    state = np.zeros(len(samples), np.int8)
    state[samples > threshold] = 1
    state[samples < -threshold] = -1
    known = np.flatnonzero(state)
    if len(known) < 2:
        return np.zeros(0, np.int64), peak
    held = state[known]
    falls = known[1:][(held[1:] == -1) & (held[:-1] == 1)]
    return falls, peak


def build_audio_tape(samples, rate, label):
    if rate <= 0 or len(samples) == 0:
        raise TapeError('Audio file is empty')
    tape = Tape(f'{label} recording, {rate} Hz')
    tape.pcm = samples
    tape.pcm_rate = rate
    falls, peak = falling_edges(samples)
    tape.gain = 1.0 / max(peak, 1.0)
    starts = [0]
    if len(falls) > 2:
        cuts = find_block_starts(np.diff(falls) / rate)
        margin = int(0.02 * rate)
        for cut in cuts[1:]:
            position = max(int(falls[cut]) - margin, 0)
            if position > starts[-1]:
                starts.append(position)
    tape.starts = starts
    return tape


# --- Opening files ---------------------------------------------------------
def unwrap_archives(data, path):
    if data[:2] == b'\x1f\x8b':
        inner = os.path.splitext(path)[0] if path.lower().endswith('.gz') else path
        try:
            return gzip.decompress(data), inner
        except OSError as error:
            raise TapeError(f'Could not unpack gzip file: {error}')
    if data[:4] == b'PK\x03\x04':
        known = IMAGE_EXTENSIONS | AUDIO_EXTENSIONS
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            members = [m for m in archive.namelist()
                       if os.path.splitext(m)[1].lower() in known]
            if not members:
                raise TapeError('Zip file has no tape image in it')
            rank = ['.tap', '.tzx', '.t64', '.csw', '.prg', '.p00']
            members.sort(key=lambda m: (
                rank.index(os.path.splitext(m)[1].lower())
                if os.path.splitext(m)[1].lower() in rank else len(rank), m))
            directory = os.path.dirname(path)
            return archive.read(members[0]), os.path.join(directory, os.path.basename(members[0]))
    return data, path


def load_tape(path, options):
    with open(path, 'rb') as source:
        data = source.read()
    data, name = unwrap_archives(data, path)
    extension = os.path.splitext(name)[1].lower()
    head = bytes(data[:32])

    if head[:12] in (b'C64-TAPE-RAW', b'C16-TAPE-RAW'):
        return parse_tap(data, name, options)
    if head[:7] == b'C64File':
        return parse_p00(data, name, options)
    if head[:3] == b'C64' and extension != '.prg':
        return parse_t64(data, name, options)
    if head[:8] == b'ZXTape!\x1a':
        return parse_tzx(data, name, options)
    if head[:23] == b'Compressed Square Wave\x1a':
        return parse_csw(data, name, options)
    if head[:4] == b'RIFF' and head[8:12] == b'WAVE':
        samples, rate = parse_wav_bytes(data)
        return build_audio_tape(samples, rate, 'WAV')
    if extension in AUDIO_EXTENSIONS:
        samples, rate = decode_compressed_audio(data, name)
        return build_audio_tape(samples, rate, extension[1:].upper())
    if extension == '.prg' or re.fullmatch(r'\.p\d\d', extension):
        return parse_prg(data, name, options)
    if extension == '.tap':
        raise TapeError('This .tap file is not a Commodore tape (it may be a Spectrum one)')
    raise TapeError(f'Unrecognised tape format: {os.path.basename(path)}')


# --- Converting to PRG ------------------------------------------------------
def edge_pulses(samples, rate):
    """Whole-cycle pulse lengths in a recording, timed fall to fall and rise to rise."""
    falls, _ = falling_edges(samples)
    rises, _ = falling_edges(-samples.astype(np.int32))
    return [np.diff(edges) / rate for edges in (falls, rises) if len(edges) > 100]


def extract_program(path, options):
    """(load address, program bytes) of the one KERNAL program on a tape, read
    the same way the KERNAL would. Raises TapeError saying why not otherwise."""
    machine = options.machine if options.machine != 'auto' else (path_hint(path) or 'c64')
    options.turbo = 0
    tape = load_tape(path, options)
    if tape.pcm is not None:
        candidates = [(pulses, False) for pulses in edge_pulses(tape.pcm, tape.pcm_rate)]
        clocks = [CLOCKS[machine][0], CLOCKS[machine].get(1, CLOCKS[machine][0])]
    else:
        candidates = [(tape.pulses, tape.levels is not None)]
        clocks = [CLOCKS[machine].get(options.video, CLOCKS[machine][0])
                  if options.video is not None else CLOCKS[machine][0]]
    reason = 'no program found'
    for pulses, half_waves in candidates:
        for clock in clocks:
            holder = Tape('')
            holder.pulses = pulses
            found, why = read_kernal_program(holder, clock, half_waves, machine)
            if found is not None:
                return found[1], found[2]
            if why != 'own loader' or reason == 'no program found':
                reason = why
    raise TapeError(reason)


def convert_to_prg(path, options):
    """Writes the program on a tape next to it as a .prg. Prints what happened."""
    target = os.path.splitext(path)[0] + '.prg'
    if os.path.splitext(path)[1].lower() == '.prg':
        print('Not converted: already a PRG', flush=True)
        return 1
    if os.path.exists(target):
        print(f'Not converted: {os.path.basename(target)} already exists', flush=True)
        return 1
    try:
        address, program = extract_program(path, options)
    except TapeError as error:
        print(f'Not converted: {error}', flush=True)
        return 1
    with open(target, 'wb') as output:
        output.write(struct.pack('<H', address) + bytes(program))
    print(f'Converted: {target} (${address:04X}, {len(program)} bytes)', flush=True)
    return 0


# --- Rendering -------------------------------------------------------------
def render_block(tape, block, rate, options):
    """Yield float32 sample arrays for one block."""
    amplitude = -options.volume if options.invert else options.volume
    start, stop = tape.block_range(block)
    edge_width = getattr(tape, 'edge_widths', {}).get(block, EDGE_WIDTH)
    # Fast blocks are drawn with every pulse a whole number of samples long. The
    # machine times edge to edge, so this removes the one-sample jitter that
    # otherwise spoils the shortest pulses at 44.1 and 48 kHz.
    snap = tape.pcm is None and block in getattr(tape, 'edge_widths', {})
    if snap:
        low_samples = max(2, int(round(edge_width * rate)))

    if tape.pcm is not None:
        gain = tape.gain * options.volume * (-1.0 if options.invert else 1.0)
        for at in range(start, stop, PCM_STEP):
            piece = tape.pcm[at:min(at + PCM_STEP, stop)].astype(np.float32) * gain
            yield np.clip(piece, -1.0, 1.0).astype(np.float32)
        return

    elapsed, written = 0.0, 0
    for at in range(start, stop, RENDER_PULSES):
        durations = tape.pulses[at:min(at + RENDER_PULSES, stop)]
        if snap:
            counts = np.maximum(np.round(durations * rate), 1).astype(np.int64)
            wave = np.ones(int(counts.sum()), np.float64)
            starts = np.concatenate(([0], np.cumsum(counts)[:-1]))
            for offset in range(low_samples):
                wave[(starts + offset)[counts > max(offset, 1) * 2 - 1]] = -1.0
            yield (wave * amplitude).astype(np.float32)
            continue
        edges = elapsed + np.cumsum(durations)
        elapsed = float(edges[-1])
        last = int(np.ceil(elapsed * rate))
        if last <= written:
            continue
        times = np.arange(written, last) / rate
        written = last
        which = np.minimum(np.searchsorted(edges, times, side='right'), len(durations) - 1)
        length = durations[which]
        into = np.maximum(times - (edges[which] - length), 0.0)

        if tape.levels is None:
            # Whole cycles: the falling edge sits on the pulse boundary, since
            # that edge is the one the machine times.
            cycle = np.where(length > LONG_PULSE, 2 * LONG_TAIL, length)
            phase = into / cycle
            if options.wave == 'sine' and tape.edge_timed:
                # As the square wave: a fixed-width low half, then the rest high, so an
                # adaptor that flips the signal still gives the machine the right timings
                width = np.minimum(edge_width, cycle / 2)
                low = into < width
                wave = np.where(low, -np.sin(np.pi * into / width),
                                np.sin(np.pi * (into - width) / np.maximum(cycle - width, 1e-9)))
            elif options.wave == 'sine':
                wave = -np.sin(2 * np.pi * phase)
            elif tape.edge_timed:
                wave = np.where(into < np.minimum(edge_width, cycle / 2), -1.0, 1.0)
            else:
                wave = np.where(phase < 0.5, -1.0, 1.0)
            wave[phase >= 1.0] = 0.0
        else:
            levels = tape.levels[at:min(at + RENDER_PULSES, stop)][which].astype(np.float64)
            hold = np.where(length > LONG_PULSE, LONG_TAIL, length)
            phase = into / hold
            wave = levels * np.sin(np.pi * phase) if options.wave == 'sine' else levels.copy()
            wave[phase >= 1.0] = 0.0
        yield (wave * amplitude).astype(np.float32)


def export_wav(tape, path, rate, options):
    import wave
    with wave.open(path, 'wb') as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(rate)
        for block in range(tape.total_blocks):
            for piece in render_block(tape, block, rate, options):
                output.writeframes((np.clip(piece, -1, 1) * 32767).astype('<i2').tobytes())


# --- Playback control ------------------------------------------------------
def write_atomic(path, text):
    temporary = path + '.tmp'
    try:
        with open(temporary, 'w') as output:
            output.write(text)
        os.replace(temporary, path)
    except OSError:
        pass


class Status:
    def __init__(self, directory):
        self.current = os.path.join(directory, 'cbm_current_block.txt')
        self.total = os.path.join(directory, 'cbm_total_blocks.txt')
        self.next = os.path.join(directory, 'cbm_next_block.txt')
        self.where = os.path.join(directory, 'cbm_position.txt')
        self.waiting_file = os.path.join(directory, 'cbm_waiting.txt')

    def write(self, current, total):
        write_atomic(self.current, str(current))
        write_atomic(self.total, str(total))
        write_atomic(self.next, str(max(min(current + 1, total - 1), 0)))

    def position(self, counter, block_start_counter, percent):
        write_atomic(self.where, f'{counter} {block_start_counter} {percent}')

    def waiting(self, waiting):
        """cbm_waiting.txt exists while the tape has stopped itself for the next part."""
        try:
            if waiting:
                write_atomic(self.waiting_file, '1')
            elif os.path.exists(self.waiting_file):
                os.remove(self.waiting_file)
        except OSError:
            pass


def read_command(path):
    """Take one command from the control file, or None.

    The GUI may still be holding the file, so an unreadable or empty file is
    left for the next poll and nothing is lost.
    """
    if not os.path.exists(path):
        return None
    try:
        with open(path, 'r') as control:
            command = control.read().strip().lower()
        if not command:
            return None
        os.remove(path)
    except OSError:
        return None
    return command


def open_stream(sounddevice, tape, options):
    if tape.needs_rate:
        rates = [tape.needs_rate]
    else:
        rates = [tape.pcm_rate] if tape.pcm is not None else (
            [options.rate] if options.rate else list(DEFAULT_RATES))
    failure = None
    for rate in rates:
        try:
            stream = sounddevice.OutputStream(samplerate=rate, channels=1, dtype='float32',
                                              blocksize=WRITE_SIZE, latency='high')
            return stream, rate
        except Exception as error:
            failure = error
    if tape.needs_rate:
        raise TapeError('Ultra needs 96 kHz sound output. Use Hyper, or set 96 kHz in Windows sound settings')
    raise TapeError(f'Could not open the sound output: {failure}')


def target_block(command, current, total):
    """Work out which block a transport command wants, or None to ignore it."""
    verb, _, argument = command.partition(':')
    wanted = None
    if argument.strip():
        try:
            wanted = int(argument)
        except ValueError:
            return None
    if verb == 'rewind':
        wanted = current - 1 if wanted is None else wanted
        return max(wanted, 0)
    if verb == 'fastforward':
        wanted = current + 1 if wanted is None else wanted
    elif verb != 'jump' or wanted is None:
        return None
    return wanted if 0 <= wanted < total else None


class TapeCounter:
    """Turns playback position into a 3-digit Datasette-style counter reading."""

    def __init__(self, tape):
        starts = np.array(tape.starts, np.int64)
        if tape.pcm is not None:
            self.times = starts / tape.pcm_rate
            self.length = tape.length / tape.pcm_rate
        else:
            cumulative = np.concatenate(([0.0], np.cumsum(tape.pulses)))
            self.times = cumulative[starts]
            self.length = float(cumulative[-1])
        ends = np.concatenate((self.times, [self.length]))
        self.durations = np.diff(ends)
        self.tape_times = tape.counter_seconds if tape.counter_seconds is not None else ends

    @staticmethod
    def reading(seconds):
        return int(seconds * COUNTS_PER_SECOND) % 1000

    def at(self, block, played):
        begin, end = self.tape_times[block], self.tape_times[block + 1]
        duration = self.durations[block]
        fraction = min(played / duration, 1.0) if duration > 0 else 1.0
        return self.reading(begin + (end - begin) * fraction)

    def percent(self, block, played):
        if self.length <= 0:
            return 100
        return min(int((self.times[block] + played) * 100 / self.length), 100)

    def block_start(self, block):
        return self.reading(self.tape_times[block])


def play_tape(tape, control_file, options):
    import sounddevice
    status = Status(os.path.dirname(os.path.abspath(control_file)))
    total = tape.total_blocks
    stream, rate = open_stream(sounddevice, tape, options)
    stream.start()
    running, paused = True, False

    counter = TapeCounter(tape)
    block, block_written, next_report = 0, 0, 0.0
    status.write(block, total)
    status.position(0, 0, 0)
    pieces = render_block(tape, block, rate, options)
    buffer, position = np.zeros(0, np.float32), 0

    while True:
        command = read_command(control_file)
        if command == 'stop':
            stream.abort()
            running = False
            status.waiting(False)
            break
        if command == 'pause':
            paused = True
        elif command == 'resume':
            paused = False
            status.waiting(False)
        elif command:
            wanted = target_block(command, block, total)
            if wanted is not None:
                if running:
                    stream.abort()
                    running = False
                block, block_written = wanted, 0
                status.write(block, total)
                status.position(counter.block_start(block), counter.block_start(block), counter.percent(block, 0))
                pieces = render_block(tape, block, rate, options)
                buffer, position = np.zeros(0, np.float32), 0

        if paused:
            if running:
                stream.stop()      # lets queued audio finish, so nothing is lost
                running = False
            time.sleep(0.02)
            continue
        if not running:
            stream.start()
            running = True

        if position >= len(buffer):
            try:
                buffer, position = next(pieces), 0
            except StopIteration:
                if block + 1 >= total:
                    break
                block, block_written = block + 1, 0
                status.write(block, total)
                pieces = render_block(tape, block, rate, options)
                buffer, position = np.zeros(0, np.float32), 0
                if block in tape.pause_before:
                    # Stops itself before the next part, as a Datasette's motor
                    # would, until Play is pressed when the game asks for it
                    paused = True
                    status.waiting(True)
            continue

        end = min(position + WRITE_SIZE, len(buffer))
        stream.write(buffer[position:end])
        block_written += end - position
        position = end

        now = time.monotonic()
        if now >= next_report:
            next_report = now + 0.2
            status.position(counter.at(block, block_written / rate), counter.block_start(block),
                            counter.percent(block, block_written / rate))

    if running:
        stream.stop()
    stream.close()
    status.waiting(False)


# --- VIC-20 memory needed ----------------------------------------------------
def leader_pulses(units):
    """How many pulses sit in long runs of KERNAL leader tone."""
    tone = (units > 0x28) & (units < 0x39)
    edges = np.flatnonzero(np.diff(np.concatenate(([0], tone.view(np.int8), [0]))))
    lengths = edges[1::2] - edges[0::2]
    return int(lengths[lengths >= 64].sum())


def kernal_headers(tape, clock, details=None):
    """(load address, end address) of every KERNAL file header on the tape.

    details, if given, gets 'other': the share of the tape that is neither KERNAL
    data nor leader, which is high when a game has its own loader."""
    if tape.pcm is not None or tape.pulses is None or len(tape.pulses) < 4100:
        return []
    pulses = whole_cycles(tape.pulses) if tape.levels is not None else tape.pulses
    units = pulses * clock / 8.0
    kinds = np.where(units < 0x39, 0, np.where(units < 0x4C, 1, 2)).astype(np.int8)
    marks = np.flatnonzero((kinds[:-21] == 2) & (kinds[1:-20] == 1))
    if len(marks) == 0:
        return []
    cells = kinds[marks[:, None] + np.arange(2, 20)]
    firsts, seconds = cells[:, 0::2], cells[:, 1::2]
    good = np.all((firsts + seconds) == 1, axis=1) & np.all(firsts <= 1, axis=1)
    good &= (firsts.sum(axis=1) % 2) == 1                    # odd parity
    values = (firsts[:, :8].astype(np.int32) << np.arange(8)).sum(axis=1)
    marks, values = marks[good], values[good]
    if details is not None:
        explained = 20 * len(marks) + leader_pulses(units)
        details['other'] = max(0.0, 1.0 - explained / len(units))

    headers, seen = [], set()
    run_start = 0
    for at in range(1, len(marks) + 1):
        if at < len(marks) and marks[at] - marks[at - 1] == 20:
            continue
        record = values[run_start:at]
        run_start = at
        if len(record) < 202:
            continue
        for offset in range(len(record) - 201):
            if record[offset] not in (0x89, 0x09) or record[offset + 9] not in (1, 3):
                continue
            body = record[offset + 9:offset + 201]
            checksum = 0
            for byte in body:
                checksum ^= int(byte)
            if checksum != int(record[offset + 201]):
                continue
            address = int(body[1]) | int(body[2]) << 8
            end = int(body[3]) | int(body[4]) << 8
            if (address, end) not in seen:
                seen.add((address, end))
                headers.append((address, end))
                if details is not None:
                    details.setdefault('kinds', {})[(address, end)] = int(body[0])
            break
    return headers


VIC_EXPANSIONS = ((0x4000, '8K'), (0x6000, '16K'), (0x8000, '24K'))


UNEXPANDED_BASIC = 0x1E00 - 0x1001     # 3583 bytes of BASIC on an unexpanded VIC-20


def vic_memory_from_headers(headers, kinds=None):
    """The RAM expansion the files on a VIC-20 tape need, such as '16K' or 'None'.

    kinds, if given, maps (address, end) to the header type: a relocatable program
    (type 1) saved from a 3K machine at $0401 that is no longer than an unexpanded
    VIC-20's BASIC space loads at $1001 there, so it gives 'None?' rather than 3K."""
    cart, size, low, unexpanded, guessed = False, 0, False, False, False
    for address, end in headers:
        if end <= address:
            end = 0x10000
        if (kinds is not None and kinds.get((address, end)) == 1 and address == 0x0401
                and end - address <= UNEXPANDED_BASIC):
            unexpanded = guessed = True
            continue
        if address >= 0xA000 and end <= 0xC000:
            cart = True
        elif 0x0400 <= address < 0x1000 and end <= 0x1000:
            low = True
        elif address < 0x1000:
            low = True
            size = max(size, end)
        elif address < 0x1E00 and end <= 0x1E00:
            unexpanded = True
        elif address == 0x1001:
            size = max(size, end - 0x1001 + 0x1201)    # BASIC moves up to $1201 when expanded
        elif address < 0x8000:
            size = max(size, min(end, 0x8000))
        else:
            cart = cart or 0xA000 <= address < 0xC000
    words = []
    if size > 0x2000:
        words.append(next((name for top, name in VIC_EXPANSIONS if size <= top), '32K'))
    elif low:
        words.append('3K')
    if cart:
        words.append('A000')
    if words:
        return ' '.join(words)
    if unexpanded:
        return 'None?' if guessed else 'None'
    return ''


MEMORY_TAG = re.compile(r'[\[(]\s*\+?\s*(\d{1,2})\s*k\b[^\])]*[\])]', re.IGNORECASE)


def memory_from_name(name):
    found = MEMORY_TAG.search(name)
    if found and int(found.group(1)) in (3, 8, 16, 24, 32, 35):
        return f'{min(int(found.group(1)), 32)}K'
    if re.search(r'[\[(]\s*(unexp|unexpanded)\s*[\])]', name, re.IGNORECASE):
        return 'None'
    return ''


_collections = {}       # libretro name lists, read once per run


def memory_from_collection(path):
    """The [8k] style tag on the matching libretro name, from the list already downloaded."""
    try:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import cbmassets
        for folder in ('Named_Boxarts', 'Named_Titles', 'Named_Snaps'):
            cache = os.path.join(tempfile.gettempdir(), f'cbm_thumbs_VIC20_{folder}.txt')
            if not os.path.exists(cache):
                continue
            stamp = (cache, os.path.getmtime(cache))
            collection = _collections.get(stamp)
            if collection is None:
                with open(cache, encoding='utf-8') as source:
                    collection = cbmassets.Collection(source.read().splitlines())
                _collections[stamp] = collection
            match = collection.find(os.path.splitext(os.path.basename(path))[0])
            if match:
                return memory_from_name(match)
    except Exception:
        pass
    return ''


def vic_memory(path, options):
    """File name tag first, then the tape's own file headers, then the libretro name."""
    found = memory_from_name(os.path.basename(path))
    if found:
        return found
    try:
        saved = options.turbo
        options.turbo = 0
        tape = load_tape(path, options)
        options.turbo = saved
        for video in (0, 1):
            details = {}
            headers = kernal_headers(tape, CLOCKS['vic20'][video], details)
            found = vic_memory_from_headers(headers, details.get('kinds'))
            if found:
                if details.get('other', 0) > 0.25:
                    # Most of the tape is in the game's own format, so the KERNAL files
                    # are only its loader: the libretro name is the better guide.
                    return memory_from_collection(path) or found.rstrip('?') + '?'
                if found.endswith('?'):
                    return memory_from_collection(path) or found
                return found
    except Exception:
        pass
    return memory_from_collection(path) or '?'


# --- C16 or Plus/4 -----------------------------------------------------------
PLUS4_NAME = re.compile(r'plus[\s_/-]*4|\+4\b|\b264\b|64k', re.IGNORECASE)


def c16_size(end):
    return '16K' if end <= 0x4000 else '32K' if end <= 0x8000 else '64K'


def c16_machine(path, options):
    """The RAM a C16/Plus/4 tape's programs need: '16K' if they end by $4000 (a
    C16), '32K' or '64K' if not (a Plus/4). A '?' after it means most of the tape
    has its own loader, so only that loader could be read and the game itself may
    be bigger."""
    try:
        saved = options.turbo
        options.turbo = 0
        tape = load_tape(path, options)
        options.turbo = saved
    except Exception:
        tape = None
    if tape is not None and tape.pcm is None and tape.pulses is not None:
        pulses = whole_cycles(tape.pulses) if tape.levels is not None else tape.pulses
        units = pulses * CLOCKS['c16'][0] / 8.0
        records, stray = kernal_records(units, c16_cuts(units))
        ends = []
        for payload in records:
            body = good_record(payload)
            if body is not None and len(body) == 191 and body[0] in (1, 3):
                start, end = u16(body, 1), u16(body, 3)
                ends.append(0x10000 if end <= start else end)
        if ends:
            size = c16_size(max(ends))
            if size == '16K' and stray > 400:
                return '64K?' if PLUS4_NAME.search(os.path.basename(path)) else '16K?'
            return size
    return '64K?' if PLUS4_NAME.search(os.path.basename(path)) else '?'


def fast_load_status(tape):
    if 'fast load' in tape.description:
        return 'available'
    refused = re.search(r'normal speed \((.*)\)', tape.description)
    return refused.group(1) if refused else 'recording only'


SPEED_NAMES = {0: 'Normal', 1: 'Fast', 2: 'Turbo', 3: 'Hyper', 4: 'Ultra'}


def tape_seconds(tape):
    if tape.pcm is not None:
        return tape.length / tape.pcm_rate
    return float(tape.pulses.sum())


def format_duration(seconds):
    whole = int(round(seconds))
    return f'{whole // 60}:{whole % 60:02d}'


def tape_speed(tape):
    fast = re.search(r'fast load (\d)', tape.description)
    return int(fast.group(1)) if fast else 0


def short_status(tape):
    """One short line for the status box, such as 'Hyper, 7 blocks, Load Time: 1:04'."""
    stops = len(tape.pause_before)
    parts = f', {stops} stop{"s" if stops > 1 else ""}' if stops else ''
    return f'{SPEED_NAMES[tape_speed(tape)]}, {tape.total_blocks} blocks{parts}, Load Time: {format_duration(tape_seconds(tape))}'


def speed_times(path, options):
    """'speed:seconds:blocks' for normal speed and each fast speed this tape allows."""
    results = []
    for level in range(5):
        options.turbo = level
        tape = load_tape(path, options)
        if tape_speed(tape) != level:
            break
        results.append(f'{level}:{tape_seconds(tape):.0f}:{tape.total_blocks}')
    return ' '.join(results)


# --- Keeping built fast-load tapes ------------------------------------------
# Building a fast-load tape takes a few seconds on an old machine, so the built
# tape is kept in the system's Cache folder and used again next time. One file per
# game: building at another speed, or playing at normal speed, removes it, and a
# changed game file or new settings give a new one.
CACHE_FORMAT = 1


def cache_prefix(path):
    stem = re.sub(r'[^A-Za-z0-9 ._()-]', '_', os.path.splitext(os.path.basename(path))[0])[:48]
    where = hashlib.blake2b(os.path.abspath(path).lower().encode('utf-8'), digest_size=4).hexdigest()
    return f'{stem}-{where}.'


def cache_file(path, options):
    info = os.stat(path)
    key = '|'.join(str(part) for part in (
        CACHE_FORMAT, info.st_size, info.st_mtime_ns, options.machine, options.video, options.turbo,
        getattr(options, 'leader', 1.0)))
    name = cache_prefix(path) + hashlib.blake2b(key.encode('utf-8'), digest_size=8).hexdigest() + '.npz'
    return os.path.join(options.cache, name)


def forget_cached(path, options, keep=None):
    """Removes the game's kept tapes, all but keep."""
    try:
        prefix = cache_prefix(path)
        for name in os.listdir(options.cache):
            full = os.path.join(options.cache, name)
            if name.startswith(prefix) and name.endswith('.npz') and full != keep:
                os.remove(full)
    except OSError:
        pass


CACHE_UNUSED_DAYS = 180


def forget_unused(folder):
    """Kept tapes not played for months, such as those of renamed or deleted games."""
    try:
        oldest = time.time() - CACHE_UNUSED_DAYS * 86400
        for name in os.listdir(folder):
            full = os.path.join(folder, name)
            if name.endswith('.npz') and os.path.getmtime(full) < oldest:
                os.remove(full)
    except OSError:
        pass


def save_cached(tape, target):
    meta = {
        'description': tape.description, 'edge_timed': bool(tape.edge_timed),
        'edge_widths': {str(block): float(width) for block, width in tape.edge_widths.items()},
        'needs_rate': int(tape.needs_rate), 'starts': [int(start) for start in tape.starts],
        'names': list(tape.names), 'pause_before': sorted(int(block) for block in tape.pause_before),
        'header_clock': tape.header_clock,
    }
    arrays = {'pulses': tape.pulses, 'meta': np.array(json.dumps(meta))}
    if tape.levels is not None:
        arrays['levels'] = tape.levels
    if tape.counter_seconds is not None:
        arrays['counter_seconds'] = np.asarray(tape.counter_seconds, np.float64)
    try:
        os.makedirs(os.path.dirname(target), exist_ok=True)
        temporary = target + '.tmp.npz'
        np.savez_compressed(temporary, **arrays)
        os.replace(temporary, target)
    except OSError:
        pass


def load_cached(target):
    try:
        with np.load(target, allow_pickle=False) as stored:
            meta = json.loads(str(stored['meta']))
            tape = Tape(meta['description'])
            tape.pulses = stored['pulses']
            tape.levels = stored['levels'] if 'levels' in stored.files else None
            tape.counter_seconds = stored['counter_seconds'] if 'counter_seconds' in stored.files else None
    except (OSError, ValueError, KeyError):
        return None
    tape.edge_timed = meta['edge_timed']
    tape.edge_widths = {int(block): width for block, width in meta['edge_widths'].items()}
    tape.needs_rate = meta['needs_rate']
    tape.starts = meta['starts']
    tape.names = meta['names']
    tape.pause_before = set(meta['pause_before'])
    tape.header_clock = meta['header_clock']
    return tape


def load_tape_for_play(path, options):
    """load_tape, using and keeping a built fast-load tape when there is a cache."""
    if not options.cache:
        return load_tape(path, options)
    if not options.turbo:
        forget_cached(path, options)            # normal speed: nothing is built
        return load_tape(path, options)
    try:
        target = cache_file(path, options)
    except OSError:
        return load_tape(path, options)
    forget_unused(options.cache)
    if os.path.exists(target):
        tape = load_cached(target)
        if tape is not None:
            try:
                os.utime(target)                    # still in use
            except OSError:
                pass
            tape.description += ' (kept from last time)'
            return tape
    tape = load_tape(path, options)
    if tape_speed(tape) > 0 and tape.pcm is None:
        forget_cached(path, options)
        save_cached(tape, target)
    else:
        forget_cached(path, options)
    return tape


def serve_checks():
    """One process for the whole scan: starting Python and numpy for every game
    took longer than checking most of them."""
    sys.stdin.reconfigure(encoding='utf-8', errors='replace')
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    for line in sys.stdin:
        line = line.rstrip('\r\n')
        if not line:
            continue
        if line == 'quit':
            break
        path, _, machine = line.partition('\t')
        try:
            main([path, '--check', '--machine', machine or 'auto'])
        except SystemExit:
            print('Check: bad file', flush=True)
        except Exception as error:
            print(f'Error: {type(error).__name__}: {error}', flush=True)
            print('Check: bad file', flush=True)
        print('Done:', flush=True)
    return 0


def main(arguments=None):
    if sys.stdout.encoding.lower().replace('-', '') != 'utf8':
        sys.stdout.reconfigure(errors='replace')   # game names may not fit the console code page
    parser = argparse.ArgumentParser(description='Commodore tape player with zero-based block navigation')
    parser.add_argument('tape_file', nargs='?', help='tape image or audio file to play')
    parser.add_argument('--control', default=os.path.join(tempfile.gettempdir(), 'cbm_control.txt'),
                        help='control file path; status files are written beside it')
    parser.add_argument('--machine', choices=['auto', 'c64', 'vic20', 'c16'], default='auto')
    parser.add_argument('--video', choices=['auto', 'pal', 'ntsc'], default='auto')
    parser.add_argument('--wave', choices=['square', 'sine'], default='square')
    parser.add_argument('--invert', action='store_true', help='invert signal polarity')
    parser.add_argument('--volume', type=float, default=AMPLITUDE, help='output level, 0 to 1')
    parser.add_argument('--rate', type=int, default=0, help='sample rate for generated audio')
    parser.add_argument('--turbo', type=int, choices=[0, 1, 2, 3, 4], default=0,
                        help='C64 fast load: 0 off, 1 fast, 2 faster, 3 fastest, 4 ultra')
    parser.add_argument('--check', action='store_true', help='report whether fast load is possible, then exit')
    parser.add_argument('--export', metavar='WAV', help='write the tape to a WAV file instead of playing it')
    parser.add_argument('--list', action='store_true', help='list the blocks and exit')
    parser.add_argument('--no-stops', dest='stops', action='store_false',
                        help='play straight through, without stopping where the tape was stopped')
    parser.add_argument('--to-prg', action='store_true',
                        help='save the one program on a tape as a .prg beside it, then exit')
    parser.add_argument('--leader', type=int, choices=[100, 50, 25], default=100,
                        help='Short Leader: percent of the normal leader tone to keep (for testing)')
    parser.add_argument('--cache', metavar='FOLDER',
                        help='keep built fast-load tapes here and use them again')
    parser.add_argument('--serve', action='store_true',
                        help='check many games: reads "file<TAB>machine" lines, answers each as --check '
                             'would, then a Done: line')
    options = parser.parse_args(arguments)
    global LEADER_SCALE
    LEADER_SCALE = options.leader / 100.0
    options.leader = LEADER_SCALE
    if options.serve:
        return serve_checks()
    if not options.tape_file:
        parser.error('a tape file is needed')
    options.video = {'auto': None, 'pal': 0, 'ntsc': 1}[options.video]

    if options.to_prg:
        try:
            return convert_to_prg(options.tape_file, options)
        except Exception as error:
            print(f'Not converted: {error}', flush=True)
            return 1
    if options.check:
        options.turbo = 1
    try:
        started = time.time()
        if options.check or options.list or options.export:
            tape = load_tape(options.tape_file, options)
        else:
            tape = load_tape_for_play(options.tape_file, options)
        if LEADER_SCALE < 1.0 and not options.check and tape_speed(tape) == 0 and tape.header_clock \
                and tape.pcm is None and tape.levels is None:
            shorten_leaders(tape, tape.header_clock, options.machine if options.machine != 'auto'
                            else ('vic20' if 'VIC' in tape.description else 'c64'), options.stops)
        if options.stops and not options.check:
            add_tape_stops(tape, tape.header_clock)
        print(f'Loaded {tape.description}: {tape.total_blocks} blocks in {time.time() - started:.2f}s', flush=True)
        print(f'Info: {short_status(tape)}', flush=True)
        if options.check:
            print(f'Check: {fast_load_status(tape)}', flush=True)
            print(f'Times: {speed_times(options.tape_file, options)}', flush=True)
            if options.machine == 'vic20':
                print(f'Memory: {vic_memory(options.tape_file, options)}', flush=True)
            elif options.machine == 'c16':
                print(f'Memory: {c16_machine(options.tape_file, options)}', flush=True)
            return 0
        if options.list:
            for index in range(tape.total_blocks):
                begin, end = tape.block_range(index)
                name = tape.names[index] if index < len(tape.names) else ''
                stop = ' (stops before)' if index in tape.pause_before else ''
                print(f'Block {index}: {begin}-{end} {name}{stop}'.rstrip(), flush=True)
            return 0
        if options.export:
            rate = tape.pcm_rate if tape.pcm is not None else (tape.needs_rate or options.rate or DEFAULT_RATES[0])
            export_wav(tape, options.export, rate, options)
            print(f'Wrote {options.export}', flush=True)
            return 0
        print(f'Data blocks are numbered 0 to {tape.total_blocks - 1}', flush=True)
        print('Starting playback...', flush=True)
        play_tape(tape, options.control, options)
    except TapeError as error:
        print(f'Error: {error}', flush=True)
        if options.check:
            print('Check: bad file', flush=True)
        return 1
    except Exception as error:
        print(f'Error: {type(error).__name__}: {error}', flush=True)
        if options.check:
            print('Check: bad file', flush=True)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
