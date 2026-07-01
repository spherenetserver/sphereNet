# Source-X Parity Matrix

Measurement backbone for the parity roadmap (Faz 0). The roadmap's premise is that
the largest score gains come from locking the Source-X **behaviour surface** —
`SERV.*` verbs, object verbs/properties, and triggers — rather than adding isolated
features. This document turns the otherwise-subjective category scores into an
itemised status list with the code location for each entry, so later phases target
concrete gaps instead of guesses.

## Status legend

| Status | Meaning |
|---|---|
| **Implemented** | Reachable from scripts with Source-X-equivalent behaviour. |
| **Partial** | Works on one surface (e.g. admin console) but not all (e.g. not from `SERV.*`), or behaviour is narrowed. |
| **Stub** | Recognised but returns a placeholder / no-op. |
| **Missing** | Not handled anywhere. |
| **NotApplicable** | Source-X feature with no meaning in SphereNet's design. |

Each phase should keep this table in sync and ideally back it with a guardrail test
(see `ParityMatrixServTests` for the SERV list/var primitives, and
`TriggerCoverageGuardrailTests` for the trigger surface).

---

## SERV.* world-ops verbs (Faz 1 focus list)

Dispatch: script reads route through `Program.ResolveServerProperty`
(`src/SphereNet.Server/Program.Scripting.cs`); the admin console routes through
`AdminCommandProcessor` (`src/SphereNet.Server/Admin/AdminCommandProcessor.cs`).

| Verb | Status | Where | Notes |
|---|---|---|---|
| `RESPAWN` | Implemented | `HandleServRespawn` → `RespawnAllSpawners` | Wave 197 — was admin-console only. |
| `RESTOCK` | Implemented | `HandleServRestock` | Wave 197 — was admin-console only. |
| `CLEARLISTS` | Implemented | `HandleServClearLists` → `GameWorld.ClearGlobalLists` | Wave 197 — new primitive; mirrors `CLEARVARS`. Optional `[prefix]`. |
| `VARLIST` | Implemented | `HandleServVarList` | Wave 197 — dumps `VAR.*` to the server log. Optional `[prefix]`. Caller-console routing deferred. |
| `PRINTLISTS` | Implemented | `HandleServPrintLists` | Wave 197 — dumps list names + sizes to the server log. Caller-console routing deferred. |
| `CLEARVARS` | Implemented | `_CLEARVARS=` → `GameWorld.ClearGlobalVars` | Pre-existing. |
| `ACCOUNT.<name>[.prop]` | Implemented | `ResolveServAccount` | Name lookup + sub-property read. |
| `ACCOUNT.<n>[.prop]` | Implemented | `ResolveServAccount` → `AccountManager.GetByIndex` | Wave 200 — indexed access (stable name order); was stubbed to "0". |
| `INFORMATION` | Partial | `AdminCommandProcessor` `INFORMATION` | Admin console only — no `SERV.INFORMATION` script read yet. |
| `GARBAGE` | Partial | `AdminCommandProcessor` `GARBAGE` | Admin console only. |
| `SHRINKMEM` | Partial | (≈ `GARBAGE`) | No dedicated script/console verb; GC reachable via `GARBAGE`. |
| `BLOCKIP` / `UNBLOCKIP` | Partial | `AdminCommandProcessor` | Admin console only — not exposed to scripts (intentional: security surface). |
| `EXPORT` / `IMPORT` / `RESTORE` | Missing | — | World-ops long tail (Faz 4); object serialisation to/from `.scp`. |
| `SAVESTATICS` | Missing | — | Faz 4 (static world export). |
| `LOAD` | Missing | — | Faz 4 (load a `.scp` at runtime). |
| `SECURE` | Missing | — | Server console security toggle. |

### Already-implemented SERV.* surface (not on the Faz-1 list)

Confirmed live in `ResolveServerProperty`: read-only stats (`CLIENTS`, `ACCOUNTS`,
`CHARS`, `ITEMS`, `VERSION`, `SERVNAME`), time (`TIME`, `TIMEUP`, `RTIME`, `RTICKS`,
`TICKPERIOD`), `SAVECOUNT`, `MEM`, `REGEN0-3`, `SEASON`/`SEASONMODE`, feature flags;
lookups `MAP*`, `SKILL.n`, `CHARDEF.`, `ITEMDEF.`, `AREA.`, `MULTIDEF.`, `LIST.`,
`DEFLIST.`, `GMPAGE.`, `ISEVENT.`, `ACCOUNT.n`, `LOOKUPSKILL`; var/obj access
`VAR.`/`VAR0.`, `OBJ`, `NEW`, `UID.`, `DEFMSG.`; write verbs `_SET_*`, `NEWDUPE`,
`ALLCLIENTS`, `WRITEFILE`, `LOG`, `RESYNC`, `SAVE`, `SHUTDOWN`.

---

## Triggers (Faz 2)

The trigger surface is measured and regression-guarded by
`src/SphereNet.Tests/TriggerCoverageGuardrailTests.cs`, which recomputes the
"defined but not fired" set from source on every run. Outstanding (infrastructure-
gated) entries: char `NPCSeeWantItem`, `UserMailBag`; item `Level`, `Complete`, and
the four champion-altar candle triggers.

Per-trigger **arg contract** (`SRC`, `ARGO`, `ACT`, `ARGN1/2/3`, `ARGS`, `LOCAL`,
`RETURN 0/1`, arg mutation) — the Faz 2 core:

| Aspect | Status | Notes |
|---|---|---|
| `ARGN1`/`ARGN2` seed + read | Implemented | via `WrapArgs`. |
| `ARGN3` seed + read | Implemented | Wave 202 — `WrapArgs` never seeded `Number3`, so `<ARGN3>` read 0 (e.g. `@DropOn_*` drop-Z). |
| `ARGN1/2/3` mutation (`ARGN3=x`) | Implemented | Wave 202 — the interpreter had no ARGN assignment path, so scripts could not modify trigger numbers; `RunWrapped` copies the mutation back. |
| ARGN write-back from every trigger source | Implemented | Wave 203 — item `ITEMDEF`/`TEVENTS`/`TYPEDEF`, region/room and global events bypassed `RunWrapped`, so their ARGN mutations were dropped; all firing paths now go through it. |
| `ARGS` seed + read | Implemented | via `WrapArgs` (constructor). |
| `ARGS` mutation (`ARGS=text`) | Implemented | Wave 204 — the string arg was read-only (no assignment path, no copy-back); now writable and copied back. |
| `@Speech` text rewrite (end-to-end) | Implemented | Wave 206 — the ARGS-rewrite mechanism is now wired through: a script's `ARGS=` in `@Speech` reaches the NPC-hear routing and the spatial broadcast, so the rewritten words are what others hear. |
| `ARGO`, `SRC`, `LOCAL`, `REFn` | Implemented | pre-existing; `LINK` decoupled from `ACT` in Wave 199. |
| `RETURN 1` short-circuit / order | Implemented | firing order EVENTS → TEVENTS → base def → global → `f_onchar_*`, any `RETURN 1` blocks. |
| `RETURN <value>` string vs number | Implemented | Wave 205 — RETURN evaluated its arg as a long and stored the number, so a [FUNCTION] returning a name/defname/message collapsed to a digit; `ExpressionParser.TryEvaluate` now reports whether the arg was genuinely numeric, and RETURN keeps a string value. |
| Firing tests: `TriggerArgParityTests` | — | ARGN seed + mutation round-trip. |

## Object verbs / properties

Char/item verb + property parity is exercised across `ScriptObjectParityTests`,
`GameSystemTests`, and the per-area parity suites. A full `r_Verb` / `r_WriteVal`
enumeration against Source-X is the remaining Faz 0 work and will be appended here
as it is produced.

| Verb | Status | Where | Notes |
|---|---|---|---|
| `TIMERF` | Implemented | `ObjBase.ScheduleTimerF` (delay in seconds) | Runs a delayed function; falls back to running the payload as a verb (Wave 198). |
| `TIMERFMS` | Implemented | `ObjBase.ScheduleTimerF` (delay in ms) | Wave 198 — was missing; millisecond counterpart of `TIMERF`. |

### TRY-family control verbs (`ScriptInterpreter`)

| Verb | Status | Notes |
|---|---|---|
| `TRY` | Implemented | Run a verb / property-set on the target without halting on failure. |
| `TRYP <plevel>` | Implemented | Gate the verb on the source's PrivLevel; aborts with a message below it. Tested (Wave 201). |
| `TRYSRC <src>` | Implemented | Run the verb with a different source object (via `_REF_EXEC`). |
| `TRYSRV` | Implemented | Run at SERVER privilege (Owner / PLEVEL 7), bypassing the source's PLEVEL. Wave 201 — previously passed the original (low-plevel) source, so privileged verbs failed. |

### VarObjs reference chain (`ScriptInterpreter.ResolveVarForTarget`)

| Ref | Resolves to | Status | Notes |
|---|---|---|---|
| `SRC` | trigger source | Implemented | via source console / `IScriptObj`. |
| `ARGO` / `ARGO.x` | `Object1` | Implemented | the trigger argument object. |
| `ACT` / `ACT.x` | `Object2` | Implemented | the acted-on object. |
| `LINK` / `LINK.x` | target's own `LINK` property | Implemented | Wave 199 — was aliased to `Object2` (collided with `ACT`); now reads the object's `m_uidLink`. |
| `REFn` / `REFn.x` | scope-local ref slots | Implemented | local, per-scope. |
| `OBJ`, `NEW` | global object / last-created | Implemented | via server resolver. |

---

## Next

- **Faz 1**: caller-console routing for `VARLIST`/`PRINTLISTS`; minimum-safe `FILE.*`
  set. `EXPORT`/`IMPORT`/`RESTORE`/`SAVESTATICS`/`LOAD` deferred to the Faz 4 world-ops
  block (serialisation-heavy). Done: `TIMERF`/`TIMERFMS` delayed verb+function (Wave
  198); `VarObjs` `LINK` decoupled from `ACT` (Wave 199); `SERV.ACCOUNT.n` indexed
  access (Wave 200); `TRYSRV` server-privilege + `TRYP` gate tested (Wave 201).
- **Faz 2**: per-trigger arg/return/order matrix on the guardrail set.
- **Faz 0 (ongoing)**: enumerate Source-X `CChar`/`CItem`/`CClient` `r_Verb` +
  `r_WriteVal` and append the object-surface tables above.
