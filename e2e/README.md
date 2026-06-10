# E2E Tests — TourKit AI Proxy

Playwright (Node-based) tests for frontend UI flows + API smoke checks.

## Setup

```bash
cd e2e
npm install
npx playwright install chromium
```

## Run

```bash
# Need proxy running ở localhost:5080 trước
cd .. && dotnet run --project TourkitAiProxy.csproj &

cd e2e
npm test                   # All tests
npm run test:headed        # See browser
npm run test:ui            # Interactive UI mode
npm run report             # View last HTML report
```

## Test files

| File | Coverage |
|---|---|
| `01-smoke.spec.js` | 8 page load + JS error + 5xx check |
| `02-assistant-suggestions.spec.js` | 4 quick chips + toggle + 23 chip expand + SVG icon non-empty + click chip dispatch |
| `03-home-logout.spec.js` | Logout button + greeting + search |
| `04-customers-deals.spec.js` | List load + PageHero + checkbox + auto toggle |
| `05-api-direct.spec.js` | Providers list + Session check + tool catalog |

## Session config

Tests dùng session `5294b8ec7d8f4e12bec4b44334946e1b` từ `data/tk-sessions.json`.
Nếu session expire, login lại qua `/api/v1/login`.
