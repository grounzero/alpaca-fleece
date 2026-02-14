# Contributing

Developer setup
----------------

This repository uses a local virtual environment and `pre-commit` hooks to ensure consistent formatting,
static typing and tests before pushing changes.

Run the one-line helper to create a venv and install developer tooling:

```bash
make dev-setup
```

Then activate the venv in your shell:

```bash
source .venv/bin/activate
```

Run the checks locally once (recommended) before committing:

```bash
pre-commit run --all-files
```

Notes
-----

- `pre-commit install` is run automatically by `make dev-setup`, but you must run it once per clone if you
  skip `make dev-setup`.
- Activation (`source .venv/bin/activate`) must be done in each new shell session where you work on the repo.
- CI enforces checks for PRs targeting `main` via `/.github/workflows/ci.yml`.

If you prefer a different Python management tool (e.g. `pipx` or `pyenv`), adapt the steps above but ensure
the venv is active before running `pre-commit install` so the hooks can be installed into the created venv.
