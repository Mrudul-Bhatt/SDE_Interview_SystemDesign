// GroupStore — member list for group chats (simulates Redis SET per group).
//
// GetMembers returns null (not empty set) for unknown chatIds so ChatServer can
// distinguish "this is a 1:1 chat" from "this is a group chat with 0 members."
// A 1:1 chat is identified by its ID format ("chat:alice:bob") rather than a
// group entry, keeping group membership storage independent of 1:1 logic.

namespace AdvancedDesigns
{
    public class GroupStore
    {
        private readonly Dictionary<string, HashSet<string>> _groups = new();

        public void CreateGroup(string groupId, params string[] members)
            => _groups[groupId] = new HashSet<string>(members);

        public void AddMember(string groupId, string userId)
        {
            if (_groups.ContainsKey(groupId)) _groups[groupId].Add(userId);
        }

        // Returns null if chatId is not a known group (signals 1:1 chat to ChatServer).
        public HashSet<string> GetMembers(string chatId) =>
            _groups.TryGetValue(chatId, out var m) ? m : null;
    }
}
