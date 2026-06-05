# Social Media Feed — Low-Level Design (UML Class Diagram)

This is the **class-level** view of the Social Media Feed. The defining structural feature:
three shared backing stores (`FollowGraphRedis`, `FeedCacheRedis`, `PostStoreCassandra`) are
injected into **both** `FanOutService` (write path) and `FeedService` (read path) — that
sharing *is* the hybrid fan-out architecture.

> **How to view the diagram below:** open this file in VS Code's Markdown preview
> (`Cmd+Shift+V`). If it doesn't render, install the **Markdown Preview Mermaid Support**
> extension (`bierner.markdown-mermaid`). It also renders automatically on GitHub.

---

## Class Diagram

```mermaid
classDiagram
    direction TB

    class FanOutService {
        «write path»
        -FollowGraphRedis _graph
        -FeedCacheRedis _cache
        -PostStoreCassandra _postStore
        +OnPost(post) FanOutResult
        +BackfillOnFollow(follower, target, count) void
        +CleanupOnUnfollow(follower, target) void
    }

    class FeedService {
        «read path»
        -FollowGraphRedis _graph
        -FeedCacheRedis _cache
        -PostStoreCassandra _postStore
        +GetFeed(userId, count, cursor, algorithmic, authorAffinity) FeedPage
    }

    class FollowGraphRedis {
        «social graph»
        -Dictionary~string,HashSet~ _followers
        -Dictionary~string,HashSet~ _following
        -int CelebrityThreshold
        +Follow(follower, target) void
        +Unfollow(follower, target) void
        +GetFollowers(userId) IEnumerable
        +GetFollowing(userId) IEnumerable
        +GetFollowerCount(userId) int
        +IsCelebrity(userId) bool
        +GetCelebrityFollows(userId) IEnumerable
        +GetRegularFollows(userId) IEnumerable
    }

    class FeedCacheRedis {
        «pre-built feeds»
        -Dictionary~string,List~ _feeds
        -int MaxFeedSize
        +AddToFeed(userId, postId, score) void
        +GetFeed(userId, count, beforeScore) List~string~
        +RemoveFromFeed(userId, postId) void
        +RemoveAuthorFromFeed(userId, authorId, postStore) void
        +GetFeedSize(userId) int
    }

    class PostStoreCassandra {
        «source of truth»
        -Dictionary~string,Post~ _postsById
        -Dictionary~string,List~ _postsByAuthor
        -long _idCounter
        +CreatePost(authorId, content, ...) Post
        +Get(postId) Post
        +GetByIds(postIds) List~Post~
        +GetRecentByAuthor(authorId, count) List~Post~
    }

    class FeedRanker {
        «static utility»
        -double Gravity$
        +Score(post, affinityBoost)$ double
        +Rank(posts, authorAffinity)$ List~Post~
    }

    class Post {
        «entity / DB row»
        +string PostId
        +string AuthorId
        +string Content
        +DateTime CreatedAt
        +int LikeCount
        +int CommentCount
        +int ShareCount
        +double EngagementRaw
    }

    class FeedEntry {
        «value object»
        +string PostId
        +long Score
    }

    class FeedPage {
        «response DTO»
        +List~Post~ Posts
        +long? NextCursor
    }

    class FanOutResult {
        «receipt DTO»
        +string PostId
        +string AuthorId
        +bool WasCelebrity
        +int FanOutCount
    }

    %% ── Aggregation: BOTH services share the SAME three injected stores ──
    FanOutService o-- FollowGraphRedis
    FanOutService o-- FeedCacheRedis
    FanOutService o-- PostStoreCassandra
    FeedService   o-- FollowGraphRedis
    FeedService   o-- FeedCacheRedis
    FeedService   o-- PostStoreCassandra

    %% ── Dependency: static call / creates result objects ──
    FeedService ..> FeedRanker : Rank (static)
    FeedService ..> FeedPage : creates
    FanOutService ..> FanOutResult : creates
    FeedRanker ..> Post : scores
    FeedCacheRedis ..> PostStoreCassandra : RemoveAuthorFromFeed(arg)

    %% ── Composition: containers own their contents ──
    FeedCacheRedis "1" *-- "0..*" FeedEntry : stores
    PostStoreCassandra "1" *-- "0..*" Post : owns
    FeedPage "1" *-- "0..*" Post : holds
```

---

## Reading the relationships

| Notation | Relationship | In this design |
|----------|--------------|----------------|
| `o--` | **Aggregation** (holds, independent lifetime) | `FanOutService` **and** `FeedService` are both constructor-injected the **same** `FollowGraphRedis`, `FeedCacheRedis`, and `PostStoreCassandra` instances (created once in `Main`). Shared substrate → aggregation, not composition. |
| `*--` | **Composition** (owns its contents) | `FeedCacheRedis` owns its `FeedEntry` lists; `PostStoreCassandra` owns its `Post` objects; `FeedPage` owns its `List<Post>`. |
| `..>` | **Dependency** (uses, no stored field) | `FeedService` → `FeedRanker` (static `Rank`); both services *create* result DTOs; `FeedCacheRedis.RemoveAuthorFromFeed` takes a `PostStoreCassandra` as a parameter. |

## The structural story (the "why" behind the shape)

- **Two services, one shared substrate.** `FanOutService` (write) and `FeedService` (read) are
  deliberately separate — the two halves of the hybrid model — but they operate on the **same
  three stores**. The writer pushes into `FeedCacheRedis`; the reader reads from it. That shared
  instance is the contract between them.
- **`FollowGraphRedis` is the decision-maker.** Its `IsCelebrity` gate is what both services branch
  on: the writer uses it to decide *push vs skip*; the reader uses `GetCelebrityFollows` /
  `GetRegularFollows` to decide *pull live vs read from cache*.
- **`PostStoreCassandra` is the single source of truth.** Everything else holds only `PostId`
  references; content is *hydrated* back from here at read time via `GetByIds`.
- **`FeedEntry` (value object) vs `Post` (entity).** The cache stores the tiny `(PostId, Score)`
  pair, never the full `Post` — that's what keeps millions of feeds in memory.
- **`FeedRanker` is stateless.** A static utility (pure scoring function); neither service stores a
  reference to it — it's a pure dependency.
- **Cross-store dependency:** `FeedCacheRedis.RemoveAuthorFromFeed` needs `PostStoreCassandra`
  passed in — the cache only knows `PostId`s, so it must ask the store "who wrote this?" to scrub
  an unfollowed author's posts.

## Call flow at a glance

```
WRITE  OnPost(post):
   FollowGraphRedis.IsCelebrity(author)
     ├─ false → for each follower: FeedCacheRedis.AddToFeed(...)   → FanOutResult(count=N)
     └─ true  → skip (post already in PostStoreCassandra)          → FanOutResult(celeb, 0)

READ   GetFeed(user):
   1. FeedCacheRedis.GetFeed(user, count*2, cursor)      → regular-follow post IDs
   2. PostStoreCassandra.GetByIds(...)                    → hydrate to Posts
   3. FollowGraphRedis.GetCelebrityFollows(user)
        → PostStoreCassandra.GetRecentByAuthor(celeb)     → live celebrity posts
   4. merge + de-dupe → FeedRanker.Rank (or sort by time) → FeedPage
```
