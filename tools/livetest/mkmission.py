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
  telegraph <Section> <n>         engine telegraph at mission start, so a target can begin at the
                                  speed its waypoint would set rather than changing speed for the
                                  first seconds while rounds are being fired at it
  type <Section> <UnitType>       swap the unit's Type=, e.g. to put a specific ship in a mission
  date <year>                     mission year, in [Environment] Date=. Hull loadouts are gated on
                                  it: a Sovremenny declares LoadoutDate_Late=1988, and before that
                                  year its WeaponSystem3/4 carry wp_ss-n-22 while from it the
                                  WeaponSystem3Late/4Late carry wp_ss-n-22b INSTEAD. One mission
                                  cannot hold both, so a family split across loadouts needs two
  unformed <Section>              remove a unit from its taskforce formation list. A FORMATION
                                  OVERRIDES POSITIONS: two merchants moved 48 nm apart were still
                                  0.6 nm apart in game because they were both named in a
                                  Circle|1.5 formation, which cost two runs to spot. `drop` does
                                  this for the unit it removes; do it by hand whenever a `move`
                                  has to separate units that start in one formation
  loadout <Section> <Name>        per-unit LoadoutVariant, the name of a hull's AvailableLoadouts
                                  entry (SceneCreator reads LoadoutVariant per unit). A Sovremenny
                                  offers Default and Late, and its SS-N-22 launchers carry
                                  wp_ss-n-22 on one and wp_ss-n-22b on the other, so this is the
                                  only way to reach a weapon the hull hides behind a loadout
  radar <Section|Taskforce1> <True|False>
                                  set RadarsActive. A bare taskforce name applies it to every unit
                                  in that taskforce, which is what a shooter group wants: the key
                                  defaults to FALSE for a taskforce unit, and a shooter with no
                                  radar track sends its rounds at the wrong place until their own
                                  seekers take over
  copy <Section> <NewSection> <dx> <dz>   duplicate a unit, offset from the original
  drop <Section>                  remove a unit entirely
  course <Section> <x> <z>        replace the unit's waypoint list with one point, absolute NM.
                                  Heading alone does not hold: a unit steers back onto its
                                  waypoint, so a test that needs a beam-on target has to move the
                                  waypoint too.

copy and drop also update the NumberOfTaskforce<N><Kind> count in the [Mission] header. The loader
reads only as many units as that count declares, so a section added without it is silently ignored.

RadarsActive defaults to FALSE for a taskforce unit and true only for a neutral one
(SceneCreator.ReadAndParseSharedUnitData). A shooter whose radar is off has to wait for the AI radar
routine or a passive track, and a poor track shows up as a round steering at the wrong place until
its own seeker takes over. Every autotot base mission now sets RadarsActive=True on its Taskforce1
units, and a derived mission inherits it. Where the geometry outruns the shooter's own radar, add an
E-2C with RadarsActive=True instead, as the yj20 missions do.

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
TLG = re.compile(r'^(Telegraph\s*=\s*)(\d+)\s*$', re.I)
TYP = re.compile(r'^(Type\s*=\s*)(\S+)\s*$', re.I)
RDR = re.compile(r'^(RadarsActive\s*=\s*)(\S+)\s*$', re.I)
LDO = re.compile(r'^(LoadoutVariant\s*=\s*)(\S*)\s*$', re.I)
SEC = re.compile(r'^\[(.+)\]\s*$')
DAT = re.compile(r'^(Date\s*=\s*)(\d+)(\s*,.*)$', re.I)
UNIT = re.compile(r'^(Taskforce\d+)(Vessel|Aircraft|Submarine)(\d+)$', re.I)
FRM = re.compile(r'^(Taskforce(\d+)_Formation(\d+)\s*=\s*)([^|\r\n]*)(.*)$', re.I)
NFRM = re.compile(r'^(Taskforce(\d+)_NumberOfFormations\s*=\s*)(\d+)\s*$', re.I)

# The [Mission] header declares how many units of each kind the loader should read, and it reads
# only that many. A section added or removed without updating its count is silently ignored, which
# is how the first derived mission lost its second merchant. Aircraft is the odd one out: it takes
# no plural.
PLURAL = {'vessel': 'Vessels', 'aircraft': 'Aircraft', 'submarine': 'Submarines'}


def unform(lines, section):
    """Remove `section` from every formation member list, and drop a formation left empty.

    The member list is the first "|"-delimited field of a
    Taskforce<N>_Formation<M>= line. A unit named there is placed BY THE FORMATION, which
    overrides the position in its own section, so a `move` that has to separate two units
    starting in one formation does nothing visible until the shot goes to the wrong ship.
    """
    hit = False
    emptied = set()
    for i, line in enumerate(lines):
        m = FRM.match(line.rstrip('\r\n'))
        if not m:
            continue
        members = [t.strip() for t in m.group(4).split(',') if t.strip()]
        kept = [t for t in members if t.lower() != section.lower()]
        if len(kept) == len(members):
            continue
        hit = True
        if kept:
            lines[i] = f"{m.group(1)}{','.join(kept)}{m.group(5)}\n"
        else:
            lines[i] = None
            emptied.add(m.group(2))
    # A formation with no members left. Remove it, then renumber and recount that taskforce's
    # remaining formations, because the loader reads Formation1..NumberOfFormations BY INDEX and
    # stops at the first gap.
    for i in range(len(lines) - 1, -1, -1):
        if lines[i] is None:
            del lines[i]
    for tf in emptied:
        seq = [i for i, l in enumerate(lines)
               if FRM.match(l.rstrip('\r\n'))
               and FRM.match(l.rstrip('\r\n')).group(2) == tf]
        for n, i in enumerate(seq, 1):
            fm = FRM.match(lines[i].rstrip('\r\n'))
            lines[i] = f"Taskforce{tf}_Formation{n}={fm.group(4)}{fm.group(5)}\n"
        for i, l in enumerate(lines):
            nm = NFRM.match(l.rstrip('\r\n'))
            if nm and nm.group(2) == tf:
                lines[i] = f"{nm.group(1)}{len(seq)}\n"
    return hit


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
    if delta <= 0:
        sys.exit(f"{key} not found in the [Mission] header; cannot remove [{section}]")
    # The kind is new to this taskforce, which is normal for a cross-taskforce copy: a mission
    # whose aircraft are all friendly has no NumberOfTaskforce2Aircraft at all, and adding an
    # enemy one needs the key created rather than incremented.
    for i, line in enumerate(lines):
        if line.strip().lower() == '[mission]':
            lines.insert(i + 1, f"{key}={delta}\n")
            return
    sys.exit(f"no [Mission] section in which to add {key}")


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
        elif op == 'telegraph':
            # A unit that starts on one telegraph and has a SetTelegraph waypoint spends its first
            # seconds changing speed, and a round fired during that window freezes a target velocity
            # the target is about to stop having. On a long shot that is worth seconds of arrival
            # error no launch-time estimate could avoid: a merchant in test aa slows 8.5 to 6.6 kn
            # over ten seconds, which is 1.8 s of forced error on a 515 s Harpoon shot. Setting the
            # start telegraph to match the waypoint's makes the target settled from t = 0.
            sec = args[i + 1]
            j, m = find(lines, bounds[sec], TLG)
            if j is None:
                sys.exit(f"[{sec}] has no Telegraph")
            lines[j] = f"{m.group(1)}{args[i + 2]}\n"
            i += 3
        elif op == 'course':
            # A unit steers back onto its waypoint list, so setting Heading alone does not hold a
            # target beam-on for the length of a 400 s shot. This replaces the list with one point.
            sec, x, z = args[i + 1], args[i + 2], args[i + 3]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            span = bounds[sec]
            j, m = find(lines, span, POS)
            if j is None:
                sys.exit(f"[{sec}] has no RelativePositionInNM")
            y = m.group(3)
            k, wm = find(lines, span, WPT)
            if k is None:
                sys.exit(f"[{sec}] has no Waypoints line to replace")
            lines[k] = f"{wm.group(1)}{x},{y},{z}\n"
            i += 4
        elif op == 'type':
            sec = args[i + 1]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            j, m = find(lines, bounds[sec], TYP)
            if j is None:
                sys.exit(f"[{sec}] has no Type")
            lines[j] = f"{m.group(1)}{args[i + 2]}\n"
            i += 3
        elif op == 'date':
            # Only the year moves; the month and day stay, so light and sea state do not change
            # with the loadout under test.
            if 'Environment' not in bounds:
                sys.exit("mission has no [Environment] section")
            j, m = find(lines, bounds['Environment'], DAT)
            if j is None:
                sys.exit("[Environment] has no Date")
            lines[j] = f"{m.group(1)}{args[i + 1]}{m.group(3)}\n"
            i += 2
        elif op == 'loadout':
            sec, name = args[i + 1], args[i + 2]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            span = bounds[sec]
            j, m = find(lines, span, LDO)
            if j is not None:
                lines[j] = f"{m.group(1)}{name}\n"
            else:
                k, tm = find(lines, span, TYP)
                if k is None:
                    sys.exit(f"[{sec}] has no Type to anchor LoadoutVariant after")
                lines.insert(k + 1, f"LoadoutVariant={name}\n")
            i += 3
        elif op == 'radar':
            # RadarsActive defaults to FALSE for a taskforce unit (SceneCreator.ReadAndParse-
            # SharedUnitData), and the key is usually absent from a hand-built mission, so this
            # inserts it rather than only rewriting it. A bare taskforce name sets every unit in it.
            sel, val = args[i + 1], args[i + 2]
            targets = [sel] if sel in bounds else [
                k for k in bounds if UNIT.match(k) and UNIT.match(k).group(1).lower() == sel.lower()]
            if not targets:
                sys.exit(f"radar: no section or taskforce matches {sel!r}")
            # Rewrite from the bottom so an insert does not move a span still to be edited.
            for sec in sorted(targets, key=lambda k: bounds[k][0], reverse=True):
                span = bounds[sec]
                j, m = find(lines, span, RDR)
                if j is not None:
                    lines[j] = f"{m.group(1)}{val}\n"
                else:
                    k, tm = find(lines, span, TYP)
                    if k is None:
                        sys.exit(f"[{sec}] has no Type to anchor RadarsActive after")
                    lines.insert(k + 1, f"RadarsActive={val}\n")
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
        elif op == 'unformed':
            sec = args[i + 1]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            if not unform(lines, sec):
                print(f"note: [{sec}] was not in any formation list")
            i += 2
        elif op == 'drop':
            sec = args[i + 1]
            if sec not in bounds:
                sys.exit(f"no such section: [{sec}]")
            # Before the section goes, take the unit out of any formation that names it: a
            # dangling member is a reference the loader cannot resolve.
            unform(lines, sec)
            bounds = read_sections(lines)
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
