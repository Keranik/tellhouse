using Microsoft.AspNetCore.Html;

namespace HomeNode.Services;

// The small stroke icons the trace and the pages use, inline so the
// pages carry no icon font and no extra request. Decorative only:
// every icon sits beside words that say the same thing.
public static class Icons
{
    public static HtmlString Get(string name, int size = 16)
    {
        var paths = name switch
        {
            "spark" => "<path d=\"M12 3.5l1.9 5.1 5.1 1.9-5.1 1.9L12 17.5l-1.9-5.1L5 10.5l5.1-1.9z\"/><path d=\"M18.5 16.5l.7 1.8 1.8.7-1.8.7-.7 1.8-.7-1.8-1.8-.7 1.8-.7z\"/>",
            "shield" => "<path d=\"M12 3l7 3v5c0 5-3.5 8.2-7 10-3.5-1.8-7-5-7-10V6z\"/><path d=\"M9 11.8l2.2 2.2 4-4.2\"/>",
            "person" => "<circle cx=\"12\" cy=\"8\" r=\"3.6\"/><path d=\"M4.5 20.5c.6-4 3.8-6.5 7.5-6.5s6.9 2.5 7.5 6.5\"/>",
            "send" => "<path d=\"M21 3 10.5 13.5\"/><path d=\"M21 3l-6.5 18-4-7.5L3 9.5z\"/>",
            "lock" => "<rect x=\"5\" y=\"11\" width=\"14\" height=\"10\" rx=\"2\"/><path d=\"M8.5 11V7.5a3.5 3.5 0 0 1 7 0V11\"/>",
            "seed" => "<path d=\"M5 20c0-8.5 5.5-14 15-15-1 9.5-6.5 15-15 15z\"/><path d=\"M5 20l7.5-7.5\"/>",
            "clock" => "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 7.5V12l3 2\"/>",
            "stop" => "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M9 9l6 6M15 9l-6 6\"/>",
            "alert" => "<path d=\"M12 3.5 2.8 19.5h18.4z\"/><path d=\"M12 10v4.5M12 17.2v.01\"/>",
            "check" => "<path d=\"M5 12.5l4.5 4.5L19 7.5\"/>",
            "pen" => "<path d=\"M4 20h4L19.5 8.5l-4-4L4 16z\"/>",
            "house" => "<path d=\"M3 10.5 12 3l9 7.5\"/><path d=\"M5 9.5V21h14V9.5\"/>",
            "record" => "<rect x=\"5\" y=\"3\" width=\"14\" height=\"18\" rx=\"2\"/><path d=\"M9 8h6M9 12h6M9 16h3.5\"/>",
            "inbox" => "<path d=\"M22 12h-6l-2 3h-4l-2-3H2\"/><path d=\"M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z\"/>",
            "arrow" => "<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
            "chat" => "<path d=\"M4 5h16v11H9l-5 4z\"/>",
            "code" => "<path d=\"M8 7l-5 5 5 5M16 7l5 5-5 5\"/>",
            "mic" => "<rect x=\"9\" y=\"3\" width=\"6\" height=\"11\" rx=\"3\"/><path d=\"M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21\"/>",
            "bulb" => "<path d=\"M9 18h6M10 21h4\"/><path d=\"M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z\"/>",
            "eye" => "<path d=\"M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>",
            "image" => "<rect x=\"3.5\" y=\"4.5\" width=\"17\" height=\"15\" rx=\"2\"/><circle cx=\"9\" cy=\"10\" r=\"1.8\"/><path d=\"M20.5 16l-5-5-8 8.5\"/>",
            "camera" => "<path d=\"M4 8h3l2-3h6l2 3h3v11H4z\"/><circle cx=\"12\" cy=\"13\" r=\"3.5\"/>",
            _ => "<circle cx=\"12\" cy=\"12\" r=\"3\"/>"
        };
        return new HtmlString(
            $"<svg viewBox=\"0 0 24 24\" width=\"{size}\" height=\"{size}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{paths}</svg>");
    }
}
