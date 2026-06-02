// Message — the envelope that carries one unit of data through the queue.
//
// THE BIG IDEA:
// Think of a Message like a physical parcel on a conveyor belt:
//   - Key   = the shipping address  → decides WHICH belt lane (partition) it goes on
//   - Value = the parcel contents   → the actual payload the consumer cares about
//   - Offset = the lane position    → stamped by the belt, not the sender
//   - Partition = the lane number   → which parallel lane the parcel rides
//
// The Key → Partition relationship is the core ordering guarantee:
//   All parcels with the same address (key) always ride the same lane (partition).
//   One consumer owns each lane. Therefore, all events for "user:Alice" are
//   processed in the exact order they were produced — no race conditions.
//
// WHY OFFSET IS ASSIGNED BY THE LOG (not the producer):
// If producers could set their own offsets, two concurrent producers writing
// to the same partition could both claim offset=42 — creating a collision that
// makes the log unreadable. By making the PartitionLog assign offset = current
// log length (inside a lock), offsets are guaranteed unique, sequential, and
// monotonically increasing. The log owns the ordering, not the producer.
//
// WHY VALUE CAN BE NULL (tombstone):
// A null value is a deliberate deletion signal — it says "this key no longer exists".
// Used by LogCompactor.Compact() to know which keys to remove from the compacted log.
// A consumer reading a null value should delete the corresponding record from its
// local state (e.g. remove the user from a downstream database).
//
// WHY KEY CAN BE NULL (keyless message):
// When Key is null, the producer routes the message via round-robin across all
// partitions instead of hashing to a fixed one. Use this for events that have no
// natural ordering requirement and where even load distribution matters more
// (e.g. anonymous metrics, background job triggers).

using System;
using System.Collections.Generic;

namespace AdvancedDesigns
{
    public class Message
    {
        // The routing key — hashed to determine which partition this message lands on.
        // Same key always → same partition → ordering guaranteed for that key.
        // Null = no ordering needed; producer will round-robin across partitions.
        public string Key { get; }

        // The actual payload. Null signals a tombstone (deletion marker) for this key.
        // Consumers must check for null before processing to handle deletes correctly.
        public string Value { get; }

        // Position within the partition log. Assigned by PartitionLog.Append(), never
        // by the producer. Offsets are 0-based and strictly increasing within a partition.
        // Think of it as the message's permanent address: (topic, partition, offset)
        // uniquely identifies this message in the entire cluster, forever.
        public long Offset { get; set; }

        // Which partition lane this message was placed on. Set by PartitionLog.Append()
        // at the same time as Offset — before append both fields are meaningless defaults.
        public int Partition { get; set; }

        // Wall-clock time the producer created this message. Not used for ordering
        // (offset handles that), but useful for time-based queries and debugging.
        // In production, this is often set to the broker's clock to avoid clock skew
        // between producer machines causing misleading timestamps.
        public DateTime Timestamp { get; } = DateTime.UtcNow;

        // Optional key-value metadata attached to the message without modifying the
        // value payload. Common uses:
        //   "schema-version" → "2"         reader knows which deserialiser to use
        //   "source-service" → "checkout"  tracing — where did this message originate?
        //   "correlation-id" → "req-abc"   tie a message back to the originating request
        // Headers let you evolve routing and processing logic without changing the
        // payload schema — same idea as HTTP headers vs HTTP body.
        public Dictionary<string, string> Headers { get; } = [];

        public Message(string key, string value)
        {
            Key   = key;
            Value = value;
            // Offset and Partition are intentionally left as defaults (0) here.
            // They will be stamped with real values by PartitionLog.Append().
        }

        // Short debug representation showing the full address (partition + offset)
        // plus key and value at a glance. The "(tombstone)" label makes deletion
        // markers immediately visible when tracing message flow in the demo output.
        public override string ToString() =>
            $"[P{Partition}@{Offset}] key={Key ?? "(null)"} value={Value ?? "(tombstone)"}";
    }
}
