using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    // Entries are read through bounded streams; XML never resolves external resources.
    private sealed class XflSource : IDisposable
    {
        private const long MaximumXmlBytes = 128 * 1024 * 1024;
        private const long MaximumTotalXmlBytes = 384 * 1024 * 1024;
        private readonly string? _root;
        private readonly ZipArchive? _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _prefix = "";
        private long _readBytes;

        internal XflSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Flash import requires an absolute source path.", nameof(path));
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                EnsureSafePath(path);
                _root = path;
                return;
            }
            if (!File.Exists(path)) throw new FileNotFoundException("The Flash source does not exist.", path);
            EnsureSafePath(path);
            if (IsZipContainer(path))
            {
                if (new FileInfo(path).Length > 512L * 1024 * 1024)
                    throw new InvalidDataException("The Flash archive exceeds the 512 MiB limit.");
                _archive = new ZipArchive(OpenCompatibleZipStream(path), ZipArchiveMode.Read, leaveOpen: false);
                try
                {
                    if (_archive.Entries.Count > 20_000) throw new InvalidDataException("Too many Flash archive entries.");
                    long expanded = 0;
                    foreach (var entry in _archive.Entries)
                    {
                        var name = SafeRelativePath(entry.FullName.TrimEnd('/'));
                        if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                            throw new InvalidDataException("Flash archives cannot contain symbolic links.");
                        expanded = checked(expanded + entry.Length);
                        if (expanded > 512L * 1024 * 1024 || entry.Length > 128L * 1024 * 1024)
                            throw new InvalidDataException("The expanded Flash archive exceeds the size limit.");
                        if (!entry.FullName.EndsWith('/') && !_entries.TryAdd(name, entry))
                            throw new InvalidDataException($"Duplicate Flash archive path: {name}.");
                    }
                    var documents = _entries.Keys.Where(name => name.Equals("DOMDocument.xml", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("/DOMDocument.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (documents.Length != 1) throw new InvalidDataException("The Flash archive must contain exactly one DOMDocument.xml.");
                    _prefix = documents[0][..^"DOMDocument.xml".Length];
                }
                catch { _archive.Dispose(); throw; }
            }
            else
            {
                if (!Path.GetExtension(path).Equals(".xfl", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).Equals("DOMDocument.xml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("This is not a ZIP/XFL Flash document. Legacy binary FLA requires conversion to XFL.");
                _root = Path.GetDirectoryName(path)!;
                EnsureSafePath(_root);
            }
        }

        internal XElement ReadXml(string relativePath)
        {
            try { return ReadXmlCore(relativePath); }
            catch (XmlException exception) { throw new InvalidDataException($"Invalid XFL XML [{relativePath}]: {exception.Message}", exception); }
        }

        internal byte[] ReadBytes(string relativePath, long maximumBytes)
        {
            relativePath = SafeRelativePath(relativePath);
            using var stream = Open(relativePath);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (bytes.Length + count > maximumBytes)
                    throw new InvalidDataException($"XFL binary entry exceeds the size limit: {relativePath}.");
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }

        internal bool HasEntry(string relativePath)
        {
            relativePath = SafeRelativePath(relativePath);
            if (_archive is not null) return _entries.ContainsKey(_prefix + relativePath);
            var path = Path.Combine(_root!, relativePath.Replace('/', Path.DirectorySeparatorChar));
            EnsureSafePath(path);
            return File.Exists(path);
        }

        private XElement ReadXmlCore(string relativePath)
        {
            relativePath = SafeRelativePath(relativePath);
            using var stream = Open(relativePath);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (bytes.Length + count > MaximumXmlBytes || _readBytes + count > MaximumTotalXmlBytes)
                    throw new InvalidDataException($"XFL XML size limit exceeded: {relativePath}.");
                bytes.Write(buffer, 0, count);
                _readBytes += count;
            }
            bytes.Position = 0;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                IgnoreComments = true
            };
            using (var check = XmlReader.Create(bytes, settings))
            {
                var nodes = 0;
                while (check.Read())
                {
                    if (check.Depth > 128) throw new InvalidDataException($"XFL XML nesting limit exceeded: {relativePath}.");
                    if (++nodes > 4_000_000) throw new InvalidDataException($"XFL XML node count limit exceeded: {relativePath}.");
                }
            }
            bytes.Position = 0;
            using var reader = XmlReader.Create(bytes, settings);
            var root = XElement.Load(reader);
            if (root.Name.NamespaceName is not ("" or "http://ns.adobe.com/xfl/2008/"))
                throw new InvalidDataException($"Unknown XFL namespace in {relativePath}.");
            if (root.Descendants().Any(node => node.Name.Namespace != root.Name.Namespace))
                throw new InvalidDataException($"Mixed XML namespaces in {relativePath}.");
            return root;
        }

        private Stream Open(string relativePath)
        {
            if (_archive is not null)
            {
                if (!_entries.TryGetValue(_prefix + relativePath, out var entry))
                    throw new InvalidDataException($"Missing XFL entry: {relativePath}.");
                if (entry.Length > MaximumXmlBytes) throw new InvalidDataException($"XFL entry too large: {relativePath}.");
                return entry.Open();
            }
            var path = Path.GetFullPath(Path.Combine(_root!, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("XFL paths must stay inside the source directory.");
            EnsureSafePath(path);
            if (!File.Exists(path)) throw new InvalidDataException($"Missing XFL file: {relativePath}.");
            if (new FileInfo(path).Length > MaximumXmlBytes) throw new InvalidDataException($"XFL file too large: {relativePath}.");
            return File.OpenRead(path);
        }

        private static void EnsureSafePath(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("XFL source paths cannot cross a reparse point.");
        }

        internal static string SafeRelativePath(string path)
        {
            path = path.Replace('\\', '/');
            if (path.Length is 0 or > 1024 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)
                || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
                throw new InvalidDataException($"Unsafe XFL relative path: {path}.");
            return path;
        }

        public void Dispose() => _archive?.Dispose();
    }

    internal static bool IsZipContainer(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return stream.Read(header) == 4 && header[0] == 0x50 && header[1] == 0x4b
            && ((header[2] == 3 && header[3] == 4) || (header[2] == 5 && header[3] == 6));
    }
}
