using System.Globalization;
using System.Text.Json;

namespace MessageLink.Server;

/// <summary>
/// The server page drawn to the top of Message Link's own window (owner, 2026-11-22): no title bar of the window's own, the page's side list and bar running under
/// the window's buttons, as Windows' Settings does. WinUI's WebView2 does not turn a page's draggable areas into the window's, so the page says where they are and the
/// window hands them to Windows.
/// <list type="bullet">
/// <item><see cref="Script"/> runs in the page on this PC only: it marks the page as in the window (<c>html.in-window</c>, which the page's own style answers by leaving
/// room for the buttons) and posts the areas that drag the window (the band along the top, the bar) and the links and buttons in them, which stay the page's.</item>
/// <item>The window sends the width and height of its buttons back as <c>{"t":"inset","r":…,"h":…}</c> in CSS pixels.</item>
/// <item><see cref="Parse"/> reads a report, refusing anything that isn't one, into the window's physical pixels.</item>
/// </list>
/// Nothing in a report is the page's content: rectangles and a scale.
/// </summary>
internal static class PageWindowChrome
{
    /// <summary>The most rectangles of either kind a report may carry.</summary>
    public const int MostRects = 64;

    /// <summary>The page's area that drags the window, and the parts of it (links, buttons) that stay the page's, in physical pixels from the window's top left.</summary>
    public sealed record Regions(IReadOnlyList<Rect> Caption, IReadOnlyList<Rect> Passthrough);

    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>
    /// The script the window adds to every document, run only on the page at <paramref name="port"/>. The band along the top is the height of the window's buttons
    /// (<c>--caption-height</c>); the side list's name and logo drag the window too in the window, since the list's Overview leads to the same place.
    /// </summary>
    public static string Script(int port) => """
        (() => {
          if (location.origin !== 'https://127.0.0.1:{{PORT}}' || !(window.chrome && chrome.webview)) return;
          const mark = () => { if (!document.documentElement) return false; document.documentElement.classList.add('in-window'); return true; };
          if (!mark()) new MutationObserver((_, watch) => { if (mark()) watch.disconnect(); }).observe(document, { childList: true });
          const live = 'a,button,summary,input,select,textarea,label,[role=button],[tabindex]';
          const box = r => [r.left, r.top, r.width, r.height];
          const meets = (r, c) => r.width > 0 && r.height > 0 && r.left < c.left + c.width && r.left + r.width > c.left && r.top < c.top + c.height && r.top + r.height > c.top;
          let last = '', queued = false;
          const report = () => {
            queued = false;
            const style = getComputedStyle(document.documentElement);
            const band = parseFloat(style.getPropertyValue('--caption-height')) || 48;
            const areas = [{ left: 0, top: 0, width: innerWidth, height: band }];
            const bar = document.querySelector('header.top');
            if (bar) { const r = bar.getBoundingClientRect(); if (r.bottom > 0 && r.height > 0) areas.push(r); }
            const pass = [];
            for (const el of document.querySelectorAll(live)) {
              if (el.closest('.brand')) continue;
              const r = el.getBoundingClientRect();
              if (areas.some(a => meets(r, a))) pass.push(box(r));
              if (pass.length >= {{MOST}}) break;
            }
            const message = { t: 'regions', s: devicePixelRatio, c: areas.map(box), p: pass };
            const text = JSON.stringify(message);
            if (text === last) return;
            last = text;
            chrome.webview.postMessage(message);
          };
          const soon = () => { if (!queued) { queued = true; requestAnimationFrame(report); } };
          chrome.webview.addEventListener('message', e => {
            const d = e.data;
            if (!d || d.t !== 'inset' || !(d.r >= 0) || !(d.h > 0)) return;
            document.documentElement.style.setProperty('--caption-right', d.r + 'px');
            document.documentElement.style.setProperty('--caption-height', d.h + 'px');
            last = '';
            soon();
          });
          addEventListener('DOMContentLoaded', () => { new MutationObserver(soon).observe(document.body, { childList: true, subtree: true, attributes: true }); soon(); });
          addEventListener('load', soon);
          addEventListener('resize', soon);
          addEventListener('scroll', soon, { passive: true, capture: true });
          addEventListener('toggle', soon, { capture: true });
        })();
        """.Replace("{{PORT}}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{{MOST}}", MostRects.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The message telling the page how much room the window's buttons take, in CSS pixels.</summary>
    public static string Inset(double right, double height) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"t\":\"inset\",\"r\":{Math.Max(0, Math.Round(right, 1))},\"h\":{Math.Max(1, Math.Round(height, 1))}}}");

    /// <summary>
    /// A report from <see cref="Script"/> in the window's physical pixels, or null when it isn't one: another kind of message, a scale out of range, too many
    /// rectangles, or a value that isn't a finite number. Rectangles are clipped to the window's top left; empty ones are dropped.
    /// </summary>
    public static Regions? Parse(string? json)
    {
        if (json is not { Length: > 0 and <= 32 * 1024 }) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "regions") return null;
            if (!root.TryGetProperty("s", out var scaleValue) || scaleValue.ValueKind != JsonValueKind.Number) return null;
            var scale = scaleValue.GetDouble();
            if (scale is not (>= 0.25 and <= 8)) return null;
            if (Rects(root, "c", scale) is not { } caption || Rects(root, "p", scale) is not { } passthrough) return null;
            return new Regions(caption, passthrough);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The caption with the links and buttons cut out of it, as rectangles that cover the rest. Windows kept answering "caption" over a passthrough rectangle that lay
    /// inside one (the window's hit test on the page's Sign out, 2026-11-22), so the window never asks it to: what it hands over as caption already leaves them out.
    /// At most <paramref name="most"/> rectangles; past that the rest of the caption is left to the page rather than a link made a drag area.
    /// </summary>
    public static IReadOnlyList<Rect> CaptionWithoutHoles(Regions regions, int most = 256)
    {
        var pieces = regions.Caption.ToList();
        foreach (var hole in regions.Passthrough)
        {
            var next = new List<Rect>(pieces.Count + 4);
            foreach (var area in pieces)
            {
                int left = Math.Max(area.X, hole.X), top = Math.Max(area.Y, hole.Y);
                int right = Math.Min(area.X + area.Width, hole.X + hole.Width), bottom = Math.Min(area.Y + area.Height, hole.Y + hole.Height);
                if (right <= left || bottom <= top) { next.Add(area); continue; }
                // Above and below the hole across the area's width; beside it, only as tall as the hole is within the area.
                if (top > area.Y) next.Add(new Rect(area.X, area.Y, area.Width, top - area.Y));
                if (bottom < area.Y + area.Height) next.Add(new Rect(area.X, bottom, area.Width, area.Y + area.Height - bottom));
                if (left > area.X) next.Add(new Rect(area.X, top, left - area.X, bottom - top));
                if (right < area.X + area.Width) next.Add(new Rect(right, top, area.X + area.Width - right, bottom - top));
            }
            pieces = next.Count > most ? [.. pieces.Where(area => !Meets(area, hole))] : next;
        }
        return pieces;
    }

    private static bool Meets(Rect a, Rect b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static List<Rect>? Rects(JsonElement root, string name, double scale)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MostRects) return null;
        var rects = new List<Rect>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 4) return null;
            var v = new double[4];
            var i = 0;
            foreach (var number in item.EnumerateArray())
            {
                if (number.ValueKind != JsonValueKind.Number || !double.IsFinite(number.GetDouble()) || Math.Abs(number.GetDouble()) > 100_000) return null;
                v[i++] = number.GetDouble();
            }
            // Whole pixels that cover the rectangle, clipped to the window.
            var left = Math.Max(0, (int)Math.Floor(v[0] * scale));
            var top = Math.Max(0, (int)Math.Floor(v[1] * scale));
            var right = (int)Math.Ceiling((v[0] + v[2]) * scale);
            var bottom = (int)Math.Ceiling((v[1] + v[3]) * scale);
            if (right > left && bottom > top) rects.Add(new Rect(left, top, right - left, bottom - top));
        }
        return rects;
    }
}
