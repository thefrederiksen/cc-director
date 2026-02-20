using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeResponseParserTests
{
    // Real JSON captured from CLI Explorer test run (OF-02 scenario)
    private const string SampleJsonResponse = """
        {
            "type": "result",
            "subtype": "success",
            "is_error": false,
            "duration_ms": 1887,
            "duration_api_ms": 1480,
            "num_turns": 1,
            "result": "pong",
            "stop_reason": null,
            "session_id": "11a83681-8718-4d19-98df-e5ccac9b2c67",
            "total_cost_usd": 0.0024135,
            "usage": {
                "input_tokens": 10,
                "output_tokens": 46,
                "cache_read_input_tokens": 21735,
                "cache_creation_input_tokens": 0
            },
            "permission_denials": [],
            "uuid": "f56777f8-7949-4a62-8b06-3e74723f7bf9"
        }
        """;

    private const string ErrorMaxTurnsResponse = """
        {
            "type": "result",
            "subtype": "error_max_turns",
            "is_error": false,
            "duration_ms": 4632,
            "duration_api_ms": 4247,
            "num_turns": 2,
            "result": "{\"answer\": \"4\"}",
            "session_id": "dfd83ed5-9af2-4ecf-8c63-55312673f21c",
            "total_cost_usd": 0.00615,
            "usage": {
                "input_tokens": 20,
                "output_tokens": 297,
                "cache_read_input_tokens": 43650,
                "cache_creation_input_tokens": 224
            },
            "errors": []
        }
        """;

    [Fact]
    public void ParseJsonResponse_Success_ExtractsAllFields()
    {
        var response = ClaudeResponseParser.ParseJsonResponse(SampleJsonResponse, 0);

        Assert.Equal("pong", response.Result);
        Assert.Equal("11a83681-8718-4d19-98df-e5ccac9b2c67", response.SessionId);
        Assert.Equal("success", response.Subtype);
        Assert.False(response.IsError);
        Assert.Equal(0.0024135m, response.TotalCostUsd);
        Assert.Equal(1, response.NumTurns);
        Assert.Equal(1887, response.DurationMs);
        Assert.Equal(1480, response.DurationApiMs);
        Assert.Equal(0, response.ExitCode);
    }

    [Fact]
    public void ParseJsonResponse_Success_ExtractsUsage()
    {
        var response = ClaudeResponseParser.ParseJsonResponse(SampleJsonResponse, 0);

        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(46, response.Usage.OutputTokens);
        Assert.Equal(21735, response.Usage.CacheReadInputTokens);
        Assert.Equal(0, response.Usage.CacheCreationInputTokens);
    }

    [Fact]
    public void ParseJsonResponse_ErrorMaxTurns_ParsesCorrectly()
    {
        var response = ClaudeResponseParser.ParseJsonResponse(ErrorMaxTurnsResponse, 0);

        Assert.Equal("error_max_turns", response.Subtype);
        Assert.Equal(2, response.NumTurns);
        Assert.Equal(0.00615m, response.TotalCostUsd);
        Assert.Contains("answer", response.Result);
    }

    [Fact]
    public void ParseJsonResponse_PreservesExitCode()
    {
        var response = ClaudeResponseParser.ParseJsonResponse(SampleJsonResponse, 1);

        Assert.Equal(1, response.ExitCode);
    }

    [Fact]
    public void ParseStreamLine_InitMessage_ExtractsSessionId()
    {
        var line = """{"type":"system","subtype":"init","session_id":"3cfa8fd7-3050-4dae-bb32-ef2bffccec05","tools":["Bash","Read"]}""";

        var evt = ClaudeResponseParser.ParseStreamLine(line);

        Assert.NotNull(evt);
        Assert.Equal("system", evt.Type);
        Assert.Equal("init", evt.Subtype);
        Assert.Equal("3cfa8fd7-3050-4dae-bb32-ef2bffccec05", evt.SessionId);
    }

    [Fact]
    public void ParseStreamLine_HookStarted_ExtractsSessionId()
    {
        var line = """{"type":"system","subtype":"hook_started","session_id":"abc-123","hook_name":"SessionStart:startup"}""";

        var evt = ClaudeResponseParser.ParseStreamLine(line);

        Assert.NotNull(evt);
        Assert.Equal("system", evt.Type);
        Assert.Equal("hook_started", evt.Subtype);
        Assert.Equal("abc-123", evt.SessionId);
    }

    [Fact]
    public void ParseStreamLine_AssistantMessage_ExtractsText()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}""";

        var evt = ClaudeResponseParser.ParseStreamLine(line);

        Assert.NotNull(evt);
        Assert.Equal("assistant", evt.Type);
        Assert.Equal("pong", evt.Text);
    }

    [Fact]
    public void ParseStreamLine_ResultMessage_ExtractsText()
    {
        var line = """{"type":"result","subtype":"success","result":"pong","session_id":"abc-123"}""";

        var evt = ClaudeResponseParser.ParseStreamLine(line);

        Assert.NotNull(evt);
        Assert.Equal("result", evt.Type);
        Assert.Equal("success", evt.Subtype);
        Assert.Equal("pong", evt.Text);
        Assert.Equal("abc-123", evt.SessionId);
    }

    [Fact]
    public void ParseStreamLine_NonJsonLine_ReturnsNull()
    {
        var evt = ClaudeResponseParser.ParseStreamLine("some debug output");

        Assert.Null(evt);
    }

    [Fact]
    public void ParseStreamLine_EmptyLine_ReturnsNull()
    {
        var evt = ClaudeResponseParser.ParseStreamLine("");

        Assert.Null(evt);
    }

    [Fact]
    public void ParseStreamLine_InvalidJson_ReturnsNull()
    {
        var evt = ClaudeResponseParser.ParseStreamLine("{invalid json}");

        Assert.Null(evt);
    }

    [Fact]
    public void ParseStreamLine_PreservesRawJson()
    {
        var line = """{"type":"system","subtype":"init","session_id":"abc"}""";

        var evt = ClaudeResponseParser.ParseStreamLine(line);

        Assert.NotNull(evt);
        Assert.Equal(line, evt.RawJson);
    }

    [Fact]
    public void DeserializeResult_SimpleObject_Deserializes()
    {
        var json = """{"answer":"4","confidence":0.95}""";

        var result = ClaudeResponseParser.DeserializeResult<TestResult>(json);

        Assert.Equal("4", result.Answer);
        Assert.Equal(0.95, result.Confidence, 2);
    }

    [Fact]
    public void DeserializeResult_NullResult_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ClaudeResponseParser.DeserializeResult<TestResult>("null"));
    }

    private sealed class TestResult
    {
        public string Answer { get; set; } = "";
        public double Confidence { get; set; }
    }
}
