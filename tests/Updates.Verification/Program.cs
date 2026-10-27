using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;
using BroidShot.Updates;

internal static class Program
{
    private static int assertions;
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(Root, "profile"));
        try
        {
            if (args.Length == 2 && args[0] == "--package")
            {
                PackageManifest package = PackageManifest.Load(args[1]);
                VerifyAssemblyReferences(args[1]);
                Console.WriteLine($"PASS: packaged version {package.Version}, {package.Files.Count} verified files.");
                return 0;
            }
            await Resume(false);
            await Resume(true);
            await CancelAndResume();
            await BadPartial();
            await CompletePartial();
            await Releases();
            await InstallAndRollback();
            await FailedHelper();
            Console.WriteLine($"PASS: {assertions} update assertions; fake HTTP and temporary packages only.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); assertions++; }
    private static void VerifyAssemblyReferences(string directory)
    {
        var files = Directory.GetFiles(directory, "BroidShot*.dll");
        var identities = files.Select(AssemblyName.GetAssemblyName).ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                string name = metadata.GetString(reference.Name);
                if (!name.StartsWith("BroidShot", StringComparison.Ordinal)) continue;
                Assert(identities.TryGetValue(name, out var actual) && actual.Version == reference.Version,
                    $"{Path.GetFileName(file)} requires {name} {reference.Version}, but the package has a missing or different assembly version.");
            }
        }
    }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
    private static byte[] Payload => Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
    private static ExternalToolDefinition Tool(byte[] payload, string name = "test") => new(name, "tool.exe", (_, _) => Task.FromResult(new ExternalToolRelease
    { Version = "1", DownloadUrl = new Uri("https://downloads.test/tool.exe"), AssetFileName = "tool.exe", Sha256 = Sha(payload) })) { MaxDownloadBytes = 1024 * 1024, DisplayName = "Test" };
    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };

    private static async Task Resume(bool serverIgnoresRange)
    {
        byte[] payload = Payload;
        int calls = 0;
        var http = new Handler(request =>
        {
            if (++calls == 1)
            {
                var partial = Reply(request, payload[..512]);
                partial.Content.Headers.ContentLength = payload.Length;
                return partial;
            }
            Assert(request.Headers.Range?.Ranges.Single().From == 512, "A truncated download resumes at its saved byte offset.");
            if (serverIgnoresRange) return Reply(request, payload);
            var resumed = Reply(request, payload[512..], HttpStatusCode.PartialContent);
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(512, payload.Length - 1, payload.Length);
            return resumed;
        });
        using var manager = new ExternalToolManager(Folder("resume-" + serverIgnoresRange), http);
        var tool = Tool(payload);
        await Fails(() => manager.EnsureAsync(tool), "A truncated response must not install a tool.");
        Assert(Directory.GetFiles(Path.Combine(manager.ToolsRoot, "test"), "*.part").Length == 1, "Interrupted bytes are retained for retry.");
        var progress = new List<ExternalToolProgress>();
        string file = await manager.EnsureAsync(tool, new ProgressInline<ExternalToolProgress>(progress.Add));
        Assert(File.ReadAllBytes(file).SequenceEqual(payload), "Resumed or restarted bytes match the complete verified file.");
        Assert(progress.Any(p => p.IsResuming) == !serverIgnoresRange, "Resume is reported only when the server accepted Range.");
        Assert(!Directory.EnumerateFiles(Path.Combine(manager.ToolsRoot, "test"), "*.part").Any(), "The partial cache is removed after successful installation.");
    }

    private static async Task CancelAndResume()
    {
        byte[] payload = Payload;
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        var handler = new Handler(request =>
        {
            if (++calls == 1) return new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new CancellingStream(payload, cancel)) };
            Assert(request.Headers.Range?.Ranges.Single().From == 128, "Cancellation resumes exactly after the completed chunk.");
            var result = Reply(request, payload[128..], HttpStatusCode.PartialContent);
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(128, payload.Length - 1, payload.Length);
            return result;
        });
        using var manager = new ExternalToolManager(Folder("cancel"), handler);
        var tool = Tool(payload);
        try { await manager.EnsureAsync(tool, cancellationToken: cancel.Token); throw new InvalidOperationException("Cancellation expected."); }
        catch (OperationCanceledException) { Assert(manager.TryGetExecutable(tool) is null, "A cancelled tool is not installed."); }
        Assert(File.ReadAllBytes(await manager.EnsureAsync(tool)).SequenceEqual(payload), "A later attempt can finish a cancelled download.");
    }

    private static async Task BadPartial()
    {
        byte[] payload = Payload;
        var handler = new Handler(request =>
        {
            var result = Reply(request, payload[128..], HttpStatusCode.PartialContent);
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(128, payload.Length - 1, payload.Length);
            return result;
        });
        using var manager = new ExternalToolManager(Folder("corrupt"), handler);
        string folder = Directory.CreateDirectory(Path.Combine(manager.ToolsRoot, "test")).FullName;
        File.WriteAllBytes(Path.Combine(folder, ".download-" + Sha(payload) + ".part"), new byte[128]);
        await Fails(() => manager.EnsureAsync(Tool(payload)), "A corrupt saved prefix must fail the final SHA-256 check.");
        Assert(!Directory.EnumerateFiles(folder, "*.part").Any() && manager.TryGetExecutable(Tool(payload)) is null, "Corrupt partial bytes are discarded rather than installed.");
    }

    private static async Task CompletePartial()
    {
        byte[] payload = Payload;
        using var manager = new ExternalToolManager(Folder("complete"), new Handler(request =>
        {
            var result = Reply(request, Array.Empty<byte>(), HttpStatusCode.RequestedRangeNotSatisfiable);
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(payload.Length);
            return result;
        }));
        string folder = Directory.CreateDirectory(Path.Combine(manager.ToolsRoot, "test")).FullName;
        File.WriteAllBytes(Path.Combine(folder, ".download-" + Sha(payload) + ".part"), payload);
        Assert(File.ReadAllBytes(await manager.EnsureAsync(Tool(payload))).SequenceEqual(payload), "A fully downloaded cache is verified when the server returns 416.");
    }

    private static string ReleaseJson(string version, string url, string? digest = null, string? notes = "Yeni özellikler") => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, body = notes,
        assets = new[] { new { name = AppUpdateService.AssetName, browser_download_url = url, digest = digest is null ? null : "sha256:" + digest } }
    });

    private static async Task Releases()
    {
        string good = "https://github.com/Broid07/BroidShot/releases/download/v0.4.1/" + AppUpdateService.AssetName;
        using var empty = new AppUpdateService(new Handler(r => Reply(r, Array.Empty<byte>(), HttpStatusCode.NotFound)), Folder("empty"), new Version(0, 4, 0, 0));
        Assert(await empty.CheckAsync(CancellationToken.None) is null && empty.Status.Contains("İlk"), "A repository with no first release is a valid startup.");
        using var current = new AppUpdateService(new Handler(r => Reply(r, Encoding.UTF8.GetBytes(ReleaseJson("0.4.0", good, new string('a', 64))))), Folder("same"), new Version(0, 4, 0, 0));
        Assert(await current.CheckAsync(CancellationToken.None) is null, "Equal version tags do not create update loops.");
        using var noDigest = new AppUpdateService(new Handler(r => Reply(r, Encoding.UTF8.GetBytes(ReleaseJson("0.4.1", good)))), Folder("no-hash"), new Version(0, 4, 0, 0));
        await Fails(async () => { await noDigest.CheckAsync(CancellationToken.None); }, "Updates without checksums are refused.");
        using var foreign = new AppUpdateService(new Handler(r => Reply(r, Encoding.UTF8.GetBytes(ReleaseJson("0.4.1", "https://github.com/other/repo/releases/download/v1/file.zip", new string('a', 64))))), Folder("foreign"), new Version(0, 4, 0, 0));
        await Fails(async () => { await foreign.CheckAsync(CancellationToken.None); }, "Update assets must come from the configured repository.");

        string packageFolder = CreatePackage(Folder("release-fixture"), "0.4.1");
        string zip = Path.Combine(Root, "release.zip");
        ZipFile.CreateFromDirectory(packageFolder, zip);
        byte[] bytes = File.ReadAllBytes(zip);
        using var service = new AppUpdateService(new Handler(r => r.RequestUri!.Host == "api.github.com"
            ? Reply(r, Encoding.UTF8.GetBytes(ReleaseJson("0.4.1", good, Sha(bytes)))) : Reply(r, bytes)), Folder("download-app"), new Version(0, 4, 0, 0));
        AppUpdate update = (await service.CheckAsync(CancellationToken.None))!;
        Assert(update.Version == "0.4.1" && update.Notes == "Yeni özellikler", "A newer version includes release notes.");
        string staged = await service.DownloadAsync(update, new ProgressInline<ExternalToolProgress>(_ => { }), CancellationToken.None);
        Assert(PackageManifest.Load(staged).Version == "0.4.1", "The downloaded package and every manifest-owned file are verified.");
        File.WriteAllText(Path.Combine(staged, "BroidShot.dll"), "tampered");
        await Fails(() => Task.Run(() => PackageManifest.Load(staged)), "Tampered extracted update files are refused.");
        Assert(!AppUpdateService.TryVersion("latest", out _) && AppUpdateService.TryVersion("v0.4.1", out _), "Only numeric version tags are accepted.");
    }

    private static string CreatePackage(string folder, string version, bool obsolete = false)
    {
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { "BroidShot.exe", "BroidShot.dll", "BroidShot.runtimeconfig.json" }) File.WriteAllText(Path.Combine(folder, file), version + ":" + file);
        if (obsolete) File.WriteAllText(Path.Combine(folder, "old-owned.dll"), "old");
        var manifest = new PackageManifest { Version = version, Files = Directory.GetFiles(folder).Where(p => Path.GetFileName(p) != PackageManifest.FileName).Select(p => new PackageFile(Path.GetFileName(p), Sha(File.ReadAllBytes(p)))).ToList() };
        File.WriteAllText(Path.Combine(folder, PackageManifest.FileName), JsonSerializer.Serialize(manifest));
        return folder;
    }

    private static async Task InstallAndRollback()
    {
        string cache = Folder("transaction-cache");
        string source = CreatePackage(Path.Combine(cache, "packages", "current"), "0.4.1");
        string target = CreatePackage(Folder("installed"), "0.4.0", obsolete: true);
        File.WriteAllText(Path.Combine(target, "user-note.txt"), "keep me");
        var plan = new UpdatePlan { SourceDirectory = source, InstallDirectory = target };
        bool installed = await UpdateInstaller.ApplyAsync(plan, cache, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert(installed && PackageManifest.Load(target).Version == "0.4.1", "A healthy startup commits the new version.");
        Assert(File.ReadAllText(Path.Combine(target, "user-note.txt")) == "keep me", "Unrelated user files are preserved.");
        Assert(!File.Exists(Path.Combine(target, "old-owned.dll")) && PackageManifest.Load(Path.Combine(cache, "previous")).Version == "0.4.0", "Obsolete owned files are removed and exactly one previous package is retained.");
        CreatePackage(source, "0.4.2");
        File.WriteAllText(Path.Combine(source, "new-feature.dll"), "new");
        PackageManifest nextManifest = PackageManifest.Load(source);
        nextManifest.Files.Add(new PackageFile("new-feature.dll", Sha(File.ReadAllBytes(Path.Combine(source, "new-feature.dll")))));
        File.WriteAllText(Path.Combine(source, PackageManifest.FileName), JsonSerializer.Serialize(nextManifest));
        Assert(!await UpdateInstaller.ApplyAsync(plan, cache, (_, _) => Task.FromResult(false), CancellationToken.None), "Failed startup triggers rollback.");
        Assert(PackageManifest.Load(target).Version == "0.4.1" && File.Exists(Path.Combine(target, "user-note.txt")), "Rollback restores the previous working files and leaves user files intact.");
        Assert(!File.Exists(Path.Combine(target, "new-feature.dll")), "Rollback also removes files introduced only by the failed version.");
        await Fails(() => UpdateInstaller.ApplyAsync(plan, cache, (_, _) => Task.FromException<bool>(new IOException("start failed")), CancellationToken.None), "A startup exception is reported.");
        Assert(PackageManifest.Load(target).Version == "0.4.1", "Startup exceptions also restore the working package.");
        PackageManifest bad = PackageManifest.Load(source);
        bad.Files.Add(new PackageFile("../outside.txt", new string('a', 64)));
        File.WriteAllText(Path.Combine(source, PackageManifest.FileName), JsonSerializer.Serialize(bad));
        await Fails(() => UpdateInstaller.ApplyAsync(plan, cache, (_, _) => Task.FromResult(true), CancellationToken.None), "Path traversal is refused before installed files are touched.");
        Assert(PackageManifest.Load(target).Version == "0.4.1", "A rejected package leaves the installed version unchanged.");
    }

    private static async Task Fails(Func<Task> action, string message)
    {
        try { await action(); } catch (Exception ex) when (ex is ExternalToolException or IOException or InvalidDataException or HttpRequestException) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private static async Task FailedHelper()
    {
        string target = CreatePackage(Folder("helper-installed"), "0.4.0");
        string source = CreatePackage(Path.Combine(AppUpdateService.UpdatesRoot, "packages", "broidshot", "current"), "0.4.1");
        await Fails(() => UpdateHandoff.ScheduleAsync(source, target, Array.Empty<string>()), "A helper that cannot start never triggers application replacement.");
        Assert(PackageManifest.Load(target).Version == "0.4.0", "The existing application is intact when updater preflight fails.");
        Assert(File.ReadAllText(Path.Combine(AppUpdateService.UpdatesRoot, "failed-version.txt")) == "0.4.1", "An unstartable helper version is blocked to avoid restart loops.");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private sealed class ProgressInline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class CancellingStream(byte[] data, CancellationTokenSource source) : MemoryStream(data)
    {
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (reads++ > 0) source.Cancel();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 128)], token);
        }
    }
}
