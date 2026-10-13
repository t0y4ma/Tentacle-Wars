// MultiTargetBuildWindow.cs
// 複数ターゲットの一括ビルド(+ZIP + 任意でgit commit/push)を行う汎用エディタ拡張。
// Unity 6 (6000.x) 向け。Assets/Editor/ 以下(Editorフォルダ内)に置くこと。
//
// ・設定は ProjectSettings/MultiTargetBuild.json に保存される(プロジェクトごと・Git管理可能)。
// ・メニュー: Tools > Multi-Target Build > Open / Build All
// ・コードから: MultiTargetBuild.Builder.BuildAll(showDialogs: false)
// ・CI/バッチ: -executeMethod MultiTargetBuild.Builder.BuildAllBatch
//
// Linux(IL2CPP)の注意:
//   sysrootパッケージの型が UnityEditor.Sysroot を継承するのは、
//   「アクティブターゲットが Linux の状態でスクリプトがコンパイルされている」ときだけ。
//   それ以外の状態で Linux をビルドすると "No Linux sysroot found for x64" で失敗する。
//   このツールは必要なら先に Linux へ切り替えて再コンパイルを待ち、
//   ドメインリロード後に自動で続きを実行する。Linuxのジョブは常に先頭で実行する。
//
// 結果は Library/MultiTargetBuild/last-result.json に書き出される(ダイアログを待たずに確認できる)。
// ジョブごとの所要時間の履歴から、次回の所要時間の目安も表示する。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MultiTargetBuild
{
    public enum Subtarget
    {
        Player = 0,
        Server = 1
    }

    [Serializable]
    public class BuildJob
    {
        public bool enabled = true;
        public string name = "New Build";
        public BuildTarget target = BuildTarget.WebGL;
        // Standalone(Windows/macOS/Linux)のみ有効。Serverにすると Dedicated Server ビルドになる
        public Subtarget subtarget = Subtarget.Player;
        // 出力先フォルダ(プロジェクトからの相対パス可)
        public string outputDirectory = "Builds/WebGL";
        // 実行ファイル名(拡張子なし)。WebGL/iOSでは使わない
        public string executableName = "Game";
        // ビルド前に出力先フォルダを空にする
        public bool cleanOutputDirectory = true;
        public bool developmentBuild = false;
        // ビルド後に出力フォルダをZIPにする
        public bool createZip = false;
        public string zipPath = "";
        // ZIPから除外するパス(出力フォルダからの相対。フォルダ指定で配下すべて)
        public List<string> zipExcludes = new List<string>();
    }

    [Serializable]
    public class BuildConfig
    {
        public List<BuildJob> jobs = new List<BuildJob>();

        // falseなら scenes を使う
        public bool useBuildSettingsScenes = true;
        public List<string> scenes = new List<string>();

        // すべて成功したら、このターゲットをアクティブにして終える
        public bool switchTargetWhenDone = true;
        public BuildTarget finalActiveTarget = BuildTarget.WebGL;

        // すべて成功したら git add / commit / push する
        public bool gitCommitAndPush = false;
        public List<string> gitAddPaths = new List<string>();
        public string gitCommitMessage = "Build {date}";
        public string gitRemote = "origin";

        // 完了/失敗時にダイアログを出す(AI/自動実行ではfalseにするとエディタが止まらない)
        public bool showDialogs = true;
    }

    [Serializable]
    public class JobResult
    {
        public string name;
        public string target;
        public string result;
        public double seconds;
        public int errors;
        public string output;
        public string zip;
    }

    [Serializable]
    public class RunResult
    {
        public string startedAt;
        public string finishedAt;
        public bool success;
        public string message;
        public List<JobResult> jobs = new List<JobResult>();
    }

    [Serializable]
    class DurationHistory
    {
        public List<DurationEntry> entries = new List<DurationEntry>();
    }

    [Serializable]
    class DurationEntry
    {
        public string key;
        public List<double> seconds = new List<double>();
    }

    public static class Builder
    {
        public const string ConfigPath = "ProjectSettings/MultiTargetBuild.json";
        const string ResultDir = "Library/MultiTargetBuild";
        const string ResultPath = ResultDir + "/last-result.json";
        const string HistoryPath = ResultDir + "/history.json";
        const string PendingKey = "MultiTargetBuild.PendingAfterLinuxSwitch";
        const string PendingDialogsKey = "MultiTargetBuild.PendingShowDialogs";
        const string Tag = "[MultiTargetBuild] ";

        // ============================================================
        // 設定の読み書き
        // ============================================================

        public static BuildConfig LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonUtility.FromJson<BuildConfig>(File.ReadAllText(ConfigPath)) ?? new BuildConfig();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Tag + "Failed to read " + ConfigPath + ": " + e.Message);
            }
            return new BuildConfig();
        }

        public static void SaveConfig(BuildConfig config)
        {
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(config, true));
        }

        // ============================================================
        // 入口
        // ============================================================

        [MenuItem("Tools/Multi-Target Build/Build All", priority = 1)]
        static void BuildAllMenu()
        {
            BuildAll(LoadConfig().showDialogs);
        }

        // コマンドライン用: Unity -batchmode -quit -projectPath ... -executeMethod MultiTargetBuild.Builder.BuildAllBatch
        // バッチモードではドメインリロード後の再開ができないため、
        // Linux(IL2CPP)を含む場合は起動引数 -buildTarget Linux64 を付けて起動すること。
        public static void BuildAllBatch()
        {
            var config = LoadConfig();
            if (NeedsLinuxRecompile(config))
            {
                Debug.LogError(Tag + "Scripts are not compiled for Linux. Start Unity with -buildTarget Linux64.");
                EditorApplication.Exit(1);
                return;
            }
            bool ok = Run(config, false);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        // エディタ/コードからの入口。
        // Linuxの準備(再コンパイル)が必要なときは切り替えて戻り、リロード後に自動で続きを実行する。
        public static void BuildAll(bool showDialogs)
        {
            var config = LoadConfig();
            config.showDialogs = showDialogs;

            string error = Validate(config);
            if (error != null)
            {
                Fail(config, error);
                return;
            }

            if (NeedsLinuxRecompile(config))
            {
                Debug.Log(Tag + "Switching to Linux and recompiling scripts first. The build continues automatically after the reload.");
                SessionState.SetBool(PendingKey, true);
                SessionState.SetBool(PendingDialogsKey, showDialogs);

                if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneLinux64)
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                else
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
                return;
            }

            Run(config, showDialogs);
        }

        [InitializeOnLoadMethod]
        static void ResumeAfterReload()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;

            // 先に消す(失敗しても無限に繰り返さないため)
            SessionState.EraseBool(PendingKey);
            bool showDialogs = SessionState.GetBool(PendingDialogsKey, true);
            SessionState.EraseBool(PendingDialogsKey);

            EditorApplication.delayCall += () =>
            {
                var config = LoadConfig();
                config.showDialogs = showDialogs;
                if (NeedsLinuxRecompile(config))
                {
                    Fail(config, "Switched to Linux, but the Linux sysroot is still not available.\n" +
                                 "Install com.unity.toolchain.<host>-linux and com.unity.sdk.linux-x86_64 from the Package Manager, then restart the editor.");
                    return;
                }
                Run(config, showDialogs);
            };
        }

        // ============================================================
        // Linux(IL2CPP)の準備判定
        // ============================================================

        public static bool IsLinuxSysrootReady()
        {
            var sysrootType = typeof(Editor).Assembly.GetType("UnityEditor.Sysroot");
            if (sysrootType == null) return false;
            return TypeCache.GetTypesDerivedFrom(sysrootType).Count > 0;
        }

        static bool IsStandalone(BuildTarget t)
        {
            return t == BuildTarget.StandaloneWindows || t == BuildTarget.StandaloneWindows64
                || t == BuildTarget.StandaloneOSX || t == BuildTarget.StandaloneLinux64;
        }

        static NamedBuildTarget NamedTargetOf(BuildJob job)
        {
            if (IsStandalone(job.target) && job.subtarget == Subtarget.Server) return NamedBuildTarget.Server;
            return NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(job.target));
        }

        static bool NeedsLinuxToolchain(BuildJob job)
        {
            if (!job.enabled || job.target != BuildTarget.StandaloneLinux64) return false;
            return PlayerSettings.GetScriptingBackend(NamedTargetOf(job)) == ScriptingImplementation.IL2CPP;
        }

        static bool NeedsLinuxRecompile(BuildConfig config)
        {
            return config.jobs.Any(NeedsLinuxToolchain) && !IsLinuxSysrootReady();
        }

        // ============================================================
        // 実行
        // ============================================================

        static string Validate(BuildConfig config)
        {
            if (config.jobs.Count(j => j.enabled) == 0) return "No enabled build jobs.";
            if (GetScenes(config).Length == 0) return "No scenes to build.";
            foreach (var j in config.jobs.Where(j => j.enabled))
            {
                if (string.IsNullOrWhiteSpace(j.outputDirectory)) return "Output directory is empty: " + j.name;
                if (j.createZip && string.IsNullOrWhiteSpace(j.zipPath)) return "ZIP path is empty: " + j.name;
                if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(j.target), j.target))
                    return "Build support for " + j.target + " is not installed: " + j.name;
            }
            return null;
        }

        static string[] GetScenes(BuildConfig config)
        {
            if (!config.useBuildSettingsScenes) return config.scenes.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            return EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        }

        static bool Run(BuildConfig config, bool showDialogs)
        {
            var run = new RunResult { startedAt = DateTime.Now.ToString("o") };
            var scenes = GetScenes(config);

            // Linux(IL2CPP)は、スクリプトがLinux向けにコンパイルされている今のうちに先に行う。
            // (他のターゲットを先にビルドするとアクティブターゲットが変わるため)
            var jobs = config.jobs.Where(j => j.enabled)
                                  .Select((j, i) => (j, i))
                                  .OrderBy(x => NeedsLinuxToolchain(x.j) ? 0 : 1)
                                  .ThenBy(x => x.i)
                                  .Select(x => x.j)
                                  .ToList();

            Debug.Log(Tag + "Build All start: " + string.Join(", ", jobs.Select(j => j.name)));
            bool success = true;

            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    EditorUtility.DisplayProgressBar("Multi-Target Build", "Building " + job.name + "...", (float)i / jobs.Count);

                    var jr = BuildOne(job, scenes);
                    run.jobs.Add(jr);
                    if (jr.result != BuildResult.Succeeded.ToString())
                    {
                        success = false;
                        run.message = job.name + " failed.";
                        break;
                    }
                    RecordDuration(job, jr.seconds);

                    if (job.createZip)
                    {
                        EditorUtility.DisplayProgressBar("Multi-Target Build", "Zipping " + job.name + "...", (i + 0.9f) / jobs.Count);
                        CreateZip(job);
                        jr.zip = Path.GetFullPath(job.zipPath);
                    }
                }

                if (success && config.switchTargetWhenDone)
                {
                    var group = BuildPipeline.GetBuildTargetGroup(config.finalActiveTarget);
                    if (EditorUserBuildSettings.activeBuildTarget != config.finalActiveTarget)
                        EditorUserBuildSettings.SwitchActiveBuildTarget(group, config.finalActiveTarget);
                }

                if (success && config.gitCommitAndPush)
                {
                    EditorUtility.DisplayProgressBar("Multi-Target Build", "git commit / push...", 0.99f);
                    string gitMessage;
                    if (!GitCommitAndPush(config, out gitMessage))
                    {
                        success = false;
                        run.message = "Builds succeeded, but git failed: " + gitMessage;
                    }
                    else run.message = gitMessage;
                }

                if (success && string.IsNullOrEmpty(run.message)) run.message = "All builds succeeded.";
            }
            catch (Exception e)
            {
                success = false;
                run.message = e.Message;
                Debug.LogException(e);
            }
            finally
            {
                // Server ビルド後にサブターゲットが Server のまま残ると、
                // エディタ上で Mirror 等が「ヘッドレス(サーバー)」と判定して自動でサーバー起動してしまう。
                // 成否に関わらず Player に戻す。
                if (EditorUserBuildSettings.standaloneBuildSubtarget != StandaloneBuildSubtarget.Player)
                {
                    EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
                    Debug.Log(Tag + "Standalone subtarget reset to Player.");
                }
                EditorUtility.ClearProgressBar();
            }

            run.success = success;
            run.finishedAt = DateTime.Now.ToString("o");
            WriteResult(run);

            string summary = string.Join("\n", run.jobs.Select(j =>
                j.name + ": " + j.result + " (" + TimeSpan.FromSeconds(j.seconds).ToString(@"m\:ss") + ")"));
            if (success) Debug.Log(Tag + "Build All Complete\n" + summary + "\n" + run.message);
            else Debug.LogError(Tag + "Build All Failed: " + run.message + "\n" + summary);

            if (showDialogs && !Application.isBatchMode)
                EditorUtility.DisplayDialog(success ? "Build Complete" : "Build Error",
                    run.message + "\n\n" + summary, "OK");

            return success;
        }

        static JobResult BuildOne(BuildJob job, string[] scenes)
        {
            if (job.cleanOutputDirectory && Directory.Exists(job.outputDirectory))
                Directory.Delete(job.outputDirectory, true);
            Directory.CreateDirectory(job.outputDirectory);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = LocationPathFor(job),
                target = job.target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(job.target),
                options = job.developmentBuild ? BuildOptions.Development : BuildOptions.None
            };
            if (IsStandalone(job.target))
                options.subtarget = (int)(job.subtarget == Subtarget.Server
                    ? StandaloneBuildSubtarget.Server
                    : StandaloneBuildSubtarget.Player);

            Debug.Log(Tag + "Building " + job.name + " -> " + options.locationPathName);
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;

            foreach (var step in report.steps)
                foreach (var m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                        Debug.LogError(Tag + "[" + job.name + "] " + m.content);

            Debug.Log(Tag + "===== " + job.name + " Build Result =====\nResult: " + s.result
                      + "\nTotal Time: " + s.totalTime + "\nTotal Errors: " + s.totalErrors);

            return new JobResult
            {
                name = job.name,
                target = job.target + (IsStandalone(job.target) ? "/" + job.subtarget : ""),
                result = s.result.ToString(),
                seconds = s.totalTime.TotalSeconds,
                errors = s.totalErrors,
                output = Path.GetFullPath(options.locationPathName)
            };
        }

        public static string LocationPathFor(BuildJob job)
        {
            string name = string.IsNullOrWhiteSpace(job.executableName) ? Application.productName : job.executableName.Trim();
            switch (job.target)
            {
                case BuildTarget.WebGL:
                case BuildTarget.iOS:
                    return job.outputDirectory;
                case BuildTarget.StandaloneLinux64:
                    return Path.Combine(job.outputDirectory, StripExt(name, ".x86_64") + ".x86_64");
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return Path.Combine(job.outputDirectory, StripExt(name, ".exe") + ".exe");
                case BuildTarget.StandaloneOSX:
                    return Path.Combine(job.outputDirectory, StripExt(name, ".app") + ".app");
                case BuildTarget.Android:
                    return Path.Combine(job.outputDirectory, name + (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk"));
                default:
                    return Path.Combine(job.outputDirectory, name);
            }
        }

        static string StripExt(string name, string ext)
        {
            return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ext.Length) : name;
        }

        // ============================================================
        // ZIP
        // ============================================================

        static void CreateZip(BuildJob job)
        {
            string root = Path.GetFullPath(job.outputDirectory);
            string zip = Path.GetFullPath(job.zipPath);
            if (IsInside(zip, root)) throw new Exception("ZIP path must be outside the output directory: " + job.zipPath);

            if (File.Exists(zip)) File.Delete(zip);
            var dir = Path.GetDirectoryName(zip);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var excludes = job.zipExcludes.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Normalize).ToList();
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string rel = Normalize(Path.GetRelativePath(root, file));
                    if (excludes.Any(e => rel.Equals(e, StringComparison.OrdinalIgnoreCase)
                                          || rel.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    archive.CreateEntryFromFile(file, rel, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
            Debug.Log(Tag + "ZIP: " + zip + " (" + new FileInfo(zip).Length / 1024 / 1024 + " MB)");
        }

        static string Normalize(string p) => p.Replace('\\', '/').Trim('/');

        static bool IsInside(string path, string root)
        {
            string a = Path.GetFullPath(path).TrimEnd('\\', '/');
            string b = Path.GetFullPath(root).TrimEnd('\\', '/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(b + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // git
        // ============================================================

        static bool GitCommitAndPush(BuildConfig config, out string message)
        {
            var paths = config.gitAddPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paths.Count == 0) { message = "git: no paths to add."; return false; }

            // ウイルス対策ソフト等が一時ファイルを掴んで失敗することがあるので、addは1回だけ再試行する
            string args = "add -- " + string.Join(" ", paths.Select(Quote));
            var add = Git(args);
            if (add.exitCode != 0) { System.Threading.Thread.Sleep(2000); add = Git(args); }
            if (add.exitCode != 0) { message = "git add failed: " + add.error; return false; }

            // ステージされた変更が無ければコミットしない
            if (Git("diff --cached --quiet").exitCode == 0) { message = "git: nothing to commit."; return true; }

            string msgFile = Path.Combine(ResultDir, "commit-message.txt");
            Directory.CreateDirectory(ResultDir);
            File.WriteAllText(msgFile, config.gitCommitMessage.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                new UTF8Encoding(false));

            var commit = Git("commit -F " + Quote(msgFile));
            if (commit.exitCode != 0) { message = "git commit failed: " + commit.error + commit.output; return false; }

            var push = Git("push " + config.gitRemote + " HEAD");
            if (push.exitCode != 0) { message = "git push failed: " + push.error; return false; }

            message = "Committed and pushed: " + commit.output.Split('\n').FirstOrDefault();
            return true;
        }

        static (int exitCode, string output, string error) Git(string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                // 標準エラーを並行して読む（順に読むと、警告が多いときにパイプが詰まって git も Unity も止まる）
                var errTask = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                string e = errTask.Result;
                p.WaitForExit();
                Debug.Log(Tag + "git " + args + " -> " + p.ExitCode + (o.Length > 0 ? "\n" + o : ""));
                return (p.ExitCode, o, e);
            }
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        // ============================================================
        // 結果と所要時間の履歴
        // ============================================================

        static void Fail(BuildConfig config, string message)
        {
            Debug.LogError(Tag + message);
            WriteResult(new RunResult
            {
                startedAt = DateTime.Now.ToString("o"),
                finishedAt = DateTime.Now.ToString("o"),
                success = false,
                message = message
            });
            if (config.showDialogs && !Application.isBatchMode)
                EditorUtility.DisplayDialog("Build Error", message, "OK");
        }

        static void WriteResult(RunResult run)
        {
            Directory.CreateDirectory(ResultDir);
            File.WriteAllText(ResultPath, JsonUtility.ToJson(run, true));
        }

        public static RunResult ReadLastResult()
        {
            try { return File.Exists(ResultPath) ? JsonUtility.FromJson<RunResult>(File.ReadAllText(ResultPath)) : null; }
            catch { return null; }
        }

        static string HistoryKey(BuildJob job) => job.target + "/" + job.subtarget;

        static DurationHistory LoadHistory()
        {
            try { return File.Exists(HistoryPath) ? JsonUtility.FromJson<DurationHistory>(File.ReadAllText(HistoryPath)) : new DurationHistory(); }
            catch { return new DurationHistory(); }
        }

        static void RecordDuration(BuildJob job, double seconds)
        {
            var h = LoadHistory();
            var entry = h.entries.FirstOrDefault(e => e.key == HistoryKey(job));
            if (entry == null) { entry = new DurationEntry { key = HistoryKey(job) }; h.entries.Add(entry); }
            entry.seconds.Add(seconds);
            if (entry.seconds.Count > 10) entry.seconds.RemoveAt(0);
            Directory.CreateDirectory(ResultDir);
            File.WriteAllText(HistoryPath, JsonUtility.ToJson(h, true));
        }

        // 直近の実績(中央値)から、有効なジョブ全体の所要時間の目安を返す。実績が無いジョブがあれば -1。
        public static double EstimateSeconds(BuildConfig config)
        {
            var h = LoadHistory();
            double total = 0;
            foreach (var job in config.jobs.Where(j => j.enabled))
            {
                var e = h.entries.FirstOrDefault(x => x.key == HistoryKey(job));
                if (e == null || e.seconds.Count == 0) return -1;
                var sorted = e.seconds.OrderBy(v => v).ToList();
                total += sorted[sorted.Count / 2];
            }
            return total;
        }
    }

    // ============================================================
    // 設定ウィンドウ
    // ============================================================

    public class MultiTargetBuildWindow : EditorWindow
    {
        BuildConfig config;
        Vector2 scroll;
        readonly Dictionary<BuildJob, bool> foldouts = new Dictionary<BuildJob, bool>();

        [MenuItem("Tools/Multi-Target Build/Open", priority = 0)]
        public static void Open()
        {
            var w = GetWindow<MultiTargetBuildWindow>("Multi-Target Build");
            w.minSize = new Vector2(620, 520);
            w.Show();
        }

        void OnEnable() => config = Builder.LoadConfig();

        void OnGUI()
        {
            if (config == null) config = Builder.LoadConfig();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Build Jobs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Linux (IL2CPP) jobs always run first. If the scripts are not compiled for Linux, " +
                                    "the editor switches to Linux, recompiles, and continues automatically.", MessageType.Info);

            for (int i = 0; i < config.jobs.Count; i++)
            {
                if (DrawJob(i)) GUIUtility.ExitGUI();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Job")) config.jobs.Add(new BuildJob());
            if (GUILayout.Button("Preset: WebGL + Linux Server")) AddWebGlAndServerPreset();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Scenes", EditorStyles.boldLabel);
            config.useBuildSettingsScenes = EditorGUILayout.ToggleLeft("Use enabled scenes in Build Settings", config.useBuildSettingsScenes);
            if (!config.useBuildSettingsScenes) DrawStringList(config.scenes, "Scene path (Assets/...unity)");

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("After Build", EditorStyles.boldLabel);
            config.switchTargetWhenDone = EditorGUILayout.ToggleLeft("Switch active build target when done", config.switchTargetWhenDone);
            if (config.switchTargetWhenDone)
                config.finalActiveTarget = (BuildTarget)EditorGUILayout.EnumPopup("Final Active Target", config.finalActiveTarget);
            config.showDialogs = EditorGUILayout.ToggleLeft("Show completion dialog (turn off for automated runs)", config.showDialogs);

            config.gitCommitAndPush = EditorGUILayout.ToggleLeft("git add / commit / push when all builds succeed", config.gitCommitAndPush);
            if (config.gitCommitAndPush)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Paths to add (e.g. Assets, Builds/WebGL)");
                DrawStringList(config.gitAddPaths, "path");
                config.gitCommitMessage = EditorGUILayout.TextField("Commit Message ({date})", config.gitCommitMessage);
                config.gitRemote = EditorGUILayout.TextField("Remote", config.gitRemote);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(12);
            double est = Builder.EstimateSeconds(config);
            EditorGUILayout.LabelField("Estimated time",
                est < 0 ? "(no history yet)" : "about " + TimeSpan.FromSeconds(est).ToString(@"m\:ss") + " (+ target switching)");

            var last = Builder.ReadLastResult();
            if (last != null)
                EditorGUILayout.HelpBox("Last run: " + (last.success ? "SUCCESS" : "FAILED") + " — " + last.message
                    + "\n" + string.Join("\n", last.jobs.Select(j => j.name + ": " + j.result)), last.success ? MessageType.None : MessageType.Error);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Settings", GUILayout.Height(30))) Builder.SaveConfig(config);
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("BUILD ALL", GUILayout.Height(30)))
            {
                Builder.SaveConfig(config);
                // OnGUIの途中でビルドを始めるとレイアウトが壊れるので、1フレーム後に実行する
                EditorApplication.delayCall += () => Builder.BuildAll(config.showDialogs);
            }
            GUI.backgroundColor = old;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        // trueを返したら一覧が変わった(描画を打ち切る)
        bool DrawJob(int index)
        {
            var job = config.jobs[index];
            if (!foldouts.ContainsKey(job)) foldouts[job] = true;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            job.enabled = EditorGUILayout.Toggle(job.enabled, GUILayout.Width(18));
            foldouts[job] = EditorGUILayout.Foldout(foldouts[job], job.name + "  (" + job.target
                + (job.subtarget == Subtarget.Server ? " Server" : "") + ")", true);
            if (GUILayout.Button("▲", GUILayout.Width(24)) && index > 0) { Swap(index, index - 1); return true; }
            if (GUILayout.Button("▼", GUILayout.Width(24)) && index < config.jobs.Count - 1) { Swap(index, index + 1); return true; }
            if (GUILayout.Button("X", GUILayout.Width(24))) { config.jobs.RemoveAt(index); return true; }
            EditorGUILayout.EndHorizontal();

            if (foldouts[job])
            {
                job.name = EditorGUILayout.TextField("Name", job.name);
                job.target = (BuildTarget)EditorGUILayout.EnumPopup("Target", job.target);
                bool standalone = job.target == BuildTarget.StandaloneWindows || job.target == BuildTarget.StandaloneWindows64
                               || job.target == BuildTarget.StandaloneOSX || job.target == BuildTarget.StandaloneLinux64;
                if (standalone) job.subtarget = (Subtarget)EditorGUILayout.EnumPopup("Subtarget", job.subtarget);

                EditorGUILayout.BeginHorizontal();
                job.outputDirectory = EditorGUILayout.TextField("Output Directory", job.outputDirectory);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string sel = EditorUtility.OpenFolderPanel("Output Directory", Path.GetFullPath(job.outputDirectory), "");
                    if (!string.IsNullOrEmpty(sel)) job.outputDirectory = ProjectRelative(sel);
                }
                EditorGUILayout.EndHorizontal();

                if (job.target != BuildTarget.WebGL && job.target != BuildTarget.iOS)
                    job.executableName = EditorGUILayout.TextField("Executable Name", job.executableName);
                EditorGUILayout.LabelField("Output", Builder.LocationPathFor(job).Replace('\\', '/'), EditorStyles.miniLabel);

                job.cleanOutputDirectory = EditorGUILayout.Toggle("Clean Output First", job.cleanOutputDirectory);
                job.developmentBuild = EditorGUILayout.Toggle("Development Build", job.developmentBuild);

                job.createZip = EditorGUILayout.Toggle("Create ZIP", job.createZip);
                if (job.createZip)
                {
                    EditorGUI.indentLevel++;
                    job.zipPath = EditorGUILayout.TextField("ZIP Path", job.zipPath);
                    EditorGUILayout.LabelField("Exclude from ZIP (relative to output directory)");
                    DrawStringList(job.zipExcludes, "relative path");
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUILayout.EndVertical();
            return false;
        }

        void Swap(int a, int b)
        {
            (config.jobs[a], config.jobs[b]) = (config.jobs[b], config.jobs[a]);
        }

        static void DrawStringList(List<string> list, string placeholder)
        {
            for (int i = 0; i < list.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                list[i] = EditorGUILayout.TextField(list[i]);
                if (GUILayout.Button("X", GUILayout.Width(24))) { list.RemoveAt(i); EditorGUILayout.EndHorizontal(); GUIUtility.ExitGUI(); }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+ " + placeholder, GUILayout.Width(200))) list.Add("");
        }

        void AddWebGlAndServerPreset()
        {
            config.jobs.Add(new BuildJob
            {
                name = "Linux Server",
                target = BuildTarget.StandaloneLinux64,
                subtarget = Subtarget.Server,
                outputDirectory = "Builds/LinuxServer",
                executableName = "Server",
                createZip = true,
                zipPath = "Builds/LinuxServer.zip",
                zipExcludes = new List<string>
                {
                    "Server_BackUpThisFolder_ButDontShipItWithYourGame",
                    Application.productName + "_BurstDebugInformation_DoNotShip"
                }
            });
            config.jobs.Add(new BuildJob
            {
                name = "WebGL",
                target = BuildTarget.WebGL,
                outputDirectory = "Builds/WebGL"
            });
            config.finalActiveTarget = BuildTarget.WebGL;
            config.switchTargetWhenDone = true;
        }

        static string ProjectRelative(string path)
        {
            string project = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(project, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(project.Length).TrimStart('/')
                : full;
        }
    }
}
