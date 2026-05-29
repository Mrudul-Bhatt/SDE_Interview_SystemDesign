# Web Crawler — Beginner Summary

## What is this project?

A **Web Crawler** (also called a spider or bot) is what Google uses to discover and read web pages across the internet. You give it a starting URL (a "seed"), it fetches that page, extracts all the links on it, then fetches those pages, extracts their links, and keeps going — like following a trail of breadcrumbs across the web.

---

## The Big Challenge: Scale

A real web crawler has to handle **billions of URLs**. Three specific problems:

1. **Duplicates** — the same page can be linked from thousands of places. You must not fetch it thousands of times.
2. **Politeness** — hammering a website with hundreds of requests per second will get you IP-banned (and is rude). You must slow down per domain.
3. **Traps** — some websites generate infinite URLs (e.g., a calendar that keeps linking to "next month" forever). You must not follow them endlessly.

Every file in this project solves one of these problems.

---

## The Files — What Each One Does

### `Models/UrlTask.cs` — One Job in the Queue

Every URL to be crawled is wrapped in a `UrlTask`:

| Field | Example | Meaning |
|---|---|---|
| `Url` | `"https://example.com/products"` | The page to fetch |
| `Domain` | `"example.com"` | Pre-extracted for politeness checks |
| `Depth` | `2` | How many links deep from the starting seed |
| `Priority` | `High / Medium / Low` | High = seed URLs; Medium = discovered links |

`Depth` is the spider-trap defence. If a site keeps generating links forever, depth stops you after N hops from your starting point.

---

### `Core/UrlNormalizer.cs` — Making URLs Comparable

The same page can appear as many different strings:

```
https://Example.COM/Products/              ← uppercase, trailing slash
https://example.com/products               ← canonical
https://example.com/products?utm_source=email  ← tracking junk
https://example.com/products?b=2&a=1       ← params in different order
```

Without normalization, these look like **4 different pages** and you'd crawl the same page 4 times. The normalizer applies 6 rules to produce a single canonical form:

1. Resolve relative URLs (`/about` → `https://example.com/about`)
2. Lowercase the scheme and host
3. Remove trailing slash
4. Strip the `#fragment` (it's a position on a page, not a different page)
5. Remove tracking parameters (`utm_*`, `ref`, `fbclid`, etc.)
6. Sort remaining query parameters alphabetically

After normalization, all 4 variations above become the same string — so deduplication works correctly.

---

### `Core/BloomFilter.cs` — Deduplication Without Using Huge Memory

**The problem:** You need to track which URLs you've already seen. With 1 billion URLs, a normal HashSet would use ~50 GB of RAM. That's impractical.

**The solution:** A Bloom filter. Think of it like a large array of light switches (bits), all starting OFF.

When you **add** a URL:
- Run it through 3 different hash functions → get 3 positions
- Flip those 3 switches ON

When you **check** a URL:
- Run it through the same 3 hash functions → get 3 positions
- If **all 3 switches are ON** → "probably seen before" (skip it)
- If **any switch is OFF** → "definitely never seen" (crawl it)

The trade-off:
- **Never has false negatives** — if you added it, it will always say "seen"
- **Has ~1% false positives** — occasionally says "seen" for something new → you skip ~1% of pages
- **Uses 1.2 GB instead of 50 GB** for 1 billion URLs — a huge win

```
1B URLs × 50 bytes = 50 GB HashSet
1B URLs in Bloom   =  1.2 GB  ← fits in RAM
```

---

### `Core/RobotsCache.cs` — Respecting Website Rules

Every website can have a `/robots.txt` file that tells crawlers:
- Which paths are off-limits (`/admin`, `/private`)
- How fast you can crawl (`Crawl-delay: 1`)

```
# example.com/robots.txt
Disallow: /admin
Disallow: /private
Crawl-delay: 1
```

`RobotsCache` stores these rules per domain and answers two questions:
- `IsAllowed("https://example.com/admin")` → `false` (blocked)
- `GetCrawlDelayMs("example.com")` → `500` ms between requests

---

### `Infrastructure/UrlFrontier.cs` — The Smart Work Queue

This is not a simple queue. It's a **two-level priority queue** modeled after how Google's Mercator crawler works.

When you call `TryDequeue()`, it doesn't just give you the next URL. It:

1. Sorts by **Priority** (High → Medium → Low)
2. Then by **Depth** (shallower first — breadth-first within same priority)
3. Only picks a URL whose **domain's crawl-delay has elapsed**

So if `example.com` was crawled 200ms ago and its crawl-delay is 500ms, no `example.com` URL will be returned yet — even if there are 50 of them waiting. It returns a URL from a different domain instead.

If **all domains are currently throttled**, `TryDequeue()` returns `null` and the crawler waits briefly before trying again.

---

### `Infrastructure/SimulatedWeb.cs` — A Fake Internet for Testing

In a real crawler, fetching a URL means making an HTTP GET request over the network. That's slow, unpredictable, and needs real websites to be up.

`SimulatedWeb` is a stand-in — an in-memory map of `URL → (HTML content, outbound links, status code)`. The crawler calls `Fetch("https://example.com")` and gets back the fake HTML and links instantly, with no network involved.

---

### `Models/ContentStore.cs` — Where Crawled Pages Are Saved

After fetching a page, it's stored as a `CrawledPage`:

| Field | Example | Meaning |
|---|---|---|
| `Url` | `"https://example.com/products"` | The page URL |
| `Html` | `"<html>Products</html>"` | Raw HTML content |
| `StatusCode` | `200` | HTTP response (200 = OK, 404 = not found) |
| `Depth` | `1` | How deep from the seed |
| `ContentHash` | `"A3F92B01"` | A fingerprint of the HTML content |

`ContentHash` enables near-duplicate detection — if two different URLs have the same hash, their content is identical and you only need to index one of them.

---

### `Models/CrawlStats.cs` — The Dashboard

Simple counters updated during the crawl:

| Counter | What it tracks |
|---|---|
| `PagesCrawled` | Pages actually fetched |
| `UrlsDiscovered` | Links found in HTML |
| `DuplicatesBlocked` | Bloom filter hits |
| `RobotsBlocked` | robots.txt denials |
| `DepthBlocked` | Links beyond maxDepth |
| `NormalizationFailed` | Unparseable URLs |

---

### `Service/Crawler.cs` — The Main Engine

This ties everything together. For every URL it processes, it runs this pipeline:

```
1. Normalize URL          → UrlNormalizer
2. Already seen?          → BloomFilter.MightContain → skip if yes
3. Allowed by robots.txt? → RobotsCache.IsAllowed   → skip if no
4. Too deep?              → Depth > maxDepth         → skip if yes
5. Fetch the page         → SimulatedWeb.Fetch
6. Store result           → ContentStore.Store
7. Extract links          → repeat from step 1 for each link
```

The `Run()` loop keeps going until either `maxPages` are crawled or the frontier is empty.

---

### `Program.cs` — The Demo

Runs 5 scenarios:

| Scenario | What it tests |
|---|---|
| 1 | Full crawl across 3 domains — shows link discovery, deduplication, robots blocking |
| 2 | URL normalization — 5 spellings of the same page all collapse to one canonical URL |
| 3 | Bloom filter false positives — small filter vs large filter comparison |
| 4 | Per-domain politeness — `fast.com` (200ms) vs `slow.com` (1000ms) throttling |
| 5 | robots.txt blocking + depth limit — shows which paths are blocked and why |

---

## The Big Picture

```
Seed URLs added
      ↓
  UrlFrontier (priority queue)
      ↓
  TryDequeue() — picks highest-priority URL whose domain delay has elapsed
      ↓
  UrlNormalizer.Normalize() — make URL canonical
      ↓
  BloomFilter.MightContain() — seen before? → skip
      ↓
  RobotsCache.IsAllowed() — allowed? → skip if not
      ↓
  Depth check → skip if too deep
      ↓
  SimulatedWeb.Fetch() — get HTML + outbound links
      ↓
  ContentStore.Store() — save the crawled page
      ↓
  For each outbound link → back to top
```

## Why This Design Scales

- **Bloom filter** cuts RAM from 50 GB to 1.2 GB for 1B URLs — the only practical solution at web scale
- **URL normalization** before the Bloom filter check ensures `?a=1&b=2` and `?b=2&a=1` are not crawled twice
- **Per-domain crawl delay** prevents IP bans and respects server load — without it you'd get blocked immediately
- **Depth cap** is the only reliable defence against spider traps — without it, a calendar or session-token URL can run the crawler forever
- **Priority queue** ensures important seed pages and fresh content are crawled before deep archive pages
