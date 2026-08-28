# AutoRezzer

A quick, handy tool for the raising half of a fight: it casts raises on the people around you, and
it accepts the ones cast on you.

One toggle. When it is on and you are on a job that can raise, at a level that can raise, any
raisable body within 30 yalms gets raised — Swiftcast first when it is up — unless there are live
enemies standing on it. Separately, and whether or not that switch is on, it answers the raise
prompt when *you* are the one on the floor.

Built mostly for **Occult Crescent and FATEs**, where people go down constantly, half of them are
strangers, and nobody is keeping track of who still needs picking up. It works anywhere, but that is
the case it is shaped around.

This is a plugin for [Dalamud](https://github.com/goatcorp/Dalamud) (the FFXIV plugin framework used
with XIVLauncher).

## Installation

1. Install [XIVLauncher](https://goatcorp.github.io/) and enable Dalamud.
2. Open Dalamud settings (`/xlsettings`) → **Experimental** → **Custom Plugin Repositories**.
3. Add this repository URL and press **Save**:
   ```
   https://raw.githubusercontent.com/Marobotic/PfPresets/main/repo/repo.json
   ```
4. Open the plugin installer (`/xlplugins`), search for **AutoRezzer**, and install it.

It ships **switched off**. Open it with `/autorez` and turn it on when you want it.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Enabled | off | The switch. Off on a fresh install on purpose. |
| Enemy distance | 10 yalms | A body with a live enemy closer than this is left alone. `0` disables the check. |
| Raise delay | 2.5 s | How long somebody must have been down first. |
| Use Swiftcast | on | Spends Swiftcast so the raise is instant. |
| Red Mage: Vercure for Dualcast | on | When Swiftcast is down, throwaway Vercure to proc Dualcast so Verraise is still instant. |
| Pause Rotation Solver Reborn while raising | on | Stops RSR from eating the GCD out from under a raise. Does nothing if you don't run RSR. |
| Accept raises cast on me | on | Answers the raise prompt when *you* are the one down. Every job; works with the main switch off. |
| Raise on another job | off | Gearset to switch to when someone dies. It raises, then switches back. |
| Switch back afterwards | on | Return to the job you were on once the raise lands. |
| Raise people outside my party | on | Off limits it to party and alliance. |
| Skip Brink of Death | on | Don't re-raise someone who just got up. |
| Announce in chat | off | One line per raise. |

`/autorez` (or `/az`) opens the window. `on`, `off` and `toggle` work as arguments on either.

There is a **server bar entry** reading `Rez: on` / `Rez: off` — click it to toggle without opening
anything. That is the point of it: this plugin acts on its own, and what you want the moment you
notice it acting is an off switch, not a window.

## Switching job to raise

Pick a gearset under *Raise on another job* and the plugin will, when somebody dies and you are on a
job that cannot raise, switch to that gearset, raise them, and switch back.

A **gearset, not a job** — equipping a gearset is the only way the game changes job, and naming the
job alone would mean guessing which of your three White Mage sets was meant.

Two things worth knowing:

- **The game refuses job changes in combat.** So this only ever happens once a fight is over, which
  is the corpse-run case it is for. It is not a mid-fight rescue.
- **It gives up rather than stranding you.** Every step has a timeout, and the worst case is always
  "go back to the job you were on", never "sit here as a Sage". If somebody else raises the body
  while your gear is still swapping, it turns around without casting.

## Jobs

WHM/CNJ (Raise), SCH/SMN/ACN (Resurrection), AST (Ascend), SGE (Egeiro) from level 12;
RDM (Verraise) from level 64. Level is the *synced* level, so a 90 synced below the threshold
correctly does nothing. Conjurer and Arcanist fold into White Mage and Summoner in the selector,
since they can raise well before they pick up a job stone.

Red Mage has two routes to an instant Verraise: Swiftcast, or Dualcast bought with a throwaway
Vercure. Swiftcast is preferred because it is free; Vercure costs a GCD and a little MP and is only
used when Swiftcast is unavailable and Dualcast is not already up. **Red Mage never hardcasts
Verraise** — a ten second cast is not a raise so much as an intention, and if neither route is
available it waits rather than starting one.

## Being raised

`Accept raises cast on me` presses "yes" on the raise prompt for you. It never reads the prompt's
text - that would bake one language into the plugin - and instead insists on the state that makes
the answer unambiguous: **you are dead, and you are carrying the Raise status.** That status exists
only between somebody's cast landing and you answering, and in that window the game asks a corpse
exactly one question.

## Rotation Solver Reborn

If you run [RSR](https://github.com/FFXIV-CombatReborn/RotationSolverReborn), leaving it going while
a raise is being set up means it spends the GCD you needed, moves you, or clips the cast. With
*Pause Rotation Solver Reborn while raising* on, AutoRezzer sends `/rotation Off` when it starts
working on a body and `/rotation Auto` once the raise resolves or the bodies are gone.

It only ever restores what it found: if RSR was already off when the raise started, it is left off.
If you do not have RSR installed, the setting does nothing.

## Statistics

The window keeps a running count of raises cast, raises accepted, and a per-job breakdown of what
you have picked people up as. **Local, stored in your own config, and shown to nobody** — it exists
because it is quietly satisfying to see the number after a FATE train, not because it is reported
anywhere. *Reset Statistics* zeroes it.

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
mob-avoidance logic of any kind — one unrelated config slider in 107,000 lines — so that part is
new here.
