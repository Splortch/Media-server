# Contributing

Start with [development setup](docs/development/SETUP.md). Find an issue marked `good first issue`, discuss the intended behavior, then create a feature branch such as `feat/player-errors`.

1. Make a small change with clear behavior and meaningful tests.
2. Run the build, tests, lint and formatting commands in the README.
3. Update documentation and feature status when behavior changes.
4. Commit using `feat:`, `fix:`, `docs:`, `test:`, `refactor:` or `chore:`.
5. Open a pull request describing the problem, resulting behavior and checks actually run.
6. Ask the other maintainer to review. Prefer squash merges and delete merged branches.

Backend/media maintainer: scanning, media access, authentication, schema migrations, FFprobe integration and release builds. Frontend/experience maintainer: setup, browsing, accessibility, player behavior and browser tests. Both own interfaces, dependency updates and security.

Never submit personal media, production databases, credentials, setup tokens, private logs or API keys. Do not run personal or untrusted media in tests. New scanning code must never modify originals. Add an EF Core migration for schema changes; do not replace migrations with `EnsureCreated`.

No mandatory external account is permitted in core playback. Discuss architectural changes before introducing services or infrastructure. Contributions are provided under Apache-2.0 without a CLA requirement.

For approved AI implementation tasks, use the [issue-to-PR operating guide](docs/development/AUTONOMY.md).
A maintainer approves a focused issue with `/agent implement`; the agent proposes a PR and a human
reviews it. Activation requires repository configuration. The agent never merges automatically.

Repository administrators: review and activate the [main branch ruleset](docs/development/BRANCH-RULESET.md).
