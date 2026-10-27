# Spike: cookie injection into a fresh WebView2 profile and yt-dlp cookie writeback

Issue #35 · run 2026-10-27 · harness: `spikes/webview2-login/` (the #31 harness, extended with `--inject`,
`cookie-diff.ps1` and `lifetime-check.ps1`; throwaway, not part of the solution)

Plan references: §5.7 "Session longevity", §13 (risks), §12 M0 (e).

## Result

| Question | Answer |
|---|---|
| Does a jar injected into a **fresh** WebView2 profile keep the user logged in? | **Yes.** YouTube's page reports `ytcfg LOGGED_IN=true` with the avatar shown, 4 times out of 4. |
| Do Google's page scripts rotate it? | **Yes, on every visit.** The page calls `accounts.youtube.com/RotateCookies` ~2 s after load, which rotates `__Secure-1PSIDTS` / `__Secure-3PSIDTS` on `.youtube.com`. Even a jar rotated 2 minutes earlier gets rotated again. |
| Does the refreshed jar download members-only content? | **Yes.** Every refreshed jar passes members-only extraction. |
| What happens to the old jar? | **It survives exactly one rotation.** Google accepts the current `*PSIDTS` and the one it replaced. The rotation after that kills the old jar, however young it is. |
| What does yt-dlp write back? | It **never** touches `*PSIDTS` or any core login cookie. It updates `SIDCC` / `__Secure-*PSIDCC` (value + expiry) and extends `VISITOR_*`. It also rewrites `PREF` with its own locale, drops all HttpOnly flags, drops expired cookies, and collapses duplicates. |
| Are `.google.com` / country-domain cookies needed for a refresh? | **No.** A `.youtube.com`-only jar (23 cookies) refreshes and stays logged in. `RotateCookies` only touches `.youtube.com`. |
| Multi-day lifetime | **Pending.** See [Multi-day check](#multi-day-check). |

Environment is the same as #31 (`docs/spikes/webview2-login.md`): WebView2 runtime 155.0.4283.62, SDK 1.0.4314.35,
yt-dlp 2026.10.26 + Deno 2.9.6, same account. Members-only test video: the same as #31. "Members OK" means
`yt-dlp --cookies <copy> --skip-download` (or `-F`) succeeds; without a valid login it fails with
"This video is available to this channel's members on level: …".

## Injection

`spikes/webview2-login`: `dotnet run -- --inject <jar> --out <new jar>`:

1. Delete `profile-inject\`, create the environment, then for each line of the Netscape file
   `CookieManager.CreateCookie(name, value, domain, path)` + `IsSecure`, `IsHttpOnly`, `Expires` (unset for
   session cookies), `AddOrUpdateCookie`. A leading dot on the domain makes a domain cookie, as in the export.
2. Netscape files carry no SameSite. The harness injects Secure cookies as `SameSite=None` and others as `Lax`.
   That works, and the server re-sets the cookies it rotates with its own attributes anyway.
3. Navigate to `https://www.youtube.com/`, wait 20 s after the first load completes, read `ytcfg.get('LOGGED_IN')`,
   export (same filter as #31: `youtube.com` / `google.com`), dispose.

Chromium silently drops expired cookies on injection: of the 47 injected from the #31 export, 45 were stored. The
two missing ones were `.google.com __Secure-1PSIDRTS` / `__Secure-3PSIDRTS`, which expire **10 minutes** after
sign-in.

### What one refresh changes (gen0 → gen1)

Log of a refresh (names only; `~` = value changed, `+` = new, `-` = deleted):

```
23:18:10.413 Injected 47 cookies                            (45 stored)
23:18:10.525 nav start  https://www.youtube.com/
23:18:12.531 cookie ~   .youtube.com/ SIDCC, __Secure-1PSIDCC, __Secure-3PSIDCC
23:18:13.706 nav done   https://www.youtube.com/ ok=True http=200
23:18:13.710 cookie +   .google.com.tr/ NID, .youtube.com/ ST-* (3, short-lived)
23:18:13.895 request    GET  https://accounts.youtube.com/RotateCookiesPage -> 200
23:18:15.461 request    POST https://accounts.youtube.com/RotateCookies -> 200
23:18:16.536 cookie ~   .youtube.com/ __Secure-1PSIDTS, __Secure-3PSIDTS (+ the *CC cookies again)
23:18:18.523 cookie -   .youtube.com/ ST-* (3)
23:18:33.726 Page login state: ytcfg LOGGED_IN=true, avatar button=true, sign-in link=false
```

`cookie-diff.ps1 -Before cookies-gen0.txt -After cookies-gen1.txt` (values redacted, lengths shown):

| Status | Domain | Name | Expires before → after (UTC) | Len |
|---|---|---|---|---|
| removed (expired) | `.google.com` | `__Secure-1PSIDRTS`, `__Secure-3PSIDRTS` | 2026-10-27 19:59 → – | 84 → – |
| value + expiry | `.youtube.com` | `__Secure-1PSIDTS`, `__Secure-3PSIDTS` | 2027-10-27 19:49 → 2027-10-27 20:18 | 81 → 81 |
| value + expiry | `.youtube.com` | `SIDCC`, `__Secure-1PSIDCC`, `__Secure-3PSIDCC` | 2027-10-27 19:49 → 2027-10-27 20:18 | 72/72/74 |
| expiry | `.youtube.com` | `PREF`, `VISITOR_INFO1_LIVE`, `VISITOR_PRIVACY_METADATA` | pushed forward to the refresh time | same |

Unchanged: everything else, including `SID`, `HSID`, `SSID`, `APISID`, `SAPISID`, `__Secure-1PSID`,
`__Secure-3PSID`, `__Secure-*PAPISID`, `LOGIN_INFO`, and **all** `.google.com` / `accounts.google.com` cookies
(their `*PSIDTS` is not rotated by a YouTube visit).

Two oddities for the production export (#56/#57):
- After the page loads, `GetCookiesAsync` returns `VISITOR_INFO1_LIVE` and `VISITOR_PRIVACY_METADATA` **twice**
  on `.youtube.com` `/`, with identical visible attributes (probably a partitioned copy; the API doesn't expose
  the partition key). Dedupe by domain + path + name on export.
- The page creates `NID` on `.google.com.tr` (and on `.google.com` when it was not injected) by itself.

## Forked-session check

Every refresh started from the previous refresh's export. Each jar was then checked on a throwaway copy, so the
saved jars were never changed by yt-dlp. Before gen1, two aborted harness runs (20:13 and 20:17 UTC, a logging bug)
had also loaded YouTube from gen0, and each rotated it once into an orphaned sibling.

| Check (UTC) | After | gen0 (login, 19:49) | gen1 (20:18) | gen2 (20:20) | gen3 (20:21) | gen4 (20:22, `.youtube.com` only) |
|---|---|---|---|---|---|---|
| 20:19 | gen1 created | ✅ | ✅ | | | |
| 20:20 | gen2 created from gen1 | ❌ | ✅ | ✅ | | |
| 20:22 | gen3 created from gen2 | ❌ | ❌ | ✅ | ✅ | |
| 20:23 | gen4 created from gen3 | ❌ | ❌ | ❌ | ✅ | ✅ |

Raw rows: `spikes/webview2-login/logs/lifetime.csv` (local only).

Reading:
- Google keeps **two** `*PSIDTS` values per session: the current one and the one presented to the last
  `RotateCookies`. Every other value is dead. Age doesn't matter: gen1 died 4 minutes after it was created.
- Rotating from the "previous" value works: gen0 was rotated three times (two orphaned siblings, then gen1),
  and gen1 still became a valid lineage.
- Presenting dead cookies did **not** end the session: after the failed gen0/gen1/gen2 checks, gen3 and gen4
  still worked. There's no sign of cookie-theft protection revoking the whole session, at least not within minutes.
- The core SID family is never rotated, so a dead jar differs from a live one only in `*PSIDTS` (plus `*CC`).

## yt-dlp writeback

yt-dlp rewrites the whole `--cookies` file on exit. Five members-only runs were diffed: three in #31
(full / `.youtube.com` / core-only jars) and two here (gen0, gen1). The diff for gen1 → its yt-dlp output
(rows where only the HttpOnly prefix changed are omitted):

| Status | Domain | Name | Expires before → after (UTC) | Len |
|---|---|---|---|---|
| value + expiry | `.youtube.com` | `SIDCC`, `__Secure-1PSIDCC`, `__Secure-3PSIDCC` | 20:18 → 20:19 (+1 y) | same |
| value + expiry + **session** | `.youtube.com` | `PREF` | 2027-12-01 → session (0) | 41 → 35 |
| expiry | `.youtube.com` | `VISITOR_INFO1_LIVE`, `VISITOR_PRIVACY_METADATA` | pushed forward | same |

All five runs agree:
- **Core cookies are never dropped, blanked or changed**: `SID`, `HSID`, `SSID`, `APISID`, `SAPISID`,
  `__Secure-1PSID`, `__Secure-3PSID`, `__Secure-1PAPISID`, `__Secure-3PAPISID`, `__Secure-1PSIDTS`,
  `__Secure-3PSIDTS`, `LOGIN_INFO`. yt-dlp doesn't call `RotateCookies`, so a yt-dlp run never moves the lineage.
- `SIDCC` / `*PSIDCC` get new values from the server's `Set-Cookie` on each run. They aren't needed (the 12-cookie
  core jar in #31 had none and worked), and the server re-creates them when missing.
- **`PREF` is rewritten by yt-dlp itself**: it appends `hl=en` and sets `tz=UTC` (keys before: `f6, f4, tz`;
  after: `f6, f4, tz, hl`), and saves it as a session cookie.
- **HttpOnly is lost**: every `#HttpOnly_` line comes back without the prefix (Python's `MozillaCookieJar`
  doesn't keep it).
- Cookies on other domains (`.google.com`, `accounts.google.com`) are written back unchanged.
- Expired cookies are dropped (`*PSIDRTS`), and the duplicate `VISITOR_*` entries are collapsed into one.

## Decisions for the plan

1. **Injection works; the browser refresh in §5.7 (4) stands.** No alternative is needed. Inject with
   `CreateCookie` + `AddOrUpdateCookie`; Secure → `SameSite=None`, else `Lax`. Netscape format is enough to
   store the jar.
2. **Lineage rule, now measured:** the engine's jar must be the current or the immediately previous `*PSIDTS`.
   Every browser visit with the jar rotates it. Consequences:
   - Every refresh must start from the engine's current jar (already in the plan) and its result must be stored.
     If a refresh result is lost or rejected, the stored jar is still valid as the "previous" value, and the next
     refresh can start from it.
   - **The WebView2 profile must not keep a live copy of the session.** Clear its cookies
     (`CookieManager.DeleteAllCookies()`) before injecting, and again after exporting (login and refresh), so that
     opening the login window or any other WebView2 page can't rotate the session behind the engine's back.
   - **Rollback only reaches one rotation back.** Generations older than the last browser refresh are dead.
     Rollback (ladder step (b)) can only help when the newest generation came from a refresh. Older generations
     are kept for diagnostics only.
   - A running job's cookie copy survives one refresh, not two.
3. **Writeback merge must be three-way:** merge only the cookies whose value differs between the job's output
   and **the jar that job was given**, onto the engine's current jar. "Last writer wins" on the whole file
   would let a job that started on gen N and finished after a refresh to gen N+1 write gen N's `*PSIDTS` back.
   The jar would then die at the next refresh. Also:
   - keep the stored attributes (HttpOnly, expiry type) and take only value + expiry from the writeback;
   - **skip `PREF`** (yt-dlp-owned locale override, saved as a session cookie);
   - ignore removals (yt-dlp drops expired cookies).
4. **Merge-guard list corrected** to the explicit 12 core names on `.youtube.com`: `SID`, `HSID`, `SSID`,
   `APISID`, `SAPISID`, `__Secure-1PSID`, `__Secure-3PSID`, `__Secure-1PAPISID`, `__Secure-3PAPISID`,
   `__Secure-1PSIDTS`, `__Secure-3PSIDTS`, `LOGIN_INFO`. The old wildcard `__Secure-*PSID*` also matched
   `*PSIDRTS` (expires 10 minutes after sign-in, legitimately dropped) and `*PSIDCC` (changes on every run).
5. **Store only `.youtube.com` cookies.** yt-dlp needs only those (#31), a refresh needs only those (gen4), and
   a YouTube visit never rotates the `.google.com` copies, so they would just go stale. This settles the
   country-domain question from #31: not needed.
6. **Rotation is not time-based:** a refresh rotates even a 2-minute-old jar. Because of that, "refresh
   every 24 h" has no minimum spacing to respect, but it must not run more often than needed (each refresh moves
   the lineage, see 2).

## Multi-day check

Not done yet. Procedure (human, ~2 days):

1. Leave gen4 as the head and don't refresh. Delete `profile/` (#31) and `profile-inject/`, which hold copies of
   the session.
2. A few times a day, run
   `./lifetime-check.ps1 -MembersUrl <members-only url>`
   (tests every `out\cookies-genN.txt` on a throwaway copy and appends to `logs\lifetime.csv`).
3. Questions to answer: how long does an **unused** current jar (gen4) stay valid? Does the previous one (gen3)
   expire on its own over time? After ~2 days, refresh gen4 → gen5: does a refresh still work from an old jar?
4. Add the table here and adjust the refresh / keep-alive intervals in PLAN §5.7 if needed.

## Cleanup

`spikes/webview2-login/out/` and both profile folders hold live session cookies. Delete them when the multi-day
check is done.
