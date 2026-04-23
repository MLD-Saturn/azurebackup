# Copilot Instructions

## Project Guidelines

* Avoid multi-line PowerShell commands in terminal.

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

- Use `git commit -m "..."` with one or more `-m` flags, one per
  paragraph. Never write the commit message to a file. Never use
  `git commit -F <file>`.
- Never include emoji or non-ASCII characters in the commit message.
- Escape `"` and `\` properly so the git invocation parses cleanly.
- Run the entire `git add` + `git commit` sequence on a single line,
  using `;` to chain them. Length and readability are not concerns.
