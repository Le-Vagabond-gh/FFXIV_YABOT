# Login Gearset Sync

After you log in, this compares the job you're actually on against the job of the gearset the game currently considers active. If they don't match, it equips the lowest-numbered gearset belonging to your current job.

Your job is never changed - only the active gearset is corrected, so nothing more happens than re-applying that job's own set.

The active gearset drifts out of sync in two common ways:

- You changed job without going through a gearset (armoury chest, job stone, another plugin), so the active gearset stayed on whatever you last equipped.
- You play the same character from more than one computer. The active gearset is remembered per machine, not per character, so after switching computers it's whatever that machine last had equipped - usually a completely different job.

The mismatch matters because glamour plate links, other plugins, and the gearset window's own highlight all read the active gearset.

## When it runs

Once per login, a few seconds after you regain control of your character. The delay (default 5 seconds, adjustable up to 30) exists so inn wake-up animations and similar intros finish first. If you enable the feature while already logged in, the check runs straight away.

## Settings

- **Delay after regaining control** - how long to wait before the check. Raise it if your character is still animating when the switch fires.
- **Announce the switch in chat** - prints a line naming the gearset it switched to. On by default.
- **Excluded gearsets** - tick any gearset that should never be chosen as the switch target. Useful when a job's low-numbered slot holds a set you don't want auto-equipped (a duty-specific or Occult Crescent set, for example) - the next matching gearset for that job is used instead.

If every gearset for your current job is excluded, or you have none at all, nothing is changed.
