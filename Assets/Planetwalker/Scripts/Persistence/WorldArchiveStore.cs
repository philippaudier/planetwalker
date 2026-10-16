using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Planetwalker.Persistence
{
    /// <summary>File operations used by the archive store; replaceable to simulate failures in tests.</summary>
    public interface IArchiveFileSystem
    {
        void CreateDirectory(string path);
        bool FileExists(string path);
        /// <summary>Reads a whole file; throws <see cref="InvalidDataException"/> when it exceeds maxBytes.</summary>
        byte[] ReadAllBytes(string path, int maxBytes);
        /// <summary>Creates a new file (fails if it exists), writes the bytes and flushes them to disk.</summary>
        void WriteNewFile(string path, byte[] bytes);
        /// <summary>Atomically replaces (or creates) destination with source; source no longer exists afterwards.</summary>
        void Replace(string source, string destination);
        void Delete(string path);
        string[] GetFiles(string directory, string searchPattern);
        DateTime GetLastWriteTimeUtc(string path);
        long GetLength(string path);
    }

    /// <summary>The real file system (System.IO).</summary>
    public class PhysicalArchiveFileSystem : IArchiveFileSystem
    {
        public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public virtual bool FileExists(string path) => File.Exists(path);

        public virtual byte[] ReadAllBytes(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maxBytes)
                {
                    throw new InvalidDataException("File is larger than " + maxBytes / 1024 + " KB.");
                }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException("File was truncated while reading.");
                    }
                    read += n;
                }
                return bytes;
            }
        }

        public virtual void WriteNewFile(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true); // through the OS cache: the data is on disk before the rename
            }
        }

        public virtual void Replace(string source, string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }
            try
            {
                // Atomic on NTFS (ReplaceFile) and POSIX file systems (rename): readers see the old or the new
                // file, never a mix; on failure the destination is untouched.
                File.Replace(source, destination, null, true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByRename(source, destination);
            }
        }

        static void ReplaceByRename(string source, string destination)
        {
            string backup = destination + ".bak";
            File.Delete(backup);
            File.Move(destination, backup);
            try
            {
                File.Move(source, destination);
            }
            catch
            {
                File.Move(backup, destination);
                throw;
            }
            File.Delete(backup);
        }

        public virtual void Delete(string path) => File.Delete(path);

        public virtual string[] GetFiles(string directory, string searchPattern) =>
            Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly) : new string[0];

        public virtual DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public virtual long GetLength(string path) => new FileInfo(path).Length;
    }

    /// <summary>One archive file as seen by the browser (metadata only; nothing is generated).</summary>
    public sealed class WorldArchiveEntry
    {
        public Guid PlanetId;
        public string FilePath;
        public long FileBytes;
        /// <summary>Metadata of a readable archive (null when <see cref="Error"/> is set).</summary>
        public string DisplayName;
        public int PlanetSeed;
        public DateTime CreatedUtc;
        public DateTime ModifiedUtc;
        public GeneratorCompatibility Compatibility;
        /// <summary>Planetary statistics recorded at the last save (V0.1.3B), or null when unavailable.</summary>
        public WorldMetadata Metadata;
        /// <summary>Why the file cannot be loaded (corrupt, unsupported schema, invalid values), or null.</summary>
        public string Error;

        public bool IsValid => Error == null;
        public string ShortId => PlanetArchive.FormatId(PlanetId).Substring(0, 8);
    }

    /// <summary>Result of a store operation: success, or a user-readable error (never an exception).</summary>
    public sealed class ArchiveOperationResult
    {
        public bool Success;
        public string Error;
        public PlanetArchive Archive;
        public ArchiveReadResult Read;
        public long Bytes;
        public double Milliseconds;

        public static ArchiveOperationResult Fail(string error) => new ArchiveOperationResult { Error = error };
    }

    /// <summary>
    /// Local world archive storage (V0.1.3A): one UTF-8 JSON file per world, named by its planet ID, in
    /// <c>Application.persistentDataPath/Planetwalker/Worlds</c> (see <see cref="Planetwalker.Bootstrap.PlanetBootstrap"/>).
    /// Writes are atomic (temporary file, flushed, read back, then renamed over the destination), so an
    /// interrupted or failed save leaves the previous archive intact. Never regenerates anything.
    /// </summary>
    public sealed class WorldArchiveStore
    {
        public const string FileExtension = ".json";
        public const string TempExtension = ".tmp";
        /// <summary>Archive files larger than this are rejected unread (a normal archive is about 5 KB).</summary>
        public const int MaxFileBytes = 1024 * 1024;
        /// <summary>Orphan temporary files older than this (interrupted saves) are removed when listing.</summary>
        public static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        readonly IArchiveFileSystem files;

        public WorldArchiveStore(string directory, IArchiveFileSystem fileSystem = null)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("An archive directory is required.", nameof(directory));
            }
            Directory = Path.GetFullPath(directory);
            files = fileSystem ?? new PhysicalArchiveFileSystem();
        }

        /// <summary>Absolute directory holding the archives.</summary>
        public string Directory { get; }

        /// <summary>Time of the last <see cref="List"/> call (milliseconds).</summary>
        public double LastListMilliseconds { get; private set; }

        /// <summary>The only file name an archive can have: its planet ID (display names never reach the file system).</summary>
        public string PathFor(Guid planetId)
        {
            return Path.Combine(Directory, PlanetArchive.FormatId(planetId) + FileExtension);
        }

        /// <summary>
        /// Saves an archive: validates it, serializes, checks the text reads back to the same archive, writes a
        /// temporary file, flushes, verifies its bytes, then atomically replaces the destination.
        /// </summary>
        public ArchiveOperationResult Save(PlanetArchive archive)
        {
            var stopwatch = Stopwatch.StartNew();
            ArchiveReport validation = PlanetArchiveSerializer.Validate(archive);
            if (!validation.Ok)
            {
                return ArchiveOperationResult.Fail("Not saved: invalid world (" + validation.Summary() + ")");
            }
            string json;
            try
            {
                json = PlanetArchiveSerializer.Serialize(archive);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
            {
                return ArchiveOperationResult.Fail("Not saved: " + e.Message);
            }
            ArchiveReadResult check = PlanetArchiveSerializer.Read(json);
            if (!check.Ok || !check.Archive.Configuration.ContentEquals(archive.Configuration) || check.Archive.DisplayName != archive.DisplayName)
            {
                return ArchiveOperationResult.Fail("Not saved: the archive did not survive a read-back check ("
                    + (check.Report.Ok ? "content differs" : check.Report.Summary()) + ")");
            }
            byte[] bytes = Utf8.GetBytes(json);

            string destination = PathFor(archive.PlanetId);
            string temp = destination + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + TempExtension;
            try
            {
                files.CreateDirectory(Directory);
                files.WriteNewFile(temp, bytes);
                byte[] written = files.ReadAllBytes(temp, MaxFileBytes);
                if (!BytesEqual(written, bytes))
                {
                    throw new IOException("The written file does not match (disk write failure).");
                }
                files.Replace(temp, destination);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                TryDelete(temp);
                return ArchiveOperationResult.Fail("Not saved: " + Describe(e) + " The previous archive (if any) is unchanged.");
            }
            stopwatch.Stop();
            return new ArchiveOperationResult { Success = true, Archive = archive, Bytes = bytes.Length, Milliseconds = stopwatch.Elapsed.TotalMilliseconds };
        }

        /// <summary>Reads and fully validates an archive by ID. Nothing outside the archive directory can be addressed.</summary>
        public ArchiveOperationResult Load(Guid planetId)
        {
            var stopwatch = Stopwatch.StartNew();
            if (planetId == Guid.Empty)
            {
                return ArchiveOperationResult.Fail("Invalid planet ID.");
            }
            string path = PathFor(planetId);
            ArchiveOperationResult result = ReadFile(path);
            if (!result.Success)
            {
                return result;
            }
            if (result.Archive.PlanetId != planetId)
            {
                return ArchiveOperationResult.Fail("The archive's planet ID does not match its file name (copied or renamed file?).");
            }
            stopwatch.Stop();
            result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        ArchiveOperationResult ReadFile(string path)
        {
            string json;
            long length;
            try
            {
                if (!files.FileExists(path))
                {
                    return ArchiveOperationResult.Fail("Archive not found.");
                }
                byte[] bytes = files.ReadAllBytes(path, MaxFileBytes);
                length = bytes.Length;
                json = Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return ArchiveOperationResult.Fail("Corrupt archive: not valid UTF-8 text.");
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return ArchiveOperationResult.Fail("Cannot read the archive: " + Describe(e));
            }
            ArchiveReadResult read = PlanetArchiveSerializer.Read(json);
            if (!read.Ok)
            {
                return new ArchiveOperationResult { Error = read.Report.Summary(), Read = read, Bytes = length };
            }
            return new ArchiveOperationResult { Success = true, Archive = read.Archive, Read = read, Bytes = length };
        }

        /// <summary>
        /// Lists the archives (most recently modified first). Reads metadata only: no planet is generated.
        /// Unreadable files are listed with their error. Removes orphan temporary files of interrupted saves.
        /// </summary>
        public List<WorldArchiveEntry> List()
        {
            var stopwatch = Stopwatch.StartNew();
            var entries = new List<WorldArchiveEntry>();
            string[] paths;
            try
            {
                paths = files.GetFiles(Directory, "*" + FileExtension);
                CleanStaleTemporaryFiles();
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                LastListMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                return entries;
            }
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(FileExtension, StringComparison.Ordinal)
                    || !PlanetArchive.TryParseId(name.Substring(0, name.Length - FileExtension.Length), out Guid id))
                {
                    continue; // not an archive file name
                }
                var entry = new WorldArchiveEntry { PlanetId = id, FilePath = path };
                ArchiveOperationResult read = ReadFile(path);
                entry.FileBytes = read.Bytes;
                if (read.Success && read.Archive.PlanetId != id)
                {
                    read = ArchiveOperationResult.Fail("planet ID does not match the file name");
                }
                if (read.Success)
                {
                    PlanetArchive a = read.Archive;
                    entry.DisplayName = a.DisplayName;
                    entry.PlanetSeed = a.Configuration.PlanetSeed;
                    entry.CreatedUtc = a.CreatedUtc;
                    entry.ModifiedUtc = a.ModifiedUtc;
                    entry.Compatibility = read.Read.Compatibility;
                    entry.Metadata = a.Metadata;
                }
                else
                {
                    entry.Error = read.Error;
                    try
                    {
                        entry.ModifiedUtc = files.GetLastWriteTimeUtc(path);
                    }
                    catch (Exception e) when (IsIoFailure(e))
                    {
                        entry.ModifiedUtc = DateTime.MinValue;
                    }
                }
                entries.Add(entry);
            }
            entries.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc) != 0 ? b.ModifiedUtc.CompareTo(a.ModifiedUtc)
                : a.PlanetId.CompareTo(b.PlanetId));
            stopwatch.Stop();
            LastListMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return entries;
        }

        /// <summary>Renames a world: same ID, same configuration, new display name and modification time.</summary>
        public ArchiveOperationResult Rename(Guid planetId, string newName)
        {
            string problem = PlanetArchive.ValidateName(newName);
            if (problem != null)
            {
                return ArchiveOperationResult.Fail("Invalid name: " + problem + ".");
            }
            ArchiveOperationResult loaded = Load(planetId);
            if (!loaded.Success)
            {
                return loaded;
            }
            PlanetArchive archive = loaded.Archive;
            archive.DisplayName = newName;
            archive.ModifiedUtc = PlanetArchiveSerializer.UtcNowTruncated();
            return Save(archive);
        }

        /// <summary>
        /// Replaces the statistics snapshot of an archive (V0.1.3B "Refresh Preview"): identity, name, timestamps,
        /// configuration and presentation are kept; the archive is rewritten atomically.
        /// </summary>
        public ArchiveOperationResult UpdateMetadata(Guid planetId, WorldMetadata metadata)
        {
            ArchiveOperationResult loaded = Load(planetId);
            if (!loaded.Success)
            {
                return loaded;
            }
            loaded.Archive.Metadata = metadata?.Clone();
            return Save(loaded.Archive);
        }

        /// <summary>Copies a world under a new identity (new ID, "name (copy)", new timestamps); configuration and presentation unchanged.</summary>
        public ArchiveOperationResult Duplicate(Guid planetId)
        {
            ArchiveOperationResult loaded = Load(planetId);
            if (!loaded.Success)
            {
                return loaded;
            }
            PlanetArchive copy = loaded.Archive.Clone();
            copy.PlanetId = Guid.NewGuid();
            copy.DisplayName = PlanetArchive.CleanName(copy.DisplayName.Length + 7 > PlanetArchive.MaxNameLength
                ? PlanetArchive.CleanName(copy.DisplayName.Substring(0, PlanetArchive.MaxNameLength - 7)) + " (copy)"
                : copy.DisplayName + " (copy)");
            copy.CreatedUtc = copy.ModifiedUtc = PlanetArchiveSerializer.UtcNowTruncated();
            return Save(copy);
        }

        /// <summary>Deletes exactly one archive file (by ID).</summary>
        public ArchiveOperationResult Delete(Guid planetId)
        {
            if (planetId == Guid.Empty)
            {
                return ArchiveOperationResult.Fail("Invalid planet ID.");
            }
            string path = PathFor(planetId);
            try
            {
                if (!files.FileExists(path))
                {
                    return ArchiveOperationResult.Fail("Archive not found.");
                }
                files.Delete(path);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return ArchiveOperationResult.Fail("Not deleted: " + Describe(e));
            }
            return new ArchiveOperationResult { Success = true };
        }

        void CleanStaleTemporaryFiles()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string temp in files.GetFiles(Directory, "*" + TempExtension))
            {
                try
                {
                    if (now - files.GetLastWriteTimeUtc(temp) > StaleTempAge)
                    {
                        files.Delete(temp);
                    }
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    // Best effort: an undeletable leftover is ignored (never listed).
                }
            }
        }

        void TryDelete(string path)
        {
            try
            {
                if (files.FileExists(path))
                {
                    files.Delete(path);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                // Best effort: a leftover temporary file is ignored by List and cleaned later.
            }
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        static bool IsIoFailure(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException
                || e is NotSupportedException || e is ArgumentException || e is InvalidDataException;
        }

        static string Describe(Exception e)
        {
            if (e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                return "permission denied (" + e.Message + ").";
            }
            if (e is DirectoryNotFoundException)
            {
                return "folder not found (" + e.Message + ").";
            }
            return e.Message.EndsWith(".", StringComparison.Ordinal) ? e.Message : e.Message + ".";
        }
    }
}
