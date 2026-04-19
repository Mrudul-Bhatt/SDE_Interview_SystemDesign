# Pub/Sub & Async

---

## 26. Explain the Pub/Sub Pattern. How does it differ from a message queue?

### What is Pub/Sub?

Pub/Sub (Publish-Subscribe) is a messaging pattern where **publishers send messages to a topic** without knowing who will receive them, and **subscribers receive messages from topics** they care about without knowing who sent them.

The publisher and subscriber are completely **decoupled** — they don't know about each other.

```
[Publisher A] ─────→ [Topic: order_events] ─────→ [Subscriber 1: Email Service]
[Publisher B] ─────→                        ─────→ [Subscriber 2: Analytics Service]
                                             ─────→ [Subscriber 3: Inventory Service]
```

Every subscriber gets a **copy** of every message published to the topic.

---

### How Pub/Sub Works

**Step 1 — Subscribers register interest in a topic:**
```python
# Email Service subscribes to order_events
pubsub.subscribe("order_events", handler=send_confirmation_email)

# Analytics Service subscribes to the same topic
pubsub.subscribe("order_events", handler=record_sale_metric)
```

**Step 2 — Publisher sends a message (doesn't know who's listening):**
```python
# Order Service publishes — has no idea who subscribes
pubsub.publish("order_events", {
    "order_id": 12345,
    "user_id": 1,
    "total": 99.99,
    "items": [...]
})
```

**Step 3 — All subscribers receive the message simultaneously:**
```
Email Service     → receives message → sends confirmation email
Analytics Service → receives message → records sale
Inventory Service → receives message → decrements stock
```

---

### The Core Difference — Pub/Sub vs. Message Queue

#### Message Queue — Point-to-Point

One message is consumed by **exactly one consumer**. Consumers compete for messages. Once consumed, the message is gone.

```
[Producer] → [Queue] → [Consumer 1] ← gets the message
                     → [Consumer 2] ← sits idle (C1 got it)
                     → [Consumer 3] ← sits idle (C1 got it)
```

Think of it like a **task list** — each task is done by one worker.

```python
# Order processing queue — one worker handles each order
while True:
    order = queue.receive()      # only one consumer gets this
    process_order(order)
    queue.delete(order)          # gone forever
```

**Use cases:** Work queues, job processing, load distribution among workers.

#### Pub/Sub — Broadcast

One message is delivered to **all subscribers**. Every subscriber gets its own copy.

```
[Publisher] → [Topic] → [Subscriber 1] ← gets a copy
                      → [Subscriber 2] ← gets a copy
                      → [Subscriber 3] ← gets a copy
```

Think of it like a **radio broadcast** — one transmission, many receivers.

```python
# Order event topic — every subscriber gets a copy
pubsub.publish("order_placed", order_data)

# Email Service: gets copy → sends email
# Analytics:     gets copy → records metric
# Warehouse:     gets copy → starts fulfillment
```

**Use cases:** Event broadcasting, fan-out notifications, real-time updates.

---

### Side-by-Side Comparison

| Dimension | Message Queue | Pub/Sub |
|-----------|--------------|---------|
| Delivery | One consumer gets the message | All subscribers get a copy |
| Consumer model | Competing consumers (load sharing) | Independent subscribers |
| Message retention | Gone after consumption | Delivered to all, then gone (or retained) |
| Coupling | Producer knows message type | Publisher and subscriber fully decoupled |
| Scaling reads | Add more consumers = more throughput | Each subscriber processes independently |
| Use case | Task distribution, work queues | Event broadcasting, fan-out |
| Example | AWS SQS, RabbitMQ queue | AWS SNS, Google Pub/Sub, Kafka topics |

---

### Fan-Out Pattern — Combining Both

In practice, large systems combine pub/sub and queues using the **SNS + SQS fan-out pattern**:

```
[Order Service]
      ↓ publishes
[SNS Topic: order_placed]   ← Pub/Sub (fan-out)
      ↓          ↓          ↓
  [SQS Queue] [SQS Queue] [SQS Queue]  ← each service has its own queue
      ↓              ↓           ↓
[Email Workers] [Analytics] [Warehouse Workers]
   (multiple)    (single)      (multiple)
```

**Why this pattern:**
- SNS broadcasts to all queues (pub/sub)
- Each service has its own SQS queue (isolation — one slow service doesn't block others)
- Each queue can have multiple consumers (load sharing within each service)
- If Email Service is down, its queue buffers messages — no message loss

---

### Push vs. Pull Delivery

**Push (most pub/sub systems):**
The broker pushes messages to subscribers as they arrive. Low latency, but subscriber must handle the rate.

```
[Broker] ──pushes──→ [Subscriber]
                          ↑
                   must process at broker's pace
```

**Pull (Kafka, SQS):**
Subscribers poll the broker for new messages at their own pace. Better backpressure handling.

```
[Broker] ←──polls──── [Subscriber]
                           ↑
                    processes at its own pace
```

---

### Real-World Pub/Sub Examples

**E-commerce order placed:**
```
[Order Service] publishes "order_placed"
→ [Email Service]     sends confirmation
→ [SMS Service]       sends text notification
→ [Inventory Service] reserves stock
→ [Analytics Service] records conversion
→ [Fraud Service]     runs fraud check
→ [Loyalty Service]   adds points
```

Adding a new service requires **zero changes** to Order Service — just subscribe to the topic.

**Social media new post:**
```
[Post Service] publishes "post_created"
→ [Feed Service]          adds to followers' feeds
→ [Notification Service]  notifies tagged users
→ [Search Indexer]        indexes for search
→ [Moderation Service]    checks content policy
```

**Stock price update:**
```
[Price Feed] publishes "AAPL:price_updated" every second
→ [Trading UI]      updates charts
→ [Alert Service]   checks price alerts
→ [Options Pricing] recalculates options
→ [Risk System]     updates portfolio exposure
```

---

### Kafka — Pub/Sub with Superpowers

Apache Kafka is the dominant pub/sub system for high-throughput use cases. It adds key features on top of basic pub/sub:

**Message retention:**
Messages aren't deleted after consumption. Retained for a configurable period (days, weeks, forever).

```
Consumer Group A reads at offset 1000
Consumer Group B reads at offset 500 (catching up)
New Consumer Group C starts at offset 0 (replay all history)
```

**Consumer groups:**
Multiple instances of the same service share the work (like a queue), while different services each get all messages (like pub/sub).

```
[Kafka Topic: orders]
      ↓                         ↓
[Email Consumer Group]   [Analytics Consumer Group]
  [Instance 1]              [Instance 1]
  [Instance 2]    ← load    [Instance 2]    ← load
  [Instance 3]      shared  [Instance 3]      shared
```

Each group gets all messages, but within a group, each message goes to one instance.

**Ordering guarantees:**
Within a partition, messages are strictly ordered. Kafka assigns related messages to the same partition using a key.

```
partition key = user_id
→ All orders for user 1 go to partition 3 (in order)
→ All orders for user 2 go to partition 7 (in order)
```

---

## 27. What is a Dead Letter Queue?

### The Problem — Poison Pills

Some messages can't be processed:

- **Malformed message:** JSON with missing required field
- **Business logic error:** Order references a deleted product
- **Downstream service down:** Payment service unavailable
- **Bug in consumer code:** Unexpected data causes an exception

Without a safety valve, a bad message gets retried forever — **blocking the queue**.

```
[Queue]
[Message A: valid]    ← waiting
[Message B: BROKEN]   ← consumer fails → retried → fails → retried → fails...
[Message C: valid]    ← waiting
[Message D: valid]    ← waiting
→ Messages A, C, D stuck behind the poison pill
```

---

### What is a Dead Letter Queue?

A **Dead Letter Queue (DLQ)** is a separate queue where messages are sent after failing to process a configurable number of times. It acts as a quarantine for problematic messages.

```
[Main Queue] → [Consumer] → fails 3 times
                                 ↓
                          [Dead Letter Queue]
                                 ↓
                    Engineers inspect and fix
```

The main queue is unblocked. The broken message is isolated for investigation.

---

### How It Works

**Configuration:**
```
Main Queue settings:
- Max receive count: 3            ← retry up to 3 times
- Dead letter queue: orders-dlq   ← where to send after max retries
```

**Flow:**
```
t=0:    Message arrives in main queue
t=0:    Consumer picks it up → throws exception → message becomes visible again
t=5s:   Consumer picks it up (retry 1) → fails again
t=25s:  Consumer picks it up (retry 2) → fails again
t=125s: Consumer picks it up (retry 3) → fails again
t=125s: Receive count = 3 = max → message moved to DLQ automatically
```

Main queue is now clear. DLQ holds the broken message.

---

### What Happens to Messages in the DLQ?

**1. Alerting**
Monitor DLQ size. If messages appear, alert on-call engineers.

```python
# CloudWatch alarm: alert if DLQ has > 0 messages
alarm = cloudwatch.put_metric_alarm(
    AlarmName='orders-dlq-not-empty',
    MetricName='ApproximateNumberOfMessagesVisible',
    Threshold=0,
    ComparisonOperator='GreaterThanThreshold'
)
```

**2. Inspection**
Engineers read messages from the DLQ to understand why they failed.

```python
messages = dlq.receive_messages(MaxNumberOfMessages=10)
for msg in messages:
    print(json.loads(msg.body))   # inspect the payload
    print(msg.attributes)         # see error details, receive count
```

**3. Fix the Bug**
Identify root cause — malformed data, missing field, code bug, dependency issue.

**4. Replay**
After fixing, move messages back to the main queue to be reprocessed.

```python
def replay_dlq(dlq, main_queue):
    while True:
        messages = dlq.receive_messages(MaxNumberOfMessages=10)
        if not messages:
            break
        for msg in messages:
            main_queue.send_message(MessageBody=msg.body)
            msg.delete()   # remove from DLQ after replaying
```

**5. Discard**
If the message is truly unrecoverable (references data that no longer exists), delete it after logging.

---

### DLQ Configuration Examples

**AWS SQS:**
```json
{
  "deadLetterTargetArn": "arn:aws:sqs:us-east-1:123456789:orders-dlq",
  "maxReceiveCount": 3
}
```

**Kafka (using error topic):**
```python
try:
    process_message(msg)
except Exception as e:
    producer.send('orders-dlq', value=msg.value, headers=[
        ('error', str(e).encode()),
        ('original_topic', b'orders'),
        ('failed_at', str(time.time()).encode())
    ])
    consumer.commit()   # commit offset so main topic moves on
```

**RabbitMQ:**
```python
channel.queue_declare(
    queue='orders',
    arguments={
        'x-dead-letter-exchange': 'dlx',
        'x-dead-letter-routing-key': 'orders-dlq',
        'x-message-ttl': 30000
    }
)
```

---

### DLQ Best Practices

**1. Always have a DLQ for production queues**
Without one, poison pills block queues silently. DLQs make failures visible.

**2. Set appropriate max receive counts**
Too low (1-2): transient errors send messages to DLQ unnecessarily.
Too high (10+): broken messages retry for a long time before quarantine.
**Recommended: 3-5 retries** with exponential backoff between attempts.

**3. Add metadata when sending to DLQ**
Include error message, stack trace, original topic, timestamp.

```python
dlq.send_message(
    MessageBody=original_message,
    MessageAttributes={
        'ErrorMessage':   {'StringValue': str(exception), 'DataType': 'String'},
        'OriginalQueue':  {'StringValue': 'orders',       'DataType': 'String'},
        'FailedAt':       {'StringValue': datetime.now().isoformat(), 'DataType': 'String'},
        'AttemptCount':   {'StringValue': '3',            'DataType': 'Number'}
    }
)
```

**4. Monitor DLQ size**
A growing DLQ signals a systemic problem — alert before it becomes critical.

**5. Set a DLQ retention period**
Messages shouldn't stay forever. Set a retention period (e.g., 14 days) to prevent unbounded growth.

**6. Test your DLQ**
Intentionally send a malformed message in staging and verify it ends up in the DLQ and triggers an alert.

---

### DLQ vs. Retry Queue

**Retry Queue:** Messages are requeued with a delay after failure, for automatic retries with backoff.
```
Fail → wait 1s → retry → wait 2s → retry → wait 4s → retry → DLQ
```

**Dead Letter Queue:** Final destination after all retries are exhausted. Requires human intervention.

Some systems implement both:
```
[Main Queue] → fail → [Retry Queue: 30s delay] → fail → [Retry Queue: 5min delay] → fail → [DLQ]
```

---

### Real-World Scenario

**Legitimate failure (card declined):**
```
[Payment Consumer] → tries to charge card → card declined
                  → retried 3 times → moved to DLQ

[Alert fires] → engineer investigates
[Engineer]    → sees: "Card declined for order 12345"
              → emails customer, discards from DLQ
```

**Code bug:**
```
[Payment Consumer] → tries to charge → NullPointerException
                  → retried 3 times → moved to DLQ

[Alert fires] → engineer investigates
[Engineer]    → finds bug → deploys fix
              → replays all DLQ messages → all process successfully
```

The DLQ makes both scenarios **visible and recoverable** instead of silently lost or permanently stuck.
