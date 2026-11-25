# ADR 0002 — T08 port notes (contract_lab → Bridge.Relay)

Date: 2026-11-13. Status: accepted (T08 PASS, 16/16 tests).

## What was ported
`CommandJournal` + `CommandDispatcher` in `src/Bridge.Relay/` mirror
`starter/contract_lab/bridge_contract.py` (ContractEngine) case-for-case:
same SQLite schema, same restart recovery, same duplicate/conflict
rules, same outcome mapping, same monotonic-deadline rule.

## Deviations from the plan text (02_Execution_Plan.md T08)
1. **Journal status at insert: `executing`, not `accepted`.** The plan
   says "在首次副作用前落盘accepted", but the executable reference
   (contract_lab, the authority T08 names) INSERTs `'executing'` before
   the side effect, and its restart recovery handles both
   `('accepted','executing')`. The C# port follows the lab exactly.
   No `accepted` row is ever written; the state machine still starts
   from the journaled row before any effect.
2. **Validator error codes differ from the lab's.** Bridge.Contracts
   uses fine-grained codes (BAD_TTL, BAD_TEXT, …); the dispatcher maps
   them to the lab wire codes (INVALID_COMMAND_TTL, INVALID_PAYLOAD,
   COMMAND_EXPIRED_OR_FUTURE, …) at the dispatch boundary, so the wire
   contract matches the lab.
3. **Wall-clock expiry is deferred past the duplicate check.**
   Bridge.Contracts' validator rejects expired envelopes eagerly, but
   the lab only checks TTL at validation and enforces expiry after the
   duplicate lookup (duplicate-after-expiry returns the stored receipt).
   The dispatcher catches EXPIRED/CLOCK_SKEW, re-validates against the
   envelope's own issued_at, and lets the locked section enforce
   expiry — verified by `DuplicateAfterExpiryReturnsStatusWithoutEffect`.

## Test parity
16 C# tests mirror lab cases with identical fixture numbers
(click (100,50) → native (-1720,100); edges (0,0)→(-1920,0),
(959,539)→(-2,1078)). Reference baseline re-run: contract_lab 35/35 OK.
