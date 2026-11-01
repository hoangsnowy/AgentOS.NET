// Design gate (ADR-0016) — a sample design check for the standalone desktop. `dotnet run --project src/AgentOs.Web` has
// no database and therefore no tasks, so the Design panel could never be seen (or end-to-end tested) there. In
// Development with dev auto-login and no database ONLY, this seeds one clearly-labelled sample check into the
// in-memory stores: four generated screenshots (phone / desktop × light / dark) and a Designer verdict with findings.
// The owner buttons work on it for real (the in-memory review store). Never registered anywhere else.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Sessions;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentOs.Web.Services.BoardTasks;

/// <summary>Seeds the standalone desktop's sample design check (Development + dev auto-login + no database only).</summary>
public sealed class DesignGateSample(IServiceScopeFactory scopes) : IHostedService
{
    /// <summary>The sample's task id (fixed, so a restart shows the same sample).</summary>
    public static readonly Guid SessionId = new("5a3c1e00-de51-4e00-9a7e-000000000007");

    /// <summary>The sample's board id.</summary>
    public static readonly Guid BoardId = new("5a3c1e00-de51-4e00-9a7e-0000000000b0");

    /// <summary>The sample's ticket number.</summary>
    public const int IssueNumber = 7;

    /// <summary>The sample's summary — says what it is.</summary>
    public const string Summary = "Sample design check (standalone Development only) — the dark checkout button is hard to read and the phone layout scrolls sideways.";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var reviews = scope.ServiceProvider.GetService<IDesignReviewStore>();
        var files = scope.ServiceProvider.GetService<IScopedFileStore>();
        if (reviews is null || files is null) { return; }
        var tenant = ITenantContext.DefaultTenantId;
        if (await reviews.GetAsync(tenant, SessionId, 0, cancellationToken).ConfigureAwait(false) is not null) { return; }

        var review = DesignReview.New(tenant, BoardId, SessionId, IssueNumber, 0, DesignReviewStatus.Changes);
        var shots = new[]
        {
            new DesignShot($"{RunUploadRules.Folder}/checkout-390-light.png", "Checkout", DesignShot.PhoneWidth, "light"),
            new DesignShot($"{RunUploadRules.Folder}/checkout-390-dark.png", "Checkout", DesignShot.PhoneWidth, "dark"),
            new DesignShot($"{RunUploadRules.Folder}/checkout-1280-light.png", "Checkout", DesignShot.DesktopWidth, "light"),
            new DesignShot($"{RunUploadRules.Folder}/checkout-1280-dark.png", "Checkout", DesignShot.DesktopWidth, "dark"),
        };
        foreach (var shot in shots)
        {
            var png = Screen(shot.IsPhone ? 390 : 1280, shot.IsPhone ? 760 : 720, shot.IsDark);
            await files.PutAsync(new StoredFileInfo(Guid.NewGuid(), tenant, review.ScopeKey, shot.Path, png.LongLength, RunFile.Hash(png),
                "image/png", "Sample (Development)", DateTimeOffset.UtcNow), png, cancellationToken).ConfigureAwait(false);
        }
        await reviews.EnsureAsync(review with
        {
            Summary = Summary,
            Shots = shots,
            Findings =
            [
                new DesignFinding("Checkout · phone · dark", "The Pay button's label is grey on dark grey — barely readable.", "Use the accent button (on-accent text), as in light mode."),
                new DesignFinding("Checkout · phone · light", "The order summary is wider than the screen; the page scrolls sideways.", "Stack the summary under the form below 600 px."),
            ],
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // A schematic screen as a PNG: header bar, two content cards and a button — light or dark.
    internal static byte[] Screen(int width, int height, bool dark)
    {
        var bg = dark ? (0x24, 0x24, 0x24) : (0xFA, 0xFA, 0xFB);
        var card = dark ? (0x30, 0x30, 0x30) : (0xFF, 0xFF, 0xFF);
        var header = (0x35, 0x84, 0xE4);
        var button = dark ? (0x3A, 0x3A, 0x3A) : (0x35, 0x84, 0xE4);
        var rgb = new byte[width * height * 3];
        Fill(rgb, width, 0, 0, width, height, bg);
        Fill(rgb, width, 0, 0, width, 56, header);
        var pad = width / 20;
        Fill(rgb, width, pad, 88, width - pad, 88 + height / 4, card);
        Fill(rgb, width, pad, 120 + height / 4, width - pad, 120 + height / 2, card);
        Fill(rgb, width, pad, height - 120, Math.Min(width - pad, pad + 280), height - 72, button);
        return Png(width, height, rgb);
    }

    private static void Fill(byte[] rgb, int width, int x0, int y0, int x1, int y1, (int R, int G, int B) c)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * width) + x) * 3;
                rgb[i] = (byte)c.R;
                rgb[i + 1] = (byte)c.G;
                rgb[i + 2] = (byte)c.B;
            }
        }
    }

    // A minimal 8-bit RGB PNG: signature, IHDR, one zlib IDAT (filter 0 per row), IEND.
    internal static byte[] Png(int width, int height, byte[] rgb)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: RGB
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        png.Write(len);
        png.Write(type);
        png.Write(data);
        var crc = Crc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        png.Write(len);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) { c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); }
        foreach (var b in data) { c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); }
        return c ^ 0xFFFFFFFFu;
    }
}
