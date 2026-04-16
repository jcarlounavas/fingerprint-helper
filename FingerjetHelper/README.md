# FingerjetHelper

External helper service for **image-based fingerprint matching** used by the Laravel event-ms app. Capture and storage stay in the app; this service only compares two fingerprint images (e.g. live capture vs stored PNG) and returns match + score.

## Contract (Laravel app integration)

| Item | Detail |
|------|--------|
| **Endpoint** | `POST {helper_url}/Match/verify` |
| **Request** | JSON: `probeImageBase64`, `referenceImageBase64` — raw base64 (no `data:image/...` prefix). Optional: `threshold` (number), `autoInvert` (bool?). |
| **Response** | JSON: `match` (boolean), `score` (number). On bad input or decode errors, still 200 with `match: false`, `score: 0` so the app’s helper-status check sees the service as reachable. |
| **Config in app** | `FINGERJET_HELPER_URL` in `.env` → `config/biometrics.php` → `helper_url`. |

## When the app uses the helper

- **Verification**: User has a stored image; client sends live capture → backend calls helper (probe = live, reference = stored PNG).
- **Identify (attendance)**: One probe vs many references. Single request to `POST /Match/identify`; returns `match`, `bestMatchId`, `score`. Request: `probeImageBase64`, `references`: array of `{ id, imageBase64 }`, optional `threshold`. Max 10,000 references per request.
- **Match-two-images**: Backend sends both images to the helper and returns the result.
- **Helper-status**: App POSTs to `/Match/verify` with a small dummy payload; this helper returns 200 with `match`/`score` so the app can report “reachable”.

If the helper is missing or returns 5xx, the app falls back to exact template string match (unreliable).

## Optional health endpoint

- `GET {helper_url}/Match/status` — returns 200 and `{ "status": "ok", "service": "FingerjetHelper" }`. The app currently uses POST to `/Match/verify` for status; you can switch to this GET endpoint later if desired.

## Run

- HTTP: `http://localhost:5259` (see `launchSettings.json`).
- Set in Laravel `.env`: `FINGERJET_HELPER_URL=http://localhost:5259` (or the URL where this helper is hosted).

## Stack

- .NET (ASP.NET Core), SourceAFIS for matching, `System.Drawing.Common` for image handling. Accepts PNG/JPEG; converts to grayscale and runs SourceAFIS. Default match threshold: 10.0 (overridable via request `threshold`).
