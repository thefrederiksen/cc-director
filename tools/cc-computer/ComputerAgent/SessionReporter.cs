using System.Text;
using System.Text.Json;

namespace CCComputer.Agent;

/// <summary>
/// Generates a self-contained HTML report from the structured evidence_chain.json
/// produced by <see cref="EvidenceChainLogger"/>.
/// </summary>
public static class SessionReporter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Generate an HTML report from a session folder's evidence_chain.json.
    /// </summary>
    public static string GenerateReport(string sessionFolder)
    {
        var chainPath = Path.Combine(sessionFolder, "evidence_chain.json");
        if (!File.Exists(chainPath))
        {
            throw new FileNotFoundException($"Evidence chain not found: {chainPath}");
        }

        var json = File.ReadAllText(chainPath);
        var chain = JsonSerializer.Deserialize<EvidenceChain>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize evidence chain");

        var html = BuildHtml(chain, sessionFolder);
        var reportPath = Path.Combine(sessionFolder, "report.html");
        File.WriteAllText(reportPath, html);
        return reportPath;
    }

    /// <summary>
    /// Generate a report from the most recent session.
    /// </summary>
    public static string? GenerateLatestReport(string? baseFolder = null)
    {
        baseFolder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CCComputer",
            "sessions");

        if (!Directory.Exists(baseFolder))
            return null;

        var latestSession = Directory.GetDirectories(baseFolder)
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (latestSession == null)
            return null;

        return GenerateReport(latestSession);
    }

    // ────────────────────────────────────────────────────────────────────
    //  HTML generation
    // ────────────────────────────────────────────────────────────────────

    private static string BuildHtml(EvidenceChain chain, string sessionFolder)
    {
        var h = new StringBuilder();

        h.AppendLine("<!DOCTYPE html>");
        h.AppendLine("<html lang=\"en\">");
        h.AppendLine("<head>");
        h.AppendLine("<meta charset=\"UTF-8\">");
        h.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        h.AppendLine($"<title>Session Report — {Esc(chain.SessionId)}</title>");
        AppendStyles(h);
        h.AppendLine("</head>");
        h.AppendLine("<body>");

        AppendHeader(h, chain);
        AppendSummary(h, chain);
        AppendStepTimeline(h, chain, sessionFolder);
        AppendFinalResponse(h, chain);
        AppendModal(h);
        AppendScript(h);

        h.AppendLine("</body>");
        h.AppendLine("</html>");
        return h.ToString();
    }

    // ── Styles ───────────────────────────────────────────────────────────

    private static void AppendStyles(StringBuilder h)
    {
        h.AppendLine("<style>");
        h.Append("""
        *{box-sizing:border-box;margin:0;padding:0}
        body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:#f0f2f5;color:#1e293b;line-height:1.5;padding:24px}
        .container{max-width:1100px;margin:0 auto}

        /* Header */
        .header{background:linear-gradient(135deg,#1e3a5f,#2563eb);color:#fff;padding:24px 32px;border-radius:12px;margin-bottom:20px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:12px}
        .header h1{font-size:1.4em;font-weight:600}
        .header-meta{font-size:.85em;opacity:.85;text-align:right}

        /* Cards */
        .card{background:#fff;border-radius:12px;box-shadow:0 1px 3px rgba(0,0,0,.08);padding:20px 24px;margin-bottom:16px}
        .card h2{font-size:1.1em;margin-bottom:12px;color:#334155}

        /* Summary */
        .summary-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px;margin-top:12px}
        .stat{text-align:center;padding:12px;border-radius:8px;background:#f8fafc}
        .stat-value{font-size:1.6em;font-weight:700}
        .stat-label{font-size:.78em;color:#64748b;text-transform:uppercase;letter-spacing:.05em}
        .request-text{background:#f8fafc;padding:12px 16px;border-radius:8px;border-left:4px solid #2563eb;margin-top:12px;white-space:pre-wrap;font-size:.95em}

        /* Badges */
        .badge{display:inline-block;padding:3px 10px;border-radius:20px;font-size:.78em;font-weight:600;text-transform:uppercase;letter-spacing:.04em}
        .badge-success{background:#dcfce7;color:#16a34a}
        .badge-error{background:#fee2e2;color:#dc2626}
        .badge-max_iterations{background:#fef9c3;color:#a16207}
        .badge-cancelled{background:#e2e8f0;color:#475569}
        .badge-incomplete{background:#e2e8f0;color:#475569}

        /* Step timeline */
        .step{position:relative;padding-left:32px;margin-bottom:4px}
        .step::before{content:'';position:absolute;left:11px;top:0;bottom:0;width:2px;background:#cbd5e1}
        .step:last-child::before{display:none}
        .step-dot{position:absolute;left:4px;top:20px;width:16px;height:16px;border-radius:50%;background:#2563eb;border:3px solid #fff;box-shadow:0 0 0 2px #2563eb}
        .step-dot.fail{background:#dc2626;box-shadow:0 0 0 2px #dc2626}
        .step-header{cursor:pointer;padding:12px 16px;background:#fff;border-radius:10px;box-shadow:0 1px 3px rgba(0,0,0,.06);display:flex;justify-content:space-between;align-items:center;user-select:none;transition:background .15s}
        .step-header:hover{background:#f8fafc}
        .step-header .arrow{transition:transform .2s;font-size:.8em;color:#94a3b8}
        .step-header.open .arrow{transform:rotate(90deg)}
        .step-title{font-weight:600;font-size:.92em}
        .step-meta{font-size:.78em;color:#64748b;display:flex;gap:12px}
        .step-body{display:none;padding:12px 16px;background:#fff;border-radius:0 0 10px 10px;box-shadow:0 1px 3px rgba(0,0,0,.06);margin-top:-4px}
        .step-body.open{display:block}

        /* Actions table */
        .actions-table{width:100%;border-collapse:collapse;font-size:.85em;margin-bottom:12px}
        .actions-table th{text-align:left;padding:6px 10px;background:#f1f5f9;font-weight:600;color:#475569}
        .actions-table td{padding:6px 10px;border-top:1px solid #f1f5f9;vertical-align:top}
        .actions-table .mono{font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:.88em;word-break:break-all}
        .actions-table .result-cell{max-width:400px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .actions-table .result-cell:hover{white-space:normal;overflow:visible}
        .icon-ok{color:#16a34a}
        .icon-fail{color:#dc2626}

        /* Screenshot */
        .screenshot-wrap{margin:10px 0}
        .screenshot-wrap img{max-width:100%;border:1px solid #e2e8f0;border-radius:8px;cursor:pointer;transition:box-shadow .15s}
        .screenshot-wrap img:hover{box-shadow:0 4px 12px rgba(0,0,0,.12)}

        /* Observation */
        .observation{background:#fffbeb;border-left:4px solid #f59e0b;padding:10px 14px;border-radius:0 8px 8px 0;font-size:.9em;margin-top:8px;white-space:pre-wrap}

        /* Final response */
        .final-response{white-space:pre-wrap;font-size:.95em;margin-top:12px;padding:12px 16px;background:#f8fafc;border-radius:8px}

        /* Modal */
        .modal-overlay{display:none;position:fixed;z-index:9999;inset:0;background:rgba(0,0,0,.92);cursor:zoom-out;align-items:center;justify-content:center}
        .modal-overlay.active{display:flex}
        .modal-overlay img{max-width:96%;max-height:96%;object-fit:contain;border-radius:4px}
        """);
        h.AppendLine();
        h.AppendLine("</style>");
    }

    // ── Header bar ───────────────────────────────────────────────────────

    private static void AppendHeader(StringBuilder h, EvidenceChain chain)
    {
        var duration = chain.TotalDurationMs.HasValue
            ? FormatDuration(chain.TotalDurationMs.Value)
            : "—";

        h.AppendLine("<div class=\"container\">");
        h.AppendLine("<div class=\"header\">");
        h.AppendLine("  <div><h1>CC Computer Session Report</h1></div>");
        h.AppendLine("  <div class=\"header-meta\">");
        h.AppendLine($"    <div>Session: {Esc(chain.SessionId)}</div>");
        h.AppendLine($"    <div>Model: {Esc(chain.Config.ModelId)}</div>");
        h.AppendLine($"    <div>Duration: {duration}</div>");
        h.AppendLine("  </div>");
        h.AppendLine("</div>");
    }

    // ── Summary card ─────────────────────────────────────────────────────

    private static void AppendSummary(StringBuilder h, EvidenceChain chain)
    {
        var stepCount = chain.Steps.Count;
        var actionCount = chain.Steps.Sum(s => s.Actions.Count);
        var screenshotCount = chain.Steps.Count(s => s.Screenshot != null);
        var failureCount = chain.Steps.Sum(s => s.Actions.Count(a => !a.Success));
        var outcomeBadge = OutcomeBadge(chain.Outcome ?? "unknown");

        h.AppendLine("<div class=\"card\">");
        h.AppendLine($"  <h2>Summary {outcomeBadge}</h2>");
        h.AppendLine($"  <div class=\"request-text\">{Esc(chain.Request)}</div>");
        h.AppendLine("  <div class=\"summary-grid\">");
        AppendStat(h, stepCount.ToString(), "Steps");
        AppendStat(h, actionCount.ToString(), "Actions");
        AppendStat(h, screenshotCount.ToString(), "Screenshots");
        AppendStat(h, failureCount.ToString(), "Failures");
        h.AppendLine("  </div>");
        h.AppendLine("</div>");
    }

    private static void AppendStat(StringBuilder h, string value, string label)
    {
        h.AppendLine($"  <div class=\"stat\"><div class=\"stat-value\">{value}</div><div class=\"stat-label\">{label}</div></div>");
    }

    // ── Step timeline ────────────────────────────────────────────────────

    private static void AppendStepTimeline(StringBuilder h, EvidenceChain chain, string sessionFolder)
    {
        if (chain.Steps.Count == 0) return;

        h.AppendLine("<div class=\"card\"><h2>Step Timeline</h2></div>");

        foreach (var step in chain.Steps)
        {
            var hasFail = step.Actions.Any(a => !a.Success);
            var dotClass = hasFail ? "step-dot fail" : "step-dot";
            var stepDuration = step.StepDurationMs.HasValue ? FormatDuration(step.StepDurationMs.Value) : "";
            var actionSummary = step.Actions.Count > 0
                ? string.Join(", ", step.Actions.Select(a => a.ToolName))
                : "no actions";

            h.AppendLine($"<div class=\"step\">");
            h.AppendLine($"  <div class=\"{dotClass}\"></div>");
            h.AppendLine($"  <div class=\"step-header\" onclick=\"toggleStep(this)\">");
            h.AppendLine($"    <div><span class=\"step-title\">Step {step.StepNumber}</span> <span style=\"color:#64748b;font-size:.85em\">— {Esc(actionSummary)}</span></div>");
            h.AppendLine($"    <div class=\"step-meta\">");
            if (!string.IsNullOrEmpty(stepDuration))
                h.AppendLine($"      <span>{stepDuration}</span>");
            h.AppendLine($"      <span class=\"arrow\">&#9654;</span>");
            h.AppendLine($"    </div>");
            h.AppendLine($"  </div>");
            h.AppendLine($"  <div class=\"step-body\">");

            // Actions table
            if (step.Actions.Count > 0)
            {
                h.AppendLine("    <table class=\"actions-table\">");
                h.AppendLine("      <tr><th></th><th>Tool</th><th>Arguments</th><th>Result</th><th>Duration</th></tr>");
                foreach (var action in step.Actions)
                {
                    var icon = action.Success ? "<span class=\"icon-ok\">&#10003;</span>" : "<span class=\"icon-fail\">&#10007;</span>";
                    var argsText = FormatArguments(action.Arguments);
                    var resultText = action.Result ?? action.ErrorMessage ?? "";
                    var dur = action.DurationMs.HasValue ? $"{action.DurationMs}ms" : "";

                    h.AppendLine("      <tr>");
                    h.AppendLine($"        <td>{icon}</td>");
                    h.AppendLine($"        <td class=\"mono\">{Esc(action.ToolName)}</td>");
                    h.AppendLine($"        <td class=\"mono\">{Esc(argsText)}</td>");
                    h.AppendLine($"        <td class=\"mono result-cell\" title=\"{Esc(resultText)}\">{Esc(Truncate(resultText, 200))}</td>");
                    h.AppendLine($"        <td>{dur}</td>");
                    h.AppendLine("      </tr>");
                }
                h.AppendLine("    </table>");
            }

            // Screenshot
            if (step.Screenshot != null)
            {
                var imgPath = step.Screenshot.Path;
                var relativePath = imgPath.StartsWith(sessionFolder, StringComparison.OrdinalIgnoreCase)
                    ? imgPath.Substring(sessionFolder.Length).TrimStart('\\', '/')
                    : imgPath;
                var forwardSlash = relativePath.Replace("\\", "/");

                h.AppendLine("    <div class=\"screenshot-wrap\">");
                h.AppendLine($"      <img src=\"{Esc(relativePath)}\" alt=\"Step {step.StepNumber} screenshot\" onclick=\"showFull('{EscJs(forwardSlash)}')\" loading=\"lazy\">");
                h.AppendLine("    </div>");
            }

            // Observation
            if (!string.IsNullOrEmpty(step.Observation))
            {
                h.AppendLine($"    <div class=\"observation\">{Esc(step.Observation)}</div>");
            }

            h.AppendLine("  </div>"); // step-body
            h.AppendLine("</div>");   // step
        }
    }

    // ── Final response ───────────────────────────────────────────────────

    private static void AppendFinalResponse(StringBuilder h, EvidenceChain chain)
    {
        var outcomeBadge = OutcomeBadge(chain.Outcome ?? "unknown");

        h.AppendLine("<div class=\"card\">");
        h.AppendLine($"  <h2>Final Response {outcomeBadge}</h2>");
        if (!string.IsNullOrEmpty(chain.FinalResponse))
        {
            h.AppendLine($"  <div class=\"final-response\">{Esc(chain.FinalResponse)}</div>");
        }
        else
        {
            h.AppendLine("  <div class=\"final-response\" style=\"color:#94a3b8\">(no response recorded)</div>");
        }
        h.AppendLine("</div>");
        h.AppendLine("</div>"); // close .container
    }

    // ── Modal ────────────────────────────────────────────────────────────

    private static void AppendModal(StringBuilder h)
    {
        h.AppendLine("<div id=\"modal\" class=\"modal-overlay\" onclick=\"this.classList.remove('active')\">");
        h.AppendLine("  <img id=\"modal-img\" src=\"\" alt=\"Full size screenshot\">");
        h.AppendLine("</div>");
    }

    // ── Script ───────────────────────────────────────────────────────────

    private static void AppendScript(StringBuilder h)
    {
        h.AppendLine("<script>");
        h.Append("""
        function toggleStep(el){
            el.classList.toggle('open');
            el.nextElementSibling.classList.toggle('open');
        }
        function showFull(src){
            document.getElementById('modal-img').src=src;
            document.getElementById('modal').classList.add('active');
        }
        document.addEventListener('keydown',function(e){
            if(e.key==='Escape') document.getElementById('modal').classList.remove('active');
        });
        """);
        h.AppendLine();
        h.AppendLine("</script>");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string OutcomeBadge(string outcome)
    {
        return $"<span class=\"badge badge-{Esc(outcome)}\">{Esc(outcome.Replace("_", " "))}</span>";
    }

    private static string FormatDuration(long ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        var minutes = ms / 60_000;
        var seconds = (ms % 60_000) / 1000;
        return $"{minutes}m {seconds}s";
    }

    private static string FormatArguments(Dictionary<string, object?> args)
    {
        if (args.Count == 0) return "";
        try
        {
            return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    private static string Esc(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static string EscJs(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"");
    }
}
