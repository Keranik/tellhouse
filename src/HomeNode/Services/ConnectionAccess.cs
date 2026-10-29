using HomeNode.Data;

namespace HomeNode.Services;

// One place where a connection's stated access level is read as a
// delivery rule. The inviter card states the levels in
// plain words (DoorService.LevelConsequencesFor); this class is the
// enforcement half of those same words, and every delivery path
// (the sealed outbox, the confirmation dispatcher's fallback, and
// plan landings) consults it, so a level means the same thing on
// every road a delivery can take.
//
// The levels the house offers, and what they may receive:
//   Standard: deliveries meant for the person, except health class
//     material. The card says so outright: a Standard connection
//     cannot hear health related entries.
//   KnockOnly: nothing. The card says no packets, no questions,
//     no deliveries, and a connection at this level stands
//     KnockOnly from the moment it is confirmed (see DoorService),
//     never silently Active.
// No level the house offers lets a connection hear health class
// material. A level that may hear it would have to be added here
// and on the card together, deliberately; an unknown level fails
// closed and hears no health material either.
public static class ConnectionAccess
{
    public const string StandardLevel = "Standard";
    public const string KnockOnlyLevel = "KnockOnly";

    public static bool IsKnockOnlyLevel(string? level) =>
        string.Equals(level, KnockOnlyLevel, StringComparison.Ordinal);

    // The standing a confirmed join takes, from the level the
    // inviter chose: a KnockOnly invite confirms to a KnockOnly
    // standing connection, so the intake standing checks that
    // already exist govern it from its first packet.
    public static ConnectionStanding StandingForLevel(string? level) =>
        IsKnockOnlyLevel(level) ? ConnectionStanding.KnockOnly : ConnectionStanding.Active;

    // May this connection receive deliveries at all?
    public static bool AllowsDelivery(HouseConnection connection) =>
        connection.Standing == ConnectionStanding.Active && !IsKnockOnlyLevel(connection.Level);

    // May this connection hear health class material? Among the
    // levels the house offers, none may: Standard excludes it in
    // plain words and KnockOnly excludes everything.
    public static bool LevelAllowsHealth(string? level) => false;

    public static bool AllowsHealthDelivery(HouseConnection connection) =>
        AllowsDelivery(connection) && LevelAllowsHealth(connection.Level);

    // Health class material, read from the record: a health
    // mention, or any entry stored Restricted (the class health
    // mentions always take, and the class a health-adjacent entry
    // such as a clinic whereabouts is stored under).
    public static bool IsHealthClass(Event ev) =>
        ev.Type == EventType.HealthMention
        || ev.SensitivityClass == SensitivityClass.Restricted;
}
