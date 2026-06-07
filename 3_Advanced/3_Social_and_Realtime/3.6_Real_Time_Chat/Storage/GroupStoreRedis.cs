// GroupStoreRedis — member list for group chats (simulates a Redis SET per group).
//
// THE BIG IDEA:
// When a message is sent to a group chat, ChatServer needs to answer one question:
// "who are all the recipients?" GroupStoreRedis is the sole source of truth for that.
// Each group is stored as an unordered set of userIds — no roles, no ordering,
// just the membership list needed to fan the message out.
//
// The store also acts as the discriminator between two chat types:
//
//   Group chat  → groupId exists in _groups → GetMembers returns the member set
//   1:1 chat    → chatId NOT in _groups     → GetMembers returns null
//
// ChatServer checks the return value first. Null means "treat this as a 1:1 chat:
// parse the recipient directly from the chatId." Non-null means "fan out to every
// member in the set." This keeps 1:1 and group logic cleanly separated — 1:1 chats
// never need a GroupStoreRedis entry.
//
// In production this is a Redis SET per group:
//   SADD  group:{groupId} userId     → AddMember
//   SMEMBERS group:{groupId}         → GetMembers (returns all members in O(N))
//   EXISTS group:{groupId}           → null-check equivalent (is this a group chat?)
//
// WHY HASHSET (not List) FOR MEMBERS:
// Fan-out iterates the member list once per message — no random access needed.
// But the set must reject duplicate userIds (re-adding Alice should be a no-op,
// not a double fan-out). HashSet gives both: O(1) duplicate rejection on Add and
// O(N) iteration, matching the Redis SET semantics this simulates.
//
// WHY ADDMEMBER SILENTLY IGNORES UNKNOWN GROUPS (not throwing):
// A race condition is possible in production: the group is deleted between the
// time ChatServer verified it exists and the time AddMember runs. Throwing here
// would crash the add-member flow for a group that no longer exists — a silent
// no-op is the safer choice. Callers that need to know whether the group exists
// should call GetMembers first and check for null.

using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class GroupStoreRedis
    {
        // groupId → set of member userIds.
        // Key presence is also the signal that a chatId is a group (not a 1:1 chat) —
        // absence of the key tells ChatServer to fall back to 1:1 delivery logic.
        //
        // ── RUNTIME SNAPSHOT (Scenario 3: the "family" group) ──
        //   _groups = {
        //       "group:family" → { "alice", "bob", "carol", "dave" }
        //   }
        //   GetMembers("group:family") → { alice, bob, carol, dave }
        //       → ChatServer fans out to all-but-sender: { bob, carol, dave }
        //   GetMembers("chat:alice:bob") → null   ← key ABSENT → this is a 1:1 chat,
        //       → ChatServer instead decodes participants from the chatId string itself.
        //   The HashSet rejects duplicates: AddMember("group:family","alice") is a no-op.
        private readonly Dictionary<string, HashSet<string>> _groups = [];

        // Creates a new group with an initial member list. Uses params so callers
        // can pass members inline: CreateGroup("g1", "alice", "bob", "carol").
        // Overwrites any existing entry for groupId — intentional for demo setup,
        // where groups are recreated between runs without an explicit delete step.
        public void CreateGroup(string groupId, params string[] members) => _groups[groupId] = [.. members];

        // Adds a single user to an existing group. Silently no-ops if the group
        // does not exist (see header for why). HashSet.Add is also idempotent —
        // adding an already-present member is a no-op with no error.
        public void AddMember(string groupId, string userId)
        {
            if (_groups.ContainsKey(groupId)) _groups[groupId].Add(userId);
        }

        // Returns the member set for a known group, or null for an unknown chatId.
        // Null is the deliberate signal to ChatServer that this is a 1:1 chat —
        // not an error and not an empty group. Callers must handle null explicitly.
        public HashSet<string> GetMembers(string chatId) => _groups.TryGetValue(chatId, out var m) ? m : null;
    }
}
