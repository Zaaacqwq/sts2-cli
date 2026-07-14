using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Locate the directory containing sts2.dll: STS2_LIB env, walk up from BaseDirectory, then BaseDirectory/lib.
    /// </summary>
    private static string ResolveLibDirectory()
    {
        var envLib = Environment.GetEnvironmentVariable("STS2_LIB");
        if (!string.IsNullOrWhiteSpace(envLib))
        {
            var p = Path.GetFullPath(envLib.Trim());
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "sts2.dll")))
                return p;
        }

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "lib");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sts2.dll")))
                return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lib"));
    }

    /// <summary>
    /// Windows wakes sleeping threads only on its scheduler tick, which defaults to
    /// 64Hz — so <c>Sleep(5)</c>, <c>Sleep(10)</c> and <c>WaitHandle.WaitOne(1)</c>
    /// all really take 15.625ms. The dispatcher polls with <c>RunOne(1)</c> and
    /// <c>WaitForActionExecutor</c> drains twice per action, so two of those rounded
    /// waits land on the critical path of every single decision: measured at 58ms per
    /// engine round-trip, against ~2ms of actual work.
    ///
    /// timeBeginPeriod(1) raises the tick to 1kHz for this process. Since Windows 10
    /// 2004 the request is per-process, so the host asking for it does not help us —
    /// the engine has to ask for itself.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            try { TimeBeginPeriod(1); } catch { /* precision is an optimisation, not a requirement */ }
        }

        // Prevent unhandled exceptions from crashing the process
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception?.Message}");
            e.SetObserved();
        };

        var libDir = ResolveLibDirectory();

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            // Also check game directory (via STS2_GAME_DIR env var)
            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        if (args.Contains("--self-test-dispatcher"))
        {
            DispatcherSelfTest.Run();
            return;
        }

        // All game-engine state lives on a single dedicated thread. Construct the
        // simulator there too so no engine object is ever touched from two threads.
        using var engine = new EngineThread();
        var sim = engine.Invoke(() => new RunSimulator());
        WriteLine(new Dictionary<string, object?> { ["type"] = "ready", ["version"] = "0.2.0" });

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            Dictionary<string, object?>? result;
            var fatal = false;
            try
            {
                var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                result = engine.Invoke(() =>
                {
                    sim.ThrowIfEngineFatal();
                    var response = HandleCommand(sim, cmd);
                    // Deep legacy handlers still contain narrow catch blocks. A poison
                    // check here makes it impossible for one of them to turn a fatal
                    // dispatcher failure back into a healthy-looking decision response.
                    sim.ThrowIfEngineFatal();
                    return response;
                });
            }
            catch (JsonException ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
            }
            catch (EngineFatalException ex)
            {
                fatal = true;
                Console.Error.WriteLine($"[FATAL] {ex}");
                result = new Dictionary<string, object?>
                {
                    ["type"] = "error",
                    ["fatal"] = true,
                    ["code"] = ex.Code,
                    ["message"] = ex.Message,
                };
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}" };
            }

            if (result != null)
            {
                WriteLine(result);
                if (fatal) break;
                if (result.TryGetValue("type", out var resultTypeObj) &&
                    string.Equals(resultTypeObj as string, "quit_result", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
    }

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        switch (cmdType)
        {
            case "start_run":
            {
                var character = cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad";
                var ascension = cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0;
                var seed = cmd.TryGetProperty("seed", out var s) ? s.GetString() : null;
                var language = cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en";
                var result = sim.StartRun(
                    character, ascension, seed, language);
                // STS2 v0.107.1 completes ModManager lazily during the first
                // RunState creation. Retry transparently after that warm-up.
                if (result.TryGetValue("message", out var message) &&
                    message is string text && text.Contains("ModManager is not finished initializing"))
                    result = sim.StartRun(character, ascension, seed, language);
                return result;
            }

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "load_save":
            {
                var savePath = cmd.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                var saveJson = cmd.TryGetProperty("json", out var sj) ? sj.GetString() : null;
                if (saveJson == null && savePath != null)
                {
                    if (!File.Exists(savePath))
                        return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Save file not found: {savePath}" };
                    saveJson = File.ReadAllText(savePath);
                }
                if (saveJson == null)
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "Provide 'path' or 'json' for load_save" };
                var loadLang = cmd.TryGetProperty("lang", out var le) ? (le.GetString() ?? "en") : "en";
                return sim.LoadSave(saveJson, loadLang);
            }
            case "get_map":
                return sim.GetFullMap();

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                return sim.SetPlayer(args);
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                return sim.EnterRoom(roomType, encounter, eventId);
            }

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                return sim.SetDrawOrder(cards);
            }

            case "start_combat":
            {
                var character = cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad";
                var ascension = cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0;
                var seed = cmd.TryGetProperty("seed", out var s) ? s.GetString() : null;
                var language = cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(encounter))
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "start_combat requires 'encounter'" };
                var playerArgs = new Dictionary<string, JsonElement>();
                if (cmd.TryGetProperty("player", out var playerElem))
                    foreach (var prop in playerElem.EnumerateObject())
                        playerArgs[prop.Name] = prop.Value;
                List<string>? drawOrder = null;
                if (cmd.TryGetProperty("draw_order", out var orderElem))
                {
                    drawOrder = new List<string>();
                    foreach (var c in orderElem.EnumerateArray())
                        drawOrder.Add(c.GetString() ?? "");
                }
                return sim.StartCombat(character, ascension, seed, language, playerArgs, encounter, drawOrder);
            }

            case "list_models":
            {
                var kind = cmd.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                return sim.ListModels(kind);
            }

            case "write_continue_save":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                return sim.SaveCheckpoint(outputPath);
            }

            case "quit":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var saveResult = sim.SaveCheckpoint(outputPath);
                    bool saveOk = saveResult.TryGetValue("success", out var sObj) && sObj is bool b && b;
                    if (!saveOk)
                    {
                        // Save failed — do NOT clean up so the caller can retry with a different path.
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "save_error",
                            ["save"] = saveResult,
                        };
                    }
                    sim.CleanUp();
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "quit_result",
                        ["success"] = true,
                        ["save"] = saveResult,
                    };
                }
                sim.CleanUp();
                return new Dictionary<string, object?>
                {
                    ["type"] = "quit_result",
                    ["success"] = true,
                    ["save"] = null,
                };
            }

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    static void WriteLine(Dictionary<string, object?> data)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
        Console.Out.Flush();
    }
}
