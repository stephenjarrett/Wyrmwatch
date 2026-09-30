The default branch is master. Use sjarrett/ for feature branches unless explicitly instructed otherwise.

Never use a real game installation for tests. All integration fixtures must live in unique temporary directories. Opening the app must never adopt an unconfigured server. Installation, game settings changes, and server start/stop require a user action or explicitly enabled maintenance. Automatic maintenance is opt-in. Unknown player activity must defer automatic maintenance.

Keep UI work on the dispatcher, I/O off the dispatcher, and update existing controls without recreating focused forms. Keep tests focused on MVP safeguards and smoke flows. Run the checks and build the app before committing. Feature branches use sjarrett/.
