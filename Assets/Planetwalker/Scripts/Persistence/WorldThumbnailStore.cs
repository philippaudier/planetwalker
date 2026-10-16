using System;
using System.IO;

namespace Planetwalker.Persistence
{
    /// <summary>
    /// World thumbnails on disk (V0.1.3B): one PNG per world, <c>Thumbnails/&lt;planet-id&gt;.png</c>, next to the
    /// archives folder. Separate from the archives on purpose: a thumbnail is a disposable picture, so a missing,
    /// unreadable or failed thumbnail never affects an archive. Writes are atomic (temporary file, flushed, then
    /// renamed), like archives. File operations only: rendering and decoding live elsewhere.
    /// </summary>
    public sealed class WorldThumbnailStore
    {
        public const string FileExtension = ".png";
        /// <summary>Thumbnail files larger than this are treated as corrupt (a 256 px thumbnail is about 60-120 KB).</summary>
        public const int MaxFileBytes = 4 * 1024 * 1024;

        static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        readonly IArchiveFileSystem files;

        public WorldThumbnailStore(string directory, IArchiveFileSystem fileSystem = null)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("A thumbnail directory is required.", nameof(directory));
            }
            Directory = Path.GetFullPath(directory);
            files = fileSystem ?? new PhysicalArchiveFileSystem();
        }

        public string Directory { get; }

        /// <summary>Number of thumbnail files written (tests: thumbnails are only written at intended moments).</summary>
        public int WriteCount { get; private set; }

        public string PathFor(Guid planetId) => Path.Combine(Directory, PlanetArchive.FormatId(planetId) + FileExtension);

        public bool Exists(Guid planetId)
        {
            try
            {
                return planetId != Guid.Empty && files.FileExists(PathFor(planetId));
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return false;
            }
        }

        /// <summary>Writes (or atomically replaces) a thumbnail. Returns null on success, else a readable error; the previous file is kept on failure.</summary>
        public string Write(Guid planetId, byte[] png)
        {
            if (planetId == Guid.Empty)
            {
                return "invalid planet ID";
            }
            if (!LooksLikePng(png))
            {
                return "not a PNG image";
            }
            string destination = PathFor(planetId);
            string temp = destination + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + WorldArchiveStore.TempExtension;
            try
            {
                files.CreateDirectory(Directory);
                files.WriteNewFile(temp, png);
                files.Replace(temp, destination);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                TryDelete(temp);
                return e.Message;
            }
            WriteCount++;
            return null;
        }

        /// <summary>Reads a thumbnail's bytes; false (with a reason) when it is missing, unreadable or obviously not a PNG.</summary>
        public bool TryRead(Guid planetId, out byte[] png, out string error)
        {
            png = null;
            error = null;
            try
            {
                string path = PathFor(planetId);
                if (planetId == Guid.Empty || !files.FileExists(path))
                {
                    error = "no thumbnail";
                    return false;
                }
                png = files.ReadAllBytes(path, MaxFileBytes);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                error = e.Message;
                return false;
            }
            if (!LooksLikePng(png))
            {
                error = "corrupt thumbnail";
                png = null;
                return false;
            }
            return true;
        }

        /// <summary>Deletes a world's thumbnail (no thumbnail is not an error). Returns null on success, else the error.</summary>
        public string Delete(Guid planetId)
        {
            try
            {
                string path = PathFor(planetId);
                if (planetId != Guid.Empty && files.FileExists(path))
                {
                    files.Delete(path);
                }
                return null;
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return e.Message;
            }
        }

        /// <summary>Copies a thumbnail to another world (duplicate). No source thumbnail: nothing to do. Returns null on success.</summary>
        public string Copy(Guid source, Guid destination)
        {
            if (!TryRead(source, out byte[] png, out string error))
            {
                return error == "no thumbnail" ? null : error;
            }
            return Write(destination, png);
        }

        static bool LooksLikePng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 64)
            {
                return false;
            }
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (bytes[i] != PngSignature[i])
                {
                    return false;
                }
            }
            return true;
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
                // Best effort.
            }
        }

        static bool IsIoFailure(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException
                || e is NotSupportedException || e is ArgumentException || e is InvalidDataException;
        }
    }
}
