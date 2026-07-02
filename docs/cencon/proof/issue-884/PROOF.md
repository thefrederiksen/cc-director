# #884 credit balance + per-action cost - proof (2026-07-02)

## What shipped
- Gateway proxy `GET /account/credits` (`AccountCreditsEndpoint` + `AccountCreditsClient`): reads the
  balance from the cloud with the Gateway's own account JWT and returns a token-free DTO
  (`AccountCreditsDto { SignedIn, BalanceMicros, LastDebitMicros }`). Signed-out -> explicit
  `signedIn:false`; unreachable cloud -> clear 502. Mirrors the well-tested `/account/devices` proxy.
- Settings account section (issue #886 tab): shows `Credits: $X.XX` and a non-blocking low-balance
  notice with an add-credits link below a $1 threshold.
- Per-action cost: after a hosted (DevThrottle) "Test it" transcription the balance refreshes and the
  cost is shown ("Transcribed - cost $X. Balance $Y."); a BYO/OpenAI action shows no DevThrottle charge.

## Live cloud evidence
`GET https://devthrottle.com/api/v1/account/credits` (account JWT) -> **HTTP 200**
`{"data":{"balance_micros":9993644,"transactions":[{"kind":"debit","amount_micros":-556,...}, ...]}}`.
So the balance is real and the debit magnitude is the per-action cost. The UI renders that exact
balance as **$9.99** (screenshot `settings-account-balance.png`, `balanceMicros=9993644`).

## Tests
`AccountCreditsClientTests` (8): parses the live shape (balance + ledger), an empty ledger, throws on a
malformed shape, bears the JWT, hits `/api/v1/account/credits`, throws on non-2xx and on a missing token.

## Deferred
Usage-graph / month breakdown stays on the website (out of scope per the issue).
