using System.Collections.Concurrent;
using System.Text.Json;
using ChestCounter;

namespace FortCoffres;

public sealed class Preferences
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? SyncFolder { get; set; }
    public bool IncludeDestroyed { get; set; } = true;
}

public sealed record Summary(string Name, long Total, int? Last, double? Average, int Games,
    string Status, string Sync, List<Profile> Profiles, string? Selected, MatchRecord[] Matches);

public sealed class Engine
{
    public readonly Store Store;
    public readonly Config Config;
    public readonly TrackerData Data;
    public readonly Tracker Tracker;
    public readonly Preferences Prefs;
    public readonly ConcurrentQueue<Action> Commands = new();
    public event Action<Summary>? Changed;
    readonly string prefsPath;
    string status = "Préparation…", syncStatus = "Sync désactivée";
    public Engine(string folder, string? demos = null)
    {
        L.Set("fr");
        Store = new Store(folder);
        ErrorLog.FilePath = Store.ErrorLogPath;
        Config = Store.LoadConfig() ?? new Config();
        Config.Language = "fr";
        Config.UpdateMode = "off";
        Config.ShowOverlay = false;
        Config.OpenReportOnStart = false;
        if (demos != null) Config.DemosFolder = demos;
        if (string.IsNullOrWhiteSpace(Config.DemosFolder))
            Config.DemosFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
        prefsPath = Path.Combine(folder, "preferences.json");
        Prefs = File.Exists(prefsPath) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(prefsPath)) ?? throw new InvalidDataException("Préférences invalides") : new();
        // The upstream store preserves corrupt data before recovering it. Do not silently reset this application's history.
        if (File.Exists(Store.DataPath))
            _ = JsonSerializer.Deserialize<TrackerData>(File.ReadAllText(Store.DataPath)) ?? throw new InvalidDataException("Historique invalide");
        Data = Store.LoadData();
        Tracker = new Tracker(Store, Config, Data);
        Tracker.UpgradeIfNeeded();
        Tracker.Progress += (done, total) => { Drain(); status = $"Analyse {done}/{total}"; Publish(); };
        SavePreferences();
    }
    public int Count(MatchRecord m) => Prefs.IncludeDestroyed ? m.ChestsOpened : m.Chests.Count(c => !c.Destroyed);
    public Summary Snapshot()
    {
        var selected = Profiles.Current(Data, Config);
        var matches = Profiles.View(Data, Config).Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).ToArray();
        return new(selected?.Name ?? "Compte à détecter", matches.Sum(m => (long)Count(m)),
            matches.Length == 0 ? null : Count(matches[0]), matches.Length == 0 ? null : matches.Average(Count), matches.Length,
            status, syncStatus, Profiles.List(Data), selected?.Key, matches);
    }
    public void Publish() => Changed?.Invoke(Snapshot());
    public void Drain() { while (Commands.TryDequeue(out var command)) command(); }
    public void SavePreferences()
    {
        Store.SaveConfig(Config);
        Atomic(prefsPath, JsonSerializer.Serialize(Prefs));
    }
    public static void Atomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }
    public static string Identity(MatchRecord m) => Profiles.Key(m) + "|" + m.SessionId;
    public static int Merge(TrackerData target, IEnumerable<MatchRecord> incoming)
    {
        var map = target.Matches.ToDictionary(Identity);
        int changes = 0;
        foreach (var m in incoming)
        {
            if (string.IsNullOrWhiteSpace(m.SessionId) || string.IsNullOrWhiteSpace(Profiles.Key(m)) || !m.IsBattleRoyale || m.Chests == null || m.ChestsOpened < 0) continue;
            string key = Identity(m);
            if (map.TryGetValue(key, out var old))
            {
                if (old.AnalyzerVersion >= m.AnalyzerVersion) continue;
                target.Matches.Remove(old);
            }
            target.Matches.Add(m); map[key] = m; changes++;
        }
        return changes;
    }
    readonly Dictionary<string, (long Size, long Ticks)> seenSync = new();
    public void Synchronize()
    {
        if (string.IsNullOrWhiteSpace(Prefs.SyncFolder)) { syncStatus = "Sync désactivée"; return; }
        try
        {
            Directory.CreateDirectory(Prefs.SyncFolder);
            int changes = 0;
            foreach (string file in Directory.EnumerateFiles(Prefs.SyncFolder, "FortCoffres-*.json"))
            {
                var info = new FileInfo(file);
                var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                if (seenSync.TryGetValue(file, out var last) && last == stamp) continue;
                try
                {
                    var incoming = JsonSerializer.Deserialize<TrackerData>(File.ReadAllText(file)) ?? throw new InvalidDataException("Fichier vide");
                    changes += Merge(Data, incoming.Matches);
                    seenSync[file] = stamp;
                }
                catch (Exception ex) { throw new InvalidDataException($"Historique partagé illisible : {Path.GetFileName(file)}", ex); }
            }
            if (changes > 0) Store.SaveData(Data);
            var shared = new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion, CreatedUtc = Data.CreatedUtc, Matches = Data.Matches };
            string path = Path.Combine(Prefs.SyncFolder, "FortCoffres-" + Prefs.DeviceId + ".json");
            string content = JsonSerializer.Serialize(shared);
            if (!File.Exists(path) || File.ReadAllText(path) != content) Atomic(path, content);
            // Cloud transport, if any, is the responsibility of the user's chosen sync client.
            syncStatus = "Dossier partagé à jour";
        }
        catch (Exception ex) { syncStatus = "Erreur de synchronisation"; ErrorLog.Write("Folder sync", ex); }
    }
    public void Import(string path)
    {
        var incoming = JsonSerializer.Deserialize<TrackerData>(File.ReadAllText(path)) ?? throw new InvalidDataException("Historique invalide");
        int count = Merge(Data, incoming.Matches);
        Store.SaveData(Data); status = $"Import : {count} partie(s)"; Publish();
    }
    public void Scan()
    {
        Drain();
        status = "Lecture des replays…"; Publish();
        Synchronize();
        if (!Directory.Exists(Config.DemosFolder)) status = "Dossier replays absent";
        else
        {
            Tracker.Scan();
            Tracker.UpdateLive();
            int errors = Data.ProcessedFiles.Values.Count(v => v.StartsWith("error:"));
            status = Tracker.IsLive ? "En partie · calcul à la fin" : errors > 0 ? $"{errors} replay(s) illisible(s)" : "À jour · après chaque partie";
            if (!Data.Matches.Any() && !Tracker.IsLive && errors == 0) status = "En attente d’un replay BR";
        }
        Synchronize(); Publish();
    }
    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { Scan(); }
            catch (Exception ex) { status = "Erreur · voir le journal"; ErrorLog.Write("Scan", ex); Publish(); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { break; }
        }
    }
}
