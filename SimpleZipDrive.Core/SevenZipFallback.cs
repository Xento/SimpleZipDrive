using System.Runtime.InteropServices;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

namespace SimpleZipDrive.Core;

/// <summary>
/// Fallback archive extractor using SharpSevenZip (native 7z.dll).
/// For RAR archives, a sequential SharpCompress RarReader fallback is also used so entries
/// spanning or residing in later volumes can still be extracted reliably.
/// </summary>
internal sealed class SevenZipFallback : IDisposable
{
    private readonly string _archivePath;
    private readonly Func<string?> _passwordProvider;
    private readonly object _lock = new();
    private SharpSevenZip.SharpSevenZipExtractor? _extractor;
    private Dictionary<string, int>? _entryIndexMap;
    private bool _disposed;

    public SevenZipFallback(string archivePath, Func<string?> passwordProvider)
    {
        _archivePath = archivePath;
        _passwordProvider = passwordProvider;
    }

    /// <summary>
    /// Tries to extract an entry by its normalized path to the output stream.
    /// SharpSevenZip is attempted first. For RAR files, failures are retried using a
    /// forward-only RarReader over every detected volume.
    /// Returns true if extraction succeeded, false otherwise.
    /// </summary>
    public bool TryExtractEntry(string normalizedPath, Stream outputStream)
    {
        if (_disposed) return false;

        try
        {
            EnsureInitialized();

            if (_entryIndexMap != null)
            {
                // Normalize path: SharpSevenZip uses backslash-separated paths
                var searchPaths = new[]
                {
                    normalizedPath.TrimStart('/'),
                    normalizedPath.TrimStart('/').Replace('/', '\\')
                };

                foreach (var searchPath in searchPaths)
                {
                    if (_entryIndexMap.TryGetValue(searchPath, out var index))
                    {
                        try
                        {
                            lock (_lock)
                            {
                                if (_extractor != null)
                                {
                                    _extractor.ExtractFile(index, outputStream);
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log(ex, $"SevenZip extraction failed for '{normalizedPath}', trying sequential RAR fallback.");
                            break;
                        }
                    }
                }

                // Case-insensitive fallback search
                var normalizedLower = normalizedPath.TrimStart('/').ToLowerInvariant();
                foreach (var kvp in _entryIndexMap)
                {
                    if (kvp.Key.Replace('\\', '/').Equals(normalizedLower, StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals(normalizedLower, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            lock (_lock)
                            {
                                if (_extractor != null)
                                {
                                    _extractor.ExtractFile(kvp.Value, outputStream);
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log(ex, $"SevenZip case-insensitive extraction failed for '{normalizedPath}', trying sequential RAR fallback.");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, $"SevenZip fallback initialization/search failed for '{normalizedPath}', trying sequential RAR fallback.");
        }

        if (!ResetOutputForRetry(outputStream))
            return false;

        return TryExtractMultiVolumeRarEntry(normalizedPath, outputStream);
    }

    /// <summary>
    /// Sequentially reads a RAR archive through all detected volume files until the requested
    /// entry is reached. This is slower than random access, but it correctly follows split and
    /// solid data into later .partNNN.rar/.rNN volumes. The caller's normal RAM/disk cache then
    /// keeps the extracted result for subsequent accesses.
    /// </summary>
    private bool TryExtractMultiVolumeRarEntry(string normalizedPath, Stream outputStream)
    {
        if (!_archivePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            return false;

        List<FileStream>? volumeStreams = null;
        try
        {
            var volumeFiles = ArchiveFactory.GetFileParts(new FileInfo(_archivePath)).ToArray();
            if (volumeFiles.Length == 0)
                return false;

            volumeStreams = volumeFiles.Select(static file => file.OpenRead()).ToList();

            var password = _passwordProvider();
            var readerOptions = new ReaderOptions
            {
                Password = string.IsNullOrEmpty(password) ? null : password,
                LeaveStreamOpen = true
            };

            using var reader = RarReader.OpenReader(volumeStreams, readerOptions);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || string.IsNullOrEmpty(reader.Entry.Key))
                    continue;

                var candidatePath = ZipFsHelpers.NormalizePath(reader.Entry.Key);
                if (!candidatePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var entryStream = reader.OpenEntryStream();
                entryStream.CopyTo(outputStream);
                DiagnosticLogger.Log($"Sequential RAR fallback extracted '{normalizedPath}' using {volumeFiles.Length} volume(s).");
                return true;
            }

            DiagnosticLogger.Log($"Sequential RAR fallback could not find '{normalizedPath}' in {volumeFiles.Length} volume(s).");
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log(ex, $"Sequential multi-volume RAR fallback failed for '{normalizedPath}'.");
            return false;
        }
        finally
        {
            if (volumeStreams != null)
            {
                foreach (var stream in volumeStreams)
                {
                    stream.Dispose();
                }
            }
        }
    }

    private static bool ResetOutputForRetry(Stream outputStream)
    {
        try
        {
            if (!outputStream.CanSeek)
                return false;

            outputStream.Position = 0;
            outputStream.SetLength(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureInitialized()
    {
        if (_entryIndexMap != null)
            return;

        lock (_lock)
        {
            if (_entryIndexMap != null)
                return;

            try
            {
                if (!TrySetLibraryPath())
                {
                    _entryIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var password = _passwordProvider();
                _extractor = string.IsNullOrEmpty(password)
                    ? new SharpSevenZip.SharpSevenZipExtractor(_archivePath)
                    : new SharpSevenZip.SharpSevenZipExtractor(_archivePath, password);

                var entries = _extractor.ArchiveFileData;
                _entryIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    if (!entry.IsDirectory && !string.IsNullOrEmpty(entry.FileName))
                    {
                        _entryIndexMap[entry.FileName] = entry.Index;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log(ex, $"SevenZip fallback initialization failed for '{_archivePath}'. Sequential RAR fallback remains available.");
                _entryIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static bool TrySetLibraryPath()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var dllName = isArm64 ? "7z_arm64.dll" : "7z.dll";
            var dllPath = Path.Combine(baseDir, dllName);

            if (!File.Exists(dllPath))
                return false;

            SharpSevenZip.SharpSevenZipBase.SetLibraryPath(dllPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the 7z.dll is available in the application directory.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var isArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var dllName = isArm64 ? "7z_arm64.dll" : "7z.dll";
            return File.Exists(Path.Combine(baseDir, dllName));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_lock)
            {
                _extractor?.Dispose();
                _extractor = null;
            }
        }
    }
}
