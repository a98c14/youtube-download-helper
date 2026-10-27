# Spike: WebView2 Google sign-in, cookie export, members-only download

Issue #31 · run 2026-10-27 · harness: `spikes/webview2-login/` (throwaway, not part of the solution)

Plan references: §5.7 (login flow), §5.6 (PO-token note), §13 (risks), §12 M0 (a) and (c).

## Result

| Question | Answer |
|---|---|
| Does Google sign-in work in WebView2? | **Yes.** No "This browser or app may not be secure" page, with no workarounds (default user agent, default browser args). |
| Do the exported cookies download members-only content? | **Yes.** A members-only video downloaded in full (2160p, `401+251`). Without cookies the same URL fails with the members-only error. |
| Is a PO-token plugin needed? | **No.** Cookies + Deno only, no PO-token provider installed, zero PO-token warnings. |
| Premium formats | **Not tested.** The account has Premium, but no video offering a Premium format was found (see [Open](#open)). |

## Environment

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26200, x64 |
| WebView2 runtime | 155.0.4283.62 (Evergreen) |
| WebView2 SDK | Microsoft.Web.WebView2 1.0.4314.35, .NET 10 (SDK 10.0.403), WPF |
| User agent | WebView2 default: `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0` |
| yt-dlp | 2026.10.26 (latest stable, official `yt-dlp.exe`) |
| Deno | 2.9.6; yt-dlp challenge solver scripts v0.8.0 (bundled) |
| ffmpeg | 8.1.1 (Gyan full build) |
| Account | Real account with 2FA, member of the channel at the "Ders Videolar" level, Turkish locale |

## Sign-in flow

The harness creates a `CoreWebView2Environment` with an isolated user-data folder (fresh, deleted before the run)
and navigates to:

```
https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F
```

Observed navigation chain (query strings removed):

```
accounts.google.com/ServiceLogin
accounts.google.com/v3/signin/identifier        ← user signs in (password + 2FA)
accounts.google.com/CheckCookie
accounts.google.com/ServiceLogin
gds.google.com/web/landing → /web/recoveryoptions   ← "add recovery options" interstitial, user skipped it
accounts.google.com/ServiceLogin
accounts.youtube.com/accounts/SetSID
accounts.google.com.tr/accounts/SetSID           ← country domain for a Turkish account
www.youtube.com/
```

From the start of the identifier page to login detected took ~29 s, nearly all of it user input.

Notes for the production login window (#56):
- Google may show interstitials (`gds.google.com` recovery options here). Detection must not assume a fixed
  page sequence; polling cookies is enough.
- Sign-in also visits the account's **country Google domain** (`.google.com.tr`) and sets cookies there.

## Detection rule

Login is complete when `CookieManager.GetCookiesAsync("https://www.youtube.com/")` returns both **`LOGIN_INFO`**
and **`SAPISID`** with a domain of `youtube.com` or a subdomain. The harness checks this after every
`NavigationCompleted` and on a 2 s timer. It fired about 1 s after the first `www.youtube.com/` load completed.
The harness then waits 3 s (the page was still loading a second `www.youtube.com/` navigation), exports and
disposes the WebView2. No false positives: neither cookie exists on `.youtube.com` before `SetSID`.

## Cookies

Export reads every cookie in the profile with `GetCookiesAsync("")` (an empty URI returns all cookies) and keeps
domains equal to or under `youtube.com` / `google.com`: **47 of 57** exported. The 10 skipped cookies were on
`.google.com.tr`.

Netscape format notes: Chromium reports domain cookies with a leading dot (→ include-subdomains `TRUE`),
host-only ones without (→ `FALSE`); HttpOnly cookies are written with the `#HttpOnly_` prefix, which yt-dlp
reads; session cookies (`YSC`) get expiry `0`. `CoreWebView2Cookie.Expires` is a `DateTime` in the .NET projection.

Exported on `.youtube.com` (21): `APISID`, `HSID`, `LOGIN_INFO`, `PREF`, `SAPISID`, `SID`, `SIDCC`, `SSID`,
`VISITOR_INFO1_LIVE`, `VISITOR_PRIVACY_METADATA`, `YSC` (session), `__Secure-1PAPISID`, `__Secure-1PSID`,
`__Secure-1PSIDCC`, `__Secure-1PSIDTS`, `__Secure-3PAPISID`, `__Secure-3PSID`, `__Secure-3PSIDCC`,
`__Secure-3PSIDTS`, `__Secure-ROLLOUT_TOKEN`, `__Secure-YNID`.

Also exported: 14 on `.google.com` (the same SID family plus `NID`, `__Secure-1PSIDRTS`, `__Secure-3PSIDRTS`),
8 on `accounts.google.com` (`ACCOUNT_CHOOSER`, `LSID`, `OTZ`, `SMSV`, `__Host-1PLSID`, `__Host-3PLSID`,
`__Host-GAPS`, `__Host-GAPSTS`), and `OTZ` on `gds.google.com`.

Expiry: the long-lived SID family expires ~13 months out; `*PSIDTS` / `*PSIDCC` / `SIDCC` ~12 months, but these
are the cookies Google rotates, so the expiry date says little about real lifetime (that is #35's subject).

### Minimum set for yt-dlp

Members-only extraction (`-F`) was repeated with reduced jars:

| Jar | Cookies | Members-only `-F` |
|---|---|---|
| Full export | 47 | ✅ (and full download) |
| `.youtube.com` only | 21 | ✅ |
| `.youtube.com` core | 12 | ✅ |
| none | 0 | ❌ "available to this channel's members on level: … Join this channel" |

The **core set that is sufficient**, all on `.youtube.com`:
`SID`, `HSID`, `SSID`, `APISID`, `SAPISID`, `__Secure-1PSID`, `__Secure-3PSID`, `__Secure-1PAPISID`,
`__Secure-3PAPISID`, `__Secure-1PSIDTS`, `__Secure-3PSIDTS`, `LOGIN_INFO`.
This is the same list as the writeback merge guard in PLAN §5.7 (2).

yt-dlp only needs `.youtube.com`. The `google.com` cookies are still worth storing: a browser refresh (§5.7 (4),
spike #35) has to restore the full Google session, not just YouTube's.

## yt-dlp invocation

```
yt-dlp --ignore-config -v --js-runtimes deno --cookies <per-run copy of cookies.txt> -F <url>
yt-dlp --ignore-config -v --js-runtimes deno --cookies <per-run copy of cookies.txt> -P <dir> -o "<label>-%(id)s.%(ext)s" <url>
```

No `-f` (yt-dlp's default selection, as the "Best" preset). Each run got its own copy of the jar, because yt-dlp
writes cookies back to `--cookies` on exit.

| Case | Clients used (with cookies) | Selected format | Result |
|---|---|---|---|
| Members-only | `web_creator`, `tv` | `401+251-1` (2160p AV1 + Opus) → 2.9 GB WebM | ✅ |
| Public | `web_creator`, `tv` | `401+251` | ✅ |

The debug log shows `JS runtimes: deno-2.9.6`, `PO Token Providers: none`, and challenges solved by
`[jsc:deno]`. There were **no** `WARNING:` lines about PO tokens or anything else.

## Open

- **Premium formats:** not tested. The test account has Premium, but no video with a "1080p Premium" quality
  option (e.g. format 616 in `-F`) was found. The Premium check stays an item on the manual release checklist
  (PLAN §11). If it fails there, the likely fixes are a `-f` or format-sort tweak in the "Best" preset, not a
  change to the auth flow, since the same cookies already pass members-only checks.
- Not tested here (out of scope): injecting cookies into a fresh profile, and how long the exported session
  survives yt-dlp writebacks. Both are spike #35.

## Decisions for the plan

- §5.7 login flow is **confirmed as written**. No user-agent override or browser switches are needed; detection is
  `LOGIN_INFO` + `SAPISID` on `.youtube.com`, polled after each navigation and on a timer.
- The export filter must handle **country Google domains** (`google.com.tr`, …): either export them too or decide
  in #35 that they aren't needed. yt-dlp itself does not need them.
- §5.6: **no PO-token plugin** for now; cookies + Deno were enough with yt-dlp 2026.10.26.
- §13: "Google blocks sign-in in embedded WebView2" drops to **Low** likelihood, with cookies.txt import kept as
  the fallback.
