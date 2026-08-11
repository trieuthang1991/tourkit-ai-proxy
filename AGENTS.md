<!-- codegraph:start -->
# CodeGraph — Code Intelligence

This project is indexed by **CodeGraph** (`@colbymchenry/codegraph`) — a local SQLite knowledge graph in `.codegraph/` (no embeddings, no API key, fully offline). The index **auto-syncs as you edit**, so it's normally fresh with no manual re-index step. Use it to understand code, assess impact, and navigate safely before editing.

Two ways in:
- **MCP tool** `mcp__codegraph__codegraph_explore` — one call returns the relevant symbols' verbatim, line-numbered source **plus** their call paths **plus** a blast-radius summary (replaces a grep + Read loop).
- **CLI** `codegraph <cmd>` — `explore` / `query` / `node` / `callers` / `callees` / `impact` / `status`.

## Always Do

- **Assess blast radius before editing any symbol.** Run `codegraph impact <Symbol>` (or `codegraph_explore`) and report the direct callers + affected symbols before modifying a function/class/method. Warn the user when the radius is wide.
- When exploring unfamiliar code, use `codegraph explore "<concept>"` (or the `codegraph_explore` MCP tool) instead of grepping — it returns the relevant symbols' source + call paths in one shot.
- For a single symbol's 360° view (source + callers/callees), use `codegraph node <Symbol>`.

## When Debugging

1. `codegraph explore "<error or symptom>"` — surface the relevant symbols + call paths.
2. `codegraph node <suspect function>` — its source, callers, and callees.
3. `codegraph callers <Symbol>` / `codegraph callees <Symbol>` — walk the call graph in either direction.

## When Refactoring

- **Before moving/renaming**: `codegraph impact <Symbol>` to list every caller. CodeGraph has **no automatic safe-rename** — update the callers it reports by hand, then re-check.
- The index auto-syncs; if a result looks stale right after a large change, force it with `codegraph sync` (incremental) or `codegraph index` (full rebuild).

## Never Do

- NEVER edit a function/class/method without first checking `codegraph impact` (or `codegraph_explore`) on it.
- NEVER rename symbols with blind find-and-replace — list callers with `codegraph impact` first, then update each.

## Tools Quick Reference

| Command | When to use |
|---------|-------------|
| `codegraph explore "<q>"` | Answer almost any code question in one call (source + call paths + blast radius) |
| `codegraph query <name>` | Find a symbol by name |
| `codegraph node <sym\|file>` | One symbol's source + callers/callees, or a file with its dependents |
| `codegraph callers <sym>` | Who calls this |
| `codegraph callees <sym>` | What this calls |
| `codegraph impact <sym>` | Blast radius before editing |
| `codegraph status` | Index stats / freshness |

## Keeping the Index Fresh

CodeGraph auto-syncs via its background daemon as files change — there is **no** PostToolUse re-index hook and none is needed. To force it: `codegraph sync` (incremental) or `codegraph index` (full rebuild). Inspect state with `codegraph status`.
<!-- codegraph:end -->
