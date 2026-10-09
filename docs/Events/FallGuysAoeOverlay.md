# Fall Guys AoE Markers

On stages 2 and 3 of the Fall Guys collaboration event, outlines the obstacles that are about to go off (about 2.5 seconds ahead).

- **Markers**: outlines for upcoming obstacles with a countdown (seconds until dangerous) in the middle: green while safe, yellow half a second before it gets dangerous, filled red while dangerous.
- **Safety margins**: hit predictions treat every obstacle as half a unit bigger than drawn and as dangerous slightly before it visibly goes off, because the game checks hits against where it saw you a moment earlier.
- **Stage 2 (crystal courier)**: covers the sweeping bars, the squares and the sliding blocks in all three lanes. Each obstacle gets a marker once it has gone off once or twice and its rhythm is known.
- **Stage 3**: also draws a route from your character through the lanes to the finish. It appears once the plugin has seen the opening exaflares and the rect lanes fire, and updates as you move down the course. Each stretch of the route is green when walking it now, without stopping, is clear, and red when an obstacle would get you along the way - wait for it to turn green.

Stage 3 is ported from [awgil/ffxiv_vfallguy](https://github.com/awgil/ffxiv_vfallguy) by veyn.
