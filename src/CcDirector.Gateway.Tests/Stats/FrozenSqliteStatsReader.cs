using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Microsoft.Data.Sqlite;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// The twelve statistics read projections EXACTLY as they were before the Entity Framework port: raw
/// <see cref="SqliteConnection"/>, the version 5 SQL verbatim, the same in-memory identity mirror, the same
/// C# ordering and tie-breaks.
///
/// This is the OTHER ARM of the parity proof. <see cref="GatewayStatsReadParityTests"/> writes one fixture
/// with the real aggregator, then reads the SAME PHYSICAL ROWS twice - once through this reader and once
/// through the ported implementation - and compares the rendered bodies. Without a second arm the parity
/// claim would rest on the ported code agreeing with itself.
///
/// It is a FROZEN COPY and that is deliberate, not an oversight. It pins what shipped, so it must NOT be
/// updated to track later changes to the aggregator: the moment it is edited to make a comparison pass it
/// stops being evidence about the port and becomes a mirror of whatever the port now does. If a genuine
/// behaviour change is made on purpose later, the parity test is retired with a written reason rather than
/// this file being brought into line.
///
/// The mirror is rebuilt here from the same tables <c>LoadMirror</c> reads, with the same
/// <see cref="StringComparer.OrdinalIgnoreCase"/> per-tenant identity maps - because the display spellings
/// these projections render come from the mirror and never from a SQL join, and a reader that resolved them
/// differently would compare two different things.
/// </summary>
internal sealed class FrozenSqliteStatsReader : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly Dictionary<TenantId, Dictionary<string, long>> _agentIds = new();
    private readonly Dictionary<long, string> _repoDisplay = new();
    private readonly Dictionary<long, string> _modelDisplay = new();
    private readonly Dictionary<long, string> _checkoutDisplay = new();
    private readonly Dictionary<TenantId, string> _agentsSinceUtc = new();
    private string _modelsSinceUtc = "";

    public FrozenSqliteStatsReader(string path)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        LoadMirror();
    }

    public void Dispose() => _connection.Dispose();

    private void LoadMirror()
    {
        Read("SELECT tenant, repo_id, repo_display FROM repo_identity",
            r => _repoDisplay[r.GetInt64(1)] = r.GetString(2));
        Read("SELECT tenant, agent_id, agent_display FROM agent_identity", r =>
        {
            var t = new TenantId(r.GetString(0));
            if (!_agentIds.TryGetValue(t, out var inner))
                _agentIds[t] = inner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            inner[r.GetString(2)] = r.GetInt64(1);
        });
        Read("SELECT tenant, model_id, model_display FROM model_identity",
            r => _modelDisplay[r.GetInt64(1)] = r.GetString(2));
        Read("SELECT tenant, checkout_id, checkout_display FROM checkout_identity",
            r => _checkoutDisplay[r.GetInt64(1)] = r.GetString(2));
        Read("SELECT tenant, value FROM meta WHERE name=$n",
            r => _agentsSinceUtc[new TenantId(r.GetString(0))] = r.GetString(1), ("$n", "agents_since_utc"));
        _modelsSinceUtc = ReadScalarString("SELECT value FROM meta WHERE name=$n LIMIT 1",
            ("$n", GatewayStatsDatabase.ModelsSinceKey)) ?? "";
    }

    // ---- The twelve projections, verbatim -----------------------------------------------------------

    public (long Turns, long Characters) AgentDrivenUsage(TenantId t)
    {
        long turns = 0, chars = 0;
        Read("SELECT COALESCE(SUM(turns),0), COALESCE(SUM(chars),0) FROM agent_driven_delta WHERE tenant=$tn",
            r => { turns = r.GetInt64(0); chars = r.GetInt64(1); }, ("$tn", t.Value));
        return (turns, chars);
    }

    public InputStatsDto CurrentTotals(TenantId t)
    {
        var rows = new List<InputStatBucketDto>();
        Read(@"SELECT modality, surface, SUM(turns), SUM(chars) FROM stat_delta WHERE tenant=$tn GROUP BY modality, surface", r =>
            rows.Add(new InputStatBucketDto
            {
                Modality = r.GetString(0),
                Surface = r.GetString(1),
                Turns = r.GetInt64(2),
                Characters = r.GetInt64(3),
            }), ("$tn", t.Value));

        var dto = new InputStatsDto();
        foreach (var b in rows.OrderBy(b => b.Modality, StringComparer.Ordinal)
                              .ThenBy(b => b.Surface, StringComparer.Ordinal))
            dto.Buckets.Add(b);
        return dto;
    }

    public WingmanUsageDto WingmanUsage(TenantId t)
    {
        var turns = ExecuteScalarLong("SELECT COALESCE(SUM(turns),0) FROM stat_delta WHERE tenant=$tn AND wingman=1", ("$tn", t.Value));
        var sessions = ExecuteScalarLong("SELECT COUNT(*) FROM wingman_session WHERE tenant=$tn", ("$tn", t.Value));
        return new WingmanUsageDto { Turns = turns, Sessions = (int)sessions };
    }

    public IReadOnlyList<InputHourDto> HourlyTurns(TenantId t)
    {
        var list = new List<InputHourDto>();
        Read(@"SELECT hour_utc,
                      COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                      COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                      SUM(chars)
                 FROM stat_delta
                WHERE tenant = $tn AND hour_utc <> $marker
                GROUP BY hour_utc", r =>
        {
            var voice = r.GetInt64(1);
            var typed = r.GetInt64(2);
            list.Add(new InputHourDto
            {
                Hour = r.GetString(0),
                VoiceTurns = voice,
                TypedTurns = typed,
                Turns = voice + typed,
                Characters = r.GetInt64(3),
            });
        }, ("$tn", t.Value), ("$marker", GatewayStatsDatabase.ArchiveMarker));
        list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
        return list;
    }

    public IReadOnlyList<RepoStatBucketDto> RepoTotals(TenantId t)
    {
        var sessions = SessionCounts("repo_session", "repo_id");

        var checkoutsByRepo = new Dictionary<long, List<string>>();
        Read("SELECT DISTINCT repo_id, checkout_id FROM stat_delta WHERE tenant=$tn AND checkout_id IS NOT NULL", r =>
        {
            var repoId = r.GetInt64(0);
            var checkoutId = r.GetInt64(1);
            if (!_checkoutDisplay.TryGetValue(checkoutId, out var path)) return;
            if (!checkoutsByRepo.TryGetValue(repoId, out var paths))
                checkoutsByRepo[repoId] = paths = new List<string>();
            paths.Add(path);
        }, ("$tn", t.Value));
        foreach (var paths in checkoutsByRepo.Values)
            paths.Sort(StringComparer.OrdinalIgnoreCase);

        var list = new List<RepoStatBucketDto>();
        Read(@"SELECT repo_id,
                      COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                      COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                      SUM(chars)
                 FROM stat_delta WHERE tenant=$tn GROUP BY repo_id", r =>
        {
            var id = r.GetInt64(0);
            var voice = r.GetInt64(1);
            var typed = r.GetInt64(2);
            var display = _repoDisplay.TryGetValue(id, out var d) ? d : "";
            list.Add(new RepoStatBucketDto
            {
                Repo = display,
                RepoName = RepoLeaf(display),
                Turns = voice + typed,
                VoiceTurns = voice,
                TypedTurns = typed,
                Characters = r.GetInt64(3),
                Sessions = sessions.TryGetValue(id, out var n) ? n : 0,
                Checkouts = checkoutsByRepo.TryGetValue(id, out var cks) ? cks : new List<string>(),
            });
        }, ("$tn", t.Value));
        list.Sort((a, b) =>
        {
            var byTurns = b.Turns.CompareTo(a.Turns);
            if (byTurns != 0) return byTurns;
            var byChars = b.Characters.CompareTo(a.Characters);
            return byChars != 0 ? byChars : string.CompareOrdinal(a.RepoName, b.RepoName);
        });
        return list;
    }

    public IReadOnlyList<AgentStatBucketDto> AgentTotals(TenantId t)
    {
        var sessions = SessionCounts("agent_session", "agent_id");

        var human = new Dictionary<long, (long Voice, long Typed, long Chars)>();
        Read(@"SELECT agent_id,
                      COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                      COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                      SUM(chars)
                 FROM agent_delta WHERE tenant=$tn GROUP BY agent_id",
            r => human[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), ("$tn", t.Value));

        var driven = new Dictionary<long, (long Turns, long Chars)>();
        Read("SELECT agent_id, SUM(turns), SUM(chars) FROM agent_driven_delta WHERE tenant=$tn GROUP BY agent_id",
            r => driven[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt64(2)), ("$tn", t.Value));

        var list = new List<AgentStatBucketDto>();
        if (_agentIds.TryGetValue(t, out var ids))
            foreach (var (display, id) in ids)
            {
                human.TryGetValue(id, out var h);
                driven.TryGetValue(id, out var d);
                list.Add(new AgentStatBucketDto
                {
                    Agent = display,
                    AgentName = AgentDisplayName(display),
                    Turns = h.Voice + h.Typed,
                    VoiceTurns = h.Voice,
                    TypedTurns = h.Typed,
                    Characters = h.Chars,
                    AgentDrivenTurns = d.Turns,
                    AgentDrivenCharacters = d.Chars,
                    Sessions = sessions.TryGetValue(id, out var n) ? n : 0,
                });
            }
        list.Sort((a, b) =>
        {
            var byTurns = b.Turns.CompareTo(a.Turns);
            if (byTurns != 0) return byTurns;
            var byChars = b.Characters.CompareTo(a.Characters);
            return byChars != 0 ? byChars : string.CompareOrdinal(a.AgentName, b.AgentName);
        });
        return list;
    }

    // The unfiltered accessor as it stood: every tenant's rows, keyed by surrogate id. Kept exactly so the
    // parity comparison is against what shipped - the ported reader's tenant-scoped join must produce the
    // SAME numbers at each call site, which is the point the Architect's change turns on: the fix removes a
    // reachability hazard, it does not change any served figure.
    private Dictionary<long, int> SessionCounts(string table, string column)
    {
        var counts = new Dictionary<long, int>();
        Read($"SELECT {column}, COUNT(*) FROM {table} GROUP BY {column}", r => counts[r.GetInt64(0)] = r.GetInt32(1));
        return counts;
    }

    public string AgentsSinceUtc(TenantId t) => _agentsSinceUtc.TryGetValue(t, out var v) ? v : "";

    public string ModelsSinceUtc => _modelsSinceUtc;

    public IReadOnlyList<ModelStatBucketDto> ModelTotals(TenantId t)
    {
        var list = new List<ModelStatBucketDto>();
        Read(@"SELECT model_id,
                      COALESCE(SUM(CASE WHEN is_voice = 1 THEN turns ELSE 0 END), 0),
                      COALESCE(SUM(CASE WHEN is_voice = 0 THEN turns ELSE 0 END), 0),
                      SUM(chars)
                 FROM stat_delta WHERE tenant=$tn GROUP BY model_id", r =>
        {
            var voice = r.GetInt64(1);
            var typed = r.GetInt64(2);
            string? display = r.IsDBNull(0)
                ? null
                : (_modelDisplay.TryGetValue(r.GetInt64(0), out var d) ? d : "");
            list.Add(new ModelStatBucketDto
            {
                Model = display,
                Turns = voice + typed,
                VoiceTurns = voice,
                TypedTurns = typed,
                Characters = r.GetInt64(3),
            });
        }, ("$tn", t.Value));
        list.Sort((a, b) =>
        {
            var byTurns = b.Turns.CompareTo(a.Turns);
            if (byTurns != 0) return byTurns;
            var byChars = b.Characters.CompareTo(a.Characters);
            return byChars != 0 ? byChars : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
        });
        return list;
    }

    public TokenSpendDto TokenSpend(TenantId t)
    {
        var dto = new TokenSpendDto();
        Read(@"SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                      COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                 FROM token_delta WHERE tenant=$tn", r =>
        {
            dto.InputTokens = r.GetInt64(0);
            dto.OutputTokens = r.GetInt64(1);
            dto.CacheReadTokens = r.GetInt64(2);
            dto.CacheCreationTokens = r.GetInt64(3);
        }, ("$tn", t.Value));
        return dto;
    }

    public IReadOnlyList<TokenHourDto> TokenSpendByHour(TenantId t)
    {
        var list = new List<TokenHourDto>();
        Read(@"SELECT hour_utc,
                      COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                      COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                 FROM token_delta
                WHERE tenant=$tn AND hour_utc <> $marker
                GROUP BY hour_utc", r => list.Add(new TokenHourDto
        {
            Hour = r.GetString(0),
            InputTokens = r.GetInt64(1),
            OutputTokens = r.GetInt64(2),
            CacheReadTokens = r.GetInt64(3),
            CacheCreationTokens = r.GetInt64(4),
        }), ("$tn", t.Value), ("$marker", GatewayStatsDatabase.ArchiveMarker));
        list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
        return list;
    }

    public IReadOnlyList<ModelSpendDto> TokenSpendByModel(TenantId t)
    {
        var list = new List<ModelSpendDto>();
        Read(@"SELECT model_id,
                      COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                      COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0)
                 FROM token_delta WHERE tenant=$tn GROUP BY model_id", r =>
        {
            string? display = r.IsDBNull(0)
                ? null
                : (_modelDisplay.TryGetValue(r.GetInt64(0), out var d) ? d : "");
            list.Add(new ModelSpendDto
            {
                Model = display,
                InputTokens = r.GetInt64(1),
                OutputTokens = r.GetInt64(2),
                CacheReadTokens = r.GetInt64(3),
                CacheCreationTokens = r.GetInt64(4),
            });
        }, ("$tn", t.Value));
        list.Sort((a, b) =>
        {
            var byTotal = b.TotalTokens.CompareTo(a.TotalTokens);
            return byTotal != 0 ? byTotal : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
        });
        return list;
    }

    // ---- Helpers, copied with the projections they serve --------------------------------------------

    private static string AgentDisplayName(string agent) => agent switch
    {
        "" => "(unknown)",
        "ClaudeCode" => "Claude Code",
        "RawCli" => "Raw CLI",
        _ => agent,
    };

    private static string RepoLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private void Read(string sql, Action<SqliteDataReader> onRow, params (string Name, object Value)[] args)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) onRow(reader);
    }

    private long ExecuteScalarLong(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private string? ReadScalarString(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar() as string;
    }
}
