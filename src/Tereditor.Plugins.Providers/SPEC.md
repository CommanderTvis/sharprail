---
id: module-plugin-providers
type: module-design
status: active
title: Model providers builtin
parent: group-plugins-agents
---

# Model providers builtin

Settings › Model providers manages the API keys of the model providers Pi and omp use. Enabled by default; not headless
because the keys go into files of the host's operating system. Each agent keeps its own keys, in the file it reads, so nothing is shared
between them except this page and its code.

## Acceptance contract

- Keys only. No subscription or OAuth sign-in is offered or started. In particular Anthropic is offered by API key (Claude Console) with the
  note that a Claude subscription sign-in is not supported here (JetBrains/thinkrail#437). A Claude sign-in Pi already stored is shown as signed in
  with a warning; adding an API key replaces it only when the user does so.
- Pi: `auth.json` in the agent directory (`PI_CODING_AGENT_DIR`, else `~/.pi/agent`): `{"<provider id>": {"type": "api_key", "key": "..."}}`.
  Other entries, including sign-ins, are kept as they are. The catalog is the table in Pi's `docs/providers.md`.
- omp: `<omp home>/.omp/profiles/tereditor/agent/.env` (`TEREDITOR_OMP_HOME` moves the home, as for the omp plugin), one `NAME=value` line per
  provider variable (https://omp.sh/docs/env); omp reads it for launches with tereditor's profile. Only the named variable's line is replaced or
  removed; other lines and comments stay. omp's own credential database is never written. The catalog is the variable table on that page.
- A provider the catalog does not list is added by hand with an id, an optional name and, for omp (and optionally Pi), the variable that holds its
  key. Custom providers are remembered in the plugin's state without their keys; removing the key forgets the provider.
- A key is validated (one line, not empty), written once and never read back: a report carries ids, names, variables and where each credential
  comes from (stored key, sign-in, the host's environment), never a key. Files are rewritten whole through a temporary file readable by the owner
  only, then moved over the original. Keys are not logged.
- The page: a Pi / omp switch, the file the keys live in, Connected rows (Replace key, Remove), Available rows (Add key) and the custom form;
  keys are typed into masked fields and cleared as soon as they are sent. Reads and writes run off the UI thread on the host.

## Checks

`--providers` (gate case `providers`): the manifest; Pi: sign-in preserved and flagged for Anthropic, keys added, replaced and removed without
leaking into a report, owner-only permissions, custom providers; omp: the `.env` line replaced or removed with other lines kept, quoting, catalog
variables, custom variables and their validation. The page itself is not covered by a check.
