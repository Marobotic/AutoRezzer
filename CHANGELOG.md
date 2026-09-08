# Changelog

## 1.4.1

### It stands down in PvP

Frontline, Crystalline Conflict, Rival Wings, the Wolves' Den: the plugin holds completely until you
leave. Nothing sweeps, nothing swaps your job, nothing accepts anything.

Nobody can be raised in PvP, so there was never anything to gain in there - but there was plenty to
lose. Frontline lets you change class at the spawn point, and the spawn point is exactly where dead
players pile up waiting to respawn. The plugin saw a field of bodies, decided an instant raise was
called for, and turned you into a Red Mage mid-match, taking the damage you had done and your series
progress on the class you queued as with it.

This is not a setting and will not become one. The card and the server bar both say so while it is
holding, so a plugin that is switched on and deliberately doing nothing still looks switched on.

### One less line on the card

The **Active** badge already says a job is ready to rez, so the "Ready to rez" line that sat under
the job name has gone. The line is still there when there is something to say - a level you are
synced below, a Swiftcast still on cooldown, a job with no raise at all.

## 1.4.0

A new window, and a lot fewer settings.

### Its own window frame

The grey Dalamud title bar is gone. In its place: AutoRezzer's own icon and name, a **Support this
plugin** button that opens Ko-fi, and a close button that matches the rest of the window.

### The master switch is a switch again

It sits in its own row at the top of the screen, and off means off. Nothing watches, nothing rezzes,
nothing accepts a rez cast on you, and nothing is said to your rotation. The window goes quiet with
it rather than showing settings for a plugin that is not running.

The only thing that still finishes is a job change already in progress, so reaching for the switch
mid-swap does not leave you stood in a healer's gearset.

### It looks like a phone app now

One screen with everything that matters, and the rare things one tap away. At the top is a card that
tells you what is going on:

- **Your job**, with its real in-game icon, and whether you can rez right now.
- In the Occult Crescent, **your phantom job** underneath it, drawn with the game's own pixel art.
- A green **Active** beside whichever of the two is actually going to do the rezzing.
- Under the line, what the plugin is doing: watching, rezzing someone, waiting on Swiftcast, paused.

If you have ever wondered why nothing was happening, that card is the answer. A Red Mage synced below
64 is told Verraise is gone. A White Mage under 18 is told there is no Swiftcast yet. A job that
cannot rez says so.

### Settings that are gone

Each of these had one right answer, so the plugin just does it now:

| Was a setting | Now |
|---|---|
| Raise people outside my party | Always. Rezzing strangers is the point. |
| Use Swiftcast | Always, on any job that has it. |
| Enemy distance | Removed. |
| Pause Rotation Solver, Resume as, the whole Integration tab | Automatic. Your rotation stops only while a rez is really happening, then goes back exactly as it was. |
| Switch to Red Mage | Always, when it helps. Uses your first Red Mage gearset. |
| Red Mage gearset | Removed. |
| Change phantom job back afterwards | Always changes back. |
| Stand down in sanctuaries | Now a **Sanctuaries** switch you can turn on. |

The help text went with them. Nothing on screen needs a paragraph to explain it any more.

### Where it rezzes

Four switches instead of one rule:

- **Open world / FATEs**
- **In duties**, off by default. In a dungeon there is a healer whose job this is, and you cannot
  change job mid-fight anyway.
- **Field operations**: Eureka, Bozja and the Occult Crescent.
- **Sanctuaries**, off by default.

### New

- **Accept rez after**: hold the rez prompt for a few seconds before answering it. Off by default.
  Turn it up so a wipe does not stand you straight back up into the mechanic that killed you.
- **Thin Air** is used so a White Mage can rez on an empty MP bar.
- **Lucid Dreaming** goes up on its own when low MP is what is stopping the rez.
- **Phantom job pictures** everywhere they appear, from the game's own art. Pick which one to swap
  to from two big cards rather than a list of names.
- **Your phantom job levels are remembered**, so the Crescent settings still make sense from
  Limsa. They are read on your next visit to the zone.
- **Rezzing job** is picked by job rather than by gearset, and Red Mage is marked *Recommended* since
  it is the only one that rezzes instantly without spending Swiftcast.

### It stays out of the way

- If no job on your character can rez, the rezzing half of the window is hidden entirely. What is
  left is **Accept rezzes on me**, which works on every job in the game, in every kind of content.
- On a job that cannot rez, inside a duty, it does not watch at all. There is nothing it could do.
- The Occult Crescent settings appear once you have been there.

## 1.2.0

Three fixes to one question: **when is this plugin allowed to take your rotation away?** The old
answer was "whenever it notices a body", and it was wrong in every direction, it interrupted fights
it could do nothing about, held the interruption through cooldowns it was only going to wait out,
and handed the rotation back in the wrong mode.

### It no longer stops your rotation for a raise that is not going to happen

The order of two questions was backwards. The plugin used to pause Rotation Solver Reborn and *then*
work out whether it could cast, so every reason it could not became a reason your rotation stayed
off, for as long as the body lay there.

Every one of those reasons is now asked **before** anything is paused:

- **Wrong job, in combat.** A Gunbreaker in Dancing Mad, standing near anyone's corpse, lost their
  rotation to a gearset change the game refuses outright in combat. Nothing is paused now unless a
  job change or phantom-job change has actually *started*.
- **"Only rez with Swiftcast", Swiftcast on cooldown.** Fifty seconds of no rotation and no raise.
  It now holds with its hands off and stops your rotation at the moment Swiftcast is available.
- **Red Mage with no Dualcast, no Swiftcast and no Vercure**; **mounted**; **being the one on the
  floor**; **below the MP floor**, all holds, none of them pauses.

The one exception is deliberate: a job change is also refused while a cast is in progress, and the
cast in progress is Rotation Solver's own. That is the single blocker pausing actually clears, so it
is the single case where the pause comes first.

The flowchart is now what it reads like: *can I raise this instant? → pause → raise → hand it back.
No? → hold, and keep watching.* The window says which one it is doing, and why.

### It restores the mode Rotation Solver was actually in

"Resume as" was a mode chosen in advance and applied to a state nobody had looked at, so it restored
**Henched** to people who had been in **Auto**, exactly as surely as the version before it took the
wheel off BOCCHI by resuming Auto over Henched.

RSR's IPC still cannot be asked which mode it is in, it exposes one bool. So AutoRezzer reads the
mode off the running plugin instead, at the moment it pauses, and sends back exactly that: Off, Auto,
Target Only, Manual, AutoDuty, Henched or PvP. Auto with low-HP targeting comes back as Auto with
low-HP targeting. Henched comes back as Henched.

The setting survives, renamed **Fallback mode**, for the day RSR renames the field that read comes
from. The Integration tab says outright which of the two is in use.

### Where it runs

Two switches, both of which sit between the master switch and everything else:

- **Field operations only**, Eureka, Bozja and the Occult Crescent, and nowhere else. Delubrum
  Reginae counts; it is launched from inside Bozja and runs on the same lost actions. Read off the
  zone's own classification rather than a list of zone ids, so a field operation that ships next
  year counts without a plugin update.
- **Stand down in sanctuaries**, cities, inns, the Bozjan staging area, the Crescent's base camp.
  Nobody can die in one, so there is nothing there to do. It picks back up the moment you walk out,
  and it walks out of a sanctuary without the zone changing.

**Neither one moves the master switch.** They decide whether it is being honoured, not what it says,
so turning the plugin off yourself in a city leaves it off when you leave, because nothing was
overwritten and there is nothing to restore. The server bar reads `Rez: hold` while a rule is
standing the plugin down, and the window says which rule and where it thinks you are.

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
- Works on **any job**, the phantom job grants the action, not your class, so a Warrior raises.
- **Prefer** picks between the two when both are levelled. Automatic keeps whichever is already
  equipped and never swaps for a distinction that does not exist.
- New tab showing the conditions as a checklist rather than one verdict, because "phantom raise
  unavailable" tells somebody standing in South Horn nothing.
- Optional, **off by default**: change phantom job out of combat to raise, then change back. Off
  because the phantom job is the thing being levelled, and being taken off it mid-grind is a
  decision you should make rather than discover.
- Manual **switch now** buttons for the one-off case, with a *Back to …* button so a hand swap is
  not a one-way door. Pressing one stands the automatic errand down, your choice by hand wins.

Zone detection asks the content director rather than comparing territory ids, so it is both the zone
check and the readiness check, and stays correct in zones that do not exist yet.

### MP

- New **Minimum MP** setting, 2400–10000, default 2400.
- Below the floor the plugin does not merely decline to cast, it declines to *interfere*: Rotation
  Solver is left running. Pausing somebody's rotation and then failing to raise costs them the GCD
  **and** leaves the body down, which is strictly worse than never getting involved. The floor is
  therefore checked before RSR is touched, not inside the cast.
- Red Mage needing Dualcast pays Vercure's 500 on top, so its real floor is 2900, derived rather
  than a second setting.
- The slider will not go below one raise. Lower is not a gentler setting but a broken one: the game
  refuses the cast below its cost, so the plugin would keep interrupting your rotation to attempt a
  spell it cannot afford.

### Rotation Solver Reborn and BOCCHI

- **Resume as**, defaulting to Automatic: Henched when BOCCHI is loaded, Auto otherwise.
- BOCCHI's Illegal Mode drives RSR in **Henched**, rotation only, no targeting, and does not
  re-send that mode once it has landed. Resuming in Auto therefore handed RSR its own targeting back
  mid-run without BOCCHI noticing, and two things chose targets. Resuming as Henched is what keeps
  them agreeing.
- AutoRezzer never touches BOCCHI. Illegal Mode stays as you left it; only RSR is moved, and only
  while there is a body to pick up.
- The mode is a setting rather than a reading because it has to be: RSR's IPC exposes one read -
  `AutorotationActive()`, a bool, and nothing that says *which* mode; BOCCHI exposes no IPC at all.

### Raising

- **Only rez with Swiftcast**, never hardcast, wait instead. Red Mage is unaffected, being already
  incapable of hardcasting a raise here.
- **…in combat only**, narrowing the above to where the eight second cast is actually dangerous.
- **Switch to Red Mage when Swiftcast is down**, out of combat, with a gearset picker that defaults
  to the first Red Mage set found. Red Mage is the only job with an instant raise that does not cost
  Swiftcast, so this is the difference between raising now and waiting out a sixty second cooldown.

### Changed

- **`Enabled` is now a master switch.** Off means nothing raises, nothing is accepted, no job is
  switched and RSR is left alone. Accepting a raise used to be exempt, on the reasoning that being
  raised has nothing to do with being able to raise, fine right up until you want the plugin to
  stop. A switch that leaves something running is a switch you cannot trust.
- **Announce in echo** replaces *Announce in chat*, and now fires only on the raise itself rather
  than on the Swiftcast and Vercure steps as well. Echo cannot leave your client.
- **Enemy distance and raise delay now default to 0**, and existing configs are migrated to them -
  their old defaults were the reason the plugin sat and watched people stay dead in the content it
  was built for.
- **Red Mage's Vercure is unconditional**, not a toggle. The alternative is a cast Red Mage is
  forbidden from starting, so a Red Mage with it off simply stood there.
- **Statistics are permanent**, written to disk the moment each raise lands, rather than surviving
  only if something else happened to save afterwards, and live on their own tab.
- **The status line describes the room** instead of saying "Watching.": *"Solo gaming."*,
  *"14 players around, all alive."*, *"14 players around, 3 dead already, 1 rezzed, rezzing Someone
  Else."* Counts are honest, every player in range, not only those that survived the filter.
- The window is now tabbed: **Raising / Occult Crescent / Job switching / Integration / Statistics**.

### Removed

- *Skip people with Brink of Death*.
- *Red Mage: Vercure for Dualcast*, now always on.
- The per-class statistics breakdown and *Reset Statistics*. The stored per-job history is kept in
  the config rather than deleted, so nothing is lost if it ever comes back.

### Fixed

- RSR received every mode change **twice**, once over IPC and again as a `/rotation` command
  dispatched as a "fallback" even when the IPC call had succeeded, printing two lines to chat for
  people who have RSR's toggle messages on.
- RSR was reported as *active* whenever the `/rotation` command merely existed, so the plugin would
  "resume" an autorotation that was never running.
- Turning the plugin off mid-swap abandoned the trip **where it stood**, leaving you in the healer's
  gearset. Both switchers now finish their trip home, and the tick stays alive only while one is.
- *Only rez with Swiftcast* combined with *Use Swiftcast* off was a deadlock rather than a rule -
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
