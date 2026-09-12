#!/usr/bin/env python3
"""Derive a test mission from an existing one.

Sea Power missions are ini files whose unit sections carry a position, a heading and an optional
waypoint list. Most test geometry is a small edit to an existing mission: move a group closer, put
an aircraft at a different altitude, duplicate a target so a heavy salvo cannot sink its way out of
the measurement. Doing that by hand invites a typo in a file the game will then refuse to load, and
doing it in the editor needs a person.

Usage:
  mkmission.py <base.ini> <new-name> [edits...]

Edits, applied in order:
  move <Section> <x> <z>          absolute position, nautical miles, keeps altitude
  nudge <Section> <dx> <dz>       relative move, nautical miles
  alt <Section> <feet>            altitude for an aircraft (the y term of the position)
  heading <Section> <deg>
  type <Section> <UnitType>       swap the unit's Type=, e.g. to put a specific ship in a mission
  copy <Section> <NewSection> <dx> <dz>   duplicate a unit, offset from the original
  drop <Section>                  remove a unit entirely

copy and drop also update the NumberOfTaskforce<N><Kind> count in the [Mission] header. The loader
reads only as many units as that count declares, so a section added without it is silently ignored.

Positions are read and written as RelativePositionInNM=x,y,z, which is what the game uses. A unit's
Waypoints line is shifted by the same delta as its position, so a moved unit keeps its track.

Writes into the same directory as the base file unless the name is a path. Refuses to overwrite.
"""
import os
import re
import shutil
import sys

POS = re.compile(r'^(RelativePositionInNM\s*=\s*)(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*$', re.I)
WPT = re.compile(r'^(Waypoints\s*=\s*)(.*)$', re.I)
HDG = re.compile(r'^(Heading\s*=\s*)(-?[\d.]+)\s*$', re.I)
TYP = re.compile(r'^(Type\s*=\s*)(\S+)\s*$', re.I)
SEC = re.compile(r'^\[(.+)\]\s*$')
UNIT = re.compile(r'^(Taskforce\d+)(Vessel|Aircraft|Submarine)(\d+)$', re.I)

# The [Mission] header declares how many units of each kind the loader should read, and it reads
# only that many. A section added or removed without updating its count is silently ignored, which
# is how the first derived mission lost its second merchant. Aircraft is the odd one out: it takes
# no plural.
PLURAL = {'vessel': 'Vessels', 'aircraft': 'Aircraft', 'submarine': 'Submarines'}


def count_key(section):
    m = UNIT.match(section)
    if not m:
        return None
    return f"NumberOf{m.group(1)}{PLURAL[m.group(2).lower()]}"


def bump_count(lines, section, delta):
    key = count_key(section)
    if key is None:
        return
    pat = re.compile(r'^(' + re.escape(key) + r'\s*=\s*)(\d+)\s*$', re.I)
    for i, line in enumerate(lines):
        m = pat.match(line.strip())
        if m:
            lines[i] = f"{m.group(1)}{int(m.group(2)) + delta}\n"
            return
    sys.exit(f"{key} not found in the [Mission] header; cannot add or remove [{section}]")


def read_sections(lines):
    """Map section name -> [start, end) line indices."""
    bounds, name, start = {}, None, 0
    for i, line in enumerate(lines):
        m = SEC.match(line.strip())
        if m:
            if name is not None:
                bounds[name] = (start, i)
            name, start = m.group(1), i
    if name is not None:
        bounds[name] = (start, len(lines))
    return bounds


def find(lines, span, pattern):
    for i in range(span[0], span[1]):
        m = pattern.match(lines[i].strip())
        if m:
            return i, m
    return None, None


def shift_waypoints(text, dx, dz):
    """Waypoints are 'x,y,z[/Action,arg]' entries separated by '/' groups and ';' or nothing."""
    def one(m):
        x, y, z = float(m.group(1)), m.group(2), float(m.group(3))
        return f"{x + dx:.4f},{y},{z + dz:.4f}"
    return re.sub(r'(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)', one, text)


def move(lines, bounds, section, dx=None, dz=None, absolute=None):
    if section not in bounds:
        sys.exit(f"no such section: [{section}]")
    span = bounds[section]
    i, m = find(lines, span, POS)
    if i is None:
        sys.exit(f"[{section}] has no RelativePositionInNM")
    x, y, z = float(m.group(2)), m.group(3), float(m.group(4))
    if absolute is not None:
        nx, nz = absolute
        dx, dz = nx - x, nz - z
    else:
        nx, nz = x + dx, z + dz
    lines[i] = f"{m.group(1)}{nx:.4f},{y},{nz:.4f}\n"
    j, wm = find(lines, span, WPT)
    if j is not None and wm.group(2).strip():
        lines[j] = f"{wm.group(1)}{shift_waypoints(wm.group(2), dx, dz)}\n"
    return dx, dz


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    base, newname = sys.argv[1], sys.argv[2]
    if not os.path.isfile(base):
        sys.exit(f"base mission not found: {base}")
    out = newname if os.path.sep in newname else os.path.join(os.path.dirname(base), newname)
    if not out.lower().endswith('.ini'):
        out += '.ini'
    if os.path.exists(out):
        sys.exit(f"refusing to overwrite: {out}")

    with open(base, 'r', encoding='utf-8', errors='replace', newline='') as fh:
        lines = fh.readlines()

    args, i = sys.argv[3:], 0
    while i < len(args):
        op = args[i].lower()
        bounds = read_sections(lines)
        if op == 'move':
            move(lines, bounds, args[i + 1], absolute=(float(args[i + 2]), float(args[i + 3]))); i += 4
        elif op == 'nudge':
            move(lines, bounds, args[i + 1], dx=float(args[i + 2]), dz=float(args[i + 3])); i += 4
        elif op == 'alt':
            sec, feet = args[i + 1], args[i + 2]
            span = bounds[sec]
            j, m = find(lines, span, POS)
            if j is None:
                sys.exit(f"[{sec}] has no RelativePositionInNM")
            lines[j] = f"{m.group(1)}{m.group(2)},{feet},{m.group(4)}\n"
            k, wm = find(lines, span, WPT)
            if k is not None and wm.group(2).strip():
                lines[k] = f"{wm.group(1)}{re.sub(r'(-?[\\d.]+),(-?[\\d.]+),(-?[\\d.]+)', lambda mm: f'{mm.group(1)},{feet},{mm.group(3)}', wm.group(2))}\n"
            i += 3
        elif op == 'heading':
            sec = args[i + 1]
            j, m = find(lines, bounds[sec], HDG)
            if j is None:
                sys.exit(f"[{sec}] has no Heading")
            lines[j] = f"{m.group(1)}{args[i + 2]}\n"
            i += 3
        elif op == 'type':
            sec = args[i + 1]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            j, m = find(lines, bounds[sec], TYP)
            if j is None:
                sys.exit(f"[{sec}] has no Type")
            lines[j] = f"{m.group(1)}{args[i + 2]}\n"
            i += 3
        elif op == 'copy':
            sec, newsec, dx, dz = args[i + 1], args[i + 2], float(args[i + 3]), float(args[i + 4])
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            if newsec in bounds:
                sys.exit(f"[{newsec}] already exists")
            start, end = bounds[sec]
            block = list(lines[start:end])
            block[0] = f"[{newsec}]\n"
            lines[end:end] = block
            move(lines, read_sections(lines), newsec, dx=dx, dz=dz)
            bump_count(lines, newsec, +1)
            i += 5
        elif op == 'drop':
            sec = args[i + 1]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            start, end = bounds[sec]
            del lines[start:end]
            bump_count(lines, sec, -1)
            i += 2
        else:
            sys.exit(f"unknown edit: {op}")

    with open(out, 'w', encoding='utf-8', newline='') as fh:
        fh.writelines(lines)

    bounds = read_sections(lines)
    units = [s for s in bounds if s.startswith('Taskforce')]
    print(f"wrote {out}")
    for s in units:
        j, m = find(lines, bounds[s], POS)
        if j is not None:
            print(f"  {s:<24} x={float(m.group(2)):9.2f} y={m.group(3):>6} z={float(m.group(4)):9.2f}")


if __name__ == '__main__':
    main()
