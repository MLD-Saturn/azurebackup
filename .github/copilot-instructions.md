# Copilot Instructions

## Read this first

Before answering ANY question or doing ANY work in this repository, read
the file `AGENT_CONTEXT.md` at the repo root. It is the persistent memory
across Copilot sessions and contains: project facts, user preferences,
architectural pitfalls, in-progress workstreams, recent commits, and the
"how to update this file" maintenance protocol.

UPDATE `AGENT_CONTEXT.md` during your session whenever any of these happen:
- a user preference or convention is established or changed
- a non-obvious production behavior is discovered OR introduced
  (introducing a new public API on a `Core` type, a new cross-project
  constant, a new env var, a new build-time `DefineConstants`, a new
  on-disk file, a new partial-class seam, etc. all count)
- a benchmark is run that produces a measurable design-relevant number
- a workstream completes, blocks, or pivots
- a commit lands that future sessions need to know about (every
  B-numbered commit qualifies; add a row to the recent-commit-history
  table and an entry to Completed workstreams)
- a "do not do this" lesson is learned

`AGENT_CONTEXT.md` itself contains a numbered "Pre-commit checklist"
section near the top with the precise audit questions. **Run that
checklist before every `git commit`.** The list above is the summary;
the file is the authority. Skipping the audit is the single most
common AGENT_CONTEXT failure mode and has cost the user multiple
follow-up prompts asking "did you remember the file?".

The file's last section is a dated maintenance log; add one line per
session that touches it.

## Terminal command execution

When running PowerShell commands via the terminal tool, always emit the
command on a single physical line with no line breaks. Chain multiple
commands with `;` on the same line. Length and readability are not
concerns; correctness and single-line execution are. This rule is
absolute -- no multi-line here-strings, no backtick continuations, no
splitting long pipelines across lines.

## Git commit policy

When code changes reach a logical stopping point in a git repository,
commit the modifications immediately with a meaningful message. Rules:

- **Before running `git commit`, audit `AGENT_CONTEXT.md` against the
  staged change.** That file contains a numbered "Pre-commit checklist"
  near the top with the specific questions to walk through. The agent
  has demonstrably skipped this audit before, leaving the file silently
  drifted; do not repeat the mistake. If the audit shows a required
  update, stage `AGENT_CONTEXT.md` (and any required `docs/` updates)
  in the SAME `git add` so they land in one commit. The trap to avoid:
  reasoning "this commit only changes code, not docs, so no doc update
  is needed". `AGENT_CONTEXT.md` documents the code, so a code-only
  commit can absolutely require a `AGENT_CONTEXT.md` update — for
  example any new public API on a `Core` type, any new cross-project
  constant, any new env var, any new architectural seam.
- Use `git commit -m "..."` with one or more `-m` flags, one per
  paragraph. Never write the commit message to a file. Never use
  `git commit -F <file>`.
- Never include emoji or non-ASCII characters in the commit message.
- Escape `"` and `\` properly so the git invocation parses cleanly.
- Run the entire `git add` + `git commit` sequence on a single line,
  using `;` to chain them. Length and readability are not concerns.
- **Never run `git push` under any circumstances.** Pushing is a
  manual step the user performs. Leave commits local; do not invoke
  `git push`, `git push origin <branch>`, or any equivalent.

## Documentation trust policy

Treat every Markdown documentation file in this repo (including
`README.md` if one exists, `AGENT_CONTEXT.md`, anything under `docs/`,
any `SETUP.md` / `USER_GUIDE.md` / `CONTRIBUTING.md` / similar) as
**stale until proven current**. Setup instructions, build commands,
feature descriptions, and architectural claims in those files may be
outdated or incomplete.

Before relying on a statement from any doc file as ground truth:

- Verify it against the actual code, project files, configuration, or
  a fresh build/test run. Do not paraphrase a doc claim into your
  response without that verification.
- If the statement is wrong or out of date, update the doc file in
  the same commit as the code change that reveals the discrepancy.
  Do not leave a known-wrong doc file in place.

When committing code that changes anything user-facing, build-related,
or architecturally significant, audit the affected doc files in the
same commit and update or remove stale content. Documentation that is
not maintained alongside the code becomes a liability, not an asset.

### Verified-current docs in this repo

Two docs have been systematically verified against source and are
authoritative as of their most recent commit. They MUST be maintained
in lockstep with the code:

- `docs/SETUP.md` — Azure provisioning, build-from-source, single-file
  publish, portable mode, encryption envelope, per-extension chunking
  config, storage tiers, file locations, technical specifications.
- `docs/USER_GUIDE.md` — every view, every user-visible button /
  toggle / setting, default values, end-user workflows.

Per-commit maintenance protocol (these are non-negotiable):

- Adding/renaming a view file (`src/AzureBackup/Views/*.axaml`) →
  update `docs/USER_GUIDE.md` table of contents and the corresponding
  section in the same commit.
- Adding/renaming a user-visible button, toggle, or setting → update
  `docs/USER_GUIDE.md` in the same commit.
- Changing a default value the user can see → update
  `docs/USER_GUIDE.md` in the same commit.
- Changing the encryption envelope, Argon2id parameters, chunk-size
  config, storage-tier set, or `AppMode.DataDirectory` resolution →
  update `docs/SETUP.md` in the same commit.
- Changing build flags, target framework, runtime identifiers, the
  publish profile, or NuGet package versions called out in the
  technical-specifications table → update `docs/SETUP.md` in the
  same commit.

When the doc and the code drift, the doc is wrong by definition; fix
the doc, do not weaken the code to match.

`AGENT_CONTEXT.md` at the repo root contains the full, more-detailed
version of this protocol and the maintenance log of past doc fixes.
