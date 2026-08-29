# Changelog

## 1.1.0

Occult Crescent support, a Rotation Solver Reborn integration that no longer fights BOCCHI, an MP
floor, and a master switch that actually means off.

### Requires Rotation Solver Reborn

RSR is now a stated requirement. Dalamud manifests have no plugin-to-plugin dependency field, so
this is enforced the three ways that reach people instead: the manifest description leads with it,
the README installs it first, and the **Integration** tab shows a warning with the Combat Reborn
repository URL and a copy button when RSR is not loaded.

```
https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
```

### Occult Crescent

- Raises with the phantom job's own raise, which overrides every other raise setting:
  **Occult Raise** (Phantom White Mage, phantom level 4) or **Revive** (Phantom Chemist, level 3).
  Both are instant, cost no MP and recast in five seconds, so Swiftcast, the MP floor, the hardcast
  rules and the gearset switching are all answers to costs they do not have and are skipped rather
  than consulted.
- Works on **any job** — the phantom job grants the action, not your class, so a Warrior raises.
- **Prefer** picks between the two when both are levelled. Automatic keeps whichever is already
  equipped and never swaps for a distinction that does not exist.
- New tab showing the conditions as a checklist rather than one verdict, because "phantom raise
  unavailable" tells somebody standing in South Horn nothing.
- Optional, **off by default**: change phantom job out of combat to raise, then change back. Off
  because the phantom job is the thing being levelled, and being taken off it mid-grind is a
  decision you should make rather than discover.
- Manual **switch now** buttons for the one-off case, with a *Back to …* button so a hand swap is
  not a one-way door. Pressing one stands the automatic errand down — your choice by hand wins.

Zone detection asks the content director rather than comparing territory ids, so it is both the zone
check and the readiness check, and stays correct in zones that do not exist yet.

### MP

- New **Minimum MP** setting, 2400–10000, default 2400.
- Below the floor the plugin does not merely decline to cast, it declines to *interfere*: Rotation
  Solver is left running. Pausing somebody's rotation and then failing to raise costs them the GCD
  **and** leaves the body down, which is strictly worse than never getting involved. The floor is
  therefore checked before RSR is touched, not inside the cast.
- Red Mage needing Dualcast pays Vercure's 500 on top, so its real floor is 2900 — derived rather
  than a second setting.
- The slider will not go below one raise. Lower is not a gentler setting but a broken one: the game
  refuses the cast below its cost, so the plugin would keep interrupting your rotation to attempt a
  spell it cannot afford.

### Rotation Solver Reborn and BOCCHI

- **Resume as**, defaulting to Automatic: Henched when BOCCHI is loaded, Auto otherwise.
- BOCCHI's Illegal Mode drives RSR in **Henched** — rotation only, no targeting — and does not
  re-send that mode once it has landed. Resuming in Auto therefore handed RSR its own targeting back
  mid-run without BOCCHI noticing, and two things chose targets. Resuming as Henched is what keeps
  them agreeing.
- AutoRezzer never touches BOCCHI. Illegal Mode stays as you left it; only RSR is moved, and only
  while there is a body to pick up.
- The mode is a setting rather than a reading because it has to be: RSR's IPC exposes one read —
  `AutorotationActive()`, a bool — and nothing that says *which* mode; BOCCHI exposes no IPC at all.

### Raising

- **Only rez with Swiftcast** — never hardcast, wait instead. Red Mage is unaffected, being already
  incapable of hardcasting a raise here.
- **…in combat only**, narrowing the above to where the eight second cast is actually dangerous.
- **Switch to Red Mage when Swiftcast is down**, out of combat, with a gearset picker that defaults
  to the first Red Mage set found. Red Mage is the only job with an instant raise that does not cost
  Swiftcast, so this is the difference between raising now and waiting out a sixty second cooldown.

### Changed

- **`Enabled` is now a master switch.** Off means nothing raises, nothing is accepted, no job is
  switched and RSR is left alone. Accepting a raise used to be exempt, on the reasoning that being
  raised has nothing to do with being able to raise — fine right up until you want the plugin to
  stop. A switch that leaves something running is a switch you cannot trust.
- **Announce in echo** replaces *Announce in chat*, and now fires only on the raise itself rather
  than on the Swiftcast and Vercure steps as well. Echo cannot leave your client.
- **Enemy distance and raise delay now default to 0**, and existing configs are migrated to them —
  their old defaults were the reason the plugin sat and watched people stay dead in the content it
  was built for.
- **Red Mage's Vercure is unconditional**, not a toggle. The alternative is a cast Red Mage is
  forbidden from starting, so a Red Mage with it off simply stood there.
- **Statistics are permanent** — written to disk the moment each raise lands, rather than surviving
  only if something else happened to save afterwards — and live on their own tab.
- **The status line describes the room** instead of saying "Watching.": *"Solo gaming."*,
  *"14 players around, all alive."*, *"14 players around, 3 dead already, 1 rezzed, rezzing Someone
  Else."* Counts are honest — every player in range, not only those that survived the filter.
- The window is now tabbed: **Raising / Occult Crescent / Job switching / Integration / Statistics**.

### Removed

- *Skip people with Brink of Death*.
- *Red Mage: Vercure for Dualcast* — now always on.
- The per-class statistics breakdown and *Reset Statistics*. The stored per-job history is kept in
  the config rather than deleted, so nothing is lost if it ever comes back.

### Fixed

- RSR received every mode change **twice** — once over IPC and again as a `/rotation` command
  dispatched as a "fallback" even when the IPC call had succeeded — printing two lines to chat for
  people who have RSR's toggle messages on.
- RSR was reported as *active* whenever the `/rotation` command merely existed, so the plugin would
  "resume" an autorotation that was never running.
- Turning the plugin off mid-swap abandoned the trip **where it stood**, leaving you in the healer's
  gearset. Both switchers now finish their trip home, and the tick stays alive only while one is.
- *Only rez with Swiftcast* combined with *Use Swiftcast* off was a deadlock rather than a rule —
  never use Swiftcast, never raise without it. The former now implies the latter.
- The object-table sweep was skipped on two code paths and run more than once on others.
- The status line flickered between the job explanation and nothing, because the sweep cleared a
  reason the job check had just written. Reasons are now kept apart by source.

### Config migrations

Handled on load, logged when they happen: raise delay and enemy distance reset to 0; any stored MP
floor below 2400 raised to it.

---

*Versions 1.1.0 through 1.4.0 existed only as development steps during this work and were never
released; everything above ships as 1.1.0.*

## 1.0.0

First release.
