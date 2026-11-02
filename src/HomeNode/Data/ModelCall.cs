namespace HomeNode.Data;

// One call to a Meta model that is not a message reading: hearing a
// voice clip (Muse Voice Transcribe), reviewing the record (Muse
// Wonder), counting a supply shelf (SAM 3.1), or drawing a picture
// card (Muse Image). Message readings stay in ModelReadings; this
// table is the rest of the models' work, kept so the pages can show
// which model did what, and so the models page can count it.
//
// What is kept, by purpose:
//   - Hear: timing and the length of the clip only. The words land
//     on the record through the normal message path, as the person
//     sent them (after any edit); the transcript itself is not kept
//     here, and the audio is never stored.
//   - Review: the validated findings, shown only to the person who
//     asked for the review (PersonId).
//   - See: the counts and outlines per supply, and the photo the
//     person chose to send, downscaled in their browser first.
//   - Draw: the picture card, shown with the delivery it belongs to.
//
// No foreign keys: a restored snapshot that predates this table gets
// it at startup (Program.cs), and nothing else in the record changes.
public class ModelCall
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Purpose { get; set; } = "";
    public string Model { get; set; } = "";
    // "live" when the Meta Model API answered; "mock" for the house's
    // rules-only stand-in (the review has one; hearing, seeing and
    // drawing do not, and are simply unavailable off Live).
    public string Engine { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int ElapsedMs { get; set; }
    public int? PersonId { get; set; }
    public int? ReadingId { get; set; }
    public int? DeliveryId { get; set; }
    public int? EventId { get; set; }
    public string? OutputJson { get; set; }
    public byte[]? Image { get; set; }
    public string? ImageType { get; set; }
    public DateTime AtUtc { get; set; }
}

public static class ModelCallPurpose
{
    public const string Hear = "hear";
    public const string Review = "review";
    public const string See = "see";
    public const string Draw = "draw";
}

public static class ModelCallOutcome
{
    public const string Done = "done";
    public const string Empty = "empty";
    public const string Failed = "failed";
}
