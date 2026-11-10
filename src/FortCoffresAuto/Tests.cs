using ChestCounter;
using System.Text.Json;

namespace FortCoffres;
static class Tests
{
    public static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        void Check(bool pass, string name) { if (!pass) throw new Exception("Échec : " + name); }
        MatchRecord Make(string id, string epic, int count) => new() { SessionId = id, EpicId = epic, PlayerName = epic, AnalyzerVersion = 15, ChestsOpened = count, Chests = Enumerable.Range(0, count).Select(_ => new ChestRecord()).ToList() };
        var d = new TrackerData(); var a = Make("match1", "compte1", 3); var b = Make("match1", "compte2", 7);
        Check(Engine.Merge(d, new[] { a, b, a }) == 2 && d.Matches.Count == 2, "déduplication par compte et partie");
        Check(Engine.Merge(d, new[] { a }) == 0, "import idempotent");
        var newer = Make("match1", "compte1", 4); newer.AnalyzerVersion = 16;
        Check(Engine.Merge(d, new[] { newer }) == 1 && d.Matches.Count == 2, "réanalyse remplace la partie");
        var c = new Config { Profile = "compte2" };
        Check(Profiles.View(d, c).Matches.Single().ChestsOpened == 7, "isolation des comptes");
        var engine = new Engine(Path.Combine(folder, "local"), Path.Combine(folder, "demos"));
        engine.Data.Matches.Add(a); engine.Data.Matches.Add(Make("match2", "compte1", 0));
        engine.Config.Profile = "compte1"; a.Chests[0].Destroyed = true;
        Check(engine.Prefs.IncludeDestroyed, "coffres détruits inclus par défaut");
        engine.Prefs.IncludeDestroyed = false;
        var s = engine.Snapshot(); Check(s.Total == 2 && s.Average == 1 && s.Games == 2, "coffres détruits exclus, moyenne avec zéro");
        engine.Prefs.IncludeDestroyed = true; Check(engine.Snapshot().Total == 3, "option coffres détruits");
        engine.Prefs.SyncFolder = Path.Combine(folder, "partage"); engine.Synchronize();
        var second = new Engine(Path.Combine(folder, "second")); second.Prefs.SyncFolder = engine.Prefs.SyncFolder; second.Synchronize(); second.Synchronize();
        Check(second.Data.Matches.Count == 2, "synchronisation sans doublon");
        second.Data.Matches.Add(b); second.Synchronize(); engine.Synchronize();
        Check(engine.Data.Matches.Count == 3, "fusion dans les deux sens");
        engine.SavePreferences(); engine.Store.SaveData(engine.Data);
        var reload = new Engine(Path.Combine(folder, "local")); Check(reload.Data.Matches.Count == 3 && reload.Config.Profile == "compte1", "persistance du profil et des parties");
        string bad = Path.Combine(folder, "invalide.replay"); File.WriteAllText(bad, "invalide");
        Check(ReplayHeaderProbe.IsLive(bad) == null && ReplayHeaderProbe.KeyMissing(bad), "replay invalide non déclaré complet");
        Engine.Atomic(Path.Combine(folder, "tests.txt"), "11 vérifications réussies : déduplication, idempotence, réanalyse, comptes, coffres détruits inclus par défaut, calculs, option coffres détruits, synchronisation, fusion, persistance, replay invalide.");
    }
}
