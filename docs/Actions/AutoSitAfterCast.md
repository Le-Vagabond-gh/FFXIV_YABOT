# Auto-Sit After Casting

When you start fishing (FSH "Cast", action 289), the plugin waits a configurable delay (default 3 seconds) and then runs `/sit`, so your character fishes from the stool sitting animation.

It sits **once per fishing session** and re-arms only when fishing actually ends. This avoids the toggle problem: `/sit` flips sit/stand, and since you stay seated on the stool across recasts, sitting again would just stand you back up. (The character's mode reads as `Gathering` whether standing or seated, so the sit state can't be detected directly - hence the once-per-session approach.)

A **chance slider (% of casts)** rolls per cast, just for fun. A losing cast simply tries again on the next cast; once you've sat, it's done until the session ends. Set it to 100 to always sit on the first cast.

The pending sit is dropped if you stop fishing before the delay elapses. Tune the delay to taste and test with your fishing loop (e.g. AutoHook auto-recast).
