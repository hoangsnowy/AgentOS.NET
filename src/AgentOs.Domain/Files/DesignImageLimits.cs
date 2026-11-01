// AgentOs.Domain/Files/DesignImageLimits.cs
// Design gate (ADR-0016) — what a screenshot may be to travel to a server model as an image, and how big it is.
//
// Anthropic's vision limits (platform.claude.com/docs/en/build-with-claude/vision, checked 2026-11-01): at most
// 8000 × 8000 px per image; more than 20 images in one request switches every image to a stricter many-image limit
// (keep ≤ 20 images, or ≤ 3000 px a side); 10 MB base64 per image on the Claude API but 5 MB base64 on Amazon Bedrock
// and Google-managed Vertex; 128 MB per request on the first-party API ("other platforms have their own limits").
// Long images are downscaled server-side to ~1568 px (2576 px on the newest models) on the long edge, so a very tall
// full-page capture loses its detail anyway — the Testing agent is asked for at most RequestedMaxHeight.
// We hold every provider to the strictest of those (no image library is in the solution, so nothing is resized here):
// an image over a limit is left out with a plain reason the Designer's prompt states.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AgentOs.Domain.Files;

/// <summary>The limits on screenshots sent to a model as images, and a header-only size reader.</summary>
public static class DesignImageLimits
{
    /// <summary>The longest side a model takes (Anthropic: 8000 × 8000 px with ≤ 20 images in the request).</summary>
    public const int MaxEdgePixels = 8000;

    /// <summary>The tallest screenshot the Testing agent is asked for — tall captures are downscaled to unreadable anyway.</summary>
    public const int RequestedMaxHeight = 4000;

    /// <summary>The largest image (raw bytes): 3.75 MB is 5 MB as base64 — the strictest per-image limit (Bedrock, Vertex).</summary>
    public const long MaxImageBytes = 3_750_000;

    /// <summary>The most images in one request — at most 20 keeps Anthropic's per-image limit at 8000 px.</summary>
    public const int MaxImagesPerRequest = 16;

    /// <summary>All images of one request together (raw bytes) — far under the request size limits, base64 included.</summary>
    public const long MaxRequestImageBytes = 32L * 1024 * 1024;

    /// <summary>Why <paramref name="content"/> can't go to a model as an image (plain words), or null when it can.</summary>
    public static string? Problem(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaxImageBytes) { return $"it is {content.Length / 1_000_000.0:0.#} MB — a model takes at most {MaxImageBytes / 1_000_000.0:0.##} MB an image"; }
        if (!TryReadSize(content, out var width, out var height)) { return "its size can't be read (not a PNG, JPEG or WebP)"; }
        if (width > MaxEdgePixels || height > MaxEdgePixels)
        {
            return $"it is {width} × {height} px — a model takes at most {MaxEdgePixels} px a side (take the top of a long page, not all of it)";
        }
        return null;
    }

    /// <summary>
    /// The images that fit one request, in order, and a reason for each one left out (by index): each within
    /// <see cref="Problem"/>, at most <see cref="MaxImagesPerRequest"/>, together within <see cref="MaxRequestImageBytes"/>.
    /// </summary>
    public static (IReadOnlyList<int> Kept, IReadOnlyList<(int Index, string Why)> Left) Fit(IReadOnlyList<byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var kept = new List<int>();
        var left = new List<(int, string)>();
        long total = 0;
        for (var i = 0; i < images.Count; i++)
        {
            var content = images[i];
            if (Problem(content) is { } why) { left.Add((i, why)); continue; }
            if (kept.Count >= MaxImagesPerRequest) { left.Add((i, $"a request carries at most {MaxImagesPerRequest} images")); continue; }
            if (total + content.LongLength > MaxRequestImageBytes)
            {
                left.Add((i, $"the request's images would pass {MaxRequestImageBytes / (1024 * 1024)} MB together"));
                continue;
            }
            total += content.LongLength;
            kept.Add(i);
        }
        return (kept, left);
    }

    /// <summary>The pixel size from a PNG, JPEG or WebP header (no decoding); false when it can't be read.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> c, out int width, out int height)
    {
        width = height = 0;
        // PNG: signature, then the IHDR chunk — width and height big-endian at 16 and 20.
        if (c.Length >= 24 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47 && c[12] == (byte)'I' && c[13] == (byte)'H')
        {
            width = BinaryPrimitives.ReadInt32BigEndian(c[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(c[20..]);
            return width > 0 && height > 0;
        }
        // JPEG: walk the markers to the first start-of-frame (SOF0–SOF15 but DHT C4, JPG C8, DAC CC).
        if (c.Length >= 4 && c[0] == 0xFF && c[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < c.Length)
            {
                if (c[i] != 0xFF) { i++; continue; }
                var marker = c[i + 1];
                if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xFF) { i++; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(c[(i + 2)..]);
                if (marker >= 0xC0 && marker <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(c[(i + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(c[(i + 7)..]);
                    return width > 0 && height > 0;
                }
                if (length < 2) { return false; }
                i += 2 + length;
            }
            return false;
        }
        // WebP: RIFF....WEBP then a VP8 / VP8L / VP8X chunk.
        if (c.Length >= 30 && c[..4].SequenceEqual("RIFF"u8) && c[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = c[12..16];
            if (chunk.SequenceEqual("VP8 "u8))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(c[26..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(c[28..]) & 0x3FFF;
            }
            else if (chunk.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(c[21..]);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (chunk.SequenceEqual("VP8X"u8))
            {
                width = (c[24] | (c[25] << 8) | (c[26] << 16)) + 1;
                height = (c[27] | (c[28] << 8) | (c[29] << 16)) + 1;
            }
            return width > 0 && height > 0;
        }
        return false;
    }
}
