using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeIsland.WpfApp.Services;

public sealed record WpfPetCatalogItem(
    string Id,
    string DisplayName,
    string Description,
    string DirectoryPath,
    string SpritesheetPath);

public sealed record WpfPetImportResult(WpfPetCatalogItem Pet, bool Updated);

public sealed record WpfPetImportFailure(string DirectoryPath, string Message);

public sealed record WpfPetBatchImportResult(
    int Added,
    int Updated,
    IReadOnlyList<WpfPetImportFailure> Failures)
{
    public string Summary => $"新增 {Added}，更新 {Updated}，失败 {Failures.Count}";
}

public sealed class WpfPetCatalogService
{
    public const string DefaultPetIdKey = "default_pet_id";
    private const long MaxManifestBytes = 128 * 1024;
    private const long MaxSpritesheetBytes = 64 * 1024 * 1024;
    private const long MaxArchiveBytes = 80 * 1024 * 1024;
    private const long MaxArchiveUncompressedBytes = 96 * 1024 * 1024;
    private const int MaxArchiveEntries = 64;
    private const int AtlasWidth = 1536;
    private const int AtlasV1Height = 1872;
    private const int AtlasV2Height = 2288;
    private const string ImportPrefix = ".import-";
    private const string StagingPrefix = ".staging-";
    private const string BackupPrefix = ".backup-";
    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedDeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly SettingsManager _settings;
    private IReadOnlyList<WpfPetCatalogItem> _pets = [];

    public WpfPetCatalogService(SettingsManager settings)
    {
        _settings = settings;
        PetsRoot = Path.Combine(settings.SettingsDirectory, "pets");
        try
        {
            EnsurePetsRootAvailable();
            RecoverInterruptedUpdates();
        }
        catch
        {
            // Pet storage is optional. Refresh below fails closed and repairs pet mode.
        }
        RefreshAndRepairSelection();
        AssertPureContracts();
    }

    public string PetsRoot { get; }

    public IReadOnlyList<WpfPetCatalogItem> Pets => _pets;

    public string? DefaultPetId => _settings.Get(DefaultPetIdKey, (string?)null);

    public WpfPetCatalogItem? DefaultPet =>
        _pets.FirstOrDefault(pet => string.Equals(pet.Id, DefaultPetId, StringComparison.OrdinalIgnoreCase));

    public bool HasDefaultPet => DefaultPet is not null;

    public event EventHandler? CatalogChanged;

    public WpfPetImportResult ImportDirectory(string sourceDirectory)
    {
        var result = ImportCore(sourceDirectory);
        RefreshAndNotify();
        return result;
    }

    public WpfPetImportResult ImportArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new InvalidDataException("宠物压缩包不存在");
        if (!IsSupportedArchiveFileName(archivePath))
            throw new InvalidDataException("宠物压缩包只支持 .zip 或 .codex-pet 文件");

        RejectReparsePoint(archivePath);
        EnsurePetsRootAvailable();
        var extractionRoot = Path.Combine(PetsRoot, ImportPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var packageDirectory = ExtractArchive(archivePath, extractionRoot);
            var result = ImportCore(packageDirectory);
            RefreshAndNotify();
            return result;
        }
        finally
        {
            TryDeleteDirectory(extractionRoot);
        }
    }

    public WpfPetImportResult ImportPath(string path)
    {
        if (Directory.Exists(path))
            return ImportDirectory(path);
        if (File.Exists(path))
            return ImportArchive(path);
        throw new InvalidDataException("宠物目录或压缩包不存在");
    }

    public static bool IsSupportedArchiveFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".codex-pet", StringComparison.OrdinalIgnoreCase);
    }

    public WpfPetBatchImportResult ImportCodexPets()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        var root = Path.Combine(codexHome, "pets");
        if (!Directory.Exists(root))
            return new WpfPetBatchImportResult(0, 0, [new WpfPetImportFailure(root, "未找到 Codex 宠物目录")]);

        var added = 0;
        var updated = 0;
        var failures = new List<WpfPetImportFailure>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var result = ImportCore(directory);
                if (result.Updated)
                    updated++;
                else
                    added++;
            }
            catch (Exception ex)
            {
                failures.Add(new WpfPetImportFailure(directory, ex.Message));
            }
        }

        RefreshAndNotify();
        return new WpfPetBatchImportResult(added, updated, failures);
    }

    public void SetDefault(string id)
    {
        RefreshAndRepairSelection();
        var pet = _pets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("宠物不存在或资源已失效");
        SetDefaultId(pet.Id);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        var index = _pets.ToList().FindIndex(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException("宠物不存在或资源已失效");
        var pet = _pets[index];
        var deletingDefault = string.Equals(pet.Id, DefaultPetId, StringComparison.OrdinalIgnoreCase);
        var next = _pets.Count > 1 ? _pets[(index + 1) % _pets.Count] : null;
        RejectReparsePoint(pet.DirectoryPath);
        Directory.Delete(pet.DirectoryPath, recursive: true);
        if (deletingDefault && next is not null)
            SetDefaultId(next.Id);
        RefreshAndRepairSelection();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        RefreshAndNotify();
    }

    private void RefreshAndNotify()
    {
        void Apply()
        {
            RefreshAndRepairSelection();
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private WpfPetImportResult ImportCore(string sourceDirectory)
    {
        var source = ValidatePackage(sourceDirectory);
        EnsurePetsRootAvailable();
        var target = Path.Combine(PetsRoot, source.Item.Id);
        var updated = Directory.Exists(target);
        if (updated)
            RejectReparsePoint(target);
        var staging = Path.Combine(PetsRoot, StagingPrefix + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(PetsRoot, BackupPrefix + Guid.NewGuid().ToString("N"));
        var targetMoved = false;

        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(source.ManifestPath, Path.Combine(staging, "pet.json"), overwrite: false);
            var relativeSpritesheet = Path.GetRelativePath(source.RootPath, source.Item.SpritesheetPath);
            var stagingSpritesheet = Path.Combine(staging, relativeSpritesheet);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingSpritesheet)!);
            File.Copy(source.Item.SpritesheetPath, stagingSpritesheet, overwrite: false);
            ValidatePackage(staging, source.Item.Id);

            if (updated)
            {
                Directory.Move(target, backup);
                targetMoved = true;
            }

            Directory.Move(staging, target);
            if (targetMoved)
                TryDeleteDirectory(backup);
        }
        catch
        {
            TryDeleteDirectory(staging);
            if (targetMoved && !Directory.Exists(target) && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }

        return new WpfPetImportResult(ValidatePackage(target, source.Item.Id).Item, updated);
    }

    private void RecoverInterruptedUpdates()
    {
        foreach (var import in TryEnumeratePetDirectories(ImportPrefix + "*"))
            TryDeleteDirectory(import);

        foreach (var staging in TryEnumeratePetDirectories(StagingPrefix + "*"))
            TryDeleteDirectory(staging);

        foreach (var backup in TryEnumeratePetDirectories(BackupPrefix + "*"))
        {
            try
            {
                var package = ValidatePackage(backup);
                var target = Path.Combine(PetsRoot, package.Item.Id);
                if (!Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                    continue;
                }
            }
            catch
            {
                // Preserve an unreadable backup; a later startup may regain codec/file access.
                continue;
            }

            TryDeleteDirectory(backup);
        }
    }

    private void RefreshAndRepairSelection()
    {
        try
        {
            EnsurePetsRootAvailable();
            _pets = Directory.EnumerateDirectories(PetsRoot)
                .Where(static path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                .Select(TryValidateManagedPackage)
                .Where(static item => item is not null)
                .Cast<WpfPetCatalogItem>()
                .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            _pets = [];
        }

        var configured = DefaultPetId;
        var selected = _pets.FirstOrDefault(item => string.Equals(item.Id, configured, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            if (!string.Equals(configured, selected.Id, StringComparison.Ordinal))
                SetDefaultId(selected.Id);
            return;
        }

        if (_pets.Count > 0)
        {
            SetDefaultId(_pets[0].Id);
            return;
        }

        if (_settings.Has(DefaultPetIdKey))
            _settings.Remove(DefaultPetIdKey);
        if (WpfHudDensityMode.IsPet(_settings.Get("hud_density_mode", WpfHudDensityMode.Default)))
            _settings.Set("hud_density_mode", WpfHudDensityMode.Orb);
    }

    private void EnsurePetsRootAvailable()
    {
        Directory.CreateDirectory(PetsRoot);
        RejectReparsePoint(PetsRoot);
    }

    private IReadOnlyList<string> TryEnumeratePetDirectories(string searchPattern)
    {
        try
        {
            return Directory.EnumerateDirectories(PetsRoot, searchPattern)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private WpfPetCatalogItem? TryValidateManagedPackage(string directory)
    {
        try
        {
            var expectedId = Path.GetFileName(directory);
            return ValidatePackage(directory, expectedId).Item;
        }
        catch
        {
            return null;
        }
    }

    private ValidatedPackage ValidatePackage(string directory, string? expectedId = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("宠物目录不存在");

        var root = Path.GetFullPath(directory);
        RejectReparsePoint(root);
        var manifestPath = Path.Combine(root, "pet.json");
        ValidateFileSize(manifestPath, MaxManifestBytes, "pet.json");
        RejectReparsePath(root, manifestPath);

        WpfPetManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<WpfPetManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions)
                ?? throw new InvalidDataException("pet.json 内容为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"pet.json 格式无效：{ex.Message}", ex);
        }

        var id = RequireText(manifest.Id, "id", 64);
        if (!IsSafeId(id))
            throw new InvalidDataException("id 只能包含字母、数字、点、下划线和连字符，且必须以字母或数字开头");
        if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException("pet.json 的 id 与托管目录不一致");
        var displayName = RequireText(manifest.DisplayName, "displayName", 120);
        var description = RequireText(manifest.Description, "description", 500);
        var spriteVersion = ResolveSpriteVersion(manifest.SpriteVersionNumber);

        var relativePath = RequireText(manifest.SpritesheetPath, "spritesheetPath", 260);
        var spritesheetPath = ResolveSafeRelativePath(root, relativePath);
        var extension = Path.GetExtension(spritesheetPath);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("精灵图只支持 PNG 或 WebP");
        ValidateFileSize(spritesheetPath, MaxSpritesheetBytes, "精灵图");
        RejectReparsePath(root, spritesheetPath);
        ValidateAtlas(spritesheetPath, spriteVersion);

        return new ValidatedPackage(
            root,
            manifestPath,
            new WpfPetCatalogItem(id, displayName, description, root, spritesheetPath));
    }

    private static int ResolveSpriteVersion(int? version)
    {
        var resolved = version ?? 1;
        if (resolved is not (1 or 2))
            throw new InvalidDataException("spriteVersionNumber 仅支持 1 或 2");
        return resolved;
    }

    private static void ValidateAtlas(string path, int spriteVersion)
    {
        var expectedHeight = spriteVersion == 1 ? AtlasV1Height : AtlasV2Height;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[12];
        if (stream.Read(signature) != signature.Length || !HasExpectedImageSignature(path, signature))
            throw new InvalidDataException("精灵图内容与 PNG/WebP 扩展名不匹配");

        var atlas = WpfPetAtlasDecoder.Decode(path);
        if (atlas.Bitmap.PixelWidth != AtlasWidth || atlas.Bitmap.PixelHeight != expectedHeight)
            throw new InvalidDataException($"V{spriteVersion} 精灵图尺寸必须为 {AtlasWidth}×{expectedHeight}");
    }

    private static bool HasExpectedImageSignature(string path, ReadOnlySpan<byte> signature)
    {
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return signature[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        return signature[..4].SequenceEqual("RIFF"u8) && signature[8..12].SequenceEqual("WEBP"u8);
    }

    private static string ExtractArchive(string archivePath, string extractionRoot)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaxArchiveBytes)
            throw new InvalidDataException("宠物压缩包大小无效");
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
            throw new InvalidDataException("压缩包不是有效的 ZIP 文件");
        stream.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("压缩包不是有效的 ZIP 文件", ex);
        }

        using (archive)
        {
            var plan = BuildArchivePlan(archive, extractionRoot);
            Directory.CreateDirectory(extractionRoot);
            long extractedBytes = 0;
            try
            {
                foreach (var item in plan.Entries)
                {
                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(item.TargetPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    using var source = item.Entry.Open();
                    using var target = new FileStream(item.TargetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyArchiveEntry(source, target, ref extractedBytes);
                    if (target.Length != item.Entry.Length)
                        throw new InvalidDataException("压缩包条目解压长度与声明不一致");
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new InvalidDataException($"无法解压宠物压缩包：{ex.Message}", ex);
            }

            return string.IsNullOrEmpty(plan.PackageRelativeRoot)
                ? extractionRoot
                : Path.Combine(extractionRoot, plan.PackageRelativeRoot);
        }
    }

    private static ArchivePlan BuildArchivePlan(ZipArchive archive, string extractionRoot)
    {
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("宠物压缩包为空");
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException($"宠物压缩包条目不能超过 {MaxArchiveEntries} 个");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArchiveEntryPlan>(archive.Entries.Count);
        var relativePaths = new List<string>(archive.Entries.Count);
        var manifestEntries = new List<string>();
        long declaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (IsArchiveLink(entry))
                throw new InvalidDataException("宠物压缩包不能包含符号链接或重解析点");

            var relativePath = entry.FullName.Replace('\\', '/');
            var isDirectory = relativePath.EndsWith("/", StringComparison.Ordinal);
            relativePath = relativePath.TrimEnd('/');
            if (relativePath.Length == 0)
                throw new InvalidDataException("宠物压缩包包含无效路径");

            var targetPath = ResolveSafeRelativePath(extractionRoot, relativePath);
            if (!targets.Add(Path.TrimEndingDirectorySeparator(targetPath)))
                throw new InvalidDataException("宠物压缩包包含重复路径");

            if (isDirectory)
            {
                if (entry.Length != 0)
                    throw new InvalidDataException("宠物压缩包目录条目无效");
            }
            else
            {
                if (entry.Length > MaxArchiveUncompressedBytes - declaredBytes)
                    throw new InvalidDataException("宠物压缩包解压后总大小超过 96 MiB");
                declaredBytes += entry.Length;
                if (Path.GetFileName(relativePath).Equals("pet.json", StringComparison.OrdinalIgnoreCase))
                    manifestEntries.Add(relativePath);
            }

            entries.Add(new ArchiveEntryPlan(entry, targetPath, isDirectory));
            relativePaths.Add(relativePath);
        }

        foreach (var file in entries.Where(static item => !item.IsDirectory))
        {
            var prefix = Path.TrimEndingDirectorySeparator(file.TargetPath) + Path.DirectorySeparatorChar;
            if (entries.Any(item => !ReferenceEquals(item, file) && item.TargetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("宠物压缩包中的文件路径与目录冲突");
        }

        return new ArchivePlan(entries, ResolveArchivePackageRelativeRoot(manifestEntries, relativePaths));
    }

    private static string ResolveArchivePackageRelativeRoot(
        IReadOnlyList<string> manifestEntries,
        IReadOnlyList<string> archivePaths)
    {
        if (manifestEntries.Count != 1)
            throw new InvalidDataException(manifestEntries.Count == 0
                ? "宠物压缩包中未找到 pet.json"
                : "宠物压缩包只能包含一个 pet.json");

        var segments = manifestEntries[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 2 || !segments[^1].Equals("pet.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("pet.json 只能位于压缩包根目录或唯一顶层目录内");
        if (segments.Length == 1)
            return "";

        var wrapper = segments[0];
        var wrapperPrefix = wrapper + "/";
        if (archivePaths.Any(path =>
                !path.Equals(wrapper, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(wrapperPrefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("宠物压缩包只能包含一个顶层目录");
        return wrapper;
    }

    private static bool IsArchiveLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var attributes = entry.ExternalAttributes;
        return ((attributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink ||
               (((FileAttributes)attributes) & FileAttributes.ReparsePoint) != 0;
    }

    private static void CopyArchiveEntry(Stream source, Stream target, ref long extractedBytes)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > MaxArchiveUncompressedBytes - extractedBytes)
                throw new InvalidDataException("宠物压缩包实际解压大小超过 96 MiB");
            target.Write(buffer, 0, read);
            extractedBytes += read;
        }
    }

    private static string ResolveSafeRelativePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("spritesheetPath 必须是相对路径");
        var segments = relativePath.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment =>
                segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                ReservedDeviceNames.Contains(segment.Split('.')[0])))
            throw new InvalidDataException("spritesheetPath 包含不安全的路径段");

        var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("spritesheetPath 超出宠物目录");
        return fullPath;
    }

    private static void RejectReparsePath(string root, string path)
    {
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("宠物资源不能经过符号链接或重解析点");
    }

    private static void ValidateFileSize(string path, long maxBytes, string label)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"缺少{label}");
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > maxBytes)
            throw new InvalidDataException($"{label}大小无效");
    }

    private static string RequireText(string? value, string field, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"pet.json 缺少 {field}");
        if (text.Length > maxLength)
            throw new InvalidDataException($"pet.json 的 {field} 过长");
        return text;
    }

    private static bool IsSafeId(string id) =>
        SafeIdPattern.IsMatch(id) &&
        !id.EndsWith('.') &&
        !ReservedDeviceNames.Contains(id.Split('.')[0]);

    private void SetDefaultId(string id)
    {
        if (!string.Equals(DefaultPetId, id, StringComparison.Ordinal))
            _settings.Set(DefaultPetIdKey, id);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                RejectReparsePoint(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A stale staging/backup directory is harmless and retried next startup.
        }
    }

    [Conditional("DEBUG")]
    private static void AssertPureContracts()
    {
        Debug.Assert(IsSafeId("my-pet_2.0"));
        Debug.Assert(!IsSafeId("../pet"));
        Debug.Assert(!IsSafeId("pet/name"));
        Debug.Assert(!IsSafeId("CON"));
        Debug.Assert(!IsSafeId("pet."));
        Debug.Assert(HasExpectedImageSignature("pet.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }));
        Debug.Assert(HasExpectedImageSignature("pet.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }));
        Debug.Assert(!HasExpectedImageSignature("pet.png", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }));
        Debug.Assert(ResolveSpriteVersion(null) == 1);
        Debug.Assert(ResolveSpriteVersion(1) == 1);
        Debug.Assert(ResolveSpriteVersion(2) == 2);
        Debug.Assert(IsSupportedArchiveFileName("pet.zip"));
        Debug.Assert(IsSupportedArchiveFileName("pet.CODEX-PET"));
        Debug.Assert(!IsSupportedArchiveFileName("pet.rar"));
        Debug.Assert(ResolveArchivePackageRelativeRoot(["pet.json"], ["pet.json", "spritesheet.png"]) == "");
        Debug.Assert(ResolveArchivePackageRelativeRoot(["pet/pet.json"], ["pet/pet.json", "pet/spritesheet.png"]) == "pet");
        var duplicateTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pet/pet.json" };
        Debug.Assert(!duplicateTargets.Add("PET/PET.JSON"));
        var extendedManifest = JsonSerializer.Deserialize<WpfPetManifest>(
            """{"id":"pet","displayName":"Pet","description":"Pet","spriteVersionNumber":2,"spritesheetPath":"pet.png","author":"Codex"}""",
            ManifestJsonOptions);
        Debug.Assert(extendedManifest?.Id == "pet");
        Debug.Assert(WpfHudDensityMode.Normalize("pet") == WpfHudDensityMode.Pet);
        Debug.Assert(WpfHudDensityMode.UsesFloatingAnchor("pet"));
        Debug.Assert(WpfHudDensityMode.NormalizePetScalePercent(40d) == WpfHudDensityMode.PetScalePercentMinimum);
        Debug.Assert(WpfHudDensityMode.NormalizePetScalePercent(155d) == 160d);
        Debug.Assert(WpfHudDensityMode.NormalizePetScalePercent(210d) == WpfHudDensityMode.PetScalePercentMaximum);
        Debug.Assert(WpfHudDensityMode.NormalizePetScalePercent(double.NaN) == WpfHudDensityMode.PetScalePercentDefault);
        var root = Path.Combine(Path.GetTempPath(), "pet-root");
        Debug.Assert(ResolveSafeRelativePath(root, "images/pet.png").StartsWith(root, StringComparison.OrdinalIgnoreCase));
        try
        {
            ResolveSafeRelativePath(root, "../pet.png");
            Debug.Fail("Path traversal must be rejected.");
        }
        catch (InvalidDataException)
        {
        }
        try
        {
            ResolveSpriteVersion(0);
            Debug.Fail("Unsupported sprite versions must be rejected.");
        }
        catch (InvalidDataException)
        {
        }
        try
        {
            ResolveArchivePackageRelativeRoot(["wrapper/nested/pet.json"], ["wrapper/nested/pet.json"]);
            Debug.Fail("Deep archive manifests must be rejected.");
        }
        catch (InvalidDataException)
        {
        }
        try
        {
            ResolveArchivePackageRelativeRoot(
                ["wrapper/pet.json"],
                ["wrapper/pet.json", "wrapper/spritesheet.png", "outside.txt"]);
            Debug.Fail("Wrapper archives must not contain entries outside the wrapper.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private sealed record ValidatedPackage(string RootPath, string ManifestPath, WpfPetCatalogItem Item);

    private sealed record ArchiveEntryPlan(ZipArchiveEntry Entry, string TargetPath, bool IsDirectory);

    private sealed record ArchivePlan(IReadOnlyList<ArchiveEntryPlan> Entries, string PackageRelativeRoot);

    private sealed class WpfPetManifest
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("spriteVersionNumber")]
        public int? SpriteVersionNumber { get; init; }

        [JsonPropertyName("spritesheetPath")]
        public string? SpritesheetPath { get; init; }
    }
}
