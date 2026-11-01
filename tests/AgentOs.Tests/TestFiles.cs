// File-system steps a test expects may fail (the OS holds the file, a scanner has it open) — the outcome is returned, so
// the test asserts it or deliberately ignores it, instead of swallowing the failure in an empty catch.

using System;
using System.IO;

namespace AgentOs.Tests;

internal static class TestFiles
{
    /// <summary>Run a file-system step; false when the OS refused it (an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>). Any other exception is a test failure and propagates.</summary>
    public static bool TryIo(Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            step();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Best-effort removal of a test's temp folder; false when something still holds it (the OS temp
    /// cleanup removes it later).</summary>
    public static bool TryDeleteDirectory(string path) => TryIo(() => Directory.Delete(path, recursive: true));
}
