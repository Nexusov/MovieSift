# Contributor instructions for coding agents

Read [CONTRIBUTING](CONTRIBUTING.md), [architecture](docs/ARCHITECTURE.md) and the relevant [preservation contract](docs/SUPPORTED_FORMATS.md) before changing the application. Desktop changes must follow [UI standards](docs/UI_STANDARDS.md), including shared controls, bounded scrolling, stable item identity, portable preferences and honest progress.

Keep the existing dependency/tool pins, project boundaries, explicit approvals, source checks, owned worker lifecycle and no-overwrite publication. Do not infer native desktop acceptance from headless tests. Run the relevant focused tests and preserve the full required Windows/Linux CI gates. Keep generated media, tools, archives and private paths out of Git.
