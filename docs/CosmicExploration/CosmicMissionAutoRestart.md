# Cosmic Mission Auto-Restart

When you complete a Cosmic Exploration mission, this automatically re-opens the Stellar Missions list and re-accepts the same mission if it's still available - so you can grind the same mission back-to-back without manually reopening and selecting it each time. You still report/turn in the mission yourself; the feature only handles the reopen-and-reaccept afterwards.

Completion is detected from the mission reaching at least a Bronze rank before its active state clears, so a mission you manually **abandon** without completing (rank stays None) is left alone and never restarted. After completion it opens the Stellar Missions list and re-accepts the mission if it's in the currently offered set; the mission board rotates which missions it offers, so if the same one isn't being offered that round the feature logs a note and quietly stops instead of reopening the list forever. The delay between UI actions is configurable.
