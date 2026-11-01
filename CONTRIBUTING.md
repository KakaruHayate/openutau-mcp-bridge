# Contributing

## Repository hygiene

This repository is public. Do **not** commit:

- absolute paths from any machine (checkouts, plugin folders, build outputs),
- usernames, machine names, or log excerpts that contain them,
- anything copied from a private conversation, ticket, or chat transcript,
- credentials, tokens, or the contents of `mcp-bridge-prompt.txt` from a real machine.

Build inputs that are machine-specific go through MSBuild properties or a
`Directory.Build.local.props` file, which `.gitignore` excludes. Before pushing:

```sh
git grep -n -I -E "([A-Za-z]:\\\\|/Users/|/home/)" || true
```

## Code rules

- The bridge is a guest in OpenUtau's process. It must not crash, block the UI thread,
  or write to the project unless a tool call asked for it.
- Prefer the host's own revision (`DocManager.Revision`) over inventing counters.
- Anything version-sensitive goes through `CapabilityProbe`; never hard-reference a
  member that may disappear.
- Diagnostics never throw: every write to the status or boot file is best-effort.
- Keep the tool surface small. A tool is added only when an agent cannot reasonably do
  the job with the existing ones.

## Commit messages

Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`). No AI-attribution trailers.
