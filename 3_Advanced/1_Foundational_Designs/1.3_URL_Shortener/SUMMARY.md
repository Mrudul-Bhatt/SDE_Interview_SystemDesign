# URL Shortener — Beginner Summary

## What is this project?

A **URL Shortener** is what services like bit.ly or tinyurl.com do — you give it a long ugly URL like:

```
https://www.example.com/products/category/shoes?color=red&size=10&ref=homepage
```

And it gives you back a short one like:

```
https://sho.rt/0001abc
```

When someone visits `sho.rt/0001abc`, the system looks it up and instantly redirects them to the original long URL.

---

## The 7-Character Code — Where Does It Come From?

This is the heart of the system: how do you generate `0001abc`?

### Step 1: A Global Counter (`Core/IdGenerator.cs`)

Every time someone creates a new short URL, a counter goes up by 1:

```
1st URL → ID = 100,001
2nd URL → ID = 100,002
3rd URL → ID = 100,003
...
```

`Interlocked.Increment` is used instead of a regular `++` because thousands of users could be shortening URLs at the same time. This is a CPU-level instruction that guarantees no two requests ever get the same number — like giving out numbered tickets at a deli counter.

### Step 2: Convert the Number to a Short String (`Core/Base62.cs`)

The number `100001` is then converted to a 7-character code using **Base62 encoding**.

You already know Base10 (digits 0–9, 10 choices per position). Base62 uses 62 characters: `0-9`, `a-z`, `A-Z`.

```
Base10 with 7 digits  →  10^7  =  10 million combinations
Base62 with 7 chars   →  62^7  =  3.5 trillion combinations
```

3.5 trillion is enough for ~96 years at 100 million new URLs per day. Every number maps to a unique 7-character code and back — it's completely reversible:

```
100001  →  "00001LZ"
100001  ←  "00001LZ"   (Decode gets the original number back)
```

---

## The Files — What Each One Does

### `Models/UrlRecord.cs` — One Row in the Database

Think of this as a single row in a spreadsheet. For every short URL created, one record is saved:

| Field | Example | Meaning |
|---|---|---|
| `ShortCode` | `"0001abc"` | The 7-char code in the short URL |
| `LongUrl` | `"https://example.com/..."` | Where to redirect to |
| `CreatedBy` | `"alice"` | Who created it |
| `ExpiresAt` | `2026-06-01` | Optional expiry date (null = never expires) |
| `IsActive` | `true` | Can be turned off without deleting |
| `IsExpired` | computed | Checks if the current time is past `ExpiresAt` |

### `Models/RedirectResult.cs` — The HTTP Response

When someone visits a short URL, the system returns one of three answers:

| Status | Meaning |
|---|---|
| `302` | Found — redirect to the long URL |
| `404` | Not Found — this short code doesn't exist |
| `410` | Gone — it existed but is now expired or deactivated |

The difference between 404 and 410 matters for Google: 410 tells search engines "this was intentionally removed", so they stop trying to index it.

---

### `Core/LruCache.cs` — The Speed Layer

Every redirect needs to look up the `UrlRecord` for a short code. Going to the database every time is slow. Instead, a **cache** keeps the most-used records in memory.

It's an **LRU (Least Recently Used)** cache. Imagine a whiteboard with room for only 3 things:

```
Most recent → [ apple watch ][ apple ][ app store ] ← Oldest
```

When a 4th item arrives, `app store` (the oldest, least recently used) gets erased to make room. Items that are accessed get moved to the front.

Internally it uses two data structures together:
- A **Dictionary** for O(1) lookup by key (like a phone book)
- A **LinkedList** for O(1) move-to-front and remove-oldest (like a chain of cards)

Either alone wouldn't work — the dictionary can't track order, the list can't look up by key instantly.

---

### `Storage/UrlRepository.cs` — The Database

Simulates a PostgreSQL table. Three operations:

- **`TryInsert`** — saves a new record, returns `false` if the short code already exists (same as a database unique constraint)
- **`Find`** — looks up a record by short code
- **`Deactivate`** — sets `IsActive = false` (soft delete — the row stays, just disabled)

**Why soft delete?** If you delete a record, you lose all the click analytics history for it. Soft delete keeps the history while returning 410 to anyone who visits.

---

### `Storage/ClickAnalytics.cs` — The Click Counter

Every time someone redirects through a short URL, it records:
- Total clicks for that code
- Clicks broken down by country

```
"summer-sale" → Total: 12
  US: 4 clicks
  UK: 2 clicks
  DE: 2 clicks
  IN: 2 clicks
  ...
```

The `lock` keyword ensures two users redirecting at the exact same millisecond don't corrupt the counters. In a real system, this would use Redis atomic counters instead.

---

### `Service/UrlShortenerService.cs` — The Front Desk

This is the only class your app talks to. It wires all the pieces together.

**Shorten flow:**
```
User gives long URL
  → Validate (must start with http)
  → Custom alias? Check it's not reserved ("admin", "api", ...) and not taken
  → Auto-generate? Get next ID → Base62.Encode → shortCode
  → Save UrlRecord to DB
  → Pre-warm the cache (so the very first redirect is a cache hit)
  → Return "https://sho.rt/0001abc"
```

**Redirect flow:**
```
User visits sho.rt/0001abc
  → Check cache first (fast path)
      → HIT: is it expired/deactivated? → 410 or 302
  → MISS: check database
      → Not found → 404
      → Found but expired/deactivated → 410
      → Found and active → put in cache, record click → 302
```

---

### `Program.cs` — The Demo

Runs 7 scenarios:

| Scenario | What it tests |
|---|---|
| 1 | Base62 encoding — shows numbers becoming short codes and back |
| 2 | Basic shorten + redirect, cache hits on second access |
| 3 | Custom aliases — conflict detection, reserved names, invalid characters |
| 4 | TTL expiry — 410 after the link's lifetime ends |
| 5 | Analytics — 12 clicks tracked by country |
| 6 | Deactivation — 410 after soft-delete, 404 for unknown code |
| 7 | LRU eviction — cache of size 3 evicts oldest when 5 URLs are created |

---

## The Big Picture

```
User: "shorten https://very-long-url.com"
          ↓
   IdGenerator: counter++ → 100,001
          ↓
   Base62.Encode(100001) → "00001LZ"
          ↓
   UrlRepository.TryInsert → saved to DB
          ↓
   LruCache.Put → pre-warmed
          ↓
   Return "https://sho.rt/00001LZ"

─────────────────────────────────────────

User visits: sho.rt/00001LZ
          ↓
   LruCache.TryGet("00001LZ")
     ├─ HIT  → check active/expired → 302 redirect (fast path)
     └─ MISS → UrlRepository.Find("00001LZ")
                 ├─ null      → 404
                 ├─ expired   → 410
                 └─ active    → cache it → 302 redirect
```

## Why This Design Scales

- Base62 with 7 chars gives 3.5 trillion unique codes — enough for decades
- The LRU cache absorbs repeated redirects for popular links, keeping the DB hit rate low
- `Interlocked.Increment` on the ID counter is lock-free and safe under high concurrency
- Soft delete preserves analytics history while correctly returning 410 to browsers and crawlers
- Analytics recording is isolated from the redirect path — in production it would go async via a queue so a slow analytics write never delays a redirect
