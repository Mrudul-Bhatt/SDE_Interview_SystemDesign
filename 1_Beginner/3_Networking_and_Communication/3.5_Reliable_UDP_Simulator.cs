// Q3. Implement a Reliable UDP Simulator
// Simulate TCP-like reliability over UDP: assign sequence numbers to packets,
// send ACKs, and retransmit lost packets.  Demonstrates how TCP achieves delivery
// guarantees that raw UDP deliberately omits.

using System;
using System.Collections.Generic;
using System.Threading;

// ---------------------------------------------------------------------------
// ReliableUDPSender — layered reliability on top of a lossy channel
// ---------------------------------------------------------------------------
public class ReliableUDPSender
{
    // Monotonically increasing counter: each packet gets a unique seq# so the
    // receiver can detect gaps (missing packet) and duplicates (retransmit arrived twice).
    private int _nextSeqNum = 0;

    // Maps seq# -> payload for every packet sent but not yet ACK'd.
    // Entry is created on Send() and removed on ReceiveAck().
    // If it survives longer than _timeoutMs, RetransmitTimedOut() re-sends it.
    private readonly Dictionary<int, string> _unacknowledged = new();

    // How long (ms) the sender waits for an ACK before declaring the packet lost.
    // TCP calls this the Retransmission Timeout (RTO); it adapts dynamically in production.
    private readonly int _timeoutMs;

    // Records wall-clock time of the most recent send attempt per seq#.
    // On retransmit we reset this timestamp so the next timeout window starts fresh.
    private readonly Dictionary<int, DateTime> _sentAt = new();

    // Deterministic loss simulation: drop every Nth transmission (original or retransmit).
    // Real UDP loss is random; a fixed modulo makes demos reproducible and predictable.
    private readonly int _dropEveryN;

    // Global counter incremented on every TransmitPacket call (both sends and retransmits).
    // The modulo check against _dropEveryN decides whether this transmission is dropped.
    private int _sendCount = 0;

    // timeoutMs  — ACK wait before retransmit (mirrors TCP RTO).
    // dropEveryN — simulate packet loss by dropping every Nth call to TransmitPacket.
    public ReliableUDPSender(int timeoutMs = 500, int dropEveryN = 3)
    {
        _timeoutMs = timeoutMs;
        _dropEveryN = dropEveryN;
    }

    // Public API: caller sends data by name; seq# assignment is internal.
    public void Send(string data)
    {
        // Post-increment: seqNum gets the current value, _nextSeqNum increments for next call.
        int seqNum = _nextSeqNum++;

        // Store payload so retransmits can re-send the same bytes without caller involvement.
        _unacknowledged[seqNum] = data;

        // Record send time; RetransmitTimedOut() measures elapsed from this stamp.
        _sentAt[seqNum] = DateTime.UtcNow;

        TransmitPacket(seqNum, data, isRetransmit: false);
    }

    // The "wire send": increments the global counter and applies the drop simulation.
    // isRetransmit=true is logged so you can see which packets needed a second attempt.
    private void TransmitPacket(int seqNum, string data, bool isRetransmit)
    {
        _sendCount++;

        // When _sendCount is divisible by _dropEveryN, simulate the packet vanishing
        // in transit (router queue overflow, bit-flip discard, etc.).
        bool dropped = _sendCount % _dropEveryN == 0;

        if (dropped)
        {
            // Packet "leaves" the sender but never reaches the receiver.
            // _unacknowledged[seqNum] remains set -> timeout fires -> retransmit.
            Console.WriteLine($"  [DROPPED] Seq={seqNum} data='{data}'" +
                              (isRetransmit ? " (retransmit)" : ""));
        }
        else
        {
            Console.WriteLine($"  [SENT]    Seq={seqNum} data='{data}'" +
                              (isRetransmit ? " (retransmit)" : ""));

            // In a real system the receiver sends an ACK over the network back to us.
            // Here we call ReceiveAck directly to simulate in-process ACK delivery.
            ReceiveAck(seqNum);
        }
    }

    // Called when an ACK arrives (real: from network thread; here: from TransmitPacket).
    // Removes the seq# from both maps -- the packet is safely delivered, no more tracking.
    public void ReceiveAck(int seqNum)
    {
        if (_unacknowledged.ContainsKey(seqNum))
        {
            Console.WriteLine($"  [ACK]     Seq={seqNum} acknowledged");
            _unacknowledged.Remove(seqNum); // no longer needs retransmitting
            _sentAt.Remove(seqNum);         // stop measuring its age
        }
    }

    // Called periodically (e.g. on a timer thread) to find and retransmit stale packets.
    // In TCP this runs as part of the kernel's retransmission timer (RFC 6298).
    public void RetransmitTimedOut()
    {
        var now = DateTime.UtcNow;

        // Snapshot _sentAt before iterating: TransmitPacket -> ReceiveAck may modify
        // _sentAt during the loop, causing a "collection modified" InvalidOperationException.
        foreach (var (seqNum, sentAt) in new Dictionary<int, DateTime>(_sentAt))
        {
            if ((now - sentAt).TotalMilliseconds > _timeoutMs)
            {
                Console.WriteLine($"  [TIMEOUT] Seq={seqNum} — retransmitting");

                // Reset the timer so if this retransmit also drops, the next check
                // will fire again after another _timeoutMs window.
                _sentAt[seqNum] = now;

                TransmitPacket(seqNum, _unacknowledged[seqNum], isRetransmit: true);
            }
        }
    }

    // Zero means all packets have been ACK'd — transmission complete.
    public int UnacknowledgedCount => _unacknowledged.Count;
}

// ---------------------------------------------------------------------------
// Entry point — demo
// ---------------------------------------------------------------------------
public class Program
{
    public static void Main()
    {
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Q3: Reliable UDP Simulator          ║");
        Console.WriteLine("╚══════════════════════════════════════╝");

        // dropEveryN=3 -> 3rd global send is dropped; timeoutMs=200 -> retransmit after 200ms.
        var sender = new ReliableUDPSender(timeoutMs: 200, dropEveryN: 3);

        Console.WriteLine("\n=== Sending 3 packets (every 3rd is dropped) ===");
        sender.Send("Hello");   // sendCount=1 -> NOT dropped -> ACK received
        sender.Send("World");   // sendCount=2 -> NOT dropped -> ACK received
        sender.Send("Dropped"); // sendCount=3 -> dropped (3 % 3 == 0) -> no ACK

        Console.WriteLine($"\nUnacknowledged packets: {sender.UnacknowledgedCount}"); // 1

        // Sleep longer than timeoutMs so "Dropped" is eligible for retransmit.
        Thread.Sleep(250);

        Console.WriteLine("\n=== Checking for timeouts and retransmitting ===");
        // Retransmit of seq=2 is sendCount=4 -> NOT dropped -> ACK received.
        sender.RetransmitTimedOut();

        Console.WriteLine($"Unacknowledged after retry: {sender.UnacknowledgedCount}"); // 0

        Console.WriteLine("\n--- How TCP achieves reliability over an unreliable channel ---");
        Console.WriteLine("  1. Sequence numbers  -> receiver detects missing or out-of-order packets");
        Console.WriteLine("  2. ACKs              -> sender knows which packets arrived safely");
        Console.WriteLine("  3. Retransmission    -> sender re-sends after timeout with no ACK");
        Console.WriteLine("  4. Ordering          -> receiver buffers OOO packets, delivers in order");
        Console.WriteLine("\n  UDP applications that add their own reliability selectively:");
        Console.WriteLine("    QUIC / HTTP3  -> per-stream reliability, avoids head-of-line blocking");
        Console.WriteLine("    Game engines  -> reliable for events (player join), unreliable for position");
    }
}
