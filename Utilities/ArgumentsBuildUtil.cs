using System.Text.Json;
using StarLight_Core.Downloader;
using StarLight_Core.Models;
using StarLight_Core.Models.Authentication;
using StarLight_Core.Models.Launch;
using StarLight_Core.Models.Utilities;

namespace StarLight_Core.Utilities;

public class ArgumentsBuildUtil
{
    private readonly string jarPath;

    private string userType;

    public ArgumentsBuildUtil(GameWindowConfig gameWindowConfig, GameCoreConfig gameCoreConfig, JavaConfig javaConfig,
        BaseAccount baseAccount)
    {
        GameWindowConfig = gameWindowConfig;
        GameCoreConfig = gameCoreConfig;
        JavaConfig = javaConfig;
        BaseAccount = baseAccount;
        VersionId = gameCoreConfig.Version;
        Root = FileUtil.IsAbsolutePath(gameCoreConfig.Root)
            ? Path.Combine(gameCoreConfig.Root)
            : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), gameCoreConfig.Root);
        userType = "Mojang";
        jarPath = GetVersionJarPath();
    }

    public string VersionId { get; set; }

    public string Root { get; set; }

    public BaseAccount BaseAccount { get; set; }

    public GameWindowConfig GameWindowConfig { get; set; }

    public GameCoreConfig GameCoreConfig { get; set; }

    public JavaConfig JavaConfig { get; set; }

    // TODO: -Dfabric.log.level=DEBUG
    // 参数构建器
    public async Task<List<string>> Build()
    {
        var arguments = new List<string>();

        arguments.Add(BuildMemoryArgs());
        arguments.Add(await BuildJvmArgs());
        arguments.Add(BuildWindowArgs());
        arguments.Add(BuildGameArgs());

        return arguments;
    }

    // 内存参数
    private string BuildMemoryArgs()
    {
        var args = new List<string>();

        args.Add("-Xmn" + JavaConfig.MinMemory + "M");
        args.Add("-Xmx" + JavaConfig.MaxMemory + "M");

        return string.Join(" ", args);
    }

    // Jvm 参数
    private async Task<string> BuildJvmArgs()
    {
        ProcessAccount();

        var args = new List<string>();
        var coreInfo = GameCoreUtil.GetGameCore(VersionId, Root);
        var inheritsFromInfo = new GameCoreInfo();
        if (coreInfo.InheritsFrom != null) inheritsFromInfo = GameCoreUtil.GetGameCore(coreInfo.InheritsFrom, Root);

        var appDataPath = Path.Combine(FileUtil.GetAppDataPath(), "StarLight.Core", "jar");
        var tempPath = Path.Combine(FileUtil.GetAppDataPath(), "StarLight.Core", "temp");
        
        switch (BaseAccount)
        {
            case UnifiedPassAccount:
            {
                FileUtil.IsDirectory(appDataPath, true);
                FileUtil.IsDirectory(tempPath, true);

                if (!FileUtil.IsFile(GameCoreConfig.Nide8authPath))
                {
                    var nidePath = Path.Combine(appDataPath + Path.DirectorySeparatorChar + "nide8auth.jar");
                    var downloader = new MultiFileDownloader();
                    await downloader.DownloadFiles(new List<DownloadItem>
                    {
                        new("https://login.mc-user.com:233/index/jar", nidePath)
                    });
                    args.Add("-javaagent:\"" + nidePath + "\"=" + GameCoreConfig.UnifiedPassServerId);
                    downloader.Dispose();
                }
                else
                {
                    var authPath = FileUtil.IsAbsolutePath(GameCoreConfig.Nide8authPath)
                        ? Path.Combine(GameCoreConfig.Nide8authPath)
                        : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), GameCoreConfig.Nide8authPath);
                    args.Add("-javaagent:\"" + authPath + "\"=" + GameCoreConfig.UnifiedPassServerId);
                }

                break;
            }
            case YggdrasilAccount:
            {
                FileUtil.IsDirectory(appDataPath, true);
                FileUtil.IsDirectory(tempPath, true);

                if (!FileUtil.IsFile(GameCoreConfig.AuthlibPath))
                {
                    var assetsJson = await HttpUtil.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json");
                    var assetsEntity = assetsJson.ToJsonEntry<AuthlibLatestJsonEntity>();
                    var authlibPath = Path.Combine(appDataPath + Path.DirectorySeparatorChar + "authlib-injector.jar");
                    var downloader = new MultiFileDownloader();
                    await downloader.DownloadFiles(new List<DownloadItem>
                    {
                        new(assetsEntity?.DownloadUrl, authlibPath)
                    });
                    args.Add("-javaagent:\"" + authlibPath + "\"=" + GameCoreConfig.AuthlibServerUrl);
                    downloader.Dispose();
                }
                else
                {
                    var authPath = FileUtil.IsAbsolutePath(GameCoreConfig.Nide8authPath)
                        ? Path.Combine(GameCoreConfig.Nide8authPath)
                        : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), GameCoreConfig.Nide8authPath);
                    args.Add("-javaagent:\"" + authPath + "\"=" + GameCoreConfig.UnifiedPassServerId);
                }

                break;
            }
        }

        if (coreInfo.IsNewVersion)
            args.Add(BuildClientJarArgs());

        if (SystemUtil.IsOperatingSystemGreaterThanWin10())
            args.Add(BuildSystemArgs());

        args.Add(BuildGcAndAdvancedArguments());

        var rootPath = FileUtil.IsAbsolutePath(Root)
            ? Path.Combine(Root)
            : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), Root);
        var nativesPath = Path.Combine(rootPath, "versions", VersionId, "natives");

        if (!Directory.Exists(nativesPath))
        {
            var nativesDirectories = Directory.GetDirectories(coreInfo.root, "*natives*", SearchOption.AllDirectories);

            if (nativesDirectories.Length > 0)
                nativesPath = nativesDirectories[0];
        }

        var jvmPlaceholders = new Dictionary<string, string>
        {
            { "${natives_directory}", $"\"{nativesPath}\"" },
            { "${launcher_name}", "StarLight" },
            { "${launcher_version}", StarLightInfo.Version },
            { "${classpath}", $"\"{BuildLibrariesArgs()}\"" },
            { "${version_name}", coreInfo.Id },
            { "${library_directory}", Path.Combine(rootPath, "libraries") },
            { "${classpath_separator}", ";" }
        };

        var jvmArgumentTemplate = "";

        if (coreInfo.IsNewVersion)
        {
            BuildArgsData.JvmArgumentsTemplate.Clear();

            if (coreInfo.InheritsFrom != null)
                foreach (var element in inheritsFromInfo.Arguments.Jvm.Where(element => !ElementContainsRules(element)))
                    BuildArgsData.JvmArgumentsTemplate.Add(element.ToString());

            foreach (var element in coreInfo.Arguments.Jvm.Where(element => !ElementContainsRules(element)))
                BuildArgsData.JvmArgumentsTemplate.Add(element.ToString());

            var updatedJvmArguments = BuildArgsData.JvmArgumentsTemplate.Select(argument => argument.Replace(" ", ""))
                .ToList();

            BuildArgsData.JvmArgumentsTemplate = updatedJvmArguments;
        }

        jvmArgumentTemplate = string.Join(" ", BuildArgsData.JvmArgumentsTemplate);

        args.Add(ReplacePlaceholders(jvmArgumentTemplate, jvmPlaceholders));

        var wrapperPath = Path.Combine(appDataPath + Path.DirectorySeparatorChar + "launch_wrapper.jar");

        FileUtil.IsDirectory(appDataPath, true);
        FileUtil.IsDirectory(tempPath, true);

        if (FileUtil.IsFile(wrapperPath))
        {
            args.Add($"-Doolloo.jlw.tmpdir=\"{tempPath}\" -jar \"{wrapperPath}\"");
        }
        else
        {
            var downloader = new MultiFileDownloader();
            await downloader.DownloadFiles(new List<DownloadItem>
            {
                new("http://cdn.hjdczy.top/starlight.core/launch_wrapper.jar", wrapperPath)
            });
            args.Add($"-Doolloo.jlw.tmpdir=\"{tempPath}\" -jar \"{wrapperPath}\"");
        }

        args.Add(coreInfo.MainClass);

        return string.Join(" ", args);
    }

    private string BuildClientJarArgs()
    {
        return "-Dminecraft.client.jar=" + $"\"{jarPath}\"";
    }

    // 系统参数
    private string BuildSystemArgs()
    {
        var args = new List<string>();

        if (!SystemUtil.IsOperatingSystemGreaterThanWin10()) return string.Join(" ", args);
        args.Add("-Dos.name=\"Windows 10\"");
        args.Add("-Dos.version=10.0");

        return string.Join(" ", args);
    }

    // 游戏参数
    private string BuildGameArgs()
    {
        var coreInfo = GameCoreUtil.GetGameCore(VersionId, Root);
        var inheritsFromInfo = new GameCoreInfo();
        if (coreInfo.InheritsFrom != null) inheritsFromInfo = GameCoreUtil.GetGameCore(coreInfo.InheritsFrom, Root);

        var gamePlaceholders = new Dictionary<string, string>
        {
            { "${auth_player_name}", BaseAccount.Name },
            { "${version_name}", $"\"{VersionId}\"" },
            { "${assets_root}", Path.Combine(CurrentExecutingDirectory(Root), "assets") },
            { "${assets_index_name}", coreInfo.Assets },
            { "${auth_uuid}", BaseAccount.Uuid.Replace("-", "") },
            { "${auth_access_token}", BaseAccount.AccessToken },
            { "${clientid}", "${clientid}" },
            { "${auth_xuid}", "${auth_xuid}" },
            { "${user_type}", userType },
            { "${version_type}", $"\"SL/{TextUtil.ToTitleCase(coreInfo.Type)}\"" },
            { "${user_properties}", "{}" }
        };

        var gameDirectory = GameCoreConfig.IsVersionIsolation
            ? Path.Combine(CurrentExecutingDirectory(Root), "versions", VersionId)
            : Path.Combine(CurrentExecutingDirectory(Root));

        gamePlaceholders.Add("${game_directory}", $"\"{gameDirectory}\"");

        var gameArguments = coreInfo.IsNewVersion
            ? string.Join(" ", coreInfo.Arguments.Game.Where(element => !ElementContainsRules(element)))
            : coreInfo.MinecraftArguments;

        if (coreInfo.InheritsFrom != null)
            gameArguments += inheritsFromInfo.IsNewVersion
                ? $" {string.Join(" ", inheritsFromInfo.Arguments.Game.Where(element => !ElementContainsRules(element)))}"
                : $" {inheritsFromInfo.MinecraftArguments}";

        string[] tweakClasses =
            { "--tweakClass optifine.OptiFineForgeTweaker ", "--tweakClass optifine.OptiFineTweaker " };
        string foundTweakClass = null;

        foreach (var tweakClass in tweakClasses)
            if (gameArguments.Contains(tweakClass))
            {
                foundTweakClass = tweakClass;
                gameArguments = gameArguments.Replace(tweakClass, "").Trim();
                break;
            }

        if (foundTweakClass != null) gameArguments = $"{gameArguments} {foundTweakClass}".Trim();

        return ReplacePlaceholders(gameArguments, gamePlaceholders);
    }

    // Gc 与 Advanced 参数
    private string BuildGcAndAdvancedArguments()
    {
        var allArguments = BuildArgsData.DefaultGcArguments.Concat(BuildArgsData.DefaultAdvancedArguments);

        if (!JavaConfig.DisabledOptimizationGcArgs)
            allArguments = allArguments.Concat(BuildArgsData.OptimizationGcArguments);
        if (!JavaConfig.DisabledOptimizationAdvancedArgs)
            allArguments = allArguments.Concat(BuildArgsData.OptimizationAdvancedArguments);

        return string.Join(" ", allArguments);
    }

    // 构建 ClassPath 参数
    private string BuildLibrariesArgs()
    {
        try
        {
            var coreInfo = GameCoreUtil.GetGameCore(VersionId, Root);
            var versionPath = Path.Combine(coreInfo.root, $"{VersionId}.json");
            var librariesPath = FileUtil.IsAbsolutePath(Root)
                ? Path.Combine(Root, "libraries")
                : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), Root, "libraries");

            var cps = new List<string>();

            var inheritFromPath = coreInfo.InheritsFrom != null
                ? Path.Combine(Root, "versions", coreInfo.InheritsFrom, $"{coreInfo.InheritsFrom}.json")
                : null;

            if (inheritFromPath != null)
                cps.AddRange(ProcessLibraryPath(inheritFromPath, librariesPath));

            cps.AddRange(ProcessLibraryPath(versionPath, librariesPath));
            cps.Add(jarPath);

            return string.Join(";", cps);
        }
        catch (Exception ex)
        {
            throw new Exception($"构建Library参数错误: + {ex}");
        }
    }

    // 窗口参数
    private string BuildWindowArgs()
    {
        var args = new List<string>
        {
            "--width " + GameWindowConfig.Width,
            "--height " + GameWindowConfig.Height
        };

        if (GameWindowConfig.IsFullScreen)
            args.Add("--fullscreen");

        return string.Join(" ", args);
    }

    private IEnumerable<string> ProcessLibraryPath(string filePath, string librariesPath)
    {
        var jsonData = File.ReadAllText(filePath);
        var argsLibraries = JsonSerializer.Deserialize<ArgsBuildLibraryJson>(jsonData);

        var optifinePaths = new List<string>();
        var normalPaths = new List<string>();

        foreach (var lib in argsLibraries.Libraries)
        {
            if (lib == null || lib.Downloads == null)
            {
                var path = BuildFromName(lib.Name, librariesPath);
                if (path != null)
                    if (lib.Name.StartsWith("optifine", StringComparison.OrdinalIgnoreCase))
                        optifinePaths.Add(path);
                    else
                        normalPaths.Add(path);
                continue;
            }

            if (lib.Downloads.Classifiers != null && lib.Downloads.Classifiers.Count != 0) continue;
            {
                if (!ShouldIncludeLibrary(lib.Rule)) continue;
                var path = BuildFromName(lib.Name, librariesPath);
                if (path == null) continue;
                if (lib.Name.StartsWith("optifine", StringComparison.OrdinalIgnoreCase))
                    optifinePaths.Add(path);
                else
                    normalPaths.Add(path);
            }
        }

        foreach (var path in normalPaths)
            yield return path;

        foreach (var optifinePath in optifinePaths)
            yield return optifinePath;
    }

    private bool ShouldIncludeLibrary(LibraryJsonRule[] rules)
    {
        if (rules == null || rules.Length == 0) return true;

        var isAllow = false;
        var isDisallowForOsX = false;
        var isDisallowForLinux = false;

        foreach (var rule in rules)
            switch (rule.Action)
            {
                case "allow":
                {
                    if (rule.Os == null || (rule.Os.Name.ToLower() != "linux" && rule.Os.Name.ToLower() != "osx"))
                        isAllow = true;
                    break;
                }
                case "disallow":
                {
                    if (rule.Os != null && rule.Os.Name.ToLower() == "linux") isDisallowForLinux = true;
                    if (rule.Os != null && rule.Os.Name.ToLower() == "osx") isDisallowForOsX = true;
                    break;
                }
            }

        return !isDisallowForLinux && (isDisallowForOsX || isAllow);
    }

    private static bool ElementContainsRules(JsonElement element)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("rules", out _);

    private static string ReplacePlaceholders(string template, Dictionary<string, string> placeholders)
        => placeholders.Aggregate(template,
            (current, placeholder) => current.Replace(placeholder.Key, placeholder.Value));

    private static string BuildFromName(string name, string root)
    {
        var parts = name.Split(':');
        switch (parts.Length)
        {
            case 3:
            {
                var groupIdPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
                var artifactId = parts[1];
                var version = parts[2];

                return Path.Combine(root, groupIdPath, artifactId, version, $"{artifactId}-{version}.jar");
            }
            case 4:
            {
                var groupIdPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
                var artifactId = parts[1];
                var version = parts[2];
                var natives = parts[3];

                return Path.Combine(root, groupIdPath, artifactId, version, $"{artifactId}-{version}-{natives}.jar");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// </summary>
    /// <param name="library"></param>
    /// <param name="root"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static string BuildNativesName(Library library, string root)
    {
        var parts = library.Name.Split(':');
        if (parts.Length != 3) throw new ArgumentException("名称格式无效,获取错误");

        var groupIdPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifactId = parts[1];
        var version = parts[2];

        var arch = SystemUtil.GetOperatingSystemBit();

        var windowsNative = library.Natives["windows"].Replace("${arch}", arch.ToString());

        return Path.Combine(root, groupIdPath, artifactId, version, $"{artifactId}-{version}-{windowsNative}.jar");
    }

    /// <summary>
    /// </summary>
    /// <param name="name"></param>
    /// <param name="root"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static string? BuildNewNativesName(string name, string root)
    {
        var parts = name.Split(':');

        var groupIdPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifactId = parts[1];
        var version = parts[2];

        var path = Path.Combine(root, groupIdPath, artifactId, version);

        if (parts.Length <= 3 || !parts[3].StartsWith("natives-")) return null;
        var classifier = parts[3];
        return Path.Combine(path, $"{artifactId}-{version}-{classifier}.jar");
    }

    // 完整路径
    private string CurrentExecutingDirectory(string path) => FileUtil.IsAbsolutePath(Root)
        ? Path.Combine(Root)
        : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), Root);

    // 判断账户
    private void ProcessAccount() => userType = BaseAccount is MicrosoftAccount ? "msa" : "Mojang";

    // 获取版本 Jar 实际路径
    private string GetVersionJarPath()
    {
        var coreInfo = GameCoreUtil.GetGameCore(VersionId, Root);
        var versionPath = FileUtil.IsAbsolutePath(Root)
            ? Path.Combine(Root, "versions")
            : Path.Combine(FileUtil.GetCurrentExecutingDirectory(), Root, "versions");
        if (coreInfo.InheritsFrom != null && coreInfo.InheritsFrom != "null")
            return Path.Combine(Root, "versions", coreInfo.InheritsFrom, $"{coreInfo.InheritsFrom}.jar");
        return Path.Combine(versionPath, coreInfo.Id, $"{coreInfo.Id}.jar");
    }
}