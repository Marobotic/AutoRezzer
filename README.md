# AutoRezzer

A quick, handy tool for the raising half of a fight: it casts raises on the people around you, and
it accepts the ones cast on you.

One toggle. When it is on and you are on a job that can raise, at a level that can raise, any
raisable body within 30 yalms gets raised, Swiftcast first when it is up, unless there are live
enemies standing on it. Separately, and whether or not that switch is on, it answers the raise
prompt when *you* are the one on the floor.

Built mostly for **Occult Crescent and FATEs**, where people go down constantly, half of them are
strangers, and nobody is keeping track of who still needs picking up. It works anywhere, but that is
the case it is shaped around.

This is a plugin for [Dalamud](https://github.com/goatcorp/Dalamud) (the FFXIV plugin framework used
with XIVLauncher).

## Requirements

**[Rotation Solver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) is required.**

Dalamud has no way for one plugin to declare another as a dependency, so nothing will stop you
installing AutoRezzer without it, but a raise and an autorotation are two things competing for the
same GCD, and the whole reason this plugin can raise in combat at all is that it stops RSR for the
length of the cast. Without RSR present, your rotation spends the global cooldown the raise needed
and the raise never goes out. Without it, nothing stops your rotation spending the GCD the rez needed.

Install it from the Combat Reborn repository:

```
https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
```

[BOCCHI](https://github.com/OhKannaDuh/BOCCHI) is optional, and AutoRezzer knows about it, see
*Rotation Solver Reborn and BOCCHI* below.

## Installation

1. Install [XIVLauncher](https://goatcorp.github.io/) and enable Dalamud.
2. Open Dalamud settings (`/xlsettings`) → **Experimental** → **Custom Plugin Repositories**.
3. Add **both** of these repository URLs and press **Save**:
   ```
   https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
   https://raw.githubusercontent.com/Marobotic/PfPresets/main/repo/repo.json
   ```
4. Open the plugin installer (`/xlplugins`) and install **Rotation Solver Reborn**, then **AutoRezzer**.

It ships **switched off**. Open it with `/autorez` and turn it on when you want it.

## The master switch

**The switch at the top of the window governs everything.** Off means nothing raises, nothing is accepted, no job is switched
and Rotation Solver is left entirely alone, not "the casting half is off".

This used to work the other way round: accepting a raise was exempt, on the reasoning that being
raised has nothing to do with being able to raise. That reasoning is fine right up until you want
the plugin to stop, and then it isn't, a switch that leaves something running is a switch you
cannot trust, and acting on its own is this plugin's entire job.

*Accept raises cast on me* is still its own setting. It is simply a setting inside a plugin that is
running.

The one thing that still happens with the switch off is a **job change finishing its trip home**.
That is not a feature staying on, it is the plugin cleaning up something it already started, being
turned off should never cost you your gearset or your phantom job.

## The window

`/autorez` (or `/az`) opens it. `on`, `off` and `toggle` work as arguments on either.

One screen with the switch on it, three behind a chevron. The card at the top is the part worth
knowing about.

### The card

Your job, your level, and the rez you actually have right now. It exists to answer "why
isn't it working" before you go looking through settings for the reason:

- *Raise, instant with Swiftcast* is the ordinary case.
- *Raise, no Swiftcast until Lv 18* is a White Mage between 12 and 17. It can rez, it just has to
  stand still for eight seconds. No other healer sees this: Scholar and Astrologian start at 30, Sage
  at 70, so they always have Swiftcast by the time they have a rez.
- *Verraise needs Lv 64, synced too low* is a Red Mage in old content. Sync takes the spell away.
- *No rez on this job* is everybody else.

Inside the Crescent your **phantom job** sits on the line below, with the game's own pixel art at the
same size as the job emblem. They are alternatives, not a heading and a footnote.

Exactly one of the two carries a green **Active** badge, and which one is the plugin's priority made
visible: the phantom job wins when it has a rez and *Prefer phantom rez* is on, otherwise your class
does. It passes no judgement on the phantom job you happen to be wearing, because there is nothing to
report: a White Mage on Phantom Dragoon rezzes as a White Mage.

Underneath, what the plugin is doing: `Watching`, `3 down nearby`, `Waiting on Swiftcast`,
`Paused, safe zone`. When it is waiting rather than working, it says so **and** says your rotation is
still running, because that is the part people worry about.

### Rezzing in

Four switches over the game's own classification of the zone.

| Switch | Default | Covers |
|---|---|---|
| Open world / FATEs | on | Overworld, FATEs, hunt trains, the Diadem, the Firmament |
| In duties | **off** | Dungeons, trials, raids, deep dungeons, variant and criterion |
| Field operations | on | Eureka, Bozja, Delubrum Reginae, the Occult Crescent |
| Sanctuaries | **off** | Cities, inns, the Bozjan staging area, the Crescent's base camp |

The Crescent has no switch of its own. It **is** a field operation, and nobody wanting the plugin
quiet in Bozja wants it loud one zone over. The Crescent screen is still there, for phantom jobs.

Instances are off by default and that is the most useful default here. A Gunbreaker in a savage fight
cannot rez, cannot change job in combat, and has a healer standing right there whose job this is.

*Sanctuaries* sits alongside the others rather than inside them, because a rested area cuts across
all of them: a city is open world, and the Crescent's base camp is a field operation. It is off by
default because that is the quiet answer, not because bodies never turn up in one.

**A job that cannot rez, inside a duty, is never watched.** Not held, not "watching in case": there
is no way out of it. The game refuses gearset changes in combat, a dungeon has no phantom jobs to
borrow, and the fight ends when the duty does. Field operations are the exception, which is why they
are not covered by the same rule: Bozja and the Crescent are open enough to leave combat in, and the
Crescent hands a rez to any job at all.

### Getting rezzed

| Setting | Default | What it does |
|---|---|---|
| Accept rezzes on me | on | Answers the rez prompt when *you* are down. Works on every job. |
| Wait first | off | Hold the prompt for a few seconds. Standing up instantly during a wipe puts you back in the mechanic that killed you and burns the rez. |

### Occult Crescent

| Setting | Default | What it does |
|---|---|---|
| Rez in the Crescent | on | Whether the plugin works here at all. |
| Prefer phantom rez | on | Used whenever your phantom job has one. Works on **any job**. |
| Swap phantom job to rez | **off** | Out of combat, borrow one that can rez, then straight back. |
| Swap to | first levelled one | Which phantom job to borrow, as two big cards with the game's own sprites. |

Only phantom jobs you have actually levelled far enough appear. One levelled raiser means one card,
already chosen; there is no "whichever" option, because filtering the list properly removes the need
for one.

The swap back is automatic and not offered as a choice. Borrowing a job and not giving it back is
not a preference anybody holds, it is what the feature failing looks like.

**The order it decides in:** a phantom job that can rez is used whatever your class is. A phantom
job that cannot, on a class that can, uses the class rez and ignores phantom jobs entirely. Neither
of those, and the swap picks a phantom job up out of combat and puts it back afterwards.

### Swap to a rez job

One row per **job**, not per gearset: three White Mage sets are one choice presented three times, and
the first one found is the answer. Jobs you have no gearset for, or have not levelled to their rez
level, are not listed at all. Picking one closes the screen.

| Setting | Default | What it does |
|---|---|---|
| Job | never | Which job to become when someone dies and yours cannot rez. |
| Swap back after | on | Return to the job you were on once the rez lands. |
| Use Red Mage instead | off | Out of combat with Swiftcast down, change to Red Mage and rez through Dualcast. Uses the first Red Mage gearset it finds. |

*Never hardcast rez* turns this on implicitly. Somebody who has said they will not stand still for eight
seconds, and who owns a Red Mage above 64, has already answered the question. It covers the gap
*only in combat* leaves behind: that rule stops applying the moment the fight ends, so without this
the plugin would happily hardcast out of combat instead of taking the instant route.

### Advanced

| Setting | Default | What it does |
|---|---|---|
| Never hardcast rez | off | Wait for Swiftcast instead of standing still for eight seconds. Every job except Red Mage, which uses Dualcast. |
| Only in combat | on | Narrows the rule above to combat, where the eight second cast is the dangerous one. |
| Wait before rezzing | off | How long somebody must have been down first. Turn it up in a party with its own healer. |
| Minimum MP | 2,400 | Below this it holds instead of casting and leaves your rotation alone. Red Mage's real floor is 2,900. |

**Lucid Dreaming** goes up on its own when the bar is what is stopping the rez. It is off-GCD, so it
costs nothing and nothing is paused for it. **Thin Air** waives the floor entirely on White Mage:
holding one means rezzing on an empty bar, and it is only spent when the bar could not cover the rez
anyway.
| Announce in /echo | off | One line per rez, in your own echo. It cannot leave your client. |

### The server bar

`Rez: on` / `Rez: paused` / `Rez: off`. Click to toggle without opening anything. That is the point
of it: this plugin acts on its own, and what you want the moment you notice it acting is an off
switch, not a window.

The bar and the window read from the same answer, deliberately. *Paused* covers every reason nothing
is going to happen, because from outside they are the same thing: a sanctuary, a content switch
turned off, a job with no rez and no way to get one, an empty MP bar. The tooltip says which.

## What the window hides

Settings that cannot apply to your character are not shown.

- **No job on this character can rez** and the whole rezzing half disappears: content switches, job
  swapping, the MP floor, the hardcast rule. What is left is *Accept rezzes on me*, which works on
  every job in the game, and it keeps working in every kind of content regardless of the four
  switches, because answering the prompt on your own corpse interferes with nothing.
- **The Occult Crescent** section appears once you have been there, and the phantom jobs you have
  levelled are remembered from your last visit.

  This is not a shortcut. `SupportJobLevels` exists in exactly one structure in the whole client,
  `OccultCrescentState`, reached only through the Crescent's instance director, and that director is
  created on entering the zone and destroyed on leaving. From anywhere else in the game there is
  nothing to read. (`UIModule`'s `MKDSupportJobModule` sounds like the answer and is not: it holds
  which action each phantom job has on its bar, not its level.)

  So the levels are copied out on the way past and read back when the live ones are missing. That
  recording happens whether or not the plugin is switched on, because it is the only chance anybody
  gets.

## What is not configurable, on purpose

Several settings were deleted in 1.4.0 because each had exactly one right answer.

- **Rez strangers.** The plugin is *for* strangers. A FATE train is strangers.
- **Use Swiftcast.** Mandatory wherever the job has it. Off, paired with *never hardcast*, was a
  deadlock rather than a configuration.
- **Skip bodies near enemies.** Shipped at zero, stayed at zero, and any radius large enough to
  matter rezzed nobody in the content this is built for.
- **Pause Rotation Solver**, **Resume as**, and the whole Integration screen. Now automatic: read
  the mode your autorotation is in, switch it off for the cast, put that exact mode back.
- **Stand down in sanctuaries.** Replaced by *Rez in sanctuaries*, the same question asked the
  right way round, and a switch rather than a rule.

## Switching job to raise

Pick a gearset under *Raise on another job* and the plugin will, when somebody dies and you are on a
job that cannot raise, switch to that gearset, raise them, and switch back.

A **gearset, not a job**, equipping a gearset is the only way the game changes job, and naming the
job alone would mean guessing which of your three White Mage sets was meant.

Two things worth knowing:

- **The game refuses job changes in combat.** So this only ever happens once a fight is over, which
  is the corpse-run case it is for. It is not a mid-fight rescue.
- **It gives up rather than stranding you.** Every step has a timeout, and the worst case is always
  "go back to the job you were on", never "sit here as a Sage". If somebody else raises the body
  while your gear is still swapping, it turns around without casting.

## Occult Crescent

Inside Occult Crescent, if the phantom job you are on can raise, AutoRezzer uses **its** raise and
nothing else applies. Two support jobs qualify:

| Phantom job | Raise | From phantom level |
|---|---|---|
| Phantom White Mage | Occult Raise | 4 |
| Phantom Chemist | Revive | 3 |

They are **identical in use**, instant, no MP, five second recast, thirty yalms, so *Prefer* is
really a question about which job's other four actions you would rather be holding. Automatic keeps
whichever is already equipped and never swaps for the sake of it; if the one you pick is not
levelled, the other gets used rather than nothing.

That these override everything is arithmetic, not preference. Both are instant and free, so
Swiftcast, the MP floor, the hardcast rules and the whole gearset-switching apparatus are answers to
costs they do not have. All of it is skipped rather than consulted.

Neither cares what job you are on, the phantom job grants the action, not your class, so **a
Warrior in Occult Crescent raises**.

### The conditions

The tab shows a checklist rather than one verdict, because "phantom raise unavailable" tells
somebody standing in South Horn nothing and *"you are on Phantom Cannoneer"* tells them what to fix:

```
[x] In Occult Crescent.
[ ] On Phantom Cannoneer, which cannot raise.

Phantom jobs that can raise
[x] Phantom White Mage - level 5, has Occult Raise.
[ ] Phantom Chemist - level 2, Revive needs 3.
```

If nothing qualifies, it falls back to the normal raise settings, so a real White Mage in Occult
Crescent on the wrong phantom job still raises the ordinary way.

### Borrowing a raising job

If you are levelling something that cannot raise, *Change phantom job to raise, out of combat* will
swap to a raising job, raise, and swap back.

**It is off by default and that is deliberate.** The phantom job is the thing you are levelling -
that is what the zone is *for*, and being taken off Phantom Cannoneer mid-grind is a decision you
should make rather than discover.

For the one-off case there are **buttons on the tab** instead: press *Phantom White Mage* or
*Phantom Chemist* to switch right now, and a *Back to …* button appears so it is not a one-way door.
Pressing one stands the automatic errand down rather than fighting it, your choice by hand wins.

Out of combat only, because the game refuses a support job change in combat **and for about four
seconds after it ends**. That settle is not a guess; it is BOCCHI's own number, from its
`PhantomJobChangeGate`, and it exists because `ChangeSupportJob` silently does nothing when refused.

Both switchers, gearset and phantom job, now also finish their trip home when you turn the plugin
off mid-swap, rather than abandoning it where it stood.

## MP

A raise costs **2400 MP** on every job in the game that has one, a quarter of the bar, and
spending it is how a healer ends up unable to heal the people still standing. So below *Minimum MP*
the plugin does not attempt one. The slider will not go below 2400, the cost of one raise: lower is
not a gentler setting but a broken one, since the game refuses the cast below its cost and the
plugin would keep interrupting your rotation to attempt a spell it cannot afford.

The important half is what it does *instead*: **it leaves Rotation Solver Reborn running.** Pausing
somebody's rotation and then declining to cast costs them the GCD *and* leaves the body on the
floor, which is strictly worse than never having got involved. The floor is therefore checked while
the plugin is still deciding whether to interfere, not inside the cast.

Red Mage is the one exception to the number. Without Dualcast or Swiftcast it has to buy the proc
with a Vercure first, at **500 MP**, so its real floor is **2900**. That is added for you rather
than being a second setting, it is the same rule, and the arithmetic is the game's.

Swiftcast is free, so it never moves the threshold. Occult Raise is free, so it ignores it entirely.

## Jobs

WHM/CNJ (Raise), SCH/SMN/ACN (Resurrection), AST (Ascend), SGE (Egeiro) from level 12;
RDM (Verraise) from level 64. In Occult Crescent, Phantom White Mage 4+ or Phantom Chemist 3+
raises on *any* job. Level is the *synced* level, so a 90 synced below the threshold
correctly does nothing. Conjurer and Arcanist fold into White Mage and Summoner in the selector,
since they can raise well before they pick up a job stone.

Red Mage has two routes to an instant Verraise: Swiftcast, or Dualcast bought with a throwaway
Vercure. Swiftcast is preferred because it is free; Vercure costs a GCD and a little MP and is used
whenever Swiftcast is unavailable and Dualcast is not already up. There is no toggle for it, the
alternative is a cast Red Mage is forbidden from starting, so a Red Mage with it off simply stood
there. **Red Mage never hardcasts Verraise**: a ten second cast is not a raise so much as an
intention, and if neither route is available it waits rather than starting one.

That is also why Red Mage is worth switching *to*. Out of combat, with Swiftcast on cooldown, it is
the only job in the game with an instant raise left, which is what *Switch to Red Mage when
Swiftcast is down* buys: a gearset swap and a Vercure instead of sixty seconds of waiting. Out of
combat only, because the game refuses job changes in a fight.

## Being raised

`Accept raises cast on me` presses "yes" on the raise prompt for you, on any job, a Warrior who
will never cast a raise still benefits, which is why it is a setting of its own rather than living
under the job selector. Like every setting here, it only applies while the master switch is on.

It never reads the prompt's
text, that would bake one language into the plugin, and instead insists on the state that makes
the answer unambiguous: **you are dead, and you are carrying the Raise status.** That status exists
only between somebody's cast landing and you answering, and in that window the game asks a corpse
exactly one question.

## Rotation Solver Reborn and BOCCHI

Leaving RSR going while a raise is being set up means it spends the GCD you needed, moves you, or
clips the cast. With *Pause Rotation Solver Reborn while raising* on, AutoRezzer turns RSR off over
IPC when it starts working on a body and turns it back on once the raise resolves or the bodies are
gone. If RSR was already off when the raise started, it is left off.

**RSR has more than one "on", and that is the whole of the BOCCHI story.** Off, Auto, TargetOnly,
Manual, AutoDuty, Henched and PvP are distinct states, and several of them exist precisely because
another plugin is driving. [BOCCHI](https://github.com/OhKannaDuh/BOCCHI)'s Illegal Mode drives RSR
in **Henched**, rotation only, no targeting, while it runs FATEs and critical encounters.

So the naive pause/resume is actively harmful there: coming back in *Auto* hands RSR its own
targeting back in the middle of somebody else's automation, and BOCCHI will not notice, because it
only re-sends a mode when its own desired mode changes. It still thinks RSR is Henched. It is not,
and now two things are choosing targets.

*Resume as* is how that is settled. **Automatic**, the default, resumes into Henched when BOCCHI
is loaded and Auto when it is not. You can pin it to a specific mode if your setup is unusual.

Two things worth being explicit about:

- **AutoRezzer never touches BOCCHI.** Illegal Mode stays exactly as you left it. Only RSR is
  moved, and only while there is a body to pick up.
- **The mode is a setting rather than a reading** because it has to be. RSR's IPC exposes exactly
  one read, `AutorotationActive()`, a bool, and nothing that reports *which* mode it is in.
  BOCCHI exposes no IPC at all. There is genuinely nothing to query, so the mode to come back to is
  decided in advance.

## Statistics

A running count of rezzes cast and rezzes accepted, behind Stats. **Local, stored in your own
config, and shown to nobody**, it exists because it is quietly satisfying to see the number after a
FATE train, not because it is reported anywhere.

It is written to disk the moment each raise lands, and there is no reset button: a running total is
only interesting because it has been running the whole time.

## Attribution and licence

This plugin is **GPLv3**, because parts of it are derived from
[RotationSolverReborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) (GPLv3,
The Combat Reborn Team). See `COPYING`.

Specifically ported, with thanks:

- **`TargetFilter.GetDeath`** → the raisable-body filter in `Core/RezTargeting.cs`. The non-obvious
  parts are all theirs: a corpse that is *moving* has already accepted a raise; a corpse with the
  Raise status already has one inbound; `IsDead` is not enough without `CurrentHp == 0` and
  `IsTargetable`.
- **`DataCenter.CanRaise`** → the job/level gate.
- **`ObjectHelper.CanSee`** → the line-of-sight raycast, including the 2-yalm eye offsets.
- **`ObjectHelper.IsEnemy`** → hostility by asking whether a damage action could legally be aimed at
  the target, rather than trusting `BattleNpcKind`.

**Not** from RotationSolver: the enemy-proximity rule (`IsBodySafeToRaise`). RSR has no
mob-avoidance logic of any kind, one unrelated config slider in 107,000 lines, so that part is
new here.
