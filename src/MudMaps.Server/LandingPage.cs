using System.Net;
using System.Text;

namespace MudMaps.Server;

/// <summary>A plain chooser page listing the hosted maps. Static text only, no script; links are relative so it works under any path base.</summary>
public static class LandingPage
{
    private static readonly Dictionary<string, (string Title, string Line)> Known = new()
    {
        ["colony"] = ("The Mud Colony", "An ant farm of code; ants walk where this month's pull requests went."),
        ["city"] = ("Where Mud runs hot", "Every Mud repo as one city, lit by pull requests."),
        ["islands"] = ("The Mud Archipelago", "A printed sea chart; red flags mark pull requests."),
        ["space"] = ("The Mud system", "A star for every repo, its code in orbit; moons are pull requests."),
    };

    public static string Render(IEnumerable<string> segments)
    {
        var items = new StringBuilder();
        foreach (var s in segments)
        {
            var (title, line) = Known.TryGetValue(s, out var k) ? k : (s, "");
            items.Append($"<li><a href=\"{WebUtility.HtmlEncode(s)}/\">{WebUtility.HtmlEncode(title)}</a><span>{WebUtility.HtmlEncode(line)}</span></li>");
        }
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Mud Maps</title><style>
            body{margin:0;background:#0b0605;color:#f2eadd;font:17px/1.55 system-ui,sans-serif}main{max-width:720px;margin:0 auto;padding:56px 24px}
            h1{font-weight:500;font-size:2.6rem;margin:0 0 8px}p{color:#b3a192;margin:0 0 32px}ul{list-style:none;padding:0;margin:0}
            li{padding:16px 0;border-top:1px solid #2b2320}a{color:#fe6a00;font-size:1.25rem;text-decoration:none;display:block}
            a:hover{text-decoration:underline}a:focus-visible{outline:2px solid #fe6a00;outline-offset:3px}span{color:#b3a192}
            </style></head><body><main><h1>Mud Maps</h1><p>The Mud ecosystem's code, lit by this month's pull requests.</p><ul>{{items}}</ul></main></body></html>
            """;
    }
}
