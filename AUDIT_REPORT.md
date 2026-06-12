# RPFramework Security & Correctness Audit Report

Date: 2026-06-12
Scope: RPFramework (Dalamud plugin) + RPFrameworkServer (ASP.NET Core SignalR relay)
All findings below were **fixed in code** unless listed under "Remaining Risks".

---

## 1. Executive Summary

The codebase was functionally solid but written with a trusted-client mindset. The
relay server accepted nearly everything a client sent: identity claims could be
switched mid-session, several broadcast paths had no membership or authorization
check, client-supplied identity fields were forwarded verbatim, and almost every
collection (parties, rooms, bags, playlists, profiles) could be grown without
bound. There was no rate limiting, and an unhandled exception in any hub method
silently dropped the message.

The client trusted the server symmetrically: every inbound DTO was applied to the
local persisted configuration without validation, the relay URL was not validated,
and yt-dlp was invoked without a timeout or size cap (URL validation existed only
implicitly via `VideoId.Parse`).

This audit hardened both sides:

- **Authorization**: every hub method now verifies session identity and party/room/bag
  membership or role before acting; identities are bound per-connection.
- **Validation**: a shared `InputSanitizer` applies consistent length caps, control-char
  stripping, code/URL whitelists, and recursive item-tree validation; all hub inputs
  and all client-inbound DTOs are checked.
- **Resource limits**: hard caps on parties, members, rooms, playlists, bags, bag items,
  participants, NPC entries, profile sizes, and pending trades (`Limits` class).
- **Rate limiting**: per-connection sliding windows via a global `IHubFilter`, with
  tight budgets on the highest-risk fan-out methods.
- **Replication correctness**: server-authoritative initiative rolls, recipient-locked
  trades with per-item locks, invite-gated bag joins, full disconnect cleanup, and
  SignalR group detachment on kick/disband.

Anti-cheat / anti-grief posture is now reasonable for a no-auth community relay. The
fundamental limitation that **PlayerId is an unauthenticated claim** remains (see
Remaining Risks).

---

## 2. Changes Made

### Phase 1 — Server Security

**New: `RPFrameworkServer/Services/InputSanitizer.cs`**
- `Limits`: central resource caps (names 64, codes 16, party members 32, rooms 2 000,
  playlist 200, bag items 500, bag participants 16, item-tree depth 4 / 600 nodes,
  profile 500 stat keys / 100 skills, pending trades 8/player, initiative entries 64, …).
- `SanitizeName` (control-char strip + trim + cap), `IsValidCode`, `IsValidPlayerId`,
  `IsAllowedYoutubeUrl` (youtube.com / youtu.be / bare video-ID only).
- `SanitizeItem`: recursive item-tree validation (depth, node count, name/description
  caps, amount/capacity clamps); rejects structurally bad payloads.
- `SanitizeProfile`: caps stat/check/skill counts, sanitizes strings, and **overwrites
  PlayerId/DisplayName with the server-known session identity** (impersonation fix).
- PBKDF2 password hashing (100k iterations, random salt, `v2:` format) with
  constant-time verification and a legacy unsalted-SHA-256 fallback so existing
  persisted parties keep working.

**New: `RPFrameworkServer/Services/RateLimiter.cs` + `RPFrameworkServer/Hubs/RpHubFilter.cs`**
- Sliding-window limits per (connection, category). Tight budgets for `PushProfile`
  (5/10 s), `BroadcastDiceRoll` (10/10 s), BGM playback commands (20/10 s), trades
  (10/30 s), `PartyCreate`/`PartyJoin` (10/min — password brute-force mitigation),
  `PushSheetTemplate` (5/min), default 60/10 s.
- The filter also wraps **every** hub invocation in try/catch: unhandled exceptions are
  logged with method + connection context and reported to the caller via `OnError`
  instead of being silently dropped (Phase 4.2). State is dropped on disconnect.
- Registered globally in `Program.cs`.

**`RPFrameworkServer/Hubs/RpHub.cs`**
- `Identify`: validates playerId/displayName; binds the identity to the connection on
  first call and **throws on any attempt to re-identify as a different player**
  (session-hijack fix). Also fixed a bug where cached party-member profiles were
  re-sent once per party (nested loop) instead of once.
- `OnDisconnectedAsync`: now cleans up everything — party members broadcast as offline
  (slot preserved for reconnect), BGM room membership removed + broadcast, shared-bag
  participation removed (invite retained for re-join), pending trades cancelled with
  counterpart notification. `UnregisterPlayer` is connection-checked so a stale
  disconnect can no longer knock out a quickly-reconnected player's new connection.
- `BgmJoin`: room-code validation, global room cap, members-per-room cap.
- `BgmAddSong`: YouTube URL whitelist, title sanitization, playlist cap.
- `InventoryTrade`: target/item validation, self-trade rejection, per-item trade lock
  (an item can only be in one pending non-copy trade), pending-offers-per-player cap.
- `InventoryTradeAccept/Reject`: only the addressed recipient can consume an offer;
  a third party guessing an offer GUID can neither accept nor destroy it.
- `BagShare`: full item-payload sanitization; `TryCreateBag` refuses to overwrite a
  bag session owned by someone else (GUID-collision hijack fix); bags-per-owner cap;
  invite recorded for the target.
- `BagShareAccept`: **invite-gated** — knowing the bag GUID no longer grants access;
  participant cap; duplicate-join no longer duplicates membership or notifications.
- `BagApplyOperation`: membership re-checked inside the lock (TOCTOU); per-operation
  sanitization (item trees, rename cap, gil clamp 0–999 999 999, item-count cap);
  malformed ops no longer bump the version.
- `PushProfile`: payload sanitized + capped, identity forced to session identity.
- `FetchProfile`: requires identification, validates target id (kept open to any
  player by design — see §3.2 note).
- `PartyCreate`: name sanitization, password length bounds, per-player and global
  party caps, PBKDF2 hashing.
- `PartyJoin`: code/password validation, salted verify, member cap, per-player cap.
- `PartyLeave` (owner disband): all remaining members' connections are detached from
  the SignalR group so they stop receiving broadcasts for the dead code.
- `PartyKick`: the kicked player's connection is **removed from the party group**
  (previously they kept receiving initiative/dice/profile broadcasts until relog).
- `PartySubmitRoll`: see Phase 3.
- `PartyAddNpc`: name sanitization + initiative entry cap.
- `BroadcastDiceRoll`: now requires party membership; PlayerId/DisplayName are taken
  from the session (spoof fix); message capped at 256 chars.
- `PushSheetTemplate`: DTO/code validation (role check already existed).

**`RPFrameworkServer/Models/ServerState.cs`**
- `BagSession` membership rewritten: private `HashSet`s behind a monitor with
  `IsParticipant` / `IsInvited` / `AddParticipant` / `AddInvite` / `RemoveParticipant` /
  `ParticipantCount` — membership is queried from cleanup paths outside the async
  lock, so it gets its own consistent synchronization.

**`RPFrameworkServer/Services/SessionManager.cs`**
- `Console.WriteLine` → structured `ILogger<SessionManager>` everywhere (Phase 4.3).
- `GetOrCreateRoom` enforces the global room cap; `TryCreateBag` (replaces
  `CreateBag`) enforces ownership + caps; `TryConsumeTrade` is recipient-conditional;
  `TryAddTrade` implements item locks + offer caps; `RemoveTradesInvolving`,
  `GetPlayerRooms`, `GetPlayerBags`, `PartyCount` added for disconnect cleanup and caps.

### Phase 2 — Client Hardening

**`RPFramework/Services/NetworkService.cs`**
- `ValidateServerUrl`: absolute http/https only (no file paths, data URIs, custom
  schemes); `ConnectAsync` refuses invalid URLs and logs a clear warning when
  connecting over plain HTTP (party passwords travel on this channel).
- Reconnect policy replaced: the default 4-attempt array gave up ~22 s into an
  outage; now an infinite capped backoff (0/2/5/15 s, then 30 s) (Phase 4.5).

**`RPFramework/Plugin.cs`**
- Config load wrapped in try/catch with fallback defaults — a corrupted config file
  can no longer break plugin load.
- `ValidateConfiguration`: null-repairs all collections and drops structurally
  invalid entries (empty codes, empty GUIDs, null items/skills) on startup.
- Inbound handler guards: profiles (null/oversized rejected), shared-bag state
  (item-count cap), trade items (null check, fresh GUID to avoid ID collisions),
  sheet templates (null/oversized groups+fields rejected and repaired before being
  persisted), dice rolls (null check + display truncation).
- `DtoToItem`: depth- (4) and width- (500/level) guarded recursive conversion with
  string truncation and numeric clamps — a malicious server cannot blow up the
  local config with an unbounded item tree.
- Migration version flag (see Phase 4).

**`RPFramework/Services/BgmService.cs`**
- `IsAllowedYoutubeUrl` + `TryGetVideoId`: host whitelist and strict video-ID shape
  check (`[A-Za-z0-9_-]{6,16}`) before the ID is used in a filesystem path or on the
  yt-dlp command line (path-traversal + argument-injection defense; `--` already
  terminated option parsing, this adds defense in depth).
- yt-dlp invocations get `--max-filesize 200M` and a 10-minute hard timeout with
  process-tree kill.
- `GetTitleAsync` / `DeleteCacheFor` validate URLs before use.

**`RPFramework/Windows/BgmPlayerWindow.cs`** — non-YouTube URLs rejected at input
time with inline feedback.

**`RPFramework/Windows/SettingsWindow.cs`** — inline server-URL validation and a
plain-HTTP warning in the settings UI.

### Phase 3 — Replication Model

Authority model (now enforced):

| State | Authority | Enforcement |
|---|---|---|
| Party membership / roles | Server | role checks in every party method; group detach on kick/disband |
| Initiative state | Server | d24 rolled server-side; resubmission can't re-roll; order computed and broadcast atomically under the party lock |
| Shared bag contents | Server | intent ops + version check + server echo (was already intent-based); ops sanitized server-side |
| Trade status | Server | recipient-locked consumption + per-item locks |
| BGM playback state | Server (Owner/Admin/party-DM only) | already enforced; timestamps were already server-issued (`UtcMs`) |
| Local (non-shared) bags, character stats, skills | Client | server relays but now sanitizes and identity-stamps |

- **3.2 Profile sync**: client throttle (1 per 3 s) confirmed in `PushLocalProfile`;
  now also enforced server-side (5/10 s). Push fan-out goes only to party members
  (confirmed). *Note:* `FetchProfile` intentionally serves any player's cached
  profile — the context-menu "Open Character Sheet on any nearby player" feature
  depends on it. This is a deliberate product decision, now rate-limited; restricting
  it to shared parties would remove the feature. Conflict strategy: profiles have a
  single writer (their owner); receivers never write back, so last-write-wins is
  inherently safe and no sequence number is required.
- **3.3 Initiative**: the die roll is now generated server-side; the client-sent roll
  is ignored (kept in the signature for wire compatibility). The stat bonus still
  comes from the client because the modifier rules engine (templates + passive
  skills) exists only client-side — it is clamped to ±100. End-turn already advanced
  under the party lock with a single atomic broadcast (verified).
- **3.4 Bag ops**: already intent-based with `BaseVersion` optimistic concurrency and
  server echo before local apply (verified); server-side sanitization added.
- **3.5 Template publishing**: server-side Owner/Co-DM check confirmed (pre-existing)
  and DTO validation added.
- **3.6 Trades**: per-item server-side trade locks added; double-send of the same item
  is now impossible. (True ownership verification is impossible server-side — see
  Remaining Risks.)
- **3.7 BGM sync**: timestamps were already server-issued; authorization was already
  Owner/Admin (+ party-DM bridge). Verified, no change needed beyond rate limiting.
- **3.8 Disconnect**: full cleanup implemented (see `OnDisconnectedAsync` above).
  Reconnect grace: party slots are *never* dropped on disconnect (only marked
  offline), bag invites survive participation removal, and the client re-joins rooms
  and bags automatically on reconnect — so an effectively unlimited grace period is
  provided by design rather than a timer.

### Phase 4 — Code Quality

- **DTO sync (4.1)**: both `NetDtos.cs` and `Dto.cs` now carry a mirror-contract
  header documenting the pairing, the three intentional structural differences, and
  the trailing-default-parameter rule for wire-compatible additions. A shared class
  library was evaluated and rejected for now: the two files are *intentionally* not
  identical (typed `SheetTemplate` vs opaque `JsonElement`; model-type reuse on the
  client), so unification would be a behavioral refactor, not a mechanical move. A
  reflection-based parity test is the recommended next step instead.
- **Error handling (4.2)**: server — global hub filter (above). Client — `SafeInvoke`
  already logged all failures via `Plugin.Log.Warning` (verified, no silent swallows
  in NetworkService).
- **Logging (4.3)**: server `Console.*` fully replaced with `ILogger`; added logs for
  identify/disconnect, trades, bag rejections, rate-limit hits. Client uses
  `IPluginLog` throughout (no `Console.WriteLine` found). BgmService UI-facing
  errors flow through `loadError` by design.
- **Migrations (4.4)**: `MigrateCharacters` was already idempotent (per-character
  flag); a config-level `MigrationVersion` now skips the scan entirely.
  `MigrateToPartyCharacters` is intentionally *not* one-time — it seeds per-party
  characters for newly joined parties — documented as such.
- **Reconnect (4.5)**: `WithAutomaticReconnect` upgraded to an infinite capped-backoff
  policy. Handler registration is per-connection-object (rebuilt in `ConnectAsync`),
  so no duplicate-handler race exists on automatic reconnects (verified: SignalR
  reuses the same `HubConnection` and its handlers across automatic reconnects;
  `Identify` + room/bag re-joins run in the `Reconnected` callback).

---

## 3. Remaining Risks (not fixable in code here)

1. **PlayerId is an unauthenticated claim.** Any connection can identify as any
   `Name@World` on first contact; the server cannot verify FFXIV identity. Fixed
   in-session switching, but first-claim spoofing requires real authentication
   (e.g. a registration token per player, or OAuth). Until then, treat profiles,
   trades, and party membership as cooperative-trust features.
2. **Transport security is a deployment concern.** The server binds plain Kestrel;
   HTTPS must be provided by the host (reverse proxy / certificate). The client now
   warns loudly on `http://`, but cannot force TLS.
3. **Trade ownership cannot be verified server-side.** Inventory is client-
   authoritative by design (local bags never leave the client unless shared), so the
   server cannot prove the offering player owns an item. Item locks prevent
   double-spend of the same item ID, but a modified client can fabricate items.
4. **yt-dlp executes as an unsandboxed process** on the player's machine (Wine). URL
   whitelisting, `--max-filesize`, and timeouts reduce exposure, but OS-level
   sandboxing of the process is out of plugin scope. The yt-dlp binary itself is
   fetched from GitHub releases over HTTPS without signature verification.
5. **SQLite is unencrypted at rest** — party password hashes (now PBKDF2) and
   playlists are readable by anyone with file access to the server host.
6. **Legacy SHA-256 party hashes persist** until each party is recreated; they remain
   brute-forceable offline if the DB leaks. Consider a lazy rehash-on-successful-join.
7. **No tests / CI exist** in the repository; all verification was by build + review.

---

## 4. Recommended Next Steps (priority order)

1. **DTO parity test**: a small test project that loads both assemblies and compares
   record property names/types per the mirror table — turns silent wire breakage
   into a build failure.
2. **Lazy password rehash**: on successful `PartyJoin` against a legacy hash, rehash
   with PBKDF2 and persist.
3. **Authentication token**: issue a random secret on first `Identify` of a new
   PlayerId, store it (hashed) server-side and in the plugin config; require it on
   subsequent identifies. Cheap, removes first-claim spoofing for returning players.
4. **Integration tests** for the hub (SignalR `TestServer`): join/kick/disband flows,
   bag invite gating, trade locks, rate limits.
5. **CI**: GitHub Actions building both projects (a release workflow exists for the
   plugin; add a build-and-test job for PRs).
6. **Deployment hardening doc**: reverse proxy + TLS, systemd sandboxing
   (`ProtectSystem=strict`, `DynamicUser=`), DB file permissions, log rotation.
7. **Shared DTO library**: revisit once the template DTO divergence is resolved
   (e.g. make the client also treat templates as `JsonElement` at the wire layer).

---

## Note on repository layout

`RPFrameworkServer/` is excluded by `.gitignore` (the public repo ships only the
plugin). All server-side fixes therefore exist in the working tree only and cannot
be committed to this repository — version them in the server's own (private) repo
or remove the ignore rule if the server is meant to be public.
