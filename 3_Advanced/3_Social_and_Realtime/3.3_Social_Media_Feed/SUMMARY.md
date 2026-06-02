# Social Media Feed — Beginner Summary

## What is this project?

A **Social Media Feed** (like Twitter/X, Instagram, or Facebook's home timeline) answers one deceptively simple question: *"What should I show this user when they open the app?"*

Think of it like a personal newspaper that's freshly printed every time you open it. The newspaper only contains stories from people you follow, sorted so the most interesting ones are at the top. The hard part isn't printing one newspaper — it's printing **500 million personalized newspapers per day**, each in under 200 milliseconds, when the people being followed are posting constantly.

The central trick of this whole project is a decision: **do you assemble each newspaper when the writer publishes (push), or when the reader opens the app (pull)?** The answer — "it depends on how famous the writer is" — drives the entire design.

---

## The Big Challenges

1. **The celebrity problem — fan-out explosion.** When a normal user posts, you copy it to their ~200 followers' feeds. Cheap. When a user with 100 million followers posts, copying to 100M feeds would take minutes and melt your servers. You need *two different strategies* in the same system.
2. **Read speed — feeds must open instantly.** A user won't wait 3 seconds for their feed. You can't compute "merge posts from 500 people, sort by relevance" on every app open. The answer is to pre-compute feeds ahead of time.
3. **Ranking — newest isn't always best.** A post from 6 hours ago with 2,000 likes might be more interesting than a post from 2 minutes ago with zero engagement. You need a formula that balances freshness against popularity.
4. **Pagination without drift.** As a user scrolls, new posts keep arriving at the top. Naive "page 1, page 2" pagination would show them the same post twice. You need a stable bookmark.

Every file in this project solves one of these problems.

---

## The Two Strategies (the heart of the system)

```
FAN-OUT ON WRITE (push model) — used for REGULAR users
  Alice (200 followers) posts
      ↓ at write time
  Copy post ID into all 200 followers' pre-computed feed caches
      ↓ later
  Follower opens app → feed is ALREADY built → instant read ✓

FAN-OUT ON READ (pull model) — used for CELEBRITIES
  TechGuru (10M followers) posts
      ↓ at write time
  Store post in DB only. Do NOT copy to 10M feeds. ✗ (too expensive)
      ↓ later
  Follower opens app → fetch TechGuru's recent posts fresh, merge them in
```

The system is **hybrid**: it uses push for the many regular users and pull for the few celebrities. A user's final feed is `(pre-computed regular posts) + (freshly pulled celebrity posts)`, merged and ranked.

---

## The Files — What Each One Does

### `Models/Post.cs` — The Content Unit

Every piece of content is a `Post`:

| Field | Example | Meaning |
|---|---|---|
| `PostId` | `"post:42"` | Unique identifier |
| `AuthorId` | `"alice"` | Who wrote it |
| `Content` | `"Just shipped a feature!"` | The text |
| `CreatedAt` | `2026-05-31 10:00` | When it was posted (drives sorting + decay) |
| `LikeCount` / `CommentCount` / `ShareCount` | `800 / 150 / 200` | Engagement signals |

**The `EngagementRaw` formula:** `likes×2 + comments×3 + shares×5`. Shares are weighted highest because sharing is the strongest signal of intent — re-broadcasting to your own followers means "I really endorse this," far more than a passive like. These weights are tuning knobs that real systems A/B test constantly.

---

### `Models/FeedEntry.cs` — A Slot in the Cached Feed

A `FeedEntry` is just `{ PostId, Score }` — **not the full post**. The feed cache stores only post IDs and their sort scores, like a table of contents rather than the articles themselves.

**Why store only the ID, not the post?** Two wins:
- **Compact:** a feed of 1,000 entries is 1,000 small ID+score pairs, not 1,000 full posts with text — far less memory.
- **No invalidation needed:** if a post's like count changes, you update it once in the `PostStore`. Every feed that references it sees the fresh count automatically, because feeds only hold the ID. If feeds held full copies, you'd have to hunt down and update every copy.

This mirrors how Redis sorted sets work — the member is the post ID, the score is the timestamp.

---

### `Models/FeedPage.cs` — One Page + a Cursor

A `FeedPage` holds the posts for one screen plus a `NextCursor` (null when there are no more pages).

**Why cursor-based pagination, not offset-based ("page 1, 2, 3")?**

```
OFFSET pagination (BROKEN for feeds):
  Page 1 = posts 1–5.  User reads them.
  Meanwhile 3 new posts arrive at the top.
  Page 2 = "skip 5, take 5" → but the list shifted down by 3
  → user sees posts 3, 4, 5 AGAIN (duplicates!) ✗

CURSOR pagination (CORRECT):
  Page 1 = newest 5 posts. NextCursor = timestamp of the 5th post.
  Page 2 = "give me 5 posts OLDER than that cursor"
  → new posts at the top don't matter; page 2 starts exactly where page 1 ended ✓
```

The cursor is the timestamp of the last post you saw. Each page asks for posts *older than* that exact point, so new arrivals never cause drift.

---

### `Models/FanOutResult.cs` — The Write Receipt

A diagnostic receipt returned after a post is published: `{ PostId, AuthorId, WasCelebrity, FanOutCount }`. It lets callers and tests verify which path was taken — `WasCelebrity=true, FanOutCount=0` means "skipped fan-out, will be pulled at read time"; `WasCelebrity=false, FanOutCount=200` means "pushed to 200 follower feeds."

---

### `Core/FollowGraph.cs` — Who Follows Whom

The social graph, stored as **two dictionaries** pointing in opposite directions:

```
_followers:  "alice" → { "bob", "carol", "dave" }   (who follows alice?)
_following:  "bob"   → { "alice", "techguru" }       (who does bob follow?)
```

**Why store both directions?** The two halves of the system ask opposite questions:
- **Fan-out (write path)** asks *"who follows Alice?"* → needs `_followers` to know which feeds to push to.
- **Feed read (read path)** asks *"who does Bob follow?"* → needs `_following` to know whose celebrity posts to pull.

Storing only one direction would force a full scan of every user to answer the other question. Keeping both is the classic space-for-speed trade.

**The celebrity classification:** `IsCelebrity(user)` returns true when follower count ≥ a threshold (10 in this demo, ~1–10 million in production). This single check decides which fan-out strategy applies. The graph can also split a user's follows into two buckets — `GetCelebrityFollows` and `GetRegularFollows` — which is exactly what the hybrid read path needs.

---

### `Core/FeedRanker.cs` — Deciding What's "Interesting"

This is the algorithm that makes an "algorithmic feed" (vs a plain chronological one). It uses a **HackerNews-style time-decay formula**:

```
score = (engagement × affinityBoost) / (ageHours + 1)^gravity  +  floor
```

In plain English:
- **More engagement → higher score** (popular posts rise).
- **Older → lower score** — divided by age raised to a power, so a post's score *decays* over time.
- **`gravity` (=1.8) controls how aggressively old posts sink.** Higher gravity = a more "what's happening right now" feed (Twitter ≈ 2.0); lower = long-lived content surfaces longer (Reddit ≈ 1.2).
- **`affinityBoost`** amplifies posts from authors you interact with a lot — a lightweight personalization signal without a full ML model.
- **The `+ floor`** ensures a brand-new post with zero engagement still has a small non-zero score, so it isn't buried instantly.

```
Example outcome (from Scenario 4):
  "Old but viral post 6h ago" (2000 likes)  → rises despite age
  "Fresh post just now" (2 likes)            → falls despite freshness
  alice's posts                              → boosted by affinity 0.8
```

This is why your feed shows a banger from this morning above a boring post from a minute ago.

---

### `Storage/PostStore.cs` — Durable Post Storage

The source of truth for post content. It maintains **two indexes** (simulating two Cassandra access patterns):

```
_postsById:     "post:42" → Post          (random lookup — "hydrate this feed ID")
_postsByAuthor: "alice"   → [post, post…] (range scan — "TechGuru's recent posts")
```

The by-ID index powers feed **hydration** (turning a list of cached post IDs into full posts to display). The by-author index powers the **celebrity pull** (fetching a celebrity's latest posts on demand) and **backfill** (loading someone's recent posts when you newly follow them).

---

### `Storage/FeedCache.cs` — The Pre-Computed Feeds

One pre-sorted list of `FeedEntry` per user — this is what makes feed reads instant. Mirrors a Redis sorted set per user.

**Two important design choices:**

- **Capped at 1,000 entries.** An unbounded feed per user would exhaust memory across millions of users. Users almost never scroll past 1,000 posts; if they do, you re-query the database. After every insert, the oldest (lowest-score) entries beyond 1,000 are trimmed.
- **Kept sorted (newest first) on every write.** Reads always come from the top, so sorting at write time means reads are free. This is a deliberate trade: writes (posting) are far rarer than reads (opening the app), so paying `O(n log n)` on the rare write to make the common read trivial is a win.

It also handles `RemoveAuthorFromFeed` — used on unfollow to scrub all of an ex-followed author's posts out of your feed.

---

### `Service/FanOutService.cs` — The Write Path

When someone posts, `OnPost` decides the strategy:

```
OnPost(post):
  is the author a celebrity?
     NO  → score = post timestamp
           for each follower: cache.AddToFeed(follower, postId, score)
           return { wasCelebrity: false, fanOutCount: N }
     YES → do nothing at write time (post lives in DB only)
           return { wasCelebrity: true, fanOutCount: 0 }
```

It also handles two graph-change events:
- **`BackfillOnFollow`:** when Bob follows Alice, retroactively insert Alice's recent posts into Bob's feed — so Bob's feed isn't empty until Alice's *next* post.
- **`CleanupOnUnfollow`:** when Bob unfollows Carol, remove all of Carol's posts from Bob's feed. (Done asynchronously in production so the unfollow API returns instantly.)

---

### `Service/FeedService.cs` — The Read Path

`GetFeed` assembles the final feed in **four steps** — this is the hybrid model in action:

```
1. PRE-COMPUTED: pull post IDs from the user's feed cache (regular follows,
   already pushed at write time) → hydrate to full posts via PostStore.

2. CELEBRITY PULL: for each celebrity the user follows, fetch their recent
   posts fresh from PostStore (these were NEVER pushed to the cache).

3. MERGE + DEDUPLICATE: union the two sets, drop duplicates by PostId.

4. RANK or SORT:
   algorithmic = true  → FeedRanker.Rank (engagement × decay × affinity)
   algorithmic = false → plain sort by timestamp (chronological)
```

**Why over-fetch `count × 2` from the cache in step 1?** After merging with celebrity posts and removing duplicates, you might end up with fewer than `count` items. Grabbing twice as many up front gives headroom so you don't need a second cache round-trip in the common case.

The result is a `FeedPage` with the ranked posts plus a `NextCursor` (the timestamp of the last post) for the next page.

---

### `Program.cs` — The Demo

Runs 5 scenarios:

| Scenario | What it demonstrates |
|---|---|
| 1 | Basic fan-out on write — Alice/Bob post; followers' feeds get populated instantly |
| 2 | Celebrity hybrid — TechGuru (12 followers > threshold) is skipped on write, pulled on read |
| 3 | Cursor pagination — read 15 posts in pages of 5; a new post doesn't cause page 2 to drift |
| 4 | Algorithmic vs chronological — old-but-viral rises, fresh-but-boring falls, affinity boosts |
| 5 | Follow/unfollow — backfill loads recent posts on follow; cleanup scrubs them on unfollow |

---

## The Big Picture — How It All Fits Together

```
WRITE PATH (someone posts):

FanOutService.OnPost(post)
   ↓
FollowGraph.IsCelebrity(author)?
   ├─ NO (regular user):
   │     for each follower in FollowGraph.GetFollowers(author):
   │         FeedCache.AddToFeed(follower, postId, score)   ← push to feeds
   │     → FanOutResult { wasCelebrity: false, fanOutCount: N }
   │
   └─ YES (celebrity):
         (store in PostStore only — done at CreatePost time)
         → FanOutResult { wasCelebrity: true, fanOutCount: 0 }   ← skip fan-out


READ PATH (someone opens the app):

FeedService.GetFeed(user, count, cursor, algorithmic)
   ↓
1. precomputed = FeedCache.GetFeed(user, count×2, cursor)     ← regular follows
                 → PostStore.GetByIds(...)  (hydrate IDs to posts)
   ↓
2. celebrity   = for each FollowGraph.GetCelebrityFollows(user):
                     PostStore.GetRecentByAuthor(celeb, 10)    ← pull on demand
   ↓
3. merged      = (precomputed ∪ celebrity), deduped by PostId
   ↓
4. ranked      = algorithmic ? FeedRanker.Rank(merged) : sortByTime(merged)
   ↓
   → FeedPage { Posts, NextCursor }


GRAPH CHANGES:

Follow(bob, alice)   → FanOutService.BackfillOnFollow → load alice's recent posts into bob's feed
Unfollow(bob, carol) → FanOutService.CleanupOnUnfollow → remove carol's posts from bob's feed
```

---

## Why This Design Is Used Everywhere

- **Hybrid fan-out** is exactly how Twitter/X, Instagram, and Facebook actually work — push for the masses, pull for the famous few. It's the single most important idea in feed system design, and the reason a celebrity tweet doesn't take down the site.
- **Pre-computed feed caches** are why your timeline opens in milliseconds — the expensive merge-and-rank work was done ahead of time (or is cheap because most of the feed is already assembled).
- **Storing IDs not content** in the cache is why a like count updates everywhere at once and why feeds stay memory-cheap — the same normalization principle behind every well-designed cache.
- **Cursor pagination** is the standard for any infinite-scroll feed (Twitter, Reddit, Slack, GraphQL Relay all use it) precisely because it survives live inserts.
- **Time-decay ranking** is the foundation of HackerNews, Reddit, and the simpler tiers of every "For You" feed — a transparent formula that captures "fresh + popular" before you ever reach for machine learning.
- **The follow graph as two indexes** is how every social platform answers both "who are my followers?" and "who do I follow?" in O(1) — the dual-index pattern reused across the industry.
