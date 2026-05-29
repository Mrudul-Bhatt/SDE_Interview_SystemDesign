# Search Autocomplete — Beginner Summary

## What is this project?

This is a **Search Autocomplete system** — the same thing that happens when you type "app" in Google and it suggests "apple", "apple watch", "app store", etc. in a dropdown.

---

## Step 1: What is a Trie? (The Core Idea)

Imagine you have these search terms:
```
apple
apple watch
app store
application
```

Instead of storing them as separate strings, a **Trie stores them letter by letter, sharing common prefixes**:

```
root
 └─ a
     └─ p
         ├─ p (end: "app")
         │   └─ l
         │       └─ e (end: "apple")
         │           └─ (space)
         │               └─ w → a → t → c → h (end: "apple watch")
         └─ (space)
             └─ s → t → o → r → e (end: "app store")
```

Think of it like a **file folder tree**. `a` → `p` → `p` is one shared path, then it branches. That's why it's called a **prefix tree**.

---

## Step 2: The Files — What Each One Does

### `Core/TrieNode.cs` — One Box in the Tree

Each node (box) in the tree stores:

| Property | What it means |
|---|---|
| `Children` | A map of "which letter comes next" (the branches going down) |
| `IsEndOfWord` | Is this the last letter of a complete word? (e.g., `e` in "apple") |
| `Frequency` | How many times was this term searched? |
| `TopK` | **Pre-computed list of best 5 suggestions** at this point |

The clever part: **every node already knows the top-5 suggestions** for any prefix that lands on it. So when you type "ap", the node for `p` already has `["apple watch", "apple", "app store", ...]` ready — no extra searching needed.

---

### `Models/RankedCompletion.cs` — One Suggestion

Just a simple container:
```
Term = "apple watch"
Frequency = 8,200,000   ← searched 8.2 million times
```

Higher frequency = shown first in the dropdown.

---

### `Core/Trie.cs` — The Actual Tree

Two main operations:

**Insert** (`Insert("apple", 10_000_000)`):
- Walks down the tree letter by letter: `a` → `p` → `p` → `l` → `e`
- At **every node along the path**, it updates the TopK list with "apple (10M)"
- This means every ancestor (`a`, `ap`, `app`, `appl`) now knows about "apple"

**Search** (`Search("ap")`):
- Walks down: root → `a` → `p`
- Just reads `node.TopK` — it's already sorted and ready
- Done. No scanning. No sorting at query time.

Without the pre-computed TopK, you'd have to search the entire subtree below `p` every time someone types "ap". With TopK cached at each node, it's instant.

---

### `Core/PrefixCache.cs` — A Speed Shortcut (LRU Cache)

Even walking the trie (O(prefix length) steps) can be skipped entirely for very common prefixes like "a", "ap", "app".

The cache stores: `"ap" → [apple watch, apple, app store, ...]`

It's an **LRU (Least Recently Used) cache** — it holds the 200 most recently queried prefixes. When it's full, it throws out the one that hasn't been used in the longest time.

Think of it like a sticky-note pad on your desk:
- Most common prefixes stay on top (hot)
- Rare ones fall off the bottom (evicted)

**On a hit**: return instantly from memory, never touch the trie.  
**On a miss**: ask the trie, then stick the result on the pad.

---

### `Service/AutocompleteService.cs` — The Front Desk

This is the only class your app talks to. It orchestrates the trie + cache together.

```
GetCompletions("ap")
  → Check cache first → HIT? Return instantly.
  → MISS? Ask trie → Store in cache → Return.

RecordTrendSurge("apple vision pro", 9_000_000)
  → Update trie
  → Invalidate cache for "a", "ap", "app", ..., "apple vision pro"
  → Re-warm those cache entries immediately
```

The **trend surge** is important: when something goes viral (e.g., Apple announces a product), the rankings need to update in real-time without throwing away the entire cache.

---

### `Program.cs` — The Demo

It runs 7 scenarios to prove everything works:

| Scenario | What it tests |
|---|---|
| 1 | Basic prefix completions ("a", "ap", "apple") |
| 2 | Ranking by frequency ("news" > "nba" > "netflix" for "n") |
| 3 | Cache hits on repeated queries (50% → 100% hit rate) |
| 4 | Trend surge: "apple vision pro" jumps to #1 |
| 5 | Blocklist: "hack tutorial" never appears even if searched a lot |
| 6 | Edge cases: no results, uppercase input, empty string |
| 7 | Cache flush when trie is rebuilt from fresh data |

---

## The Big Picture

```
User types "ap"
      ↓
AutocompleteService.GetCompletions("ap")
      ↓
  PrefixCache: is "ap" cached?
    ├─ YES → return instantly (Redis in production)
    └─ NO  → ask Trie
                ↓
           Walk: root → 'a' → 'p'
                ↓
           Read node.TopK (pre-sorted list)
                ↓
           Store in cache → return to user
```

## Why This Design Scales

- Queries are O(prefix_length) — extremely fast, and most prefixes are 1–5 chars
- The cache absorbs the top 200 prefixes, which are ~80% of all traffic (Zipf's law — a few prefixes are queried astronomically more than others)
- Blocked terms are filtered at insert time, not query time — zero cost at query
- TopK is pre-sorted at every node — no sorting work happens during a live query
