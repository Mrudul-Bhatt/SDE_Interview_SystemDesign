// UrlFrontier — two-level queue: priority ordering + per-domain politeness.
//
// Architecture mirrors Mercator (the Google crawler):
//   Front queues: ordered by CrawlPriority (High → Medium → Low)
//   Back queues:  one per domain, enforcing crawl-delay between consecutive requests
//
// TryDequeue() picks the highest-priority task whose domain's crawl-delay has elapsed.
// If all domains are throttled, it returns null — the caller sleeps briefly and retries.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedDesigns
{
    public class UrlFrontier
    {
        // Single list for simplicity; real system uses separate priority queues.
        // Ordering is applied at dequeue time via LINQ, not at enqueue time —
        // inserts are O(1) and we sort only the small set of candidates at dequeue.
        private readonly List<UrlTask>                _queue       = new();
        private readonly Dictionary<string, DateTime> _lastCrawled = new();
        private readonly RobotsCache                  _robots;
        private readonly object _lock = new();

        public int Count { get { lock (_lock) return _queue.Count; } }

        public UrlFrontier(RobotsCache robots) => _robots = robots;

        public void Enqueue(UrlTask task)
        {
            // lock: multiple crawler threads enqueue discovered links concurrently.
            lock (_lock) _queue.Add(task);
        }

        // Returns the next crawlable task or null if all domains are throttled.
        public UrlTask TryDequeue()
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;

                // Sort by priority first, then depth (breadth-first within same priority)
                // to avoid crawling deep dead-end paths before shallower important ones.
                var task = _queue
                    .OrderBy(t => (int)t.Priority)
                    .ThenBy(t => t.Depth)
                    .FirstOrDefault(t =>
                    {
                        // Domain never crawled → immediately available.
                        if (!_lastCrawled.TryGetValue(t.Domain, out DateTime last))
                            return true;

                        // Domain crawled before → check if crawl-delay has elapsed.
                        int delayMs = _robots.GetCrawlDelayMs(t.Domain);
                        return (now - last).TotalMilliseconds >= delayMs;
                    });

                if (task == null) return null;

                _queue.Remove(task);
                // Stamp the domain's last-crawled time so subsequent dequeues respect the delay.
                _lastCrawled[task.Domain] = now;
                return task;
            }
        }

        public IReadOnlyDictionary<string, DateTime> LastCrawled => _lastCrawled;
    }
}
