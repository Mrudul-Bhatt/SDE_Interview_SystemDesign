// Domain enums for the dispatch flow and trip lifecycle.
//
// DriverStatus is the four-state machine each driver lives in:
//   Offline      — not accepting offers (app closed, off-shift)
//   Available    — open to receive an offer
//   PendingOffer — atomically claimed by a dispatch attempt for ~15s
//   OnTrip       — accepted and currently transporting a rider
// The PendingOffer state is what makes "two riders can't get the same driver"
// safe — SET NX atomically transitions Available → PendingOffer.
//
// TripStatus walks the lifecycle: Requested → DriverAssigned → InProgress →
// Completed, with Cancelled and Failed as terminal off-ramps. Transitions are
// guarded by TripService.

public enum DriverStatus { Offline, Available, PendingOffer, OnTrip }

public enum TripStatus
{
    Requested, DriverAssigned, InProgress,
    Completed, Cancelled, Failed
}
